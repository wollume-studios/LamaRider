package model.rider

import com.jetbrains.rd.generator.nova.*
import com.jetbrains.rd.generator.nova.PredefinedType.*
import com.jetbrains.rider.model.nova.ide.SolutionModel

@Suppress("unused")
object LamaRiderModel : Ext(SolutionModel.Solution) {
    init {
        // backend -> frontend: human-readable refresh state
        property("status", string.nullable)
        // backend -> frontend: path of the latest model.json (a new path on every successful run)
        property("modelPath", string.nullable)
        // frontend -> backend: re-run the worker
        signal("refresh", void)
        // backend -> frontend: a refresh failed (message incl. log path), shown as a balloon
        signal("failed", string)
        // backend -> frontend: MetalamaDebugTransformedCode is set in the solution's Directory.Build.props
        property("debugTransformedCode", false)
        // frontend -> backend: add/remove that setting
        signal("setDebugTransformedCode", bool)
        // backend -> frontend: solution has aspect tests but Rider is not configured for them (ask the user)
        signal("testSettingsSuggested", void)
        // frontend -> backend: apply the aspect-test settings; payload is the scope: Shared | Personal | Global
        signal("applyTestSettings", string)
        // backend -> frontend: informational message, shown as a balloon
        signal("info", string)
        // backend -> frontend: Code Vision entry clicked, payload is the declaration's doc id
        signal("codeVisionClicked", string)
    }
}
