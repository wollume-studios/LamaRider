package dev.lamarider.uitest

import com.intellij.driver.client.Driver
import com.intellij.driver.sdk.invokeAction
import com.intellij.driver.client.Remote
import com.intellij.driver.model.OnDispatcher
import com.intellij.driver.sdk.VirtualFile
import com.intellij.driver.sdk.openEditor
import com.intellij.driver.sdk.openToolWindow
import com.intellij.driver.sdk.selectedTextEditor
import com.intellij.driver.sdk.singleProject
import com.intellij.driver.sdk.ui.components.UiComponent
import com.intellij.driver.sdk.ui.components.common.codeEditor
import com.intellij.driver.sdk.ui.components.common.ideFrame
import com.intellij.driver.sdk.waitFor
import com.intellij.driver.sdk.waitForIndicators
import com.intellij.driver.sdk.ui.ui
import com.intellij.driver.sdk.ui.xQuery
import com.intellij.platform.testFramework.teamCity.TeamCityReporter
import com.intellij.ide.starter.ci.CIServer
import com.intellij.ide.starter.ci.NoCIServer
import com.intellij.ide.starter.di.di
import com.intellij.ide.starter.driver.engine.runIdeWithDriver
import com.intellij.ide.starter.ide.IDETestContext
import com.intellij.ide.starter.ide.installer.ExistingIdeInstaller
import com.intellij.ide.starter.models.IdeInfo
import com.intellij.ide.starter.models.TestCase
import com.intellij.ide.starter.plugins.PluginConfigurator
import com.intellij.ide.starter.project.ProjectInfoSpec
import com.intellij.ide.starter.runner.Starter
import com.intellij.tools.ide.starter.product.rider.Rider
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.fail
import org.kodein.di.DI
import org.kodein.di.bindSingleton
import java.nio.file.Files
import java.nio.file.Path
import kotlin.io.path.createTempDirectory
import kotlin.io.path.exists
import kotlin.io.path.name
import kotlin.io.path.readText
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds

/**
 * End-to-end tests in a real Rider: the built plugin, the sample solution, the real worker and backend.
 * Everything goes through actions and IDE APIs (no OS mouse/keyboard), so the tests run without focus.
 * Run: ./gradlew uiTest (Rider from LAMARIDER_RIDER, else %LOCALAPPDATA%\Programs\Rider).
 */
class LamaRiderUiTest {
    private lateinit var solutionDir: Path

    init {
        // IDE exceptions fail the test instead of going to TeamCity.
        di = DI {
            extend(di)
            bindSingleton<CIServer>(overrides = true) {
                object : CIServer by NoCIServer {
                    override fun reportTestFailure(
                        testName: String, message: String, details: String, linkToLogs: String?,
                        kind: TeamCityReporter.SyntheticTestKind, isIgnored: Boolean, metadata: List<TeamCityReporter.TestMetadata>,
                    ) {
                        fail { "$testName fails: $message.\n$details" }
                    }
                }
            }
        }
    }

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
            }.onFailure { println("Added.cs not ready: $it") }.getOrDefault(false)
        }
    }

    @Test
    fun `Track Caret lists only the aspects at the caret`() = withRider("track") {
        openToolWindow("LamaRider")
        ideFrame { waitForText("aspects listed (first refresh done)", "Log") }
        invokeAction("LamaRider.TrackCaret")
        try {
            caretAt("MetalamaSample/Links.cs", "address")
            ideFrame { waitForText("contract on the parameter", "Url") }
            caretAt("MetalamaSample/Calculator.cs", "Multiply(")
            ideFrame {
                waitForText("fabric aspect on Multiply", "Log")
                waitFor("Url gone outside Links", timeout = 30.0.toDuration()) { !hasSubtext("Url") }
            }
        } finally {
            invokeAction("LamaRider.TrackCaret") // the setting is global to the test IDE
        }
    }

    private fun Double.toDuration() = kotlin.time.Duration.parse("${this}s")

    private fun UiComponent.waitForText(what: String, text: String) =
        waitFor(what, timeout = 5.minutes) { hasSubtext(text) }

    /** Rider's project model has no content roots the driver's project-relative lookup understands: open by absolute path. */
    private fun Driver.caretAt(relativePath: String, marker: String) = withContext<Unit>(OnDispatcher.EDT) {
        val path = solutionDir.resolve(relativePath).toString().replace('\\', '/')
        val file = utility(LocalFileSystem::class).getInstance().refreshAndFindFileByPath(path) ?: fail("No file $path")
        openEditor(file, singleProject())
        val editor = selectedTextEditor() ?: fail("No editor for $relativePath")
        val offset = editor.getDocument().getText().indexOf(marker)
        assertTrue(offset >= 0, "'$marker' not in $relativePath")
        editor.getCaretModel().moveToOffset(offset)
    }

    private fun withRider(name: String, test: Driver.() -> Unit) {
        solutionDir = sampleCopy()
        val context = Starter.newContext(name, TestCase(rider(), SolutionProject(solutionDir.resolve("MetalamaSample.sln"))))
        PluginConfigurator(context).installPluginFromDir(Path.of(System.getProperty("lamarider.plugin")))
        // Fresh temp copy of the sample each test: without this, Rider asks to trust it (a dialog on the user's screen).
        context.applyVMOptionsPatch { addSystemProperty("idea.trust.all.projects", true) }
        // A cold Rider start (first run, busy machine) alone can take ~10 minutes.
        context.runIdeWithDriver(runTimeout = 45.minutes).useDriverAndCloseIde {
            waitForIndicators(30.minutes)
            test()
        }
    }

    private fun rider(): IdeInfo {
        val path = System.getProperty("lamarider.rider")?.let(Path::of)
            ?: System.getenv("LOCALAPPDATA")?.let { Path.of(it, "Programs", "Rider") }
            ?: fail("Set LAMARIDER_RIDER to a Rider installation")
        if (!path.exists()) fail("No Rider at $path; set LAMARIDER_RIDER")
        return IdeInfo.Rider.copy(getInstaller = { ExistingIdeInstaller(path) })
    }

    /** The sample without build output, so every test starts from the same sources. */
    private fun sampleCopy(): Path {
        val source = Path.of(System.getProperty("lamarider.sample"))
        val target = createTempDirectory("lamarider-ui-").resolve(source.name)
        Files.walk(source).use { paths ->
            paths.filter { p -> source.relativize(p).none { it.name in setOf("bin", "obj", ".idea") || it.name.startsWith("_ReSharper") } }
                .forEach { p ->
                    val dst = target.resolve(source.relativize(p).toString())
                    if (Files.isDirectory(p)) Files.createDirectories(dst) else Files.copy(p, dst)
                }
        }
        check(target.resolve("MetalamaSample.sln").readText().isNotEmpty())
        return target
    }
}

/** Opens a solution file: given its folder, Rider asks which solution to open (a modal dialog the test can't answer). */
private class SolutionProject(private val solution: Path) : ProjectInfoSpec {
    override val isReusable = true
    override val downloadTimeout = 1.minutes
    override val configureProjectBeforeUse: (IDETestContext) -> Unit = {}
    override fun downloadAndUnpackProject(): Path = solution
}

@Remote("com.intellij.openapi.vfs.LocalFileSystem")
interface LocalFileSystem {
    fun getInstance(): LocalFileSystem
    fun refreshAndFindFileByPath(path: String): VirtualFile?
}
