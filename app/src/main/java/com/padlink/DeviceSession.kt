package com.padlink

import android.hardware.usb.UsbDevice
import android.hardware.usb.UsbDeviceConnection
import android.hardware.usb.UsbEndpoint
import android.hardware.usb.UsbInterface
import android.hardware.usb.UsbManager
import android.hardware.usb.UsbRequest
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.Socket
import java.nio.ByteBuffer
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.TimeoutException

/**
 * One attached client <-> one physical USB device.
 * Reads CMD_SUBMIT / CMD_UNLINK from the socket, executes them on the real device,
 * and answers with RET_SUBMIT / RET_UNLINK.
 */
class DeviceSession(
    usb: UsbManager,
    val device: UsbDevice,
    private val socket: Socket,
    private val inp: DataInputStream,
    private val out: DataOutputStream,
) {
    private class Pending(val seq: Int, val dirIn: Boolean, val request: UsbRequest?, val buffer: ByteBuffer?, val numPackets: Int)

    private val conn: UsbDeviceConnection = usb.openDevice(device) ?: throw IOException("openDevice failed")
    private val interfaces = (0 until device.interfaceCount).map { device.getInterface(it) }
    private val endpoints = HashMap<Int, UsbEndpoint>()
    private val pending = ConcurrentHashMap<Int, Pending>()
    private val controlPool = Executors.newCachedThreadPool()
    @Volatile private var running = true

    init {
        for (itf in interfaces) {
            if (itf.alternateSetting == 0) conn.claimInterface(itf, true) // force = detach Android's driver
            for (e in 0 until itf.endpointCount) {
                val ep = itf.getEndpoint(e)
                endpoints.putIfAbsent(ep.address, ep)
            }
        }
    }

    /** Blocks until the client disconnects or the device goes away. */
    fun run() {
        val reaper = Thread(::reapLoop, "usb-reaper").apply { start() }
        try {
            while (running) {
                val command = inp.readInt()
                val seq = inp.readInt()
                inp.readInt() // devid
                val direction = inp.readInt()
                val ep = inp.readInt()
                when (command) {
                    UsbIp.CMD_SUBMIT -> {
                        inp.readInt() // transfer_flags
                        val bufLen = inp.readInt()
                        inp.readInt() // start_frame
                        val numPackets = inp.readInt()
                        inp.readInt() // interval
                        val setup = ByteArray(8).also { inp.readFully(it) }
                        val outData = if (direction == UsbIp.DIR_OUT && bufLen > 0) ByteArray(bufLen).also { inp.readFully(it) } else null
                        if (numPackets != 0 && numPackets != -1) {
                            inp.skipBytes(numPackets * 16) // iso descriptors: not supported
                            sendRetSubmit(seq, UsbIp.EINVAL, null, 0, numPackets)
                        } else if (ep == 0) {
                            submitControl(seq, setup, outData, numPackets)
                        } else {
                            submitTransfer(seq, direction == UsbIp.DIR_IN, ep, bufLen, outData, numPackets)
                        }
                    }
                    UsbIp.CMD_UNLINK -> {
                        val target = inp.readInt()
                        inp.skipBytes(24)
                        val p = pending.remove(target)
                        p?.request?.cancel()
                        sendRetUnlink(seq, if (p != null) UsbIp.ECONNRESET else 0)
                    }
                    else -> throw IOException("unknown command $command")
                }
            }
        } catch (e: IOException) {
            StatusLog.add("${device.productName ?: UsbIp.busId(device)}: client gone (${e.message ?: "eof"})")
        } finally {
            close()
            reaper.join(2000)
        }
    }

    @Synchronized
    fun close() {
        if (!running) return
        running = false
        try { socket.close() } catch (_: Exception) {}
        for (p in pending.values) p.request?.cancel()
        pending.clear()
        controlPool.shutdownNow()
        for (itf in interfaces) if (itf.alternateSetting == 0) conn.releaseInterface(itf)
        conn.close()
    }

    // ---- control transfers (endpoint 0), synchronous on a pool thread ----

    private fun submitControl(seq: Int, setup: ByteArray, outData: ByteArray?, numPackets: Int) {
        pending[seq] = Pending(seq, true, null, null, numPackets)
        controlPool.execute {
            val bmReq = setup[0].toInt() and 0xff
            val bReq = setup[1].toInt() and 0xff
            val wValue = le16(setup, 2)
            val wIndex = le16(setup, 4)
            val wLength = le16(setup, 6)
            val isIn = bmReq and 0x80 != 0

            var status = 0
            var data: ByteArray? = null
            var actual = 0
            when {
                bmReq == 0x00 && bReq == 0x09 -> Unit // SET_CONFIGURATION: Android already configured it
                bmReq == 0x01 && bReq == 0x0B -> setInterface(wIndex, wValue) // SET_INTERFACE
                else -> {
                    val buf = if (isIn) ByteArray(wLength) else (outData ?: ByteArray(0))
                    val n = conn.controlTransfer(bmReq, bReq, wValue, wIndex, buf, buf.size, 2000)
                    when {
                        n >= 0 -> { actual = n; if (isIn) data = buf }
                        bmReq == 0x02 && bReq == 0x01 -> Unit // CLEAR_FEATURE(halt): pretend ok
                        else -> status = UsbIp.EPIPE
                    }
                }
            }
            if (pending.remove(seq) != null) {
                try { sendRetSubmit(seq, status, data, actual, numPackets) } catch (_: IOException) { close() }
            }
        }
    }

    private fun setInterface(id: Int, alt: Int) {
        interfaces.firstOrNull { it.id == id && it.alternateSetting == alt }?.let { conn.setInterface(it) }
    }

    // ---- interrupt / bulk transfers, async via UsbRequest ----

    private fun submitTransfer(seq: Int, dirIn: Boolean, ep: Int, bufLen: Int, outData: ByteArray?, numPackets: Int) {
        val endpoint = endpoints[ep or (if (dirIn) 0x80 else 0)]
        if (endpoint == null) { sendRetSubmit(seq, UsbIp.EPIPE, null, 0, numPackets); return }

        val req = UsbRequest()
        if (!req.initialize(conn, endpoint)) { sendRetSubmit(seq, UsbIp.ENODEV, null, 0, numPackets); return }
        val buffer: ByteBuffer? = when {
            dirIn -> ByteBuffer.allocate(bufLen)
            outData != null -> ByteBuffer.wrap(outData)
            else -> null // zero-length OUT
        }
        val p = Pending(seq, dirIn, req, buffer, numPackets)
        req.clientData = p
        pending[seq] = p
        if (!req.queue(buffer)) {
            pending.remove(seq)
            req.close()
            sendRetSubmit(seq, UsbIp.EPIPE, null, 0, numPackets)
        }
    }

    private fun reapLoop() {
        while (running) {
            val r = try {
                conn.requestWait(500)
            } catch (_: TimeoutException) {
                continue
            } catch (e: Exception) {
                if (running) StatusLog.add("requestWait: ${e.message}")
                if (!running) break else continue
            } ?: continue
            val p = r.clientData as? Pending ?: continue
            r.close()
            if (pending.remove(p.seq) == null) continue // was unlinked
            val n = p.buffer?.position() ?: 0
            try {
                sendRetSubmit(p.seq, 0, if (p.dirIn) p.buffer?.array() else null, n, p.numPackets)
            } catch (_: IOException) {
                close()
            }
        }
    }

    // ---- replies ----

    private fun sendRetSubmit(seq: Int, status: Int, data: ByteArray?, actual: Int, numPackets: Int) = synchronized(out) {
        out.writeInt(UsbIp.RET_SUBMIT)
        out.writeInt(seq)
        out.writeInt(0); out.writeInt(0); out.writeInt(0) // devid, direction, ep
        out.writeInt(status)
        out.writeInt(actual)
        out.writeInt(0) // start_frame
        out.writeInt(numPackets)
        out.writeInt(0) // error_count
        out.write(ZERO8)
        if (data != null && actual > 0) out.write(data, 0, actual)
        out.flush()
    }

    private fun sendRetUnlink(seq: Int, status: Int) = synchronized(out) {
        out.writeInt(UsbIp.RET_UNLINK)
        out.writeInt(seq)
        out.writeInt(0); out.writeInt(0); out.writeInt(0)
        out.writeInt(status)
        out.write(ZERO24)
        out.flush()
    }

    private fun le16(b: ByteArray, o: Int) = (b[o].toInt() and 0xff) or ((b[o + 1].toInt() and 0xff) shl 8)

    private companion object {
        val ZERO8 = ByteArray(8)
        val ZERO24 = ByteArray(24)
    }
}
