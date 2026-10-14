# Test coverage

What the automated tests cover, and what is only checked by hand in the `runIde` sandbox.
Run them with `dotnet test LamaRider.sln` (backend model + worker) and `./gradlew unitTest` (frontend logic).

## Covered by unit tests

| Feature | Tests |
|---|---|
| Worker: aspect classes, instances, transformations, diagnostics (incl. their project), library package/summary/doc URL, hidden internal aspects | `tests/LamaRider.Worker.Tests/WorkerTests.cs` |
| Worker: doc-id normalisation, LF offsets, duplicate projects, compile errors don't fail the run | `WorkerTests.cs` |
| Worker: transformed files formatted like Metalama, declaration spans for the popup, T# classification | `WorkerTests.cs` |
| Model: Code Vision lookup (incl. parameter aspects counted on their method), click targets, T# per file | `tests/LamaRider.Model.Tests/AspectModelTests.cs` |
| Model: project-wide errors on own aspect classes, compile-time files (Alt+Enter build), error summary | `AspectModelTests.cs` |
| Auto refresh: which file changes refresh | `RefreshTriggerTests.cs` |
| LamaDebug configuration added to .sln / .slnx | `LamaDebugConfigurationTests.cs` |
| Test-settings status text (settings page) | `TestSettingsStatusTests.cs` |
| Metalama version detection, worker runner, workspace inputs, Directory.Build.props toggle | `MetalamaVersionDetectorTests.cs`, `WorkerRunnerTests.cs`, `WorkspaceInputsTests.cs`, `DebugTransformedCodePropsTests.cs` |
| Frontend: grouping/filter, caret target, error signature + info-bar text, contract transformations, member names, transformed-code lookup and re-indentation | `src/rider/test/kotlin/dev/lamarider/ModelDataTest.kt` |
| Frontend: obj/…/metalama fallback for Open Transformed File / Metalama Diff | `ObjTransformedFilesTest.kt` |

## Checked by hand in the sandbox only

These need a running Rider (ReSharper daemon, rd protocol, Swing UI); the logic they call is tested above.

- ReSharper glue: Code Vision and error daemon stages, T# highlighter, Alt+Enter build action, quick fix, ReSharper
  navigation to library aspects, solution builder, file watcher, settings layers for aspect tests.
- Frontend UI: LamaRider tool window, Code Vision popup, transformed-code popup, error info bar, settings page,
  Debug with LamaDebug (solution configuration switch + Debug), Metalama Diff.

A ReSharper integration test project (`JetBrains.ReSharper.TestFramework`) would cover the first group; not set up yet.
