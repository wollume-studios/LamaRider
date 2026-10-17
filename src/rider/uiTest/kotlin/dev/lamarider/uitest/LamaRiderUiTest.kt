package dev.lamarider.uitest

import com.intellij.driver.client.Driver
import com.intellij.driver.sdk.VirtualFile
import com.intellij.driver.sdk.invokeAction
import com.intellij.driver.model.OnDispatcher
import com.intellij.driver.sdk.openToolWindow
import com.intellij.driver.sdk.singleProject
import com.intellij.driver.sdk.ui.components.common.codeEditor
import com.intellij.driver.sdk.ui.components.common.gutter
import com.intellij.driver.sdk.ui.components.common.ideFrame
import com.intellij.driver.sdk.waitFor
import com.intellij.driver.sdk.ui.ui
import com.intellij.driver.sdk.ui.xQuery
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.fail
import java.nio.file.Files
import java.nio.file.Path
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds

/**
 * End-to-end tests in a real Rider: the built plugin, the sample solution, the real worker and backend.
 * Run: ./gradlew uiTest (Rider from LAMARIDER_RIDER, else %LOCALAPPDATA%\Programs\Rider).
 */
class LamaRiderUiTest : UiTestBase() {
    @Test
    fun `LamaRider window lists the aspects of the solution`() = withRider("aspects") {
        openToolWindow("LamaRider")
        ideFrame {
            waitForText("project aspect Log listed", "Log")
            waitForText("library package listed", "Metalama.Patterns.Contracts")
        }
    }

    @Test
    fun `Show Transformed Code shows the woven method`() = withRider("popup") {
        openToolWindow("LamaRider")
        ideFrame { waitForText("aspects listed (first refresh done)", "Log") }
        caretAt("MetalamaSample/Calculator.cs", "Add(")
        // An editor action: its data context (editor, file) comes from the editor component.
        ideFrame { invokeAction("LamaRider.ShowTransformedCode", component = codeEditor().component) }
        // The popup is its own window: search all of them.
        waitFor("transformed Add in the popup", timeout = 1.minutes) { ui.x(xQuery { contains(byVisibleText("Entering Add")) }).present() }
    }

    @Test
    fun `an aspect a fabric adds is written as an attribute`() = withRider("asattribute") {
        openToolWindow("LamaRider")
        ideFrame { waitForText("aspects listed (first refresh done)", "Log") }
        caretAt("MetalamaSample/Calculator.cs", "Multiply(")
        editorAction("LamaRider.AddAspectsAsAttributes")
        waitFor("[Log] added to Multiply", timeout = 1.minutes) { editorText().contains("[Log]\n    public int Multiply(") }
    }

    @Test
    fun `declarations are replaced with their transformed code`() = withRider("replace") {
        openToolWindow("LamaRider")
        ideFrame { waitForText("aspects listed (first refresh done)", "Log") }
        // Fabric-added aspect: excluded so the fabric doesn't add it again.
        caretAt("MetalamaSample/Calculator.cs", "Multiply(")
        editorAction("LamaRider.ReplaceWithTransformedCode")
        waitFor("Multiply replaced", timeout = 1.minutes) { editorText().contains("Entering Multiply") }
        assertTrue(editorText().contains("ExcludeAspect(typeof(LogAttribute))]\n    public int Multiply("), editorText())
        // Attribute aspect: the attribute goes.
        caretAt("MetalamaSample/Calculator.cs", "Add(")
        editorAction("LamaRider.ReplaceWithTransformedCode")
        waitFor("Add replaced", timeout = 1.minutes) { editorText().contains("Entering Add") }
        assertTrue(!editorText().contains("[Log]"), editorText())
        ideFrame { invokeAction("SaveAll", component = codeEditor().component) }
        assertTrue(dotnet("build", solutionDir.resolve("MetalamaSample/MetalamaSample.csproj").toString()) == 0, "replaced code builds")
    }

