package dev.lamarider

import dev.lamarider.testing.AspectTestOutput
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import java.io.File

class AspectTestOutputTest {
    @TempDir
    lateinit var dir: File

    private fun file(path: String, text: String = "x", modified: Long = 0) = File(dir, path).apply {
        parentFile.mkdirs()
        writeText(text)
        if (modified > 0) setLastModified(modified)
    }

    private fun testProject() = file("Tests/Tests.csproj", """<PackageReference Include="Metalama.Testing.AspectTesting" />""")

    @Test
    fun `finds the newest output of the last run for the test and for its snapshot`() {
        testProject()
        val test = file("Tests/LogTests/LogTest.cs")
        val snapshot = file("Tests/LogTests/LogTest.t.cs")
        file("Tests/obj/transformed/net8.0/LogTests/LogTest.t.cs", modified = 1_000_000)
        val newest = file("Tests/obj/transformed/net10.0/LogTests/LogTest.t.cs", modified = 2_000_000)

        val files = AspectTestOutput.of(test)!!
        assertEquals(snapshot, files.snapshot)
        assertEquals(newest, files.output)
        assertEquals(test, AspectTestOutput.of(snapshot)!!.test)
    }

    @Test
    fun `only in aspect test projects`() {
        file("App/App.csproj", "<Project />")
        assertNull(AspectTestOutput.of(file("App/Calc.cs")))
    }

    @Test
    fun `differs ignores line endings and trailing whitespace like Metalama's comparison`() {
        testProject()
        val test = file("Tests/T.cs")
        file("Tests/T.t.cs", "class A\r\n{\r\n}\r\n")
        file("Tests/obj/transformed/net8.0/T.t.cs", "class A  \n{\n}")
        assertFalse(AspectTestOutput.of(test)!!.differs())

        file("Tests/obj/transformed/net8.0/T.t.cs", "class B\n{\n}")
        assertTrue(AspectTestOutput.of(test)!!.differs())
    }

    @Test
    fun `no output before the first run, a missing snapshot differs`() {
        testProject()
        val test = file("Tests/T.cs")
        assertNull(AspectTestOutput.of(test)!!.output)
        assertFalse(AspectTestOutput.of(test)!!.differs())
        file("Tests/obj/transformed/net8.0/T.t.cs", "class A {}")
        assertTrue(AspectTestOutput.of(test)!!.differs())
    }
}
