package com.padlink

import android.hardware.usb.UsbDevice
import java.io.DataOutputStream
import java.net.Inet4Address
import java.net.Inet6Address
import java.net.InetAddress

/** USB/IP wire protocol constants + helpers (all fields big-endian). */
object UsbIp {
    const val PORT = 3240
    const val VERSION = 0x0111

    const val OP_REQ_DEVLIST = 0x8005
    const val OP_REP_DEVLIST = 0x0005
    const val OP_REQ_IMPORT = 0x8003
    const val OP_REP_IMPORT = 0x0003

    // PadLink extension (not part of USB/IP): lets the Windows app discover TVs and show their name.
    // Reply: version, OP_REP_INFO, status 0, then u16 length + UTF-8 TV name.
    const val OP_REQ_INFO = 0x8F01
    const val OP_REP_INFO = 0x0F01

    const val CMD_SUBMIT = 1
    const val CMD_UNLINK = 2
    const val RET_SUBMIT = 3
    const val RET_UNLINK = 4

    const val DIR_OUT = 0
    const val DIR_IN = 1

    // Linux errno values, negated, as USB/IP expects.
    const val EPIPE = -32
    const val EINVAL = -22
    const val ENODEV = -19
    const val ECONNRESET = -104

    const val SPEED_FULL = 2
    const val SPEED_HIGH = 3

    /** Set to true ONLY if you know what you're doing: USB/IP has no auth or encryption. */
    const val ALLOW_PUBLIC_CLIENTS = false

    /** "/dev/bus/usb/001/005" -> (1, 5) */
    fun busDev(d: UsbDevice): Pair<Int, Int> {
        val parts = d.deviceName.trim('/').split('/')
        val bus = parts.getOrNull(parts.size - 2)?.toIntOrNull() ?: 1
        val dev = parts.lastOrNull()?.toIntOrNull() ?: d.deviceId
        return bus to dev
    }

    fun busId(d: UsbDevice): String = busDev(d).let { "${it.first}-${it.second}" }

    /** Android doesn't expose link speed; guess from endpoint packet sizes. */
    fun speed(d: UsbDevice): Int {
        for (i in 0 until d.interfaceCount) {
            val itf = d.getInterface(i)
            for (e in 0 until itf.endpointCount) {
                if (itf.getEndpoint(e).maxPacketSize > 64) return SPEED_HIGH
            }
        }
        return SPEED_FULL
    }

    private fun bcdDevice(d: UsbDevice): Int = try {
        val (maj, min) = d.version.split('.')
        (maj.toInt(16) shl 8) or min.toInt(16)
    } catch (_: Exception) { 0 }

    /** Distinct interfaces (alt setting 0) of the first configuration. */
    private fun primaryInterfaces(d: UsbDevice) =
        (0 until d.interfaceCount).map { d.getInterface(it) }.filter { it.alternateSetting == 0 }

    private fun writeFixed(out: DataOutputStream, s: String, len: Int) {
        val b = s.toByteArray(Charsets.US_ASCII)
        val buf = ByteArray(len)
        System.arraycopy(b, 0, buf, 0, minOf(b.size, len - 1))
        out.write(buf)
    }

    /** usbip_usb_device (312 bytes) [+ usbip_usb_interface (4 bytes) each]. */
    fun writeDevice(out: DataOutputStream, d: UsbDevice, withInterfaces: Boolean) {
        val (bus, dev) = busDev(d)
        val ifaces = primaryInterfaces(d)
        writeFixed(out, "/sys/devices/padlink/${busId(d)}", 256)
        writeFixed(out, busId(d), 32)
        out.writeInt(bus)
        out.writeInt(dev)
        out.writeInt(speed(d))
        out.writeShort(d.vendorId)
        out.writeShort(d.productId)
        out.writeShort(bcdDevice(d))
        out.writeByte(d.deviceClass)
        out.writeByte(d.deviceSubclass)
        out.writeByte(d.deviceProtocol)
        out.writeByte(if (d.configurationCount > 0) d.getConfiguration(0).id else 1)
        out.writeByte(d.configurationCount)
        out.writeByte(ifaces.size)
        if (withInterfaces) {
            for (i in ifaces) {
                out.writeByte(i.interfaceClass)
                out.writeByte(i.interfaceSubclass)
                out.writeByte(i.interfaceProtocol)
                out.writeByte(0)
            }
        }
    }

    /** LAN, loopback, link-local, Tailscale/CGNAT (100.64/10) and IPv6 ULA only. */
    fun isAllowedClient(a: InetAddress): Boolean {
        if (ALLOW_PUBLIC_CLIENTS) return true
        if (a.isLoopbackAddress || a.isSiteLocalAddress || a.isLinkLocalAddress) return true
        val b = a.address
        if (a is Inet4Address) {
            val b0 = b[0].toInt() and 0xff
            val b1 = b[1].toInt() and 0xff
            return b0 == 100 && (b1 and 0xC0) == 64
        }
        if (a is Inet6Address) return (b[0].toInt() and 0xFE) == 0xFC
        return false
    }
}
