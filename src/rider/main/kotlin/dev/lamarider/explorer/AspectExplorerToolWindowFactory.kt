package dev.lamarider.explorer

import com.intellij.openapi.actionSystem.ActionGroup
import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.project.DumbAware
import com.intellij.openapi.project.Project
import com.intellij.openapi.wm.ToolWindow
import com.intellij.openapi.wm.ToolWindowFactory
import com.intellij.ui.content.ContentFactory

class AspectExplorerToolWindowFactory : ToolWindowFactory, DumbAware {
    override fun createToolWindowContent(project: Project, toolWindow: ToolWindow) {
        val panel = AspectExplorerPanel(project, toolWindow.disposable)
        toolWindow.contentManager.addContent(ContentFactory.getInstance().createContent(panel, "", false))
        (ActionManager.getInstance().getAction("LamaRider.Gear") as? ActionGroup)?.let(toolWindow::setAdditionalGearActions)
    }

    companion object {
        const val ID = "LamaRider"
    }
}
