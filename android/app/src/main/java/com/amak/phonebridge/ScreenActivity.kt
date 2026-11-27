package com.amak.phonebridge

import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.ActivityInfo
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.TextView
import org.json.JSONObject
import java.io.DataInputStream
import java.net.ServerSocket
import java.net.Socket
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * The phone as a second screen for the laptop. Full screen, landscape. The laptop sends a
 * fresh key over Nyx's encrypted link; this screen then listens once on a free port, the
 * laptop connects and streams JPEG frames sealed with AES-256-GCM. Fingers go back over
 * the link as real touch input, so it works like a touchscreen and the laptop's own mouse
 * pointer stays where it is.
 */
class ScreenActivity : Activity() {

    companion object {
        private const val CHANNEL = "second_screen"
        private const val NOTIF_ID = 4712
        private const val MAX_FRAME = 8 * 1024 * 1024

        @Volatile private var pendingKey: ByteArray? = null
        @Volatile private var current: ScreenActivity? = null

        /** The laptop offered a session: hand the key to the open screen, or ask to open it. */
        fun offer(c: Context, key: ByteArray) {
            pendingKey = key
            val open = current
            if (open != null) { open.runOnUiThread { open.begin() }; return }
            notifyOffer(c.applicationContext)
        }

        /** The laptop's open windows, after the Windows button asked for them. */
        fun windows(items: org.json.JSONArray) {
            val open = current ?: return
            open.runOnUiThread { open.pickWindow(items) }
        }

        /** The laptop stopped (or couldn't start): close the screen with a word why. */
        fun stopFromLaptop(why: String?) {
            pendingKey = null
            val open = current ?: return
            open.runOnUiThread {
                if (why.isNullOrEmpty()) open.finish() else open.say(why)
            }
        }

        private fun notifyOffer(c: Context) {
            val nm = c.getSystemService(NotificationManager::class.java)
            nm.createNotificationChannel(NotificationChannel(CHANNEL, "Second screen", NotificationManager.IMPORTANCE_HIGH))
            val open = PendingIntent.getActivity(c, 42, Intent(c, ScreenActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
            val n = Notification.Builder(c, CHANNEL)
                .setSmallIcon(R.drawable.ic_stat_nyx)
                .setContentTitle("Use as second screen")
                .setContentText("Your laptop is ready to extend onto this phone. Tap to start.")
                .setContentIntent(open)
                .setAutoCancel(true)
                .setTimeoutAfter(60_000)
                .build()
            try { nm.notify(NOTIF_ID, n) } catch (e: Exception) {}
        }
    }

    private val main = Handler(Looper.getMainLooper())
    private lateinit var image: ImageView
    private lateinit var note: TextView
    @Volatile private var running = false
    @Volatile private var server: ServerSocket? = null
    @Volatile private var socket: Socket? = null
    private var frameW = 0
    private var frameH = 0
    private var shown: Bitmap? = null
    private var hinted = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        current = this
        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        if (Build.VERSION.SDK_INT >= 28)
            window.attributes.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES

        val root = FrameLayout(this)
        root.setBackgroundColor(Color.BLACK)
        image = ImageView(this).apply { scaleType = ImageView.ScaleType.FIT_CENTER }
        note = TextView(this).apply {
            setTextColor(Color.WHITE); textSize = 15f; gravity = Gravity.CENTER
            setPadding(48, 48, 48, 48); alpha = 0.85f
        }
        root.addView(image, FrameLayout.LayoutParams(-1, -1))
        root.addView(note, FrameLayout.LayoutParams(-1, -2, Gravity.CENTER))

        // the way to put something on this screen: pick one of the laptop's windows
        val pick = TextView(this).apply {
            text = "Windows"
            setTextColor(Color.WHITE); textSize = 14f
            setPadding(36, 18, 36, 18)
            background = android.graphics.drawable.GradientDrawable().apply {
                cornerRadius = 60f; setColor(0xB3000000.toInt()); setStroke(2, 0x55FFFFFF)
            }
            setOnClickListener {
                LinkService.instance?.send(JSONObject().put("t", "screen_windows"))
            }
        }
        // Stop: ends the session; the laptop takes its windows back and switches the virtual screen off
        val stop = TextView(this).apply {
            text = "Stop"
            setTextColor(Color.WHITE); textSize = 14f
            setPadding(36, 18, 36, 18)
            background = android.graphics.drawable.GradientDrawable().apply {
                cornerRadius = 60f; setColor(0xCCD71921.toInt())
            }
            setOnClickListener { finish() }
        }
        val pills = android.widget.LinearLayout(this).apply {
            orientation = android.widget.LinearLayout.HORIZONTAL
            addView(pick)
            addView(stop, android.widget.LinearLayout.LayoutParams(-2, -2).also { it.marginStart = 18 })
        }
        root.addView(pills, FrameLayout.LayoutParams(-2, -2, Gravity.TOP or Gravity.END).also { it.setMargins(0, 36, 36, 0) })
        setContentView(root)
        image.setOnTouchListener { _, ev -> onTouch(ev); true }
        hideBars()
        try { getSystemService(NotificationManager::class.java).cancel(NOTIF_ID) } catch (e: Exception) {}
        begin()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideBars()
    }

    private fun hideBars() {
        if (Build.VERSION.SDK_INT >= 30) {
            window.insetsController?.let {
                it.hide(WindowInsets.Type.systemBars())
                it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
        } else {
            @Suppress("DEPRECATION")
            window.decorView.systemUiVisibility = (View.SYSTEM_UI_FLAG_FULLSCREEN or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY)
        }
    }

    fun say(text: String) {
        note.text = text
        note.visibility = View.VISIBLE
    }

    /** A list of the laptop's windows: tap one to bring it here (or, if it's here, send it back). */
    fun pickWindow(items: org.json.JSONArray) {
        if (isFinishing) return
        val ids = ArrayList<Long>(); val here = ArrayList<Boolean>(); val labels = ArrayList<String>()
        for (i in 0 until minOf(items.length(), 40)) {
            val o = items.optJSONObject(i) ?: continue
            ids += o.optLong("id"); here += o.optBoolean("here")
            labels += (if (o.optBoolean("here")) "✓  " else "") + o.optString("title").take(80)
        }
        if (ids.isEmpty()) { say("No open windows on the laptop."); main.postDelayed({ note.visibility = View.GONE }, 2500); return }
        android.app.AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
            .setTitle("Bring a window here")
            .setItems(labels.toTypedArray()) { _, i ->
                LinkService.instance?.send(JSONObject().put("t", "screen_move").put("id", ids[i]).put("back", here[i]))
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    /** Start with the key the laptop sent, or ask the laptop for one. */
    fun begin() {
        if (running) return
        val svc = LinkService.instance
        if (svc == null || LinkService.linkKind.isEmpty()) { say("The laptop isn't linked. Open Nyx on the laptop, then try again."); return }
        if (!LinkService.onNetwork) { say("The second screen needs the Wi-Fi link. Put both on the same Wi-Fi."); return }
        val key = pendingKey
        if (key == null) {
            say("Asking the laptop…")
            svc.send(JSONObject().put("t", "screen_request"))
            return
        }
        pendingKey = null
        running = true
        say("Connecting…")
        Thread { receive(key) }.start()
    }

    private fun receive(key: ByteArray) {
        try {
            val ss = ServerSocket(0)
            server = ss
            ss.soTimeout = 15_000
            LinkService.instance?.send(JSONObject().put("t", "screen_ready").put("port", ss.localPort))
            val s = ss.accept()
            try { ss.close() } catch (e: Exception) {}
            socket = s
            s.tcpNoDelay = true
            s.soTimeout = 10_000 // the laptop sends at least every 2 s
            val input = DataInputStream(s.getInputStream().buffered(1 shl 16))
            val spec = SecretKeySpec(key, "AES")
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            val opts = BitmapFactory.Options()
            var lastCounter = 0L
            while (running) {
                val len = input.readInt()
                if (len < 12 + 16 || len > MAX_FRAME) break
                val buf = ByteArray(len)
                input.readFully(buf)
                // nonces count up: a replayed or reordered frame is dropped
                var counter = 0L
                for (i in 4 until 12) counter = (counter shl 8) or (buf[i].toLong() and 0xff)
                if (counter <= lastCounter) continue
                cipher.init(Cipher.DECRYPT_MODE, spec, GCMParameterSpec(128, buf, 0, 12))
                val jpeg = try { cipher.doFinal(buf, 12, len - 12) } catch (e: Exception) { break } // tampered: stop
                lastCounter = counter
                val bmp = BitmapFactory.decodeByteArray(jpeg, 0, jpeg.size, opts) ?: continue
                main.post {
                    if (!running) { bmp.recycle(); return@post }
                    val old = shown
                    shown = bmp
                    frameW = bmp.width; frameH = bmp.height
                    image.setImageBitmap(bmp)
                    if (!hinted) {
                        // a new monitor starts empty: say so, or it looks like nothing happened
                        hinted = true
                        say("This is your laptop's extra screen. It starts empty: tap Windows (top right) to bring one of the laptop's windows here. Use it like a touchscreen: swipe to scroll, pinch to zoom, long-press for right-click.")
                        note.setBackgroundColor(0xCC000000.toInt())
                        main.postDelayed({ note.animate().alpha(0f).setDuration(400).withEndAction { note.visibility = View.GONE; note.alpha = 0.85f } }, 6000)
                    } else if (note.alpha > 0f && note.text.startsWith("Connecting")) note.visibility = View.GONE
                    if (old != null && old !== bmp) old.recycle()
                }
            }
        } catch (e: Exception) {
        } finally {
            val wasRunning = running
            running = false
            try { server?.close() } catch (e: Exception) {}
            try { socket?.close() } catch (e: Exception) {}
            main.post { if (wasRunning && !isFinishing) say("The laptop stopped sending. Press Extend to phone on the laptop, or go back.") }
        }
    }

    // ------------------------------------------------------------ touch

    // Every finger goes to the laptop as real touch, so the extra screen behaves like a
    // touchscreen there: swipe to scroll, pinch to zoom, long-press for right-click.
    private val fingers = LinkedHashMap<Int, Pair<Float, Float>>()
    private var lastMove = 0L
    // Windows lets go of a touch it hasn't heard about for a while: keep a still finger alive
    private val keepAlive = object : Runnable {
        override fun run() {
            if (fingers.isEmpty()) return
            sendFingers("move", -1)
            main.postDelayed(this, 100)
        }
    }

    private fun onTouch(ev: MotionEvent) {
        if (frameW == 0) return
        for (i in 0 until ev.pointerCount) fingers[ev.getPointerId(i)] = ev.getX(i) to ev.getY(i)
        when (ev.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN -> {
                sendFingers("down", ev.getPointerId(ev.actionIndex))
                main.removeCallbacks(keepAlive)
                main.postDelayed(keepAlive, 100)
            }
            MotionEvent.ACTION_MOVE -> {
                if (ev.eventTime - lastMove >= 16) { lastMove = ev.eventTime; sendFingers("move", -1) }
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP -> {
                val id = ev.getPointerId(ev.actionIndex)
                sendFingers("up", id)
                fingers.remove(id)
                if (fingers.isEmpty()) main.removeCallbacks(keepAlive)
            }
            MotionEvent.ACTION_CANCEL -> {
                sendFingers("cancel", -1)
                fingers.clear()
                main.removeCallbacks(keepAlive)
            }
        }
    }

    /** Each finger as [id, x, y], x and y 0..1 across the laptop's picture (letterboxed to fit). */
    private fun sendFingers(action: String, changed: Int) {
        val vw = image.width.toFloat(); val vh = image.height.toFloat()
        if (vw <= 0 || vh <= 0 || frameW == 0) return
        val scale = Math.min(vw / frameW, vh / frameH)
        val left = (vw - frameW * scale) / 2
        val top = (vh - frameH * scale) / 2
        val pts = org.json.JSONArray()
        for ((id, p) in fingers) {
            if (id !in 0..9) continue
            val x = ((p.first - left) / (frameW * scale)).coerceIn(0f, 1f)
            val y = ((p.second - top) / (frameH * scale)).coerceIn(0f, 1f)
            pts.put(org.json.JSONArray().put(id).put(x.toDouble()).put(y.toDouble()))
        }
        LinkService.instance?.send(JSONObject().put("t", "screen_fingers").put("a", action).put("i", changed).put("p", pts))
    }

    override fun onDestroy() {
        if (current === this) current = null
        val was = running
        running = false
        try { server?.close() } catch (e: Exception) {}
        try { socket?.close() } catch (e: Exception) {}
        if (was) LinkService.instance?.send(JSONObject().put("t", "screen_stop"))
        shown = null
        super.onDestroy()
    }
}
