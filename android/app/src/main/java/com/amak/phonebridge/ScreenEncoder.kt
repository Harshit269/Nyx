package com.amak.phonebridge

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Build
import android.view.Surface
import java.util.concurrent.ConcurrentLinkedQueue

/**
 * The phone's hardware video encoder (H.264), fed straight from the mirrored screen: far
 * smoother and lighter on Wi-Fi than a JPEG per frame, like scrcpy. Only what changed is sent,
 * and a still screen costs almost nothing. When the phone turns, a new encoder at the new size
 * takes over (its first frame carries the new picture size, so the laptop follows).
 */
class ScreenEncoder {

    class Made(val codec: MediaCodec, val surface: Surface, val w: Int, val h: Int)

    companion object {
        private const val MIME = "video/avc"
        /** About 6 bits per pixel-second at 60 fps; more pictures a second get a little more. */
        fun bitrate(w: Int, h: Int, fps: Int) = (w.toLong() * h * Math.min(fps, 90) / 9).toInt().coerceIn(4_000_000, 16_000_000)

        /** The phone's size scaled so its long side is at most [side], in steps of 16 (what video wants). */
        fun sizeFor(realW: Int, realH: Int, side: Int): Pair<Int, Int> {
            val scale = Math.min(1.0, side.toDouble() / Math.max(realW, realH))
            fun fit(v: Int) = Math.max(16, (Math.round(v * scale / 16.0) * 16).toInt())
            return fit(realW) to fit(realH)
        }

        /** An encoder ready for frames, trying smaller sizes if this phone's encoder can't do the big one. */
        fun make(realW: Int, realH: Int, longSide: Int, maxFps: Int = 60): Made? {
            for (side in intArrayOf(longSide, 1920, 1600, 1280).filter { it <= longSide }) {
                val (w, h) = sizeFor(realW, realH, side)
                // as many pictures a second as this encoder says it can manage at this size (up to what was asked)
                val fps = try {
                    val caps = android.media.MediaCodecList(android.media.MediaCodecList.REGULAR_CODECS).codecInfos
                        .firstOrNull { it.isEncoder && it.supportedTypes.any { t -> t.equals(MIME, true) } }
                        ?.getCapabilitiesForType(MIME)?.videoCapabilities
                    when {
                        caps == null -> 60
                        caps.areSizeAndRateSupported(w, h, maxFps.toDouble()) -> maxFps
                        caps.areSizeAndRateSupported(w, h, 90.0) && maxFps >= 90 -> 90
                        else -> 60
                    }
                } catch (e: Exception) { 60 }
                var codec: MediaCodec? = null
                try {
                    val fmt = MediaFormat.createVideoFormat(MIME, w, h).apply {
                        setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
                        setInteger(MediaFormat.KEY_BIT_RATE, bitrate(w, h, fps))
                        setInteger(MediaFormat.KEY_FRAME_RATE, fps)
                        setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 10)
                        // a still screen still sends a frame now and then, so the laptop's picture settles sharp
                        setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 100_000)
                        if (Build.VERSION.SDK_INT >= 29) setFloat(MediaFormat.KEY_MAX_FPS_TO_ENCODER, fps.toFloat())
                        setInteger(MediaFormat.KEY_COLOR_STANDARD, MediaFormat.COLOR_STANDARD_BT709)
                        setInteger(MediaFormat.KEY_COLOR_RANGE, MediaFormat.COLOR_RANGE_LIMITED)
                    }
                    codec = MediaCodec.createEncoderByType(MIME)
                    codec.configure(fmt, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
                    val surface = codec.createInputSurface()
                    codec.start()
                    android.util.Log.i("NyxMirror", "encoder ${w}x$h at $fps fps, ${bitrate(w, h, fps) / 1000} kbit/s")
                    return Made(codec, surface, w, h)
                } catch (e: Exception) {
                    try { codec?.release() } catch (x: Exception) {}
                }
            }
            return null
        }
    }

    @Volatile private var codec: MediaCodec? = null
    private val retired = ConcurrentLinkedQueue<MediaCodec>()

    /** Starts using [next]; the old encoder is let go by the sending thread (only it touches encoders). */
    fun swap(next: MediaCodec?) {
        val old = codec
        codec = next
        if (old != null) retired.add(old)
    }

    /**
     * Sends every encoded frame (blocking writes keep it in step with Wi-Fi: if the network is
     * slow, the encoder simply skips screen frames). The picture-size info goes in front of each
     * keyframe, so a keyframe alone can always be shown.
     */
    fun drain(alive: () -> Boolean, send: (key: Boolean, ptsUs: Long, data: ByteArray) -> Unit) {
        val info = MediaCodec.BufferInfo()
        var config: ByteArray? = null
        var from: MediaCodec? = null
        try {
            while (alive()) {
                releaseRetired()
                val c = codec ?: break
                if (c !== from) { from = c; config = null } // a new encoder sends its own size info first
                val i = try { c.dequeueOutputBuffer(info, 100_000) } catch (e: Exception) { continue }
                if (i < 0) continue
                val data: ByteArray? = try {
                    val buf = c.getOutputBuffer(i)
                    val bytes = if (buf == null) null else ByteArray(info.size).also { buf.position(info.offset); buf.get(it) }
                    c.releaseOutputBuffer(i, false)
                    bytes
                } catch (e: Exception) { null }
                if (data == null) continue
                if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) { config = data; continue }
                if (data.isEmpty()) continue
                val key = info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0
                val cfg = config
                send(key, info.presentationTimeUs, if (key && cfg != null) cfg + data else data)
            }
        } finally {
            swap(null)
            releaseRetired()
        }
    }

    /** Lets every encoder go. Call only from the sending thread, or once it has stopped. */
    fun releaseAll() { swap(null); releaseRetired() }

    private fun releaseRetired() {
        while (true) {
            val old = retired.poll() ?: return
            try { old.stop() } catch (e: Exception) {}
            try { old.release() } catch (e: Exception) {}
        }
    }
}
