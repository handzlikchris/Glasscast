package uk.co.reliablesolutions.glassesremote.companion

import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.IBinder
import android.util.Log
import org.json.JSONObject
import java.util.concurrent.CopyOnWriteArrayList

/**
 * Keeps the phone reachable for the glasses: a foreground service holding the authenticated
 * connection to the PC, which only relays between the glasses and this phone. When glasses reach
 * the phone through it:
 *
 *   relayOpen → [GlassesRelay]: new glasses pair (the same code on both screens, Approve in a
 *   notification here); paired ones prove they hold the key → Android's screen-capture consent
 *   ("asking") → Start: capture and WebRTC ("live"), or Cancel ("declined").
 *
 * Once the glasses' DataChannel is open, the session no longer needs the PC: losing the relay
 * or the PC link doesn't end it. (It does need the phone online: Meta's app takes its link to
 * the glasses down when the phone loses internet.) It ends on Stop / End session here, the phone
 * locking (capture stops), End on the glasses, the glasses going quiet, or newer glasses taking
 * over. Anything not yet connected (a pairing prompt, the consent dialog, a session still
 * connecting) is dropped when the relay goes.
 *
 * Pairing approval and the consent dialog are the gates on the phone: every session needs someone
 * to tap Start here. All state lives on one thread ([handler]).
 */
class CompanionService : Service() {
    companion object {
        private const val TAG = "CompanionService"
        const val ACTION_START = "start"
        const val ACTION_STOP = "stop"
        const val ACTION_CONSENT = "consent"
        const val ACTION_END_SESSION = "endSession"
        const val ACTION_PAIR = "pair"
        const val EXTRA_CODE = "code"
        const val EXTRA_DATA = "data"
        const val EXTRA_APPROVE = "approve"
        const val EXTRA_ATTEMPT = "attempt"
        /** A pairing prompt nobody answers is a Reject. */
        private const val PAIR_TIMEOUT_MS = 60_000L

        private const val CHANNEL_ID = "companion"
        private const val ASK_CHANNEL_ID = "ask"
        private const val NOTIFY_ID = 1
        private const val ASK_ID = 2
        private const val PAIR_ID = 3

        @Volatile
        var status: String = "Stopped"
            private set

        /** Started and not stopped since (the setup screen's Start / Stop button). */
        @Volatile
        var running: Boolean = false
            private set

        private val watchers = CopyOnWriteArrayList<() -> Unit>()

        fun watch(watcher: () -> Unit) = watchers.add(watcher)

        fun unwatch(watcher: () -> Unit) = watchers.remove(watcher)

        /** Something the setup screen shows changed elsewhere (the input service came or went). */
        fun changed() = watchers.forEach { it() }

        fun start(context: Context) {
            context.startForegroundService(Intent(context, CompanionService::class.java).setAction(ACTION_START))
        }

        fun stop(context: Context) {
            context.startService(Intent(context, CompanionService::class.java).setAction(ACTION_STOP))
        }
    }

