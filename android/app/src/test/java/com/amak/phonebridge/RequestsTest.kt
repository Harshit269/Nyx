package com.amak.phonebridge

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class RequestsTest {
    @After fun reset() = Requests.clear()

    @Test fun nothingPendingShowsNothing() {
        assertTrue(Requests.items.isEmpty())
        assertFalse(Requests.expire(0))
    }

    @Test fun answeredRequestShowsDoneThenGoes() {
        Requests.add("lock", "Lock", now = 0)
        assertTrue(Requests.finish("lock", ok = true, msg = "Locked.", now = 500))
        assertEquals(Requests.DONE, Requests.items[0].state)
        Requests.expire(3_000)
        assertEquals(1, Requests.items.size)        // still showing for a moment
        Requests.expire(7_000)
        assertTrue(Requests.items.isEmpty())        // then gone
    }

    @Test fun wakeWithoutTheLaptopComingBackSaysWhy() {
        Requests.add("wake", "Wake", now = 0)
        assertFalse(Requests.expire(30_000))
        assertTrue(Requests.expire(Requests.WAKE_WAIT_MS))
        assertEquals(Requests.FAILED, Requests.items[0].state)
        assertTrue(Requests.items[0].msg.contains("didn't wake"))
    }

    @Test fun restartStaysCancellableDuringCountdown() {
        Requests.add("restart", "Restart", now = 0)
        Requests.finish("restart", ok = true, msg = "Restarting in 15 seconds.", now = 1_000)
        Requests.expire(10_000)
        assertEquals(1, Requests.items.size)
        Requests.expire(1_000 + Requests.COUNTDOWN_MS)
        assertTrue(Requests.items.isEmpty())
    }

    @Test fun answersGoToTheOldestWaitingOfThatKind() {
        val a = Requests.add("sleep", "Sleep", now = 0)
        val b = Requests.add("sleep", "Sleep", now = 10)
        Requests.finish("sleep", ok = false, msg = "No.", now = 20)
        assertEquals(Requests.FAILED, a.state)
        assertEquals(Requests.WAITING, b.state)
        assertFalse(Requests.finish("lock", ok = true, msg = "", now = 30))
    }
}
