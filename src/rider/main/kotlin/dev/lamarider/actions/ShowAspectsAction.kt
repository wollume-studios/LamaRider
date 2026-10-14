package dev.lamarider.actions

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.components.service
import com.intellij.openapi.project.DumbAwareAction
import dev.lamarider.AspectPopup
import dev.lamarider.LamaRiderService

/** Keyboard equivalent of clicking the Code Vision entry: aspects of the declaration at the caret. */
class ShowAspectsAction : DumbAwareAction() {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        e.presentation.isEnabledAndVisible = e.project != null && e.getData(CommonDataKeys.EDITOR) != null &&
            e.getData(CommonDataKeys.VIRTUAL_FILE)?.extension == "cs"
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val editor = e.getData(CommonDataKeys.EDITOR) ?: return
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE) ?: return
        val service = project.service<LamaRiderService>()
        val target = service.model?.targetAt(file.path, editor.caretModel.offset)
        if (target == null) {
            service.notify("No Metalama aspect here", "Place the caret on a declaration that an aspect targets, or on an aspect class.")
            return
        }
        AspectPopup.show(project, target)
    }
}
