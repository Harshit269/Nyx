package com.amak.phonebridge

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/**
 * The Allow / Deny buttons on the "Allow this laptop?" notification. Not exported:
 * only Nyx's own notification (an explicit, immutable PendingIntent) can reach it.
 */
class ApprovalReceiver : BroadcastReceiver() {
    companion object {
        const val EXTRA_ADDRESS = "address"
        const val EXTRA_ALLOW = "allow"
    }

    override fun onReceive(c: Context, i: Intent) {
        val address = i.getStringExtra(EXTRA_ADDRESS) ?: return
        LinkService.instance?.resolve(address, i.getBooleanExtra(EXTRA_ALLOW, false))
    }
}
