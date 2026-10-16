package dev.lamarider

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

class AspectPopupGroupsTest {
    private val add = "M:Calc.Add(System.Int32,System.Int32)"
    private fun on(target: String, cls: String, via: String, name: String = "Calc.Add(int, int)") =
        AspectInstanceInfo(cls, target, name, "Calc", "Method", "P", via, null)

    private val model = ModelData(
        aspectClasses = listOf(
            AspectClassInfo("T:Log", "Log", "P", false, null),
            AspectClassInfo("T:GreaterThan1", "GreaterThan1", "P", false, null),
        ),
        instances = listOf(
            on("$add;Parameter=1", "T:GreaterThan1", "Fabric", "Calc.Add(int, int)/b"),
            on(add, "T:Log", "Attribute"),
            on("$add;Parameter=0", "T:GreaterThan1", "Fabric", "Calc.Add(int, int)/a"),
            on("$add;Parameter=0", "T:GreaterThan1", "Fabric", "Calc.Add(int, int)/a"), // reported twice (e.g. per target framework)
            on("$add;Return", "T:NotNull", "Attribute", "Calc.Add(int, int)/return"),
        ),
    )

    private fun shape(groups: List<TargetAspects>) = groups.map { g -> g.caption to g.entries.map { it.label } }

    @Test
    fun `aspects are grouped by what they decorate, the member first`() = assertEquals(
        listOf(
            "Add(int, int)" to listOf("Log"),
            "Parameter a" to listOf("GreaterThan1 (fabric)"),
            "Parameter b" to listOf("GreaterThan1 (fabric)"),
            "Return value" to listOf("NotNull"),
        ),
        shape(model.aspectsByTarget(add)),
    )

    @Test
    fun `a parameter's hint shows only that parameter`() = assertEquals(
        listOf("Parameter a" to listOf("GreaterThan1 (fabric)")),
        shape(model.aspectsByTarget("$add;Parameter=0")),
    )

    @Test
    fun `attribute aspects are marked, the attribute already leads to the class`() {
        val entries = model.aspectsByTarget(add).flatMap { it.entries }
        assertEquals(listOf(true, false, false, true), entries.map { it.viaAttribute })
    }
}
