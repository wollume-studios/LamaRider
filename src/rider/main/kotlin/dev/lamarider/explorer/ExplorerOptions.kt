package dev.lamarider.explorer

import com.intellij.icons.AllIcons
import com.intellij.ide.util.PropertiesComponent
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.DefaultActionGroup
import com.intellij.openapi.project.DumbAwareToggleAction
import dev.lamarider.AspectGrouping

/** LamaRider window view options (remembered). */
object ExplorerOptions {
    private const val ONLY_AT_CARET = "lamarider.onlyAtCaret"
    private const val GROUPING = "lamarider.grouping"

    var onlyAtCaret: Boolean
        get() = PropertiesComponent.getInstance().getBoolean(ONLY_AT_CARET)
        set(value) = PropertiesComponent.getInstance().setValue(ONLY_AT_CARET, value)

    var grouping: AspectGrouping
        get() = PropertiesComponent.getInstance().getValue(GROUPING)?.let { runCatching { AspectGrouping.valueOf(it) }.getOrNull() } ?: AspectGrouping.PACKAGE
        set(value) = PropertiesComponent.getInstance().setValue(GROUPING, value.name)
}

/** With Track Caret: the aspect list shows only the aspects at the caret, not all aspects. */
class OnlyAtCaretAction : DumbAwareToggleAction("Show Only Aspects at Caret", "With Track Caret, list only the aspects affecting the code at the caret", AllIcons.General.Filter) {
    override fun getActionUpdateThread() = ActionUpdateThread.EDT

    override fun update(e: AnActionEvent) {
        super.update(e)
        e.presentation.isEnabled = TrackCaretAction.isEnabled()
    }

    override fun isSelected(e: AnActionEvent) = ExplorerOptions.onlyAtCaret

    override fun setSelected(e: AnActionEvent, state: Boolean) {
        ExplorerOptions.onlyAtCaret = state
        AspectExplorerPanel.current?.viewOptionsChanged()
    }
}

/** "Group By" menu of the aspect list: ungrouped, project/package, namespace. */
class GroupingActions : DefaultActionGroup("Group By", true) {
    init {
        templatePresentation.icon = AllIcons.Actions.GroupBy
        AspectGrouping.entries.forEach { grouping ->
            add(object : DumbAwareToggleAction(grouping.label) {
                override fun getActionUpdateThread() = ActionUpdateThread.EDT
                override fun isSelected(e: AnActionEvent) = ExplorerOptions.grouping == grouping
                override fun setSelected(e: AnActionEvent, state: Boolean) {
                    if (!state) return
                    ExplorerOptions.grouping = grouping
                    AspectExplorerPanel.current?.viewOptionsChanged()
                }
            })
        }
    }
}
