package dev.lamarider.uitest

import com.intellij.driver.client.Driver
import com.intellij.driver.client.Remote
import com.intellij.driver.model.OnDispatcher
import com.intellij.driver.sdk.expireNotifications
import com.intellij.driver.sdk.invokeAction
import com.intellij.driver.sdk.openToolWindow
import com.intellij.driver.sdk.singleProject
import com.intellij.driver.sdk.ui.components.UiComponent
import com.intellij.driver.sdk.ui.components.common.codeEditor
import com.intellij.driver.sdk.ui.components.common.gutter
import com.intellij.driver.sdk.ui.components.common.ideFrame
import com.intellij.driver.sdk.ui.components.elements.JTreeUiComponent
import com.intellij.driver.sdk.ui.remote.Component
import com.intellij.driver.sdk.ui.ui
import com.intellij.driver.sdk.ui.xQuery
import com.intellij.driver.sdk.waitFor
import com.intellij.ide.starter.ide.IDETestContext
import org.junit.jupiter.api.Tag
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.fail
import java.awt.Rectangle
import java.awt.Robot
import java.awt.Toolkit
import java.nio.file.Files
import java.nio.file.Path
import javax.imageio.ImageIO
import kotlin.io.path.writeText
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds

/**
 * The screenshots in docs/screenshots: each feature on the sample solution, captured from the (virtual) screen into
 * build/screenshots. Not part of the normal uiTest run: ./gradlew uiTest -PuiTestScreenshots (Linux: under xvfb-run with a
 * 1920x1080 screen), then copy build/screenshots/\*.png to docs/screenshots.
 */
@Tag("screenshots")
class ScreenshotsUiTest : UiTestBase() {
    private val out = Path.of(System.getProperty("lamarider.screenshots", "build/screenshots"))

    override fun configure(context: IDETestContext) {
        // On Linux, Rider picks Wayland when a desktop session runs: its windows would not be on the Xvfb screen we capture.
        if (!System.getProperty("os.name").startsWith("Windows")) context.applyVMOptionsPatch { addSystemProperty("awt.toolkit.name", "XToolkit") }
    }

    @Test
    fun `tool window, Code Vision, popups, T#, diff and settings`() = withRider("shots", prepare = { dir ->
        // Fabric aspects on Multiply and its parameters: named Code Vision and parameter inlay hints.
        dir.resolve("MetalamaSample/Fabric.cs").writeText(
            """
            using Metalama.Framework.Aspects;
            using Metalama.Framework.Fabrics;
            using Metalama.Patterns.Contracts;

            namespace MetalamaSample;

            public class Fabric : ProjectFabric
            {
                public override void AmendProject(IProjectAmender amender)
                {
                    var multiply = amender.SelectMany(c => c.Types)
                        .Where(t => t.Name == nameof(Calculator))
                        .SelectMany(t => t.Methods)
                        .Where(m => m.Name == nameof(Calculator.Multiply));
                    multiply.AddAspect<LogAttribute>();
                    multiply.SelectMany(m => m.Parameters).AddAspect<NonNegativeAttribute>();
                }
            }
            """.trimIndent() + "\n")
    }) {
        layout()
        openToolWindow("LamaRider")
        ideFrame { waitForText("aspects listed (first refresh done)", "Log") }

        caretAt("MetalamaSample/Calculator.cs", "Add(")
        ideFrame { aspectList().clickRow { it.trim().startsWith("Log") } }
        ideFrame { waitForText("targets of Log", "Multiply(int, int)") }
        shot("lamarider-window", frame())

        caretAt("MetalamaSample/Calculator.cs", "Multiply(")
        shot("code-vision", editorArea(330))

        editorAction("LamaRider.ShowAspects")
        val aspects = popup("Metalama Aspects")
        shot("code-vision-popup", editorArea(330).union(windowOf(aspects)).grown())
        close(aspects)

        caretAt("MetalamaSample/Calculator.cs", "Add(")
        editorAction("LamaRider.ShowTransformedCode")
        val transformed = popup("Entering Add")
        shot("transformed-code-popup", editorArea(330).union(windowOf(transformed)).grown())
        close(transformed)

        caretAt("MetalamaSample/LogAttribute.cs", "LogAttribute :")
        shot("tsharp-highlighting", editorArea(560))

        invokeAction("LamaRider.TrackCaret")
        try {
            caretAt("MetalamaSample/Calculator.cs", "Multiply(")
            ideFrame { waitForText("aspects at the caret", "NonNegative") }
            shot("track-caret", frame())
        } finally {
            invokeAction("LamaRider.TrackCaret")
        }

        caretAt("MetalamaSample/Calculator.cs", "Multiply(")
        invokeAction("HideAllWindows") // the diff gets the frame's width
        editorAction("LamaRider.MetalamaDiff")
        shot("metalama-diff", windowOf(popup("Transformed (read-only)")))

        // Not blocking: the dialog is modal.
        invokeAction("LamaRider.Settings", now = false)
        val settings = windowOf(popup("Aspect Tests"))
        shot("settings", settings)
        ui.x(xQuery { byTitle("Settings") }).x(xQuery { byText("Cancel") }).click()
        waitFor("settings closed", timeout = 30.seconds) { !ui.x(xQuery { byTitle("Settings") }).present() }

        // Rider's Jump to Colors and Fonts on T# code opens the Metalama T# page.
        caretAt("MetalamaSample/LogAttribute.cs", "meta.Proceed")
        Thread.sleep(5000)
        ideFrame { invokeAction("JumpToColorsAndFonts", now = false, component = codeEditor().component) }
        // A chooser first: C# (the class `meta`) or Metalama T#.
        popup("Template keyword (meta)").click()
        waitFor("color settings open", timeout = 1.minutes) { ui.x(xQuery { byTitle("Settings") }).present() }
        Thread.sleep(3000)
        shot("tsharp-color-settings", windowOf(popup("Color Scheme")))
        ui.x(xQuery { byTitle("Settings") }).x(xQuery { byText("Cancel") }).click()
    }

