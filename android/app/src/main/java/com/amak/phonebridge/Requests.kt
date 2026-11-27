package com.amak.phonebridge

/**
 * What the phone has asked the laptop to do and how each one went, for the "Requests to laptop"
 * card: Waiting… until the laptop answers, then Done or Didn't work (with why), and gone a few
 * seconds later. Nothing pending = nothing shown. Used from the main thread only.
 */
object Requests {
    const val WAITING = "waiting"
    const val DONE = "done"
    const val FAILED = "failed"

    /** How long Wake gets to bring the laptop back before it counts as not working. */
    const val WAKE_WAIT_MS = 40_000L
    /** Anything else the laptop hasn't answered by then didn't reach it. */
    const val ANSWER_WAIT_MS = 60_000L
    private const val SHOW_DONE_MS = 6_000L
    private const val SHOW_FAILED_MS = 15_000L
    /** Restart and shut down wait this long on the laptop; Cancel works until then. */
    const val COUNTDOWN_MS = 15_000L

    class Item(val id: Int, val kind: String, val label: String, val startedAt: Long) {
        var state = WAITING
        var msg = ""
        var endedAt = 0L
        /** Restart / shut down accepted: can still be cancelled until this time. */
        var cancelUntil = 0L
    }

    private var nextId = 1
    val items = mutableListOf<Item>()

    fun add(kind: String, label: String, now: Long = System.currentTimeMillis()): Item =
        Item(nextId++, kind, label, now).also { items.add(it) }

    /** The laptop answered the oldest waiting request of this kind. False if none was waiting. */
    fun finish(kind: String, ok: Boolean, msg: String, now: Long = System.currentTimeMillis()): Boolean {
        val it = items.firstOrNull { i -> i.kind == kind && i.state == WAITING } ?: return false
        it.state = if (ok) DONE else FAILED
        it.msg = msg
        it.endedAt = now
        if (ok && (kind == "restart" || kind == "shutdown")) it.cancelUntil = now + COUNTDOWN_MS
        return true
    }

    /** Times out what got no answer and drops finished ones after a moment. True if anything changed. */
    fun expire(now: Long = System.currentTimeMillis()): Boolean {
        var changed = false
        for (i in items) {
            if (i.state != WAITING) continue
            val limit = if (i.kind == "wake") WAKE_WAIT_MS else ANSWER_WAIT_MS
            if (now - i.startedAt >= limit) {
                i.state = FAILED
                i.msg = if (i.kind == "wake")
                    "The laptop didn't wake up. Windows has to let its Wi-Fi wake it (see Nyx's help on the laptop)."
                else "The laptop didn't answer."
                i.endedAt = now
                changed = true
            }
        }
        val gone = items.removeAll { i ->
            i.state != WAITING && now - i.endedAt >= (if (i.state == FAILED) SHOW_FAILED_MS else SHOW_DONE_MS) && now >= i.cancelUntil
        }
        return changed || gone
    }

    fun clear() { items.clear() }
}
