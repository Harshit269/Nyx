package com.amak.phonebridge

import android.app.WallpaperManager
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Build
import java.io.ByteArrayOutputStream
import java.io.File

/**
 * The picture on the laptop's phone card. Android blocks apps from reading the
 * wallpaper, so the user can pick the same image in Nyx (photo picker, no permission).
 * Until a picture is chosen, the card shows the wallpaper's main colour instead.
 */
object Wallpaper {
    private fun file(c: Context) = File(c.filesDir, "card.jpg")

    fun hasCustom(c: Context) = file(c).exists()

    /**
     * Whether Android lets Nyx read the real wallpaper: that takes "All files access", which
     * you can choose to give Nyx in the app (it's only used for this).
     */
    fun canReadWallpaper(): Boolean =
        Build.VERSION.SDK_INT >= 30 && android.os.Environment.isExternalStorageManager()

    /** Why there's no picture to send: "" when there is one. */
    var lastWhy: String = ""
        private set

    /** A picture you chose wins; otherwise the real wallpaper, when Android allows it. */
    @android.annotation.SuppressLint("MissingPermission") // guarded by canReadWallpaper()
    fun picture(c: Context): Bitmap? {
        lastWhy = ""
        val f = file(c)
        if (f.exists()) BitmapFactory.decodeFile(f.path)?.let { return it }
        if (!canReadWallpaper()) { lastWhy = "no_access"; return null }
        val wm = WallpaperManager.getInstance(c)
        if (wm.wallpaperInfo != null) { lastWhy = "live"; return null }
        // the drawable first; some phones only hand out the file, so try that too
        try {
            wm.drawable?.let { d ->
                val w = d.intrinsicWidth.coerceIn(1, 4096)
                val h = d.intrinsicHeight.coerceIn(1, 4096)
                val bmp = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
                d.setBounds(0, 0, w, h)
                d.draw(android.graphics.Canvas(bmp))
                return bmp
            }
        } catch (e: Exception) {
            lastWhy = "error: " + (e.message ?: e.javaClass.simpleName)
        }
        for (which in intArrayOf(WallpaperManager.FLAG_SYSTEM, WallpaperManager.FLAG_LOCK)) {
            try {
                wm.getWallpaperFile(which)?.use { pfd ->
                    val opts = BitmapFactory.Options().apply { inSampleSize = 2 }
                    BitmapFactory.decodeFileDescriptor(pfd.fileDescriptor, null, opts)?.let { lastWhy = ""; return it }
                }
            } catch (e: Exception) {
                lastWhy = "error: " + (e.message ?: e.javaClass.simpleName)
            }
        }
        if (lastWhy.isEmpty()) lastWhy = "unreadable"
        return null
    }

    /** The wallpaper's main colour as #RRGGBB. Any app may read this. */
    fun mainColor(c: Context): String? {
        if (Build.VERSION.SDK_INT < 27) return null
        return try {
            val argb = WallpaperManager.getInstance(c).getWallpaperColors(WallpaperManager.FLAG_SYSTEM)
                ?.primaryColor?.toArgb() ?: return null
            String.format("#%06X", argb and 0xFFFFFF)
        } catch (e: Exception) {
            null
        }
    }

    /** Small enough to travel quickly (a phone-shaped 270 x 585 crop at most), as base64 JPEG. */
    fun toJpegBase64(src: Bitmap): String {
        val targetAspect = 9.0 / 19.5
        var w = src.width
        var h = src.height
        var x = 0
        var y = 0
        if (w.toDouble() / h > targetAspect) { val nw = (h * targetAspect).toInt(); x = (w - nw) / 2; w = nw }
        else { val nh = (w / targetAspect).toInt(); y = (h - nh) / 2; h = nh }
        val cropped = Bitmap.createBitmap(src, x, y, w.coerceAtLeast(1), h.coerceAtLeast(1))
        val scaled = Bitmap.createScaledBitmap(cropped, 270, 585, true)
        val out = ByteArrayOutputStream()
        scaled.compress(Bitmap.CompressFormat.JPEG, 82, out)
        return LinkCipher.b64(out.toByteArray())
    }

    /** Save a picture chosen in the photo picker, downsized, as the card picture. */
    fun savePicked(c: Context, uri: Uri): Boolean {
        return try {
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            c.contentResolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, bounds) }
            if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return false
            var sample = 1
            while (bounds.outHeight / (sample * 2) >= 1200) sample *= 2
            val bmp = c.contentResolver.openInputStream(uri)?.use {
                BitmapFactory.decodeStream(it, null, BitmapFactory.Options().apply { inSampleSize = sample })
            } ?: return false
            file(c).outputStream().use { bmp.compress(Bitmap.CompressFormat.JPEG, 88, it) }
            true
        } catch (e: Exception) {
            false
        }
    }

    fun clear(c: Context) { file(c).delete() }
}