    @Test
    fun `a new file is picked up without a manual refresh`() = withRider("autorefresh") {
        openToolWindow("LamaRider")
        ideFrame { waitForText("aspects listed (first refresh done)", "Log") }
        // Written outside Rider, as by git or another editor: the file watcher must trigger the refresh.
        Files.writeString(solutionDir.resolve("MetalamaSample/Added.cs"),
            """
            namespace MetalamaSample;

            public class Added
            {
                [Log]
                public int Twice(int x) => 2 * x;
            }
            """.trimIndent())
        waitFor("Added.cs analyzed", timeout = 3.minutes, interval = 10.seconds) {
            runCatching {
                caretAt("MetalamaSample/Added.cs", "Twice(")
                ideFrame { invokeAction("LamaRider.ShowTransformedCode", component = codeEditor().component) }
                ui.x(xQuery { contains(byVisibleText("Entering Twice")) }).present()
            }.onFailure { println("Added.cs not ready: " + generateSequence(it) { e -> e.cause }.joinToString(" <- ") { e -> "${e.javaClass.simpleName}: ${e.message?.lineSequence()?.firstOrNull { l -> l.isNotBlank() }}" }) }.getOrDefault(false)
        }
    }

    @Test
    fun `aspect test is added to the existing test project and its output accepted`() = withRider("aspecttest") {
        openToolWindow("LamaRider")
        ideFrame { waitForText("aspects listed (first refresh done)", "Log") }
        caretAt("MetalamaSample/LogAttribute.cs", "LogAttribute :")
        editorAction("LamaRider.CreateAspectTest")
        val test = solutionDir.resolve("MetalamaSample.AspectTests/LogTests/LogTest.cs")
        waitFor("test file created", timeout = 1.minutes) { Files.exists(test) }
        assertTrue(Files.readString(test).contains("    [Log]\n    public int Add("), Files.readString(test))

        // The test project has MetalamaEnabled=False; LamaRider still shows what the aspect does to the test input.
        waitFor("test input woven", timeout = 3.minutes, interval = 10.seconds) {
            runCatching {
                caretAt("MetalamaSample.AspectTests/LogTests/LogTest.cs", "Add(")
                ideFrame { invokeAction("LamaRider.ShowTransformedCode", component = codeEditor().component) }
                ui.x(xQuery { contains(byVisibleText("Entering Add")) }).present()
            }.getOrDefault(false)
        }

        // First run: fails, writes the output (and a placeholder snapshot).
        dotnet("test", solutionDir.resolve("MetalamaSample.AspectTests").toString())
        caretAt("MetalamaSample.AspectTests/LogTests/LogTest.cs", "Add(")
        editorAction("LamaRider.AcceptAspectTestOutput")
        val snapshot = solutionDir.resolve("MetalamaSample.AspectTests/LogTests/LogTest.t.cs")
        waitFor("snapshot accepted", timeout = 1.minutes) { Files.exists(snapshot) && Files.readString(snapshot).contains("Entering Add") }
        assertTrue(dotnet("test", solutionDir.resolve("MetalamaSample.AspectTests").toString()) == 0, "accepted test passes")
    }

