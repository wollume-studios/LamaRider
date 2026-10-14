package dev.lamarider.settings

import com.intellij.openapi.components.service
import com.intellij.openapi.options.BoundConfigurable
import com.intellij.openapi.project.Project
import com.intellij.ui.dsl.builder.bindSelected
import com.intellij.ui.dsl.builder.panel
import dev.lamarider.LamaRiderService
import dev.lamarider.actions.AutoBuildAction
import dev.lamarider.actions.ConfigureTestsAction

/** Settings › Tools › LamaRider. */
class LamaRiderConfigurable(private val project: Project) : BoundConfigurable("LamaRider") {
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
            row {
                button("Configure Rider for Metalama Tests…") { ConfigureTestsAction.choose(project) }
                    .comment("xUnit discovery by Test Runner and no orphaned-process prompt for diff tools.")
            }
        }
    }
}
