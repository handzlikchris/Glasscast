package uk.co.reliablesolutions.glassesremote.companion

import java.math.BigInteger
import java.nio.ByteBuffer
import java.security.AlgorithmParameters
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.PublicKey
import java.security.SecureRandom
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPublicKeySpec
import java.util.Base64
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * The phone's half of pairing the glasses and proving each other on every session: the same
 * derivations as the glasses' client-web/src/phoneTrust.ts (both check one test vector). The PC
 * only passes these messages along and never holds a key.
 *
 * Pairing: the glasses commit to their P-256 key (SHA-256), the phone sends its key, the glasses
 * reveal theirs; Z = ECDH, prk = HMAC("glasses-remote/pair/v1", Z‖gPub‖pPub), and from prk a
 * 6-digit code (shown on both screens), the 32-byte key and a 12-byte id. A session:
 * sk = HMAC(key, "session"‖gNonce‖pNonce), proofs HMAC(sk, "phone") and HMAC(sk, "glasses"),
 * and the offer and answer MACed with HMAC(sk, "offer\n"‖sdp) / HMAC(sk, "answer\n"‖sdp).
 */
object GlassesTrust {
    private const val PAIR_LABEL = "glasses-remote/pair/v1"
    private val random = SecureRandom()

    private val p256: ECParameterSpec by lazy {
        AlgorithmParameters.getInstance("EC").apply { init(ECGenParameterSpec("secp256r1")) }
            .getParameterSpec(ECParameterSpec::class.java)
    }

    data class Paired(val code: String, val id: String, val key: ByteArray)

    fun b64u(bytes: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)

    /** Null for anything that isn't base64url. */
    fun unb64u(text: String): ByteArray? = runCatching { Base64.getUrlDecoder().decode(text) }.getOrNull()

    fun nonce(): ByteArray = ByteArray(16).also { random.nextBytes(it) }

    fun newKeyPair(): KeyPair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()

    /** The 65-byte uncompressed point (0x04‖X‖Y), as Web Crypto exports it. */
    fun rawPublic(key: PublicKey): ByteArray {
        val w = (key as ECPublicKey).w
        return byteArrayOf(4) + fixed(w.affineX) + fixed(w.affineY)
    }

    /** A P-256 public key from its uncompressed point; null if it isn't one on the curve. */
    fun publicFromRaw(raw: ByteArray): PublicKey? {
        if (raw.size != 65 || raw[0] != 4.toByte()) return null
        val x = BigInteger(1, raw.copyOfRange(1, 33))
        val y = BigInteger(1, raw.copyOfRange(33, 65))
        if (!onCurve(x, y)) return null
        return runCatching { KeyFactory.getInstance("EC").generatePublic(ECPublicKeySpec(ECPoint(x, y), p256)) }.getOrNull()
    }

    fun sha256(bytes: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(bytes)

    fun ecdh(own: PrivateKey, other: PublicKey): ByteArray =
        KeyAgreement.getInstance("ECDH").apply {
            init(own)
            doPhase(other, true)
        }.generateSecret()

    fun derivePairing(z: ByteArray, glassesKey: ByteArray, phoneKey: ByteArray): Paired {
        val prk = hmac(PAIR_LABEL.toByteArray(), z, glassesKey, phoneKey)
        val code = (ByteBuffer.wrap(hmac(prk, "code".toByteArray())).int.toLong() and 0xffffffffL) % 1_000_000
        return Paired(
            code = code.toString().padStart(6, '0'),
            id = b64u(hmac(prk, "id".toByteArray()).copyOf(12)),
            key = hmac(prk, "key".toByteArray()),
        )
    }

    fun sessionKey(key: ByteArray, glassesNonce: ByteArray, phoneNonce: ByteArray): ByteArray =
        hmac(key, "session".toByteArray(), glassesNonce, phoneNonce)

    fun labelMac(sk: ByteArray, label: String): ByteArray = hmac(sk, label.toByteArray())

    fun sdpMac(sk: ByteArray, kind: String, sdp: String): ByteArray = hmac(sk, "$kind\n".toByteArray(), sdp.toByteArray())

    /** Constant-time comparison of a MAC with the base64url one received. */
    fun matches(expected: ByteArray, received: String): Boolean {
        val bytes = unb64u(received) ?: return false
        return MessageDigest.isEqual(expected, bytes)
    }

    private fun hmac(key: ByteArray, vararg parts: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").run {
            init(SecretKeySpec(key, "HmacSHA256"))
            parts.forEach { update(it) }
            doFinal()
        }

    private fun fixed(value: BigInteger): ByteArray {
        val bytes = value.toByteArray()
        return when {
            bytes.size == 32 -> bytes
            bytes.size > 32 -> bytes.copyOfRange(bytes.size - 32, bytes.size)
            else -> ByteArray(32 - bytes.size) + bytes
        }
    }

    private fun onCurve(x: BigInteger, y: BigInteger): Boolean {
        val field = (p256.curve.field as java.security.spec.ECFieldFp).p
        if (x.signum() < 0 || x >= field || y.signum() < 0 || y >= field) return false
        val lhs = y.modPow(BigInteger.TWO, field)
        val rhs = x.modPow(BigInteger.valueOf(3), field).add(p256.curve.a.multiply(x)).add(p256.curve.b).mod(field)
        return lhs == rhs
    }
}
