package dev.lamarider

import com.intellij.ide.util.PropertiesComponent
import com.intellij.openapi.components.service
import com.intellij.openapi.project.Project
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
