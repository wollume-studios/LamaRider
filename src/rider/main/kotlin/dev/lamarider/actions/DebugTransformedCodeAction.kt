package dev.lamarider.actions

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.ToggleAction
import com.intellij.openapi.components.service
import com.intellij.openapi.project.DumbAware
import dev.lamarider.LamaRiderService

/** Sets MetalamaDebugTransformedCode in the solution's Directory.Build.props: the debugger then steps through transformed code. */
class DebugTransformedCodeAction : ToggleAction(), DumbAware {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT

    override fun isSelected(e: AnActionEvent) = e.project?.service<LamaRiderService>()?.debugTransformedCode ?: false

    override fun setSelected(e: AnActionEvent, state: Boolean) {
        val service = e.project?.service<LamaRiderService>() ?: return
        service.setDebugTransformedCode(state)
        service.notify(
            if (state) "Debugging transformed code" else "Debugging source code",
            "Rebuild the solution to apply." + if (state) " Set breakpoints in the transformed files (Open Transformed File)." else "",
        )
    }
}
