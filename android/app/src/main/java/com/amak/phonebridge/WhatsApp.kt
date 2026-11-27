package com.amak.phonebridge

import android.app.Notification
import android.app.RemoteInput
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.service.notification.StatusBarNotification
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.ConcurrentHashMap

/**
 * WhatsApp chats, read the only way WhatsApp allows: from its notifications. Each chat's
 * notification carries its recent messages (MessagingStyle) and a "Reply" action; Nyx
 * forwards the messages to the laptop and uses that same Reply action to answer.
 * No history: a chat starts showing from the first message that arrives while Nyx runs,
 * and replying works while WhatsApp's notification for that chat is still there.
 */
object WhatsApp {
    /** Switched off for now: WhatsApp messages aren't collected or sent to the laptop. */
    const val ENABLED = false

    private val apps = setOf("com.whatsapp", "com.whatsapp.w4b")

    fun isWhatsApp(pkg: String) = pkg in apps

    private class Chat(val id: String) {
        @Volatile var reply: Notification.Action? = null
        @Volatile var last: JSONObject? = null
    }

    private val chats = ConcurrentHashMap<String, Chat>()
    private val countOnly = Regex("^\\d+ (new )?messages?( from \\d+ chats)?$", RegexOption.IGNORE_CASE)

    /** A stable id for the chat that doesn't expose the phone number inside WhatsApp's key. */
    private fun chatId(sbn: StatusBarNotification, title: String): String {
        val raw = (if (Build.VERSION.SDK_INT >= 26) sbn.notification.shortcutId else null) ?: sbn.tag ?: title
        return LinkCipher.b64(PairCrypto.sha256((sbn.packageName + "|" + raw).toByteArray())).take(16)
    }

    /** The chat as a message for the laptop, or null when this isn't a chat notification. */
    fun fromNotification(sbn: StatusBarNotification): JSONObject? {
        val n = sbn.notification ?: return null
        if ((n.flags and Notification.FLAG_GROUP_SUMMARY) != 0) return null
        val ex = n.extras ?: return null
        val title = (ex.getCharSequence(Notification.EXTRA_CONVERSATION_TITLE)
            ?: ex.getCharSequence(Notification.EXTRA_TITLE))?.toString()?.trim() ?: return null
        if (title.isEmpty() || title.equals("WhatsApp", ignoreCase = true)) return null
        val group = ex.getBoolean(Notification.EXTRA_IS_GROUP_CONVERSATION, false)

        val messages = JSONArray()
        @Suppress("DEPRECATION")
        val bundles = ex.getParcelableArray(Notification.EXTRA_MESSAGES)
        if (bundles != null) {
            for (p in bundles) {
                val b = p as? Bundle ?: continue
                val text = b.getCharSequence("text")?.toString() ?: continue
                if (text.isBlank() || countOnly.matches(text.trim())) continue
                val sender = senderOf(b)
                messages.put(JSONObject()
                    .put("text", text.take(4000))
                    .put("time", b.getLong("time", sbn.postTime))
                    .put("sender", sender ?: "")
                    .put("me", sender == null))
            }
        }
        if (messages.length() == 0) {
            // older style: one line, "Sender: text" in groups
            val text = ex.getCharSequence(Notification.EXTRA_TEXT)?.toString() ?: return null
            if (text.isBlank() || countOnly.matches(text.trim())) return null
            messages.put(JSONObject().put("text", text.take(4000)).put("time", sbn.postTime)
                .put("sender", if (group) "" else title).put("me", false))
        }

        val id = chatId(sbn, title)
        val chat = chats.getOrPut(id) { Chat(id) }
        chat.reply = n.actions?.firstOrNull { !it.remoteInputs.isNullOrEmpty() }
        val o = JSONObject().put("t", "wa_chat").put("chat", id).put("name", title.take(80))
            .put("group", group).put("canReply", chat.reply != null).put("messages", messages)
        chat.last = o
        return o
    }

    @Suppress("DEPRECATION")
    private fun senderOf(b: Bundle): String? {
        if (Build.VERSION.SDK_INT >= 28) {
            val person = b.getParcelable<android.app.Person>("sender_person")
            if (person != null) return person.name?.toString()
        }
        return b.getCharSequence("sender")?.toString()
    }

    /** The chat's notification went away (read on the phone): it can no longer be answered. */
    fun gone(sbn: StatusBarNotification): JSONObject? {
        val title = sbn.notification?.extras?.let {
            (it.getCharSequence(Notification.EXTRA_CONVERSATION_TITLE) ?: it.getCharSequence(Notification.EXTRA_TITLE))?.toString()?.trim()
        } ?: return null
        val chat = chats[chatId(sbn, title)] ?: return null
        chat.reply = null
        chat.last?.put("canReply", false)
        return JSONObject().put("t", "wa_gone").put("chat", chat.id)
    }

    /** Every chat seen since Nyx started, for a laptop that just connected. */
    fun all(): List<JSONObject> = chats.values.mapNotNull { it.last }

    /** Answer through WhatsApp's own Reply action, exactly as replying from the notification does. */
    fun reply(c: Context, chatId: String, text: String): String? {
        val chat = chats[chatId] ?: return "That chat isn't on your phone's notifications any more."
        val action = chat.reply ?: return "WhatsApp only lets Nyx reply while the chat's notification is showing on your phone."
        val inputs = action.remoteInputs ?: return "WhatsApp didn't offer a reply box for this chat."
        return try {
            val results = Bundle()
            for (ri in inputs) results.putCharSequence(ri.resultKey, text)
            val intent = Intent()
            RemoteInput.addResultsToIntent(inputs, intent, results)
            action.actionIntent.send(c, 0, intent)
            null
        } catch (e: Exception) {
            "WhatsApp didn't accept the reply: " + (e.message ?: e.javaClass.simpleName)
        }
    }
}
