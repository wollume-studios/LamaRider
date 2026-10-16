package dev.lamarider.uitest

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
        dotnet("test", solutionDir.resolve("MetalamaSample.AspectTests").toString()) // first run writes the output
        val output = solutionDir.resolve("MetalamaSample.AspectTests/obj/transformed")
        waitFor("test ran", timeout = 10.seconds) { Files.exists(output) }
    }

    @Test
    fun `debugging an aspect test stops at a breakpoint in the aspect`() = withRider("debugaspect") {
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
            ideFrame { waitForText("compiled copy banner", "Metalama's compiled copy of LogAttribute.cs") }
        } finally {
            debugger.getCurrentSession()?.stop()
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
