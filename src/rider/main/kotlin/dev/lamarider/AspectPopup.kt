package dev.lamarider

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.service
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.popup.JBPopupFactory
import com.intellij.openapi.wm.ToolWindowManager
import com.intellij.ui.awt.RelativePoint
import java.awt.MouseInfo
import dev.lamarider.diff.MetalamaDiff
import dev.lamarider.explorer.AspectExplorerPanel
import dev.lamarider.explorer.AspectExplorerToolWindowFactory
import dev.lamarider.explorer.Navigation

/** What a Code Vision click on a declaration (identified by doc id) offers. */
object AspectPopup {
    /** [atMouse]: opened from a Code Vision click (pointer is on the hint); otherwise anchored at the caret. */
    fun show(project: Project, docId: String, atMouse: Boolean = false) = ApplicationManager.getApplication().invokeLater {
        val model = project.service<LamaRiderService>().model ?: return@invokeLater
        val instances = model.instancesOn(docId)
        if (model.aspectClass(docId) != null && instances.isEmpty()) {
            showInExplorer(project, docId)
            return@invokeLater
        }

        val choices = mutableListOf<Pair<String, () -> Unit>>()
        instances.firstOrNull()?.location?.let { loc -> choices += "Preview Transformed Code" to { MetalamaDiff.open(project, loc.file, loc.start) } }
        for (instance in instances) {
            val cls = model.aspectClass(instance.aspectClass)
            val name = cls?.name ?: instance.aspectClass.substringAfterLast('.')
            choices += "Show $name in Aspect Explorer (${instance.appliedVia})" to { showInExplorer(project, instance.aspectClass) }
            cls?.location?.let { loc -> choices += "Go to $name" to { Navigation.open(project, loc) } }
        }
        if (model.aspectClass(docId) != null) choices += "Show targets in Aspect Explorer" to { showInExplorer(project, docId) }
        if (choices.isEmpty()) return@invokeLater
        val editor = FileEditorManager.getInstance(project).selectedTextEditor ?: return@invokeLater

        val popup = JBPopupFactory.getInstance()
            .createPopupChooserBuilder(choices.map { it.first })
            .setTitle("Metalama Aspects")
            .setItemChosenCallback { label -> choices.first { it.first == label }.second() }
            .createPopup()
        val pointer = if (atMouse) MouseInfo.getPointerInfo()?.location else null
        if (pointer != null) popup.show(RelativePoint.fromScreen(pointer)) else popup.showInBestPositionFor(editor)
    }

    private fun showInExplorer(project: Project, aspectClassId: String) {
        ToolWindowManager.getInstance(project).getToolWindow(AspectExplorerToolWindowFactory.ID)?.activate {
            AspectExplorerPanel.current?.select(aspectClassId)
        }
    }
}
