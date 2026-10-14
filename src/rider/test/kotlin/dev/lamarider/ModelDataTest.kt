package dev.lamarider

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class ModelDataTest {
    private val file = "C:/src/Calc.cs"
    private fun instance(target: String, start: Int, end: Int, cls: String = "T:Log") =
        AspectInstanceInfo(cls, target, target, "Calc", "Method", "P", "Attribute", SourceLocation(file, start, end))

    private val model = ModelData(
        aspectClasses = listOf(AspectClassInfo("T:Log", "Log", "P", false, SourceLocation("C:/src/Log.cs", 0, 200))),
        instances = listOf(instance("T:Calc", 0, 100, "T:Id"), instance("M:Calc.Add", 20, 50)),
    )

    @Test
    fun `caret inside a member picks the member, not the containing type`() =
        assertEquals("M:Calc.Add", model.targetAt(file, 30))

    @Test
    fun `caret in the type outside members picks the type`() =
        assertEquals("T:Calc", model.targetAt(file, 70))

    @Test
    fun `caret inside an aspect class picks the class`() =
        assertEquals("T:Log", model.targetAt("c:/SRC/log.cs", 10))

    @Test
    fun `caret elsewhere finds nothing`() =
        assertNull(model.targetAt(file, 150))
}

class ModelDataGroupingTest {
    private fun cls(name: String, pkg: String, library: Boolean) =
        AspectClassInfo("T:$pkg.$name", name, if (library) "" else pkg, library, null, pkg, null, null)

    private val model = ModelData(
        aspectClasses = listOf(
            cls("Url", "Metalama.Patterns.Contracts", true),
            cls("Class1", "EFA", false),
            cls("Memoize", "Metalama.Patterns.Memoization", true),
            cls("NotNull", "Metalama.Patterns.Contracts", true),
        ),
        diagnostics = listOf(
            DiagnosticInfo("LAMA0042", "Error", "ordering", null, null, null),
            DiagnosticInfo("LAMA0001", "Warning", "w", null, null, null),
        ),
    )

    @Test
    fun `projects come first, then packages, each sorted`() =
        assertEquals(
            listOf("EFA" to listOf("Class1"), "Metalama.Patterns.Contracts" to listOf("NotNull", "Url"), "Metalama.Patterns.Memoization" to listOf("Memoize")),
            model.groups("").map { g -> g.name to g.classes.map { it.name } },
        )

    @Test
    fun `filter keeps only matching classes and non-empty groups`() =
        assertEquals(listOf("Metalama.Patterns.Contracts" to listOf("Url")), model.groups("ur").map { g -> g.name to g.classes.map { it.name } })

    @Test
    fun `error signature covers errors only`() {
        assertEquals("LAMA0042|ordering|null|null", model.errorSignature())
        assertEquals("", ModelData().errorSignature())
    }
}
