package model.rider

import com.jetbrains.rd.generator.nova.*
import com.jetbrains.rd.generator.nova.PredefinedType.*
import com.jetbrains.rider.model.nova.ide.SolutionModel

@Suppress("unused")
object LamaRiderModel : Ext(SolutionModel.Solution) {
    private val EditorPosition = structdef {
        field("file", string)
        field("offset", int)
    }

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
        // backend -> frontend: Metalama formats transformed code when debugging it (MetalamaFormatOutput not false)
        property("formatTransformedCode", true)
        // backend -> frontend: MetalamaFormatCompileTimeCode in our Directory.Build.props group
        property("formatCompileTimeCode", false)
        // frontend -> backend: write MetalamaFormatOutput=false (or remove it) in the solution's Directory.Build.props
        signal("setFormatTransformedCode", bool)
        signal("setFormatCompileTimeCode", bool)
        // backend -> frontend: solution has aspect tests but Rider is not configured for them (ask the user)
        signal("testSettingsSuggested", void)
        // frontend -> backend: apply the aspect-test settings; payload is the scope: Shared | Personal | Global
        signal("applyTestSettings", string)
        // backend -> frontend: informational message, shown as a balloon
        signal("info", string)
        // backend -> frontend: Code Vision entry clicked, payload is the declaration's doc id
        signal("codeVisionClicked", string)
        // frontend -> backend: open a (library) aspect class via ReSharper navigation (sources or decompiler); payload is "T:Full.Name"
        signal("navigateToClass", string)
        // frontend -> backend: build a project after its aspect/template code is saved (opt-in, off by default)
        property("autoBuild", false)
        // frontend -> backend: add Metalama's LamaDebug configuration to the solution file (copied from Debug)
        signal("addLamaDebugConfiguration", void)
        // backend -> frontend: Rider set up for aspect tests? (settings page); text explains the state
        property("testSettingsConfigured", bool.nullable)
        property("testSettingsStatus", string.nullable)
        // frontend -> backend: recompute the two above (settings page opened)
        signal("refreshTestSettingsStatus", void)
        // backend -> frontend: open a file the backend created (aspect test, accepted snapshot)
        signal("openFile", string)
        // frontend -> backend (editor menu): aspect test for the aspect class at the position / accept the test output
        signal("createAspectTest", EditorPosition)
        signal("acceptAspectTestOutput", string)
        // frontend -> backend (editor menu twins of the Alt+Enter actions): for the declaration whose name is at the position,
        // write the aspects fabrics etc. add as attributes / replace its source with its transformed code
        signal("addAspectsAsAttributes", EditorPosition)
        signal("replaceWithTransformedCode", EditorPosition)
        // frontend -> backend: run (debug=false) or debug the Metalama aspect test of a file in Rider's unit test runner
        signal("runAspectTest", structdef("RunAspectTest") {
            field("file", string)
            field("debug", bool)
        })
    }
}
