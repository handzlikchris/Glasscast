package uk.co.reliablesolutions.glassesremote.companion

import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjection
import android.util.Log
import android.view.WindowManager
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

/**
 * One phone session: the screen (MediaProjection, whole display) as a WebRTC video track, and the
 * DataChannel "input" the glasses send taps, swipes, keys and text on. The phone offers; the PC
 * relays the offer, the answer and the ICE candidates (via [signal]) and nothing else.
 *
 * Everything that touches state runs on the service's thread ([post]); WebRTC calls back on its
 * own threads and hops over.
 */
class ScreenSession(
    private val context: Context,
    consent: Intent,
    private val signal: (JSONObject) -> Unit,
    private val post: (() -> Unit) -> Unit,
    private val onEnded: (String) -> Unit,
) {
    companion object {
        private const val TAG = "ScreenSession"
        private const val FPS = 30
        /** Enough for a sharp 600-pixel screen of text; libwebrtc adapts below it. */
        private const val MAX_BITRATE_BPS = 2_500_000
        /** Glasses input per second (a pinch-drag sends nothing; taps and swipes are few). */
        private const val MAX_INPUT_PER_SECOND = 60

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
    private val pc: PeerConnection
    private val channel: DataChannel
    private val screenWidth: Int
    private val screenHeight: Int
    private var closed = false
    private var windowStart = 0L
    private var windowCount = 0

    init {
        initialize(context)
        factory = PeerConnectionFactory.builder()
            // Hardware H.264 (Constrained Baseline, which the glasses decode) and VP8.
            .setVideoEncoderFactory(DefaultVideoEncoderFactory(egl.eglBaseContext, true, false))
            .setVideoDecoderFactory(DefaultVideoDecoderFactory(egl.eglBaseContext))
            .createPeerConnectionFactory()

        val bounds = context.getSystemService(WindowManager::class.java).currentWindowMetrics.bounds
        screenWidth = bounds.width()
        screenHeight = bounds.height()

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

        pc.createOffer(object : SdpAdapter() {
            override fun onCreateSuccess(sdp: SessionDescription) {
                pc.setLocalDescription(object : SdpAdapter() {
                    override fun onSetSuccess() = post {
                        if (!closed) signal(JSONObject().put("type", "rtcOffer").put("sdp", sdp.description))
                    }
                }, sdp)
            }
        }, MediaConstraints())
    }

    fun setAnswer(sdp: String) {
        if (closed) return
        pc.setRemoteDescription(SdpAdapter(), SessionDescription(SessionDescription.Type.ANSWER, sdp))
    }

    fun addCandidate(candidate: String, sdpMid: String?, sdpMLineIndex: Int) {
        if (closed) return
        pc.addIceCandidate(IceCandidate(sdpMid ?: "", sdpMLineIndex, candidate))
    }

    /** Ends the session here (the PC is told by the caller). */
    fun close() {
        if (closed) return
        closed = true
        InputService.instance?.keepScreenOn(false)
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
        val command = InputProtocol.parse(raw) ?: return
        val input = InputService.instance
        when (command) {
            is InputCommand.Ping -> send(JSONObject().put("type", "pong").put("t", command.t))
            is InputCommand.SetRegion -> {
                crop.region = Region(command.x, command.y, command.width, command.height)
                sendScreen()
            }
            is InputCommand.Tap -> input?.tap(screenX(command.x), screenY(command.y))
            is InputCommand.LongPress -> input?.longPress(screenX(command.x), screenY(command.y))
            is InputCommand.Swipe -> input?.swipe(
                screenX(command.x1), screenY(command.y1), screenX(command.x2), screenY(command.y2), command.ms,
            )
            is InputCommand.Nav -> input?.nav(command.action)
            is InputCommand.TypeText -> sendResult("typeText", input?.typeText(command.text) ?: false)
            is InputCommand.Key -> sendResult("key", input?.key(command.key) ?: false)
        }
    }

    private fun screenX(x: Double): Float = ((crop.region.x + x * crop.region.width) * screenWidth).toFloat()

    private fun screenY(y: Double): Float = ((crop.region.y + y * crop.region.height) * screenHeight).toFloat()

    private fun sendScreen() {
        val r = crop.region
        send(
            JSONObject().put("type", "screen").put("width", screenWidth).put("height", screenHeight).put(
                "region",
                JSONObject().put("x", r.x).put("y", r.y).put("width", r.width).put("height", r.height),
            ),
        )
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
            if (!closed && channel.state() == DataChannel.State.OPEN) sendScreen()
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
