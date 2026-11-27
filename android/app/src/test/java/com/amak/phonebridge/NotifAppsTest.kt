package com.amak.phonebridge

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class NotifAppsTest {
    @Test fun package_names_are_accepted() {
        assertTrue(NotifApps.valid("com.whatsapp"))
        assertTrue(NotifApps.valid("com.google.android.apps.messaging"))
        assertTrue(NotifApps.valid("in.org.npci.upiapp"))
    }

    @Test fun anything_else_is_dropped() {
        for (p in listOf("", "com.x/../y", "a b", "com.x;rm", "x".repeat(201))) assertFalse(p, NotifApps.valid(p))
    }
}
