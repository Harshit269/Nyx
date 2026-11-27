package com.amak.phonebridge

import android.app.Notification
import android.content.ComponentName
import android.content.Context
import android.provider.Settings
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import org.json.JSONObject

class NotifListener : NotificationListenerService() {

    companion object {
        @Volatile
        var instance: NotifListener? = null

        /** Whether notification access is switched on for Nyx (exact component match). */
        fun hasAccess(c: Context): Boolean {
            val mine = ComponentName(c, NotifListener::class.java)
            val s = Settings.Secure.getString(c.contentResolver, "enabled_notification_listeners") ?: ""
            return s.split(':').any { ComponentName.unflattenFromString(it) == mine }
        }

        /**
         * Access is on but Android hasn't bound the listener (happens after an app update):
         * ask it to bind again so notifications start flowing without a phone restart.
         */
        fun rebindIfNeeded(c: Context) {
            if (instance == null && hasAccess(c))
                try { requestRebind(ComponentName(c, NotifListener::class.java)) } catch (e: Exception) {}
        }
    }

    override fun onListenerConnected() {
        instance = this
        // what's already in the shade counts too (after an update or a restart, nothing's lost)
        try { activeNotifications?.forEach { count(it) } } catch (e: Exception) {}
        // a laptop already linked gets what's in the shade right away
        LinkService.instance?.let { svc -> sendActive(svc) }
    }

    override fun onListenerDisconnected() {
        instance = null
    }

    /** Everything currently in the notification shade, oldest first, so the laptop list matches the phone. */
    fun sendActive(svc: LinkService) {
        val all = try { activeNotifications } catch (e: Exception) { null } ?: return
        val ids = org.json.JSONArray()
        for (sbn in all.sortedBy { it.postTime }) {
            if (NotifApps.isMuted(this, sbn.packageName)) continue // switched off on the laptop
            if (WhatsApp.ENABLED && WhatsApp.isWhatsApp(sbn.packageName)) {
                try { WhatsApp.fromNotification(sbn)?.let { svc.send(it) } } catch (e: Exception) {}
            }
            toJson(sbn)?.let { svc.send(it.put("old", true)); ids.put(sbn.key) }
        }
        // the full list, so the laptop can drop anything that left the shade while it was away
        svc.send(JSONObject().put("t", "notif_sync").put("ids", ids))
        svc.send(TodayCount.json(svc))
    }

    /** The apps with a notification up right now (name by package), for the laptop's app list. */
    fun activeApps(): Map<String, String> {
        val out = HashMap<String, String>()
        try {
            for (sbn in activeNotifications ?: emptyArray()) {
                if (sbn.packageName == packageName || sbn.packageName in out) continue
                out[sbn.packageName] = try {
                    packageManager.getApplicationLabel(packageManager.getApplicationInfo(sbn.packageName, 0)).toString()
                } catch (e: Exception) { sbn.packageName }
            }
        } catch (e: Exception) {}
        return out
    }

    private fun toJson(sbn: StatusBarNotification): JSONObject? {
        if (sbn.packageName == packageName || sbn.isOngoing) return null
        if (NotifApps.isMuted(this, sbn.packageName)) return null // switched off on the laptop
        val n = sbn.notification ?: return null
        if ((n.flags and Notification.FLAG_GROUP_SUMMARY) != 0) return null
        val ex = n.extras ?: return null
        val title = ex.getCharSequence(Notification.EXTRA_TITLE)?.toString() ?: ""
        val body = (ex.getCharSequence(Notification.EXTRA_BIG_TEXT)
            ?: ex.getCharSequence(Notification.EXTRA_TEXT))?.toString() ?: ""
        if (title.isEmpty() && body.isEmpty()) return null
        val app = try {
            packageManager.getApplicationLabel(
                packageManager.getApplicationInfo(sbn.packageName, 0)
            ).toString()
        } catch (e: Exception) {
            sbn.packageName
        }
        return JSONObject()
            .put("t", "notif")
            .put("canRead", readAction(n) != null)
            .put("id", sbn.key)
            .put("pkg", sbn.packageName)
            .put("app", app)
            .put("title", title)
            .put("text", body)
            .put("time", sbn.postTime)
    }

    /** Counts a real arrival (not a re-post of the same thing); true when it counted. */
    private fun count(sbn: StatusBarNotification): Boolean {
        val j = toJson(sbn) ?: return false
        val quiet = (sbn.notification.flags and Notification.FLAG_ONLY_ALERT_ONCE) != 0
        return TodayCount.add(this, sbn.key, j.optString("title") + "\u0000" + j.optString("text"), quiet, sbn.postTime)
    }

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        // counted even when no laptop is linked, so "today" is the whole day
        if (count(sbn)) LinkService.instance?.send(TodayCount.json(this))
        val svc = LinkService.instance ?: return
        if (NotifApps.isMuted(this, sbn.packageName)) return // switched off on the laptop
        if (WhatsApp.ENABLED && WhatsApp.isWhatsApp(sbn.packageName)) {
            try { WhatsApp.fromNotification(sbn)?.let { svc.send(it) } } catch (e: Exception) {}
        }
        toJson(sbn)?.let { svc.send(it) }
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification) {
        val svc = LinkService.instance ?: return
        svc.send(JSONObject().put("t", "notif_removed").put("id", sbn.key))
        if (WhatsApp.ENABLED && WhatsApp.isWhatsApp(sbn.packageName)) {
            try { WhatsApp.gone(sbn)?.let { svc.send(it) } } catch (e: Exception) {}
        }
    }

    /**
     * The notification's own "Mark as read" button (WhatsApp, Messages, Gmail… add one).
     * Pressing it marks the message read in that app, which is the only way to do that from
     * outside the app.
     */
    private fun readAction(n: Notification): Notification.Action? =
        n.actions?.firstOrNull { a ->
            (android.os.Build.VERSION.SDK_INT >= 28 && a.semanticAction == Notification.Action.SEMANTIC_ACTION_MARK_AS_READ) ||
                (a.title?.toString()?.lowercase()?.replace(" ", "")?.let { it == "markasread" || it == "markread" } == true)
        }

    /** Presses "Mark as read" on the notification with this key; false when it has none. */
    fun markRead(key: String): Boolean {
        val sbn = try { activeNotifications?.firstOrNull { it.key == key } } catch (e: Exception) { null } ?: return false
        val action = readAction(sbn.notification) ?: return false
        return try { action.actionIntent.send(); true } catch (e: Exception) { false }
    }

    fun dismiss(key: String) {
        try {
            cancelNotification(key)
        } catch (e: Exception) {
        }
    }
}
