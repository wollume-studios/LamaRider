package dev.lamarider

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.service
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.popup.JBPopupFactory
import com.intellij.openapi.ui.popup.ListSeparator
import com.intellij.openapi.ui.popup.PopupStep
import com.intellij.openapi.ui.popup.util.BaseListPopupStep
import com.intellij.openapi.wm.IdeFocusManager
import com.intellij.ui.awt.RelativePoint
import java.awt.MouseInfo
import dev.lamarider.diff.MetalamaDiff

/**
 * What a Code Vision click on a declaration offers: its transformed code, then its aspects grouped by what they decorate
 * (the member, each parameter, the return value). [docId] is a member id, or a parameter target from its inlay hint.
 */
object AspectPopup {
    private class Item(val label: String, val separator: String? = null, val sub: List<Item> = emptyList(), val run: (() -> Unit)? = null)

    /** [atMouse]: opened from a Code Vision click (pointer is on the hint); otherwise anchored at the caret. */
    fun show(project: Project, docId: String, atMouse: Boolean = false) = ApplicationManager.getApplication().invokeLater {
        val model = project.service<LamaRiderService>().model ?: return@invokeLater
        val member = AspectModelIds.owner(docId)
        val groups = model.aspectsByTarget(docId)
        if (model.aspectClass(docId) != null && groups.isEmpty()) {
            AspectClick.showInLamaRider(project, docId)
            return@invokeLater
        }

        val location = model.instancesOn(member).firstNotNullOfOrNull { it.location }
        // Only attribute aspects ("transformed code" hint): the transformed code right away; it links the aspects.
        if (location != null && groups.isNotEmpty() && groups.all { g -> g.entries.all { it.viaAttribute } } &&
            TransformedCodePopup.show(project, location.file, member, caretAt = location.start)) return@invokeLater

        val items = mutableListOf<Item>()
        val pointer = if (atMouse) MouseInfo.getPointerInfo()?.location else null
        location?.let { loc ->
            if (model.transformedDeclaration(loc.file, member) != null) {
                items += Item("Show Transformed Code") { TransformedCodePopup.show(project, loc.file, member, caretAt = loc.start); Unit }
            }
            items += Item("Metalama Diff (whole file)") { MetalamaDiff.open(project, loc.file, loc.start) }
        }
        for (group in groups) {
            group.entries.forEachIndexed { i, entry ->
                val separator = if (i == 0) group.caption else null
                val id = entry.instance.aspectClass
                items += when (AspectClickAction.current) {
                    AspectClickAction.SOURCE -> Item(entry.label, separator) { AspectClick.goToSource(project, id) }
                    AspectClickAction.LAMARIDER -> Item(entry.label, separator) { AspectClick.showInLamaRider(project, id) }
                    AspectClickAction.ASK -> Item(entry.label, separator, sub = AspectClick.choices(project, id).map { (label, run) -> Item(label, run = run) })
                }
            }
        }
        if (model.aspectClass(docId) != null) items += Item("Show targets in LamaRider", "Aspect class") { AspectClick.showInLamaRider(project, docId) }
        if (items.isEmpty()) return@invokeLater
        val editor = FileEditorManager.getInstance(project).selectedTextEditor ?: return@invokeLater

        val popup = JBPopupFactory.getInstance().createListPopup(step(project, "Metalama Aspects", items))
        if (pointer != null) popup.show(RelativePoint.fromScreen(pointer)) else popup.showInBestPositionFor(editor)
    }

    private fun step(project: Project, title: String?, items: List<Item>): BaseListPopupStep<Item> = object : BaseListPopupStep<Item>(title, items) {
        override fun getTextFor(value: Item) = value.label
        override fun getSeparatorAbove(value: Item) = value.separator?.let(::ListSeparator)
        override fun hasSubstep(value: Item) = value.sub.isNotEmpty()
        override fun onChosen(value: Item, finalChoice: Boolean): PopupStep<*>? {
            if (value.sub.isNotEmpty()) return step(project, null, value.sub)
            // After focus returns to the editor: a popup opened earlier is deactivated (and cancelled) by that.
            return doFinalStep { IdeFocusManager.getInstance(project).doWhenFocusSettlesDown { value.run?.invoke() } }
        }
    }
}