    @Test
    fun `aspect test output is compared with the snapshot and accepted from the banner`() = withRider("aspecttestdiff") {
        openToolWindow("LamaRider")
        ideFrame { waitForText("aspects listed (first refresh done)", "Log") }
        caretAt("MetalamaSample/LogAttribute.cs", "LogAttribute :")
        editorAction("LamaRider.CreateAspectTest")
        val test = solutionDir.resolve("MetalamaSample.AspectTests/LogTests/LogTest.cs")
        waitFor("test file created", timeout = 1.minutes) { Files.exists(test) }
        // Fails and writes the output. LamaRider builds the new test in the background, which can collide: try again.
        val output = solutionDir.resolve("MetalamaSample.AspectTests/obj/transformed")
        for (attempt in 1..3) {
            dotnet("test", solutionDir.resolve("MetalamaSample.AspectTests").toString())
            if (Files.exists(output)) break
        }
        assertTrue(Files.exists(output), "dotnet test wrote no output")

        // Run outside the IDE: what Rider does when its window gets the focus back.
        ideFrame { invokeAction("Synchronize", component = codeEditor().component) }
        caretAt("MetalamaSample.AspectTests/LogTests/LogTest.cs", "Add(")
        waitFor("banner: output differs", timeout = 1.minutes) { ui.x(xQuery { byAccessibleName("Compare") }).present() }
        // The banner's links must be on screen to be clicked: the test window is only 1024 pixels wide.
        ideFrame { invokeAction("HideAllWindows", component = codeEditor().component) }
        ui.x(xQuery { byAccessibleName("Compare") }).click()
        waitFor("diff of output and snapshot", timeout = 1.minutes) { ui.x(xQuery { contains(byVisibleText("Last run output (read-only)")) }).present() }

        caretAt("MetalamaSample.AspectTests/LogTests/LogTest.cs", "Add(")
        ui.x(xQuery { byAccessibleName("Accept all") }).click()
        val snapshot = solutionDir.resolve("MetalamaSample.AspectTests/LogTests/LogTest.t.cs")
        waitFor("snapshot accepted", timeout = 1.minutes) { Files.readString(snapshot).contains("Entering Add") }
        assertTrue(dotnet("test", solutionDir.resolve("MetalamaSample.AspectTests").toString()) == 0, "accepted test passes")
    }

    @Test
    fun `Metalama Premium code is kept out of the free build`() = withRider("premium", prepare = { dir ->
        val csproj = dir.resolve("MetalamaSample/MetalamaSample.csproj")
        Files.writeString(csproj, Files.readString(csproj).replace(
            """<PackageReference Include="Metalama.Framework" Version="2026.1.30" />""",
            """<PackageReference Include="Metalama.Framework" Version="2026.1.30" />
    <PackageReference Include="Metalama.Extensions.CodeFixes" Version="2026.1.30" />"""))
        Files.writeString(dir.resolve("MetalamaSample/SuggestObsolete.cs"),
            """
            using Metalama.Extensions.CodeFixes;
            using Metalama.Framework.Aspects;
            using Metalama.Framework.Code;

            namespace MetalamaSample;

            public class SuggestObsoleteAttribute : MethodAspect
            {
                public override void BuildAspect(IAspectBuilder<IMethod> builder)
                {
                    builder.Diagnostics.Suggest(CodeFixFactory.AddAttribute(builder.Target, typeof(ObsoleteAttribute)));
                }
            }
            """.trimIndent())
    }) {
        openToolWindow("LamaRider")
        ideFrame { waitForText("aspects listed (first refresh done)", "Log") }
        caretAt("MetalamaSample/SuggestObsolete.cs", "Suggest(")
        editorAction("LamaRider.WrapPremiumCode")
        waitFor("Premium code in #if", timeout = 1.minutes) {
            editorText().contains("#if METALAMA_PREMIUM\n        builder.Diagnostics.Suggest(") &&
                editorText().contains("#if METALAMA_PREMIUM\nusing Metalama.Extensions.CodeFixes;\n#endif")
        }
        editorAction("LamaRider.SetUpPremiumBuilds")
        val csproj = solutionDir.resolve("MetalamaSample/MetalamaSample.csproj")
        waitFor("project set up", timeout = 1.minutes) { Files.readString(csproj).contains("METALAMA_PREMIUM") }
        ideFrame { invokeAction("SaveAll", component = codeEditor().component) }
        // Without a license the Premium package alone fails the build (LAMA0806): the free build leaves it out.
        assertTrue(dotnet("build", csproj.toString()) == 0, "free build passes")
    }

