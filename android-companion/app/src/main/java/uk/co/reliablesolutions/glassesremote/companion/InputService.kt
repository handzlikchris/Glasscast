package uk.co.reliablesolutions.glassesremote.companion

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.graphics.Path
import android.graphics.Rect
import android.graphics.PixelFormat
import android.graphics.PointF
import android.accessibilityservice.InputMethod
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.KeyEvent
import android.view.View
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import android.view.accessibility.AccessibilityWindowInfo

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

        private const val TAG = "InputService"
        /** Long enough for views that only react to a press they can see (a very short one can be missed). */
        private const val TAP_MS = 100L
        private const val LONG_PRESS_MS = 700L
        /** Each piece of a held finger's movement takes this long (the glasses send ~25 a second). */
        private const val TOUCH_SEGMENT_MS = 30L
        /** Double tap: two taps this long, this far apart (inside Android's 40..300 ms double-tap window). */
        private const val DOUBLE_TAP_TAP_MS = 50L
        private const val DOUBLE_TAP_GAP_MS = 100L

        /** Nodes searched for a text field when none has input focus (a screen has a few hundred). */
        private const val MAX_NODES = 1500
    }

    private var keepAwake: View? = null
    private val main = Handler(Looper.getMainLooper())

    // The held finger (touch): one continued stroke, a piece at a time. Main thread only.
    private var touchStroke: GestureDescription.StrokeDescription? = null
    private var touchAt = PointF()
    private var touchTarget: PointF? = null
    private var touchEnding = false
    private var touchBusy = false

    /** Called (on the main thread) when windows open, close, move or resize; set by a live session. */
    @Volatile
    var onWindowsChanged: (() -> Unit)? = null

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

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        if (event?.eventType == AccessibilityEvent.TYPE_WINDOWS_CHANGED) onWindowsChanged?.invoke()
    }

    /**
     * The window Fit follows: the top-most app window that doesn't fill the screen, such as a
     * Samsung pop-up view window (freeform windows sit above full-screen apps). Null when every
     * app window fills the screen. In screen pixels.
     */
    fun floatingAppWindow(screenWidth: Int, screenHeight: Int): Rect? {
        val bounds = Rect()
        return windows
            .filter { it.type == AccessibilityWindowInfo.TYPE_APPLICATION }
            .sortedByDescending { it.layer }
            .firstNotNullOfOrNull { window ->
                window.getBoundsInScreen(bounds)
                val own = window.root?.packageName?.toString() == packageName
                val fillsScreen = bounds.width() * bounds.height() >= 0.9 * screenWidth * screenHeight
                if (own || fillsScreen || bounds.width() < 100 || bounds.height() < 100) null else Rect(bounds)
            }
    }

    override fun onInterrupt() {}

    fun tap(x: Float, y: Float): Boolean = stroke(pointPath(x, y), TAP_MS)

    fun longPress(x: Float, y: Float): Boolean = stroke(pointPath(x, y), LONG_PRESS_MS)

    fun doubleTap(x: Float, y: Float): Boolean {
        val gesture = GestureDescription.Builder()
            .addStroke(GestureDescription.StrokeDescription(pointPath(x, y), 0, DOUBLE_TAP_TAP_MS))
            .addStroke(GestureDescription.StrokeDescription(pointPath(x, y), DOUBLE_TAP_TAP_MS + DOUBLE_TAP_GAP_MS, DOUBLE_TAP_TAP_MS))
            .build()
        return dispatchGesture(gesture, logCancelled("double tap"), null)
    }

    /**
     * A finger held on the screen: down, then moved in short pieces as the glasses send its
     * position (the newest wins while a piece is in flight), then lifted. Held without moving, it
     * is Android's own long press. Any thread; the work happens on the main thread.
     */
    fun touch(phase: TouchPhase, x: Float, y: Float) = main.post {
        when (phase) {
            TouchPhase.DOWN -> {
                touchStroke = null
                touchBusy = false
                touchEnding = false
                touchTarget = null
                touchAt = PointF(x, y)
                val stroke = GestureDescription.StrokeDescription(pointPath(x, y), 0, 1, true)
                touchStroke = stroke
                dispatchTouch(stroke)
            }
            TouchPhase.MOVE -> if (touchStroke != null) {
                touchTarget = PointF(x, y)
                if (!touchBusy) nextTouchPiece()
            }
            TouchPhase.UP -> if (touchStroke != null) {
                touchTarget = PointF(x, y)
                touchEnding = true
                if (!touchBusy) nextTouchPiece()
            }
        }
    }

    /** Lifts a held finger where it is (the session ended mid-drag). */
    fun releaseTouch() = main.post {
        if (touchStroke != null) {
            touchEnding = true
            if (!touchBusy) nextTouchPiece()
        }
    }

    private fun nextTouchPiece() {
        val stroke = touchStroke ?: return
        val target = touchTarget
        if (target == null && !touchEnding) return
        val to = target ?: touchAt
        touchTarget = null
        val path = Path().apply {
            moveTo(touchAt.x, touchAt.y)
            lineTo(to.x, to.y)
        }
        val ending = touchEnding
        val piece = stroke.continueStroke(path, 0, TOUCH_SEGMENT_MS, !ending)
        touchAt = to
        if (ending) {
            touchStroke = null
            touchEnding = false
        } else {
            touchStroke = piece
        }
        dispatchTouch(piece)
    }

    private fun dispatchTouch(stroke: GestureDescription.StrokeDescription) {
        touchBusy = true
        val sent = dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), object : GestureResultCallback() {
            override fun onCompleted(gestureDescription: GestureDescription?) {
                touchBusy = false
                nextTouchPiece()
            }

            override fun onCancelled(gestureDescription: GestureDescription?) {
                // Another gesture (a tap, a swipe) or the system took over: the finger is up.
                Log.w(TAG, "held touch cancelled")
                touchBusy = false
                touchStroke = null
                touchEnding = false
            }
        }, main)
        if (!sent) {
            Log.w(TAG, "held touch not dispatched")
            touchBusy = false
            touchStroke = null
        }
    }

    private fun logCancelled(what: String) = object : GestureResultCallback() {
        override fun onCancelled(gestureDescription: GestureDescription?) {
            Log.w(TAG, "$what cancelled")
        }
    }

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
    /**
     * Types text. First as a keyboard would (Android 13+: this service is also an input method, see
     * flagInputMethodEditor), into whatever has keyboard input right now: that reaches views that
     * draw their own text, such as a remote desktop client's screen with its keyboard open. Else
     * into the text field on screen through accessibility.
     */
    fun typeText(text: String): Boolean {
        keyboardInput()?.let { input ->
            input.commitText(text, 1, null)
            // Lengths only: typed text never goes into the log.
            Log.i(TAG, "typeText: ${text.length} chars as a keyboard into ${editorName()}")
            return true
        }
        val node = editableTarget()
        if (node == null) {
            Log.i(TAG, "typeText: no text field on screen")
            return false
        }
        val current = if (node.isShowingHintText) "" else node.text?.toString().orEmpty()
        val (start, end) = selection(node, current.length)
        val updated = current.substring(0, start) + text + current.substring(end)
        val ok = setText(node, updated, start + text.length)
        // Lengths only: typed text never goes into the log.
        Log.i(TAG, "typeText: ${text.length} chars into ${node.className} -> $ok")
        return ok
    }

    fun key(key: KeyName): Boolean {
        keyboardInput()?.let { input ->
            val code = if (key == KeyName.ENTER) KeyEvent.KEYCODE_ENTER else KeyEvent.KEYCODE_DEL
            input.sendKeyEvent(KeyEvent(KeyEvent.ACTION_DOWN, code))
            input.sendKeyEvent(KeyEvent(KeyEvent.ACTION_UP, code))
            Log.i(TAG, "key $key as a keyboard into ${editorName()}")
            return true
        }
        val node = editableTarget()
        if (node == null) {
            Log.i(TAG, "key $key: no text field on screen")
            return false
        }
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
        // A cancelled gesture is a missed tap on the glasses: log it (adb logcat -s InputService).
        val sent = dispatchGesture(gesture, object : GestureResultCallback() {
            override fun onCancelled(gestureDescription: GestureDescription?) {
                Log.w(TAG, "gesture cancelled ($ms ms)")
            }
        }, null)
        if (!sent) Log.w(TAG, "gesture not dispatched")
        return sent
    }

    private fun pointPath(x: Float, y: Float) = Path().apply { moveTo(x, y) }

    /**
     * Where typed text goes: the field with input focus, or else (focus went elsewhere, e.g. Enter
     * sent a message and the field let go) the text field on screen in the active window, which
     * gets focus first. The focused one of several, else the only or first visible one.
     */
    private fun editableTarget(): AccessibilityNodeInfo? {
        findFocus(AccessibilityNodeInfo.FOCUS_INPUT)?.takeIf { it.isEditable }?.let { return it }
        val root = rootInActiveWindow ?: return null
        val fields = ArrayList<AccessibilityNodeInfo>()
        collectEditable(root, fields, budget = intArrayOf(MAX_NODES))
        val field = fields.firstOrNull { it.isFocused } ?: fields.firstOrNull() ?: return null
        val focused = field.performAction(AccessibilityNodeInfo.ACTION_FOCUS)
        Log.i(TAG, "text field had no input focus; using ${field.className} of ${fields.size} (focus $focused)")
        return field
    }

    /** The keyboard connection to whatever has keyboard input now (Android 13+), or null. */
    private fun keyboardInput(): InputMethod.AccessibilityInputConnection? =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU && inputMethod?.currentInputStarted == true) {
            inputMethod?.currentInputConnection
        } else {
            null
        }

    /** The app whose editor has keyboard input, for the log (a package name, no content). */
    private fun editorName(): String =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) inputMethod?.currentInputEditorInfo?.packageName ?: "?" else "?"

    private fun collectEditable(node: AccessibilityNodeInfo, into: MutableList<AccessibilityNodeInfo>, budget: IntArray) {
        if (--budget[0] < 0) return
        if (node.isEditable && node.isVisibleToUser && node.isEnabled) into.add(node)
        for (i in 0 until node.childCount) {
            val child = node.getChild(i) ?: continue
            collectEditable(child, into, budget)
        }
    }

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
