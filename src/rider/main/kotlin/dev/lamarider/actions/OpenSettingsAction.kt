package dev.lamarider.actions

import com.intellij.icons.AllIcons
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.options.ShowSettingsUtil
import com.intellij.openapi.project.DumbAwareAction
import dev.lamarider.settings.LamaRiderConfigurable

class OpenSettingsAction : DumbAwareAction("LamaRider Settings…", "Settings › Tools › LamaRider", AllIcons.General.Settings) {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT

    override fun actionPerformed(e: AnActionEvent) =
        ShowSettingsUtil.getInstance().showSettingsDialog(e.project, LamaRiderConfigurable::class.java)
}
