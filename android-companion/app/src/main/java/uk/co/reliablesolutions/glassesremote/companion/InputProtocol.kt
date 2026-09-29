package uk.co.reliablesolutions.glassesremote.companion

import org.json.JSONException
import org.json.JSONObject

/** Global actions the glasses may ask for (AccessibilityService.performGlobalAction). */
enum class NavAction { BACK, HOME, RECENTS, NOTIFICATIONS }

enum class KeyName { ENTER, BACKSPACE }

enum class TouchPhase { DOWN, MOVE, UP }

/** A validated message from the glasses on the DataChannel. Positions are 0..1 within the frame they see. */
sealed interface InputCommand {
    data class Tap(val x: Double, val y: Double) : InputCommand
    data class LongPress(val x: Double, val y: Double) : InputCommand
    data class DoubleTap(val x: Double, val y: Double) : InputCommand
    /** A finger held down (a pinch held still), moved with the drag, lifted on release. */
    data class Touch(val phase: TouchPhase, val x: Double, val y: Double) : InputCommand
    data class Swipe(val x1: Double, val y1: Double, val x2: Double, val y2: Double, val ms: Long) : InputCommand
    data class Nav(val action: NavAction) : InputCommand
    data class TypeText(val text: String) : InputCommand
    data class Key(val key: KeyName) : InputCommand
    /** The crop, 0..1 of the phone's screen. Stops following a window. */
    data class SetRegion(val x: Double, val y: Double, val width: Double, val height: Double) : InputCommand
    /** Crop to the top app window (a pop-up view) and keep following it. */
    data object FitWindow : InputCommand
    /** The previous (older) or next (newer) recently used app; Fit follows it. */
    data class SwitchApp(val previous: Boolean) : InputCommand
    /** The glasses' heartbeat: the phone ends a session it stops hearing from. */
    data class Ping(val t: Double) : InputCommand
    /** End on the glasses. */
    data object End : InputCommand
}

/**
 * Strict parser for the glasses' DataChannel messages, in the style of the PC's ControlProtocol:
 * a small JSON object, a known type, exactly its properties, numbers clamped, text capped and
 * flattened. Anything else is rejected. Keep in sync with client-web/src/phoneProtocol.ts.
 */
object InputProtocol {
    const val MAX_MESSAGE_CHARS = 4096
    const val MAX_TEXT_LENGTH = 500
    const val SWIPE_MIN_MS = 50L
    const val SWIPE_MAX_MS = 2000L
    /** Smallest crop side, as a share of the screen. */
    const val MIN_REGION = 0.1

    private val NAV = mapOf(
        "back" to NavAction.BACK,
        "home" to NavAction.HOME,
        "recents" to NavAction.RECENTS,
        "notifications" to NavAction.NOTIFICATIONS,
    )
    private val KEYS = mapOf("Enter" to KeyName.ENTER, "Backspace" to KeyName.BACKSPACE)
    private val PHASES = mapOf("down" to TouchPhase.DOWN, "move" to TouchPhase.MOVE, "up" to TouchPhase.UP)

    fun parse(raw: String): InputCommand? {
        if (raw.isEmpty() || raw.length > MAX_MESSAGE_CHARS) return null
        val o = try {
            JSONObject(raw)
        } catch (e: JSONException) {
            return null
        }
        val type = o.opt("type") as? String ?: return null
        return when (type) {
            "tap" -> if (only(o, "x", "y")) point(o) { x, y -> InputCommand.Tap(x, y) } else null
            "longPress" -> if (only(o, "x", "y")) point(o) { x, y -> InputCommand.LongPress(x, y) } else null
            "doubleTap" -> if (only(o, "x", "y")) point(o) { x, y -> InputCommand.DoubleTap(x, y) } else null
            "touch" -> touch(o)
            "swipe" -> swipe(o)
            "nav" -> if (only(o, "action")) (o.opt("action") as? String)?.let(NAV::get)?.let { InputCommand.Nav(it) } else null
            "typeText" -> typeText(o)
            "key" -> if (only(o, "key")) (o.opt("key") as? String)?.let(KEYS::get)?.let { InputCommand.Key(it) } else null
            "setRegion" -> region(o)
            "fitWindow" -> if (only(o)) InputCommand.FitWindow else null
            "switchApp" -> if (only(o, "dir")) {
                when (o.opt("dir")) {
                    "previous" -> InputCommand.SwitchApp(previous = true)
                    "next" -> InputCommand.SwitchApp(previous = false)
                    else -> null
                }
            } else {
                null
            }
            "ping" -> if (only(o, "t")) num(o, "t")?.let { InputCommand.Ping(it) } else null
            "end" -> if (only(o)) InputCommand.End else null
            else -> null
        }
    }

    /** Line breaks and tabs become spaces (text never presses Enter); other control characters go. */
    fun flatten(text: String): String {
        val sb = StringBuilder(text.length)
        for (c in text) {
            when {
                c == '\r' || c == '\n' || c == '\t' || c.code == 0x2028 || c.code == 0x2029 -> sb.append(' ')
                !Character.isISOControl(c) -> sb.append(c)
            }
        }
        return sb.toString()
    }

    private fun only(o: JSONObject, vararg allowed: String): Boolean {
        val keys = o.keys()
        while (keys.hasNext()) {
            val key = keys.next()
            if (key != "type" && key !in allowed) return false
        }
        return true
    }

    private fun num(o: JSONObject, name: String): Double? {
        val v = o.opt(name) as? Number ?: return null
        val d = v.toDouble()
        return if (d.isFinite()) d else null
    }

    private fun unit(o: JSONObject, name: String): Double? = num(o, name)?.coerceIn(0.0, 1.0)

    private inline fun point(o: JSONObject, make: (Double, Double) -> InputCommand): InputCommand? {
        val x = unit(o, "x") ?: return null
        val y = unit(o, "y") ?: return null
        return make(x, y)
    }

    private fun touch(o: JSONObject): InputCommand? {
        if (!only(o, "phase", "x", "y")) return null
        val phase = (o.opt("phase") as? String)?.let(PHASES::get) ?: return null
        return point(o) { x, y -> InputCommand.Touch(phase, x, y) }
    }

    private fun swipe(o: JSONObject): InputCommand? {
        if (!only(o, "x1", "y1", "x2", "y2", "ms")) return null
        val ms = num(o, "ms") ?: return null
        return InputCommand.Swipe(
            unit(o, "x1") ?: return null,
            unit(o, "y1") ?: return null,
            unit(o, "x2") ?: return null,
            unit(o, "y2") ?: return null,
            ms.toLong().coerceIn(SWIPE_MIN_MS, SWIPE_MAX_MS),
        )
    }

    private fun typeText(o: JSONObject): InputCommand? {
        if (!only(o, "text")) return null
        val text = o.opt("text") as? String ?: return null
        if (text.length > MAX_TEXT_LENGTH) return null
        val flat = flatten(text)
        return if (flat.isBlank()) null else InputCommand.TypeText(flat)
    }

    private fun region(o: JSONObject): InputCommand? {
        if (!only(o, "x", "y", "width", "height")) return null
        val width = (num(o, "width") ?: return null).coerceIn(MIN_REGION, 1.0)
        val height = (num(o, "height") ?: return null).coerceIn(MIN_REGION, 1.0)
        val x = (num(o, "x") ?: return null).coerceIn(0.0, 1.0 - width)
        val y = (num(o, "y") ?: return null).coerceIn(0.0, 1.0 - height)
        return InputCommand.SetRegion(x, y, width, height)
    }
}
