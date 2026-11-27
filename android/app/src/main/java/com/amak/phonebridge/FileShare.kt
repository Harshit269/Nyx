package com.amak.phonebridge

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.provider.OpenableColumns
import org.json.JSONObject
import java.io.File
import java.io.InputStream
import java.io.OutputStream
import java.security.MessageDigest

/**
 * Files between phone and laptop, over the same encrypted link as everything else:
 *   file_start {id, name, size}  ->  file_part {id, seq, data} ...  ->  file_end {id, sha256}
 * and the receiver answers file_done {id, ok, msg}. A file is kept only when its size and
 * SHA-256 match. Received files land in Downloads/Nyx and are never opened automatically.
 */
object FileShare {
    const val CHUNK = 96 * 1024
    const val MAX_SIZE = 4L * 1024 * 1024 * 1024   // 4 GB
    private const val CHANNEL = "files"

    // ------------------------------------------------------------------ names

    /** A safe file name: no folders, no control or reserved characters, sensible length. */
    fun safeName(raw: String): String {
        var n = raw.substringAfterLast('/').substringAfterLast('\\')
        n = n.filter { it >= ' ' && it !in "<>:\"|?*" }.trim().trimEnd('.', ' ')
        if (n.isEmpty() || n == "." || n == "..") n = "file"
        if (n.length > 120) {
            val dot = n.lastIndexOf('.')
            val ext = if (dot > 0 && n.length - dot <= 12) n.substring(dot) else ""
            n = n.take(120 - ext.length) + ext
        }
        return n
    }

    // ---------------------------------------------------------------- sending

    class Outgoing(val name: String, val size: Long, val input: InputStream)

    /** Opens a picked or shared file while we still have permission to read it. */
    fun open(c: Context, uri: Uri): Outgoing? {
        return try {
            var name = "file"
            var size = -1L
            c.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { cur ->
                if (cur.moveToFirst()) {
                    cur.getString(0)?.let { name = it }
                    if (!cur.isNull(1)) size = cur.getLong(1)
                }
            }
            // some apps share links that don't report a name: use the end of the path instead
            if (name == "file") uri.lastPathSegment?.takeIf { it.isNotBlank() }?.let { name = it }
            val input = c.contentResolver.openInputStream(uri) ?: return null
            Outgoing(safeName(name), size, input)
        } catch (e: Exception) {
            null
        }
    }

    /**
     * Streams one file to the laptop. Blocks (run it on its own thread). [send] must write
     * synchronously so a big file never piles up in memory.
     */
    fun send(c: Context, f: Outgoing, laptopName: String, send: (JSONObject) -> Boolean): String? {
        val id = java.util.UUID.randomUUID().toString().take(12)
        val nm = c.getSystemService(NotificationManager::class.java)
        val notifId = id.hashCode()
        try {
            if (f.size > MAX_SIZE) return "That file is bigger than 4 GB."
            if (!send(JSONObject().put("t", "file_start").put("id", id).put("name", f.name).put("size", f.size)))
                return "The laptop isn't linked."
            val sha = MessageDigest.getInstance("SHA-256")
            val buf = ByteArray(CHUNK)
            var seq = 0
            var sent = 0L
            var lastShown = 0L
            f.input.use { input ->
                while (true) {
                    val n = readFully(input, buf)
                    if (n <= 0) break
                    sha.update(buf, 0, n)
                    val ok = send(JSONObject().put("t", "file_part").put("id", id).put("seq", seq++)
                        .put("data", LinkCipher.b64(if (n == buf.size) buf else buf.copyOf(n))))
                    if (!ok) { send(JSONObject().put("t", "file_cancel").put("id", id)); return "The link dropped while sending." }
                    sent += n
                    if (sent > MAX_SIZE) { send(JSONObject().put("t", "file_cancel").put("id", id)); return "That file is bigger than 4 GB." }
                    val now = System.currentTimeMillis()
                    if (now - lastShown > 400) { lastShown = now; progress(c, nm, notifId, f.name, laptopName, sent, f.size) }
                }
            }
            send(JSONObject().put("t", "file_end").put("id", id).put("size", sent).put("sha256", hex(sha.digest())))
            return null
        } catch (e: Exception) {
            send(JSONObject().put("t", "file_cancel").put("id", id))
            return "Couldn't read that file."
        } finally {
            try { nm.cancel(notifId) } catch (e: Exception) {}
        }
    }

    private fun readFully(input: InputStream, buf: ByteArray): Int {
        var total = 0
        while (total < buf.size) {
            val r = input.read(buf, total, buf.size - total)
            if (r < 0) break
            total += r
        }
        return total
    }

    // -------------------------------------------------------------- receiving

    private class Incoming(val name: String, val size: Long, val out: OutputStream, val uri: Uri?, val file: File?,
                           val sha: MessageDigest = MessageDigest.getInstance("SHA-256"), var got: Long = 0, var next: Int = 0)

    private val incoming = HashMap<String, Incoming>()

