package com.padlink

import android.app.Activity
import android.app.PendingIntent
import android.content.Intent
import android.graphics.Typeface
import android.hardware.usb.UsbManager
import android.os.Build
import android.os.Bundle
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import java.net.Inet4Address
import java.net.NetworkInterface

class MainActivity : Activity() {
    private lateinit var info: TextView
    private lateinit var log: TextView
    private val usb by lazy { getSystemService(UsbManager::class.java) }

    /** Devices we ask permission for automatically. 0x28DE = Valve. */
    private val autoVendors = setOf(0x28DE)
    private val asked = mutableSetOf<String>()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        info = TextView(this).apply { textSize = 20f; setPadding(48, 48, 48, 24) }
        log = TextView(this).apply { textSize = 14f; typeface = Typeface.MONOSPACE; setPadding(48, 0, 48, 48) }
        val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; addView(info); addView(log) }
        setContentView(ScrollView(this).apply { addView(col) })

        if (Build.VERSION.SDK_INT >= 33) requestPermissions(arrayOf("android.permission.POST_NOTIFICATIONS"), 0)
        startForegroundService(Intent(this, UsbIpService::class.java))
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        refresh() // USB_DEVICE_ATTACHED while open
    }

    override fun onResume() {
        super.onResume()
        StatusLog.listener = { runOnUiThread { refresh() } }
        requestMissingPermissions()
        refresh()
    }

    override fun onPause() {
        StatusLog.listener = null
        super.onPause()
    }

    private fun requestMissingPermissions() {
        val dev = usb.deviceList.values.firstOrNull {
            it.vendorId in autoVendors && !usb.hasPermission(it) && it.deviceName !in asked
        } ?: return
        asked += dev.deviceName // don't nag in a loop if the user says no
        val pi = PendingIntent.getBroadcast(
            this, 0, Intent("com.padlink.USB_PERMISSION").setPackage(packageName),
            PendingIntent.FLAG_MUTABLE,
        )
        usb.requestPermission(dev, pi) // dialog; onResume runs again afterwards
    }

    private fun refresh() {
        val ips = NetworkInterface.getNetworkInterfaces().toList()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { ni -> ni.inetAddresses.toList().filterIsInstance<Inet4Address>().map { ni.name to it.hostAddress } }
        val firstIp = ips.firstOrNull { it.second?.startsWith("100.") != true }?.second ?: "<tv-ip>"

        val sb = StringBuilder("PadLink — USB over IP\n\nThis TV: ${UsbIpService.tvName(this)}\n\n")
        sb.append("Addresses:\n")
        ips.forEach { (name, ip) -> sb.append("  $ip  ($name${if (ip?.startsWith("100.") == true) ", Tailscale" else ""})\n") }

        sb.append("\nShared devices:\n")
        val server = UsbIpService.server
        val devs = usb.deviceList.values.filter { usb.hasPermission(it) }
        if (devs.isEmpty()) sb.append("  none — plug in the controller and allow access\n")
        devs.forEach { d ->
            val busId = UsbIp.busId(d)
            val state = if (server?.isAttached(busId) == true) "ATTACHED" else "ready"
            sb.append("  [$busId] ${d.productName ?: "USB device"}  %04x:%04x  $state\n".format(d.vendorId, d.productId))
        }
        devs.firstOrNull()?.let {
            sb.append("\nOn the Windows PC run:\n  usbip attach -r $firstIp -b ${UsbIp.busId(it)}\n")
        }
        info.text = sb
        log.text = StatusLog.text()
    }
}
