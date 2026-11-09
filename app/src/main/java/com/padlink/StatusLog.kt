package com.padlink

import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** Tiny in-memory log shared by the service and the UI. */
object StatusLog {
    private val lines = ArrayDeque<String>()
    private val fmt = SimpleDateFormat("HH:mm:ss", Locale.US)
    @Volatile var listener: (() -> Unit)? = null

    fun add(msg: String) {
        synchronized(lines) {
            lines.addFirst("${fmt.format(Date())}  $msg")
            while (lines.size > 30) lines.removeLast()
        }
        listener?.invoke()
    }

    fun text(): String = synchronized(lines) { lines.joinToString("\n") }
}
