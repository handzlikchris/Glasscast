package uk.co.reliablesolutions.glassesremote.companion

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.WindowInsets
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

/**
 * Setup, done once: pair with the PC (approve the code in its popup: that only lets this phone use
 * the PC as a meeting point), turn on the accessibility service, start the companion. The glasses
 * pair with this phone the first time they choose Phone (the same code on both, Approve in a
 * notification here); after that the phone only asks for the screen-capture consent each time.
 */
class MainActivity : Activity() {
    private lateinit var prefs: Prefs
    private lateinit var status: TextView
    private lateinit var code: TextView
    private lateinit var server: EditText
    private lateinit var screen: TextView
    private var pairing: Pairing? = null
    private val watcher: () -> Unit = { runOnUiThread { refresh() } }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        prefs = Prefs(this)

        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(48, 64, 48, 48)
        }
        fun text(value: String, size: Float = 16f, bold: Boolean = false) = TextView(this).apply {
            this.text = value
            textSize = size
            if (bold) setTypeface(typeface, Typeface.BOLD)
            setPadding(0, 12, 0, 12)
        }.also { column.addView(it) }
        fun button(label: String, onClick: () -> Unit) = Button(this).apply {
            text = label
            setOnClickListener { onClick() }
        }.also { column.addView(it) }

        text("Glasses Remote companion", 22f, bold = true)
        status = text("")

        text("1. Pair with the PC", 18f, bold = true)
        server = EditText(this).apply {
            setText(prefs.server)
            inputType = InputType.TYPE_TEXT_VARIATION_URI
            isSingleLine = true
        }.also { column.addView(it) }
        button("Pair") { startPairing() }
        code = text("", 28f, bold = true)

        text("2. Turn on input", 18f, bold = true)
        text(
            "Settings > Accessibility > Installed apps > Glasses Remote: on. If it's greyed out: " +
                "Settings > Apps > Glasses Remote > menu (top right) > Allow restricted settings, then try again.",
        )
        button("Open accessibility settings") { startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)) }

        text("3. Start", 18f, bold = true)
        text(
            "Keeps a connection to the PC so the glasses can reach this phone to start a session. " +
                "The first time, the glasses pair with this phone: approve the code in the notification. " +
                "Each session still asks you here first, and once it runs it no longer needs the PC.",
        )
        button("Start") { CompanionService.start(this) }
        button("Stop") { CompanionService.stop(this) }
        button("Forget the glasses") {
            prefs.glasses = null
            refresh()
        }

        LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            addView(
                TextView(this@MainActivity).apply {
                    text = "Screen for the glasses"
                    textSize = 18f
                    setTypeface(typeface, Typeface.BOLD)
                },
                LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f),
            )
            addView(
                Button(this@MainActivity).apply {
                    text = "ⓘ"
                    contentDescription = "How to allow the square screen"
                    setOnClickListener { showScreenHelp() }
                },
            )
        }.also { column.addView(it) }
        text(
            "Square makes the phone's screen square (the glasses' view is square) with more on it; " +
                "Reset puts it back. Change it before starting a session.",
        )
        button("Square screen") {
            changeScreen { "Square: ${DisplayOverride.square(this)}" }
        }
        button("Reset screen") {
            changeScreen {
                DisplayOverride.reset()
                "Back to the phone's own screen"
            }
        }
        screen = text("")

        // Android 15+ draws apps edge to edge: keep the content clear of the status and navigation bars.
        val scroll = ScrollView(this).apply { addView(column) }
        scroll.setOnApplyWindowInsetsListener { view, insets ->
            val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
            view.setPadding(bars.left, bars.top, bars.right, bars.bottom)
            insets
        }
        setContentView(scroll)

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        }
    }

    override fun onResume() {
        super.onResume()
        CompanionService.watch(watcher)
        refresh()
    }

    override fun onPause() {
        CompanionService.unwatch(watcher)
        super.onPause()
    }

    override fun onDestroy() {
        pairing?.cancel()
        super.onDestroy()
    }

    /** Runs a screen change, or says how to allow it (a one-off permission over adb). */
    private fun changeScreen(change: () -> String) {
        if (!DisplayOverride.allowed(this)) {
            screen.text = "Not allowed yet: tap ⓘ to see how."
            showScreenHelp()
            return
        }
        screen.text = runCatching(change).getOrElse { "Couldn't change the screen: ${it.message ?: it.javaClass.simpleName}" }
    }

    /** How to allow the square screen: a one-off adb command, with a button to copy it. */
    private fun showScreenHelp() {
        val allowed = DisplayOverride.allowed(this)
        AlertDialog.Builder(this)
            .setTitle("Square screen: one-off setup")
            .setMessage(
                (if (allowed) "Allowed on this phone: nothing to do.\n\n" else "") +
                    "Changing the screen size needs a permission Android only gives over adb. It's a " +
                    "one-off: it stays after restarts.\n\n" +
                    "1. On the phone: Settings > Developer options > USB debugging on.\n" +
                    "2. Connect the phone to a PC with adb (Android platform-tools) and run:\n\n" +
                    DisplayOverride.GRANT_COMMAND + "\n\n" +
                    "Then Square screen and Reset screen work here any time, no PC needed.",
            )
            .setPositiveButton("Copy command") { _, _ ->
                getSystemService(ClipboardManager::class.java)
                    .setPrimaryClip(ClipData.newPlainText("adb command", DisplayOverride.GRANT_COMMAND))
            }
            .setNegativeButton("Close", null)
            .show()
    }

    private fun refresh() {
        status.text = listOf(
            "Companion: ${CompanionService.status}",
            "Paired with the PC: ${if (prefs.token != null) "yes" else "no"}",
            "Glasses paired: ${if (prefs.glasses != null) "yes" else "no (they pair the first time they choose Phone)"}",
            "Input (accessibility): ${if (InputService.instance != null) "on" else "off"}",
        ).joinToString("\n")
    }

    private fun startPairing() {
        val url = server.text.toString().trim()
        if (!url.startsWith("wss://")) {
            code.text = "The address must start with wss://"
            return
        }
        prefs.server = url
        pairing?.cancel()
        code.text = "Contacting the PC…"
        code.visibility = View.VISIBLE
        pairing = Pairing(url, Build.MODEL.take(32), object : Pairing.Listener {
            override fun onCode(code: String, expiresInSeconds: Int) = runOnUiThread {
                this@MainActivity.code.text = "On the PC, approve if it shows\n$code"
            }

            override fun onPaired(token: String) = runOnUiThread {
                prefs.token = token
                code.text = "Paired"
                refresh()
            }

            override fun onFailed(reason: String) = runOnUiThread { code.text = reason }
        }).also { it.start() }
    }
}
