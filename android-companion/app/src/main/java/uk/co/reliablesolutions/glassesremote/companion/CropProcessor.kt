package uk.co.reliablesolutions.glassesremote.companion

import org.webrtc.VideoFrame
import org.webrtc.VideoProcessor
import org.webrtc.VideoSink
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt

/** A crop of the screen: 0..1 on each axis. */
data class Region(val x: Double, val y: Double, val width: Double, val height: Double) {
    companion object {
        val FULL = Region(0.0, 0.0, 1.0, 1.0)
    }
}

/**
 * Crops each captured frame to the region the glasses look at and scales it so its long side is
 * at most [maxSide] (the glasses show 600×600). The frame keeps the crop's shape; the glasses
 * letterbox it. cropAndScale on a texture buffer is a GPU transform, not a copy.
 */
class CropProcessor(private val maxSide: Int = 600) : VideoProcessor {
    @Volatile
    var region: Region = Region.FULL

    private var sink: VideoSink? = null

    override fun setSink(sink: VideoSink?) {
        this.sink = sink
    }

    override fun onCapturerStarted(success: Boolean) {}

    override fun onCapturerStopped() {}

    override fun onFrameCaptured(frame: VideoFrame) {
        val out = sink ?: return
        val buffer = frame.buffer
        val r = region
        val w = buffer.width
        val h = buffer.height
        val cropX = even(r.x * w)
        val cropY = even(r.y * h)
        val cropW = max(2, even(r.width * w)).coerceAtMost(w - cropX)
        val cropH = max(2, even(r.height * h)).coerceAtMost(h - cropY)
        val scale = min(1.0, maxSide.toDouble() / max(cropW, cropH))
        val scaled = buffer.cropAndScale(cropX, cropY, cropW, cropH, max(2, even(cropW * scale)), max(2, even(cropH * scale)))
        val cropped = VideoFrame(scaled, frame.rotation, frame.timestampNs)
        out.onFrame(cropped)
        cropped.release()
    }

    private fun even(v: Double): Int = (v.roundToInt() / 2) * 2

    private fun even(v: Int): Int = (v / 2) * 2
}
