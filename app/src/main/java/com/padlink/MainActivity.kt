package com.padlink

import android.app.Activity
import android.app.PendingIntent
import android.content.Intent
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.hardware.usb.UsbDevice
import android.hardware.usb.UsbManager
import android.os.Build
import android.os.Bundle
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.widget.LinearLayout
import android.widget.TextView
import java.net.Inet4Address
import java.net.NetworkInterface

class MainActivity : Activity() {
    private val usb by lazy { getSystemService(UsbManager::class.java) }

    /** Devices we ask permission for automatically. 0x28DE = Valve. */
    private val autoVendors = setOf(0x28DE)
    private val asked = mutableSetOf<String>()

    private lateinit var statusPill: LinearLayout
    private lateinit var statusDot: View
    private lateinit var statusTitle: TextView
    private lateinit var statusDetail: TextView
    private lateinit var tvName: TextView
    private lateinit var addressRow: LinearLayout
    private lateinit var deviceList: LinearLayout
    private lateinit var hint: TextView
    private lateinit var log: TextView

    private companion object {
        const val GREEN = 0xFF2BD47A.toInt()
        const val BLUE = 0xFF4C9BFF.toInt()
        const val AMBER = 0xFFFFB938.toInt()
        const val GREY = 0xFF8A93A6.toInt()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        statusPill = findViewById(R.id.statusPill)
        statusDot = findViewById(R.id.statusDot)
        statusTitle = findViewById(R.id.statusTitle)
        statusDetail = findViewById(R.id.statusDetail)
        tvName = findViewById(R.id.tvName)
        addressRow = findViewById(R.id.addressRow)
        deviceList = findViewById(R.id.deviceList)
        hint = findViewById(R.id.hint)
        log = findViewById(R.id.log)

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

    // ---------------------------------------------------------------------------------------

    private fun refresh() {
        val server = UsbIpService.server
        val all = usb.deviceList.values.sortedBy { it.deviceName }
        val shared = all.filter { usb.hasPermission(it) }
        val waitingPermission = all.filter { it.vendorId in autoVendors && !usb.hasPermission(it) }
        val inUse = shared.mapNotNull { d -> server?.clientOf(UsbIp.busId(d))?.let { d to it } }

        // Big status on the left.
        when {
            inUse.isNotEmpty() -> setStatus(GREEN, "Connected to your PC",
                "${label(inUse.first().first)} is in use by ${inUse.first().second}.")
            shared.any { it.vendorId in autoVendors } -> setStatus(BLUE, "Ready",
                "Controller plugged in. Open PadLink on your PC to start playing.")
            waitingPermission.isNotEmpty() -> setStatus(AMBER, "Allow access",
                "Select OK on the USB prompt so PadLink can share the controller.")
            else -> setStatus(GREY, "Plug in your controller",
                "Connect the Steam Controller or its wireless receiver to a USB port on this TV.")
        }

        // This TV: name + address chips.
        tvName.text = UsbIpService.tvName(this)
        addressRow.removeAllViews()
        val ips = localIpv4()
        if (ips.isEmpty()) addressRow.addView(chip("No network", GREY))
        ips.forEach { ip ->
            val tailscale = ip.startsWith("100.") && (ip.split('.')[1].toIntOrNull() ?: 0) in 64..127
            addressRow.addView(chip(if (tailscale) "Tailscale  $ip" else "Wi-Fi / LAN  $ip", if (tailscale) BLUE else GREY))
        }

        // Devices: controllers and anything shared; the TV's own internal USB gadgets are just counted.
        deviceList.removeAllViews()
        val visible = all.filter { usb.hasPermission(it) || it.vendorId in autoVendors }
        val hidden = all.size - visible.size
        if (visible.isEmpty()) deviceList.addView(emptyRow())
        if (hidden > 0) deviceList.addView(footnote(if (hidden == 1) "+ 1 other USB device (not shared)" else "+ $hidden other USB devices (not shared)"))
        visible.forEach { d ->
            val client = server?.clientOf(UsbIp.busId(d))
            val (state, color) = when {
                client != null -> "In use by $client" to GREEN
                usb.hasPermission(d) -> "Ready" to BLUE
                d.vendorId in autoVendors -> "Waiting for OK" to AMBER
                else -> "Not shared" to GREY
            }
            deviceList.addView(deviceRow(d, state, color), deviceList.childCount - (if (hidden > 0) 1 else 0))
        }

        hint.visibility = if (inUse.isEmpty()) View.VISIBLE else View.GONE
        log.text = StatusLog.text().lines().take(3).joinToString("\n")
    }

    private fun setStatus(color: Int, title: String, detail: String) {
        statusDot.background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(color) }
        statusPill.background = pill(withAlpha(color, 0x2E))
        statusTitle.text = title
        statusDetail.text = detail
    }

    private fun label(d: UsbDevice) = d.productName?.takeIf { it.isNotBlank() } ?: "USB device"

    private fun localIpv4(): List<String> =
        NetworkInterface.getNetworkInterfaces().toList()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { ni -> ni.inetAddresses.toList().filterIsInstance<Inet4Address>().mapNotNull { it.hostAddress } }

    // ---- tiny view builders ----

    private fun dp(v: Int) = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v.toFloat(), resources.displayMetrics).toInt()

    private fun withAlpha(color: Int, alpha: Int) = Color.argb(alpha, Color.red(color), Color.green(color), Color.blue(color))

    private fun pill(fill: Int) = GradientDrawable().apply { cornerRadius = dp(999).toFloat(); setColor(fill) }

    private fun chip(text: String, color: Int) = TextView(this).apply {
        this.text = text
        textSize = 14f
        setTextColor(if (color == GREY) 0xCCFFFFFF.toInt() else color)
        background = pill(withAlpha(color, 0x29))
        setPadding(dp(14), dp(6), dp(14), dp(6))
        layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT)
            .apply { marginEnd = dp(10) }
    }

    private fun deviceRow(d: UsbDevice, state: String, color: Int) = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
        setPadding(0, dp(14), 0, dp(14))
        addView(LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            addView(TextView(context).apply {
                text = label(d)
                textSize = 18f
                typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                setTextColor(Color.WHITE)
            })
            addView(TextView(context).apply {
                text = "%s  ·  %04x:%04x".format(d.manufacturerName ?: "USB", d.vendorId, d.productId)
                textSize = 13f
                maxLines = 1
                setTextColor(0x80FFFFFF.toInt())
            })
        })
        addView(chip(state, color).apply {
            layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT)
        })
    }

    private fun emptyRow() = TextView(this).apply {
        text = "No controller plugged in yet"
        textSize = 16f
        setTextColor(0x80FFFFFF.toInt())
        setPadding(0, dp(16), 0, dp(16))
    }

    private fun footnote(text: String) = TextView(this).apply {
        this.text = text
        textSize = 13f
        setTextColor(0x59FFFFFF)
        setPadding(0, dp(4), 0, dp(10))
    }
}
