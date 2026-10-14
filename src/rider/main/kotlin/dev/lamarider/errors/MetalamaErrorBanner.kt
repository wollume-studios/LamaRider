package dev.lamarider.errors

import com.intellij.ide.BrowserUtil
import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.actionSystem.ActionPlaces
import com.intellij.openapi.components.service
import com.intellij.openapi.fileEditor.FileEditor
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.ui.EditorNotificationPanel
import com.intellij.ui.EditorNotificationProvider
import dev.lamarider.LamaRiderService
import dev.lamarider.explorer.Navigation
import java.io.File
import java.util.function.Function
import javax.swing.JComponent

/** Like the Visual Studio extension's info bar: Metalama errors stop the aspects, shown on top of every C# editor. */
class MetalamaErrorBanner : EditorNotificationProvider {
    override fun collectNotificationData(project: Project, file: VirtualFile): Function<in FileEditor, out JComponent?>? {
        if (!file.name.endsWith(".cs", ignoreCase = true)) return null
        val errors = project.service<LamaRiderService>().model?.errors().orEmpty()
        if (errors.isEmpty()) return null
        val first = errors.first()
        return Function { editor ->
            EditorNotificationPanel(editor, EditorNotificationPanel.Status.Error).apply {
                // Full list in the tooltip; the first error leads since the bar truncates.
                text = "Metalama: ${first.id} ${first.message.lineSequence().first()}" +
                    (if (errors.size > 1) " (+${errors.size - 1} more)" else "") + ". Aspects are not applied."
                first.file?.let { path ->
                    createActionLabel("Go to error") { Navigation.openLine(project, path, (first.line ?: 1) - 1) }
                }
                if (first.id.startsWith("LAMA")) {
                    createActionLabel("${first.id} documentation") { BrowserUtil.browse("https://doc.metalama.net/errors/${first.id.lowercase()}") }
                }
                createActionLabel("Build solution") {
                    ActionManager.getInstance().getAction("BuildSolutionAction")?.let { action ->
                        ActionManager.getInstance().tryToExecute(action, null, null, ActionPlaces.NOTIFICATION, true)
                    }
                }
                toolTipText = errors.joinToString("\n") { e -> "${e.id}: ${e.message}" + (e.file?.let { " (${File(it).name}:${e.line})" } ?: "") }
            }
        }
    }
}
