package com.amak.phonebridge

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.projection.MediaProjection
import android.os.Build
import org.json.JSONObject
import java.io.DataOutputStream
import java.net.ServerSocket
import java.net.Socket
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * What the phone's apps are playing, streamed to the laptop: Android's audio capture on a
 * screen-sharing session (one "Start sharing?" covers screen and sound), 48 kHz stereo, sealed
 * 20 ms at a time with AES-256-GCM. Used by audio-only sharing and by mirroring "with sound".
 *
 * The sound *moves* to the laptop, like a cable: the phone's media volume is muted while it
 * streams (Android hands captured sound over at full level whatever the volume) and comes back
 * when it ends. Sound Android can't capture (Spotify plays through the low-latency AAudio path,
 * which skips capture; some apps also opt out) arrives as silence, so when the
 * phone is playing yet nothing comes through, the phone's speaker is unmuted again and the laptop
 * is told why ("audio_note").
 */
class AudioCapture(
    ctx: Context,
    private val key: ByteArray,
    private val projection: MediaProjection,
    private val onEnd: () -> Unit
) {

    companion object {
        const val RATE = 48000
        private const val FRAME = 960 // 20 ms
        private const val PREFS = "nyx_audio"

        /** If Nyx ended without unmuting (killed mid-stream), give the phone its sound back. */
        fun restoreIfLeftMuted(c: Context) {
            val p = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            if (!p.getBoolean("muted", false)) return
            try { c.getSystemService(AudioManager::class.java).adjustStreamVolume(AudioManager.STREAM_MUSIC, AudioManager.ADJUST_UNMUTE, 0) } catch (e: Exception) {}
            p.edit().putBoolean("muted", false).apply()
        }

        @Volatile var current: AudioCapture? = null
            private set
    }

    private val app = ctx.applicationContext
    private val am = app.getSystemService(AudioManager::class.java)
    /** The laptop's mute button: false = the laptop muted it, so the phone plays it itself. */
    @Volatile var toLaptop = true
        set(v) { field = v; if (!v) quietPhone(false) }
    @Volatile private var phoneMuted = false
    @Volatile private var muteBreaksCapture = false // some phones apply the volume to the capture too
    private var blockedSaid = false

    @Volatile private var running = false
    @Volatile private var server: ServerSocket? = null
    @Volatile private var socket: Socket? = null
    @Volatile private var recorder: AudioRecord? = null

    fun start() {
        if (Build.VERSION.SDK_INT < 29 || running) return
        running = true
        current = this
        Thread { run() }.start()
    }

    @Suppress("MissingPermission")
    private fun run() {
        try {
            if (Build.VERSION.SDK_INT < 29) return
            val config = AudioPlaybackCaptureConfiguration.Builder(projection)
                .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                .addMatchingUsage(AudioAttributes.USAGE_GAME)
                .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                .build()
            val format = AudioFormat.Builder().setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                .setSampleRate(RATE).setChannelMask(AudioFormat.CHANNEL_IN_STEREO).build()
            val min = AudioRecord.getMinBufferSize(RATE, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT)
            val rec = AudioRecord.Builder().setAudioFormat(format).setAudioPlaybackCaptureConfig(config)
                .setBufferSizeInBytes(Math.max(min, FRAME * 4 * 4)).build()
            recorder = rec

            val ss = ServerSocket(0)
            server = ss
            ss.soTimeout = 15_000
            LinkService.instance?.send(JSONObject().put("t", "audio_ready").put("port", ss.localPort))
            val s = ss.accept()
            try { ss.close() } catch (e: Exception) {}
            socket = s
            s.tcpNoDelay = true
            val out = DataOutputStream(s.getOutputStream())
            rec.startRecording()

            val spec = SecretKeySpec(key, "AES")
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            val pcm = ByteArray(FRAME * 4) // stereo, 16-bit
            var counter = 0L
            var silentSince = 0L   // music playing on the phone but nothing captured, since when
            var unmutedAt = 0L     // when silence made us give the phone its sound back
            while (running) {
                var got = 0
                while (got < pcm.size && running) {
                    val n = rec.read(pcm, got, pcm.size - got)
                    if (n <= 0) throw java.io.IOException("capture stopped")
                    got += n
                }
                val now = System.currentTimeMillis()
                if (loud(pcm)) {
                    silentSince = 0
                    if (unmutedAt != 0L && now - unmutedAt < 1000) muteBreaksCapture = true // muting silenced it
                    unmutedAt = 0
                    if (blockedSaid) { blockedSaid = false; note(false) }
                    if (toLaptop && !muteBreaksCapture) quietPhone(true)
                } else if (playing()) {
                    if (silentSince == 0L) silentSince = now
                    if (now - silentSince > 3000) { // not a video still loading
                        if (phoneMuted) { quietPhone(false); unmutedAt = now; silentSince = now }
                        else if (unmutedAt == 0L || now - unmutedAt > 1000) {
                            unmutedAt = 0
                            if (!blockedSaid) { blockedSaid = true; note(true) }
                        }
                    }
                } else silentSince = 0
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
            val was = running
            stop()
            if (was) onEnd()
        }
    }

    private fun loud(pcm: ByteArray): Boolean {
        var i = 0
        while (i < pcm.size - 1) {
            val v = (pcm[i].toInt() and 0xFF) or (pcm[i + 1].toInt() shl 8)
            if (v > 24 || v < -24) return true
            i += 8 // every other stereo frame is plenty
        }
        return false
    }

    private fun playing() = try { am.isMusicActive } catch (e: Exception) { false }

    /** Mutes (or gives back) the phone's own speaker for media; remembered in case Nyx is killed. */
    private fun quietPhone(on: Boolean) {
        if (on == phoneMuted) return
        try {
            am.adjustStreamVolume(AudioManager.STREAM_MUSIC, if (on) AudioManager.ADJUST_MUTE else AudioManager.ADJUST_UNMUTE, 0)
            phoneMuted = on
            android.util.Log.i("NyxAudio", if (on) "phone speaker muted: sound goes to the laptop" else "phone speaker back on")
            app.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean("muted", on).apply()
        } catch (e: Exception) {}
    }

    /** Tells the laptop that what's playing can't be shared (so it plays on the phone). */
    private fun note(blocked: Boolean) {
        android.util.Log.i("NyxAudio", if (blocked) "playing app blocks capture: sound stays on the phone" else "capture works again")
        LinkService.instance?.send(JSONObject().put("t", "audio_note").put("blocked", blocked))
    }

    fun stop() {
        running = false
        if (current === this) current = null
        quietPhone(false)
        try { recorder?.stop() } catch (e: Exception) {}
        try { recorder?.release() } catch (e: Exception) {}
        recorder = null
        try { server?.close() } catch (e: Exception) {}
        try { socket?.close() } catch (e: Exception) {}
    }
}
