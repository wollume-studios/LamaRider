package dev.lamarider

import com.intellij.ide.util.PropertiesComponent
import com.intellij.openapi.components.service
import com.intellij.openapi.project.Project
import com.intellij.openapi.project.ProjectManager
import com.jetbrains.rd.ide.model.HintOptions
import com.intellij.openapi.ui.popup.JBPopupFactory
import com.intellij.openapi.ui.popup.PopupStep
import com.intellij.openapi.ui.popup.util.BaseListPopupStep
import com.intellij.openapi.wm.IdeFocusManager
import com.intellij.openapi.wm.ToolWindowManager
import com.intellij.ui.awt.RelativePoint
import dev.lamarider.explorer.AspectExplorerPanel
import dev.lamarider.explorer.AspectExplorerToolWindowFactory
import dev.lamarider.explorer.Navigation
import java.awt.MouseInfo

/** What clicking an aspect in the Code Vision popup or the transformed-code popup does (Settings › Tools › LamaRider). */
enum class AspectClickAction(val label: String) {
    SOURCE("Go to the aspect's source"),
    LAMARIDER("Show it in the LamaRider window"),
    ASK("Ask (sub-menu)");

    companion object {
        private const val KEY = "lamarider.aspectClick"
        var current: AspectClickAction
            get() = PropertiesComponent.getInstance().getValue(KEY)?.let { v -> entries.firstOrNull { it.name == v } } ?: SOURCE
            set(value) = PropertiesComponent.getInstance().setValue(KEY, value.name, SOURCE.name)
    }
}

/** How Code Vision shows aspects (Settings › Tools › LamaRider). */
enum class AspectHintStyle(val label: String) {
    NAMED("Each aspect by name where it applies"),
    COUNT("Their number on the declaration"),
}

/** How the transformed code of a declaration is reached from Code Vision. */
enum class TransformedCodeAccess(val label: String) {
    HINT("\"transformed code\" hint of its own"),
    MENU("Item in the aspect menu"),
}

object AspectHintSettings {
    private const val STYLE = "lamarider.hintStyle"
    private const val TRANSFORMED = "lamarider.transformedCodeAccess"

    var style: AspectHintStyle
        get() = PropertiesComponent.getInstance().getValue(STYLE)?.let { v -> AspectHintStyle.entries.firstOrNull { it.name == v } } ?: AspectHintStyle.NAMED
        set(value) = PropertiesComponent.getInstance().setValue(STYLE, value.name, AspectHintStyle.NAMED.name)

    var transformedCode: TransformedCodeAccess
        get() = PropertiesComponent.getInstance().getValue(TRANSFORMED)?.let { v -> TransformedCodeAccess.entries.firstOrNull { it.name == v } } ?: TransformedCodeAccess.HINT
        set(value) = PropertiesComponent.getInstance().setValue(TRANSFORMED, value.name, TransformedCodeAccess.HINT.name)

    fun options() = HintOptions(countOnly = style == AspectHintStyle.COUNT, transformedCodeHint = transformedCode == TransformedCodeAccess.HINT)

    /** Every open solution's backend redraws its hints. */
    fun apply() = ProjectManager.getInstance().openProjects.forEach { it.service<LamaRiderService>().sendHintOptions() }
}

object AspectClick {
    /** Go to the aspect class: its source, or for library aspects ReSharper's navigation (SourceLink or decompiled). */
    fun goToSource(project: Project, aspectClassId: String) {
        val location = project.service<LamaRiderService>().model?.aspectClass(aspectClassId)?.location
        if (location != null) Navigation.open(project, location) else project.service<LamaRiderService>().navigateToClass(aspectClassId)
    }

    fun showInLamaRider(project: Project, aspectClassId: String) {
        ToolWindowManager.getInstance(project).getToolWindow(AspectExplorerToolWindowFactory.ID)?.activate {
            AspectExplorerPanel.current?.select(aspectClassId)
        }
    }

    /** The two choices, for an "Ask" sub-menu. */
    fun choices(project: Project, aspectClassId: String): List<Pair<String, () -> Unit>> = listOf(
        "Go to Source" to { goToSource(project, aspectClassId) },
        "Show in LamaRider" to { showInLamaRider(project, aspectClassId) },
    )

    /** A click outside a menu (e.g. a link): runs the configured action, or asks at the mouse pointer. */
    fun run(project: Project, aspectClassId: String) = when (AspectClickAction.current) {
        AspectClickAction.SOURCE -> goToSource(project, aspectClassId)
        AspectClickAction.LAMARIDER -> showInLamaRider(project, aspectClassId)
        AspectClickAction.ASK -> {
            val items = choices(project, aspectClassId)
            val step = object : BaseListPopupStep<Pair<String, () -> Unit>>(null, items) {
                override fun getTextFor(value: Pair<String, () -> Unit>) = value.first
                override fun onChosen(value: Pair<String, () -> Unit>, finalChoice: Boolean): PopupStep<*>? =
                    doFinalStep { IdeFocusManager.getInstance(project).doWhenFocusSettlesDown { value.second() } }
            }
            val popup = JBPopupFactory.getInstance().createListPopup(step)
            MouseInfo.getPointerInfo()?.location?.let { popup.show(RelativePoint.fromScreen(it)) } ?: popup.showCenteredInCurrentWindow(project)
        }
    }
}
