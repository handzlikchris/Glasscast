package uk.co.reliablesolutions.glassesremote.companion

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.text.InputType
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
                "Each session still asks you here first, and once it runs it no longer needs the PC or the internet.",
        )
        button("Start") { CompanionService.start(this) }
        button("Stop") { CompanionService.stop(this) }
        button("Forget the glasses") {
            prefs.glasses = null
            refresh()
        }

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