    private lateinit var thread: HandlerThread
    private lateinit var handler: Handler
    private lateinit var prefs: Prefs
    private var link: ServerLink? = null
    private lateinit var relay: GlassesRelay
    private var session: ScreenSession? = null
    /** The session key of glasses that proved themselves and wait for the consent. */
    private var authorizedKey: ByteArray? = null
    /** The glasses asked and the consent dialog is up (or should be). Read from the main thread too. */
    @Volatile
    private var asking = false

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        prefs = Prefs(this)
        thread = HandlerThread("companion").also { it.start() }
        handler = Handler(thread.looper)
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL_ID, "Companion", NotificationManager.IMPORTANCE_LOW))
        nm.createNotificationChannel(NotificationChannel(ASK_CHANNEL_ID, "Glasses requests", NotificationManager.IMPORTANCE_HIGH))
        relay = GlassesRelay(
            store = object : GlassesRelay.Store {
                override var glasses: PairedGlasses?
                    get() = prefs.glasses
                    set(value) {
                        prefs.glasses = value
                        watchers.forEach { it() }
                    }
            },
            send = { link?.send(it) },
            listener = object : GlassesRelay.Listener {
                override fun onPairingRequest(code: String, attempt: Int) = askPairing(code, attempt)

                override fun onPairingClosed() = getSystemService(NotificationManager::class.java).cancel(PAIR_ID)

                override fun onAuthorized(sessionKey: ByteArray) = onGlassesAuthorized(sessionKey)
            },
        )
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> {
                running = true
                watchers.forEach { it() }
                foreground(projection = false, "Connecting to the PC…")
                handler.post { connect() }
            }
            ACTION_STOP -> handler.post {
                relay.close()
                endSession(bye = "stopped")
                link?.stop()
                link = null
                setStatus("Stopped")
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
            }
            ACTION_CONSENT -> {
                val code = intent.getIntExtra(EXTRA_CODE, Activity.RESULT_CANCELED)
                val data = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                    intent.getParcelableExtra(EXTRA_DATA, Intent::class.java)
                } else {
                    @Suppress("DEPRECATION")
                    intent.getParcelableExtra(EXTRA_DATA)
                }
                // Straight away, while the consent activity is still in front: the projection
                // foreground type needs the consent and may not start from the background.
                if (code == Activity.RESULT_OK && data != null && asking) {
                    foreground(projection = true, "Glasses: starting…")
                }
                handler.post { onConsent(code, data) }
            }
            ACTION_END_SESSION -> handler.post { endSession(bye = "stopped") }
            ACTION_PAIR -> {
                val approve = intent.getBooleanExtra(EXTRA_APPROVE, false)
                val attempt = intent.getIntExtra(EXTRA_ATTEMPT, -1)
                handler.post { relay.decide(approve, attempt) }
            }
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        running = false
        watchers.forEach { it() }
        handler.post {
            session?.close()
            session = null
            link?.stop()
            link = null
        }
        thread.quitSafely()
        super.onDestroy()
    }

    private fun connect() {
        link?.stop()
        val token = prefs.token
        if (token == null) {
            setStatus("Not paired: pair with the PC first")
            stopSelf()
            return
        }
        setStatus("Connecting to the PC…")
        link = ServerLink(prefs.server, token, object : ServerLink.Listener {
            override fun onConnected() = handler.post { setStatus("Ready: the glasses can start a phone session") }.let {}

            override fun onDisconnected(reason: String) = handler.post {
                // A connected session carries on: it doesn't need the PC. Only what was still
                // being set up through it goes.
                relayGone()
                if (session == null) setStatus("Reconnecting to the PC…")
            }.let {}

            override fun onAuthFailed() = handler.post {
                prefs.token = null
                setStatus("The PC doesn't know this phone any more: pair again")
                link = null
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
            }.let {}

            override fun onMessage(message: JSONObject) = handler.post { handle(message) }.let {}
        }).also { it.start() }
    }

    private fun handle(message: JSONObject) {
        when (message.opt("type")) {
            "relayOpen" -> {
                relayGone()
                relay.open()
            }
            "relayClosed" -> relayGone()
            "pairStart", "pairReveal", "hello", "proof" -> relay.receive(message)
            "rtcAnswer" -> {
                val sdp = message.opt("sdp") as? String ?: return
                val mac = message.opt("mac") as? String ?: return
                session?.setAnswer(sdp, mac)
            }
            "iceCandidate" -> {
                val candidate = message.opt("candidate") as? String ?: return
                session?.addCandidate(candidate, message.opt("sdpMid") as? String, message.optInt("sdpMLineIndex", 0))
            }
        }
    }

    /** The relay (or the PC link) went: drop what it was still setting up, keep a connected session. */
    private fun relayGone() {
        relay.close()
        authorizedKey = null
        if (asking) {
            asking = false
            getSystemService(NotificationManager::class.java).cancel(ASK_ID)
        }
        if (session?.connected == false) endSession(bye = null)
    }

    /** Glasses proved they're the paired ones: newer glasses replace a running session. */
    private fun onGlassesAuthorized(sessionKey: ByteArray) {
        endSession(bye = "replaced")
        authorizedKey = sessionKey
        asking = true
        sendState("asking")
        askConsent()
        setStatus("The glasses ask to see this screen")
    }

    /** Shows the pairing code with Approve / Reject; no answer in a minute is a Reject. */
    private fun askPairing(code: String, attempt: Int) {
        fun action(approve: Boolean, request: Int) = PendingIntent.getService(
            this, request,
            Intent(this, CompanionService::class.java).setAction(ACTION_PAIR)
                .putExtra(EXTRA_APPROVE, approve).putExtra(EXTRA_ATTEMPT, attempt),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val shown = "${code.take(3)} ${code.drop(3)}"
        val notification = Notification.Builder(this, ASK_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("Pair glasses? Code $shown")
            .setContentText("Approve only if the glasses show $shown")
            .setOngoing(true)
            .addAction(Notification.Action.Builder(null, "Reject", action(false, 10)).build())
            .addAction(Notification.Action.Builder(null, "Approve", action(true, 11)).build())
            .build()
        getSystemService(NotificationManager::class.java).notify(PAIR_ID, notification)
        setStatus("Glasses want to pair: code $shown")
        handler.postDelayed({ relay.decide(false, attempt) }, PAIR_TIMEOUT_MS)
    }

    /**
     * Brings up the consent dialog. Starting an activity from the background is allowed while our
     * accessibility service is bound; the notification is the fallback either way.
     */
    private fun askConsent() {
        val intent = Intent(this, ConsentActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        runCatching { startActivity(intent) }.onFailure { Log.w(TAG, "consent activity not started", it) }
        val pending = PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val notification = Notification.Builder(this, ASK_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("Glasses want to control this phone")
            .setContentText("Tap to choose whether to share the screen")
            .setContentIntent(pending)
            .setAutoCancel(true)
            .build()
        getSystemService(NotificationManager::class.java).notify(ASK_ID, notification)
    }

    private fun onConsent(code: Int, data: Intent?) {
        getSystemService(NotificationManager::class.java).cancel(ASK_ID)
        val sessionKey = authorizedKey
        authorizedKey = null
        if (!asking || sessionKey == null) {
            // The glasses gave up meanwhile: a late answer starts nothing.
            foreground(projection = false, status)
            return
        }
        asking = false
        if (code != Activity.RESULT_OK || data == null) {
            sendState("declined")
            setStatus("Declined on the phone")
            return
        }

        try {
            session = ScreenSession(
                this,
                data,
                sessionKey,
                signal = { link?.send(it) },
                post = { block -> handler.post(block) },
                postDelayed = { ms, block -> handler.postDelayed(block, ms) },
                onEnded = { reason ->
                    Log.i(TAG, "phone session ended: $reason")
                    endSession(
                        bye = when (reason) {
                            "capture stopped" -> "capture"
                            "silent" -> "silent"
                            else -> null
                        },
                    )
                },
            )
            sendState("live")
            setStatus("Live: the glasses see and control this screen")
            foreground(projection = true, "Glasses are controlling this phone")
        } catch (e: RuntimeException) {
            Log.e(TAG, "couldn't start the session", e)
            session = null
            sendState("ended")
            foreground(projection = false, "Couldn't start: ${e.message}")
        }
    }

    /**
     * Ends the running session (if any). [bye] is why, for the glasses (on the channel, when it's
     * open): stopped, capture, replaced or silent; null when they know already. A relay still open
     * hears "ended", so glasses waiting for the video stop waiting.
     */
    private fun endSession(bye: String?) {
        if (asking) {
            asking = false
            getSystemService(NotificationManager::class.java).cancel(ASK_ID)
        }
        val running = session ?: return
        session = null
        val connected = running.connected
        running.close(bye)
        if (!connected && relay.isOpen) sendState("ended")
        setStatus(if (link != null) "Ready: the glasses can start a phone session" else "Stopped")
        foreground(projection = false, status)
    }

    private fun sendState(state: String) {
        link?.send(JSONObject().put("type", "sessionState").put("state", state))
    }

    private fun setStatus(text: String) {
        status = text
        if (session == null) foreground(projection = false, text)
        watchers.forEach { it() }
    }

    private fun foreground(projection: Boolean, text: String) {
        val stop = PendingIntent.getService(
            this, 1, Intent(this, CompanionService::class.java).setAction(if (projection) ACTION_END_SESSION else ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE,
        )
        val open = PendingIntent.getActivity(this, 2, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val notification = Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("Glasscast")
            .setContentText(text)
            .setContentIntent(open)
            .setOngoing(true)
            .addAction(Notification.Action.Builder(null, if (projection) "End session" else "Stop", stop).build())
            .build()
        val type = when {
            projection -> ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
            Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE -> ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE
            else -> 0
        }
        startForeground(NOTIFY_ID, notification, type)
    }
}
