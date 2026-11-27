package com.amak.phonebridge

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class TodayCountTest {
    private fun run(posts: List<Triple<String, String, Boolean>>): Int {
        val seen = mutableListOf<String>()
        var n = 0
        for ((key, words, quiet) in posts) {
            val m = TodayCount.marks(seen, key, words, quiet) ?: continue
            seen += m; n++
        }
        return n
    }

    @Test fun a_refreshed_notification_counts_once() =
        assertEquals(1, run(List(50) { Triple("sys|charging", "Battery health limits charging to 90%", false) }))

    @Test fun new_messages_in_one_chat_each_count() =
        assertEquals(3, run(listOf(Triple("wa|mum", "hi", false), Triple("wa|mum", "hi\nare you there", false), Triple("wa|mum", "hi\nare you there\ncall me", false))))

    @Test fun quiet_progress_updates_dont_count() =
        assertEquals(1, run((0..100).map { Triple("dl|file", "Downloading $it%", true) }))

    @Test fun a_quiet_notification_still_counts_the_first_time() =
        assertNotNull(TodayCount.marks(emptyList(), "x", "first", quiet = true))

    @Test fun different_apps_with_the_same_words_both_count() =
        assertEquals(2, run(listOf(Triple("a|1", "Hello", false), Triple("b|1", "Hello", false))))

    @Test fun same_key_same_words_is_not_new() {
        val m = TodayCount.marks(emptyList(), "k", "w", false)!!
        assertNull(TodayCount.marks(m, "k", "w", false))
    }

    @Test fun hours_put_each_arrival_in_its_hour_newest_last() {
        val now = 100 * TodayCount.HOUR
        val times = listOf(now - 10_000, now - 59 * 60_000, now - TodayCount.HOUR - 1, now - 11 * TodayCount.HOUR - 5, now - 12 * TodayCount.HOUR - 5, now + 5000)
        val bars = TodayCount.hours(times, now)
        assertArrayEquals(intArrayOf(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2), bars)
    }

    @Test fun keep_drops_anything_older_than_a_day() {
        val now = 100 * TodayCount.HOUR
        assertEquals(listOf(now - 23 * TodayCount.HOUR), TodayCount.keep(listOf(now - 25 * TodayCount.HOUR, now - 23 * TodayCount.HOUR), now))
    }
}
