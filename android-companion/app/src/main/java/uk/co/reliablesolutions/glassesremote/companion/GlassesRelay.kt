package uk.co.reliablesolutions.glassesremote.companion

import org.json.JSONObject
import java.security.KeyPair

/** The glasses this phone is paired with: the pairing id and the shared key (app-private storage). */
class PairedGlasses(val id: String, val key: ByteArray)

/**
 * The phone's side of one glasses relay (the PC only passes it along; architecture/phone-mode.md):
 * pairs new glasses (the same code on both screens, Approve here) and checks paired ones on every
 * session. When the glasses have proved they hold the key, [Listener.onAuthorized] hands over the
 * session key; the service then asks for the capture consent and signs its offer with it.
 *
 * Messages from the glasses arrive already checked by the PC's parser, and are checked again here:
 * anything out of order or malformed is ignored. One pairing attempt per relay, and pairing prompts
 * at most every [promptGapMs]. Everything runs on the service's thread.
 */
class GlassesRelay(
    private val store: Store,
    private val send: (JSONObject) -> Unit,
    private val listener: Listener,
    private val now: () -> Long = System::currentTimeMillis,
    private val promptGapMs: Long = 10_000,
) {
    interface Store {
        var glasses: PairedGlasses?
    }

    interface Listener {
        /** Show the code with Approve / Reject; answer with [decide] and this attempt number. */
        fun onPairingRequest(code: String, attempt: Int)

        /** The prompt is no longer needed (decided, relay gone). */
        fun onPairingClosed()

        fun onAuthorized(sessionKey: ByteArray)
    }

    private enum class State { CLOSED, OPEN, PAIR_KEY_SENT, PAIR_WAITING, CHALLENGED, AUTHORIZED }

    private var state = State.CLOSED
    private var commit: ByteArray? = null
    private var keys: KeyPair? = null
    private var pending: GlassesTrust.Paired? = null
    private var sessionKey: ByteArray? = null
    private var pairTried = false
    private var attempt = 0
    private var lastPromptAt = Long.MIN_VALUE / 2

    val isOpen: Boolean get() = state != State.CLOSED

    /** Glasses reached the phone through the PC. */
    fun open() {
        close()
        state = State.OPEN
    }

    /** The relay is gone (the glasses closed it, or the PC link dropped). */
    fun close() {
        if (state == State.PAIR_WAITING) listener.onPairingClosed()
        state = State.CLOSED
        commit = null
        keys = null
        pending = null
        sessionKey = null
        pairTried = false
    }

    fun receive(message: JSONObject) {
        when (message.opt("type")) {
            "pairStart" -> pairStart(message)
            "pairReveal" -> pairReveal(message)
            "hello" -> hello(message)
            "proof" -> proof(message)
        }
    }

    /** Approve or Reject on the phone (or the prompt timing out). A stale attempt is ignored. */
    fun decide(approved: Boolean, attempt: Int) {
        val paired = pending
        if (state != State.PAIR_WAITING || attempt != this.attempt || paired == null) return
        pending = null
        state = State.OPEN
        listener.onPairingClosed()
        if (approved) {
            store.glasses = PairedGlasses(paired.id, paired.key)
            send(JSONObject().put("type", "paired"))
        } else {
            send(JSONObject().put("type", "pairFailed"))
        }
    }

    private fun pairStart(message: JSONObject) {
        if (state != State.OPEN || pairTried) return
        val commitment = (message.opt("commit") as? String)?.let(GlassesTrust::unb64u)?.takeIf { it.size == 32 } ?: return
        if (now() - lastPromptAt < promptGapMs) {
            send(JSONObject().put("type", "pairFailed"))
            return
        }
        pairTried = true
        commit = commitment
        val pair = GlassesTrust.newKeyPair()
        keys = pair
        state = State.PAIR_KEY_SENT
        send(JSONObject().put("type", "pairKey").put("key", GlassesTrust.b64u(GlassesTrust.rawPublic(pair.public))))
    }

    private fun pairReveal(message: JSONObject) {
        if (state != State.PAIR_KEY_SENT) return
        val raw = (message.opt("key") as? String)?.let(GlassesTrust::unb64u)
        val glassesKey = raw?.let(GlassesTrust::publicFromRaw)
        val pair = keys
        // Their key must be the one they committed to before seeing ours.
        if (raw == null || glassesKey == null || pair == null || !GlassesTrust.sha256(raw).contentEquals(commit)) {
            state = State.OPEN
            send(JSONObject().put("type", "pairFailed"))
            return
        }
        val z = GlassesTrust.ecdh(pair.private, glassesKey)
        val paired = GlassesTrust.derivePairing(z, raw, GlassesTrust.rawPublic(pair.public))
        pending = paired
        keys = null
        state = State.PAIR_WAITING
        attempt++
        lastPromptAt = now()
        listener.onPairingRequest(paired.code, attempt)
    }

    private fun hello(message: JSONObject) {
        if (state != State.OPEN) return
        val glasses = store.glasses
        val id = message.opt("id") as? String
        val nonce = (message.opt("nonce") as? String)?.let(GlassesTrust::unb64u)?.takeIf { it.size == 16 }
        if (glasses == null || id != glasses.id || nonce == null) {
            send(JSONObject().put("type", "authFailed"))
            return
        }
        val phoneNonce = GlassesTrust.nonce()
        val sk = GlassesTrust.sessionKey(glasses.key, nonce, phoneNonce)
        sessionKey = sk
        state = State.CHALLENGED
        send(
            JSONObject().put("type", "challenge").put("nonce", GlassesTrust.b64u(phoneNonce))
                .put("mac", GlassesTrust.b64u(GlassesTrust.labelMac(sk, "phone"))),
        )
    }

    private fun proof(message: JSONObject) {
        val sk = sessionKey
        if (state != State.CHALLENGED || sk == null) return
        val mac = message.opt("mac") as? String
        if (mac == null || !GlassesTrust.matches(GlassesTrust.labelMac(sk, "glasses"), mac)) {
            state = State.OPEN
            sessionKey = null
            send(JSONObject().put("type", "authFailed"))
            return
        }
        state = State.AUTHORIZED
        listener.onAuthorized(sk)
    }
}
