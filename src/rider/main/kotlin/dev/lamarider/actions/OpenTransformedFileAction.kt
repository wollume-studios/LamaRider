package dev.lamarider.actions

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.components.service
import com.intellij.openapi.fileEditor.OpenFileDescriptor
import com.intellij.openapi.project.DumbAwareAction
import com.intellij.openapi.vfs.LocalFileSystem
import dev.lamarider.LamaRiderService
import dev.lamarider.diff.ObjTransformedFiles
import java.io.File

/** Opens obj/<Configuration>/<TFM>/metalama/<file> (written by MetalamaDebugTransformedCode) as a real editor, so breakpoints can be set. */
class OpenTransformedFileAction : DumbAwareAction() {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        e.presentation.isEnabledAndVisible = e.project != null && e.getData(CommonDataKeys.VIRTUAL_FILE)?.extension == "cs"
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val source = e.getData(CommonDataKeys.VIRTUAL_FILE) ?: return
        val transformed = ObjTransformedFiles.find(source.path)
            ?.let { LocalFileSystem.getInstance().refreshAndFindFileByIoFile(File(it)) }
        if (transformed == null) {
            project.service<LamaRiderService>().notify(
                "No transformed file for ${source.name}",
                "Turn on Debug Transformed Code (Tools menu) and rebuild the solution.",
            )
            return
        }
        OpenFileDescriptor(project, transformed).navigate(true)
    }
}
