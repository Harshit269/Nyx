package com.amak.phonebridge

import android.Manifest
import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.PictureInPictureParams
import android.content.Context
import android.content.Intent
import android.content.pm.ActivityInfo
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.graphics.Color
import android.graphics.ImageFormat
import android.graphics.Rect
import android.graphics.SurfaceTexture
import android.graphics.YuvImage
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.media.Image
import android.media.ImageReader
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.os.Looper
import android.util.Rational
import android.util.Size
import android.view.Gravity
import android.view.OrientationEventListener
import android.view.Surface
import android.view.TextureView
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import java.net.ServerSocket
import java.net.Socket
import java.nio.ByteBuffer
import java.util.concurrent.atomic.AtomicReference
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * The phone as a webcam for the laptop. Films with the front or back camera, compresses each
 * frame to JPEG and streams it over Wi-Fi, sealed with AES-256-GCM under a fresh key the laptop
 * sent inside Nyx's encrypted link. The laptop shows it in OBS Virtual Camera, which Meet,
 * Zoom and Teams can pick. Leave the screen and it shrinks to picture-in-picture and keeps
 * filming while you use the phone.
 */
class CameraActivity : Activity() {

    companion object {
        private const val CHANNEL = "webcam"
        private const val NOTIF_ID = 4713
        private const val PERM = 51
        private const val W = 1280
        private const val H = 720

        @Volatile private var pendingKey: ByteArray? = null
        @Volatile private var current: CameraActivity? = null

        fun offer(c: Context, key: ByteArray) {
            pendingKey = key
            val open = current
            if (open != null) { open.runOnUiThread { open.begin() }; return }
            val app = c.applicationContext
            val nm = app.getSystemService(NotificationManager::class.java)
            nm.createNotificationChannel(NotificationChannel(CHANNEL, "Webcam", NotificationManager.IMPORTANCE_HIGH))
            val pi = PendingIntent.getActivity(app, 43, Intent(app, CameraActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
            val n = Notification.Builder(app, CHANNEL)
                .setSmallIcon(R.drawable.ic_stat_nyx)
                .setContentTitle("Use as webcam")
                .setContentText("Your laptop wants this phone's camera. Tap to start.")
                .setContentIntent(pi).setAutoCancel(true).setTimeoutAfter(60_000).build()
            try { nm.notify(NOTIF_ID, n) } catch (e: Exception) {}
        }

        fun stopFromLaptop(why: String?) {
            pendingKey = null
            val open = current ?: return
            open.runOnUiThread { if (why.isNullOrEmpty()) open.finish() else open.say(why) }
        }
    }

    private val main = Handler(Looper.getMainLooper())
    private lateinit var texture: TextureView
    private lateinit var note: TextView
    private lateinit var controls: View
    private var cameraThread: HandlerThread? = null
    private var cameraHandler: Handler? = null
    private var camera: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var reader: ImageReader? = null
    private var front = false
    private var sensorDegrees = 90
    private var deviceDegrees = 0
    private var orientation: OrientationEventListener? = null

    // streaming
    @Volatile private var streaming = false
    @Volatile private var server: ServerSocket? = null
    @Volatile private var socket: Socket? = null
    private val latest = AtomicReference<ByteArray?>(null)
    private val lock = Object()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        current = this
        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        val root = FrameLayout(this)
        root.setBackgroundColor(Color.BLACK)
        texture = TextureView(this)
        root.addView(texture, FrameLayout.LayoutParams(-1, -1, Gravity.CENTER))
        note = TextView(this).apply {
            setTextColor(Color.WHITE); textSize = 15f; gravity = Gravity.CENTER
            setPadding(48, 36, 48, 36); setBackgroundColor(0xAA000000.toInt())
        }
        root.addView(note, FrameLayout.LayoutParams(-1, -2, Gravity.CENTER))

        val bar = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER }
        zoomPill = pill("0.5×", 0xB3000000.toInt()) { toggleZoom() }.also { it.visibility = View.GONE }
        bar.addView(zoomPill, LinearLayout.LayoutParams(-2, -2).also { it.marginEnd = 24 })
        bar.addView(pill("Flip", 0xB3000000.toInt()) { flip() })
        // the phone's microphone too, for a whole webcam: camera and voice
        micPill = pill(if (MicService.running) "Mic on" else "Mic off", 0xB3000000.toInt()) { toggleMic() }
        bar.addView(micPill, LinearLayout.LayoutParams(-2, -2).also { it.marginStart = 24 })
        bar.addView(pill("Stop", 0xCCD71921.toInt()) { finish() }, LinearLayout.LayoutParams(-2, -2).also { it.marginStart = 24 })
        controls = bar
        root.addView(bar, FrameLayout.LayoutParams(-2, -2, Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL).also { it.bottomMargin = 96 })
        setContentView(root)

        // the preview keeps the camera's 16:9 shape (upright on a portrait screen: 9:16)
        root.addOnLayoutChangeListener { v, _, _, _, _, _, _, _, _ -> fitPreview(v.width, v.height) }
        texture.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
            override fun onSurfaceTextureAvailable(s: SurfaceTexture, w: Int, h: Int) { openCamera() }
            override fun onSurfaceTextureSizeChanged(s: SurfaceTexture, w: Int, h: Int) {}
            override fun onSurfaceTextureDestroyed(s: SurfaceTexture) = true
            override fun onSurfaceTextureUpdated(s: SurfaceTexture) {}
        }
        // which way up the phone is held, so the laptop gets an upright picture either way
        orientation = object : OrientationEventListener(this) {
            override fun onOrientationChanged(o: Int) {
                if (o != ORIENTATION_UNKNOWN) deviceDegrees = ((o + 45) / 90 * 90) % 360
            }
        }.also { if (it.canDetectOrientation()) it.enable() }

