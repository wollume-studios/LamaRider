package dev.lamarider.uitest

import com.intellij.driver.client.Driver
import com.intellij.driver.client.Remote
import com.intellij.driver.model.OnDispatcher
import com.intellij.driver.sdk.VirtualFile
import com.intellij.driver.sdk.invokeAction
import com.intellij.driver.sdk.openEditor
import com.intellij.driver.sdk.selectedTextEditor
import com.intellij.driver.sdk.singleProject
import com.intellij.driver.sdk.ui.components.UiComponent
import com.intellij.driver.sdk.ui.components.common.codeEditor
import com.intellij.driver.sdk.ui.components.common.ideFrame
import com.intellij.driver.sdk.waitFor
import com.intellij.driver.sdk.waitForIndicators
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
import com.intellij.platform.testFramework.teamCity.TeamCityReporter
import com.intellij.tools.ide.starter.product.rider.Rider
import org.junit.jupiter.api.Assertions.assertTrue
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

/**
 * A real Rider with the built plugin on a fresh copy of the sample solution (see [withRider]), and the helpers the UI tests
 * share. Everything goes through actions and IDE APIs (no OS mouse/keyboard), so the tests run without focus.
 */
abstract class UiTestBase {
    protected lateinit var solutionDir: Path

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
                        // Rider's own debugger data-flow hints assert on files outside the solution (Metalama's compiled copy).
                        if ("DFASupportCSharp" in details) return
                        fail { "$testName fails: $message.\n$details" }
                    }
                }
            }
        }
    }

    /**
     * An editor action with the editor as data context. The Alt+Enter twins of these actions share the backend code, but
     * the intention popup needs keyboard focus, which test Riders on a virtual display don't get.
     */
    protected fun Driver.editorAction(id: String) =
        ideFrame { invokeAction(id, component = codeEditor().component) }

    protected fun dotnet(vararg args: String): Int =
        ProcessBuilder("dotnet", *args).inheritIO().start().waitFor()

    protected fun UiComponent.waitForText(what: String, text: String) =
        waitFor(what, timeout = 5.minutes) { hasSubtext(text) }

    protected fun Driver.file(relativePath: String): VirtualFile {
        val path = solutionDir.resolve(relativePath).toString().replace('\\', '/')
        // VFS refresh of a new file off the EDT (on it, it needs the write-intent lock).
        return utility(LocalFileSystem::class).getInstance().refreshAndFindFileByPath(path) ?: fail("No file $path")
    }

    /** Rider's project model has no content roots the driver's project-relative lookup understands: open by absolute path. */
    protected fun Driver.caretAt(relativePath: String, marker: String) {
        val file = file(relativePath)
        withContext<Unit>(OnDispatcher.EDT) { openEditor(file, singleProject()) }
        // Moving the caret makes Rider sync the editor, which reads the PSI: needs a read action (2026.3 asserts it).
        withReadAction<Unit>(OnDispatcher.EDT) {
            val editor = selectedTextEditor() ?: fail("No editor for $relativePath")
            val offset = editor.getDocument().getText().indexOf(marker)
            assertTrue(offset >= 0, "'$marker' not in $relativePath")
            editor.getCaretModel().moveToOffset(offset)
        }
    }

    protected fun Driver.editorText(): String =
        withReadAction(OnDispatcher.EDT) { selectedTextEditor()?.getDocument()?.getText() ?: "" }

    /** [prepare] changes the sample copy before Rider opens it. */
    /** [sample]: a folder next to samples/MetalamaSample with a solution of the same name. */
    protected fun withRider(name: String, prepare: (Path) -> Unit = {}, sample: String = "MetalamaSample", solution: String = sample, test: Driver.() -> Unit) {
        solutionDir = sampleCopy(sample)
        prepare(solutionDir)
        val context = Starter.newContext(name, TestCase(rider(), SolutionProject(solutionDir.resolve("$solution.sln"))))
        PluginConfigurator(context).installPluginFromDir(Path.of(System.getProperty("lamarider.plugin")))
        // Fresh temp copy of the sample each test: without this, Rider asks to trust it (a dialog on the user's screen).
        context.applyVMOptionsPatch { addSystemProperty("idea.trust.all.projects", true) }
        configure(context)
        // A cold Rider start (first run, busy machine) alone can take ~10 minutes.
        context.runIdeWithDriver(runTimeout = 45.minutes).useDriverAndCloseIde {
            waitForIndicators(30.minutes)
            test()
        }
    }

    /** Extra setup of the test IDE before it starts. */
    protected open fun configure(context: IDETestContext) {}

    private fun rider(): IdeInfo {
        val path = System.getProperty("lamarider.rider")?.let(Path::of)
            ?: System.getenv("LOCALAPPDATA")?.let { Path.of(it, "Programs", "Rider") }
            ?: fail("Set LAMARIDER_RIDER to a Rider installation")
        if (!path.exists()) fail("No Rider at $path; set LAMARIDER_RIDER")
        return IdeInfo.Rider.copy(getInstaller = { ExistingIdeInstaller(path) })
    }

    /** The sample without build output, so every test starts from the same sources. */
    private fun sampleCopy(sample: String): Path {
        val source = Path.of(System.getProperty("lamarider.sample")).resolveSibling(sample)
        val target = createTempDirectory("lamarider-ui-").resolve(source.name)
        Files.walk(source).use { paths ->
            paths.filter { p -> source.relativize(p).none { it.name in setOf("bin", "obj", ".idea") || it.name.startsWith("_ReSharper") } }
                .forEach { p ->
                    val dst = target.resolve(source.relativize(p).toString())
                    if (Files.isDirectory(p)) Files.createDirectories(dst) else Files.copy(p, dst)
                }
        }
        check(target.resolve("$sample.sln").readText().isNotEmpty())
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

@Remote("com.intellij.xdebugger.XDebuggerManager", plugin = "com.intellij/intellij.platform.debugger")
interface XDebuggerManager {
    fun getCurrentSession(): XDebugSession?
}

@Remote("com.intellij.xdebugger.XDebugSession", plugin = "com.intellij/intellij.platform.debugger")
interface XDebugSession {
    fun isPaused(): Boolean
    fun stepInto()
    fun stepOut()
    fun stop()
}

@Remote("com.intellij.openapi.fileEditor.FileEditorManager")
interface FileEditorManagerRef {
    fun getInstance(project: com.intellij.driver.sdk.Project): FileEditorManagerRef
    fun getSelectedFiles(): Array<VirtualFile>
    fun getOpenFiles(): Array<VirtualFile>
    fun getAllEditors(file: VirtualFile): Array<TextEditorRef>
    fun openFile(file: VirtualFile, focusEditor: Boolean): Array<TextEditorRef>
}

@Remote("com.intellij.openapi.fileEditor.TextEditor")
interface TextEditorRef {
    fun getEditor(): EditorRef
}

@Remote("com.intellij.openapi.editor.Editor")
interface EditorRef {
    fun getCaretModel(): CaretModelRef
    fun getDocument(): DocumentRef
}

@Remote("com.intellij.openapi.editor.CaretModel")
interface CaretModelRef {
    fun getOffset(): Int
    fun moveToOffset(offset: Int)
}

@Remote("com.intellij.openapi.editor.Document")
interface DocumentRef {
    fun getText(): String
    fun getLineNumber(offset: Int): Int
}
