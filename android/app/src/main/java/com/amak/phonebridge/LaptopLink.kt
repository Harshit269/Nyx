package com.amak.phonebridge

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.io.InputStream
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/** One open connection to a laptop, over Bluetooth or Wi-Fi. */
class Link(
    val input: InputStream,
    val output: OutputStream,
    val close: () -> Unit,
    val cipher: LinkCipher?,
    val kind: String,          // "Bluetooth" or "Wi-Fi"
    val laptopId: String = "", // known up front on Wi-Fi (the handshake names it)
    val peer: String = "",     // Bluetooth address of the laptop, for the approval check
    val peerName: String = ""
)

/**
 * Laptops this phone is paired with. A pairing arrives once over Bluetooth: the laptop
 * sends its id, name and a random 32-byte secret. Stored in the app's private prefs.
 */
object Pairing {
    private const val PREFS = "nyx"

    private fun prefs(c: Context) = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    /** This phone's stable Nyx id, made on first use. */
    fun phoneId(c: Context): String {
        val p = prefs(c)
        p.getString("phone_id", null)?.let { return it }
        val bytes = ByteArray(8).also { SecureRandom().nextBytes(it) }
        val id = bytes.joinToString("") { "%02x".format(it) }
        p.edit().putString("phone_id", id).apply()
        return id
    }

    private fun all(c: Context): JSONObject =
        try { JSONObject(prefs(c).getString("laptops", "{}")!!) } catch (e: Exception) { JSONObject() }

    private fun saveAll(c: Context, o: JSONObject) = prefs(c).edit().putString("laptops", o.toString()).apply()

    fun ids(c: Context): JSONArray = JSONArray().also { a -> all(c).keys().forEach { a.put(it) } }

    fun laptop(c: Context, id: String): JSONObject? = all(c).optJSONObject(id)

    /** The most recently used laptop, for the control screen. */
    fun current(c: Context): Pair<String, JSONObject>? {
        val o = all(c)
        val last = prefs(c).getString("last_laptop", null)
        if (last != null && o.has(last)) return last to o.getJSONObject(last)
        val k = o.keys().asSequence().firstOrNull() ?: return null
        return k to o.getJSONObject(k)
    }

    fun setCurrent(c: Context, id: String) = prefs(c).edit().putString("last_laptop", id).apply()

    fun pair(c: Context, id: String, name: String, secret: String) {
        val o = all(c)
        val entry = o.optJSONObject(id) ?: JSONObject()
        entry.put("name", name).put("secret", secret)
        o.put(id, entry)
        saveAll(c, o)
        setCurrent(c, id)
    }

    /** Bluetooth addresses of laptops the user allowed. Anything else must be approved first. */
    fun isApprovedBt(c: Context, address: String): Boolean =
        address.isNotEmpty() && (prefs(c).getStringSet("bt_ok", emptySet()) ?: emptySet()).contains(address)

    fun approveBt(c: Context, address: String) {
        val set = HashSet(prefs(c).getStringSet("bt_ok", emptySet()) ?: emptySet())
        set.add(address)
        prefs(c).edit().putStringSet("bt_ok", set).apply()
    }

    /** Forget a laptop entirely: its Wi-Fi secret and every Bluetooth approval. */
    fun forgetAll(c: Context) {
        prefs(c).edit().remove("laptops").remove("bt_ok").remove("last_laptop").apply()
    }

    /** Merge extra fields (wake info, cached power log, last seen) into a laptop's entry. */
    fun update(c: Context, id: String, fields: JSONObject) {
        val o = all(c)
        val entry = o.optJSONObject(id) ?: return
        fields.keys().forEach { entry.put(it, fields.get(it)) }
        o.put(id, entry)
        saveAll(c, o)
    }
}

/**
 * AES-256-GCM over each line, mirroring the desktop's LinkCipher exactly:
 * nonce = 4-byte direction (1 = to phone, 2 = to laptop) + 8-byte counter, big-endian;
 * a line is base64(ciphertext || 16-byte tag).
 */
