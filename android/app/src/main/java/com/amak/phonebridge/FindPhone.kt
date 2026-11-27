package com.amak.phonebridge

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.media.AudioAttributes
import android.media.AudioManager
import android.media.Ringtone
import android.media.RingtoneManager
import android.os.Handler
import android.os.Looper

/**
 * "Find phone" from the laptop: rings loudly on the alarm stream (so silent mode and
 * Do Not Disturb's ringer settings don't mute it) for up to 30 seconds. The alarm volume
 * goes back to what it was afterwards. A notification with Stop ends it on the phone.
 */
object FindPhone {
    private const val CHANNEL = "find_phone"
    private const val NOTIF_ID = 4711
    private const val SECONDS = 30L

    private val main = Handler(Looper.getMainLooper())
    private var tone: Ringtone? = null
    private var oldVolume = -1
    private var onDone: (() -> Unit)? = null

    val ringing get() = tone != null

    fun start(c: Context, done: () -> Unit) {
        main.post {
            if (tone != null) return@post
            val app = c.applicationContext
            val am = app.getSystemService(AudioManager::class.java)
            try {
                oldVolume = am.getStreamVolume(AudioManager.STREAM_ALARM)
                am.setStreamVolume(AudioManager.STREAM_ALARM, am.getStreamMaxVolume(AudioManager.STREAM_ALARM), 0)
            } catch (e: Exception) { oldVolume = -1 }
            val uri = RingtoneManager.getActualDefaultRingtoneUri(app, RingtoneManager.TYPE_RINGTONE)
                ?: RingtoneManager.getDefaultUri(RingtoneManager.TYPE_ALARM)
            val t = RingtoneManager.getRingtone(app, uri) ?: run { restore(app); done(); return@post }
            t.audioAttributes = AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_ALARM)
                .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                .build()
            if (android.os.Build.VERSION.SDK_INT >= 28) t.isLooping = true
            try { t.play() } catch (e: Exception) { restore(app); done(); return@post }
            tone = t
            onDone = done
            showNotification(app)
            main.postDelayed({ stop(app) }, SECONDS * 1000)
        }
    }

    fun stop(c: Context) {
        main.post {
            val t = tone ?: return@post
            tone = null
            main.removeCallbacksAndMessages(null)
            try { t.stop() } catch (e: Exception) {}
            val app = c.applicationContext
            restore(app)
            try { app.getSystemService(NotificationManager::class.java).cancel(NOTIF_ID) } catch (e: Exception) {}
            onDone?.invoke()
            onDone = null
        }
    }

    private fun restore(c: Context) {
        if (oldVolume < 0) return
        try { c.getSystemService(AudioManager::class.java).setStreamVolume(AudioManager.STREAM_ALARM, oldVolume, 0) } catch (e: Exception) {}
        oldVolume = -1
    }

    private fun showNotification(c: Context) {
        val nm = c.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Find phone", NotificationManager.IMPORTANCE_HIGH))
        val stop = PendingIntent.getBroadcast(c, 41, Intent(c, StopReceiver::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val n = Notification.Builder(c, CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_nyx)
            .setContentTitle("Your laptop is ringing this phone")
            .setContentText("Tap to stop.")
            .setContentIntent(stop)
            .addAction(Notification.Action.Builder(null, "Stop", stop).build())
            .setAutoCancel(true)
            .build()
        try { nm.notify(NOTIF_ID, n) } catch (e: Exception) {}
    }

    /** Not exported: only Nyx's own notification can stop the ringing. */
    class StopReceiver : BroadcastReceiver() {
        override fun onReceive(c: Context, i: Intent) = stop(c)
    }
}
