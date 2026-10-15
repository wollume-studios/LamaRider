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

    @Test
    fun `contract on a parameter shows the transformation of its method`() {
        val url = AspectInstanceInfo("T:Url", "M:Links.Open(System.String);Parameter=0", "Links.Open(string)/address", "Links", "Parameter", "P", "Attribute", null)
        val change = TransformationInfo("T:Url", "M:Links.Open(System.String)", "InsertStatement", "Add contract", null)
        val other = TransformationInfo("T:Log", "M:Links.Open(System.String)", "OverrideMember", "Log", null)
        assertEquals(listOf(change), ModelData(instances = listOf(url), transformations = listOf(change, other)).transformationsOf(url))
    }

    @Test
    fun `aspects on a method include those on its parameters`() {
        fun on(target: String, cls: String) = AspectInstanceInfo(cls, target, target, "Test", "Method", "P", "Attribute", null)
        val model = ModelData(instances = listOf(on("M:T.M(System.String)", "T:Class1"), on("M:T.M(System.String);Parameter=0", "T:Url"), on("M:T.Other", "T:Log")))
        assertEquals(listOf("T:Class1", "T:Url"), model.instancesOn("M:T.M(System.String)").map { it.aspectClass })
    }

    @Test
    fun `transformed declaration is found by source file and target`() {
        val decl = TransformedDeclaration("M:Calc.Add", 10, 20)
        val model = ModelData(transformedFiles = listOf(TransformedFileInfo("C:\\src\\Calc.cs", "C:/out/0_Calc.cs", listOf(decl))))
        assertEquals(decl, model.transformedDeclaration("c:/src/calc.cs", "M:Calc.Add")?.second)
        assertNull(model.transformedDeclaration("c:/src/calc.cs", "M:Calc.Sub"))
    }

    @Test
    fun `snippet keeps relative indentation and drops the common one`() {
        val text = "class C\n{\n    void M()\n    {\n        x();\n    }\n}\n"
        val start = text.indexOf("void")
        val end = text.indexOf("    }") + 5
        assertEquals("void M()\n{\n    x();\n}", ModelData.snippet(text, start, end))
    }

    @Test
    fun `member name drops the type and shows parameters after the method`() {
        fun named(name: String) = AspectInstanceInfo("T:A", "x", name, "NS.Links", "Method", "P", "Attribute", null)
        assertEquals("Open(string)", named("Links.Open(string)").memberName)
        assertEquals("Open(string) › address", named("Links.Open(string)/address").memberName)
        assertEquals("Links", named("Links").memberName)
    }

    @Test
    fun `error banner shows the first error and how many more`() {
        fun error(id: String, message: String) = DiagnosticInfo(id, "Error", message, null, null, null)
        assertNull(ModelData().errorBannerText())
        assertEquals("Metalama: LAMA0042 ordering. Aspects are not applied.", ModelData(diagnostics = listOf(error("LAMA0042", "ordering\ndetails"))).errorBannerText())
        assertEquals(
            "Metalama: CS1061 nope (+1 more). Aspects are not applied.",
            ModelData(diagnostics = listOf(error("CS1061", "nope"), DiagnosticInfo("W", "Warning", "w", null, null, null), error("LAMA0001", "x"))).errorBannerText(),
        )
    }

    @Test
    fun `error banner leaves located errors to the editor squiggles`() {
        val located = DiagnosticInfo("LAMA0037", "Error", "must be static", null, "C:/src/Test.cs", 23)
        val projectWide = DiagnosticInfo("LAMA0042", "Error", "ordering", null, null, null)
        assertNull(ModelData(diagnostics = listOf(located)).errorBannerText())
        assertEquals("Metalama: LAMA0042 ordering. Aspects are not applied.", ModelData(diagnostics = listOf(located, projectWide)).errorBannerText())
        assertEquals(listOf(projectWide), ModelData(diagnostics = listOf(located, projectWide)).bannerErrors())
    }

    @Test
    fun `aspects at the caret cover the member, its parameters and the containing type`() {
        val file = "C:/src/Test.cs"
        fun at(target: String, cls: String, start: Int, end: Int) = AspectInstanceInfo(cls, target, target, "Test", "Method", "P", "Attribute", SourceLocation(file, start, end))
        val onType = at("T:Test", "T:Observable", 0, 500)
        val onMethod = at("M:Test.M(System.String)", "T:Class1", 100, 200)
        val onParam = at("M:Test.M(System.String);Parameter=0", "T:Url", 120, 130)
        val elsewhere = at("M:Test.Other", "T:Log", 300, 400)
        val onlyParam = at("M:Test.P(System.String);Parameter=0", "T:Url", 220, 230)
        // P has no aspect of its own: its span comes from the transformed declarations.
        val decls = listOf(TransformedDeclaration("M:Test.P(System.String)", 0, 10, 210, 260))
        val model = ModelData(
            instances = listOf(onType, onMethod, onParam, elsewhere, onlyParam),
            transformedFiles = listOf(TransformedFileInfo(file, "C:/out/0_Test.cs", decls)),
        )
        assertEquals(setOf(onType, onMethod, onParam), model.instancesAt(file, 150).toSet()) // in the method body
        assertEquals(setOf(onType, onlyParam), model.instancesAt(file, 250).toSet())
        assertEquals(setOf(onType), model.instancesAt(file, 450).toSet())
        assertEquals(emptyList<AspectInstanceInfo>(), model.instancesAt("C:/src/Other.cs", 150))
    }
}
