package uk.co.reliablesolutions.glassesremote.companion

import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The phone's side of a glasses relay, driven by glasses made from the same primitives. */
class GlassesRelayTest {
    private val sent = mutableListOf<JSONObject>()
    private val prompts = mutableListOf<Pair<String, Int>>()
    private var closedPrompts = 0
    private var authorized: ByteArray? = null
    private var clock = 100_000L
    private val store = object : GlassesRelay.Store {
        override var glasses: PairedGlasses? = null
    }
    private val relay = GlassesRelay(store, { sent += it }, object : GlassesRelay.Listener {
        override fun onPairingRequest(code: String, attempt: Int) {
            prompts += code to attempt
        }

        override fun onPairingClosed() {
            closedPrompts++
        }

        override fun onAuthorized(sessionKey: ByteArray) {
            authorized = sessionKey
        }
    }, now = { clock })

    private fun last(type: String) = sent.last { it.getString("type") == type }

    private fun msg(type: String, vararg pairs: Pair<String, String>) =
        JSONObject().put("type", type).apply { pairs.forEach { (k, v) -> put(k, v) } }

    /** Glasses pairing: returns their pairing (id, key) and the code they'd show. */
    private fun pairAsGlasses(approve: Boolean = true): Pair<GlassesTrust.Paired, String> {
        val keys = GlassesTrust.newKeyPair()
        val raw = GlassesTrust.rawPublic(keys.public)
        relay.receive(msg("pairStart", "commit" to GlassesTrust.b64u(GlassesTrust.sha256(raw))))
        val phoneRaw = GlassesTrust.unb64u(last("pairKey").getString("key"))!!
        relay.receive(msg("pairReveal", "key" to GlassesTrust.b64u(raw)))
        val z = GlassesTrust.ecdh(keys.private, GlassesTrust.publicFromRaw(phoneRaw)!!)
        val ours = GlassesTrust.derivePairing(z, raw, phoneRaw)
        val (code, attempt) = prompts.last()
        relay.decide(approve, attempt)
        return ours to code
    }

    /** A session's handshake as the glasses do it; returns the session key they'd hold. */
    private fun helloAsGlasses(pairing: GlassesTrust.Paired, proveWrong: Boolean = false): ByteArray? {
        val nonce = GlassesTrust.nonce()
        relay.receive(msg("hello", "id" to pairing.id, "nonce" to GlassesTrust.b64u(nonce)))
        val challenge = sent.last()
        if (challenge.getString("type") != "challenge") return null
        val sk = GlassesTrust.sessionKey(pairing.key, nonce, GlassesTrust.unb64u(challenge.getString("nonce"))!!)
        assertTrue(GlassesTrust.matches(GlassesTrust.labelMac(sk, "phone"), challenge.getString("mac")))
        val proof = GlassesTrust.labelMac(if (proveWrong) ByteArray(32) else sk, "glasses")
        relay.receive(msg("proof", "mac" to GlassesTrust.b64u(proof)))
        return sk
    }

    @Test
    fun `pairs with the same code on both sides and keeps only what was approved`() {
        relay.open()
        val (ours, code) = pairAsGlasses()

        assertEquals(ours.code, code)
        assertEquals("paired", sent.last().getString("type"))
        assertEquals(ours.id, store.glasses!!.id)
        assertArrayEquals(ours.key, store.glasses!!.key)
        assertEquals(1, closedPrompts)
    }

    @Test
    fun `a rejected pairing keeps nothing`() {
        relay.open()
        pairAsGlasses(approve = false)
        assertEquals("pairFailed", sent.last().getString("type"))
        assertNull(store.glasses)
    }

    @Test
    fun `a revealed key that doesn't match the commitment is refused`() {
        relay.open()
        relay.receive(msg("pairStart", "commit" to GlassesTrust.b64u(ByteArray(32))))
        relay.receive(msg("pairReveal", "key" to GlassesTrust.b64u(GlassesTrust.rawPublic(GlassesTrust.newKeyPair().public))))
        assertEquals("pairFailed", sent.last().getString("type"))
        assertTrue(prompts.isEmpty())
    }

    @Test
    fun `paired glasses prove themselves and get the session key`() {
        relay.open()
        val (ours, _) = pairAsGlasses()
        val sk = helloAsGlasses(ours)
        assertNotNull(authorized)
        assertArrayEquals(sk, authorized)
    }

    @Test
    fun `unknown glasses and wrong proofs are refused`() {
        relay.open()
        val (ours, _) = pairAsGlasses()
        helloAsGlasses(GlassesTrust.Paired("000000", "AAAAAAAAAAAAAAAA", ours.key))
        assertEquals("authFailed", sent.last().getString("type"))

        helloAsGlasses(ours, proveWrong = true)
        assertEquals("authFailed", sent.last().getString("type"))
        assertNull(authorized)
    }

    @Test
    fun `nothing happens without an open relay, and closing drops a pending prompt`() {
        relay.receive(msg("pairStart", "commit" to GlassesTrust.b64u(ByteArray(32))))
        assertTrue(sent.isEmpty())

        relay.open()
        val keys = GlassesTrust.newKeyPair()
        val raw = GlassesTrust.rawPublic(keys.public)
        relay.receive(msg("pairStart", "commit" to GlassesTrust.b64u(GlassesTrust.sha256(raw))))
        relay.receive(msg("pairReveal", "key" to GlassesTrust.b64u(raw)))
        val attempt = prompts.last().second
        relay.close()
        assertEquals(1, closedPrompts)
        relay.decide(true, attempt)
        assertNull(store.glasses)
    }

    @Test
    fun `pairing prompts are spaced out`() {
        relay.open()
        pairAsGlasses(approve = false)
        relay.open()
        clock += 1_000
        relay.receive(msg("pairStart", "commit" to GlassesTrust.b64u(ByteArray(32))))
        assertEquals("pairFailed", sent.last().getString("type"))
        assertEquals(1, prompts.size)

        relay.open()
        clock += 10_000
        pairAsGlasses()
        assertEquals(2, prompts.size)
    }
}
