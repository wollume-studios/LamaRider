package dev.lamarider.actions

import com.intellij.ide.util.PropertiesComponent
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.ToggleAction
import com.intellij.openapi.components.service
import com.intellij.openapi.project.DumbAware
import com.intellij.openapi.project.Project
import dev.lamarider.LamaRiderService

/** Off by default: builds the project after aspect/template code is saved, so Metalama resumes without a manual build. */
class AutoBuildAction : ToggleAction(), DumbAware {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT

    override fun isSelected(e: AnActionEvent) = isEnabled()

    override fun setSelected(e: AnActionEvent, state: Boolean) {
        e.project?.let { setEnabled(it, state) }
    }

    companion object {
        private const val KEY = "lamarider.autoBuild"
        fun isEnabled() = PropertiesComponent.getInstance().getBoolean(KEY)

        fun setEnabled(project: Project, enabled: Boolean) {
            PropertiesComponent.getInstance().setValue(KEY, enabled)
            project.service<LamaRiderService>().setAutoBuild(enabled)
        }
    }
}
