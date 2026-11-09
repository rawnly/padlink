package com.padlink

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.ServiceInfo
import android.hardware.usb.UsbDevice
import android.hardware.usb.UsbManager
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder
import android.os.PowerManager

/** Keeps the USB/IP server alive while the TV is in the background / screen off. */
class UsbIpService : Service() {
    companion object {
        @Volatile var server: UsbIpServer? = null

        /** The name set in TV Settings (e.g. "Living Room TV"), else the model. */
        fun tvName(ctx: Context): String =
            android.provider.Settings.Global.getString(ctx.contentResolver, "device_name")
                ?.takeIf { it.isNotBlank() } ?: Build.MODEL
    }

    private var wifiLock: WifiManager.WifiLock? = null
    private var wakeLock: PowerManager.WakeLock? = null

    private val detachReceiver = object : BroadcastReceiver() {
        override fun onReceive(c: Context, i: Intent) {
            @Suppress("DEPRECATION")
            val d = i.getParcelableExtra<UsbDevice>(UsbManager.EXTRA_DEVICE) ?: return
            server?.onDetached(d)
            StatusLog.add("${d.productName ?: d.deviceName} unplugged")
        }
    }

    override fun onCreate() {
        super.onCreate()
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel("padlink", "PadLink", NotificationManager.IMPORTANCE_LOW))
        val n = Notification.Builder(this, "padlink")
            .setContentTitle("PadLink running")
            .setContentText("Sharing USB devices on port ${UsbIp.PORT}")
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .build()
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(1, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        } else {
            startForeground(1, n)
        }

        // Low-latency Wi-Fi mode: stops Wi-Fi power-save from adding 10-100 ms bursts of lag.
        val wm = applicationContext.getSystemService(WifiManager::class.java)
        @Suppress("DEPRECATION")
        val mode = if (Build.VERSION.SDK_INT >= 29) WifiManager.WIFI_MODE_FULL_LOW_LATENCY else WifiManager.WIFI_MODE_FULL_HIGH_PERF
        wifiLock = wm.createWifiLock(mode, "padlink").apply { acquire() }
        wakeLock = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "padlink:server").apply { acquire() }

        val f = IntentFilter(UsbManager.ACTION_USB_DEVICE_DETACHED)
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(detachReceiver, f, RECEIVER_NOT_EXPORTED)
        else registerReceiver(detachReceiver, f)

        try {
            server = UsbIpServer(getSystemService(UsbManager::class.java)) { tvName(this) }.also { it.start() }
        } catch (e: Exception) {
            StatusLog.add("Server failed to start: ${e.message}")
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int) = START_STICKY

    override fun onDestroy() {
        unregisterReceiver(detachReceiver)
        server?.stop()
        server = null
        wifiLock?.release()
        wakeLock?.release()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null
}
