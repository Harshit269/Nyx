package com.amak.phonebridge

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.os.Build
import android.os.IBinder
import org.json.JSONObject
import java.io.DataOutputStream
import java.net.ServerSocket
import java.net.Socket
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * The phone as the laptop's microphone. Records with the voice-call source (Android's echo
 * cancelling and noise suppression), seals 20 ms at a time with AES-256-GCM under a fresh key
 * the laptop sent inside Nyx's link, and streams it over Wi-Fi. Runs as a foreground service
 * with its own notification, so it keeps going while you use the phone; Stop ends it.
 */
class MicService : Service() {

    companion object {
        private const val CHANNEL = "microphone"
        private const val NOTIF_ID = 4714
        private const val OFFER_ID = 4715
        private const val RATE = 48000
        private const val FRAME = 960 // 20 ms
        const val ACTION_STOP = "com.amak.phonebridge.MIC_STOP"
        const val ACTION_START = "com.amak.phonebridge.MIC_START"

        @Volatile private var pendingKey: ByteArray? = null
        @Volatile var instance: MicService? = null
            private set
        val running get() = instance != null

        /** Must be started from something on screen (Android only lets a visible app open the mic). */
        fun start(c: Context) {
            c.startForegroundService(Intent(c, MicService::class.java))
        }

        fun stop(c: Context) {
            c.startService(Intent(c, MicService::class.java).setAction(ACTION_STOP))
        }

        /** The laptop sent a key: start streaming, or ask (by notification) to be started. */
        fun offer(c: Context, key: ByteArray) {
            pendingKey = key
            val svc = instance
            if (svc != null) { svc.begin(); return }
            val app = c.applicationContext
            val nm = app.getSystemService(NotificationManager::class.java)
            nm.createNotificationChannel(NotificationChannel(CHANNEL, "Microphone", NotificationManager.IMPORTANCE_HIGH))
            // opens Nyx for a moment, which may start the microphone (a hidden app can't)
            val pi = PendingIntent.getActivity(app, 44, Intent(app, MainActivity::class.java).setAction(ACTION_START)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
            val n = Notification.Builder(app, CHANNEL)
                .setSmallIcon(R.drawable.ic_stat_nyx)
                .setContentTitle("Use as microphone")
                .setContentText("Your laptop wants this phone's microphone. Tap to start.")
                .setContentIntent(pi).setAutoCancel(true).setTimeoutAfter(60_000).build()
            try { nm.notify(OFFER_ID, n) } catch (e: Exception) {}
        }

        fun stopFromLaptop() {
            pendingKey = null
            instance?.let { it.stopSelfNow(tell = false) }
        }
    }

    @Volatile private var streaming = false
    @Volatile private var server: ServerSocket? = null
    @Volatile private var socket: Socket? = null
    @Volatile private var recorder: AudioRecord? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) { stopSelfNow(tell = true); return START_NOT_STICKY }
        instance = this
        val stop = PendingIntent.getService(this, 45, Intent(this, MicService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Microphone", NotificationManager.IMPORTANCE_HIGH))
        try { nm.cancel(OFFER_ID) } catch (e: Exception) {}
        val n = Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_nyx)
            .setContentTitle("Nyx is using your microphone")
            .setContentText("Your laptop hears this phone. Tap Stop to end it.")
            .setOngoing(true)
            .addAction(Notification.Action.Builder(null, "Stop", stop).build())
            .build()
        try {
            if (Build.VERSION.SDK_INT >= 30) startForeground(NOTIF_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE)
            else startForeground(NOTIF_ID, n)
        } catch (e: Exception) {
            stopSelf(); instance = null; return START_NOT_STICKY // not allowed right now (app wasn't on screen)
        }
        begin()
        return START_NOT_STICKY
    }

    /** Stream with the key the laptop sent, or ask the laptop for one. */
    fun begin() {
        if (streaming) return
        val svc = LinkService.instance
        if (svc == null || !LinkService.onNetwork) { stopSelfNow(tell = false); return }
        val key = pendingKey
        if (key == null) { svc.send(JSONObject().put("t", "mic_request")); return }
        pendingKey = null
        streaming = true
        Thread { stream(key) }.start()
    }

    private fun stream(key: ByteArray) {
        try {
            val ss = ServerSocket(0)
            server = ss
            ss.soTimeout = 15_000
            LinkService.instance?.send(JSONObject().put("t", "mic_ready").put("port", ss.localPort))
            val s = ss.accept()
            try { ss.close() } catch (e: Exception) {}
            socket = s
            s.tcpNoDelay = true
            val out = DataOutputStream(s.getOutputStream())

            val min = AudioRecord.getMinBufferSize(RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
            @Suppress("MissingPermission")
            val rec = AudioRecord(MediaRecorder.AudioSource.VOICE_COMMUNICATION, RATE, AudioFormat.CHANNEL_IN_MONO,
                AudioFormat.ENCODING_PCM_16BIT, Math.max(min, FRAME * 2 * 4))
            recorder = rec
            rec.startRecording()

            val spec = SecretKeySpec(key, "AES")
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            val pcm = ByteArray(FRAME * 2)
            var counter = 0L
            while (streaming) {
                var got = 0
                while (got < pcm.size && streaming) {
                    val n = rec.read(pcm, got, pcm.size - got)
                    if (n <= 0) throw java.io.IOException("microphone stopped")
                    got += n
                }
                counter++
                val nonce = ByteArray(12)
                for (i in 0 until 8) nonce[4 + i] = (counter ushr (56 - 8 * i)).toByte()
                cipher.init(Cipher.ENCRYPT_MODE, spec, GCMParameterSpec(128, nonce))
                val sealed = cipher.doFinal(pcm)
                out.writeInt(12 + sealed.size)
                out.write(nonce)
                out.write(sealed)
                out.flush()
            }
        } catch (e: Exception) {
        } finally {
            stopSelfNow(tell = streaming)
        }
    }

    fun stopSelfNow(tell: Boolean) {
        val was = streaming
        streaming = false
        try { recorder?.stop() } catch (e: Exception) {}
        try { recorder?.release() } catch (e: Exception) {}
        recorder = null
        try { server?.close() } catch (e: Exception) {}
        try { socket?.close() } catch (e: Exception) {}
        if (tell && was) LinkService.instance?.send(JSONObject().put("t", "mic_stop"))
        instance = null
        try { stopForeground(STOP_FOREGROUND_REMOVE) } catch (e: Exception) {}
        stopSelf()
    }

    override fun onDestroy() {
        if (streaming) stopSelfNow(tell = true)
        instance = null
        super.onDestroy()
    }
}
