package dev.lamarider.debug

import com.intellij.ide.actions.OpenInRightSplitAction
import com.intellij.openapi.components.service
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.editor.LogicalPosition
import com.intellij.openapi.editor.ScrollType
import com.intellij.openapi.editor.event.CaretEvent
import com.intellij.openapi.editor.event.CaretListener
import com.intellij.openapi.fileEditor.FileEditor
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.fileEditor.TextEditor
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.Messages
import com.intellij.xdebugger.XDebuggerManager
import com.jetbrains.rd.ide.model.RunAspectTest
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.ui.EditorNotificationPanel
import com.intellij.ui.EditorNotificationProvider
import com.intellij.ui.EditorNotifications
import dev.lamarider.LamaRiderService
import dev.lamarider.explorer.Navigation
import java.io.File
import java.util.function.Function
import javax.swing.JComponent

/**
 * On Metalama's compiled copy of compile-time code (where the debugger stops in aspects, templates and fabrics): what it
 * is, the source side by side with synced carets, and readable formatting. The copy itself is never changed: the
 * debugger maps it by line.
 */
class CompiledCopyBanner : EditorNotificationProvider {
    override fun collectNotificationData(project: Project, file: VirtualFile): Function<in FileEditor, out JComponent?>? {
        if (file.extension != "cs") return null
        // The source in this solution, not in another clone of the project.
        val solutionDir = project.basePath?.let { File(it).path } ?: return null
        val copy = CompileTimeMaps.copyFor(File(file.path), CompileTimeMaps.root()) { it.startsWith(solutionDir, ignoreCase = File.separatorChar == '\\') } ?: return null
        val source = File(copy.source)
        return Function { fileEditor ->
            EditorNotificationPanel(fileEditor, EditorNotificationPanel.Status.Info).apply {
                text = "Metalama's compiled copy of ${source.name}: the code the debugger runs"
                createActionLabel("Show ${source.name} side by side") { showSideBySide(project, fileEditor, copy) }
                createActionLabel("Open ${source.name} at this line") {
                    Navigation.openLine(project, source.path, copy.sourceLine(caretLine(fileEditor)) ?: 0)
                }
                val service = project.service<LamaRiderService>()
                if (!service.formatCompileTimeCode) {
                    createActionLabel("Make it readable") {
                        service.setFormatCompileTimeCode(true)
                        EditorNotifications.getInstance(project).updateNotifications(file)
                        debugAgain(project, service)
                    }
                }
            }
        }
    }

    /** Formatting applies from the next build of the aspect project: offer to restart the aspect test (it builds first). */
    private fun debugAgain(project: Project, service: LamaRiderService) {
        val title = "Readable Aspect Code"
        val test = service.lastDebuggedTest
        if (test == null) {
            Messages.showInfoMessage(project, "Metalama formats compile-time code from the next build on. Rebuild the aspect project and debug again.", title)
            return
        }
        val answer = Messages.showYesNoDialog(project,
            "Metalama formats compile-time code from the next build on. Stop debugging and debug ${File(test).name} again? Rider builds the aspect project first.",
            title, "Debug Again", "Later", Messages.getQuestionIcon())
        if (answer != Messages.YES) return
        XDebuggerManager.getInstance(project).currentSession?.stop()
        service.runAspectTest(RunAspectTest(test, true))
    }

    private fun caretLine(fileEditor: FileEditor) = (fileEditor as? TextEditor)?.editor?.caretModel?.logicalPosition?.line ?: 0

    /** The source in the right split; moving the caret in either editor moves the other to the matching line. */
    private fun showSideBySide(project: Project, fileEditor: FileEditor, copy: CompileTimeMaps.Copy) {
        val copyEditor = (fileEditor as? TextEditor)?.editor ?: return
        val sourceFile = LocalFileSystem.getInstance().refreshAndFindFileByPath(copy.source.replace('\\', '/')) ?: return
        OpenInRightSplitAction.openInRightSplit(project, sourceFile, null, false)
        val sourceEditor = FileEditorManager.getInstance(project).getEditors(sourceFile).filterIsInstance<TextEditor>().lastOrNull()?.editor ?: return

        var syncing = false
        fun follow(from: Editor, to: Editor, map: (Int) -> Int?) = object : CaretListener {
            override fun caretPositionChanged(event: CaretEvent) {
                if (syncing || to.isDisposed || event.editor != from) return
                val line = map(event.newPosition.line) ?: return
                syncing = true
                try {
                    to.caretModel.moveToLogicalPosition(LogicalPosition(line, 0))
                    to.scrollingModel.scrollToCaret(ScrollType.CENTER)
                } finally {
                    syncing = false
                }
            }
        }
        // Both listeners end with the compiled copy's editor tab.
        copyEditor.caretModel.addCaretListener(follow(copyEditor, sourceEditor, copy::sourceLine), fileEditor)
        sourceEditor.caretModel.addCaretListener(follow(sourceEditor, copyEditor, copy::copyLine), fileEditor)
        copy.sourceLine(copyEditor.caretModel.logicalPosition.line)?.let {
            sourceEditor.caretModel.moveToLogicalPosition(LogicalPosition(it, 0))
            sourceEditor.scrollingModel.scrollToCaret(ScrollType.CENTER)
        }
    }
}
