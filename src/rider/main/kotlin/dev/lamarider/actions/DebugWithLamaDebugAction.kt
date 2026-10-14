package dev.lamarider.actions

import com.intellij.icons.AllIcons
import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.components.service
import com.intellij.openapi.project.DumbAwareAction
import com.intellij.openapi.ui.Messages
import com.jetbrains.rider.projectView.SolutionConfigurationManager
import dev.lamarider.LamaRiderService

/**
 * Metalama's documented way to debug transformed code: switch to the LamaDebug configuration and debug.
 * Selects LamaDebug (adding it to the solution first, after asking) and starts the selected run configuration.
 */
class DebugWithLamaDebugAction : DumbAwareAction(
    "Debug with LamaDebug", "Debug the transformed code: select the LamaDebug configuration, then Debug", AllIcons.Actions.StartDebugger,
) {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        e.presentation.isEnabledAndVisible = e.project != null
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val manager = SolutionConfigurationManager.getInstance(project)
        val active = manager.activeConfigurationAndPlatform
        val lamaDebug = manager.solutionConfigurationsAndPlatforms.filter { it.configuration == LAMA_DEBUG }
        val target = lamaDebug.firstOrNull { it.platform == active?.platform } ?: lamaDebug.firstOrNull()
        if (target == null) {
            val answer = Messages.showYesNoDialog(
                project,
                "The solution has no LamaDebug configuration yet. Metalama builds it with transformed code you can step through " +
                    "and set breakpoints in (obj/LamaDebug/net…/metalama).\n\nAdd LamaDebug to the solution file, copied from Debug?",
                "Debug with LamaDebug", "Add LamaDebug", "Cancel", Messages.getQuestionIcon(),
            )
            if (answer == Messages.YES) project.service<LamaRiderService>().addLamaDebugConfiguration()
            return
        }
        if (target != active) manager.activeConfigurationAndPlatform = target
        val debug = ActionManager.getInstance().getAction("Debug") ?: return
        ActionManager.getInstance().tryToExecute(debug, e.inputEvent, null, e.place, true)
    }

    companion object {
        const val LAMA_DEBUG = "LamaDebug"
    }
}