    @Test
    fun `error info bar`() = withRider("shots-errors", prepare = { dir ->
        // A project-wide error (no location): LAMA0041, an exception in a fabric.
        dir.resolve("MetalamaSample/Fabric.cs").writeText(
            """
            using Metalama.Framework.Fabrics;

            namespace MetalamaSample;

            public class Fabric : ProjectFabric
            {
                public override void AmendProject(IProjectAmender amender) => throw new InvalidOperationException("Not ready yet");
            }
            """.trimIndent() + "\n")
    }) {
        layout()
        openToolWindow("LamaRider")
        caretAt("MetalamaSample/LogAttribute.cs", "LogAttribute :")
        val banner = popup("Metalama: LAMA0041")
        shot("error-banner", editorArea(330).union(banner.rect()).grown())
    }

    @Test
    fun `aspect test file and debugging into the aspect`() = withRider("shots-debugaspect") {
        layout()
        openToolWindow("LamaRider")
        ideFrame { waitForText("aspects listed (first refresh done)", "Log") }

        // A new test: opens prefilled in the aspect test project.
        caretAt("MetalamaSample/LogAttribute.cs", "LogAttribute :")
        editorAction("LamaRider.CreateAspectTest")
        val test = solutionDir.resolve("MetalamaSample.AspectTests/LogTests/LogTest.cs")
        waitFor("test file created", timeout = 1.minutes) { Files.exists(test) }
        // Its build (so Rider lists the test) must not run into the debug session's.
        runCatching { waitFor("test project built", timeout = 3.minutes) { ideFrame().hasSubtext("Added the test") } }
        caretAt("MetalamaSample.AspectTests/LogTests/LogTest.cs", "Add(")
        shot("create-aspect-test", frame())

        val aspect = solutionDir.resolve("MetalamaSample/LogAttribute.cs")
        val line = Files.readAllLines(aspect).indexOfFirst { it.contains("var name = meta.Target.Method.Name;") }
        breakpoint("MetalamaSample/LogAttribute.cs", line)

        caretAt("MetalamaSample.AspectTests/Log/Add.cs", "Add(")
        ideFrame {
            waitFor("run icon in the gutter", timeout = 2.minutes) {
                gutter().getGutterIcons().any { it.mark.getTooltipText()?.contains("Metalama aspect test") == true }
            }
        }
        shot("aspect-test", editorArea(260))

        debugUntilPaused { editorAction("LamaRider.DebugAspectTest") }
        ideFrame { waitForText("compiled copy banner", "Metalama's compiled copy of LogAttribute.cs") }
        shot("debug-aspect-test", frame())
    }

    @Test
    fun `debugging transformed code`() = withRider("shots-debugtransformed", prepare = { dir ->
        // What Settings › Tools › LamaRider › Debug transformed code writes; built once so the transformed file exists.
        dir.resolve("Directory.Build.props").writeText(
            """
            <Project>
              <PropertyGroup Label="LamaRider">
                <MetalamaDebugTransformedCode>true</MetalamaDebugTransformedCode>
              </PropertyGroup>
            </Project>
            """.trimIndent() + "\n")
        dotnet("build", dir.resolve("MetalamaSample/MetalamaSample.csproj").toString())
    }) {
        layout()
        openToolWindow("LamaRider")
        ideFrame { waitForText("aspects listed (first refresh done)", "Log") }
        // Stop in Program.cs, then step into Multiply: the debugger continues in its transformed code.
        breakpoint("MetalamaSample/Program.cs", 0)
        caretAt("MetalamaSample/Program.cs", "Multiply")
        debugUntilPaused { invokeAction("Debug", now = false) }
        val debugger = service(XDebuggerManager::class, singleProject())
        // Program.cs creates the Calculator first: into its constructor, back out, then into Multiply.
        for (step in listOf<XDebugSession.() -> Unit>({ stepInto() }, { stepOut() }, { stepInto() })) {
            withContext<Unit>(OnDispatcher.EDT) { debugger.getCurrentSession()?.step() }
            Thread.sleep(1000)
            waitFor("stepped", timeout = 1.minutes) { debugger.getCurrentSession()?.isPaused() == true }
        }
        Thread.sleep(3000)
        shot("debug-transformed-code", frame())
    }

