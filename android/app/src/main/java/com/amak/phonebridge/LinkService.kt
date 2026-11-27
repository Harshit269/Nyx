package com.amak.phonebridge

import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothServerSocket
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.provider.ContactsContract
import android.provider.Telephony
import android.telecom.TelecomManager
import android.telephony.SmsManager
import android.telephony.TelephonyManager
import org.json.JSONArray
import org.json.JSONObject
import java.io.BufferedInputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.ServerSocket
import java.net.Socket
import java.util.UUID
import java.util.concurrent.Executors
import kotlin.concurrent.thread

/**
 * Foreground service that waits for the Nyx desktop app on two doors at once:
 * a Bluetooth RFCOMM channel, and (while on Wi-Fi) a TCP port that the laptop finds
 * through a UDP beacon. Speaks newline-delimited JSON; Wi-Fi lines are AES-GCM sealed
 * after a mutual handshake with the secret this phone got when it paired over Bluetooth.
 */
@SuppressLint("MissingPermission")
class LinkService : Service() {

    companion object {
        val APP_UUID: UUID = UUID.fromString("8f2b6c1e-5d3a-4e7b-9c41-2a6d0b7e9f13")
        const val CHANNEL = "bridge"
        const val TCP_PORT = 47823
        const val BEACON_PORT = 47824
        const val APPROVE_CHANNEL = "approve"
        const val APPROVAL_ID = 7

        @Volatile
        var instance: LinkService? = null

        @Volatile
        var status: String = "stopped"

        /** Linked over the network or the USB cable: what the screen, camera, mic and sound streams need. */
        val onNetwork get() = linkKind == "Wi-Fi" || linkKind == "USB"

        /** "Wi-Fi", "USB" or "Bluetooth" while linked, else empty. */
        @Volatile
        var linkKind: String = ""

        /** The control screen listens here for pc_result / power_log / link changes (main thread). */
        @Volatile
        var onLaptopEvent: ((JSONObject) -> Unit)? = null
    }

