package com.amak.phonebridge

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.content.res.Configuration
import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.Outline
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.ViewOutlineProvider
import android.view.animation.DecelerateInterpolator
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast

/**
 * Nyx on the phone, in the laptop app's Nothing-style system: black canvas (or light,
 * following the phone), rounded tiles, dot-matrix type, one red accent.
 *
 * From the top: the link status, anything still to set up (it disappears when done),
 * a laptop request to approve, laptop controls, the picture the laptop shows, and the
 * laptop's on/off log.
 */
class MainActivity : Activity() {

    /** Design tokens, the same values as desktop/Colors.Dark.xaml and Colors.Light.xaml. */
    private class Palette(dark: Boolean) {
        val canvas = if (dark) Color.parseColor("#000000") else Color.parseColor("#F2F2F2")
        val tile = if (dark) Color.parseColor("#111111") else Color.parseColor("#FFFFFF")
        val tile2 = if (dark) Color.parseColor("#1C1C1C") else Color.parseColor("#F0F0F0")
        val line = if (dark) Color.parseColor("#242424") else Color.parseColor("#E4E4E4")
        val ink = if (dark) Color.parseColor("#FFFFFF") else Color.parseColor("#0A0A0A")
        val onInk = if (dark) Color.parseColor("#000000") else Color.parseColor("#FFFFFF")
        val text2 = if (dark) Color.parseColor("#8C8C8C") else Color.parseColor("#6B6B6B")
        val dotOff = if (dark) Color.parseColor("#1E1E1E") else Color.parseColor("#E2E2E2")
        val red = Color.parseColor("#EB2F37")
        val ok = if (dark) Color.parseColor("#3DDC84") else Color.parseColor("#16A34A")
        val dark = dark
    }

    private lateinit var P: Palette

    private companion object {
        const val PICK_PICTURE = 42
        const val PICK_FILES = 43
    }

    private val handler = Handler(Looper.getMainLooper())
    private val tick = object : Runnable {
        override fun run() {
            refresh()
            handler.postDelayed(this, 1000)
        }
    }

    private fun dp(v: Int): Int = (v * resources.displayMetrics.density + 0.5f).toInt()
    private fun dpf(v: Float): Float = v * resources.displayMetrics.density

    // ------------------------------------------------------------------ views kept

    private lateinit var headPill: TextView
    private lateinit var headDot: PulseDot
    private lateinit var heroWord: DotTextView
    private lateinit var heroName: TextView
    private lateinit var heroDetail: TextView
    private lateinit var bridgeSwitch: PillSwitch
    private lateinit var bridgeLabel: TextView

    private lateinit var setupTile: LinearLayout
    private lateinit var requestsTile: LinearLayout
    private lateinit var requestsRows: LinearLayout
    private var requestsKey = ""
    private lateinit var setupRows: LinearLayout

    private lateinit var approvalBox: LinearLayout
    private lateinit var approvalText: TextView

    private val controlTiles = mutableListOf<View>()
    private lateinit var wakeTile: View
    private lateinit var laptopResult: TextView

    private lateinit var wallThumb: ImageView
    private lateinit var pictureText: TextView
    private lateinit var realWallpaper: TextView

    private lateinit var logList: LinearLayout

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val dark = (resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES
        P = Palette(dark)
        window.statusBarColor = P.canvas
        window.navigationBarColor = P.canvas
        @Suppress("DEPRECATION")
        window.decorView.systemUiVisibility = if (dark) 0 else
            View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR or View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR

        val root = LinearLayout(this)
        root.orientation = LinearLayout.VERTICAL
        root.setPadding(dp(16), dp(20), dp(16), dp(32))

        root.addView(header(), margins(bottom = 18))
        root.addView(hero())
        root.addView(approval(), margins(top = 10))
        root.addView(setup(), margins(top = 10))
        root.addView(requests(), margins(top = 10))
        root.addView(controls(), margins(top = 10))
        root.addView(uses(), margins(top = 10))
        root.addView(picture(), margins(top = 10))
        root.addView(log(), margins(top = 10))
        root.addView(text("Nyx " + versionName() + "  ·  No account, no cloud. Everything stays between your phone and laptop.", 12f, P.text2).also {
            it.gravity = Gravity.CENTER
        }, margins(top = 20))

        val scroll = ScrollView(this)
        scroll.setBackgroundColor(P.canvas)
        scroll.isFillViewport = true
        scroll.fitsSystemWindows = true
        scroll.isVerticalScrollBarEnabled = false
        scroll.addView(root)
        setContentView(scroll)

        // tiles rise into place one after another
        for (i in 0 until root.childCount) {
            val v = root.getChildAt(i)
            v.alpha = 0f
            v.translationY = dpf(18f)
            v.animate().alpha(1f).translationY(0f).setStartDelay(40L * i).setDuration(420)
                .setInterpolator(DecelerateInterpolator(1.8f)).start()
        }
        handleMicStart(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleMicStart(intent)
    }

    // ------------------------------------------------------------ microphone

    private val MIC_PERM = 52
    private lateinit var micButton: TextView // the microphone tile's label

    /** The laptop's "Use as microphone" notification was tapped: start (Nyx is on screen now, so Android allows it). */
    private fun handleMicStart(i: Intent?) {
        if (i?.action != MicService.ACTION_START) return
        i.action = null
        startMic()
    }

    private fun startMic() {
        if (checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(android.Manifest.permission.RECORD_AUDIO), MIC_PERM)
            return
        }
        MicService.start(this)
        micButton.postDelayed({ refreshMicButton() }, 400)
    }

    private fun toggleMic() {
        if (MicService.running) MicService.stop(this) else startMic()
        micButton.postDelayed({ refreshMicButton() }, 400)
    }

    private lateinit var controlButton: TextView

