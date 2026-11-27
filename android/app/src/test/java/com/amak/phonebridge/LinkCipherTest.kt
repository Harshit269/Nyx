package com.amak.phonebridge

import org.json.JSONArray
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.fail
import org.junit.Test
import java.io.ByteArrayInputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

/**
 * The same known answers as the desktop tests (made independently with Python's
 * "cryptography"), so phone, laptop and reference agree byte for byte.
 */
class LinkCipherTest {
    private val secret = LinkCipher.unb64("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=")
    private val nPhone = LinkCipher.unb64("oKGio6SlpqeoqaqrrK2urw==")
    private val nLaptop = LinkCipher.unb64("UFFSU1RVVldYWVpbXF1eXw==")
    private val key = "sSb0CLdzQLjlR/ZdKLaRKkVlgBTqQ2w8Og/MW5a6E6Y="

    @Test fun macsAndKeyMatchKnownAnswers() {
        assertEquals("J7aVjZQSbaEMSC8a5qeqzIbx3pec63n7GcAYVU+1f44=", LinkCipher.b64(LinkCipher.mac(secret, "L", nPhone, nLaptop)))
        assertEquals("4uqfaFkAFqzbNblOXk2/nYxxQU0Vll2NT6Pg03ek204=", LinkCipher.b64(LinkCipher.mac(secret, "P", nLaptop, nPhone)))
        assertEquals(key, LinkCipher.b64(LinkCipher.mac(secret, "K", nPhone, nLaptop)))
    }

    @Test fun phoneSealsExactlyLikeTheReference() {
        val c = LinkCipher(LinkCipher.unb64(key))
        assertEquals("5xtl24K9N/iqaoG9L7W+M8CSQfamm4tFHl1DkwnoqS4fjEDRuwnivwCC", c.seal("{\"t\":\"hello\",\"phone\":\"p1\"}"))
    }

    @Test fun phoneOpensReferenceLaptopFramesInOrder() {
        val c = LinkCipher(LinkCipher.unb64(key))
        assertEquals("{\"t\":\"ping\"}", c.open("6iJptetzGc5g1ne9j3s0+ruY/rtOsfbH8PCllw=="))
        assertEquals("{\"t\":\"get_sms\",\"limit\":400}", c.open("j+RykuK9CZ69Yz7re9sS/44a+l57W7lCD0wLciFFDmHptn2ipzk+grKzOQ=="))
    }

    private fun mustFail(block: () -> Unit) {
        try { block(); fail("should have been rejected") } catch (e: AssertionError) { throw e } catch (e: Exception) { }
    }

    @Test fun replayReorderTamperAndJunkAreRejected() {
        val frame0 = "6iJptetzGc5g1ne9j3s0+ruY/rtOsfbH8PCllw=="
        val frame1 = "j+RykuK9CZ69Yz7re9sS/44a+l57W7lCD0wLciFFDmHptn2ipzk+grKzOQ=="
        // reorder
        mustFail { LinkCipher(LinkCipher.unb64(key)).open(frame1) }
        // replay
        val c = LinkCipher(LinkCipher.unb64(key))
        c.open(frame0)
        mustFail { c.open(frame0) }
        // every single bit flipped
        val bytes = LinkCipher.unb64(frame0)
        for (i in 0 until bytes.size * 8) {
            val bad = bytes.copyOf()
            bad[i / 8] = (bad[i / 8].toInt() xor (1 shl (i % 8))).toByte()
            mustFail { LinkCipher(LinkCipher.unb64(key)).open(LinkCipher.b64(bad)) }
        }
        // wrong key, junk, truncated
        mustFail { LinkCipher(ByteArray(32)).open(frame0) }
        mustFail { LinkCipher(LinkCipher.unb64(key)).open("not base64 !!") }
        mustFail { LinkCipher(LinkCipher.unb64(key)).open("AAAA") }
    }

    @Test fun ownFramesCannotBeReflectedBack() {
        // the phone must not accept a frame it sent itself (different direction nonce)
        val k = LinkCipher.random(32)
        val sent = LinkCipher(k).seal("{\"t\":\"pc\",\"action\":\"shutdown\"}")
        mustFail { LinkCipher(k).open(sent) }
    }

