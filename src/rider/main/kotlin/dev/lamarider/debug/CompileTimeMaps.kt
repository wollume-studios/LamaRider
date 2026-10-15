package dev.lamarider.debug

import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Metalama compiles an aspect project's compile-time code (aspects, templates, fabrics) from a rewritten copy, and the
 * debugger only knows that copy:
 * - builds: <metalama temp>/CompileTime/<project>/<tfm>/<hash>/<version>/, with a .map per file to the source's lines;
 * - aspect test runs: <metalama temp>/Tests/<guid>/CompileTime/<project>/<tfm>/<hash>/, the same files without maps.
 *
 * ponytail: reads Metalama's internal TextMapFile format (2026.1); a format change makes the lookups return nothing,
 * so breakpoints are simply not mirrored.
 */
object CompileTimeMaps {
    class Copy(val file: File, val source: String, internal val lines: Map<Int, Int>) {
        /** 0-based line in [file] for a 0-based source line, if that line has code there. */
        fun line(sourceLine: Int): Int? = lines[sourceLine]
    }

    /** Metalama's temporary directory: METALAMA_TEMP, else %TEMP%\Metalama on Windows, ~/.local/share/Metalama/Temp elsewhere. */
    fun root(): File {
        System.getenv("METALAMA_TEMP")?.let { return File(it) }
        val os = System.getProperty("os.name")
        val home = System.getProperty("user.home")
        return when {
            os.startsWith("Windows") -> File(System.getenv("TMP") ?: System.getenv("TEMP") ?: "$home\\AppData\\Local\\Temp", "Metalama")
            os.startsWith("Mac") -> File(home, "Library/Application Support/Metalama/Temp")
            else -> File(System.getenv("XDG_DATA_HOME") ?: "$home/.local/share", "Metalama/Temp")
        }
    }

    /** Build copies of [sourcePath] made since it last changed (one per target framework). */
    fun copiesOf(cacheRoot: File, sourcePath: String): List<Copy> {
        val source = File(sourcePath)
        return maps(cacheRoot).mapNotNull { read(it) }
            .filter { File(it.source).path.equals(source.path, ignoreCase = File.separatorChar == '\\') }
            .filter { it.file.lastModified() >= source.lastModified() }
            .toList()
    }

    /** Build copies by file name, for [mapOf]. */
    fun byName(cacheRoot: File): Map<String, List<Copy>> = maps(cacheRoot).mapNotNull { read(it) }.toList().groupBy { it.file.name }

    /** A test run's [copy] with the lines of the build copy that has the same name and content (null: none, or still being written). */
    fun mapOf(copy: File, builds: Map<String, List<Copy>>): Copy? {
        val candidates = builds[copy.name] ?: return null
        val content = runCatching { copy.readBytes() }.getOrNull() ?: return null
        return candidates.firstOrNull { runCatching { it.file.readBytes().contentEquals(content) }.getOrDefault(false) }
            ?.let { Copy(copy, it.source, it.lines) }
    }

    private fun maps(cacheRoot: File) = cacheRoot.walk().maxDepth(6).filter { it.isFile && it.extension == "map" }

    private fun read(map: File): Copy? = try {
        val bytes = ByteBuffer.wrap(map.readBytes()).order(ByteOrder.LITTLE_ENDIAN)
        bytes.position(8)
        val sourcePath = string(bytes)
        bytes.int
        val target = File(map.parentFile, string(bytes))
        bytes.int
        val lines = HashMap<Int, Int>()
        repeat(bytes.int) {
            bytes.int; val from = bytes.int; bytes.int
            bytes.int; val to = bytes.int; bytes.int
            lines.merge(from, to, ::minOf) // a source line's first statement
        }
        if (target.isFile) Copy(target, sourcePath, lines) else null
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
