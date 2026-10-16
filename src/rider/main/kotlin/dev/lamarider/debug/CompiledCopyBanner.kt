package dev.lamarider.debug

import com.intellij.openapi.fileEditor.FileEditor
import com.intellij.openapi.fileEditor.TextEditor
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.ui.EditorNotificationPanel
import com.intellij.ui.EditorNotificationProvider
import dev.lamarider.explorer.Navigation
import java.io.File
import java.util.function.Function
import javax.swing.JComponent

/**
 * On Metalama's compiled copy of compile-time code (where the debugger stops in aspects, templates and fabrics): what it
 * is and a way back to the source line. The copy is not reformatted: the debugger maps it by line.
 */
class CompiledCopyBanner : EditorNotificationProvider {
    override fun collectNotificationData(project: Project, file: VirtualFile): Function<in FileEditor, out JComponent?>? {
        if (file.extension != "cs") return null
        val copy = CompileTimeMaps.copyFor(File(file.path), CompileTimeMaps.root()) ?: return null
        val source = File(copy.source)
        return Function { editor ->
            EditorNotificationPanel(editor, EditorNotificationPanel.Status.Info).apply {
                text = "Metalama's compiled copy of ${source.name}: the code the debugger runs (kept as compiled so its lines match the debugger)"
                createActionLabel("Open ${source.name} at this line") {
                    val caretLine = (editor as? TextEditor)?.editor?.caretModel?.logicalPosition?.line ?: 0
                    Navigation.openLine(project, source.path, copy.sourceLine(caretLine) ?: 0)
                }
            }
        }
    }
}
