package com.amak.phonebridge

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.fail
import org.junit.Test
import java.math.BigInteger
import java.security.AlgorithmParameters
import java.security.KeyFactory
import java.security.KeyPair
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPrivateKeySpec
import java.security.spec.ECPublicKeySpec

/** Same known answers as the desktop PairingTests (made with Python's "cryptography"). */
class PairCryptoTest {
    private val pubPhone = LinkCipher.unb64("BHQJW89MN0HtxsmNKaCkh1Hl7IDwuf2L0QcHbvkREp+qqhWIlfytCdHt7YR6JBmkyZ5miPOkGV20jlHHI7qvn2k=")
    private val pubLaptop = LinkCipher.unb64("BFKBEjGRoZnabJNfZFU+wloDfTKap0uXWZk0zVFzOSV326PdHDiNb1X3zegV0+GX/aZSe6iP1qWsUJ0gLDpmi3E=")

    private fun phoneKeys(): KeyPair {
        val ap = AlgorithmParameters.getInstance("EC")
        ap.init(ECGenParameterSpec("secp256r1"))
        val spec = ap.getParameterSpec(ECParameterSpec::class.java)
        val kf = KeyFactory.getInstance("EC")
        val d = BigInteger(1, LinkCipher.unb64("CTjq8nlFexnyP4p7VUA/1DJ72EYm7UcOYOXIHye29B4="))
        val pub = kf.generatePublic(ECPublicKeySpec(ECPoint(BigInteger(1, pubPhone.copyOfRange(1, 33)), BigInteger(1, pubPhone.copyOfRange(33, 65))), spec))
        return KeyPair(pub, kf.generatePrivate(ECPrivateKeySpec(d, spec)))
    }

    @Test fun agreementSecretAndCodeMatchTheReference() {
        val kp = phoneKeys()
        assertArrayEquals(pubPhone, PairCrypto.publicBytes(kp))
        val z = PairCrypto.agree(kp, pubLaptop)
        assertEquals("2nSyhcOA/VIZOgMNCdsrGYx5IzYdXgV/Sb56UFQvZ9U=", LinkCipher.b64(z))
        assertEquals("lGw+Bv709sEX+RCyp/S/iuwavE3Oy0pCvQZ2ymkIS6A=", LinkCipher.b64(PairCrypto.secret(z, pubPhone, pubLaptop)))
        assertEquals("055367", PairCrypto.code(z, pubPhone, pubLaptop))
        assertEquals("AxnIZJi8eRfYCP9Cz8igQcwfNg4SfOJ+51Tf8aDT8Mg=", LinkCipher.b64(PairCrypto.sha256(pubLaptop)))
    }

    @Test fun twoFreshKeyPairsAgreeAndCodesAreSixDigits() {
        val a = PairCrypto.newKeyPair()
        val b = PairCrypto.newKeyPair()
        val pa = PairCrypto.publicBytes(a)
        val pb = PairCrypto.publicBytes(b)
        assertEquals(65, pa.size)
        assertArrayEquals(PairCrypto.agree(a, pb), PairCrypto.agree(b, pa))
        val code = PairCrypto.code(PairCrypto.agree(a, pb), pa, pb)
        assertEquals(true, Regex("^[0-9]{6}$").matches(code))
    }

    @Test fun badKeysAreRefused() {
        val kp = PairCrypto.newKeyPair()
        val offCurve = ByteArray(65).also { it[0] = 4; it[1] = 1 }
        for (bad in listOf(ByteArray(0), byteArrayOf(4, 1, 2), offCurve, ByteArray(65))) {
            try { PairCrypto.agree(kp, bad); fail("accepted a bad key") } catch (e: AssertionError) { throw e } catch (e: Exception) { }
        }
    }
}