    @Test
    fun `aspect test project is created when there is none`() = withRider("aspecttestproject", prepare = { dir ->
        dotnet("sln", dir.resolve("MetalamaSample.sln").toString(), "remove", dir.resolve("MetalamaSample.AspectTests/MetalamaSample.AspectTests.csproj").toString())
        dir.resolve("MetalamaSample.AspectTests").toFile().deleteRecursively()
    }) {
        openToolWindow("LamaRider")
        ideFrame { waitForText("aspects listed (first refresh done)", "Log") }
        caretAt("MetalamaSample/IdAttribute.cs", "IdAttribute :")
        editorAction("LamaRider.CreateAspectTest")
        val test = solutionDir.resolve("MetalamaSample.AspectTests/IdTests/IdTest.cs")
        waitFor("project and test created", timeout = 2.minutes) { Files.exists(test) }
        assertTrue(Files.readString(test).contains("[Id]\npublic partial class IdTarget"), Files.readString(test))
        assertTrue(Files.readString(solutionDir.resolve("MetalamaSample.sln")).contains("MetalamaSample.AspectTests.csproj"), "added to the solution")
        // First run writes the output. LamaRider builds the new project in the background right after creating it,
        // which can collide with this run: try again.
        val output = solutionDir.resolve("MetalamaSample.AspectTests/obj/transformed")
        for (attempt in 1..3) {
            dotnet("test", solutionDir.resolve("MetalamaSample.AspectTests").toString())
            if (Files.exists(output)) break
        }
        assertTrue(Files.exists(output), "dotnet test wrote no output")
    }

    @Test
    fun `debugging an aspect test stops at a breakpoint in the aspect`() = debugAspectTest("debugaspect", readable = false)

    @Test
    fun `debugging stops in readable compile-time code`() = debugAspectTest("debugreadable", readable = true)

    /** [readable]: the solution asks Metalama to format compile-time code (Settings › Readable aspect code while debugging). */
    private fun debugAspectTest(name: String, readable: Boolean) = withRider(name, prepare = { dir ->
        if (readable) Files.writeString(dir.resolve("Directory.Build.props"),
            """
            <Project>
              <PropertyGroup Label="LamaRider">
                <MetalamaFormatCompileTimeCode>true</MetalamaFormatCompileTimeCode>
              </PropertyGroup>
            </Project>
            """.trimIndent() + "\n")
    }) {
        openToolWindow("LamaRider")
        ideFrame { waitForText("aspects listed (first refresh done)", "Log") }
        // A template line that runs while the test runner weaves the test input (compile time).
        val aspect = solutionDir.resolve("MetalamaSample/LogAttribute.cs")
        val line = Files.readAllLines(aspect).indexOfFirst { it.contains("var name = meta.Target.Method.Name;") }
        val file = utility(LocalFileSystem::class).getInstance().refreshAndFindFileByPath(aspect.toString().replace('\\', '/')) ?: fail("No $aspect")
        withContext<Unit>(OnDispatcher.EDT) { service(com.intellij.driver.sdk.XDebuggerUtil::class).toggleLineBreakpoint(singleProject(), file, line, false) }

        // Never built: Rider builds the test project and discovers its tests first.
        val debugger = service(XDebuggerManager::class, singleProject())
        caretAt("MetalamaSample.AspectTests/Log/Add.cs", "Add(")
        ideFrame {
            waitFor("run icon in the gutter", timeout = 2.minutes) {
                gutter().getGutterIcons().any { it.mark.getTooltipText()?.contains("Metalama aspect test") == true }
            }
        }
        editorAction("LamaRider.DebugAspectTest")
        waitFor("debug session started", timeout = 10.minutes) { debugger.getCurrentSession() != null }
        try {
            waitFor("stopped at the breakpoint in the aspect", timeout = 5.minutes) { debugger.getCurrentSession()?.isPaused() == true }
            // The debugger shows Metalama's compiled copy; the banner leads back to LogAttribute.cs.
            // By the links' accessible names: the banner is wider than the test's 1024-pixel window (text scrolled out of view).
            waitFor("compiled copy banner", timeout = 5.minutes) {
                ui.x(xQuery { byAccessibleName("Show LogAttribute.cs side by side") }).present()
            }
            // Offered only while Metalama does not format compile-time code.
            assertTrue(ui.x(xQuery { byAccessibleName("Make it readable") }).present() != readable, "'Make it readable' offered: ${!readable}")
            if (!readable) {
                sideBySideFollowsTheCaret(aspect, file)
                // Readable code applies from the next build: the dialog restarts the test, which builds first.
                ui.x(xQuery { byAccessibleName("Make it readable") }).click()
                ui.x(xQuery { byAccessibleName("Debug Again") }).click()
                val editors = utility(FileEditorManagerRef::class).getInstance(singleProject())
                waitFor("stopped again in readable compile-time code", timeout = 10.minutes, interval = 5.seconds) {
                    val session = debugger.getCurrentSession()
                    val (path, text) = withReadAction(OnDispatcher.EDT) {
                        editors.getSelectedFiles().firstOrNull()?.let { it.getPath() to editors.getAllEditors(it).firstOrNull()?.getEditor()?.getDocument()?.getText() }
                    } ?: (null to null)
                    println("debug again: session ${session != null}, paused ${session?.isPaused()}, selected $path, global:: ${text?.contains("global::")}")
                    session?.isPaused() == true && text != null && text.contains("OverrideMethod") && !text.contains("global::")
                }
            }
        } finally {
            debugger.getCurrentSession()?.stop()
        }
    }

