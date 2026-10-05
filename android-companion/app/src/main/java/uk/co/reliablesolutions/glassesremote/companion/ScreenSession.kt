package uk.co.reliablesolutions.glassesremote.companion

import android.content.Context
import android.content.Intent
import android.graphics.Point
import android.hardware.display.DisplayManager
import android.media.projection.MediaProjection
import android.util.Log
import android.view.Display
import android.view.accessibility.AccessibilityNodeInfo
import org.json.JSONArray
import org.json.JSONObject
import org.webrtc.DataChannel
import org.webrtc.DefaultVideoDecoderFactory
import org.webrtc.DefaultVideoEncoderFactory
import org.webrtc.EglBase
import org.webrtc.IceCandidate
import org.webrtc.MediaConstraints
import org.webrtc.MediaStream
import org.webrtc.MediaStreamTrack
import org.webrtc.PeerConnection
import org.webrtc.PeerConnectionFactory
import org.webrtc.RtpParameters
import org.webrtc.RtpReceiver
import org.webrtc.RtpTransceiver
import org.webrtc.ScreenCapturerAndroid
import org.webrtc.SdpObserver
import org.webrtc.SessionDescription
import org.webrtc.SurfaceTextureHelper
import org.webrtc.VideoSource
import org.webrtc.VideoTrack
import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets
import kotlin.math.abs

/**
 * One phone session: the screen (MediaProjection, whole display) as a WebRTC video track, and the
 * DataChannel "input" the glasses send taps, swipes, keys and text on. The phone offers; the PC
 * relays the offer, the answer and the ICE candidates (via [signal]) and nothing else. The offer
 * carries a MAC under [sessionKey] (agreed with the glasses in [GlassesRelay]) and the answer must
 * carry one, which ties this connection's DTLS fingerprints to the paired glasses.
 *
 * Once the channel is open the session needs no PC: the glasses ping every 2 s on it, and the
 * session ends when they go quiet for [SILENT_MS], when the channel closes, or when they say "end".
 * Before it opens, it gives up after [CONNECT_MS].
 *
 * Everything that touches state runs on the service's thread ([post]); WebRTC calls back on its
 * own threads and hops over.
 */
