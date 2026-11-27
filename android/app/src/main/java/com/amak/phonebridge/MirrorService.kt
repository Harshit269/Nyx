package com.amak.phonebridge

import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.Bitmap
import android.graphics.PixelFormat
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.ImageReader
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.os.IBinder
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.atomic.AtomicReference
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * Shows the phone's screen on the laptop over Wi-Fi, with Android's own screen sharing: one
 * "Start?" question each time (always the whole screen, no app picker), popped up right away
 * when Android allows it, else from a notification. Frames are JPEG, sealed with AES-256-GCM under a
 * fresh key the laptop sent inside Nyx's link, at most 30 a second and only when something
 * changed. Clicks from the laptop come back through [ControlService].
 */
class MirrorService : Service() {

    companion object {
        private const val CHANNEL = "mirror"
        private const val NOTIF_ID = 4716
        private const val OFFER_ID = 4717
        private const val LONG_SIDE = 2160 // sharp text, close to the phone's own; still fine for Wi-Fi
        const val ACTION_STOP = "com.amak.phonebridge.MIRROR_STOP"

        @Volatile var pendingKey: ByteArray? = null
        /** Set when the laptop wants the sound too ("With sound"): its own key, same sharing session. */
        @Volatile var pendingAudioKey: ByteArray? = null
        /** The laptop can show video (H.264); otherwise a JPEG per frame, as before. */
        @Volatile var pendingVideo = false
        /** What the laptop asked for: the long side of the picture and pictures a second. */
        @Volatile var pendingSize = 1600
        @Volatile var pendingFps = 60
        @Volatile var instance: MirrorService? = null
            private set
        val running get() = instance != null

        /** The phone's own "Show screen on laptop" is waiting for the laptop's key. */
        @Volatile var waiting = false

        fun offer(c: Context, key: ByteArray) {
            pendingKey = key
            if (instance != null || waiting) return // already asked on the phone
            val app = c.applicationContext
            val nm = app.getSystemService(NotificationManager::class.java)
            nm.createNotificationChannel(NotificationChannel(CHANNEL, "Screen mirroring", NotificationManager.IMPORTANCE_HIGH))
            val start = Intent(app, MirrorStartActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
            val pi = PendingIntent.getActivity(app, 46, start, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
            val n = Notification.Builder(app, CHANNEL)
                .setSmallIcon(R.drawable.ic_stat_nyx)
                .setContentTitle("Mirror this screen to the laptop?")
                .setContentText("Tap, then Start.")
                .setContentIntent(pi).setAutoCancel(true).setTimeoutAfter(60_000).build()
            try { nm.notify(OFFER_ID, n) } catch (e: Exception) {}
            popUp(app, start)
        }

        /**
         * Puts the question straight on screen when Android lets Nyx (it does while Nyx's
         * "Allow laptop control" is on, or Nyx is open); otherwise the notification stays.
         */
        fun popUp(app: Context, start: Intent) {
            try { app.startActivity(start) } catch (e: Exception) {}
        }

        /** Android 14+: whole screen only, so the question is just "Start?" with no app picker. */
        fun captureIntent(c: Context): Intent {
            val mpm = c.getSystemService(MediaProjectionManager::class.java)
            return if (Build.VERSION.SDK_INT >= 34)
                mpm.createScreenCaptureIntent(android.media.projection.MediaProjectionConfig.createConfigForDefaultDisplay())
            else mpm.createScreenCaptureIntent()
        }

        fun stopFromLaptop() { pendingKey = null; instance?.finish(tell = false) }

        fun cancelOffer(c: Context) {
            try { c.getSystemService(NotificationManager::class.java).cancel(OFFER_ID) } catch (e: Exception) {}
        }
    }

    private var projection: MediaProjection? = null
    private var display: VirtualDisplay? = null
    private var reader: ImageReader? = null
    private var thread: HandlerThread? = null
    @Volatile private var streaming = false
    @Volatile private var server: ServerSocket? = null
    @Volatile private var socket: Socket? = null
    private val latest = AtomicReference<ByteArray?>(null)
    private val lock = Object()
    private var lastFrame = 0L
    private var bmp: Bitmap? = null      // reused frame to frame
    private var dpi = 0
    private var shown = 0 to 0           // the size being sent, to follow the phone turning
    private var turns: DisplayManager.DisplayListener? = null
    // Android ends screen sharing when the phone locks, so the screen stays on (dimmed) while
    // mirroring, even if you only watch from the laptop
    private var awake: android.os.PowerManager.WakeLock? = null
    // video mode: the hardware encoder draws straight from the screen (no JPEG per frame)
    private var enc: ScreenEncoder? = null
    private var videoSide = 1600
    private var videoFps = 60
    // Wi-Fi without its power-saving naps while mirroring (they make frames arrive in clumps)
    private var wifiLock: android.net.wifi.WifiManager.WifiLock? = null
    @Volatile private var video = false

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) { finish(tell = true); return START_NOT_STICKY }
        val stop = PendingIntent.getService(this, 47, Intent(this, MirrorService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Screen mirroring", NotificationManager.IMPORTANCE_HIGH))
        val n = Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_nyx)
            .setContentTitle("Your screen is on the laptop")
            .setContentText("Nyx is showing this screen on your laptop. Tap Stop to end it.")
            .setOngoing(true)
            .addAction(Notification.Action.Builder(null, "Stop", stop).build())
            .build()
        try {
            if (Build.VERSION.SDK_INT >= 29) startForeground(NOTIF_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
            else startForeground(NOTIF_ID, n)
        } catch (e: Exception) { stopSelf(); return START_NOT_STICKY }

        val code = intent?.getIntExtra("code", 0) ?: 0
        val data: Intent? = if (Build.VERSION.SDK_INT >= 33) intent?.getParcelableExtra("data", Intent::class.java)
                            else @Suppress("DEPRECATION") intent?.getParcelableExtra("data")
        val key = pendingKey
        if (code != Activity.RESULT_OK || data == null || key == null) { finish(tell = false); return START_NOT_STICKY }
        pendingKey = null
        instance = this

        val mpm = getSystemService(MediaProjectionManager::class.java)
        val mp = mpm.getMediaProjection(code, data) ?: run { finish(tell = false); return START_NOT_STICKY }
        projection = mp
        mp.registerCallback(object : MediaProjection.Callback() {
            override fun onStop() { finish(tell = true) } // stopped from the status bar, or the phone locked
        }, null)
        try {
            @Suppress("DEPRECATION")
            awake = getSystemService(android.os.PowerManager::class.java)
                .newWakeLock(android.os.PowerManager.SCREEN_DIM_WAKE_LOCK, "nyx:mirror").also { it.acquire(4 * 60 * 60 * 1000L) }
        } catch (e: Exception) {}
        ControlSwitch.set(this, true) // laptop control only for as long as this mirror runs
        val wantVideo = pendingVideo
        pendingVideo = false
        videoSide = pendingSize.coerceIn(720, LONG_SIDE)
        videoFps = pendingFps.coerceIn(30, 120)
        try {
            @Suppress("DEPRECATION")
            val mode = if (Build.VERSION.SDK_INT >= 29) android.net.wifi.WifiManager.WIFI_MODE_FULL_LOW_LATENCY
                       else android.net.wifi.WifiManager.WIFI_MODE_FULL_HIGH_PERF
            wifiLock = applicationContext.getSystemService(android.net.wifi.WifiManager::class.java)
                .createWifiLock(mode, "nyx:mirror").also { it.setReferenceCounted(false); it.acquire() }
        } catch (e: Exception) {}
        startCapture(mp, wantVideo)
        streaming = true
        Thread { stream(key) }.start()
        // "With sound": the same sharing session also carries what the phone's apps play
        val audioKey = pendingAudioKey
        pendingAudioKey = null
        if (audioKey != null && Build.VERSION.SDK_INT >= 29 &&
            checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) == android.content.pm.PackageManager.PERMISSION_GRANTED)
            sound = AudioCapture(this, audioKey, mp) { LinkService.instance?.send(JSONObject().put("t", "audio_stop")) }.also { it.start() }
        return START_NOT_STICKY
    }

    /** The phone's real screen size now (it swaps when the phone turns). */
    private fun real(): Pair<Int, Int> {
        @Suppress("DEPRECATION")
        val m = android.util.DisplayMetrics().also { (getSystemService(Context.WINDOW_SERVICE) as android.view.WindowManager).defaultDisplay.getRealMetrics(it) }
        dpi = m.densityDpi
        return m.widthPixels to m.heightPixels
    }

    /** The phone's screen size now, scaled to at most [LONG_SIDE]. */
    private fun target(): Pair<Int, Int> {
        val (rw, rh) = real()
        val scale = Math.min(1f, LONG_SIDE.toFloat() / Math.max(rw, rh))
        return ((rw * scale).toInt() and 1.inv()) to ((rh * scale).toInt() and 1.inv())
    }

    private fun startCapture(mp: MediaProjection, wantVideo: Boolean) {
        val t = HandlerThread("nyx-mirror").also { it.start() }
        thread = t
        val handler = Handler(t.looper)
        val made = if (wantVideo) real().let { (rw, rh) -> ScreenEncoder.make(rw, rh, videoSide, videoFps) } else null
        if (made != null) {
            video = true
            enc = ScreenEncoder().also { it.swap(made.codec) }
            shown = real()
            display = mp.createVirtualDisplay("Nyx mirror", made.w, made.h, dpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, made.surface, null, handler)
        } else {
            val (w, h) = target()
            shown = w to h
            val r = newReader(w, h, handler)
            reader = r
            display = mp.createVirtualDisplay("Nyx mirror", w, h, dpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, r.surface, null, handler)
        }
        // the phone turned: send landscape frames, not a small landscape picture inside a portrait one
        val l = object : DisplayManager.DisplayListener {
            override fun onDisplayAdded(id: Int) {}
            override fun onDisplayRemoved(id: Int) {}
            override fun onDisplayChanged(id: Int) {
                if (id != android.view.Display.DEFAULT_DISPLAY || !streaming) return
                try {
                    if (video) {
                        val size = real()
                        if (size == shown) return
                        shown = size
                        val next = ScreenEncoder.make(size.first, size.second, videoSide, videoFps) ?: return
                        display?.resize(next.w, next.h, dpi)
                        display?.surface = next.surface
                        enc?.swap(next.codec)
                        return
                    }
                    val size = target()
                    if (size == shown) return
                    shown = size
                    val old = reader
                    val nr = newReader(size.first, size.second, handler)
                    display?.resize(size.first, size.second, dpi)
                    display?.surface = nr.surface
                    reader = nr
                    old?.close()
                } catch (e: Exception) {}
            }
        }
        turns = l
        getSystemService(DisplayManager::class.java).registerDisplayListener(l, handler)
    }

    private fun newReader(w: Int, h: Int, handler: Handler): ImageReader {
        val r = ImageReader.newInstance(w, h, PixelFormat.RGBA_8888, 2)
        r.setOnImageAvailableListener({ rd ->
            val img = try { rd.acquireLatestImage() } catch (e: Exception) { null } ?: return@setOnImageAvailableListener
            try {
                val now = System.currentTimeMillis()
                if (socket != null && now - lastFrame >= 33) { // at most ~30 a second
                    lastFrame = now
                    val plane = img.planes[0]
                    val rowPx = plane.rowStride / plane.pixelStride
                    var b = bmp
                    if (b == null || b.width != rowPx || b.height != img.height) {
                        b?.recycle()
                        b = Bitmap.createBitmap(rowPx, img.height, Bitmap.Config.ARGB_8888)
                        bmp = b
                    }
                    b!!.copyPixelsFromBuffer(plane.buffer)
                    val crop = if (rowPx != img.width) Bitmap.createBitmap(b, 0, 0, img.width, img.height) else b
                    val out = ByteArrayOutputStream(img.width * img.height / 5)
                    crop.compress(Bitmap.CompressFormat.JPEG, 82, out)
                    if (crop !== b) crop.recycle()
                    latest.set(out.toByteArray())
                    synchronized(lock) { lock.notify() }
                }
            } catch (e: Exception) {
            } finally { img.close() }
        }, handler)
        return r
    }

    private fun stream(key: ByteArray) {
        try {
            val ss = ServerSocket(0)
            server = ss
            ss.soTimeout = 15_000
            LinkService.instance?.send(JSONObject().put("t", "mirror_ready").put("port", ss.localPort)
                .put("control", ControlService.instance != null || ControlSwitch.canToggle(this)).put("codec", if (video) "h264" else "jpeg"))
            val s = ss.accept()
            try { ss.close() } catch (e: Exception) {}
            socket = s
            s.tcpNoDelay = true
            val out = DataOutputStream(s.getOutputStream().buffered(1 shl 16))
            val spec = SecretKeySpec(key, "AES")
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            var counter = 0L
            fun seal(frame: ByteArray) {
                counter++
                val nonce = ByteArray(12)
                for (i in 0 until 8) nonce[4 + i] = (counter ushr (56 - 8 * i)).toByte()
                cipher.init(Cipher.ENCRYPT_MODE, spec, GCMParameterSpec(128, nonce))
                val sealed = cipher.doFinal(frame)
                out.writeInt(12 + sealed.size)
                out.write(nonce)
                out.write(sealed)
                out.flush()
            }
            val e = enc
            if (video && e != null) {
                // each piece: [1 = keyframe][8-byte time, microseconds][H.264, Annex B]
                e.drain({ streaming }) { key, pts, data ->
                    val piece = ByteArray(9 + data.size)
                    piece[0] = if (key) 1 else 0
                    for (i in 0 until 8) piece[1 + i] = (pts ushr (56 - 8 * i)).toByte()
                    System.arraycopy(data, 0, piece, 9, data.size)
                    seal(piece)
                }
                return
            }
            while (streaming) {
                var frame = latest.getAndSet(null)
                if (frame == null) {
                    synchronized(lock) { lock.wait(1000) }
                    frame = latest.getAndSet(null) ?: continue
                }
                seal(frame)
            }
        } catch (e: Exception) {
        } finally {
            enc?.releaseAll() // this thread is the only one that touches the encoders
            finish(tell = streaming)
        }
    }

    private var sound: AudioCapture? = null

    fun finish(tell: Boolean) {
        val was = streaming
        streaming = false
        synchronized(lock) { lock.notifyAll() }
        try { sound?.stop() } catch (e: Exception) {}
        sound = null
        try { turns?.let { getSystemService(DisplayManager::class.java).unregisterDisplayListener(it) } } catch (e: Exception) {}
        try { awake?.let { if (it.isHeld) it.release() } } catch (e: Exception) {}
        try { wifiLock?.let { if (it.isHeld) it.release() } } catch (e: Exception) {}
        wifiLock = null
        awake = null
        bmp = null
        turns = null
        try { server?.close() } catch (e: Exception) {}
        try { socket?.close() } catch (e: Exception) {}
        try { display?.release() } catch (e: Exception) {}
        // close the frame reader on its own thread, after any frame it's copying right now:
        // closing it from here mid-copy unmapped the frame and crashed the app (seen on 1.3)
        val r = reader; val t = thread
        if (t != null && r != null && Handler(t.looper).post { try { r.close() } catch (e: Exception) {} }) Unit
        else try { r?.close() } catch (e: Exception) {}
        try { projection?.stop() } catch (e: Exception) {}
        display = null; reader = null; projection = null
        t?.quitSafely(); thread = null
        ControlSwitch.set(this, false) // and off again, so payment apps work
        if (tell && was) {
            val locked = try { getSystemService(android.app.KeyguardManager::class.java).isKeyguardLocked } catch (e: Exception) { false }
            LinkService.instance?.send(JSONObject().put("t", "mirror_stop").put("why", if (locked) "locked" else ""))
        }
        instance = null
        try { stopForeground(STOP_FOREGROUND_REMOVE) } catch (e: Exception) {}
        stopSelf()
    }

    override fun onDestroy() {
        if (streaming) finish(tell = true)
        instance = null
        super.onDestroy()
    }
}

/**
 * Asks Android's "Start sharing your screen?" question (it must come from something on screen),
 * then hands the answer to [MirrorService]. See-through, gone right after.
 */
class MirrorStartActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        MirrorService.cancelOffer(this)
        if (MirrorService.pendingKey != null) { ask(); return }
        // started from the phone: get a key from the laptop first (a moment), then ask
        if (LinkService.instance == null || !LinkService.onNetwork) {
            android.widget.Toast.makeText(this, "Link the laptop over Wi-Fi first.", android.widget.Toast.LENGTH_LONG).show()
            finish(); return
        }
        MirrorService.waiting = true
        LinkService.instance?.send(JSONObject().put("t", "mirror_request"))
        val h = Handler(mainLooper)
        val started = System.currentTimeMillis()
        h.post(object : Runnable {
            override fun run() {
                when {
                    MirrorService.pendingKey != null -> { MirrorService.waiting = false; ask() }
                    System.currentTimeMillis() - started > 6000 -> {
                        MirrorService.waiting = false
                        android.widget.Toast.makeText(this@MirrorStartActivity, "The laptop didn't answer. Open Nyx on the laptop.", android.widget.Toast.LENGTH_LONG).show()
                        finish()
                    }
                    else -> h.postDelayed(this, 100)
                }
            }
        })
    }

    private var askedMic = false

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        ask() // without it, the screen still comes through, just without sound
    }

    private fun ask() {
        // with sound: Android needs the microphone permission to pass app sound along (asked once)
        if (!askedMic && MirrorService.pendingAudioKey != null && Build.VERSION.SDK_INT >= 29 &&
            checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) != android.content.pm.PackageManager.PERMISSION_GRANTED) {
            askedMic = true
            requestPermissions(arrayOf(android.Manifest.permission.RECORD_AUDIO), 8)
            return
        }
        @Suppress("DEPRECATION")
        startActivityForResult(MirrorService.captureIntent(this), 1)
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        @Suppress("DEPRECATION")
        super.onActivityResult(requestCode, resultCode, data)
        if (resultCode == RESULT_OK && data != null)
            startForegroundService(Intent(this, MirrorService::class.java).putExtra("code", resultCode).putExtra("data", data))
        else {
            MirrorService.pendingKey = null
            LinkService.instance?.send(JSONObject().put("t", "mirror_stop"))
        }
        finish()
    }
}
