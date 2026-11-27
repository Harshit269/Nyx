package com.amak.phonebridge

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.content.ComponentName
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Path
import android.provider.Settings
import android.view.accessibility.AccessibilityEvent
import org.json.JSONObject

/**
 * Lets the laptop press things on the phone while its screen is mirrored: tap, long-press,
 * swipe, and Back / Home / Recents. Android only allows this through an accessibility service
 * you switch on yourself (Settings > Accessibility > Nyx). Nyx reads nothing on screen with it,
 * and ignores every command unless you're sharing the screen with the laptop right now.
 */
/**
 * Payment and banking apps (UPI apps, banks) refuse to run while any app on the phone *has* an
 * accessibility service, even one that's switched off. So Nyx's control service doesn't exist
 * (its component is disabled, so Android doesn't list it) except while you mirror. When the
 * laptop has allowed it (WRITE_SECURE_SETTINGS, granted once over USB), Nyx also switches it on
 * and off itself; without that, "Allow laptop control" brings it back for you to switch on.
 */
object ControlSwitch {
    fun canToggle(c: Context) =
        c.checkSelfPermission("android.permission.WRITE_SECURE_SETTINGS") == PackageManager.PERMISSION_GRANTED

    /** Makes the service exist (listed by Android) or not. Any app may do this to its own parts. */
    fun present(c: Context, on: Boolean) {
        try {
            c.packageManager.setComponentEnabledSetting(ComponentName(c, ControlService::class.java),
                if (on) PackageManager.COMPONENT_ENABLED_STATE_ENABLED else PackageManager.COMPONENT_ENABLED_STATE_DISABLED,
                PackageManager.DONT_KILL_APP)
        } catch (e: Exception) {}
    }

    /**
     * On: make it exist, then switch it on a moment later (Android has to notice it first).
     * Off: switch it off and make it disappear again. False when Nyx isn't allowed to switch it.
     */
    fun set(c: Context, on: Boolean): Boolean {
        if (on) {
            present(c, true)
            if (!canToggle(c)) return false
            android.os.Handler(android.os.Looper.getMainLooper()).postDelayed({ write(c, true) }, 700)
            return true
        }
        if (!canToggle(c)) return false // without the permission it's the user's own switch: leave it
        val ok = write(c, false)
        present(c, false)
        return ok
    }

    private fun write(c: Context, on: Boolean): Boolean {
        if (!canToggle(c)) return false
        return try {
            val cr = c.contentResolver
            val me = ComponentName(c, ControlService::class.java)
            val list = (Settings.Secure.getString(cr, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES) ?: "")
                .split(':').filter { it.isNotBlank() }.toMutableList()
            list.removeAll { ComponentName.unflattenFromString(it) == me }
            if (on) list.add(me.flattenToString())
            Settings.Secure.putString(cr, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES, list.joinToString(":"))
            if (on) Settings.Secure.putInt(cr, Settings.Secure.ACCESSIBILITY_ENABLED, 1)
            true
        } catch (e: Exception) { false }
    }
}

class ControlService : AccessibilityService() {

    companion object {
        @Volatile var instance: ControlService? = null
            private set

        /** Whether it's switched on in Settings (even if Android hasn't started it yet). */
        fun enabled(c: Context): Boolean {
            val list = Settings.Secure.getString(c.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES) ?: return false
            return list.split(':').any { it.endsWith("/" + ControlService::class.java.name) || it.endsWith("/.ControlService") }
        }
    }

    override fun onServiceConnected() {
        // switched on with no mirror running (by hand, or left over): off again right away, so
        // payment and bank apps keep working. Nyx turns it back on by itself when you mirror.
        if (!MirrorService.running && ControlSwitch.canToggle(this)) {
            disableSelf()
            ControlSwitch.present(this, false)
            return
        }
        instance = this
    }
    override fun onDestroy() { instance = null; super.onDestroy() }
    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}
    override fun onInterrupt() {}

    /** One command from the laptop: x and y run 0..1 across the screen. */
    fun act(c: JSONObject) {
        if (!MirrorService.running) return // only while you're sharing the screen
        @Suppress("DEPRECATION")
        val m = android.util.DisplayMetrics().also { (getSystemService(Context.WINDOW_SERVICE) as android.view.WindowManager).defaultDisplay.getRealMetrics(it) }
        fun px(k: String) = (c.optDouble(k, 0.5).coerceIn(0.0, 1.0) * m.widthPixels).toFloat()
        fun py(k: String) = (c.optDouble(k, 0.5).coerceIn(0.0, 1.0) * m.heightPixels).toFloat()
        when (c.optString("a")) {
            "tap" -> stroke(Path().apply { moveTo(px("x"), py("y")) }, 40)
            "long" -> stroke(Path().apply { moveTo(px("x"), py("y")) }, 650)
            "swipe" -> stroke(Path().apply { moveTo(px("x"), py("y")); lineTo(px("x2"), py("y2")) },
                c.optLong("ms", 250).coerceIn(40, 2000))
            "back" -> performGlobalAction(GLOBAL_ACTION_BACK)
            "home" -> performGlobalAction(GLOBAL_ACTION_HOME)
            "recents" -> performGlobalAction(GLOBAL_ACTION_RECENTS)
        }
    }

    private fun stroke(p: Path, ms: Long) {
        val g = GestureDescription.Builder().addStroke(GestureDescription.StrokeDescription(p, 0, ms)).build()
        try { dispatchGesture(g, null, null) } catch (e: Exception) {}
    }
}
