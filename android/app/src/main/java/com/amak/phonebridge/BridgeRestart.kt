package com.amak.phonebridge

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/**
 * Brings the bridge back after the phone restarts or Nyx is updated, if it was on.
 * Without this, every app update quietly cut the laptop off until you opened Nyx.
 */
class BridgeRestart : BroadcastReceiver() {
    companion object {
        private const val PREFS = "ui"
        private const val KEY = "bridge_on"

        fun remember(c: Context, on: Boolean) =
            c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean(KEY, on).apply()

        fun wanted(c: Context) = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY, false)
    }

    override fun onReceive(c: Context, i: Intent) {
        if (i.action != Intent.ACTION_BOOT_COMPLETED && i.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        if (!wanted(c) || LinkService.instance != null) return
        try { c.startForegroundService(Intent(c, LinkService::class.java)) } catch (e: Exception) {}
    }
}
