package uk.co.reliablesolutions.glassesremote.companion

/**
 * The connect code the glasses show the first time they look for their phone on a server: typed
 * here, sent as `claim{code}`, it tells the server which phone those glasses are after. It only
 * lets this phone answer them: the glasses still pair with it (Approve here) and prove themselves.
 */
object ConnectCode {
    /** The server's alphabet: no 0/O, 1/I/L, so the code reads unambiguously off the glasses. */
    private const val ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"

    /** What was typed, as the server expects it ("ABC-234"), or null if it can't be a code. */
    fun normalize(typed: String): String? {
        val chars = typed.uppercase().filter { !it.isWhitespace() && it != '-' }
        if (chars.length != 6 || chars.any { it !in ALPHABET }) return null
        return chars.substring(0, 3) + "-" + chars.substring(3)
    }
}