    /** One frame size for every screenshot (no window manager on Xvfb: Rider keeps whatever size it starts with). */
    private fun Driver.layout() {
        var frame: Component? = null
        ideFrame { frame = component }
        withContext<Unit>(OnDispatcher.EDT) { cast(frame!!, AwtWindow::class).setBounds(0, 0, 1600, 1000) }
        Thread.sleep(2000)
    }

    private fun Driver.frame(): Rectangle = ideFrame().rect()

    /** The editor with its gutter, [height] pixels from its top. */
    private fun Driver.editorArea(height: Int): Rectangle {
        Thread.sleep(5000) // Code Vision, inlay hints and highlighting of a just opened file
        val editor = ideFrame().codeEditor().rect()
        val gutter = ideFrame().gutter().rect()
        return Rectangle(gutter.x, editor.y, editor.x + editor.width - gutter.x, height)
    }

    private fun Driver.aspectList(): JTreeUiComponent =
        ideFrame().xx(JTreeUiComponent::class.java) { byClass("Tree") }.list().first { it.hasSubtext("Metalama.Patterns.Contracts") }

    /** A component showing [text], in any window (popups, dialogs and diff windows are windows of their own). */
    private fun Driver.popup(text: String): UiComponent {
        val query = xQuery { contains(byVisibleText(text)) }
        waitFor("'$text' shown", timeout = 5.minutes) { ui.x(query).present() }
        return ui.x(query)
    }

    private fun Driver.windowOf(c: UiComponent): Rectangle {
        val window = utility(SwingUtilities::class).getWindowAncestor(c.component) ?: return c.rect()
        val p = window.getLocationOnScreen()
        return Rectangle(p.x, p.y, window.width, window.height)
    }

    private fun Driver.close(popupContent: UiComponent) {
        withContext<Unit>(OnDispatcher.EDT) { utility(PopupUtil::class).getPopupContainerFor(popupContent.component)?.cancel() }
        Thread.sleep(500)
    }

    private fun Driver.breakpoint(relativePath: String, line: Int) {
        check(line >= 0) { "No breakpoint line in $relativePath" }
        val file = file(relativePath)
        withContext<Unit>(OnDispatcher.EDT) { service(com.intellij.driver.sdk.XDebuggerUtil::class).toggleLineBreakpoint(singleProject(), file, line, false) }
    }

    private fun Driver.debugUntilPaused(start: Driver.() -> Unit) {
        val debugger = service(XDebuggerManager::class, singleProject())
        start()
        waitFor("debug session started", timeout = 10.minutes) { debugger.getCurrentSession() != null }
        waitFor("stopped at the breakpoint", timeout = 5.minutes) { debugger.getCurrentSession()?.isPaused() == true }
        Thread.sleep(3000) // variables, frames, the editor at the breakpoint
    }

    private fun Driver.shot(name: String, area: Rectangle) {
        expireNotifications(singleProject()) // balloons ("Cannot update plugin", ...) would cover the tool windows
        Thread.sleep(1000)
        val rect = area.intersection(Rectangle(Toolkit.getDefaultToolkit().screenSize))
        Files.createDirectories(out)
        ImageIO.write(Robot().createScreenCapture(rect), "png", out.resolve("$name.png").toFile())
        println("Screenshot $name: $rect")
    }

    private fun UiComponent.rect(): Rectangle =
        component.getLocationOnScreen().let { Rectangle(it.x, it.y, component.width, component.height) }

    private fun Rectangle.grown() = Rectangle(this).apply { grow(16, 16) }
}

@Remote("java.awt.Window")
interface AwtWindow {
    fun setBounds(x: Int, y: Int, width: Int, height: Int)
}

@Remote("javax.swing.SwingUtilities")
interface SwingUtilities {
    fun getWindowAncestor(c: Component): Component?
}

@Remote("com.intellij.openapi.ui.popup.util.PopupUtil")
interface PopupUtil {
    fun getPopupContainerFor(c: Component): JBPopup?
}

@Remote("com.intellij.openapi.ui.popup.JBPopup")
interface JBPopup {
    fun cancel()
}
