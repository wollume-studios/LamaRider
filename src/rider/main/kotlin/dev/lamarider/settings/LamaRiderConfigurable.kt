package dev.lamarider.settings

import com.intellij.openapi.components.service
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.util.text.StringUtil
import com.intellij.openapi.options.BoundConfigurable
import com.intellij.openapi.project.Project
import com.intellij.ui.dsl.builder.MAX_LINE_LENGTH_WORD_WRAP
import com.intellij.ui.SimpleListCellRenderer
import com.intellij.ui.dsl.builder.bindItem
import com.intellij.ui.dsl.builder.bindSelected
import dev.lamarider.AspectClickAction
import com.intellij.ui.dsl.builder.panel
import dev.lamarider.LamaRiderService
import dev.lamarider.actions.AutoBuildAction
import dev.lamarider.actions.ConfigureTestsAction
import javax.swing.JEditorPane

/** Settings › Tools › LamaRider. */
class LamaRiderConfigurable(private val project: Project) : BoundConfigurable("LamaRider") {
    private lateinit var testStatus: JEditorPane
    private val listening = Disposer.newDisposable()

    private fun showTestStatus(status: Pair<Boolean?, String>?) {
        val icon = when (status?.first) {
            true -> "<icon src='AllIcons.General.InspectionsOK'>&nbsp;"
            false -> "<icon src='AllIcons.General.Warning'>&nbsp;"
            null -> ""
        }
        if (::testStatus.isInitialized) testStatus.text = icon + StringUtil.escapeXmlEntities(status?.second ?: "Checking…")
    }

    override fun disposeUIResources() {
        super.disposeUIResources()
        Disposer.dispose(listening)
    }

    override fun createPanel() = panel {
        val service = project.service<LamaRiderService>()
        group("Metalama Analysis") {
            row {
                checkBox("Build the project after aspect or template code is saved")
                    .comment("Metalama pauses its design-time analysis after compile-time code changes until the project is built (LAMA0118).")
                    .bindSelected({ AutoBuildAction.isEnabled() }, { AutoBuildAction.setEnabled(project, it) })
            }
        }
        group("Code Vision") {
            row("Clicking an aspect:") {
                comboBox(AspectClickAction.entries, SimpleListCellRenderer.create("") { it.label })
                    .comment("In the Code Vision menu and the transformed-code popup. Library aspects open their SourceLink or decompiled source.")
                    .bindItem({ AspectClickAction.current }, { AspectClickAction.current = it ?: AspectClickAction.SOURCE })
            }
        }
        group("Debugging") {
            row {
                checkBox("Debug transformed code in every build configuration")
                    .comment(
                        "Sets <code>MetalamaDebugTransformedCode</code> in the solution's Directory.Build.props. " +
                            "Without it, use <b>Debug with LamaDebug</b> in the LamaRider tool window.",
                    )
                    .bindSelected({ service.debugTransformedCode }, { if (it != service.debugTransformedCode) service.setDebugTransformedCode(it) })
            }
            row {
                checkBox("Format transformed code")
                    .comment(
                        "Readable transformed files when debugging them (this setting or LamaDebug), but slower builds: Metalama warns with " +
                            "LAMA0066. Off writes <code>MetalamaFormatOutput=false</code> to Directory.Build.props. " +
                            "LamaRider's popup and Metalama Diff are formatted either way.",
                    )
                    .bindSelected({ service.formatTransformedCode }, { if (it != service.formatTransformedCode) service.setFormatTransformedCode(it) })
            }
            row {
                checkBox("Readable aspect code while debugging")
                    .comment(
                        "When the debugger stops in an aspect, template or fabric, it shows Metalama's compiled copy of that code. " +
                            "On: Metalama formats it (no <code>global::</code>, normal indentation); builds of aspect projects get slower. " +
                            "Writes <code>MetalamaFormatCompileTimeCode=true</code> to Directory.Build.props; rebuild the aspect project after changing it.",
                    )
                    .bindSelected({ service.formatCompileTimeCode }, { if (it != service.formatCompileTimeCode) service.setFormatCompileTimeCode(it) })
            }
        }
        group("Aspect Tests") {
            row { text("", maxLineLength = MAX_LINE_LENGTH_WORD_WRAP).applyToComponent { testStatus = this } }
            row {
                button("Configure Rider for Metalama Tests…") { ConfigureTestsAction.choose(project) }
                    .comment("xUnit discovery by Test Runner and no orphaned-process prompt for diff tools.")
            }
        }
        // Live: the backend answers the refresh, and again after Configure… applied the settings.
        service.addListener(listening) { showTestStatus(service.testSettingsStatus) }
        showTestStatus(service.testSettingsStatus)
        service.refreshTestSettingsStatus()
    }
}