        try { getSystemService(NotificationManager::class.java).cancel(NOTIF_ID) } catch (e: Exception) {}
        if (Build.VERSION.SDK_INT >= 31) setPictureInPictureParams(pipParams())
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(Manifest.permission.CAMERA), PERM)
        begin()
    }

    private fun pill(text: String, colour: Int, onClick: () -> Unit) = TextView(this).apply {
        this.text = text; setTextColor(Color.WHITE); textSize = 15f
        setPadding(54, 26, 54, 26)
        background = android.graphics.drawable.GradientDrawable().apply { cornerRadius = 80f; setColor(colour) }
        setOnClickListener { onClick() }
    }

    fun say(text: String) { note.text = text; note.visibility = View.VISIBLE }

    private lateinit var micPill: TextView
    private val MIC_PERM = 52

    private fun toggleMic() {
        if (MicService.running) MicService.stop(this)
        else if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), MIC_PERM)
        else MicService.start(this)
        main.postDelayed({ micPill.text = if (MicService.running) "Mic on" else "Mic off" }, 500)
    }

    private fun fitPreview(vw: Int, vh: Int) {
        if (vw <= 0 || vh <= 0) return
        // the camera's shape, upright on a portrait screen (4:3 sensor → 3:4)
        val long = Math.max(size.width, size.height); val short = Math.min(size.width, size.height)
        var w = vw; var h = vw * long / short
        if (h > vh) { h = vh; w = vh * short / long }
        val lp = texture.layoutParams as FrameLayout.LayoutParams
        if (lp.width != w || lp.height != h) { lp.width = w; lp.height = h; texture.layoutParams = lp }
    }

    // ------------------------------------------------------------ picture-in-picture

    private fun pipParams(): PictureInPictureParams {
        val b = PictureInPictureParams.Builder().setAspectRatio(Rational(3, 4))
        if (Build.VERSION.SDK_INT >= 31) b.setAutoEnterEnabled(true).setSeamlessResizeEnabled(true)
        return b.build()
    }

    override fun onUserLeaveHint() {
        super.onUserLeaveHint()
        // Home or another app: keep filming in a small floating window
        if (Build.VERSION.SDK_INT < 31 && streaming) try { enterPictureInPictureMode(pipParams()) } catch (e: Exception) {}
    }

    override fun onPictureInPictureModeChanged(inPip: Boolean, newConfig: Configuration) {
        super.onPictureInPictureModeChanged(inPip, newConfig)
        controls.visibility = if (inPip) View.GONE else View.VISIBLE
        if (inPip) note.visibility = View.GONE
    }

    override fun onStop() {
        super.onStop()
        // the floating window was closed (or picture-in-picture isn't allowed): the camera stops
        if (!isInPictureInPictureMode && !isChangingConfigurations) finish()
    }

    // ------------------------------------------------------------ session

    /** Start with the key the laptop sent, or ask the laptop for one. */
    fun begin() {
        if (streaming) return
        val svc = LinkService.instance
        if (svc == null || LinkService.linkKind.isEmpty()) { say("The laptop isn't linked. Open Nyx on the laptop, then try again."); return }
        if (!LinkService.onNetwork) { say("The webcam needs the Wi-Fi link. Put both on the same Wi-Fi."); return }
        val key = pendingKey
        if (key == null) { say("Asking the laptop…"); svc.send(JSONObject().put("t", "cam_request")); return }
        pendingKey = null
        streaming = true
        say("Connecting to the laptop…")
        Thread { stream(key) }.start()
    }

    private fun stream(key: ByteArray) {
        try {
            val ss = ServerSocket(0)
            server = ss
            ss.soTimeout = 15_000
            LinkService.instance?.send(JSONObject().put("t", "cam_ready").put("port", ss.localPort))
            val s = ss.accept()
            try { ss.close() } catch (e: Exception) {}
            socket = s
            s.tcpNoDelay = true
            main.post { say("Live on your laptop. In the call, pick \"OBS Virtual Camera\"."); main.postDelayed({ note.visibility = View.GONE }, 4000) }
            val out = DataOutputStream(s.getOutputStream().buffered(1 shl 16))
            val spec = SecretKeySpec(key, "AES")
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            var counter = 0L
            while (streaming) {
                var frame: ByteArray? = latest.getAndSet(null)
                if (frame == null) {
                    synchronized(lock) { lock.wait(500) }
                    frame = latest.getAndSet(null) ?: continue
                }
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
        } catch (e: Exception) {
        } finally {
            val was = streaming
            streaming = false
            try { server?.close() } catch (e: Exception) {}
            try { socket?.close() } catch (e: Exception) {}
            main.post { if (was && !isFinishing) say("The laptop stopped the camera. Start it again from Nyx, or tap Stop.") }
        }
    }

    // ------------------------------------------------------------ camera

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == MIC_PERM) {
            if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) toggleMic()
            else say("Nyx needs the microphone permission to be the laptop's microphone.")
            return
        }
        if (requestCode == PERM && grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) openCamera()
        else if (requestCode == PERM) say("Nyx needs the camera permission to be a webcam.")
    }

    private fun cameraId(cm: CameraManager): String? {
        val want = if (front) CameraCharacteristics.LENS_FACING_FRONT else CameraCharacteristics.LENS_FACING_BACK
        return cm.cameraIdList.firstOrNull { cm.getCameraCharacteristics(it).get(CameraCharacteristics.LENS_FACING) == want }
            ?: cm.cameraIdList.firstOrNull()
    }

    /**
     * The whole sensor: phone cameras are 4:3, and a 16:9 "video" size is a crop of it. Around
     * 1280×960 keeps it light enough to send 30 times a second.
     */
    private fun pickSize(sizes: Array<Size>): Size {
        val full = sizes.filter { it.width * 3 == it.height * 4 && it.width <= 1600 }
        val pool = if (full.isNotEmpty()) full else sizes.toList()
        return pool.minByOrNull { Math.abs(it.width - W) + Math.abs(it.height - 960) } ?: Size(W, 960)
    }

    private var size = Size(W, 960)
    private var builder: CaptureRequest.Builder? = null
    private var zoomRange: android.util.Range<Float>? = null
    private var wide = false
    private lateinit var zoomPill: TextView

    /** 0.5×: the ultra-wide camera, where the phone has one (Android 11+ picks it by zoom). */
    private fun toggleZoom() {
        val range = zoomRange ?: return
        wide = !wide
        applyZoom(range)
        zoomPill.text = if (wide) "1×" else "0.5×"
    }

    private fun applyZoom(range: android.util.Range<Float>) {
        if (Build.VERSION.SDK_INT < 30) return
        val b = builder ?: return
        b.set(CaptureRequest.CONTROL_ZOOM_RATIO, if (wide) Math.max(range.lower, 0.5f) else 1f)
        try { session?.setRepeatingRequest(b.build(), null, cameraHandler) } catch (e: Exception) {}
    }

    @Suppress("DEPRECATION")
    private fun openCamera() {
        if (camera != null || !texture.isAvailable) return
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) return
        val cm = getSystemService(CameraManager::class.java)
        val id = cameraId(cm) ?: run { say("This phone has no camera Nyx can use."); return }
        val ch = cm.getCameraCharacteristics(id)
        sensorDegrees = ch.get(CameraCharacteristics.SENSOR_ORIENTATION) ?: 90
        val map = ch.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP) ?: return
        size = pickSize(map.getOutputSizes(ImageFormat.YUV_420_888))
        fitPreview((texture.parent as View).width, (texture.parent as View).height)
        zoomRange = if (Build.VERSION.SDK_INT >= 30)
            ch.get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE)?.takeIf { it.lower < 0.95f } else null
        zoomPill.visibility = if (zoomRange != null) View.VISIBLE else View.GONE
        if (zoomRange == null) wide = false
        zoomPill.text = if (wide) "1×" else "0.5×"

        val t = HandlerThread("nyx-camera").also { it.start() }
        cameraThread = t
        val h = Handler(t.looper)
        cameraHandler = h
        val r = ImageReader.newInstance(size.width, size.height, ImageFormat.YUV_420_888, 3)
        reader = r
        r.setOnImageAvailableListener({ rd ->
            val img = try { rd.acquireLatestImage() } catch (e: Exception) { null } ?: return@setOnImageAvailableListener
            try { if (streaming && socket != null) encode(img) } catch (e: Exception) {} finally { img.close() }
        }, h)

        try {
            cm.openCamera(id, object : CameraDevice.StateCallback() {
                override fun onOpened(cam: CameraDevice) {
                    camera = cam
                    val st = texture.surfaceTexture ?: return
                    st.setDefaultBufferSize(size.width, size.height)
                    val preview = Surface(st)
                    cam.createCaptureSession(listOf(preview, r.surface), object : CameraCaptureSession.StateCallback() {
                        override fun onConfigured(s: CameraCaptureSession) {
                            session = s
                            val req = cam.createCaptureRequest(CameraDevice.TEMPLATE_RECORD).apply {
                                addTarget(preview); addTarget(r.surface)
                                set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO)
                                // video stabilisation crops in; a webcam sits still, so keep the full view
                                set(CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE, CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE_OFF)
                            }
                            builder = req
                            val range = zoomRange
                            if (range != null && wide) applyZoom(range)
                            else try { s.setRepeatingRequest(req.build(), null, h) } catch (e: Exception) {}
                        }
                        override fun onConfigureFailed(s: CameraCaptureSession) { main.post { say("The camera couldn't start.") } }
                    }, h)
                }
                override fun onDisconnected(cam: CameraDevice) { cam.close(); camera = null }
                override fun onError(cam: CameraDevice, error: Int) {
                    cam.close(); camera = null
                    main.post { say("The camera is busy (another app may be using it).") }
                }
            }, h)
        } catch (e: SecurityException) { say("Nyx needs the camera permission to be a webcam.") }
    }

    private fun closeCamera() {
        try { session?.close() } catch (e: Exception) {}
        try { camera?.close() } catch (e: Exception) {}
        try { reader?.close() } catch (e: Exception) {}
        session = null; camera = null; reader = null; builder = null
        cameraThread?.quitSafely(); cameraThread = null; cameraHandler = null
    }

    private fun flip() {
        front = !front
        closeCamera()
        openCamera()
    }

    /** One camera frame as [quarter turns to upright][JPEG], handed to the sender (only the newest is kept). */
    private fun encode(img: Image) {
        val w = img.width; val h = img.height
        val nv21 = toNv21(img)
        val jpeg = ByteArrayOutputStream(w * h / 4)
        jpeg.write(turns())
        YuvImage(nv21, ImageFormat.NV21, w, h, null).compressToJpeg(Rect(0, 0, w, h), 70, jpeg)
        latest.set(jpeg.toByteArray())
        synchronized(lock) { lock.notify() }
    }

    /** How many quarter turns clockwise make the picture upright, from how the phone is held. */
    private fun turns(): Int {
        val deg = if (front) (sensorDegrees - deviceDegrees + 360) % 360 else (sensorDegrees + deviceDegrees) % 360
        return deg / 90
    }

    private fun toNv21(img: Image): ByteArray {
        val w = img.width; val h = img.height
        val out = ByteArray(w * h * 3 / 2)
        val y = img.planes[0]; val u = img.planes[1]; val v = img.planes[2]
        copyPlane(y.buffer, y.rowStride, y.pixelStride, w, h, out, 0, 1)
        // NV21: V then U, interleaved, at half resolution
        copyPlane(v.buffer, v.rowStride, v.pixelStride, w / 2, h / 2, out, w * h, 2)
        copyPlane(u.buffer, u.rowStride, u.pixelStride, w / 2, h / 2, out, w * h + 1, 2)
        return out
    }

    private fun copyPlane(buf: ByteBuffer, rowStride: Int, pixelStride: Int, w: Int, h: Int, out: ByteArray, start: Int, step: Int) {
        val row = ByteArray(rowStride)
        var o = start
        for (r in 0 until h) {
            buf.position(r * rowStride)
            val len = Math.min(rowStride, buf.remaining())
            buf.get(row, 0, len)
            if (pixelStride == 1 && step == 1) { System.arraycopy(row, 0, out, o, w); o += w }
            else for (c in 0 until w) { val i = c * pixelStride; if (i < len) out[o] = row[i]; o += step }
        }
    }

    override fun onDestroy() {
        if (current === this) current = null
        val was = streaming
        streaming = false
        synchronized(lock) { lock.notifyAll() }
        try { server?.close() } catch (e: Exception) {}
        try { socket?.close() } catch (e: Exception) {}
        orientation?.disable()
        closeCamera()
        if (was) LinkService.instance?.send(JSONObject().put("t", "cam_stop"))
        super.onDestroy()
    }
}