    /** Handles file_start / file_part / file_end / file_cancel from the laptop. Returns a reply, if any. */
    fun onMessage(c: Context, m: JSONObject): JSONObject? {
        val id = m.optString("id").take(40)
        if (id.isEmpty()) return null
        when (m.optString("t")) {
            "file_start" -> {
                abort(c, id)
                val size = m.optLong("size", -1)
                if (size > MAX_SIZE) return done(id, false, "The phone takes files up to 4 GB.")
                if (incoming.size >= 4) return done(id, false, "Too many files at once.")
                val name = safeName(m.optString("name"))
                return try {
                    incoming[id] = create(c, name, size)
                    null
                } catch (e: Exception) {
                    done(id, false, "The phone couldn't save it: " + (e.message ?: "no space?"))
                }
            }
            "file_part" -> {
                val f = incoming[id] ?: return null
                if (m.optInt("seq", -1) != f.next) { abort(c, id); return done(id, false, "Parts arrived out of order.") }
                val data = try { java.util.Base64.getDecoder().decode(m.optString("data")) } catch (e: Exception) { null }
                    ?: run { abort(c, id); return done(id, false, "A part was damaged.") }
                f.got += data.size
                if (f.got > MAX_SIZE || (f.size >= 0 && f.got > f.size)) { abort(c, id); return done(id, false, "The file was bigger than announced.") }
                f.sha.update(data)
                f.out.write(data)
                f.next++
                return null
            }
            "file_end" -> {
                val f = incoming.remove(id) ?: return null
                try { f.out.close() } catch (e: Exception) {}
                val sizeOk = f.got == m.optLong("size", -1) && (f.size < 0 || f.got == f.size)
                val shaOk = hex(f.sha.digest()).equals(m.optString("sha256"), ignoreCase = true)
                if (!sizeOk || !shaOk) {
                    discard(c, f)
                    return done(id, false, "The file didn't arrive intact, so it was thrown away. Try again.")
                }
                finish(c, f)
                received(c, f)
                return done(id, true, "Saved on the phone in Downloads/Nyx.")
            }
            "file_cancel" -> abort(c, id)
        }
        return null
    }

    /** Drops every half-received file, e.g. when the link ends. */
    fun abortAll(c: Context) { incoming.keys.toList().forEach { abort(c, it) } }

    private fun abort(c: Context, id: String) {
        val f = incoming.remove(id) ?: return
        try { f.out.close() } catch (e: Exception) {}
        discard(c, f)
    }

    private fun done(id: String, ok: Boolean, msg: String) =
        JSONObject().put("t", "file_done").put("id", id).put("ok", ok).put("msg", msg)

    private fun create(c: Context, name: String, size: Long): Incoming {
        if (Build.VERSION.SDK_INT >= 29) {
            val v = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, name)
                put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/Nyx")
                put(MediaStore.Downloads.IS_PENDING, 1)
            }
            val uri = c.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, v)
                ?: throw java.io.IOException("no storage")
            val out = c.contentResolver.openOutputStream(uri) ?: throw java.io.IOException("no storage")
            return Incoming(name, size, out.buffered(256 * 1024), uri, null)
        }
        // Android 8-9: Nyx's own Downloads folder (no storage permission needed)
        val dir = File(c.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS), "Nyx").apply { mkdirs() }
        var file = File(dir, name)
        var i = 2
        while (file.exists()) { file = File(dir, name.substringBeforeLast('.') + " ($i)" + (if ('.' in name) "." + name.substringAfterLast('.') else "")); i++ }
        return Incoming(name, size, file.outputStream().buffered(256 * 1024), null, file)
    }

    private fun finish(c: Context, f: Incoming) {
        if (f.uri != null && Build.VERSION.SDK_INT >= 29)
            c.contentResolver.update(f.uri, ContentValues().apply { put(MediaStore.Downloads.IS_PENDING, 0) }, null, null)
    }

    private fun discard(c: Context, f: Incoming) {
        try { if (f.uri != null) c.contentResolver.delete(f.uri, null, null) else f.file?.delete() } catch (e: Exception) {}
    }

    // ---------------------------------------------------------- notifications

    private fun channel(nm: NotificationManager) =
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Files", NotificationManager.IMPORTANCE_DEFAULT))

    private fun progress(c: Context, nm: NotificationManager, id: Int, name: String, to: String, sent: Long, size: Long) {
        try {
            channel(nm)
            val pct = if (size > 0) (sent * 100 / size).toInt().coerceIn(0, 100) else 0
            nm.notify(id, Notification.Builder(c, CHANNEL)
                .setSmallIcon(R.drawable.ic_stat_nyx)
                .setContentTitle("Sending $name")
                .setContentText("To $to")
                .setProgress(100, pct, size <= 0)
                .setOnlyAlertOnce(true)
                .setOngoing(true)
                .build())
        } catch (e: Exception) {}
    }

    private fun received(c: Context, f: Incoming) {
        try {
            val nm = c.getSystemService(NotificationManager::class.java)
            channel(nm)
            val b = Notification.Builder(c, CHANNEL)
                .setSmallIcon(R.drawable.ic_stat_nyx)
                .setContentTitle("Received ${f.name}")
                .setContentText("From your laptop · Downloads/Nyx")
                .setAutoCancel(true)
            if (f.uri != null) {
                val type = c.contentResolver.getType(f.uri) ?: "*/*"
                val view = Intent(Intent.ACTION_VIEW).setDataAndType(f.uri, type)
                    .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
                b.setContentIntent(PendingIntent.getActivity(c, f.name.hashCode(), Intent.createChooser(view, f.name)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT))
            }
            nm.notify(f.name.hashCode(), b.build())
        } catch (e: Exception) {}
    }

    /** The laptop's answer to a file the phone sent. */
    fun sentResult(c: Context, name: String, ok: Boolean, msg: String) {
        try {
            val nm = c.getSystemService(NotificationManager::class.java)
            channel(nm)
            nm.notify(("done" + name).hashCode(), Notification.Builder(c, CHANNEL)
                .setSmallIcon(R.drawable.ic_stat_nyx)
                .setContentTitle(if (ok) "Sent $name" else "Couldn't send $name")
                .setContentText(msg)
                .setAutoCancel(true)
                .build())
        } catch (e: Exception) {}
    }

    fun hex(b: ByteArray) = b.joinToString("") { "%02x".format(it) }
}
