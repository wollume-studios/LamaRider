package dev.lamarider

import dev.lamarider.debug.CompileTimeMaps
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import java.io.ByteArrayOutputStream
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder

class CompileTimeMapsTest {
    @TempDir
    lateinit var dir: File

    /** Metalama's TextMapFile: magic, source path, int, target file name, int, count, then (char, line, column) source/target pairs. */
    private fun map(file: File, source: String, target: String, lines: List<Pair<Int, Int>>) {
        val out = ByteArrayOutputStream()
        fun int(v: Int) = out.write(ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(v).array())
        fun string(s: String) = s.toByteArray().let { out.write(it.size); out.write(it) }
        out.write(ByteArray(8) { 7 })
        string(source); int(0); string(target); int(0); int(lines.size)
        for ((from, to) in lines) { int(0); int(from); int(4); int(0); int(to); int(4) }
        file.parentFile.mkdirs()
        file.writeBytes(out.toByteArray())
    }

    private fun compiled(hash: String, source: File, lines: List<Pair<Int, Int>>, modified: Long): File {
        val folder = File(dir, "Metalama/CompileTime/App/.netcoreapp8.0/$hash/2026.1.30")
        val target = File(folder, "Aspect_$hash.cs").apply { parentFile.mkdirs(); writeText("x"); setLastModified(modified) }
        map(File(folder, "Aspect_$hash.map"), source.path, target.name, lines)
        return target
    }

    @Test
    fun `maps a source line to the compiled copy built from the current source`() {
        val source = File(dir, "App/Aspect.cs").apply { parentFile.mkdirs(); writeText("x"); setLastModified(2_000_000) }
        compiled("old", source, listOf(12 to 30), 1_000_000) // compiled before the last edit
        val current = compiled("new", source, listOf(6 to 13, 12 to 19, 12 to 25), 3_000_000)

        val copies = CompileTimeMaps.copiesOf(File(dir, "Metalama/CompileTime"), source.path)
        assertEquals(listOf(current.path), copies.map { it.file.path })
        assertEquals(19, copies.single().line(12))
        assertNull(copies.single().line(40))
    }

    @Test
    fun `a test run's copy takes the map of the identical build copy`() {
        val source = File(dir, "App/Aspect.cs").apply { parentFile.mkdirs(); writeText("x"); setLastModified(1) }
        val built = compiled("h1", source, listOf(12 to 19), 3_000_000).apply { writeText("compiled code") }
        // Same file name, other content (an older build of the aspect): its map would point to wrong lines.
        val older = File(dir, "Metalama/CompileTime/App/.netcoreapp8.0/h2/2026.1.30").apply { mkdirs() }
        File(older, built.name).writeText("older compiled code")
        map(File(older, built.nameWithoutExtension + ".map"), source.path, built.name, listOf(12 to 30))
        // Test runs extract the compile-time code again, to Tests/<guid>/CompileTime/<project>/<tfm>/<hash>/ (no map).
        val run = File(dir, "Metalama/Tests/guid/CompileTime/App/.netcoreapp8.0/h1/${built.name}").apply { parentFile.mkdirs(); writeText("compiled code") }

        // A clone of the project elsewhere compiles to the same file: its map names the other source.
        val clone = File(dir, "Clone/Aspect.cs").apply { parentFile.mkdirs(); writeText("x") }
        val cloneBuild = File(dir, "Metalama/CompileTime/Clone/.netcoreapp8.0/h1/2026.1.30").apply { mkdirs() }
        File(cloneBuild, built.name).writeText("compiled code")
        map(File(cloneBuild, built.nameWithoutExtension + ".map"), clone.path, built.name, listOf(12 to 40))

        val copy = CompileTimeMaps.mapOf(run, CompileTimeMaps.byName(File(dir, "Metalama/CompileTime"))) { it == source.path }!!
        assertEquals(source.path, copy.source)
        assertEquals(run.path, copy.file.path)
        assertEquals(19, copy.line(12))
    }

    @Test
    fun `the banner's copy belongs to the open solution, not to a clone with the same compiled code`() {
        val clone = File(dir, "Old/App/Aspect.cs").apply { parentFile.mkdirs(); writeText("x") }
        val source = File(dir, "Work/App/Aspect.cs").apply { parentFile.mkdirs(); writeText("x") }
        compiled("h1", clone, listOf(12 to 40), 1_000_000).writeText("compiled code") // listed first: older clone
        val folder = File(dir, "Metalama/CompileTime/App2/.netcoreapp8.0/h1/2026.1.30").apply { mkdirs() }
        File(folder, "Aspect_h1.cs").writeText("compiled code")
        map(File(folder, "Aspect_h1.map"), source.path, "Aspect_h1.cs", listOf(12 to 19))
        val run = File(dir, "Metalama/Tests/guid/CompileTime/App/.netcoreapp8.0/h1/Aspect_h1.cs").apply { parentFile.mkdirs(); writeText("compiled code") }

        val copy = CompileTimeMaps.copyFor(run, File(dir, "Metalama")) { it.startsWith(File(dir, "Work").path) }!!
        assertEquals(source.path, copy.source)
        assertEquals(19, copy.line(12))
    }

    @Test
    fun `a line of a compiled copy leads back to its source line`() {
        val source = File(dir, "App/Aspect.cs").apply { parentFile.mkdirs(); writeText("x"); setLastModified(1) }
        val built = compiled("h1", source, listOf(10 to 17, 11 to 18, 12 to 19), 3_000_000).apply { writeText("compiled code") }
        val run = File(dir, "Metalama/Tests/guid/CompileTime/App/.netcoreapp8.0/h1/${built.name}").apply { parentFile.mkdirs(); writeText("compiled code") }
        val root = File(dir, "Metalama")

        // The build's own copy has its map next to it; a test run's copy borrows the identical build copy's map.
        for (copy in listOf(built, run)) {
            val mapped = CompileTimeMaps.copyFor(copy, root) { true }!!
            assertEquals(source.path, mapped.source)
            assertEquals(12, mapped.sourceLine(19))
            assertEquals(12, mapped.sourceLine(21)) // generated lines after a statement belong to it
            assertNull(mapped.sourceLine(3)) // before any mapped code
        }
        assertNull(CompileTimeMaps.copyFor(source, root) { true }) // not a compiled copy
    }

    @Test
    fun `a source line without code follows the statement above it in the copy`() {
        val copy = CompileTimeMaps.Copy(File("x.cs"), "src.cs", mapOf(10 to 17, 12 to 19, 15 to 30))
        assertEquals(19, copy.copyLine(12))
        assertEquals(19, copy.copyLine(13)) // a blank line or comment after line 12
        assertEquals(17, copy.copyLine(11))
        assertNull(copy.copyLine(5))
    }

    @Test
    fun `a corrupt map is ignored`() {
        val source = File(dir, "App/Aspect.cs").apply { parentFile.mkdirs(); writeText("x"); setLastModified(1) }
        File(dir, "Metalama/CompileTime/App/x/2026.1.30/A.map").apply { parentFile.mkdirs(); writeBytes(byteArrayOf(1, 2, 3)) }
        assertTrue(CompileTimeMaps.copiesOf(File(dir, "Metalama/CompileTime"), source.path).isEmpty())
    }
}