    @Test fun constantTimeCompareAndRandomness() {
        assertEquals(true, LinkCipher.same(byteArrayOf(1, 2, 3), byteArrayOf(1, 2, 3)))
        assertEquals(false, LinkCipher.same(byteArrayOf(1, 2, 3), byteArrayOf(1, 2, 4)))
        assertNotEquals(LinkCipher.b64(LinkCipher.random(16)), LinkCipher.b64(LinkCipher.random(16)))
    }
}

class LineInTest {
    private fun lines(text: String): List<String> {
        val r = LineIn(ByteArrayInputStream(text.toByteArray(Charsets.UTF_8)))
        return generateSequence { r.readLine() }.toList()
    }

    @Test fun splitsLinesAndHandlesCrlfUnicodeAndNoTrailingNewline() {
        assertEquals(listOf("a", "b", "", "नमस्ते ₹450 😀", "tail"), lines("a\nb\r\n\nनमस्ते ₹450 😀\ntail"))
        assertNull(LineIn(ByteArrayInputStream(ByteArray(0))).readLine())
    }

    @Test fun aSmallLimitHoldsDuringTheHandshake() {
        val r = LineIn(ByteArrayInputStream(("x".repeat(20_000) + "\n").toByteArray()))
        r.maxLen = 16 * 1024
        try { r.readLine(); fail("should have refused") } catch (e: java.io.IOException) { }
        val ok = LineIn(ByteArrayInputStream("{\"t\":\"nyx_auth\"}\n".toByteArray()))
        ok.maxLen = 16 * 1024
        assertEquals("{\"t\":\"nyx_auth\"}", ok.readLine())
    }

    @Test fun refusesALineOverFourMegabytes() {
        val r = LineIn(ByteArrayInputStream(ByteArray(5 * 1024 * 1024) { 'A'.code.toByte() }))
        try { r.readLine(); fail("should have refused") } catch (e: java.io.IOException) { }
    }
}

class WakeOnLanTest {
    @Test fun buildsAStandardMagicPacket() {
        val p = WakeOnLan.packet("0A:1B:2C:3D:4E:5F")!!
        assertEquals(102, p.size)
        for (i in 0 until 6) assertEquals(0xFF.toByte(), p[i])
        val m = byteArrayOf(0x0A, 0x1B, 0x2C, 0x3D, 0x4E, 0x5F)
        for (r in 0 until 16) assertArrayEquals(m, p.copyOfRange(6 + r * 6, 12 + r * 6))
        assertArrayEquals(p, WakeOnLan.packet("0a-1b-2c-3d-4e-5f"))
    }

    @Test fun rejectsAnythingThatIsNotAMac() {
        for (bad in listOf("", "zz:zz", "01:02:03", "0A:1B:2C:3D:4E:5F:60", "0A1B2C3D4E5F", "0A:1B:2C:3D:4E:5G"))
            assertNull(bad, WakeOnLan.packet(bad))
    }

    @Test fun onlyPlainIpv4TargetsAreUsed() {
        val t = WakeOnLan.targets(JSONArray().put("192.168.1.255").put("evil.example.com").put("999.1.1.1").put("10.0.0.255 extra"))
        assertEquals(listOf("192.168.1.255", "255.255.255.255"), t)
    }

    @Test fun reallySendsTheRightBytesOnTheWire() {
        DatagramSocket(0, InetAddress.getByName("127.0.0.1")).use { rx ->
            rx.soTimeout = 3000
            val n = WakeOnLan.send(JSONArray().put("0A:1B:2C:3D:4E:5F").put("junk"), JSONArray().put("127.0.0.1"), intArrayOf(rx.localPort))
            assertEquals(true, n >= 1)
            val buf = ByteArray(200)
            val p = DatagramPacket(buf, buf.size)
            rx.receive(p)
            assertArrayEquals(WakeOnLan.packet("0A:1B:2C:3D:4E:5F"), buf.copyOf(p.length))
        }
    }
}