class LinkCipher(key: ByteArray) {
    private val keySpec = SecretKeySpec(key, "AES")
    private var sendCounter = 0L
    private var recvCounter = 0L

    private fun nonce(dir: Int, counter: Long) = ByteBuffer.allocate(12).putInt(dir).putLong(counter).array()

    @Synchronized
    fun seal(plain: String): String {
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, keySpec, GCMParameterSpec(128, nonce(TO_LAPTOP, sendCounter++)))
        return b64(c.doFinal(plain.toByteArray(Charsets.UTF_8)))
    }

    /** Throws on a tampered, replayed or reordered line. */
    fun open(line: String): String {
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.DECRYPT_MODE, keySpec, GCMParameterSpec(128, nonce(TO_PHONE, recvCounter++)))
        return String(c.doFinal(unb64(line)), Charsets.UTF_8)
    }

    companion object {
        const val TO_PHONE = 1
        const val TO_LAPTOP = 2

        fun mac(secret: ByteArray, label: String, a: ByteArray, b: ByteArray): ByteArray {
            val m = Mac.getInstance("HmacSHA256")
            m.init(SecretKeySpec(secret, "HmacSHA256"))
            m.update(label[0].code.toByte())
            m.update(a)
            m.update(b)
            return m.doFinal()
        }

        fun same(a: ByteArray, b: ByteArray) = MessageDigest.isEqual(a, b)

        fun random(n: Int) = ByteArray(n).also { SecureRandom().nextBytes(it) }

        // java.util.Base64 (API 26+) rather than android.util.Base64, so this class runs in
        // plain JVM unit tests too
        fun b64(b: ByteArray): String = java.util.Base64.getEncoder().encodeToString(b)

        fun unb64(s: String): ByteArray = java.util.Base64.getDecoder().decode(s)
    }
}

/** Reads '\n'-terminated UTF-8 lines straight from a stream, with no read-ahead buffer. */
class LineIn(private val s: InputStream) {
    private val buf = java.io.ByteArrayOutputStream()

    /** Longest line accepted. Kept small while a connection is still unverified. */
    var maxLen = 4 * 1024 * 1024

    fun readLine(): String? {
        buf.reset()
        while (true) {
            val b = s.read()
            if (b < 0) return if (buf.size() == 0) null else buf.toString("UTF-8")
            if (b == '\n'.code) return buf.toString("UTF-8").trimEnd('\r')
            if (buf.size() > maxLen) throw java.io.IOException("line too long")
            buf.write(b)
        }
    }
}

/** Wake-on-LAN: the standard magic packet (6 x 0xFF, then the MAC 16 times) to UDP port 9. */
object WakeOnLan {
    private val MAC = Regex("^[0-9A-Fa-f]{2}([:-][0-9A-Fa-f]{2}){5}$")
    private val IPV4 = Regex("^\\d{1,3}(\\.\\d{1,3}){3}$")

    /** The magic packet for one MAC like "0A:1B:2C:3D:4E:5F", or null if it isn't one. */
    fun packet(mac: String): ByteArray? {
        if (!MAC.matches(mac)) return null
        val bytes = mac.split(':', '-').map { it.toInt(16).toByte() }.toByteArray()
        val packet = ByteArray(6 + 16 * 6)
        for (j in 0 until 6) packet[j] = 0xFF.toByte()
        for (r in 0 until 16) System.arraycopy(bytes, 0, packet, 6 + r * 6, 6)
        return packet
    }

    /**
     * Only plain IPv4 addresses are used as targets: the list comes from the laptop, and a
     * host name here would make the phone go and look it up.
     */
    fun targets(broadcasts: JSONArray): List<String> =
        ((0 until broadcasts.length()).map { broadcasts.optString(it) } + "255.255.255.255")
            .filter { IPV4.matches(it) && it.split('.').all { p -> p.toInt() <= 255 } }
            .distinct()

