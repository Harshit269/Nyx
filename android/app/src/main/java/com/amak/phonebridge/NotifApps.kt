package com.amak.phonebridge

import android.content.Context
import android.content.Intent
import org.json.JSONArray
import org.json.JSONObject

/**
 * Which apps' notifications go to the laptop. You pick on the laptop (Settings > Notifications
 * from apps); the phone keeps the list, so a switched-off app's notifications never leave the
 * phone at all, don't pop up and don't count in Today.
 */
object NotifApps {
    private const val PREFS = "nyx_notif_apps"
    @Volatile private var cache: Set<String>? = null

    fun muted(c: Context): Set<String> =
        cache ?: (c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getStringSet("muted", emptySet()) ?: emptySet())
            .toSet().also { cache = it }

    fun isMuted(c: Context, pkg: String) = pkg in muted(c)

    /** The laptop's list. Only plain package names are kept, at most 2000 of them. */
    fun setMuted(c: Context, pkgs: Collection<String>) {
        val clean = pkgs.filter { valid(it) }.take(2000).toSet()
        c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putStringSet("muted", clean).apply()
        cache = clean
    }

    fun valid(pkg: String) = pkg.length in 1..200 && pkg.all { it.isLetterOrDigit() || it == '.' || it == '_' }

    /** Apps you can open, plus any that has a notification up right now (system ones too). */
    fun list(c: Context, extra: Map<String, String>): JSONObject {
        val pm = c.packageManager
        val apps = HashMap<String, String>(extra)
        try {
            val launch = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
            for (ri in pm.queryIntentActivities(launch, 0)) {
                val pkg = ri.activityInfo?.packageName ?: continue
                if (pkg == c.packageName) continue
                apps[pkg] = try { ri.loadLabel(pm).toString() } catch (e: Exception) { pkg }
            }
        } catch (e: Exception) {}
        val items = JSONArray()
        for ((pkg, name) in apps.entries.sortedBy { it.value.lowercase() })
            items.put(JSONObject().put("pkg", pkg).put("name", name.take(80)))
        return JSONObject().put("t", "notif_apps").put("items", items)
    }
}