    private fun refreshMicButton() {
        if (::controlButton.isInitialized)
            controlButton.visibility = if (ControlService.enabled(this) || ControlSwitch.canToggle(this)) View.GONE else View.VISIBLE
        if (::micButton.isInitialized) micButton.text = if (MicService.running) "Stop mic" else "Microphone"
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == MIC_PERM) {
            if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) startMic()
            else toast("Nyx needs the microphone permission to be the laptop's microphone.")
        }
    }

    override fun onResume() {
        super.onResume()
        refreshMicButton()
        // the bridge should be on but isn't (killed, or an update stopped it): bring it back
        if (bridgeRunning()) BridgeRestart.remember(this, true)
        else if (BridgeRestart.wanted(this)) toggleBridge()
        handler.post(tick)
        LinkService.onLaptopEvent = { onLaptopEvent(it) }
        renderLaptop()
        renderApproval()
        loadThumb()
        LinkService.instance?.requestPowerLog()
        // back from the All files access switch: send the real wallpaper straight away
        val canRead = Wallpaper.canReadWallpaper()
        if (canRead && !hadWallpaperAccess) LinkService.instance?.sendWallpaper()
        hadWallpaperAccess = canRead
        // ask once, after the app is set up: the laptop's phone card looks empty without it
        val ui = getSharedPreferences("ui", MODE_PRIVATE)
        if (!canRead && !Wallpaper.hasCustom(this) && Build.VERSION.SDK_INT >= 30 &&
            LinkService.instance != null && !ui.getBoolean("asked_wallpaper", false)) {
            ui.edit().putBoolean("asked_wallpaper", true).apply()
            askWallpaperAccess()
        }
    }

    private var hadWallpaperAccess = false

    override fun onPause() {
        super.onPause()
        handler.removeCallbacks(tick)
        LinkService.onLaptopEvent = null
    }

    // ---------------------------------------------------------------- sections

    private fun header(): View {
        val row = LinearLayout(this)
        row.orientation = LinearLayout.HORIZONTAL
        row.gravity = Gravity.CENTER_VERTICAL
        val logo = DotTextView(this, 3.4f)
        logo.color = P.ink
        logo.text = "NYX"
        row.addView(logo)
        row.addView(View(this), LinearLayout.LayoutParams(0, 1, 1f))

        val pill = LinearLayout(this)
        pill.orientation = LinearLayout.HORIZONTAL
        pill.gravity = Gravity.CENTER_VERTICAL
        pill.setPadding(dp(8), 0, dp(14), 0)
        pill.background = rounded(P.tile, P.line, 18)
        headDot = PulseDot(this)
        pill.addView(headDot, LinearLayout.LayoutParams(dp(20), dp(20)))
        headPill = text("", 12f, P.ink, bold = true)
        headPill.letterSpacing = 0.08f
        pill.addView(headPill, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT).also { it.marginStart = dp(4) })
        // tap: choose how to link, Wi-Fi or the USB cable (the laptop does the switching)
        pill.isClickable = true
        pill.contentDescription = "Choose Wi-Fi or USB link"
        pill.setOnClickListener { chooseLink() }
        pressable(pill)
        row.addView(pill, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, dp(36)))
        return row
    }

    private fun chooseLink() {
        val svc = LinkService.instance
        if (svc == null || LinkService.linkKind.isEmpty()) {
            toast("Not linked yet. Once the laptop is linked, you can pick Wi-Fi or the USB cable here.")
            return
        }
        // a Nyx sheet from the bottom: same tiles, an icon per way, a red dot on the one in use
        val dialog = android.app.Dialog(this)
        dialog.requestWindowFeature(android.view.Window.FEATURE_NO_TITLE)
        val sheet = tile()
        sheet.addView(caption("Link to the laptop"))
        fun option(icon: Int, title: String, sub: String, kind: String) {
            val current = if (kind == "usb") LinkService.linkKind == "USB" else LinkService.linkKind == "Wi-Fi"
            val row = LinearLayout(this)
            row.orientation = LinearLayout.HORIZONTAL
            row.gravity = Gravity.CENTER_VERTICAL
            row.setPadding(dp(12), dp(12), dp(14), dp(12))
            row.background = RippleDrawable(ColorStateList.valueOf(P.line), rounded(if (current) P.tile2 else P.tile, 0, 18), null)
            row.isClickable = true
            val iconBox = android.widget.FrameLayout(this)
            iconBox.background = rounded(if (current) P.tile else P.tile2, 0, 20)
            val img = ImageView(this)
            img.setImageResource(icon)
            img.imageTintList = ColorStateList.valueOf(P.ink)
            iconBox.addView(img, android.widget.FrameLayout.LayoutParams(dp(20), dp(20), Gravity.CENTER))
            row.addView(iconBox, LinearLayout.LayoutParams(dp(40), dp(40)))
            val words = LinearLayout(this)
            words.orientation = LinearLayout.VERTICAL
            words.addView(text(title, 16f, P.ink, bold = true))
            words.addView(text(sub, 13f, P.text2), margins(top = 2))
            row.addView(words, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).also { it.marginStart = dp(14) })
            if (current) {
                val dot = View(this)
                dot.background = android.graphics.drawable.GradientDrawable().also { it.shape = android.graphics.drawable.GradientDrawable.OVAL; it.setColor(P.red) }
                row.addView(dot, LinearLayout.LayoutParams(dp(9), dp(9)).also { it.marginStart = dp(10) })
            }
            row.setOnClickListener {
                dialog.dismiss()
                if (current) return@setOnClickListener
                svc.send(org.json.JSONObject().put("t", "link_prefer").put("kind", kind))
                Requests.add("link_" + kind, if (kind == "usb") "Switch to USB cable" else "Switch to Wi-Fi")
                renderRequests()
            }
            pressable(row)
            sheet.addView(row, margins(top = 10))
        }
        option(R.drawable.ic_wifi, "Wi-Fi", "Links by itself on the same Wi-Fi", "wifi")
        option(R.drawable.ic_plug, "USB cable", "Through the cable, steady and fast", "usb")
        val frame = android.widget.FrameLayout(this)
        frame.setPadding(dp(12), 0, dp(12), dp(16))
        frame.addView(sheet)
        dialog.setContentView(frame)
        dialog.window?.let { w ->
            w.setBackgroundDrawable(android.graphics.drawable.ColorDrawable(android.graphics.Color.TRANSPARENT))
            w.setGravity(Gravity.BOTTOM)
            w.setLayout(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
            w.setWindowAnimations(android.R.style.Animation_InputMethod) // slides up from the bottom
        }
        dialog.show()
    }

    private fun hero(): View {
        val t = tile()
        t.addView(caption("Laptop"))
        heroWord = DotTextView(this, 5.2f)
        heroWord.color = P.ink
        heroWord.offColor = P.dotOff
        t.addView(heroWord, wrap(top = 14))
        heroName = text("", 22f, P.ink, bold = true)
        t.addView(heroName, margins(top = 16))
        heroDetail = text("", 14f, P.text2)
        t.addView(heroDetail, margins(top = 4))

        val sw = LinearLayout(this)
        sw.orientation = LinearLayout.HORIZONTAL
        sw.gravity = Gravity.CENTER_VERTICAL
        sw.setPadding(dp(16), dp(10), dp(12), dp(10))
        sw.background = rounded(P.tile2, 0, 18)
        val labels = LinearLayout(this)
        labels.orientation = LinearLayout.VERTICAL
        labels.addView(text("Bridge", 15f, P.ink, bold = true))
        bridgeLabel = text("", 12f, P.text2)
        labels.addView(bridgeLabel)
        sw.addView(labels, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        bridgeSwitch = PillSwitch(this).also {
            it.ink = P.ink; it.track = P.line; it.canvasColor = P.onInk; it.red = P.red
            it.onToggle = { toggleBridge() }
        }
        sw.addView(bridgeSwitch)
        sw.setOnClickListener { toggleBridge() }
        t.addView(sw, margins(top = 18))
        return t
    }

    /** "Allow this laptop?" — the first time a laptop links over Bluetooth. Nothing is shared until you allow it. */
    private fun approval(): View {
        approvalBox = LinearLayout(this)
        approvalBox.orientation = LinearLayout.VERTICAL
        approvalBox.setPadding(dp(20), dp(18), dp(20), dp(18))
        approvalBox.background = rounded(P.tile, P.red, 26)
        approvalBox.addView(caption("Request", P.red))
        approvalText = text("", 17f, P.ink, bold = true)
        approvalBox.addView(approvalText, margins(top = 8))
        approvalBox.addView(text("It will see your notifications, texts and contacts, and can make calls and texts.", 13f, P.text2), margins(top = 6))
        val ab = LinearLayout(this)
        ab.orientation = LinearLayout.HORIZONTAL
        val deny = button("Deny", primary = false)
        val allow = button("Allow", primary = true)
        deny.setOnClickListener { answerApproval(false) }
        allow.setOnClickListener { answerApproval(true) }
        ab.addView(deny, LinearLayout.LayoutParams(0, dp(48), 1f).also { it.marginEnd = dp(8) })
        ab.addView(allow, LinearLayout.LayoutParams(0, dp(48), 1f))
        approvalBox.addView(ab, margins(top = 14))
        approvalBox.visibility = View.GONE
        return approvalBox
    }

    private fun setup(): View {
        setupTile = tile()
        setupTile.addView(caption("Set up"))
        setupRows = LinearLayout(this)
        setupRows.orientation = LinearLayout.VERTICAL
        setupTile.addView(setupRows, margins(top = 6))
        return setupTile
    }

    /** "Requests to laptop": only there while something you asked for is on its way or just finished. */
    private fun requests(): View {
        requestsTile = tile()
        requestsTile.addView(caption("Requests to laptop"))
        requestsRows = LinearLayout(this)
        requestsRows.orientation = LinearLayout.VERTICAL
        requestsTile.addView(requestsRows, margins(top = 4))
        requestsTile.visibility = View.GONE
        return requestsTile
    }

    private fun renderRequests() {
        Requests.expire()
        val now = System.currentTimeMillis()
        val list = Requests.items.toList()
        val key = list.joinToString("|") { "${it.id}:${it.state}:${it.msg}:${it.cancelUntil > now}" }
        if (key == requestsKey) return
        requestsKey = key
        requestsRows.removeAllViews()
        requestsTile.visibility = if (list.isEmpty()) View.GONE else View.VISIBLE
        for (item in list) {
            val row = LinearLayout(this)
            row.orientation = LinearLayout.HORIZONTAL
            row.gravity = Gravity.CENTER_VERTICAL
            val words = LinearLayout(this)
            words.orientation = LinearLayout.VERTICAL
            words.addView(text(item.label, 15f, P.ink, bold = true))
            val (stateText, color) = when (item.state) {
                Requests.WAITING -> "Waiting for the laptop…" to P.text2
                Requests.DONE -> (item.msg.ifEmpty { "Done" }) to P.ok
                else -> ("Didn't work. " + item.msg).trim() to P.red
            }
            words.addView(text(stateText, 13f, color), margins(top = 2))
            row.addView(words, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
            if (item.cancelUntil > now) {
                val cancel = button("Cancel", primary = false, small = true)
                cancel.setOnClickListener {
                    LinkService.instance?.laptopCommand("cancel")
                    item.cancelUntil = 0
                    item.msg = "Cancelling…"
                    renderRequests()
                }
                row.addView(cancel, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, dp(36)).also { it.marginStart = dp(10) })
            }
            requestsRows.addView(row, margins(top = 12))
        }
    }

    private fun controls(): View {
        val t = tile()
        t.addView(caption("Control the laptop"))
        val rows = listOf(
            listOf(Triple("Lock", "lock", R.drawable.ic_lock), Triple("Sleep", "sleep", R.drawable.ic_moon), Triple("Wake", "wake", R.drawable.ic_sun)),
            listOf(Triple("Restart", "restart", R.drawable.ic_restart), Triple("Shut down", "shutdown", R.drawable.ic_power))
        )
        for ((i, row) in rows.withIndex()) {
            val r = LinearLayout(this)
            r.orientation = LinearLayout.HORIZONTAL
            for ((j, item) in row.withIndex()) {
                val (label, action, icon) = item
                val b = iconTile(label, icon, danger = action == "shutdown")
                b.setOnClickListener { onControl(action, label) }
                r.addView(b, LinearLayout.LayoutParams(0, dp(92), 1f).also { if (j < row.size - 1) it.marginEnd = dp(8) })
                if (action == "wake") wakeTile = b else controlTiles.add(b)
            }
            t.addView(r, margins(top = if (i == 0) 14 else 8))
        }
        laptopResult = text("", 13f, P.text2)
        laptopResult.visibility = View.GONE
        t.addView(laptopResult, margins(top = 12))
        return t
    }

    /** What the phone can be for the laptop: a 2-column grid of the same tiles as the controls. */
    private fun uses(): View {
        val t = tile()
        t.addView(caption("Use with the laptop"))
        val items = listOf(
            Triple("Send files", R.drawable.ic_send) { pickFiles() },
            Triple("Show screen", R.drawable.ic_phone) { startActivity(Intent(this, MirrorStartActivity::class.java)) },
            Triple("Second screen", R.drawable.ic_monitor) { startActivity(Intent(this, ScreenActivity::class.java)) },
            Triple("Webcam", R.drawable.ic_camera) { startActivity(Intent(this, CameraActivity::class.java)) },
            Triple("Microphone", R.drawable.ic_mic) { toggleMic() },
            Triple("Sound on laptop", R.drawable.ic_speaker) {
                if (AudioShareService.running) AudioShareService.instance?.finish(tell = true)
                else startActivity(Intent(this, AudioStartActivity::class.java))
            }
        )
        for ((i, pair) in items.chunked(2).withIndex()) {
            val r = LinearLayout(this)
            r.orientation = LinearLayout.HORIZONTAL
            for ((j, item) in pair.withIndex()) {
                val (label, icon, action) = item
                val b = iconTile(label, icon, danger = false)
                b.setOnClickListener { action() }
                if (icon == R.drawable.ic_mic) micButton = (b as LinearLayout).getChildAt(1) as TextView
                r.addView(b, LinearLayout.LayoutParams(0, dp(84), 1f).also { if (j == 0) it.marginEnd = dp(8) })
            }
            t.addView(r, margins(top = if (i == 0) 14 else 8))
        }
        // taps from the laptop need Android's accessibility switch, which only you can turn on
        controlButton = button("Allow laptop control", primary = false, small = true)
        controlButton.setOnClickListener {
            ControlSwitch.present(this, true) // listed again so you can switch it on
            toast("Settings > Accessibility > Installed apps > Nyx: control from laptop > on.")
            startActivity(Intent(android.provider.Settings.ACTION_ACCESSIBILITY_SETTINGS))
        }
        t.addView(controlButton, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(40)).also { it.topMargin = dp(10) })
        return t
    }

    /** The picture the laptop shows on its phone card, with the two ways to set it. */
    private fun picture(): View {
        val t = tile()
        val row = LinearLayout(this)
        row.orientation = LinearLayout.HORIZONTAL
        row.gravity = Gravity.CENTER_VERTICAL

        val frame = FrameLayout(this)
        frame.background = rounded(P.tile2, P.line, 14)
        frame.setPadding(dp(3), dp(3), dp(3), dp(3))
        wallThumb = ImageView(this)
        wallThumb.scaleType = ImageView.ScaleType.CENTER_CROP
        wallThumb.outlineProvider = object : ViewOutlineProvider() {
            override fun getOutline(v: View, o: Outline) = o.setRoundRect(0, 0, v.width, v.height, dpf(11f))
        }
        wallThumb.clipToOutline = true
        frame.addView(wallThumb, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
        row.addView(frame, LinearLayout.LayoutParams(dp(62), dp(128)))

        val right = LinearLayout(this)
        right.orientation = LinearLayout.VERTICAL
        right.addView(caption("On your laptop"))
        pictureText = text("", 14f, P.ink)
        right.addView(pictureText, margins(top = 6))
        val btns = LinearLayout(this)
        btns.orientation = LinearLayout.HORIZONTAL
        realWallpaper = button("Use wallpaper", primary = true, small = true)
        realWallpaper.setOnClickListener { askWallpaperAccess() }
        btns.addView(realWallpaper, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, dp(38)).also { it.marginEnd = dp(8) })
        val choose = button("Choose picture", primary = false, small = true)
        choose.setOnClickListener { pickPicture() }
        btns.addView(choose, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, dp(38)))
        right.addView(btns, margins(top = 12))
        row.addView(right, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).also { it.marginStart = dp(18) })

        t.addView(row)
        return t
    }

    private fun log(): View {
        val t = tile()
        val head = LinearLayout(this)
        head.orientation = LinearLayout.HORIZONTAL
        head.gravity = Gravity.CENTER_VERTICAL
        head.addView(caption("On and off"), LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        // Today | 7 days: a small segmented switch
        val seg = LinearLayout(this)
        seg.orientation = LinearLayout.HORIZONTAL
        seg.setPadding(dp(3), dp(3), dp(3), dp(3))
        seg.background = rounded(P.tile2, 0, 18)
        fun segItem(label: String, days: Int): TextView {
            val t = text(label, 12f, P.ink, bold = true)
            t.gravity = Gravity.CENTER
            t.setPadding(dp(12), 0, dp(12), 0)
            t.setOnClickListener { logDays = days; lastLogKey = ""; paintSeg(); renderLaptop() }
            return t
        }
        segToday = segItem("Today", 1)
        segWeek = segItem("7 days", 7)
        seg.addView(segToday, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, dp(30)))
        seg.addView(segWeek, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, dp(30)))
        paintSeg()
        head.addView(seg, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT).also { it.marginEnd = dp(8) })
        val refresh = ImageView(this)
        refresh.setImageResource(R.drawable.ic_refresh)
        refresh.imageTintList = ColorStateList.valueOf(P.ink)
        refresh.setPadding(dp(9), dp(9), dp(9), dp(9))
        refresh.background = RippleDrawable(ColorStateList.valueOf(P.line), rounded(P.tile2, 0, 18), null)
        refresh.contentDescription = "Refresh the log"
        refresh.setOnClickListener {
            refresh.animate().rotationBy(360f).setDuration(600).start()
            if (LinkService.instance?.requestPowerLog() != true) toast("The laptop isn't linked right now, so this is the last saved log.")
        }
        head.addView(refresh, LinearLayout.LayoutParams(dp(36), dp(36)))
        t.addView(head)
        logList = LinearLayout(this)
        logList.orientation = LinearLayout.VERTICAL
        t.addView(logList, margins(top = 8))
        return t
    }

    private var logDays = 1
    private var lastLogKey = ""
    private lateinit var segToday: TextView
    private lateinit var segWeek: TextView

    private fun paintSeg() {
        for ((v, on) in listOf(segToday to (logDays == 1), segWeek to (logDays == 7))) {
            v.background = if (on) rounded(P.ink, 0, 15) else null
            v.setTextColor(if (on) P.onInk else P.text2)
        }
    }

    private fun startOfToday(): Long {
        val c = java.util.Calendar.getInstance()
        c.set(java.util.Calendar.HOUR_OF_DAY, 0); c.set(java.util.Calendar.MINUTE, 0)
        c.set(java.util.Calendar.SECOND, 0); c.set(java.util.Calendar.MILLISECOND, 0)
        return c.timeInMillis
    }

    // ---------------------------------------------------------------- render

    private fun renderApproval() {
        if (!::approvalBox.isInitialized) return
        val p = LinkService.instance?.pending
        val show = p != null
        if (show && approvalBox.visibility != View.VISIBLE) {
            approvalBox.visibility = View.VISIBLE
            approvalBox.alpha = 0f
            approvalBox.scaleX = 0.97f; approvalBox.scaleY = 0.97f
            approvalBox.animate().alpha(1f).scaleX(1f).scaleY(1f).setDuration(260).start()
        } else if (!show) approvalBox.visibility = View.GONE
        if (p != null) approvalText.text =
            if (p.code.isNotEmpty()) "Pair with ${p.name}?\nCode ${p.code.take(3)} ${p.code.drop(3)}: allow only if the laptop shows the same code."
            else "Allow ${p.name} to link with Nyx?"
        pictureText.text = when {
            Wallpaper.hasCustom(this) -> "Your chosen picture."
            Wallpaper.canReadWallpaper() -> "Your wallpaper. It follows when you change it."
            else -> "Only your wallpaper's colour for now. Tap Use wallpaper to show the real one."
        }
        realWallpaper.visibility = if (Wallpaper.canReadWallpaper() || Build.VERSION.SDK_INT < 30) View.GONE else View.VISIBLE
    }

    /** Shows the same picture the laptop gets, decoded off the main thread. */
    private fun loadThumb() {
        Thread {
            val bmp: Bitmap? = try {
                Wallpaper.picture(this)?.let { src ->
                    val h = 360
                    Bitmap.createScaledBitmap(src, (src.width * h / src.height.toFloat()).toInt().coerceAtLeast(1), h, true)
                }
            } catch (e: Exception) { null }
            val color = Wallpaper.mainColor(this)
            runOnUiThread {
                if (bmp != null) {
                    wallThumb.setImageBitmap(bmp)
                    wallThumb.alpha = 0f
                    wallThumb.animate().alpha(1f).setDuration(320).start()
                } else {
                    wallThumb.setImageDrawable(null)
                    wallThumb.setBackgroundColor(color?.let { Color.parseColor(it) } ?: P.tile2)
                }
            }
        }.start()
    }

    private val timeFmt = java.text.SimpleDateFormat("EEE d MMM, HH:mm", java.util.Locale.getDefault())

    private fun renderLaptop() {
        if (!::heroName.isInitialized) return
        val cur = Pairing.current(this)
        val linked = LinkService.linkKind
        val running = bridgeRunning()
        heroName.text = cur?.second?.optString("name")?.takeIf { it.isNotEmpty() } ?: if (cur == null) "No laptop yet" else "Laptop"
        heroWord.text = when {
            linked.isNotEmpty() -> "Linked"
            !running -> "Off"
            cur == null -> "Pair"
            else -> "Waiting"
        }
        heroWord.color = if (linked.isNotEmpty() || !running) P.ink else P.ink
        heroDetail.text = when {
            linked.isNotEmpty() -> when (linked) { "Wi-Fi" -> "Over Wi-Fi, encrypted"; "USB" -> "Over the USB cable, encrypted"; else -> "Over Bluetooth" }
            !running -> "Turn the bridge on so your laptop can reach this phone."
            cur == null -> "On the same Wi-Fi, press Pair in Nyx on the laptop and check both show the same code."
            else -> cur.second.optLong("last_seen", 0).let {
                if (it > 0) "Last seen " + timeFmt.format(java.util.Date(it)) + ". Links by itself on the same Wi-Fi."
                else "Links by itself when both are on the same Wi-Fi."
            }
        }
        headPill.text = when {
            linked.isNotEmpty() -> "LINKED · " + linked.uppercase()
            running -> "WAITING"
            else -> "OFF"
        }
        headDot.color = when {
            linked.isNotEmpty() -> P.ok
            running -> P.red
            else -> P.text2
        }
        headDot.pulsing = running && linked.isEmpty()

        val canControl = linked.isNotEmpty() && cur?.second?.optBoolean("control", true) != false
        for (b in controlTiles) b.alpha = if (canControl) 1f else 0.38f
        wakeTile.alpha = if (cur != null) 1f else 0.38f

        val log = cur?.second?.optJSONArray("power_log")
        // this runs every second: only rebuild the list when it would look different
        val key = "$logDays|${startOfToday()}|${cur == null}|${log?.toString()?.hashCode()}"
        if (key == lastLogKey) return
        lastLogKey = key
        logList.removeAllViews()
        if (log == null || log.length() == 0) {
            logList.addView(text(
                if (cur == null) "Pair with your laptop and its on, off, sleep and wake times show up here."
                else "No log yet. It loads the next time the laptop links.", 14f, P.text2), margins(top = 4, bottom = 4))
            return
        }
        fun labelOf(kind: String) = when (kind) {
            "on" -> "Turned on" to false
            "off" -> "Shut down" to false
            "sleep" -> "Went to sleep" to false
            "wake" -> "Woke up" to false
            else -> "Lost power or froze" to true
        }
        val today = startOfToday()
        val since = today - (logDays - 1) * 24L * 3600 * 1000
        val items = (0 until log.length()).map { log.getJSONObject(it) }.filter { it.optLong("time") >= since }
        if (items.isEmpty()) {
            val last = log.getJSONObject(0)
            val (label, _) = labelOf(last.optString("kind"))
            logList.addView(text(
                (if (logDays == 1) "Nothing yet today." else "Nothing in the last 7 days.") +
                    " Last: " + label.lowercase() + ", " + timeFmt.format(java.util.Date(last.optLong("time"))) + ".",
                14f, P.text2), margins(top = 4, bottom = 4))
            return
        }
        val hourFmt = java.text.SimpleDateFormat("HH:mm", java.util.Locale.getDefault())
        val n = minOf(items.size, if (logDays == 1) 12 else 30)
        for (i in 0 until n) {
            val item = items[i]
            val (label, red) = labelOf(item.optString("kind"))
            val time = item.optLong("time")
            val shown = if (time >= today) hourFmt.format(java.util.Date(time)) else timeFmt.format(java.util.Date(time))
            logList.addView(logRow(label, shown, red, first = i == 0, last = i == n - 1))
        }
    }

    /** One timeline entry: a dot on a thin rail, what happened, and when. */
    private fun logRow(label: String, time: String, red: Boolean, first: Boolean, last: Boolean): View {
        val row = LinearLayout(this)
        row.orientation = LinearLayout.HORIZONTAL
        row.gravity = Gravity.CENTER_VERTICAL
        val rail = object : View(this) {
            val paint = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG)
            override fun onDraw(c: android.graphics.Canvas) {
                val cx = width / 2f
                val cy = height / 2f
                paint.color = P.line
                paint.strokeWidth = dpf(1.5f)
                if (!first) c.drawLine(cx, 0f, cx, cy, paint)
                if (!last) c.drawLine(cx, cy, cx, height.toFloat(), paint)
                paint.color = if (red) P.red else if (first) P.ink else P.text2
                c.drawCircle(cx, cy, dpf(if (first) 5f else 4f), paint)
            }
        }
        row.addView(rail, LinearLayout.LayoutParams(dp(18), dp(46)))
        row.addView(text(label, 15f, if (red) P.red else P.ink, bold = first),
            LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).also { it.marginStart = dp(12) })
        row.addView(text(time, 13f, P.text2))
        return row
    }

    private data class SetupItem(val title: String, val sub: String, val action: String, val run: () -> Unit)

    // null until the first refresh: "" means "nothing left to set up", which must hide the tile
    private var lastSetupKey: String? = null

    private fun refresh() {
        renderRequests()
        val missing = missingPerms()
        val items = mutableListOf<SetupItem>()
        val ui = getSharedPreferences("ui", MODE_PRIVATE)
        // Android 13+ blocks SMS, call log and notification access for apps installed from
        // a file until "Allow restricted settings" is on. After the first try, go there.
        val restricted = Build.VERSION.SDK_INT >= 33
        if (missing.isNotEmpty()) {
            val tried = ui.getBoolean("tried_perms", false)
            items += if (restricted && tried)
                SetupItem("Permissions", "${missing.size} blocked by Android. In App info, tap ⋮ (top right) > Allow restricted settings, then come back.", "App info") { openAppInfo() }
            else SetupItem("Permissions", "${missing.size} still needed for calls, texts and contacts.", "Allow") {
                ui.edit().putBoolean("tried_perms", true).apply()
                requestPermissions(neededPerms(), 1)
            }
        }
        if (!notifAccess()) {
            val tried = ui.getBoolean("tried_notif", false)
            items += if (restricted && tried)
                SetupItem("Notification access", "If Android said it's restricted: App info > ⋮ > Allow restricted settings, then turn it on.", "App info") { openAppInfo() }
            else SetupItem("Notification access", "So your notifications show on the laptop.", "Turn on") {
                ui.edit().putBoolean("tried_notif", true).apply()
                startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
            }
        }
        // rebuild only when something changed, so rows don't flicker every second
        val key = items.joinToString { it.title + it.sub }
        if (key != lastSetupKey) {
            lastSetupKey = key
            setupRows.removeAllViews()
            items.forEachIndexed { i, it -> setupRows.addView(setupRow(it, i == items.size - 1)) }
            setupTile.visibility = if (items.isEmpty()) View.GONE else View.VISIBLE
        }

        bridgeSwitch.set(bridgeRunning())
        bridgeLabel.text = if (bridgeRunning()) LinkService.status.replaceFirstChar { it.uppercase() } else "Off"
        renderLaptop()
    }

    private fun setupRow(item: SetupItem, last: Boolean): View {
        val row = LinearLayout(this)
        row.orientation = LinearLayout.HORIZONTAL
        row.gravity = Gravity.CENTER_VERTICAL
        row.setPadding(0, dp(12), 0, dp(12))
        val dot = PulseDot(this).also { it.color = P.red; it.pulsing = true }
        row.addView(dot, LinearLayout.LayoutParams(dp(20), dp(20)))
        val texts = LinearLayout(this)
        texts.orientation = LinearLayout.VERTICAL
        texts.addView(text(item.title, 16f, P.ink, bold = true))
        texts.addView(text(item.sub, 13f, P.text2), margins(top = 2))
        row.addView(texts, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).also { it.marginStart = dp(12); it.marginEnd = dp(12) })
        val b = button(item.action, primary = true, small = true)
        b.setOnClickListener { item.run() }
        row.addView(b, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, dp(38)))
        if (!last) {
            val wrap = LinearLayout(this)
            wrap.orientation = LinearLayout.VERTICAL
            wrap.addView(row)
            val line = View(this)
            line.setBackgroundColor(P.line)
            wrap.addView(line, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(1)))
            return wrap
        }
        return row
    }

    // ---------------------------------------------------------------- building blocks

    private fun tile(): LinearLayout {
        val t = LinearLayout(this)
        t.orientation = LinearLayout.VERTICAL
        t.setPadding(dp(20), dp(18), dp(20), dp(20))
        t.background = rounded(P.tile, P.line, 26)
        return t
    }

    private fun caption(value: String, color: Int = P.text2): TextView {
        val t = text(value.uppercase(), 11f, color, bold = true)
        t.letterSpacing = 0.12f
        return t
    }

    private fun text(value: String, size: Float, color: Int, bold: Boolean = false): TextView {
        val t = TextView(this)
        t.text = value
        t.textSize = size
        t.setTextColor(color)
        t.typeface = Typeface.create(if (bold) "sans-serif-medium" else "sans-serif", Typeface.NORMAL)
        t.setLineSpacing(0f, 1.18f)
        return t
    }

    /** Pill button: primary is ink-filled (white on dark), secondary is a quiet tile. */
    private fun button(label: String, primary: Boolean, small: Boolean = false): TextView {
        val t = text(label, if (small) 13f else 15f, if (primary) P.onInk else P.ink, bold = true)
        t.gravity = Gravity.CENTER
        t.setPadding(dp(if (small) 16 else 20), 0, dp(if (small) 16 else 20), 0)
        val fill = if (primary) P.ink else P.tile2
        t.background = RippleDrawable(ColorStateList.valueOf(if (primary) P.text2 else P.line), rounded(fill, 0, 24), null)
        t.isClickable = true
        t.isFocusable = true
        pressable(t)
        return t
    }

    /** A square control: line icon above its label. */
    private fun iconTile(label: String, icon: Int, danger: Boolean): View {
        val t = LinearLayout(this)
        t.orientation = LinearLayout.VERTICAL
        t.gravity = Gravity.CENTER
        t.background = RippleDrawable(ColorStateList.valueOf(P.line), rounded(P.tile2, 0, 20), null)
        t.isClickable = true
        t.isFocusable = true
        t.contentDescription = label
        val i = ImageView(this)
        i.setImageResource(icon)
        i.imageTintList = ColorStateList.valueOf(if (danger) P.red else P.ink)
        t.addView(i, LinearLayout.LayoutParams(dp(22), dp(22)))
        t.addView(text(label, 13f, if (danger) P.red else P.ink, bold = true), wrap(top = 10))
        pressable(t)
        return t
    }

    /** A gentle squeeze under the finger. */
    @SuppressLint("ClickableViewAccessibility")
    private fun pressable(v: View) {
        v.setOnTouchListener { view, e ->
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> view.animate().scaleX(0.96f).scaleY(0.96f).setDuration(90).start()
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> view.animate().scaleX(1f).scaleY(1f).setDuration(160).start()
            }
            false
        }
    }

    private fun margins(top: Int = 0, bottom: Int = 0): LinearLayout.LayoutParams {
        val lp = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
        lp.topMargin = dp(top)
        lp.bottomMargin = dp(bottom)
        return lp
    }

    private fun wrap(top: Int = 0): LinearLayout.LayoutParams {
        val lp = LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT)
        lp.topMargin = dp(top)
        return lp
    }

    private fun rounded(fill: Int, stroke: Int, radius: Int): GradientDrawable {
        val g = GradientDrawable()
        g.setColor(fill)
        g.cornerRadius = dp(radius).toFloat()
        if (stroke != 0) g.setStroke(dp(1), stroke)
        return g
    }

    private fun versionName(): String =
        try { packageManager.getPackageInfo(packageName, 0).versionName ?: "" } catch (e: Exception) { "" }

    // ----------------------------------------------------------------- actions

    /**
     * Android only lets an app read the wallpaper with All files access. Explain that first,
     * then open the exact settings switch for Nyx; Nyx uses it for the wallpaper and nothing else.
     */
    private fun askWallpaperAccess() {
        if (Build.VERSION.SDK_INT < 30) return
        android.app.AlertDialog.Builder(this)
            .setTitle("Show your real wallpaper?")
            .setMessage("Android only lets apps read the wallpaper with \"All files access\". Nyx uses it only to send a small copy of your wallpaper to your laptop. You can turn it off any time.")
            .setPositiveButton("Open settings") { _, _ ->
                val i = Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION, android.net.Uri.parse("package:$packageName"))
                try { startActivity(i) } catch (e: Exception) {
                    startActivity(Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION))
                }
            }
            .setNegativeButton("Not now", null)
            .show()
    }

    private fun answerApproval(allow: Boolean) {
        val p = LinkService.instance?.pending ?: return
        LinkService.instance?.resolve(p.address, allow)
        renderApproval()
    }

    private fun pickPicture() {
        // the system photo picker: no storage permission, and only the one image is shared
        val i = if (Build.VERSION.SDK_INT >= 33) Intent(android.provider.MediaStore.ACTION_PICK_IMAGES)
        else Intent(Intent.ACTION_GET_CONTENT).setType("image/*")
        try { startActivityForResult(i, PICK_PICTURE) } catch (e: Exception) { toast("No photo picker on this phone.") }
    }

    private fun pickFiles() {
        if (LinkService.instance == null || LinkService.linkKind.isEmpty()) { toast("The laptop isn't linked right now."); return }
        val i = Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*")
            .putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true)
        @Suppress("DEPRECATION")
        try { startActivityForResult(i, PICK_FILES) } catch (e: Exception) { toast("No file picker on this phone.") }
    }

    @Deprecated("Activity result API needs androidx; plain Activity keeps the app dependency-free")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        @Suppress("DEPRECATION")
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == PICK_FILES && resultCode == RESULT_OK && data != null) {
            val uris = mutableListOf<android.net.Uri>()
            data.clipData?.let { clip -> for (i in 0 until clip.itemCount) uris.add(clip.getItemAt(i).uri) }
            if (uris.isEmpty()) data.data?.let { uris.add(it) }
            val files = uris.take(20).mapNotNull { FileShare.open(this, it) }
            if (files.isEmpty()) { toast("Couldn't open that file."); return }
            for (f in files) Requests.add("file:" + f.name, "Send " + f.name)
            renderRequests()
            LinkService.instance?.sendFiles(files) { problem ->
                if (problem != null) {
                    for (f in files) Requests.finish("file:" + f.name, ok = false, msg = problem)
                    renderRequests()
                }
            }
            return
        }
        if (requestCode != PICK_PICTURE || resultCode != RESULT_OK) return
        val uri = data?.data ?: return
        Thread {
            val ok = Wallpaper.savePicked(this, uri)
            runOnUiThread {
                toast(if (ok) "Picture set. It shows on the laptop now." else "Could not read that picture.")
                renderApproval()
                loadThumb()
                if (ok) LinkService.instance?.sendWallpaper()
            }
        }.start()
    }

    /** The line under the controls: hidden until there's something to say. */
    private fun showResult(msg: String) {
        laptopResult.text = msg
        laptopResult.visibility = if (msg.isEmpty()) View.GONE else View.VISIBLE
    }

    private fun openAppInfo() {
        toast("Tap ⋮ at the top right, then Allow restricted settings.")
        startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, android.net.Uri.parse("package:$packageName")))
    }

    private fun toast(msg: String) = Toast.makeText(this, msg, Toast.LENGTH_LONG).show()

    private fun onControl(action: String, label: String) {
        if (action == "wake") { wake(); return }
        val svc = LinkService.instance
        if (svc == null || LinkService.linkKind.isEmpty()) {
            toast("The laptop isn't linked. It has to be on and running Nyx.")
            return
        }
        if (action == "restart" || action == "shutdown") {
            android.app.AlertDialog.Builder(this)
                .setTitle("$label the laptop?")
                .setMessage("It waits 15 seconds first. You can cancel from here or on the laptop.")
                .setPositiveButton(label) { _, _ -> svc.laptopCommand(action); Requests.add(action, label); renderRequests() }
                .setNegativeButton("Keep it on", null)
                .show()
            return
        }
        svc.laptopCommand(action)
        Requests.add(action, label)
        renderRequests()
    }

    /** Wake-on-LAN magic packet. Works from sleep when the laptop's Wi-Fi supports it. */
    private fun wake() {
        val cur = Pairing.current(this)
        if (cur == null) { toast("Pair with a laptop first."); return }
        // no "already awake" check: a laptop that just fell asleep can still look linked for a
        // moment, and the signal does nothing to one that's awake
        val macs = cur.second.optJSONArray("macs")
        if (macs == null || macs.length() == 0) { toast("Link to the laptop once more so Nyx can learn how to wake it."); return }
        val bcast = cur.second.optJSONArray("bcast") ?: org.json.JSONArray()
        // done when the laptop links up again; the card says so if it doesn't within a while
        // a laptop that just fell asleep can still look linked: ask it to answer instead of trusting that
        if (LinkService.linkKind.isNotEmpty()) LinkService.instance?.requestPowerLog()
        if (Requests.items.none { it.kind == "wake" && it.state == Requests.WAITING }) Requests.add("wake", "Wake")
        renderRequests()
        Thread {
            val n = try { WakeOnLan.send(macs, bcast) } catch (e: Exception) { 0 }
            runOnUiThread {
                if (n == 0) { Requests.finish("wake", false, "Couldn't send the wake signal. Is the phone on Wi-Fi?"); renderRequests() }
            }
        }.start()
    }

    private fun onLaptopEvent(e: org.json.JSONObject) {
        when (e.optString("t")) {
            "pc_result" -> {
                val action = e.optString("action")
                if (action.startsWith("link_") && !e.optBoolean("ok")) { Requests.finish(action, false, e.optString("msg")); renderRequests(); return }
                if (action == "cancel") {
                    // the countdown was stopped: say so on the restart / shut down line
                    Requests.items.lastOrNull { it.kind == "restart" || it.kind == "shutdown" }?.let {
                        it.cancelUntil = 0
                        it.msg = if (e.optBoolean("ok")) "Cancelled." else e.optString("msg")
                    }
                } else Requests.finish(action, e.optBoolean("ok"), e.optString("msg"))
                renderRequests()
            }
            "power_log" -> {
                // only an awake laptop answers
                if (Requests.finish("wake", true, "The laptop is awake.")) renderRequests()
                renderLaptop()
            }
            "approval" -> renderApproval()
            "file_done" -> {
                Requests.finish("file:" + e.optString("name"), e.optBoolean("ok"), if (e.optBoolean("ok")) "Sent." else e.optString("msg"))
                renderRequests()
            }
            "link" -> {
                if (e.optString("kind").isNotEmpty()) Requests.finish("wake", true, "The laptop is awake.")
                when (e.optString("kind")) {
                    "USB" -> Requests.finish("link_usb", true, "Linked over the USB cable.")
                    "Wi-Fi" -> Requests.finish("link_wifi", true, "Linked over Wi-Fi.")
                }
                renderRequests()
                renderLaptop()
                if (e.optString("kind").isNotEmpty()) LinkService.instance?.requestPowerLog()
            }
        }
    }

    // ----------------------------------------------------------------- logic

    private fun neededPerms(): Array<String> {
        val p = mutableListOf(
            Manifest.permission.READ_PHONE_STATE,
            Manifest.permission.READ_CALL_LOG,
            Manifest.permission.ANSWER_PHONE_CALLS,
            Manifest.permission.CALL_PHONE,
            Manifest.permission.READ_SMS,
            Manifest.permission.SEND_SMS,
            Manifest.permission.RECEIVE_SMS,
            Manifest.permission.READ_CONTACTS
        )
        if (Build.VERSION.SDK_INT >= 31) p.add(Manifest.permission.BLUETOOTH_CONNECT)
        if (Build.VERSION.SDK_INT >= 33) p.add(Manifest.permission.POST_NOTIFICATIONS)
        return p.toTypedArray()
    }

    private fun missingPerms(): List<String> =
        neededPerms().filter { checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }

    private fun notifAccess(): Boolean = NotifListener.hasAccess(this)

    private fun bridgeRunning(): Boolean = LinkService.instance != null

    private fun toggleBridge() {
        if (bridgeRunning()) {
            stopService(Intent(this, LinkService::class.java))
            BridgeRestart.remember(this, false)
            bridgeSwitch.set(false)
            return
        }
        if (Build.VERSION.SDK_INT >= 31 &&
            checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED
        ) {
            toast("Allow the Nearby devices permission first.")
            return
        }
        try {
            startForegroundService(Intent(this, LinkService::class.java))
        } catch (e: Exception) {
            toast("Android didn't let the bridge start: " + (e.message ?: e.javaClass.simpleName))
            bridgeSwitch.set(false)
            return
        }
        BridgeRestart.remember(this, true)
        bridgeSwitch.set(true)
    }
}
