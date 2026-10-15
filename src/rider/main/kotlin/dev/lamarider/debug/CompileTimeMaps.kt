package dev.lamarider.debug

import java.io.DataInputStream
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Metalama compiles an aspect project's compile-time code (aspects, templates, fabrics) from a rewritten copy under
 * <temp>/Metalama/CompileTime/<project>/<tfm>/<hash>/<version>/, and the debugger only knows that copy. A .map next to
 * each copied file maps its lines to the source file's.
 *
 * ponytail: reads Metalama's internal TextMapFile format (2026.1); a format change makes copiesOf return nothing,
 * so breakpoints are simply not mirrored.
 */
object CompileTimeMaps {
    class Copy(val file: File, private val lines: Map<Int, Int>) {
        /** 0-based line in [file] for a 0-based source line, if that line has code there. */
        fun line(sourceLine: Int): Int? = lines[sourceLine]
    }

    /** Where .NET's Path.GetTempPath() points, which Metalama uses. */
    fun root(): File {
        val windows = System.getProperty("os.name").startsWith("Windows")
        val temp = if (windows) System.getenv("TMP") ?: System.getenv("TEMP") ?: System.getenv("USERPROFILE")
        else System.getenv("TMPDIR") ?: "/tmp"
        return File(temp, "Metalama/CompileTime")
    }

    /** Compiled copies of [sourcePath] made since it last changed (one per target framework). */
    fun copiesOf(root: File, sourcePath: String): List<Copy> {
        val source = File(sourcePath)
        return root.walk().maxDepth(6).filter { it.isFile && it.extension == "map" }
            .mapNotNull { read(it, source) }
            .filter { it.file.lastModified() >= source.lastModified() }
            .toList()
    }

    private fun read(map: File, source: File): Copy? = try {
        val bytes = ByteBuffer.wrap(map.readBytes()).order(ByteOrder.LITTLE_ENDIAN)
        bytes.position(8)
        val sourcePath = string(bytes)
        if (!File(sourcePath).path.equals(source.path, ignoreCase = File.separatorChar == '\\')) null
        else {
            bytes.int
            val target = File(map.parentFile, string(bytes))
            bytes.int
            val lines = HashMap<Int, Int>()
            repeat(bytes.int) {
                bytes.int; val from = bytes.int; bytes.int
                bytes.int; val to = bytes.int; bytes.int
                lines.merge(from, to, ::minOf) // a source line's first statement
            }
            if (target.isFile) Copy(target, lines) else null
        }
    } catch (e: RuntimeException) { // BufferUnderflowException, IllegalArgumentException: not a map we understand
        null
    }

    /** .NET BinaryWriter string: 7-bit encoded length, UTF-8. */
    private fun string(bytes: ByteBuffer): String {
        var length = 0
        var shift = 0
        while (true) {
            val b = bytes.get().toInt() and 0xFF
            length = length or ((b and 0x7F) shl shift)
            if (b < 0x80) break
            shift += 7
        }
        val data = ByteArray(length).also { bytes.get(it) }
        return String(data, Charsets.UTF_8)
    }
}
