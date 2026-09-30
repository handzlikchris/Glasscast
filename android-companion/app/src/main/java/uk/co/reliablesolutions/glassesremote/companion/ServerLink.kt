package uk.co.reliablesolutions.glassesremote.companion

import android.content.Context
import android.content.SharedPreferences
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONException
import org.json.JSONObject
import java.util.concurrent.TimeUnit

/**
 * What the companion keeps: the PC's companion socket URL and the companion token from pairing
 * with the PC (the PC keeps only its hash), and the paired glasses' key. App-private storage,
 * excluded from backups (allowBackup=false).
 */
class Prefs(context: Context) {
    companion object {
        const val DEFAULT_SERVER = "wss://glasses.example.com/ws/companion"
    }

    private val sp: SharedPreferences = context.getSharedPreferences("companion", Context.MODE_PRIVATE)

    var server: String
        get() = sp.getString("server", DEFAULT_SERVER) ?: DEFAULT_SERVER
        set(value) = sp.edit().putString("server", value).apply()

    var token: String?
        get() = sp.getString("token", null)
        set(value) = sp.edit().putString("token", value).apply()

    /**
     * The glasses paired on this phone (Approve here), and the key both hold. The PC never sees it.
     * One pair of glasses at a time: a new pairing replaces the old.
     */
    var glasses: PairedGlasses?
        get() {
            val id = sp.getString("glassesId", null) ?: return null
            val key = sp.getString("glassesKey", null)?.let(GlassesTrust::unb64u) ?: return null
            return PairedGlasses(id, key)
        }
        set(value) = sp.edit().apply {
            if (value == null) {
                remove("glassesId")
                remove("glassesKey")
            } else {
                putString("glassesId", value.id)
                putString("glassesKey", GlassesTrust.b64u(value.key))
            }
        }.apply()
}

/** OkHttp sends no Origin header, which the PC's companion socket requires (web pages always send one). */
private val http: OkHttpClient = OkHttpClient.Builder()
    .readTimeout(0, TimeUnit.MILLISECONDS)
    .build()

private fun JSONObject.typeOrNull(): String? = opt("type") as? String

private fun parse(text: String): JSONObject? = try {
    JSONObject(text)
} catch (e: JSONException) {
    null
}

/**
 * The paired companion's connection to the PC: authenticates with the token, pings every 15 s
 * (the PC drops a companion silent for 45 s), and reconnects with backoff until [stop].
 * Callbacks arrive on OkHttp's thread; the service hops them onto its own.
 */
class ServerLink(
    private val url: String,
    private val token: String,
    private val listener: Listener,
) {
    interface Listener {
        fun onConnected()
        fun onDisconnected(reason: String)
        /** The PC doesn't know this token (forgotten, or paired again elsewhere): pair again. */
        fun onAuthFailed()
        fun onMessage(message: JSONObject)
    }

    @Volatile
    private var socket: WebSocket? = null

    @Volatile
    private var stopped = false
    private var attempt = 0
    private val timer = java.util.concurrent.Executors.newSingleThreadScheduledExecutor()

    fun start() {
        stopped = false
        connect()
        timer.scheduleWithFixedDelay({
            socket?.send(JSONObject().put("type", "ping").put("t", System.currentTimeMillis()).toString())
        }, 15, 15, TimeUnit.SECONDS)
    }

    fun stop() {
        stopped = true
        socket?.close(1000, "bye")
        socket = null
        timer.shutdownNow()
    }

    fun send(message: JSONObject): Boolean = socket?.send(message.toString()) ?: false

    private fun connect() {
        if (stopped) return
        http.newWebSocket(Request.Builder().url(url).build(), object : WebSocketListener() {
            private var authenticated = false

            override fun onOpen(webSocket: WebSocket, response: Response) {
                webSocket.send(JSONObject().put("type", "auth").put("token", token).toString())
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                val message = parse(text) ?: return
                when (message.typeOrNull()) {
                    "authenticated" -> {
                        authenticated = true
                        attempt = 0
                        socket = webSocket
                        listener.onConnected()
                    }
                    "authFailed" -> {
                        stopped = true
                        listener.onAuthFailed()
                    }
                    else -> if (authenticated) listener.onMessage(message)
                }
            }

            // Answer the PC's close so onClosed follows; its reason ("replaced", "forgotten") matters.
            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(1000, null)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = lost(webSocket, reason)

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) =
                lost(webSocket, t.message ?: "connection failed")

            private fun lost(webSocket: WebSocket, reason: String) {
                if (socket == webSocket) socket = null
                if (authenticated) listener.onDisconnected(reason)
                if (!stopped) {
                    // "forgotten": the PC dropped this phone; retrying can't help.
                    if (reason == "forgotten") {
                        stopped = true
                        listener.onAuthFailed()
                        return
                    }
                    val delay = minOf(30L, 1L shl minOf(attempt++, 5))
                    runCatching { timer.schedule({ connect() }, delay, TimeUnit.SECONDS) }
                }
            }
        })
    }
}

/**
 * Pairs this phone with the PC once: asks for the Approve popup, shows the code, and hands back
 * the companion token when someone clicks Approve on the PC.
 */
class Pairing(private val url: String, private val name: String, private val listener: Listener) {
    interface Listener {
        fun onCode(code: String, expiresInSeconds: Int)
        fun onPaired(token: String)
        fun onFailed(reason: String)
    }

    private var socket: WebSocket? = null
    private var done = false

    fun start() {
        socket = http.newWebSocket(Request.Builder().url(url).build(), object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                webSocket.send(JSONObject().put("type", "pair").put("name", name).toString())
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                val message = parse(text) ?: return
                when (message.typeOrNull()) {
                    "pairCode" -> listener.onCode(message.optString("code"), message.optInt("expiresInSeconds"))
                    "paired" -> {
                        val token = message.opt("token") as? String
                        if (token != null) finish { listener.onPaired(token) } else finish { listener.onFailed("no token") }
                    }
                    "pairFailed" -> finish { listener.onFailed("Not approved on the PC") }
                }
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(1000, null)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) =
                finish { listener.onFailed("Pairing ended without approval") }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) =
                finish { listener.onFailed("Couldn't reach the PC: ${t.message}") }
        })
    }

    fun cancel() {
        done = true
        socket?.close(1000, "cancelled")
    }

    @Synchronized
    private fun finish(action: () -> Unit) {
        if (done) return
        done = true
        action()
        socket?.close(1000, "done")
    }
}
