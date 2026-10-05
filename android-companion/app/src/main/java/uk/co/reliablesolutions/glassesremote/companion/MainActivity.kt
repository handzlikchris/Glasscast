package uk.co.reliablesolutions.glassesremote.companion

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.content.res.Configuration
import android.graphics.Color
import android.graphics.Point
import android.graphics.Typeface
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.hardware.display.DisplayManager
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.text.InputType
import android.util.TypedValue
import android.view.Display
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
 *
 * Plain platform views (no AndroidX), laid out as Material-style cards: a status card on top, then
 * one card per step. Colours come from the device theme (light or dark).
 */
class MainActivity : Activity() {
    private lateinit var prefs: Prefs
    private lateinit var code: TextView
    private lateinit var server: EditText
    private lateinit var screen: TextView
    private lateinit var runButton: Button
    private lateinit var pairButton: Button
    private lateinit var pairCard: View
    private lateinit var unpairButton: Button
    private val statusRows = mutableMapOf<String, Pair<TextView, TextView>>()
    private var pairing: Pairing? = null
    private val watcher: () -> Unit = { runOnUiThread { refresh() } }

    // The look: colours from the theme, a few fixed state colours.
    private val dark by lazy {
        resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK == Configuration.UI_MODE_NIGHT_YES
    }
    private val accent by lazy { themeColor(android.R.attr.colorAccent, Color.rgb(0x3D, 0x8B, 0xFD)) }
    private val textPrimary by lazy { themeColor(android.R.attr.textColorPrimary, if (dark) Color.WHITE else Color.BLACK) }
    private val textSecondary by lazy { themeColor(android.R.attr.textColorSecondary, Color.GRAY) }
    private val card by lazy { if (dark) 0x1FFFFFFF else 0x0F000000 }
    private val good = Color.rgb(0x3D, 0xB8, 0x6B)
    private val warn = Color.rgb(0xE8, 0xA3, 0x3D)
    private val bad = Color.rgb(0xE5, 0x5B, 0x55)
    private val idle = Color.rgb(0x8A, 0x8F, 0x98)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        prefs = Prefs(this)

        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(20), dp(16), dp(24))
        }

        column.addView(text("Glasscast", 26f, bold = true))
        column.addView(text("Lets your Meta glasses see and control this phone.", 15f, secondary = true), margins(top = 2))

        // Status: one row per thing that has to be right, with a coloured dot.
        column.addView(
            card(
                statusRow("companion", "Companion"),
                statusRow("pc", "PC"),
                statusRow("glasses", "Glasses"),
                statusRow("input", "Input"),
                statusRow("screen", "Screen"),
            ),
            margins(top = 16),
        )

        // Pair with the PC.
        server = EditText(this).apply {
            setText(prefs.server)
            inputType = InputType.TYPE_TEXT_VARIATION_URI
            hint = "wss://your-server/ws/companion"
            isSingleLine = true
            textSize = 14f
        }
        code = text("", 22f, bold = true).apply { visibility = View.GONE }
        // Shown only until the phone is paired: then Run is the first card, and Unpair is in it.
        pairCard = card(
            title("Pair with the PC"),
            body("The PC is only the meeting point the glasses use to reach this phone. Approve the code in its popup."),
            server,
            buttonRow(filledButton("Pair") { startPairing() }.also { pairButton = it }),
            code,
        )
        column.addView(pairCard, margins(top = 16))

        // Run: Start / Stop, first once paired.
        runButton = filledButton("") { if (CompanionService.running) CompanionService.stop(this) else CompanionService.start(this) }
        column.addView(
            card(
                title("Run"),
                body(
                    "Keeps this phone reachable for the glasses. The first time, the glasses pair with this phone: " +
                        "approve the code in the notification. Each session still asks you here first.",
                ),
                buttonRow(runButton),
                buttonRow(
                    textButton("Forget the glasses") {
                        prefs.glasses = null
                        refresh()
                    },
                    textButton("Unpair from the PC") { confirmUnpair() }.also { unpairButton = it },
                ),
            ),
            margins(top = 16),
        )

        // Input.
        column.addView(
            card(
                title("Allow input"),
                body(
                    "Settings > Accessibility > Installed apps > Glasscast: on. If it's greyed out: " +
                        "Settings > Apps > Glasscast > menu (top right) > Allow restricted settings, then try again.",
                ),
                buttonRow(tonalButton("Open accessibility settings") { startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)) }),
            ),
            margins(top = 16),
        )

        // Screen for the glasses.
        screen = body("").apply { visibility = View.GONE }
        column.addView(
            card(
                title("Screen for the glasses", info = { showScreenHelp() }),
                body("Square fills the glasses' square view, with more on it. Reset puts the phone back. Change it before a session."),
                buttonRow(
                    tonalButton("Square screen") { changeScreen { "Square: ${DisplayOverride.square(this)}" } },
                    tonalButton("Reset screen") {
                        changeScreen {
                            DisplayOverride.reset()
                            "Back to the phone's own screen"
                        }
                    },
                ),
                screen,
            ),
            margins(top = 16),
        )

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

    private fun refresh() {
        val running = CompanionService.running
        // The service's status is a sentence ("Ready: the glasses can …"); the card shows its first part.
        setStatus("companion", if (running) CompanionService.status.substringBefore(':') else "Stopped", if (running) good else idle)
        setStatus("pc", if (prefs.token != null) "Paired" else "Not paired", if (prefs.token != null) good else warn)
        setStatus(
            "glasses",
            if (prefs.glasses != null) "Paired" else "Pair on first use",
            if (prefs.glasses != null) good else idle,
        )
        val input = InputService.instance != null
        setStatus("input", if (input) "On" else "Off: turn it on below", if (input) good else bad)
        val size = Point().also {
            @Suppress("DEPRECATION")
            getSystemService(DisplayManager::class.java).getDisplay(Display.DEFAULT_DISPLAY).getRealSize(it)
        }
        val square = size.x == size.y
        setStatus(
            "screen",
            if (square) "Square ${size.x}×${size.y} · ${resources.configuration.densityDpi} dpi" else "Phone's own",
            if (square) good else idle,
        )
        runButton.text = if (running) "■  Stop" else "▶  Start"
        // Not paired: the Pair card leads. Paired: it goes, Run leads, and Unpair takes its place.
        val paired = prefs.token != null
        pairCard.visibility = if (paired) View.GONE else View.VISIBLE
        unpairButton.visibility = if (paired) View.VISIBLE else View.GONE
    }

    /** Forgets the PC's companion token here (the PC still lists the phone until Forget phone in its tray). */
    private fun confirmUnpair() {
        AlertDialog.Builder(this)
            .setTitle("Unpair from the PC?")
            .setMessage(
                "The companion stops and forgets this PC. To use the glasses again, pair with the PC again " +
                    "(approve the code in its popup). On the PC, Forget phone in the tray removes it there too.",
            )
            .setPositiveButton("Unpair") { _, _ ->
                if (CompanionService.running) CompanionService.stop(this)
                prefs.token = null
                refresh()
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    /** Runs a screen change, or says how to allow it (a one-off permission over adb). */
    private fun changeScreen(change: () -> String) {
        screen.visibility = View.VISIBLE
        if (!DisplayOverride.allowed(this)) {
            screen.text = "Not allowed yet: tap ⓘ to see how."
            showScreenHelp()
            return
        }
        screen.text = runCatching(change).getOrElse { "Couldn't change the screen: ${it.message ?: it.javaClass.simpleName}" }
        screen.postDelayed({ refresh() }, 800)
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

    private fun startPairing() {
        val url = server.text.toString().trim()
        code.visibility = View.VISIBLE
        if (!url.startsWith("wss://")) {
            code.text = "The address must start with wss://"
            return
        }
        prefs.server = url
        pairing?.cancel()
        code.text = "Contacting the PC…"
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

    // ---- building blocks ----

    private fun dp(value: Int) = (value * resources.displayMetrics.density).toInt()

    private fun themeColor(attr: Int, fallback: Int): Int {
        val value = TypedValue()
        if (!theme.resolveAttribute(attr, value, true)) return fallback
        return if (value.resourceId != 0) runCatching { getColor(value.resourceId) }.getOrDefault(fallback) else value.data
    }

    private fun margins(top: Int = 0) =
        LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
            .apply { topMargin = dp(top) }

    private fun text(value: String, size: Float, bold: Boolean = false, secondary: Boolean = false) = TextView(this).apply {
        text = value
        textSize = size
        setTextColor(if (secondary) textSecondary else textPrimary)
        if (bold) setTypeface(typeface, Typeface.BOLD)
    }

    private fun body(value: String) = text(value, 14f, secondary = true).apply { setLineSpacing(0f, 1.15f) }

    /** A card's title; [info] adds an ⓘ at its right. */
    private fun title(value: String, info: (() -> Unit)? = null): View {
        val label = text(value, 18f, bold = true)
        if (info == null) return label
        return LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            addView(label, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
            addView(
                textButton("ⓘ") { info() }.apply {
                    contentDescription = "How to allow the square screen"
                    minHeight = dp(36)
                    minimumHeight = dp(36)
                    minWidth = dp(44)
                    minimumWidth = dp(44)
                    setPadding(dp(8), 0, dp(8), 0)
                },
            )
        }
    }

    /** A rounded card holding [children], spaced out. */
    private fun card(vararg children: View) = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(dp(16), dp(14), dp(16), dp(16))
        background = GradientDrawable().apply {
            cornerRadius = dp(20).toFloat()
            setColor(card)
        }
        children.forEachIndexed { i, child -> addView(child, margins(top = if (i == 0) 0 else 8)) }
    }

    /** A status line: a coloured dot, the label, and the value at the right. */
    private fun statusRow(key: String, label: String): View {
        val dot = text("●", 14f)
        val value = text("", 14f, secondary = true).apply { gravity = Gravity.END }
        statusRows[key] = dot to value
        return LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            addView(dot)
            addView(
                text(label, 15f),
                LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT)
                    .apply { marginStart = dp(10) },
            )
            addView(value, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply { marginStart = dp(12) })
        }
    }

    private fun setStatus(key: String, value: String, color: Int) {
        val (dot, text) = statusRows[key] ?: return
        dot.setTextColor(color)
        text.text = value
    }

    private fun pill(fill: Int): Drawable {
        val shape = GradientDrawable().apply {
            cornerRadius = dp(24).toFloat()
            setColor(fill)
        }
        val mask = GradientDrawable().apply {
            cornerRadius = dp(24).toFloat()
            setColor(Color.WHITE)
        }
        return RippleDrawable(ColorStateList.valueOf(0x33FFFFFF), shape, mask)
    }

    private fun baseButton(label: String, onClick: () -> Unit) = Button(this).apply {
        text = label
        isAllCaps = false
        textSize = 15f
        stateListAnimator = null
        minHeight = dp(48)
        setOnClickListener { onClick() }
    }

    /** The main action: filled with the accent colour, its text dark or light to stand out on it. */
    private fun filledButton(label: String, onClick: () -> Unit) = baseButton(label, onClick).apply { styleFilled(this) }

    private fun styleFilled(button: Button) = button.apply {
        background = pill(accent)
        setTextColor(if (Color.luminance(accent) > 0.5f) Color.rgb(0x10, 0x1B, 0x33) else Color.WHITE)
        setTypeface(Typeface.DEFAULT_BOLD)
    }

    /** A secondary action: a lighter pill. */
    private fun tonalButton(label: String, onClick: () -> Unit) = baseButton(label, onClick).apply { styleTonal(this) }

    private fun styleTonal(button: Button) = button.apply {
        background = pill(if (dark) 0x26FFFFFF else 0x14000000)
        setTextColor(textPrimary)
        setTypeface(Typeface.DEFAULT)
    }

    /** A quiet action: text only. */
    private fun textButton(label: String, onClick: () -> Unit) = baseButton(label, onClick).apply {
        background = pill(Color.TRANSPARENT)
        setTextColor(accent)
    }

    /** Buttons side by side, sharing the width. */
    private fun buttonRow(vararg buttons: Button) = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        buttons.forEachIndexed { i, button ->
            addView(
                button,
                LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply { if (i > 0) marginStart = dp(8) },
            )
        }
    }
}