    /**
     * The source of the compiled copy in a split next to it follows the copy's caret, also when the source is already open
     * as a tab next to the copy (where the breakpoint was set).
     */
    private fun Driver.sideBySideFollowsTheCaret(aspect: Path, source: VirtualFile) {
        val editors = utility(FileEditorManagerRef::class).getInstance(singleProject())
        val copy = editors.getSelectedFiles().first()
        withContext<Unit>(OnDispatcher.EDT) { editors.openFile(source, false); editors.openFile(copy, true) }
        ideFrame { invokeAction("HideAllWindows", component = codeEditor().component) }
        ui.x(xQuery { byAccessibleName("Show LogAttribute.cs side by side") }).click()
        waitFor("LogAttribute.cs in a second editor", timeout = 30.seconds) {
            println("side by side: ${editors.getAllEditors(source).size} LogAttribute.cs editors; open: " +
                editors.getOpenFiles().joinToString { it.getPath() } + "; selected: " + editors.getSelectedFiles().joinToString { it.getPath() })
            editors.getAllEditors(source).size >= 2
        }

        val nameLine = Files.readAllLines(aspect).indexOfFirst { it.contains("var name = meta.Target.Method.Name;") }
        withReadAction<Unit>(OnDispatcher.EDT) {
            val copyEditor = editors.getAllEditors(copy).first().getEditor()
            copyEditor.getCaretModel().moveToOffset(copyEditor.getDocument().getText().lastIndexOf("Leaving"))
        }
        waitFor("every LogAttribute.cs editor follows to the finally block", timeout = 30.seconds) {
            withReadAction(OnDispatcher.EDT) {
                editors.getAllEditors(source).all { e -> e.getEditor().let { it.getDocument().getLineNumber(it.getCaretModel().getOffset()) } > nameLine }
            }
        }
    }

    @Test
    fun `Track Caret shows the aspects at the caret in the targets tree`() = withRider("track") {
        openToolWindow("LamaRider")
        ideFrame { waitForText("aspects listed (first refresh done)", "Log") }
        invokeAction("LamaRider.TrackCaret")
        try {
            caretAt("MetalamaSample/Links.cs", "address")
            ideFrame { waitForText("contract on the parameter", "Open(string) › address") }
            caretAt("MetalamaSample/Calculator.cs", "Multiply(")
            ideFrame {
                waitForText("fabric aspect on Multiply", "Multiply(int, int)")
                waitFor("Links target gone", timeout = 30.seconds) { !hasSubtext("Open(string) › address") }
                waitForText("aspect list not filtered", "Metalama.Patterns.Contracts")
            }
            invokeAction("LamaRider.OnlyAtCaret")
            ideFrame { waitFor("aspect list filtered to the caret", timeout = 30.seconds) { !hasSubtext("Metalama.Patterns.Contracts") } }
            invokeAction("LamaRider.OnlyAtCaret")
        } finally {
            invokeAction("LamaRider.TrackCaret") // the setting is global to the test IDE
        }
    }
}
