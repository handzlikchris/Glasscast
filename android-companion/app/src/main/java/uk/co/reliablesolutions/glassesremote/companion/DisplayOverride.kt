package uk.co.reliablesolutions.glassesremote.companion

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.hardware.display.DisplayManager
import android.os.Process
import android.view.Display
import org.lsposed.hiddenapibypass.HiddenApiBypass

/**
 * Switches the phone's screen to a square for the glasses (their view is 600×600) and back, the
 * same as `adb shell wm size 1080x1080` and `wm density 320`, but from the phone at any time.
 *
 * It calls the window manager's forced size and density (IWindowManager, hidden API: reached
 * through HiddenApiBypass), which needs WRITE_SECURE_SETTINGS. That is granted once over adb and
 * survives reboots:
 *
 *   adb shell pm grant uk.co.reliablesolutions.glassesremote.companion android.permission.WRITE_SECURE_SETTINGS
 *
 * The square's side is the physical screen's short side; the density is the user's choice
 * (320, 2026-09-29). Reset clears both, back to the phone's own size and density.
 */
object DisplayOverride {
    /** Density for the square screen: more fits on it, still readable on the glasses. */
    const val SQUARE_DENSITY = 320

    /**
     * The phone user this app runs as (0 normally). Not USER_CURRENT (-2), as `wm density` uses:
     * an app asking for that is refused (it needs INTERACT_ACROSS_USERS_FULL; seen on the S25).
     */
    private val user: Int get() = Process.myUid() / 100_000

    const val GRANT_COMMAND =
        "adb shell pm grant uk.co.reliablesolutions.glassesremote.companion android.permission.WRITE_SECURE_SETTINGS"

    fun allowed(context: Context): Boolean =
        context.checkSelfPermission(Manifest.permission.WRITE_SECURE_SETTINGS) == PackageManager.PERMISSION_GRANTED

    /** Square at [SQUARE_DENSITY]; returns what it set, e.g. "1080×1080 at density 320". */
    fun square(context: Context): String {
        val mode = context.getSystemService(DisplayManager::class.java).getDisplay(Display.DEFAULT_DISPLAY).mode
        val side = minOf(mode.physicalWidth, mode.physicalHeight)
        call("setForcedDisplaySize", Display.DEFAULT_DISPLAY, side, side)
        call("setForcedDisplayDensityForUser", Display.DEFAULT_DISPLAY, SQUARE_DENSITY, user)
        return "${side}×$side at density $SQUARE_DENSITY"
    }

    /** Back to the phone's own screen size and density. */
    fun reset() {
        call("clearForcedDisplaySize", Display.DEFAULT_DISPLAY)
        call("clearForcedDisplayDensityForUser", Display.DEFAULT_DISPLAY, user)
    }

    private fun call(name: String, vararg args: Int) {
        HiddenApiBypass.addHiddenApiExemptions("Landroid/view/")
        val wm = Class.forName("android.view.WindowManagerGlobal").getMethod("getWindowManagerService").invoke(null)
            ?: throw IllegalStateException("no window manager")
        val types = Array<Class<*>>(args.size) { Int::class.javaPrimitiveType!! }
        try {
            wm.javaClass.getMethod(name, *types).invoke(wm, *args.toTypedArray())
        } catch (e: java.lang.reflect.InvocationTargetException) {
            throw e.targetException ?: e
        }
    }
}
