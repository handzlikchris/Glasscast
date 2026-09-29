package uk.co.reliablesolutions.glassesremote.companion

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.math.BigInteger
import java.security.AlgorithmParameters
import java.security.KeyFactory
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.security.spec.ECPrivateKeySpec

/**
 * The same vector as the glasses' client-web/src/phoneTrust.test.ts (made with Node's crypto):
 * the phone and the glasses must derive the same code, key, id and MACs.
 */
class GlassesTrustTest {
    private val dG = "ERERERERERERERERERERERERERERERERERERERERERE"
    private val dP = "IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI"
    private val gPub = "BAIX5hfwtkQ5KCePlpmeaaI6TywVK99tbN9m5bgCgtTtGUp968uXcS0t2jyoWqh2Wlb0X8dYWZZS8ol8ZTBuV5Q"
    private val pPub = "BNZak5d8qj0bCBhS_1ennkZfFmBXcwS66tUF3TpIWJzzUBheiVNy32Ih6joTdVfkc_3bZ1XwW9UHw8Uz_OnJEoU"
    private val z = "zPwmH1gZPJjKStSlO7rG8O4pvE1IQ4CQRGkIYiynmvY"
    private val commit = "K60P1hDZnq5EPpMqJhQryh5fqZW0UYRSgn547x8xf_A"
    private val key = "2rZ3D8DKYzvJGm7ap1t99fPIpg7gyA1oaCtLy81N734"
    private val gNonce = "MzMzMzMzMzMzMzMzMzMzMw"
    private val pNonce = "RERERERERERERERERERERA"
    private val sdp = "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\n"

    private fun bytes(b64u: String) = GlassesTrust.unb64u(b64u)!!

    private fun privateKey(d: String) = KeyFactory.getInstance("EC").generatePrivate(
        ECPrivateKeySpec(
            BigInteger(1, bytes(d)),
            AlgorithmParameters.getInstance("EC").apply { init(ECGenParameterSpec("secp256r1")) }
                .getParameterSpec(ECParameterSpec::class.java),
        ),
    )

    @Test
    fun `ECDH on the phone gives the glasses' secret`() {
        val glasses = GlassesTrust.publicFromRaw(bytes(gPub))!!
        assertArrayEquals(bytes(z), GlassesTrust.ecdh(privateKey(dP), glasses))
        assertEquals(gPub, GlassesTrust.b64u(GlassesTrust.rawPublic(glasses)))
        assertEquals(commit, GlassesTrust.b64u(GlassesTrust.sha256(bytes(gPub))))
    }

    @Test
    fun `derives the code, key and id the glasses derive`() {
        val paired = GlassesTrust.derivePairing(bytes(z), bytes(gPub), bytes(pPub))
        assertEquals("752138", paired.code)
        assertEquals("e7OWfrvMTctc7H2n", paired.id)
        assertEquals(key, GlassesTrust.b64u(paired.key))
    }

    @Test
    fun `session MACs match the glasses'`() {
        val sk = GlassesTrust.sessionKey(bytes(key), bytes(gNonce), bytes(pNonce))
        assertEquals("8kaJJpg5ZFDDEx2mYJXnoXEs6kX0KSCzmN2-G9JReeE", GlassesTrust.b64u(GlassesTrust.labelMac(sk, "phone")))
        assertTrue(GlassesTrust.matches(GlassesTrust.labelMac(sk, "glasses"), "iRMu34EQiaj6y2JJ7TeSteLdEFjFNmK3quM8-6HTYVk"))
        assertEquals("m1tV9PeYr9t7ILJdrnakF4xUwa5KzeWJkI43l5o_5hY", GlassesTrust.b64u(GlassesTrust.sdpMac(sk, "offer", sdp)))
        assertTrue(GlassesTrust.matches(GlassesTrust.sdpMac(sk, "answer", sdp), "2pwIizM2lmNqQdeOT61Hmp3zdDgucN3DnbSEz3DU-U8"))
        assertFalse(GlassesTrust.matches(GlassesTrust.sdpMac(sk, "answer", sdp + "a=x\r\n"), "2pwIizM2lmNqQdeOT61Hmp3zdDgucN3DnbSEz3DU-U8"))
    }

    @Test
    fun `refuses points that are not on the curve`() {
        val bad = bytes(gPub).clone().also { it[64] = (it[64] + 1).toByte() }
        assertNull(GlassesTrust.publicFromRaw(bad))
        assertNull(GlassesTrust.publicFromRaw(ByteArray(65)))
        assertNull(GlassesTrust.publicFromRaw(bytes(gPub).copyOf(64)))
        assertNotNull(GlassesTrust.publicFromRaw(bytes(pPub)))
    }

    @Test
    fun `fresh keys round-trip through their raw form`() {
        val pair = GlassesTrust.newKeyPair()
        val raw = GlassesTrust.rawPublic(pair.public)
        assertEquals(65, raw.size)
        assertEquals(GlassesTrust.b64u(raw), GlassesTrust.b64u(GlassesTrust.rawPublic(GlassesTrust.publicFromRaw(raw)!!)))
        assertNull(GlassesTrust.unb64u("not base64url!"))
    }
}