class ScreenSession(
    private val context: Context,
    consent: Intent,
    private val sessionKey: ByteArray,
    private val signal: (JSONObject) -> Unit,
    private val post: (() -> Unit) -> Unit,
    private val postDelayed: (Long, () -> Unit) -> Unit,
    private val onEnded: (String) -> Unit,
) {
    companion object {
        private const val TAG = "ScreenSession"
        private const val FPS = 30
        /** Enough for a sharp 600-pixel screen of text; libwebrtc adapts below it. */
        private const val MAX_BITRATE_BPS = 2_500_000
        /** Glasses input per second (a held drag sends ~25 touch moves a second; the rest are few). */
        private const val MAX_INPUT_PER_SECOND = 120
        /** Window changes come in bursts while a pop-up is dragged: refit once they settle a little. */
        private const val REFIT_DELAY_MS = 120L
        /** Nothing from the glasses this long on the channel: they're gone (they ping every 2 s). */
        private const val SILENT_MS = 15_000L
        /** From the consent to an open channel. */
        private const val CONNECT_MS = 60_000L
        private const val WATCH_MS = 2_500L
        /**
         * A still screen makes no frames, and a picture the encoder dropped or the network lost
         * then stays wrong on the glasses until something moves (seen: seconds of a stale page
         * until a scroll). So the last frame goes again after this long without one.
         */
        private const val STILL_REPEAT_MS = 400L
        /** A walk's target scrolled into view: check its box this often until it stops moving. */
        private const val WALK_SETTLE_MS = 100L
        /** The longest a walk waits for the page to stop scrolling before answering. */
        private const val WALK_MAX_WAIT_MS = 1_000L

        @Volatile
        private var initialized = false

        private fun initialize(context: Context) {
            if (initialized) return
            PeerConnectionFactory.initialize(
                PeerConnectionFactory.InitializationOptions.builder(context.applicationContext).createInitializationOptions(),
            )
            initialized = true
        }
    }

    private val egl = EglBase.create()
    private val factory: PeerConnectionFactory
    private val capturer: ScreenCapturerAndroid
    private val helper: SurfaceTextureHelper
    private val source: VideoSource
    private val track: VideoTrack
    private val crop = CropProcessor()
    /**
     * The app whose window the crop follows (the glasses always see the app in front). Set by
     * picking an app in the overview; when it leaves the screen, whatever is in front instead.
     */
    private var followPackage: String? = null
    private var refitPending = false
    /**
     * The app overview is open (left twice, or Apps on the bar): the whole screen shows, and the
     * app picked next is followed.
     */
    private var picking = false
    /** Where the last walk (app profiles) ended, to step on from; dropped when the followed app changes. */
    private var walked: AccessibilityNodeInfo? = null
    /** Counts walks, so a walk still waiting for its page to settle gives way to a newer one. */
    private var walkCount = 0
    private val pc: PeerConnection
    private val channel: DataChannel
    private val screenWidth: Int
    private val screenHeight: Int
    private var closed = false
    /** The channel opened: from here on the session doesn't need the relay. Read from the service. */
    var connected = false
        private set
    private val startedAt = System.currentTimeMillis()
    private var lastHeard = 0L
    private var windowStart = 0L
    private var windowCount = 0

    init {
        initialize(context)
        factory = PeerConnectionFactory.builder()
            // Hardware H.264 (Constrained Baseline, which the glasses decode) and VP8.
            .setVideoEncoderFactory(DefaultVideoEncoderFactory(egl.eglBaseContext, true, false))
            .setVideoDecoderFactory(DefaultVideoDecoderFactory(egl.eglBaseContext))
            .createPeerConnectionFactory()

        // The display's real pixels: what the capture covers and what gestures and window bounds use.
        val display = context.getSystemService(DisplayManager::class.java).getDisplay(Display.DEFAULT_DISPLAY)
        val size = Point()
        @Suppress("DEPRECATION")
        display.getRealSize(size)
        screenWidth = size.x
        screenHeight = size.y

        capturer = ScreenCapturerAndroid(consent, object : MediaProjection.Callback() {
            // The user tapped the status-bar chip's Stop, the phone locked, or another app took over.
            override fun onStop() = post { end("capture stopped") }
        })
        helper = SurfaceTextureHelper.create("screen", egl.eglBaseContext)
        source = factory.createVideoSource(true)
        source.setVideoProcessor(crop)
        capturer.initialize(helper, context, source.capturerObserver)
        capturer.startCapture(screenWidth, screenHeight, FPS)
        track = factory.createVideoTrack("screen", source)

        val config = PeerConnection.RTCConfiguration(emptyList()).apply {
            sdpSemantics = PeerConnection.SdpSemantics.UNIFIED_PLAN
            // Keep gathering: the phone may move between Wi-Fi and mobile data mid-session.
            continualGatheringPolicy = PeerConnection.ContinualGatheringPolicy.GATHER_CONTINUALLY
            tcpCandidatePolicy = PeerConnection.TcpCandidatePolicy.DISABLED
        }
        pc = factory.createPeerConnection(config, Observer())
            ?: throw IllegalStateException("no peer connection")

        val transceiver = pc.addTransceiver(
            track,
            RtpTransceiver.RtpTransceiverInit(RtpTransceiver.RtpTransceiverDirection.SEND_ONLY, listOf("phone")),
        )
        preferH264(transceiver)
        val parameters = transceiver.sender.parameters
        // Screens are text: drop frames rather than resolution when the link is short.
        parameters.degradationPreference = RtpParameters.DegradationPreference.MAINTAIN_RESOLUTION
        parameters.encodings.forEach { it.maxBitrateBps = MAX_BITRATE_BPS }
        transceiver.sender.setParameters(parameters)

        channel = pc.createDataChannel("input", DataChannel.Init().apply { ordered = true })
        channel.registerObserver(ChannelObserver())

        watchWindows()
        refit()

        pc.createOffer(object : SdpAdapter() {
            override fun onCreateSuccess(sdp: SessionDescription) {
                pc.setLocalDescription(object : SdpAdapter() {
                    override fun onSetSuccess() = post {
                        if (!closed) {
                            val mac = GlassesTrust.b64u(GlassesTrust.sdpMac(sessionKey, "offer", sdp.description))
                            signal(JSONObject().put("type", "rtcOffer").put("sdp", sdp.description).put("mac", mac))
                        }
                    }
                }, sdp)
            }
        }, MediaConstraints())
        watch()
        repeatWhileStill()
    }

    /** The glasses' answer, if it's MACed with this session's key (else the session ends). */
    fun setAnswer(sdp: String, mac: String) {
        if (closed) return
        if (!GlassesTrust.matches(GlassesTrust.sdpMac(sessionKey, "answer", sdp), mac)) {
            end("answer not from the paired glasses")
            return
        }
        pc.setRemoteDescription(SdpAdapter(), SessionDescription(SessionDescription.Type.ANSWER, sdp))
    }

    fun addCandidate(candidate: String, sdpMid: String?, sdpMLineIndex: Int) {
        if (closed) return
        pc.addIceCandidate(IceCandidate(sdpMid ?: "", sdpMLineIndex, candidate))
    }

    /**
     * Ends the session here. [bye] tells the glasses why first (stopped, capture, replaced, silent),
     * when the channel is still open.
     */
    fun close(bye: String? = null) {
        if (closed) return
        if (bye != null) send(JSONObject().put("type", "bye").put("reason", bye))
        closed = true
        InputService.instance?.let { input ->
            input.keepScreenOn(false)
            input.onWindowsChanged = null
            input.onAppFront = null
            // A finger still down (the glasses left mid-drag) comes up.
            input.releaseTouch()
        }
        runCatching { capturer.stopCapture() }
        runCatching { channel.close() }
        pc.dispose()
        capturer.dispose()
        source.dispose()
        helper.dispose()
        factory.dispose()
        egl.release()
    }

    private fun end(reason: String) {
        if (closed) return
        Log.i(TAG, "session ended: $reason")
        onEnded(reason)
    }

    /** Sends the last frame again while the screen is still (see [STILL_REPEAT_MS]). */
    private fun repeatWhileStill() {
        postDelayed(STILL_REPEAT_MS / 2) {
            if (closed) return@postDelayed
            if (System.nanoTime() - crop.lastFrameAtNs >= STILL_REPEAT_MS * 1_000_000) helper.forceFrame()
            repeatWhileStill()
        }
    }

    /** Ends a session whose glasses went quiet, or that never connected. */
    private fun watch() {
        postDelayed(WATCH_MS) {
            if (closed) return@postDelayed
            val now = System.currentTimeMillis()
            when {
                connected && now - lastHeard > SILENT_MS -> end("silent")
                !connected && now - startedAt > CONNECT_MS -> end("never connected")
                else -> watch()
            }
        }
    }

    private fun preferH264(transceiver: RtpTransceiver) {
        val codecs = factory.getRtpSenderCapabilities(MediaStreamTrack.MediaType.MEDIA_TYPE_VIDEO).codecs
        // H.264 first (the glasses decode it in hardware), VP8 as the fallback, plus RTX/RED/FEC.
        val ranked = codecs.sortedBy {
            when (it.name.uppercase()) {
                "H264" -> 0
                "VP8" -> 1
                "RTX", "RED", "ULPFEC" -> 2
                else -> 3
            }
        }.filter { it.name.uppercase() in setOf("H264", "VP8", "RTX", "RED", "ULPFEC") }
        runCatching { transceiver.setCodecPreferences(ranked) }
    }

    /** Glasses input, strictly parsed and rate-limited, in screen pixels. */
    private fun handleInput(raw: String) {
        val now = System.currentTimeMillis()
        if (now - windowStart >= 1000) {
            windowStart = now
            windowCount = 0
        }
        if (++windowCount > MAX_INPUT_PER_SECOND) return
        lastHeard = now
        val command = InputProtocol.parse(raw) ?: return
        val input = InputService.instance
        when (command) {
            is InputCommand.Ping -> send(JSONObject().put("type", "pong").put("t", command.t))
            is InputCommand.End -> end("ended on the glasses")
            is InputCommand.SwitchApp -> {
                val target = input?.recentApps?.step(command.previous, System.currentTimeMillis())
                if (target == null || !input.bringToFront(target)) {
                    sendResult("switchApp", false)
                } else {
                    // Its window may appear a moment later: window changes refit it then.
                    followApp(target)
                    sendScreen()
                }
            }
            is InputCommand.Tap -> input?.tap(screenX(command.x), screenY(command.y))
            is InputCommand.LongPress -> input?.longPress(screenX(command.x), screenY(command.y))
            is InputCommand.DoubleTap -> input?.doubleTap(screenX(command.x), screenY(command.y))
            is InputCommand.Touch -> input?.touch(command.phase, screenX(command.x), screenY(command.y))
            // In the app overview a swipe settles before lifting, so it moves one app, not a fling's two.
            is InputCommand.Swipe -> input?.swipe(
                screenX(command.x1), screenY(command.y1), screenX(command.x2), screenY(command.y2), command.ms,
                settle = picking,
            )
            is InputCommand.Nav -> {
                input?.nav(command.action)
                if (command.action == NavAction.RECENTS) pickApp()
            }
            is InputCommand.OverviewApps -> sendOverviewApps(input)
            is InputCommand.Controls -> sendControls(input)
            is InputCommand.Walk -> walk(input, command)
            is InputCommand.TypeText -> sendResult("typeText", input?.typeText(command.text) ?: false)
            is InputCommand.Key -> sendResult("key", input?.key(command.key) ?: false)
        }
    }

    /** Shows the whole screen while the app overview is open, until an app comes to the front. */
    private fun pickApp() {
        picking = true
        crop.region = Region.FULL
        helper.forceFrame()
        sendScreen()
        InputService.instance?.onAppFront = { pkg -> post { if (!closed) appPicked(pkg) } }
    }

    private fun appPicked(pkg: String) {
        if (!picking) return
        picking = false
        InputService.instance?.onAppFront = null
        followApp(pkg)
    }

    private fun setRegion(region: Region) {
        picking = false
        crop.region = region
        // The new crop shows at once, not with the screen's next change.
        helper.forceFrame()
        sendScreen()
    }

    private fun followApp(pkg: String?) {
        if (pkg != followPackage) walked = null
        followPackage = pkg
        watchWindows()
        refit()
    }

    /** Listens for window changes (the accessibility service may have started after the session). */
    private fun watchWindows() {
        InputService.instance?.onWindowsChanged = {
            post {
                if (!closed && !refitPending) {
                    refitPending = true
                    handlerDelay { refitPending = false; refit() }
                }
            }
        }
    }

    private fun handlerDelay(block: () -> Unit) = postDelayed(REFIT_DELAY_MS) { if (!closed) block() }

    /**
     * Crops to the followed app's window (a split-screen half, a pop-up, or the full screen). When
     * it isn't on screen any more (it opened another app, or you went home and opened one), the
     * app in front is followed instead; with none, the whole screen shows.
     */
    private fun refit() {
        if (picking) return
        val input = InputService.instance ?: return
        val pkg = followPackage?.takeIf { input.appWindow(it) != null } ?: input.appToFit(screenWidth, screenHeight)
        val renamed = pkg != followPackage
        if (renamed) walked = null
        followPackage = pkg
        val bounds = pkg?.let { input.appWindow(it) }
        val region = if (bounds == null) {
            Region.FULL
        } else {
            val x = bounds.left.coerceIn(0, screenWidth).toDouble() / screenWidth
            val y = bounds.top.coerceIn(0, screenHeight).toDouble() / screenHeight
            val right = bounds.right.coerceIn(0, screenWidth).toDouble() / screenWidth
            val bottom = bounds.bottom.coerceIn(0, screenHeight).toDouble() / screenHeight
            Region(x, y, right - x, bottom - y)
        }
        if (!same(region, crop.region)) setRegion(region) else if (renamed) sendScreen()
    }

    private fun same(a: Region, b: Region): Boolean =
        abs(a.x - b.x) < 0.002 && abs(a.y - b.y) < 0.002 && abs(a.width - b.width) < 0.002 && abs(a.height - b.height) < 0.002

    private fun screenX(x: Double): Float = ((crop.region.x + x * crop.region.width) * screenWidth).toFloat()

    private fun screenY(y: Double): Float = ((crop.region.y + y * crop.region.height) * screenHeight).toFloat()

    private fun sendScreen() {
        val r = crop.region
        send(
            JSONObject().put("type", "screen").put("width", screenWidth).put("height", screenHeight).put(
                "region",
                JSONObject().put("x", r.x).put("y", r.y).put("width", r.width).put("height", r.height),
            ).apply {
                val pkg = followPackage
                if (pkg != null) {
                    InputService.instance?.appLabel(pkg)?.let { put("app", it.take(24)) }
                    // The package picks the glasses' app profile (architecture/app-profiles.md).
                    put("pkg", pkg.take(100))
                }
            },
        )
    }

    /**
     * The overview's row of apps as points in the frame the glasses see (0..1), with names for
     * their status bar; empty when the overview isn't open or has no row. The glasses move their
     * cursor there and a pinch taps, as anywhere else: nothing is opened from here.
     */
    private fun sendOverviewApps(input: InputService?) {
        val apps = JSONArray()
        if (picking && input != null) {
            val r = crop.region
            input.overviewApps().forEach { (bounds, label) ->
                val x = (bounds.exactCenterX().toDouble() / screenWidth - r.x) / r.width
                val y = (bounds.exactCenterY().toDouble() / screenHeight - r.y) / r.height
                if (x in 0.0..1.0 && y in 0.0..1.0) apps.put(JSONObject().put("x", x).put("y", y).put("label", label.take(24)))
            }
        }
        send(JSONObject().put("type", "overviewApps").put("apps", apps))
    }

    /** The crop in screen pixels: what the glasses see. */
    private fun cropBox(): Box {
        val r = crop.region
        return Box(
            (r.x * screenWidth).toInt(), (r.y * screenHeight).toInt(),
            ((r.x + r.width) * screenWidth).toInt(), ((r.y + r.height) * screenHeight).toInt(),
        )
    }

    /** A control for the glasses: its box in 0..1 of their frame (clipped to it), or null if it's outside. */
    private fun controlJson(control: Control): JSONObject? {
        val area = cropBox()
        val left = maxOf(control.box.left, area.left)
        val top = maxOf(control.box.top, area.top)
        val right = minOf(control.box.right, area.right)
        val bottom = minOf(control.box.bottom, area.bottom)
        if (right <= left || bottom <= top) return null
        val w = (area.right - area.left).toDouble()
        val h = (area.bottom - area.top).toDouble()
        return JSONObject()
            .put("x", (left - area.left) / w).put("y", (top - area.top) / h)
            .put("w", (right - left) / w).put("h", (bottom - top) / h)
            .put("kind", control.kind).put("label", control.label).put("id", control.id)
    }

    /**
     * What you can press in the followed app's window, for the glasses' app profiles. Labels are
     * words on the screen: counted in the log, never written to it.
     */
    private fun sendControls(input: InputService?) {
        val pkg = followPackage
        val items = JSONArray()
        val root = if (picking) null else pkg?.let { input?.appRoot(it) }
        if (root != null) ScreenControls.onScreen(A11yNode(root)).forEach { c -> controlJson(c)?.let(items::put) }
        Log.i(TAG, "controls: ${items.length()}")
        send(JSONObject().put("type", "controls").put("pkg", pkg.orEmpty()).put("items", items))
    }

    /**
     * Steps to the next (or previous) thing of a kind in the followed app, in reading order, from
     * where the last walk ended (or where you're reading, after a scroll). Something off screen is
     * scrolled into view first, and the answer waits for the page to stop moving.
     */
    private fun walk(input: InputService?, command: InputCommand.Walk) {
        val count = ++walkCount
        val pkg = followPackage
        val root = if (picking) null else pkg?.let { input?.appRoot(it) }
        if (root == null) return sendWalked(pkg, null)
        val area = cropBox()
        val list = ScreenControls.walkable(A11yNode(root), command.unit)
        val last = walked
        val from = last?.let { node -> list.indexOfFirst { (it.node as A11yNode).info == node } }
            ?.takeIf { it >= 0 && list[it].box.touches(area) }
        val at = ScreenControls.step(list, from, command.next, area)
        Log.i(TAG, "walk ${command.unit.wire} ${if (command.next) "next" else "previous"}: ${at ?: "end"} of ${list.size}")
        if (at == null) return sendWalked(pkg, null)
        val target = list[at]
        val node = (target.node as A11yNode).info
        walked = node
        if (target.box.within(area)) return sendWalked(pkg, target)
        node.performAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_SHOW_ON_SCREEN.id)
        settle(count, pkg, target, previous = null, waited = 0L)
    }

    /** Answers a walk once its target's box holds still on screen (Chrome scrolls smoothly), or after a second. */
    private fun settle(count: Int, pkg: String?, target: Control, previous: Box?, waited: Long) {
        postDelayed(WALK_SETTLE_MS) {
            if (closed || count != walkCount) return@postDelayed
            val node = (target.node as A11yNode)
            node.info.refresh()
            val box = node.box
            val still = previous != null && box == previous
            when {
                still && box.touches(cropBox()) -> sendWalked(pkg, target.copy(box = box))
                waited + WALK_SETTLE_MS >= WALK_MAX_WAIT_MS ->
                    sendWalked(pkg, if (box.touches(cropBox())) target.copy(box = box) else null)
                else -> settle(count, pkg, target, box, waited + WALK_SETTLE_MS)
            }
        }
    }

    private fun sendWalked(pkg: String?, control: Control?) {
        val message = JSONObject().put("type", "walked").put("pkg", pkg.orEmpty())
        control?.let(::controlJson)?.let { message.put("item", it) }
        send(message)
    }

    private fun sendResult(of: String, ok: Boolean) = send(JSONObject().put("type", "result").put("of", of).put("ok", ok))

    private fun send(message: JSONObject) {
        if (closed || channel.state() != DataChannel.State.OPEN) return
        val bytes = message.toString().toByteArray(StandardCharsets.UTF_8)
        channel.send(DataChannel.Buffer(ByteBuffer.wrap(bytes), false))
    }

    private inner class ChannelObserver : DataChannel.Observer {
        override fun onBufferedAmountChange(previousAmount: Long) {}

        override fun onStateChange() = post {
            if (closed) return@post
            when (channel.state()) {
                DataChannel.State.OPEN -> {
                    connected = true
                    lastHeard = System.currentTimeMillis()
                    sendScreen()
                }
                DataChannel.State.CLOSED -> if (connected) end("glasses left")
                else -> {}
            }
        }

        override fun onMessage(buffer: DataChannel.Buffer) {
            if (buffer.binary) return
            val data = buffer.data
            if (data.remaining() > InputProtocol.MAX_MESSAGE_CHARS * 4) return
            val bytes = ByteArray(data.remaining())
            data.get(bytes)
            val text = String(bytes, StandardCharsets.UTF_8)
            post { if (!closed) handleInput(text) }
        }
    }

    private inner class Observer : PeerConnection.Observer {
        override fun onIceCandidate(candidate: IceCandidate) = post {
            if (!closed) {
                signal(
                    JSONObject().put("type", "iceCandidate").put("candidate", candidate.sdp)
                        .put("sdpMid", candidate.sdpMid).put("sdpMLineIndex", candidate.sdpMLineIndex),
                )
            }
        }

        override fun onConnectionChange(newState: PeerConnection.PeerConnectionState) {
            Log.i(TAG, "connection $newState")
            when (newState) {
                PeerConnection.PeerConnectionState.CONNECTED -> post { InputService.instance?.keepScreenOn(true) }
                PeerConnection.PeerConnectionState.FAILED -> post { end("connection failed") }
                else -> {}
            }
        }

        override fun onSignalingChange(state: PeerConnection.SignalingState) {}
        override fun onIceConnectionChange(state: PeerConnection.IceConnectionState) {}
        override fun onIceConnectionReceivingChange(receiving: Boolean) {}
        override fun onIceGatheringChange(state: PeerConnection.IceGatheringState) {}
        override fun onIceCandidatesRemoved(candidates: Array<out IceCandidate>) {}
        override fun onAddStream(stream: MediaStream) {}
        override fun onRemoveStream(stream: MediaStream) {}
        override fun onDataChannel(channel: DataChannel) {}
        override fun onRenegotiationNeeded() {}
        override fun onAddTrack(receiver: RtpReceiver, streams: Array<out MediaStream>) {}
    }

    /** SdpObserver with nothing to do by default; failures are logged. */
    private open class SdpAdapter : SdpObserver {
        override fun onCreateSuccess(sdp: SessionDescription) {}
        override fun onSetSuccess() {}
        override fun onCreateFailure(error: String?) {
            Log.w(TAG, "SDP create failed: $error")
        }

        override fun onSetFailure(error: String?) {
            Log.w(TAG, "SDP set failed: $error")
        }
    }
}
