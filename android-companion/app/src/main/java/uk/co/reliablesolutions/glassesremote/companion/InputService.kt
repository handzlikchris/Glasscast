package uk.co.reliablesolutions.glassesremote.companion

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.graphics.Path
import android.graphics.PixelFormat
import android.os.Bundle
import android.view.View
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo

/**
 * Turns the glasses' input into input on the phone, through Android's accessibility API: gestures
 * (tap, long press, swipe) at screen pixels, global actions (Back, Home, Recents, notifications),
 * and text into the focused field. The user turns it on once in Settings > Accessibility; a
 * sideloaded app first needs App info > menu > Allow restricted settings.
 *
 * It does nothing on its own: only a live phone session (ScreenSession) calls it.
 */
class InputService : AccessibilityService() {
    companion object {
        /** The running service, while it is enabled. */
        @Volatile
        var instance: InputService? = null
            private set

        private const val TAP_MS = 60L
        private const val LONG_PRESS_MS = 700L
    }

    private var keepAwake: View? = null

    override fun onServiceConnected() {
        instance = this
    }

    override fun onUnbind(intent: android.content.Intent?): Boolean {
        instance = null
        keepScreenOn(false)
        return super.onUnbind(intent)
    }

    override fun onDestroy() {
        instance = null
        keepScreenOn(false)
        super.onDestroy()
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}

    override fun onInterrupt() {}

    fun tap(x: Float, y: Float): Boolean = stroke(pointPath(x, y), TAP_MS)

    fun longPress(x: Float, y: Float): Boolean = stroke(pointPath(x, y), LONG_PRESS_MS)

    fun swipe(x1: Float, y1: Float, x2: Float, y2: Float, ms: Long): Boolean {
        val path = Path().apply {
            moveTo(x1, y1)
            lineTo(x2, y2)
        }
        return stroke(path, ms)
    }

    fun nav(action: NavAction): Boolean = performGlobalAction(
        when (action) {
            NavAction.BACK -> GLOBAL_ACTION_BACK
            NavAction.HOME -> GLOBAL_ACTION_HOME
            NavAction.RECENTS -> GLOBAL_ACTION_RECENTS
            NavAction.NOTIFICATIONS -> GLOBAL_ACTION_NOTIFICATIONS
        },
    )

    /**
     * Inserts text at the cursor of the focused text field (or at its end). Accessibility can only
     * replace a field's whole text, so this reads it, splices, writes it back and moves the cursor.
     * False when nothing editable has focus.
     */
    fun typeText(text: String): Boolean {
        val node = focusedEditable() ?: return false
        val current = if (node.isShowingHintText) "" else node.text?.toString().orEmpty()
        val (start, end) = selection(node, current.length)
        val updated = current.substring(0, start) + text + current.substring(end)
        return setText(node, updated, start + text.length)
    }

    fun key(key: KeyName): Boolean {
        val node = focusedEditable() ?: return false
        return when (key) {
            KeyName.ENTER -> node.performAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.id)
            KeyName.BACKSPACE -> {
                val current = if (node.isShowingHintText) "" else node.text?.toString().orEmpty()
                val (start, end) = selection(node, current.length)
                when {
                    start != end -> setText(node, current.substring(0, start) + current.substring(end), start)
                    start > 0 -> {
                        // Don't split a surrogate pair (an emoji) in half.
                        val from = if (start >= 2 && Character.isSurrogatePair(current[start - 2], current[start - 1])) start - 2 else start - 1
                        setText(node, current.substring(0, from) + current.substring(start), from)
                    }
                    else -> true
                }
            }
        }
    }

    /**
     * Keeps the screen on while a session is live: screen capture stops when the phone locks
     * (Android 15+). A 1-pixel accessibility overlay that takes no touches and no focus.
     */
    fun keepScreenOn(on: Boolean) {
        val wm = getSystemService(WindowManager::class.java) ?: return
        if (on && keepAwake == null) {
            val view = View(this)
            val params = WindowManager.LayoutParams(
                1, 1,
                WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY,
                WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON or
                    WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                    WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE,
                PixelFormat.TRANSPARENT,
            )
            try {
                wm.addView(view, params)
                keepAwake = view
            } catch (e: RuntimeException) {
                // Not attached (service going away): the screen just follows its own timeout.
            }
        } else if (!on) {
            keepAwake?.let { view -> runCatching { wm.removeView(view) } }
            keepAwake = null
        }
    }

    private fun stroke(path: Path, ms: Long): Boolean {
        val gesture = GestureDescription.Builder()
            .addStroke(GestureDescription.StrokeDescription(path, 0, ms))
            .build()
        return dispatchGesture(gesture, null, null)
    }

    private fun pointPath(x: Float, y: Float) = Path().apply { moveTo(x, y) }

    private fun focusedEditable(): AccessibilityNodeInfo? =
        findFocus(AccessibilityNodeInfo.FOCUS_INPUT)?.takeIf { it.isEditable }

    private fun selection(node: AccessibilityNodeInfo, length: Int): Pair<Int, Int> {
        val a = node.textSelectionStart
        val b = node.textSelectionEnd
        if (a < 0 || b < 0 || a > length || b > length) return length to length
        return minOf(a, b) to maxOf(a, b)
    }

    private fun setText(node: AccessibilityNodeInfo, text: String, cursor: Int): Boolean {
        val args = Bundle().apply {
            putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, text)
        }
        if (!node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args)) return false
        val at = cursor.coerceIn(0, text.length)
        node.performAction(
            AccessibilityNodeInfo.ACTION_SET_SELECTION,
            Bundle().apply {
                putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, at)
                putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, at)
            },
        )
        return true
    }
}
