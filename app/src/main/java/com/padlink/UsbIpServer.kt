package com.padlink

import android.hardware.usb.UsbDevice
import android.hardware.usb.UsbManager
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.ConcurrentHashMap

/** Minimal USB/IP server: exports every USB device this app has permission for. */
class UsbIpServer(private val usb: UsbManager, private val deviceName: () -> String) {
    private var serverSocket: ServerSocket? = null
    private val sessions = ConcurrentHashMap<String, DeviceSession>() // busid -> session

    fun exportedDevices(): List<UsbDevice> =
        usb.deviceList.values.filter { usb.hasPermission(it) }.sortedBy { it.deviceName }

    private val clients = ConcurrentHashMap<String, String>() // busid -> PC address

    fun isAttached(busId: String) = sessions.containsKey(busId)

    /** Address of the PC currently using this device, or null. */
    fun clientOf(busId: String): String? = clients[busId]

    fun start() {
        val ss = ServerSocket(UsbIp.PORT).apply { reuseAddress = true }
        serverSocket = ss
        Thread({
            StatusLog.add("Listening on port ${UsbIp.PORT}")
            while (!ss.isClosed) {
                val s = try { ss.accept() } catch (_: IOException) { break }
                Thread({ handle(s) }, "usbip-client").start()
            }
        }, "usbip-accept").start()
    }

    fun stop() {
        try { serverSocket?.close() } catch (_: Exception) {}
        sessions.values.forEach { it.close() }
    }

    /** Called when a USB device is unplugged. */
    fun onDetached(d: UsbDevice) {
        sessions.values.filter { it.device.deviceName == d.deviceName }.forEach { it.close() }
    }

    private fun handle(s: Socket) {
        val peer = s.inetAddress
        if (!UsbIp.isAllowedClient(peer)) {
            StatusLog.add("Rejected ${peer.hostAddress} (not LAN/Tailscale)")
            s.close(); return
        }
        s.tcpNoDelay = true // latency matters more than anything here
        try {
            val inp = DataInputStream(BufferedInputStream(s.getInputStream()))
            val out = DataOutputStream(BufferedOutputStream(s.getOutputStream()))
            inp.readUnsignedShort() // version
            val code = inp.readUnsignedShort()
            inp.readInt() // status
            when (code) {
                UsbIp.OP_REQ_INFO -> {
                    out.writeShort(UsbIp.VERSION); out.writeShort(UsbIp.OP_REP_INFO); out.writeInt(0)
                    val name = deviceName().toByteArray(Charsets.UTF_8).take(200).toByteArray()
                    out.writeShort(name.size)
                    out.write(name)
                    out.flush()
                    s.close()
                }
                UsbIp.OP_REQ_DEVLIST -> {
                    val devs = exportedDevices()
                    out.writeShort(UsbIp.VERSION); out.writeShort(UsbIp.OP_REP_DEVLIST); out.writeInt(0)
                    out.writeInt(devs.size)
                    devs.forEach { UsbIp.writeDevice(out, it, withInterfaces = true) }
                    out.flush()
                    s.close()
                }
                UsbIp.OP_REQ_IMPORT -> {
                    val raw = ByteArray(32).also { inp.readFully(it) }
                    val busId = String(raw, Charsets.US_ASCII).trimEnd('\u0000')
                    val dev = exportedDevices().firstOrNull { UsbIp.busId(it) == busId }
                    out.writeShort(UsbIp.VERSION); out.writeShort(UsbIp.OP_REP_IMPORT)
                    if (dev == null || sessions.containsKey(busId)) {
                        out.writeInt(1); out.flush(); s.close()
                        StatusLog.add("Import of $busId refused (${if (dev == null) "not found" else "busy"})")
                        return
                    }
                    val session = DeviceSession(usb, dev, s, inp, out)
                    out.writeInt(0)
                    UsbIp.writeDevice(out, dev, withInterfaces = false)
                    out.flush()
                    sessions[busId] = session
                    clients[busId] = peer.hostAddress ?: "?"
                    StatusLog.add("${dev.productName ?: busId} attached by ${peer.hostAddress}")
                    try { session.run() } finally {
                        sessions.remove(busId)
                        clients.remove(busId)
                        StatusLog.listener?.invoke() // refresh the TV screen
                    }
                }
                else -> s.close()
            }
        } catch (e: Exception) {
            StatusLog.add("Client ${peer.hostAddress}: ${e.message}")
            try { s.close() } catch (_: Exception) {}
        }
    }
}
