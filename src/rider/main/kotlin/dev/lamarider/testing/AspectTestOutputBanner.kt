package dev.lamarider.testing

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.fileEditor.FileEditor
import com.intellij.openapi.project.DumbAwareAction
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.openapi.vfs.newvfs.BulkFileListener
import com.intellij.openapi.vfs.newvfs.events.VFileEvent
import com.intellij.ui.EditorNotificationPanel
import com.intellij.ui.EditorNotificationProvider
import com.intellij.ui.EditorNotifications
import java.io.File
import java.util.function.Function
import javax.swing.JComponent

/** On an aspect test and its .t.cs: the last run's output differs from the snapshot. */
class AspectTestOutputBanner : EditorNotificationProvider {
    override fun collectNotificationData(project: Project, file: VirtualFile): Function<in FileEditor, out JComponent?>? {
        if (file.extension != "cs") return null
        val files = AspectTestOutput.of(File(file.path)) ?: return null
        // Loaded into the VFS so that the next run's output raises file events (OutputChanges); a test run is a separate process.
        LocalFileSystem.getInstance().refreshIoFiles(listOf(files.projectDir.resolve("obj")), true, false, null)
        LocalFileSystem.getInstance().refreshIoFiles(listOf(files.outputDir), true, true, null)
        if (!files.differs()) return null
        return Function { fileEditor ->
            EditorNotificationPanel(fileEditor, EditorNotificationPanel.Status.Warning).apply {
                text = if (files.snapshot.isFile && files.snapshot.length() > 0) "Test output differs from ${files.snapshot.name}"
                    else "${files.snapshot.name} is empty"
                createActionLabel("Compare") { AspectTestDiff.show(project, files) }
                createActionLabel("Accept all") { AspectTestDiff.accept(project, files) }
            }
        }
    }
}

/** New test output or an edited snapshot: banners update. */
class OutputChanges : BulkFileListener {
    override fun after(events: List<VFileEvent>) {
        if (events.any { it.path.endsWith(".t.cs") || it.path.contains("/obj/transformed") }) EditorNotifications.updateAll()
    }
}

/** Editor and project view menu: the diff of the last run's output and the snapshot. */
class CompareAspectTestOutputAction : DumbAwareAction() {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE)
        e.presentation.isEnabledAndVisible = e.project != null && file?.extension == "cs" && AspectTestOutput.of(File(file.path)) != null
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE) ?: return
        AspectTestOutput.of(File(file.path))?.let { AspectTestDiff.show(project, it) }
    }
}
