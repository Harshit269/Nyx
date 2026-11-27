package com.amak.phonebridge

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Date
import java.util.Locale

/**
 * How many notifications arrived today, and when (for the laptop's "last 12 hours" dots).
 * Counted on the phone so it's the whole day, even while the laptop was off or asleep.
 *
 * Apps re-post the same notification all the time (a download's progress, a chat that's
 * re-sorted, a system note refreshed). Only a real arrival counts: a notification with new
 * words, or the first one under its key. A silent update (the app said "alert only once")
 * to something already counted doesn't. Only times and short hashes are kept, never what
 * the notifications said.
 */
object TodayCount {
    // the first version counted every re-post (540 a day), so this one starts clean
    private const val PREFS = "today_count2"
    private const val MAX_SEEN = 800
    private const val MAX_TIMES = 3000
    const val HOUR = 3_600_000L

    /** What to remember for one posted notification, or null when it isn't a new arrival. */
    fun marks(seen: Collection<String>, key: String, words: String, quiet: Boolean): List<String>? {
        val k = "k" + key.hashCode().toString(36)
        val m = "m" + (key + "\u0000" + words).hashCode().toString(36)
        if (m in seen) return null          // same notification, same words: only refreshed
        if (quiet && k in seen) return null // updated without alerting you (progress, a quiet edit)
        return listOf(k, m)
    }

    /** Arrivals per hour, oldest first: [11] is the last 60 minutes, [0] is 11-12 hours ago. */
    fun hours(times: List<Long>, now: Long): IntArray {
        val bars = IntArray(12)
        for (t in times) {
            val ago = ((now - t) / HOUR).toInt()
            if (now >= t && ago < 12) bars[11 - ago]++
        }
        return bars
    }

    fun startOfDay(now: Long): Long = Calendar.getInstance().apply {
        timeInMillis = now; set(Calendar.HOUR_OF_DAY, 0); set(Calendar.MINUTE, 0); set(Calendar.SECOND, 0); set(Calendar.MILLISECOND, 0)
    }.timeInMillis

    /** Keeps the last day of arrival times (the 12-hour dots reach back past midnight). */
    fun keep(times: List<Long>, now: Long): List<Long> = times.filter { now - it < 24 * HOUR && it <= now + HOUR }.takeLast(MAX_TIMES)

    /** Counts one arrival; false when it was only an update of one already counted. */
    @Synchronized
    fun add(c: Context, key: String, words: String, quiet: Boolean, postTime: Long): Boolean {
        val p = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val seen = p.getString("seen", "")!!.split(',').filter { it.isNotEmpty() }
        val add = marks(seen, key, words, quiet) ?: return false
        val now = System.currentTimeMillis()
        val at = if (postTime in (now - 24 * HOUR)..now) postTime else now
        val times = keep(load(p) + at, now)
        p.edit()
            .putString("seen", (seen.filter { it !in add } + add).takeLast(MAX_SEEN).joinToString(","))
            .putString("times", times.joinToString(",") { (it / 1000).toString() })
            .apply()
        return true
    }

    private fun load(p: android.content.SharedPreferences): List<Long> =
        p.getString("times", "")!!.split(',').mapNotNull { it.toLongOrNull()?.times(1000) }.sorted()

    @Synchronized
    fun times(c: Context): List<Long> = keep(load(c.getSharedPreferences(PREFS, Context.MODE_PRIVATE)), System.currentTimeMillis())

    fun json(c: Context): JSONObject {
        val now = System.currentTimeMillis()
        val all = times(c)
        val midnight = startOfDay(now)
        val recent = JSONArray()
        all.filter { now - it < 12 * HOUR }.forEach { recent.put(it) }
        return JSONObject().put("t", "notif_today")
            .put("count", all.count { it >= midnight })
            .put("day", SimpleDateFormat("yyyy-MM-dd", Locale.US).format(Date(now)))
            .put("times", recent) // arrival times of the last 12 hours, for the dots
            .put("now", now)
    }
}