    @Volatile private var running = false
    @Volatile private var btServer: BluetoothServerSocket? = null
    @Volatile private var tcpServer: ServerSocket? = null
    @Volatile private var link: Link? = null
    @Volatile private var linkLaptop: String = ""
    private val main = Handler(Looper.getMainLooper())
    private val io = Executors.newSingleThreadExecutor()
    private var lastNumber = ""

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(c: Context, i: Intent) {
            try {
                when (i.action) {
                    TelephonyManager.ACTION_PHONE_STATE_CHANGED -> onPhoneState(i)
                    Telephony.Sms.Intents.SMS_RECEIVED_ACTION -> onSmsReceived(i)
                }
            } catch (e: Exception) {
            }
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        instance = this
        AudioCapture.restoreIfLeftMuted(this)
        if (!MirrorService.running) ControlSwitch.set(this, false) // off (and unlisted) unless mirroring right now
        // switched on by hand before Nyx could manage it: keep it there for them
        if (!ControlSwitch.canToggle(this) && ControlService.enabled(this)) ControlSwitch.present(this, true)
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL, "Nyx link", NotificationManager.IMPORTANCE_LOW)
        )
        val n = Notification.Builder(this, CHANNEL)
            .setContentTitle("Nyx is running")
            .setContentText("Links to your laptop over Wi-Fi or Bluetooth")
            .setSmallIcon(R.drawable.ic_stat_nyx)
            .setOngoing(true)
            .build()
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(1, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        } else {
            startForeground(1, n)
        }

        val f = IntentFilter()
        f.addAction(TelephonyManager.ACTION_PHONE_STATE_CHANGED)
        f.addAction(Telephony.Sms.Intents.SMS_RECEIVED_ACTION)
        f.priority = 999
        if (Build.VERSION.SDK_INT >= 33) {
            registerReceiver(receiver, f, Context.RECEIVER_EXPORTED)
        } else {
            registerReceiver(receiver, f)
        }

        running = true
        Pairing.phoneId(this)
        thread(name = "bridge-accept") { acceptLoop() }
        thread(name = "wifi-accept") { wifiAcceptLoop() }
        thread(name = "usb-accept") { usbAcceptLoop() }
        thread(name = "wifi-beacon") { beaconLoop() }
        // change the wallpaper and the laptop's phone card follows
        if (Build.VERSION.SDK_INT >= 27) try {
            android.app.WallpaperManager.getInstance(this).addOnColorsChangedListener({ _, _ -> sendWallpaper() }, main)
        } catch (e: Exception) {
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_STICKY

    override fun onDestroy() {
        running = false
        try { unregisterReceiver(receiver) } catch (e: Exception) {}
        try { btServer?.close() } catch (e: Exception) {}
        try { tcpServer?.close() } catch (e: Exception) {}
        try { link?.close?.invoke() } catch (e: Exception) {}
        io.shutdownNow()
        linkKind = ""
        instance = null
        status = "stopped"
        super.onDestroy()
    }

    // ------------------------------------------------------------------ link

    private fun acceptLoop() {
        while (running) {
            try {
                val adapter = (getSystemService(Context.BLUETOOTH_SERVICE) as BluetoothManager).adapter
                if (adapter == null || !adapter.isEnabled) {
                    status = "Bluetooth is off"
                    Thread.sleep(3000)
                    continue
                }
                if (link == null) status = "waiting for laptop"
                val srv = adapter.listenUsingRfcommWithServiceRecord("Nyx", APP_UUID)
                btServer = srv
                val s = srv.accept()
                try { srv.close() } catch (e: Exception) {}
                btServer = null
                val addr = try { s.remoteDevice.address ?: "" } catch (e: Exception) { "" }
                val name = try { s.remoteDevice.name ?: "a laptop" } catch (e: Exception) { "a laptop" }
                val l = Link(s.inputStream, s.outputStream, { try { s.close() } catch (e: Exception) {} },
                    null, "Bluetooth", peer = addr, peerName = name)
                thread(name = "bt-link") {
                    // Any device already paired with this phone (a car, a friend's laptop) could
                    // open this channel. Nothing is shared until you allow it here once.
                    if (Pairing.isApprovedBt(this, addr) || (!recentlyDenied(addr) && awaitApproval(l))) {
                        runLink(l, LineIn(BufferedInputStream(s.inputStream)))
                    } else {
                        try { l.close() } catch (e: Exception) {}
                    }
                }
            } catch (e: Exception) {
                if (running) {
                    status = "error: " + (e.message ?: e.javaClass.simpleName)
                    try { Thread.sleep(2000) } catch (ie: InterruptedException) {}
                }
            }
        }
    }

    // ------------------------------------------------------------ approval

    /** A laptop asking to link. [code] is set for Wi-Fi pairing: it must match the laptop's. */
    class Pending(val address: String, val name: String, val code: String = "") {
        val done = java.util.concurrent.CountDownLatch(1)
        @Volatile var allowed = false
    }

    /** The laptop waiting to be allowed, if any. The main screen shows it with Allow / Deny. */
    @Volatile var pending: Pending? = null
        private set

    private fun awaitApproval(l: Link): Boolean {
        if (l.peer.isEmpty()) return false
        return ask(Pending(l.peer, l.peerName), 90)
    }

    /** Show the question (notification + main screen) and wait for Allow / Deny. */
    private fun ask(p: Pending, seconds: Long): Boolean {
        pending?.let { resolve(it.address, false) } // only one question at a time
        pending = p
        status = "waiting for you to allow " + p.name
        showApprovalNotification(p)
        emit(JSONObject().put("t", "approval"))
        val answered = p.done.await(seconds, java.util.concurrent.TimeUnit.SECONDS)
        if (pending === p) pending = null
        getSystemService(NotificationManager::class.java).cancel(APPROVAL_ID)
        emit(JSONObject().put("t", "approval"))
        return answered && p.allowed
    }

    /**
     * Pairing over Wi-Fi, no Bluetooth needed. Both sides make a fresh P-256 key pair; the
     * laptop commits to its key (a hash) before seeing ours, then reveals it, so nobody in
     * the middle can try keys until the 6-digit codes happen to match. Both screens show the
     * code; the pairing is saved only when you allow it here AND confirm it on the laptop.
     */
    private fun wifiPair(s: Socket, input: LineIn, out: java.io.OutputStream, first: JSONObject) {
        fun line(o: JSONObject) { out.write((o.toString() + "\n").toByteArray(Charsets.UTF_8)); out.flush() }
        val laptopId = first.getString("laptop").take(64)
        val laptopName = first.optString("name", "a laptop").take(64)
        val commit = LinkCipher.unb64(first.getString("commit"))
        if (commit.size != 32 || recentlyDenied("wifi:$laptopId")) return

        val keys = PairCrypto.newKeyPair()
        val pubPhone = PairCrypto.publicBytes(keys)
        line(JSONObject().put("t", "nyx_pair_pub").put("phone", Pairing.phoneId(this))
            .put("name", deviceName()).put("pub", LinkCipher.b64(pubPhone)))

        val reveal = JSONObject(input.readLine() ?: return)
        val pubLaptop = LinkCipher.unb64(reveal.getString("pub"))
        if (reveal.optString("t") != "nyx_pair_reveal" || !LinkCipher.same(PairCrypto.sha256(pubLaptop), commit)) return
        val z = PairCrypto.agree(keys, pubLaptop)
        val secret = PairCrypto.secret(z, pubPhone, pubLaptop)
        val code = PairCrypto.code(z, pubPhone, pubLaptop)

        s.soTimeout = 130_000 // the person needs time to compare the codes
        val allowed = ask(Pending("wifi:$laptopId", laptopName, code), 120)
        if (!allowed) { line(JSONObject().put("t", "nyx_pair_no")); return }
        line(JSONObject().put("t", "nyx_pair_ok"))
        val answer = JSONObject(input.readLine() ?: return)
        if (answer.optString("t") != "nyx_pair_ok") return
        Pairing.pair(this, laptopId, laptopName, LinkCipher.b64(secret))
        emit(JSONObject().put("t", "link").put("kind", ""))
    }

    /** Called from the notification buttons and the main screen. */
    /** Devices you said no to are not allowed to ask again for 10 minutes (no prompt spam). */
    private val deniedUntil = java.util.concurrent.ConcurrentHashMap<String, Long>()

    fun recentlyDenied(address: String) = (deniedUntil[address] ?: 0L) > System.currentTimeMillis()

    fun resolve(address: String, allow: Boolean) {
        val p = pending ?: return
        if (p.address != address) return
        if (allow && !address.startsWith("wifi:")) Pairing.approveBt(this, address)
        if (!allow) deniedUntil[address] = System.currentTimeMillis() + 10 * 60 * 1000
        p.allowed = allow
        p.done.countDown()
    }

    private fun showApprovalNotification(p: Pending) {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(APPROVE_CHANNEL, "Laptop requests", NotificationManager.IMPORTANCE_HIGH))
        fun action(allow: Boolean): android.app.PendingIntent {
            val i = Intent(this, ApprovalReceiver::class.java)
                .putExtra(ApprovalReceiver.EXTRA_ADDRESS, p.address)
                .putExtra(ApprovalReceiver.EXTRA_ALLOW, allow)
            return android.app.PendingIntent.getBroadcast(this, if (allow) 1 else 2, i,
                android.app.PendingIntent.FLAG_IMMUTABLE or android.app.PendingIntent.FLAG_UPDATE_CURRENT)
        }
        val open = android.app.PendingIntent.getActivity(this, 3, Intent(this, MainActivity::class.java),
            android.app.PendingIntent.FLAG_IMMUTABLE)
        val n = Notification.Builder(this, APPROVE_CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_nyx)
            .setContentTitle(if (p.code.isNotEmpty()) "Pair with ${p.name}? Code ${p.code.take(3)} ${p.code.drop(3)}"
                             else "Allow ${p.name} to link with Nyx?")
            .setContentText(if (p.code.isNotEmpty()) "Allow only if the laptop shows the same code."
                            else "It will see your notifications, texts and contacts, and can make calls.")
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(null, "Allow", action(true)).build())
            .addAction(Notification.Action.Builder(null, "Deny", action(false)).build())
            .setAutoCancel(true)
            .build()
        try { nm.notify(APPROVAL_ID, n) } catch (e: Exception) {}
    }

    /**
     * The Wi-Fi door: the laptop connects here after hearing our beacon. It only opens on
     * the Wi-Fi address (never on mobile data), and moves when the Wi-Fi address changes.
     */
    private fun wifiAcceptLoop() {
        while (running) {
            val ip = wifiIp()
            if (ip.isEmpty()) {
                try { Thread.sleep(3000) } catch (ie: InterruptedException) {}
                continue
            }
            try {
                val srv = ServerSocket()
                srv.reuseAddress = true
                srv.bind(InetSocketAddress(InetAddress.getByName(ip), TCP_PORT))
                srv.soTimeout = 5000
                tcpServer = srv
                while (running && wifiIp() == ip) {
                    val s = try { srv.accept() } catch (t: java.net.SocketTimeoutException) { continue }
                    // a flood of half-open connections must not pile up threads
                    if (handshakes.incrementAndGet() > 4) {
                        handshakes.decrementAndGet()
                        try { s.close() } catch (e: Exception) {}
                        continue
                    }
                    thread(name = "wifi-link") {
                        try { wifiHandshake(s) } finally { }
                    }
                }
                try { srv.close() } catch (e: Exception) {}
            } catch (e: Exception) {
                try { tcpServer?.close() } catch (e2: Exception) {}
                if (running) try { Thread.sleep(3000) } catch (ie: InterruptedException) {}
            }
        }
    }

    /**
     * The USB door: the laptop reaches it through the cable (adb's port forwarding), on the
     * phone's internal address only, so nothing on Wi-Fi or mobile data can see it. Same
     * pairing proof and encryption as Wi-Fi.
     */
    private fun usbAcceptLoop() {
        while (running) {
            try {
                val srv = ServerSocket()
                srv.reuseAddress = true
                srv.bind(InetSocketAddress(InetAddress.getByName("127.0.0.1"), TCP_PORT))
                srv.soTimeout = 5000
                while (running) {
                    val s = try { srv.accept() } catch (t: java.net.SocketTimeoutException) { continue }
                    if (handshakes.incrementAndGet() > 4) {
                        handshakes.decrementAndGet()
                        try { s.close() } catch (e: Exception) {}
                        continue
                    }
                    thread(name = "usb-link") { wifiHandshake(s, "USB") }
                }
                try { srv.close() } catch (e: Exception) {}
            } catch (e: Exception) {
                if (running) try { Thread.sleep(3000) } catch (ie: InterruptedException) {}
            }
        }
    }

    private val handshakes = java.util.concurrent.atomic.AtomicInteger(0)

    /**
     * Mutual proof of the pairing secret (see LinkCipher), then an encrypted link.
     * Anything that fails the handshake is dropped without hearing a single message.
     */
    private fun wifiHandshake(s: Socket, kind: String = "Wi-Fi") {
        var counted = true
        try {
            s.tcpNoDelay = true
            s.soTimeout = 8000
            val input = LineIn(BufferedInputStream(s.getInputStream()))
            input.maxLen = 16 * 1024 // nothing unverified gets to use much memory
            val out = s.getOutputStream()
            val nPhone = LinkCipher.random(16)
            out.write((JSONObject().put("t", "nyx_hello").put("phone", Pairing.phoneId(this))
                .put("n", LinkCipher.b64(nPhone)).toString() + "\n").toByteArray(Charsets.UTF_8))
            out.flush()

            val auth = JSONObject(input.readLine() ?: throw java.io.IOException("closed"))
            if (auth.optString("t") == "nyx_pair") { wifiPair(s, input, out, auth); return }
            val laptopId = auth.getString("laptop")
            val entry = Pairing.laptop(this, laptopId) ?: throw SecurityException("unknown laptop")
            val secret = LinkCipher.unb64(entry.getString("secret"))
            val nLaptop = LinkCipher.unb64(auth.getString("n"))
            if (auth.optString("t") != "nyx_auth" ||
                !LinkCipher.same(LinkCipher.unb64(auth.getString("mac")), LinkCipher.mac(secret, "L", nPhone, nLaptop))
            ) throw SecurityException("laptop failed the pairing check")

            out.write((JSONObject().put("t", "nyx_auth_ok")
                .put("mac", LinkCipher.b64(LinkCipher.mac(secret, "P", nLaptop, nPhone))).toString() + "\n")
                .toByteArray(Charsets.UTF_8))
            out.flush()
            // the laptop pings every 30 s; this long without a word means it went to sleep or
            // dropped off Wi-Fi without closing the socket. End the link, so the phone shows
            // the truth and Wake sends its signal.
            s.soTimeout = 75_000

            val cipher = LinkCipher(LinkCipher.mac(secret, "K", nPhone, nLaptop))
            val l = Link(s.getInputStream(), out, { try { s.close() } catch (e: Exception) {} }, cipher, kind, laptopId)
            handshakes.decrementAndGet() // verified: no longer counts against the handshake limit
            counted = false
            input.maxLen = 4 * 1024 * 1024
            runLink(l, input)
        } catch (e: Exception) {
            try { s.close() } catch (e2: Exception) {}
        } finally {
            if (counted) handshakes.decrementAndGet()
        }
    }

    /**
     * While on Wi-Fi and paired, tell the laptop we're here every two seconds. Also while
     * linked over Bluetooth, so the laptop can move over to the faster Wi-Fi link.
     */
    private fun beaconLoop() {
        var sock: DatagramSocket? = null
        while (running) {
            try {
                val current = link
                // announced even before pairing, so the laptop can offer "Pair" without Bluetooth
                if ((current == null || current.kind == "Bluetooth") && wifiIp().isNotEmpty()) {
                    if (sock == null) sock = DatagramSocket().also { it.broadcast = true }
                    val data = JSONObject().put("nyx", 1).put("phone", Pairing.phoneId(this)).put("port", TCP_PORT)
                        .put("name", deviceName().take(48))
                        .toString().toByteArray(Charsets.UTF_8)
                    for (target in broadcastTargets()) {
                        try { sock.send(DatagramPacket(data, data.size, target, BEACON_PORT)) } catch (e: Exception) {}
                    }
                }
                // keep the laptop's phone card current (only sends when something changed)
                if (link != null) sendStatus(force = false)
            } catch (e: Exception) {
                try { sock?.close() } catch (e2: Exception) {}
                sock = null
            }
            try { Thread.sleep(2000) } catch (ie: InterruptedException) {}
        }
        try { sock?.close() } catch (e: Exception) {}
    }

    private fun broadcastTargets(): List<InetAddress> {
        val out = mutableListOf<InetAddress>(InetAddress.getByName("255.255.255.255"))
        try {
            for (ni in NetworkInterface.getNetworkInterfaces() ?: return out) {
                if (!ni.isUp || ni.isLoopback) continue
                for (ia in ni.interfaceAddresses) ia.broadcast?.let { out.add(it) }
            }
        } catch (e: Exception) {
        }
        return out.distinct()
    }

    /** Make this the one live link (dropping any older one), then serve it until it closes. */
    private fun runLink(l: Link, input: LineIn) {
        val old = link
        link = l
        linkKind = l.kind
        linkLaptop = l.laptopId
        if (old != null && old !== l) try { old.close() } catch (e: Exception) {}
        status = "connected over " + l.kind
        if (l.laptopId.isNotEmpty()) {
            Pairing.setCurrent(this, l.laptopId)
            Pairing.update(this, l.laptopId, JSONObject().put("linked_since", System.currentTimeMillis()))
        }
        try {
            send(
                JSONObject().put("t", "hello")
                    .put("phone", Pairing.phoneId(this))
                    .put("paired", Pairing.ids(this))
                    .put("device", deviceName())
                    .put("android", Build.VERSION.RELEASE)
                    .put("ip", wifiIp())
                    .put("proto", 3)
                    .put("screen", true) // can be a second screen
                    .put("camera", true) // can be a webcam
                    .put("mic", true)    // can be a microphone
                    .put("mirror", true) // can show its screen over Wi-Fi
                    .put("audio", Build.VERSION.SDK_INT >= 29) // can play its sound on the laptop
                    .put("app", try { packageManager.getPackageInfo(packageName, 0).versionName } catch (e: Exception) { "" })
                    .put("notifAccess", NotifListener.hasAccess(this))
                    .put("wallAccess", Wallpaper.canReadWallpaper())
                    .put("controlAuto", ControlSwitch.canToggle(this)) // payment-app fix already done
            )
            NotifListener.rebindIfNeeded(this)
            sendStatus(force = true)
            sendWallpaper()
            // only now tell the screen: anything it asks for goes out after the hello
            emit(JSONObject().put("t", "link").put("kind", l.kind))
            while (running && link === l) {
                val raw = input.readLine() ?: break
                if (raw.isBlank()) continue
                // a Wi-Fi line that fails to decrypt is tampering or a broken link: end it
                val line = l.cipher?.open(raw) ?: raw
                try {
                    handleCommand(JSONObject(line), l)
                } catch (e: Exception) {
                    send(JSONObject().put("t", "error").put("msg", e.message ?: e.javaClass.simpleName))
                }
            }
        } catch (e: Exception) {
        } finally {
            try { l.close() } catch (e: Exception) {}
            FileShare.abortAll(this) // half-received files from this link are thrown away
            if (link === l) {
                link = null
                linkKind = ""
                status = "waiting for laptop"
                if (linkLaptop.isNotEmpty())
                    Pairing.update(this, linkLaptop, JSONObject().put("last_seen", System.currentTimeMillis()))
                emit(JSONObject().put("t", "link").put("kind", ""))
            }
        }
    }

    private fun emit(o: JSONObject) {
        main.post { try { onLaptopEvent?.invoke(o) } catch (e: Exception) {} }
    }

    /**
     * Writes one message and waits until it's on the wire, so a big file is sent at the
     * link's pace instead of piling up in memory. Returns false when there's no link.
     */
    fun sendNow(o: JSONObject): Boolean {
        val text = o.toString()
        return try {
            io.submit<Boolean> {
                val l = link ?: return@submit false
                try {
                    val line = l.cipher?.seal(text) ?: text
                    l.output.write((line + "\n").toByteArray(Charsets.UTF_8))
                    l.output.flush()
                    true
                } catch (e: Exception) {
                    try { l.close() } catch (e2: Exception) {}
                    false
                }
            }.get()
        } catch (e: Exception) { false }
    }

    private val sendingNames = java.util.concurrent.ConcurrentHashMap<String, String>()

    /** Sends files to the linked laptop, one after another, on a background thread. */
    fun sendFiles(files: List<FileShare.Outgoing>, onDone: ((String?) -> Unit)? = null) {
        Thread {
            var problem: String? = null
            val laptop = Pairing.current(this)?.second?.optString("name")?.ifEmpty { null } ?: "your laptop"
            for (f in files) {
                problem = FileShare.send(this, f, laptop) { msg ->
                    if (msg.optString("t") == "file_start") sendingNames[msg.optString("id")] = f.name
                    sendNow(msg)
                } ?: problem
            }
            if (problem != null) FileShare.sentResult(this, files.firstOrNull()?.name ?: "file", false, problem)
            main.post { onDone?.invoke(problem) }
        }.apply { name = "nyx-send-files" }.start()
    }

    /** Queue one JSON message for the laptop. Safe to call from any thread. */
    fun send(o: JSONObject) {
        val text = o.toString()
        try {
            io.execute {
                val l = link ?: return@execute
                try {
                    val line = l.cipher?.seal(text) ?: text
                    l.output.write((line + "\n").toByteArray(Charsets.UTF_8))
                    l.output.flush()
                } catch (e: Exception) {
                    // close only the link this write failed on, never a newer one
                    try { l.close() } catch (e2: Exception) {}
                }
            }
        } catch (e: Exception) {
        }
    }

    // ---------------------------------------------------------- laptop control

    /** lock, sleep, restart, shutdown or cancel. Returns false when no laptop is linked. */
    fun laptopCommand(action: String): Boolean {
        if (link == null) return false
        send(JSONObject().put("t", "pc").put("action", action))
        return true
    }

    // ------------------------------------------------------------ phone card

    /** The name you gave the phone in Settings ("Sam's Nothing Phone (3)"), else make + model. */
    private fun deviceName(): String {
        val user = try { android.provider.Settings.Global.getString(contentResolver, "device_name") } catch (e: Exception) { null }
        return if (!user.isNullOrBlank()) user else Build.MANUFACTURER + " " + Build.MODEL
    }

    @Volatile private var lastStatus = ""

    /** Battery, charging, signal bars, Wi-Fi and Bluetooth, for the laptop's phone card. */
    private fun sendStatus(force: Boolean) {
        val o = JSONObject().put("t", "status")
        try {
            val bi = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
            val level = bi?.getIntExtra(android.os.BatteryManager.EXTRA_LEVEL, -1) ?: -1
            val scale = bi?.getIntExtra(android.os.BatteryManager.EXTRA_SCALE, 100) ?: 100
            if (level >= 0 && scale > 0) o.put("battery", level * 100 / scale)
            o.put("charging", (bi?.getIntExtra(android.os.BatteryManager.EXTRA_PLUGGED, 0) ?: 0) != 0)
            o.put("saver", getSystemService(android.os.PowerManager::class.java)?.isPowerSaveMode == true)
        } catch (e: Exception) {
        }
        try {
            if (Build.VERSION.SDK_INT >= 28) {
                val ss = getSystemService(TelephonyManager::class.java)?.signalStrength
                if (ss != null) o.put("signal", ss.level) // 0..4
            }
        } catch (e: Exception) {
        }
        o.put("wifi", wifiIp().isNotEmpty())
        o.put("bt", try { (getSystemService(Context.BLUETOOTH_SERVICE) as BluetoothManager).adapter?.isEnabled == true } catch (e: Exception) { false })
        val text = o.toString()
        if (!force && text == lastStatus) return
        lastStatus = text
        send(o)
    }

    /**
     * The picture for the laptop's phone card. Android doesn't let apps read the wallpaper
     * itself, so this is the picture chosen in Nyx (small JPEG) plus the wallpaper's main
     * colour, which any app may read.
     */
    fun sendWallpaper() {
        Thread {
            try {
                val bmp = Wallpaper.picture(this)
                val o = JSONObject().put("t", "wallpaper")
                if (bmp != null) o.put("jpg", Wallpaper.toJpegBase64(bmp)) else o.put("why", Wallpaper.lastWhy.take(160))
                Wallpaper.mainColor(this)?.let { o.put("color", it) }
                send(o)
            } catch (e: Exception) {
            }
        }.start()
    }

    fun requestPowerLog(): Boolean {
        if (link == null) return false
        send(JSONObject().put("t", "get_power_log"))
        return true
    }

    // -------------------------------------------------------------- commands

    @Suppress("DEPRECATION") // acceptRingingCall/endCall are the only call controls a non-dialer app gets
    private fun handleCommand(c: JSONObject, l: Link) {
        when (c.optString("t")) {
            // a pairing secret is only ever accepted over Bluetooth, never over Wi-Fi
            "pair" -> if (l.kind == "Bluetooth") {
                val id = c.getString("laptop")
                Pairing.pair(this, id, c.optString("name", "Laptop"), c.getString("secret"))
                linkLaptop = id
                send(JSONObject().put("t", "paired").put("laptop", id))
                emit(JSONObject().put("t", "link").put("kind", l.kind))
            }
            "laptop_info" -> {
                val id = c.optString("laptop")
                if (id.isNotEmpty() && Pairing.laptop(this, id) != null) {
                    linkLaptop = id
                    Pairing.setCurrent(this, id)
                    Pairing.update(this, id, JSONObject()
                        .put("name", c.optString("name", "Laptop"))
                        .put("macs", c.optJSONArray("macs") ?: JSONArray())
                        .put("bcast", c.optJSONArray("bcast") ?: JSONArray())
                        .put("control", c.optBoolean("control", true))
                        .put("linked_since", System.currentTimeMillis()))
                    emit(JSONObject().put("t", "link").put("kind", l.kind))
                }
            }
            "get_status" -> { sendStatus(force = true); sendWallpaper() }
            "file_start", "file_part", "file_end", "file_cancel" -> FileShare.onMessage(this, c)?.let { send(it) }
            "file_done" -> {
                val name = sendingNames.remove(c.optString("id")) ?: "file"
                FileShare.sentResult(this, name, c.optBoolean("ok"), c.optString("msg"))
                emit(c.put("name", name))
            }
            "get_wa" -> if (WhatsApp.ENABLED) WhatsApp.all().forEach { send(it) }
            "ring" -> FindPhone.start(this) { send(JSONObject().put("t", "ring_done")) }
            "ring_stop" -> FindPhone.stop(this)
            // second screen: a fresh key for one session, only ever sent inside this link
            "screen_offer" -> {
                val key = LinkCipher.unb64(c.getString("key"))
                if (key.size == 32) ScreenActivity.offer(this, key)
            }
            "screen_stop" -> ScreenActivity.stopFromLaptop(null)
            // webcam: a fresh key for one session, only ever sent inside this link
            "cam_offer" -> {
                val key = LinkCipher.unb64(c.getString("key"))
                if (key.size == 32) CameraActivity.offer(this, key)
            }
            "cam_stop" -> CameraActivity.stopFromLaptop(null)
            // microphone: same idea, its own fresh key
            "mic_offer" -> {
                val key = LinkCipher.unb64(c.getString("key"))
                if (key.size == 32) MicService.offer(this, key)
            }
            "mic_stop", "mic_error" -> MicService.stopFromLaptop()
            // screen mirroring over Wi-Fi
            "mirror_offer" -> {
                val key = LinkCipher.unb64(c.getString("key"))
                // "with sound": a second key for the phone's audio, on the same sharing session
                MirrorService.pendingAudioKey = c.optString("audioKey").takeIf { it.isNotEmpty() }
                    ?.let { LinkCipher.unb64(it) }?.takeIf { it.size == 32 }
                MirrorService.pendingVideo = c.optBoolean("h264", false)
                MirrorService.pendingSize = c.optInt("size", 1600)
                MirrorService.pendingFps = c.optInt("fps", 60)
                if (key.size == 32) MirrorService.offer(this, key)
            }
            "mirror_stop" -> MirrorService.stopFromLaptop()
            "mirror_input" -> ControlService.instance?.act(c)
            // the phone's sound on the laptop
            "audio_offer" -> {
                val key = LinkCipher.unb64(c.getString("key"))
                if (key.size == 32) AudioShareService.offer(this, key)
            }
            "audio_stop" -> AudioShareService.stopFromLaptop()
            // the laptop's mute button: muted there = play it on the phone instead
            // which apps' notifications the laptop wants: the list to choose from, and your choice
            "get_notif_apps" -> send(NotifApps.list(this, NotifListener.instance?.activeApps() ?: emptyMap()))
            "notif_mute" -> {
                val arr = c.optJSONArray("pkgs") ?: JSONArray()
                val before = NotifApps.muted(this)
                NotifApps.setMuted(this, (0 until arr.length()).map { arr.optString(it) })
                // switched back on: send what that app has up right now
                if (NotifApps.muted(this) != before) NotifListener.instance?.sendActive(this)
            }
            // the laptop just allowed the on/off switch: turn control off now, unless mirroring
            "control_auto" -> {
                if (!MirrorService.running) ControlSwitch.set(this, false)
                send(JSONObject().put("t", "control_auto_done").put("ok", ControlSwitch.canToggle(this)))
            }
            "audio_here" -> AudioCapture.current?.toLaptop = c.optBoolean("laptop", true)
            "cam_error" -> CameraActivity.stopFromLaptop(c.optString("msg").take(300))
            "screen_windows" -> c.optJSONArray("items")?.let { ScreenActivity.windows(it) }
            "screen_error" -> ScreenActivity.stopFromLaptop(c.optString("msg").take(300))
            "get_notifs" -> {
                val nl = NotifListener.instance
                if (nl != null) nl.sendActive(this) else NotifListener.rebindIfNeeded(this)
                send(JSONObject().put("t", "notif_access").put("on", NotifListener.hasAccess(this)).put("bound", nl != null))
            }
            "wa_reply" -> if (WhatsApp.ENABLED) {
                val text = c.getString("text").take(4000)
                val problem = WhatsApp.reply(this, c.getString("chat"), text)
                send(JSONObject().put("t", "wa_sent").put("chat", c.getString("chat")).put("ok", problem == null)
                    .put("msg", problem ?: ""))
            }
            "pc_result" -> emit(c)
            "power_log" -> {
                if (linkLaptop.isNotEmpty())
                    Pairing.update(this, linkLaptop, JSONObject().put("power_log", c.optJSONArray("items") ?: JSONArray()))
                emit(c)
            }
            "ping" -> send(JSONObject().put("t", "pong"))
            // each feature checks its own permission first, and tells the laptop plainly what's
            // missing instead of passing on a raw Android error
            "get_contacts" -> if (need(android.Manifest.permission.READ_CONTACTS, "contacts"))
                send(JSONObject().put("t", "contacts").put("items", readContacts()))
            "get_sms" -> if (need(android.Manifest.permission.READ_SMS, "sms"))
                send(JSONObject().put("t", "sms_list").put("items", readSms(c.optInt("limit", 300))))
            "get_calls" -> if (need(android.Manifest.permission.READ_CALL_LOG, "calls"))
                send(JSONObject().put("t", "calls").put("items", readCalls(c.optInt("limit", 60).coerceIn(1, 200))))
            "send_sms" -> if (need(android.Manifest.permission.SEND_SMS, "send_sms"))
                sendSms(c.getString("to"), c.getString("body"))
            "dial" -> if (need(android.Manifest.permission.CALL_PHONE, "calls")) dial(c.getString("number"))
            "answer" -> if (need(android.Manifest.permission.ANSWER_PHONE_CALLS, "calls")) telecom().acceptRingingCall()
            "hangup" -> if (Build.VERSION.SDK_INT >= 28) {
                if (need(android.Manifest.permission.ANSWER_PHONE_CALLS, "calls")) telecom().endCall()
            } else {
                send(JSONObject().put("t", "error").put("msg", "Hang up needs Android 9 or newer"))
            }
            "dismiss_notif" -> NotifListener.instance?.dismiss(c.getString("id"))
            "notif_read" -> {
                val id = c.getString("id")
                val ok = NotifListener.instance?.markRead(id) == true
                send(JSONObject().put("t", "notif_read_done").put("id", id).put("ok", ok))
            }
        }
    }

    /**
     * True when the permission is granted. Otherwise tells the laptop which feature is blocked,
     * and whether Android's "restricted settings" lock (sideloaded apps, Android 13+) is the
     * reason, so the laptop can say exactly what to tap.
     */
    private fun need(permission: String, what: String): Boolean {
        if (checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED) return true
        val restricted = Build.VERSION.SDK_INT >= 33 && what in setOf("sms", "send_sms", "calls")
        send(JSONObject().put("t", "error").put("code", "permission").put("what", what).put("restricted", restricted)
            .put("msg", "Nyx on the phone isn't allowed to use " + when (what) {
                "sms", "send_sms" -> "your texts"
                "contacts" -> "your contacts"
                else -> "calls"
            } + " yet."))
        return false
    }

    private fun telecom(): TelecomManager = getSystemService(Context.TELECOM_SERVICE) as TelecomManager

    private fun dial(number: String) {
        telecom().placeCall(Uri.fromParts("tel", number, null), Bundle())
    }

    @Suppress("DEPRECATION")
    private fun sendSms(to: String, body: String) {
        try {
            val sm: SmsManager = if (Build.VERSION.SDK_INT >= 31) {
                getSystemService(SmsManager::class.java)
            } else {
                SmsManager.getDefault()
            }
            val parts = sm.divideMessage(body)
            sm.sendMultipartTextMessage(to, null, parts, null, null)
            send(JSONObject().put("t", "sms_sent").put("ok", true).put("to", to))
        } catch (e: Exception) {
            send(
                JSONObject().put("t", "sms_sent").put("ok", false).put("to", to)
                    .put("msg", e.message ?: e.javaClass.simpleName)
            )
        }
    }

    // ------------------------------------------------------------------ data

    private fun readContacts(): JSONArray {
        val arr = JSONArray()
        val cols = arrayOf(
            ContactsContract.CommonDataKinds.Phone.DISPLAY_NAME,
            ContactsContract.CommonDataKinds.Phone.NUMBER
        )
        contentResolver.query(
            ContactsContract.CommonDataKinds.Phone.CONTENT_URI, cols, null, null,
            ContactsContract.CommonDataKinds.Phone.DISPLAY_NAME + " ASC"
        )?.use { cur ->
            while (cur.moveToNext()) {
                arr.put(
                    JSONObject()
                        .put("name", cur.getString(0) ?: "")
                        .put("number", cur.getString(1) ?: "")
                )
            }
        }
        return arr
    }

    /** Recent calls, newest first: who, which way (in / out / missed), when, how long. */
    private fun readCalls(limit: Int): JSONArray {
        val arr = JSONArray()
        val cols = arrayOf(
            android.provider.CallLog.Calls.NUMBER, android.provider.CallLog.Calls.CACHED_NAME,
            android.provider.CallLog.Calls.TYPE, android.provider.CallLog.Calls.DATE,
            android.provider.CallLog.Calls.DURATION
        )
        contentResolver.query(android.provider.CallLog.Calls.CONTENT_URI, cols, null, null,
            android.provider.CallLog.Calls.DATE + " DESC")?.use { cur ->
            while (arr.length() < limit && cur.moveToNext()) {
                val kind = when (cur.getInt(2)) {
                    android.provider.CallLog.Calls.INCOMING_TYPE -> "in"
                    android.provider.CallLog.Calls.OUTGOING_TYPE -> "out"
                    android.provider.CallLog.Calls.MISSED_TYPE, android.provider.CallLog.Calls.REJECTED_TYPE -> "missed"
                    else -> continue // blocked, voicemail and the like
                }
                arr.put(JSONObject()
                    .put("number", cur.getString(0) ?: "")
                    .put("name", cur.getString(1) ?: "")
                    .put("kind", kind)
                    .put("time", cur.getLong(3))
                    .put("duration", cur.getLong(4)))
            }
        }
        return arr
    }

    private fun readSms(limit: Int): JSONArray {
        val arr = JSONArray()
        val cols = arrayOf("address", "body", "date", "type", "read")
        // inbox (1) and sent (2) only; filtering in the query keeps drafts and failed
        // messages from being scanned at all
        contentResolver.query(
            Uri.parse("content://sms"), cols, "type IN (1, 2)", null, "date DESC"
        )?.use { cur ->
            var n = 0
            while (n < limit && cur.moveToNext()) {
                val type = cur.getInt(3)
                arr.put(
                    JSONObject()
                        .put("address", cur.getString(0) ?: "")
                        .put("body", cur.getString(1) ?: "")
                        .put("time", cur.getLong(2))
                        .put("inbox", type == 1)
                        .put("read", cur.getInt(4) == 1) // so the laptop's unread texts match the phone
                )
                n++
            }
        }
        return arr
    }

    private fun contactName(number: String): String {
        if (number.isEmpty()) return ""
        return try {
            val uri = Uri.withAppendedPath(
                ContactsContract.PhoneLookup.CONTENT_FILTER_URI, Uri.encode(number)
            )
            var name = ""
            contentResolver.query(
                uri, arrayOf(ContactsContract.PhoneLookup.DISPLAY_NAME), null, null, null
            )?.use { cur ->
                if (cur.moveToFirst()) name = cur.getString(0) ?: ""
            }
            name
        } catch (e: Exception) {
            ""
        }
    }

    private fun wifiIp(): String {
        try {
            val ifaces = NetworkInterface.getNetworkInterfaces() ?: return ""
            for (ni in ifaces) {
                if (!ni.isUp || ni.isLoopback || !ni.name.startsWith("wlan")) continue
                for (a in ni.inetAddresses) {
                    if (a is Inet4Address && !a.isLoopbackAddress) return a.hostAddress ?: ""
                }
            }
        } catch (e: Exception) {
        }
        return ""
    }

    // ---------------------------------------------------------------- events

    @Suppress("DEPRECATION")
    private fun onPhoneState(i: Intent) {
        val state = i.getStringExtra(TelephonyManager.EXTRA_STATE) ?: return
        // With READ_CALL_LOG granted, Android sends every phone-state broadcast twice:
        // once without the number and once with it. Skip the bare ringing copy, or the
        // laptop opens the call panel as "Unknown number" before the real one arrives.
        if (state == TelephonyManager.EXTRA_STATE_RINGING &&
            !i.hasExtra(TelephonyManager.EXTRA_INCOMING_NUMBER) &&
            checkSelfPermission(android.Manifest.permission.READ_CALL_LOG) == PackageManager.PERMISSION_GRANTED
        ) return
        val num = i.getStringExtra(TelephonyManager.EXTRA_INCOMING_NUMBER) ?: ""
        if (num.isNotEmpty()) lastNumber = num
        val s = when (state) {
            TelephonyManager.EXTRA_STATE_RINGING -> "ringing"
            TelephonyManager.EXTRA_STATE_OFFHOOK -> "offhook"
            else -> "idle"
        }
        send(
            JSONObject().put("t", "call").put("state", s)
                .put("number", lastNumber)
                .put("name", contactName(lastNumber))
        )
        if (s == "idle") lastNumber = ""
    }

    private fun onSmsReceived(i: Intent) {
        val msgs = Telephony.Sms.Intents.getMessagesFromIntent(i) ?: return
        if (msgs.isEmpty()) return
        val from = msgs[0].originatingAddress ?: ""
        val body = msgs.joinToString("") { it.messageBody ?: "" }
        send(
            JSONObject().put("t", "sms")
                .put("address", from)
                .put("name", contactName(from))
                .put("body", body)
                .put("time", System.currentTimeMillis())
                .put("inbox", true)
        )
    }
}
