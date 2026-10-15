package dev.lamarider.actions

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.components.service
import com.intellij.openapi.project.DumbAwareAction
import com.intellij.openapi.vfs.VirtualFile
import com.jetbrains.rd.ide.model.EditorPosition
import com.jetbrains.rd.ide.model.RunAspectTest
import dev.lamarider.LamaRiderService

/** Editor menu: Metalama aspect test for the aspect class at the caret (same as Alt+Enter on the class name). */
class CreateAspectTestAction : DumbAwareAction() {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        e.presentation.isEnabledAndVisible = e.project != null && e.getData(CommonDataKeys.EDITOR) != null &&
            e.getData(CommonDataKeys.VIRTUAL_FILE)?.extension == "cs"
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val editor = e.getData(CommonDataKeys.EDITOR) ?: return
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE) ?: return
        project.service<LamaRiderService>().createAspectTest(EditorPosition(file.path, editor.caretModel.offset))
    }
}

/** Editor menu: saves the last run's output of this aspect test as its .t.cs snapshot (same as the Alt+Enter action). */
class AcceptAspectTestOutputAction : DumbAwareAction() {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE)
        e.presentation.isEnabledAndVisible = e.project != null && file?.extension == "cs" && !file.name.endsWith(".t.cs")
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE) ?: return
        project.service<LamaRiderService>().acceptAspectTestOutput(file.path)
    }
}

/** Editor and Solution Explorer menus: runs or debugs the Metalama aspect test of a file in Rider's unit test runner. */
abstract class RunAspectTestActionBase(private val debug: Boolean) : DumbAwareAction() {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE)
        e.presentation.isEnabledAndVisible = e.project != null && file != null && isAspectTestFile(file)
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE) ?: return
        project.service<LamaRiderService>().runAspectTest(RunAspectTest(file.path, debug))
    }

    companion object {
        /** A main test file (.cs, not a .t.cs snapshot) of a project that references Metalama.Testing.AspectTesting. */
        fun isAspectTestFile(file: VirtualFile): Boolean {
            if (file.extension != "cs" || file.name.endsWith(".t.cs")) return false
            val csproj = generateSequence(file.parent) { it.parent }
                .firstNotNullOfOrNull { dir -> dir.children.firstOrNull { it.extension == "csproj" } } ?: return false
            return runCatching { String(csproj.contentsToByteArray()) }.getOrDefault("").contains("Metalama.Testing.AspectTesting", ignoreCase = true)
        }
    }
}

class RunAspectTestAction : RunAspectTestActionBase(debug = false)

class DebugAspectTestAction : RunAspectTestActionBase(debug = true)
