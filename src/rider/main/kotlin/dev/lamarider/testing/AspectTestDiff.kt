package dev.lamarider.testing

import com.intellij.diff.DiffContentFactory
import com.intellij.diff.DiffManager
import com.intellij.diff.requests.SimpleDiffRequest
import com.intellij.diff.util.DiffUserDataKeys
import com.intellij.icons.AllIcons
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.command.WriteCommandAction
import com.intellij.openapi.components.service
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.DumbAwareAction
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.ui.EditorNotifications
import dev.lamarider.LamaRiderService

/**
 * The last run's output of an aspect test against its .t.cs snapshot. The snapshot side is the real file: Rider's diff
 * arrows take single changes, it can be edited, and Accept All takes the whole output.
 */
object AspectTestDiff {
    fun show(project: Project, files: AspectTestOutput.Files) {
        val output = files.output ?: return project.service<LamaRiderService>().notify("LamaRider",
            "${files.test.name} has no output yet: run the aspect test first.")
        val snapshot = snapshotFile(files) ?: return
        val factory = DiffContentFactory.getInstance()
        val request = SimpleDiffRequest(
            "Metalama test: ${files.test.name}",
            factory.create(project, output.readText().replace("\r\n", "\n"), snapshot.fileType),
            factory.create(project, snapshot),
            "Last run output (read-only)",
            "${snapshot.name} (expected)",
        )
        request.putUserData(DiffUserDataKeys.CONTEXT_ACTIONS, listOf(acceptAll(project, files)))
        DiffManager.getInstance().showDiff(project, request)
    }

    /** The whole output into the snapshot: an undoable edit of its document, saved right away. */
    fun accept(project: Project, files: AspectTestOutput.Files) {
        val output = files.output ?: return
        val snapshot = snapshotFile(files) ?: return
        val document = FileDocumentManager.getInstance().getDocument(snapshot) ?: return
        WriteCommandAction.runWriteCommandAction(project, "Accept Metalama Test Output", null, {
            document.setText(output.readText().replace("\r\n", "\n"))
            FileDocumentManager.getInstance().saveDocument(document)
        })
        EditorNotifications.getInstance(project).updateAllNotifications()
    }

    private fun acceptAll(project: Project, files: AspectTestOutput.Files): AnAction =
        object : DumbAwareAction("Accept All", "Replace ${files.snapshot.name} with the last run's output", AllIcons.Actions.Commit) {
            override fun actionPerformed(e: AnActionEvent) = accept(project, files)
        }

    /** The snapshot, created empty when the test has none yet. */
    private fun snapshotFile(files: AspectTestOutput.Files): VirtualFile? {
        if (!files.snapshot.exists()) files.snapshot.writeText("")
        return LocalFileSystem.getInstance().refreshAndFindFileByIoFile(files.snapshot)
    }
}
