package dev.lamarider.actions

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.components.service
import com.intellij.openapi.project.DumbAwareAction
import com.intellij.openapi.ui.popup.JBPopupFactory
import dev.lamarider.LamaRiderService
import dev.lamarider.TestSettingsScope

/** Tools › Configure Rider for Metalama Tests: same settings as the notification, on demand. */
class ConfigureTestsAction : DumbAwareAction() {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        e.presentation.isEnabledAndVisible = e.project != null
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        JBPopupFactory.getInstance()
            .createPopupChooserBuilder(TestSettingsScope.entries.toList())
            .setRenderer(com.intellij.ui.dsl.listCellRenderer.textListCellRenderer { it.label })
            .setTitle("Configure Rider for Metalama Tests")
            .setItemChosenCallback { scope -> project.service<LamaRiderService>().applyTestSettings(scope.id) }
            .createPopup()
            .showCenteredInCurrentWindow(project)
    }
}
