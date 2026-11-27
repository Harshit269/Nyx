package com.amak.phonebridge

import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.widget.Toast

/**
 * "Nyx" in Android's share sheet: share any photo or file and it goes to the linked laptop.
 * No screen of its own. The files are opened right away, while the share still allows it.
 */
class ShareActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val uris = urisOf(intent)
        val svc = LinkService.instance
        when {
            uris.isEmpty() -> toast("Nothing to send.")
            svc == null || LinkService.linkKind.isEmpty() -> toast("Your laptop isn't linked. Open Nyx and turn Bridge on, with the laptop on the same Wi-Fi.")
            else -> {
                val files = uris.take(20).mapNotNull { FileShare.open(this, it) }
                if (files.isEmpty()) toast("Couldn't open that file.")
                else {
                    svc.sendFiles(files)
                    toast(if (files.size == 1) "Sending ${files[0].name} to your laptop" else "Sending ${files.size} files to your laptop")
                }
            }
        }
        finish()
    }

    private fun toast(s: String) = Toast.makeText(applicationContext, s, Toast.LENGTH_LONG).show()

    @Suppress("DEPRECATION")
    private fun urisOf(i: Intent): List<Uri> = when (i.action) {
        Intent.ACTION_SEND ->
            listOfNotNull(if (Build.VERSION.SDK_INT >= 33) i.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
                          else i.getParcelableExtra(Intent.EXTRA_STREAM))
        Intent.ACTION_SEND_MULTIPLE ->
            (if (Build.VERSION.SDK_INT >= 33) i.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java)
             else i.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM)) ?: emptyList()
        else -> emptyList()
    }
}
