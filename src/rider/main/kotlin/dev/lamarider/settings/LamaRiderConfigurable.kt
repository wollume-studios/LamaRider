package dev.lamarider.settings

import com.intellij.openapi.components.service
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.util.text.StringUtil
import com.intellij.openapi.options.BoundConfigurable
import com.intellij.openapi.project.Project
import com.intellij.ui.dsl.builder.MAX_LINE_LENGTH_WORD_WRAP
import com.intellij.ui.dsl.builder.bindSelected
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
        group("Debugging") {
            row {
                checkBox("Debug transformed code in every build configuration")
                    .comment(
                        "Sets <code>MetalamaDebugTransformedCode</code> in the solution's Directory.Build.props. " +
                            "Without it, use <b>Debug with LamaDebug</b> in the LamaRider tool window.",
                    )
                    .bindSelected({ service.debugTransformedCode }, { if (it != service.debugTransformedCode) service.setDebugTransformedCode(it) })
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