    fun send(macs: JSONArray, broadcasts: JSONArray, ports: IntArray = intArrayOf(9, 7)): Int {
        var sent = 0
        DatagramSocket().use { sock ->
            sock.broadcast = true
            val targets = targets(broadcasts)
            for (i in 0 until macs.length()) {
                val packet = packet(macs.optString(i)) ?: continue // a bad entry is skipped, not fatal
                for (t in targets) {
                    for (port in ports) {
                        try {
                            sock.send(DatagramPacket(packet, packet.size, InetAddress.getByName(t), port))
                            sent++
                        } catch (e: Exception) {
                        }
                    }
                }
            }
        }
        return sent
    }
}

/**
 * Wi-Fi pairing crypto (mirrors the desktop's WifiPairing): P-256 ECDH, a shared secret and
 * a 6-digit comparison code, both derived from the agreed value and both public keys.
 * Public keys travel as 65 bytes: 0x04, X (32), Y (32).
 */
object PairCrypto {
    private val params by lazy {
        val g = java.security.KeyPairGenerator.getInstance("EC")
        g.initialize(java.security.spec.ECGenParameterSpec("secp256r1"))
        (g.generateKeyPair().public as java.security.interfaces.ECPublicKey).params
    }

    fun newKeyPair(): java.security.KeyPair {
        val g = java.security.KeyPairGenerator.getInstance("EC")
        g.initialize(java.security.spec.ECGenParameterSpec("secp256r1"))
        return g.generateKeyPair()
    }

    private fun fixed32(b: java.math.BigInteger): ByteArray {
        val raw = b.toByteArray()
        val out = ByteArray(32)
        val n = minOf(raw.size, 32)
        System.arraycopy(raw, raw.size - n, out, 32 - n, n)
        return out
    }

    fun publicBytes(kp: java.security.KeyPair): ByteArray {
        val w = (kp.public as java.security.interfaces.ECPublicKey).w
        return byteArrayOf(4) + fixed32(w.affineX) + fixed32(w.affineY)
    }

    /** The raw shared X coordinate. Throws for anything that isn't a valid P-256 point. */
    fun agree(kp: java.security.KeyPair, peer: ByteArray): ByteArray {
        require(peer.size == 65 && peer[0] == 4.toByte()) { "bad public key" }
        val x = java.math.BigInteger(1, peer.copyOfRange(1, 33))
        val y = java.math.BigInteger(1, peer.copyOfRange(33, 65))
        val pub = java.security.KeyFactory.getInstance("EC")
            .generatePublic(java.security.spec.ECPublicKeySpec(java.security.spec.ECPoint(x, y), params))
        val ka = javax.crypto.KeyAgreement.getInstance("ECDH")
        ka.init(kp.private)
        ka.doPhase(pub, true)
        return ka.generateSecret()
    }

    fun sha256(vararg parts: ByteArray): ByteArray {
        val md = java.security.MessageDigest.getInstance("SHA-256")
        parts.forEach { md.update(it) }
        return md.digest()
    }

    fun secret(z: ByteArray, pubPhone: ByteArray, pubLaptop: ByteArray): ByteArray =
        sha256("nyx pair v1".toByteArray(), z, pubPhone, pubLaptop)

    /** Six digits both people compare, like Bluetooth's numeric comparison. */
    fun code(z: ByteArray, pubPhone: ByteArray, pubLaptop: ByteArray): String {
        val h = sha256("nyx code v1".toByteArray(), z, pubPhone, pubLaptop)
        val n = ((h[0].toLong() and 0xFF) shl 24) or ((h[1].toLong() and 0xFF) shl 16) or
                ((h[2].toLong() and 0xFF) shl 8) or (h[3].toLong() and 0xFF)
        return String.format("%06d", n % 1_000_000)
    }
}
