package com.amak.phonebridge

import android.Manifest
import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.IBinder
import org.json.JSONObject

/**
 * Plays the phone's sound on the laptop. Android's audio capture (Android 10+, after the same
 * "Start sharing?" question as screen sharing) records what apps are playing; Nyx seals 20 ms
 * at a time with AES-256-GCM under a fresh key from the laptop and streams it over Wi-Fi.
 * Apps can opt out of being captured (calls and some streaming apps do).
 */
class AudioShareService : Service() {

    companion object {
        private const val CHANNEL = "phone_audio"
        private const val NOTIF_ID = 4718
        private const val OFFER_ID = 4719
        const val ACTION_STOP = "com.amak.phonebridge.AUDIO_STOP"

        @Volatile var pendingKey: ByteArray? = null
        @Volatile var waiting = false
        @Volatile var instance: AudioShareService? = null
            private set
        val running get() = instance != null

        fun offer(c: Context, key: ByteArray) {
            pendingKey = key
            if (instance != null || waiting) return
            val app = c.applicationContext
            val nm = app.getSystemService(NotificationManager::class.java)
            nm.createNotificationChannel(NotificationChannel(CHANNEL, "Phone audio on laptop", NotificationManager.IMPORTANCE_HIGH))
            val start = Intent(app, AudioStartActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
            val pi = PendingIntent.getActivity(app, 48, start, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
            val n = Notification.Builder(app, CHANNEL)
                .setSmallIcon(R.drawable.ic_stat_nyx)
                .setContentTitle("Play phone audio on the laptop")
                .setContentText("Tap, then Start.")
                .setContentIntent(pi).setAutoCancel(true).setTimeoutAfter(60_000).build()
            try { nm.notify(OFFER_ID, n) } catch (e: Exception) {}
            MirrorService.popUp(app, start)
        }

        fun stopFromLaptop() { pendingKey = null; instance?.finish(tell = false) }
        fun cancelOffer(c: Context) { try { c.getSystemService(NotificationManager::class.java).cancel(OFFER_ID) } catch (e: Exception) {} }
    }

    private var projection: MediaProjection? = null
    @Volatile private var streaming = false

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) { finish(tell = true); return START_NOT_STICKY }
        val stop = PendingIntent.getService(this, 49, Intent(this, AudioShareService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Phone audio on laptop", NotificationManager.IMPORTANCE_HIGH))
        val n = Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_nyx)
            .setContentTitle("Your phone's sound plays on the laptop")
            .setContentText("Music and videos from this phone come out of your laptop. Tap Stop to end it.")
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
        if (Build.VERSION.SDK_INT < 29 || code != Activity.RESULT_OK || data == null || key == null) { finish(tell = false); return START_NOT_STICKY }
        pendingKey = null
        instance = this
        val mp = getSystemService(MediaProjectionManager::class.java).getMediaProjection(code, data)
            ?: run { finish(tell = false); return START_NOT_STICKY }
        projection = mp
        mp.registerCallback(object : MediaProjection.Callback() { override fun onStop() { finish(tell = true) } }, Handler(mainLooper))
        streaming = true
        capture = AudioCapture(this, key, mp) { finish(tell = true) }.also { it.start() }
        return START_NOT_STICKY
    }

    private var capture: AudioCapture? = null

    fun finish(tell: Boolean) {
        val was = streaming
        streaming = false
        try { capture?.stop() } catch (e: Exception) {}
        capture = null
        try { projection?.stop() } catch (e: Exception) {}
        projection = null
        if (tell && was) LinkService.instance?.send(JSONObject().put("t", "audio_stop"))
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

/** Asks for the microphone permission (Android requires it to capture app sound) and the sharing question. */
class AudioStartActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        AudioShareService.cancelOffer(this)
        if (Build.VERSION.SDK_INT < 29) {
            android.widget.Toast.makeText(this, "Needs Android 10 or newer.", android.widget.Toast.LENGTH_LONG).show()
            finish(); return
        }
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), 7)
            return
        }
        next()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) next()
        else {
            android.widget.Toast.makeText(this, "Android needs the microphone permission to pass app sound to the laptop.", android.widget.Toast.LENGTH_LONG).show()
            finish()
        }
    }

    private fun next() {
        if (AudioShareService.pendingKey != null) { ask(); return }
        if (LinkService.instance == null || !LinkService.onNetwork) {
            android.widget.Toast.makeText(this, "Link the laptop over Wi-Fi first.", android.widget.Toast.LENGTH_LONG).show()
            finish(); return
        }
        AudioShareService.waiting = true
        LinkService.instance?.send(JSONObject().put("t", "audio_request"))
        val h = Handler(mainLooper)
        val started = System.currentTimeMillis()
        h.post(object : Runnable {
            override fun run() {
                when {
                    AudioShareService.pendingKey != null -> { AudioShareService.waiting = false; ask() }
                    System.currentTimeMillis() - started > 6000 -> {
                        AudioShareService.waiting = false
                        android.widget.Toast.makeText(this@AudioStartActivity, "The laptop didn't answer. Open Nyx on the laptop.", android.widget.Toast.LENGTH_LONG).show()
                        finish()
                    }
                    else -> h.postDelayed(this, 100)
                }
            }
        })
    }

    private fun ask() {
        @Suppress("DEPRECATION")
        startActivityForResult(MirrorService.captureIntent(this), 1)
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        @Suppress("DEPRECATION")
        super.onActivityResult(requestCode, resultCode, data)
        if (resultCode == RESULT_OK && data != null)
            startForegroundService(Intent(this, AudioShareService::class.java).putExtra("code", resultCode).putExtra("data", data))
        else {
            AudioShareService.pendingKey = null
            LinkService.instance?.send(JSONObject().put("t", "audio_stop"))
        }
        finish()
    }
}
