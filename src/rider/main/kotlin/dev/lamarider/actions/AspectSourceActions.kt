package dev.lamarider.actions

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.components.service
import com.intellij.openapi.project.DumbAwareAction
import com.jetbrains.rd.ide.model.EditorPosition
import dev.lamarider.LamaRiderService

/** Editor menu twins of the Alt+Enter actions on a declaration name: shown in files aspects transform. */
abstract class AspectSourceAction(private val fire: LamaRiderService.(EditorPosition) -> Unit) : DumbAwareAction() {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE)
        e.presentation.isEnabledAndVisible = e.getData(CommonDataKeys.EDITOR) != null && file?.extension == "cs" &&
            e.project?.service<LamaRiderService>()?.transformedPathFor(file.path) != null
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val editor = e.getData(CommonDataKeys.EDITOR) ?: return
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE) ?: return
        project.service<LamaRiderService>().fire(EditorPosition(file.path, editor.caretModel.offset))
    }
}

class AddAspectsAsAttributesAction : AspectSourceAction(LamaRiderService::addAspectsAsAttributes)

class ReplaceWithTransformedCodeAction : AspectSourceAction(LamaRiderService::replaceWithTransformedCode)

/** Twins of the Metalama Premium API quick-fixes (Find Action and UI tests; Alt+Enter on the warning is the usual way). */
abstract class PremiumAction(private val fire: LamaRiderService.(EditorPosition) -> Unit) : DumbAwareAction() {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        e.presentation.isEnabledAndVisible = e.getData(CommonDataKeys.EDITOR) != null && e.getData(CommonDataKeys.VIRTUAL_FILE)?.extension == "cs"
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val editor = e.getData(CommonDataKeys.EDITOR) ?: return
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE) ?: return
        project.service<LamaRiderService>().fire(EditorPosition(file.path, editor.caretModel.offset))
    }
}

class WrapPremiumCodeAction : PremiumAction(LamaRiderService::wrapPremiumCode)

class SetUpPremiumBuildsAction : PremiumAction(LamaRiderService::setUpPremiumBuilds)

/** Every quick-fix of the fix samples, results in fix-selftest.json next to the solution: asks for it and refreshes (Find Action only). */
class SelfTestFixesAction : DumbAwareAction() {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val dir = project.basePath ?: return
        java.io.File(dir, "fix-selftest.request").writeText("")
        project.service<LamaRiderService>().refresh()
    }
}
