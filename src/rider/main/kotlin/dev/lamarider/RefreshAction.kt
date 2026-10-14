package dev.lamarider

import com.intellij.icons.AllIcons
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.components.service
import com.intellij.openapi.project.DumbAwareAction

class RefreshAction : DumbAwareAction("Refresh Metalama Aspects", null, AllIcons.Actions.Refresh) {
    override fun actionPerformed(e: AnActionEvent) {
        e.project?.service<LamaRiderService>()?.refresh()
    }
}
