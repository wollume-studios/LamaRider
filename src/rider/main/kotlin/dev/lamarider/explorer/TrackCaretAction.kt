package dev.lamarider.explorer

import com.intellij.ide.util.PropertiesComponent
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.project.DumbAwareToggleAction

/** LamaRider window: the targets tree shows the aspects affecting the code at the caret (remembered). */
class TrackCaretAction : DumbAwareToggleAction() {
    override fun getActionUpdateThread() = ActionUpdateThread.EDT

    override fun isSelected(e: AnActionEvent) = isEnabled()

    override fun setSelected(e: AnActionEvent, state: Boolean) {
        PropertiesComponent.getInstance().setValue(KEY, state)
        AspectExplorerPanel.current?.trackCaretChanged()
    }

    companion object {
        private const val KEY = "lamarider.trackCaret"
        fun isEnabled() = PropertiesComponent.getInstance().getBoolean(KEY)
    }
}
