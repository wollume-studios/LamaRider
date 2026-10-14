package dev.lamarider

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
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
