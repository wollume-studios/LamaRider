package dev.lamarider

import dev.lamarider.diff.ObjTransformedFiles
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import java.io.File

class ObjTransformedFilesTest {
    @TempDir
    lateinit var dir: File

    private fun file(path: String, modified: Long = 0) = File(dir, path).apply {
        parentFile.mkdirs()
        writeText("x")
        if (modified > 0) setLastModified(modified)
    }

    @Test
    fun `finds the newest compiler-emitted file of the project`() {
        file("App/App.csproj")
        val source = file("App/Sub/Calc.cs")
        file("App/obj/Debug/net8.0/metalama/Sub/Calc.cs", 1_000_000)
        val newest = file("App/obj/LamaDebug/net8.0/metalama/Sub/Calc.cs", 2_000_000)
        file("App/obj/Debug/net8.0/Calc.cs", 3_000_000) // not under metalama/
        assertEquals(newest.path, ObjTransformedFiles.find(source.path))
    }

    @Test
    fun `nothing without a build`() {
        file("App/App.csproj")
        assertNull(ObjTransformedFiles.find(file("App/Calc.cs").path))
    }
}
