package dev.lamarider.actions

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.components.service
import com.intellij.openapi.project.DumbAwareAction
import dev.lamarider.AspectModelIds
import dev.lamarider.LamaRiderService
import dev.lamarider.TransformedCodePopup

/** Transformed code of the declaration at the caret in a popup (whole-file Metalama Diff when there is none). */
class ShowTransformedCodeAction : DumbAwareAction() {
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
            service.notify("No Metalama aspect here", "Place the caret on a declaration that an aspect targets.")
            return
        }
        TransformedCodePopup.showOrDiff(project, file.path, AspectModelIds.owner(target), editor.caretModel.offset)
    }
}
