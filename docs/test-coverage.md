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
| Worker: aspects as attributes (constructor and named arguments from the aspect object), standalone transformed code (aspect attributes removed, `[ExcludeAspect]`) | `AttributeArgumentsTests.cs`, `WorkerTests.cs` |
| Model: warnings of *Replace with transformed code*, code-fix license note | `ReplaceWithTransformedTests.cs`, `AspectHintsTests.cs` |
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

## Known gaps

- **Windows**: UI tests are routinely run only on Linux (Ubuntu VM); they can run on Windows but open Rider on the desktop. Windows-specific behavior (frontend↔backend paths, the
  Miscellaneous Files project, file locks) is not covered end to end. A *Run / Debug Metalama Aspect Test* failure was
  reported on Windows that the Linux UI test does not reproduce; 0.6.261016.1844 added a fix candidate (prefer the real
  project over Miscellaneous Files) and logging (`LamaRider: run aspect test refused ...`).
- **Fixtures**: only the small `samples/MetalamaSample` (.sln, net8). No fixture is shaped like a real project (.slnx,
  net10, older generated test projects).
- **Features without automated end-to-end tests**: parameter inlay hint clicks, tooltip rendering, the "transformed
  code" hint opening the popup directly, the *Clicking an aspect* setting and popup links, the compiled-copy banner
  action, Alt+Enter variants (only their editor-menu twins are tested), the LamaDebug configuration, settings toggles,
  applying the aspect-test settings, library-aspect navigation (decompiled / SourceLink), auto-build, grouping options.
- **Versions**: tested in Rider 2026.2.3 and 2026.3 EAP6, with Metalama 2026.1.30 only (the Metalama 2027 xUnit v3 test
  project is only unit-tested).
- **Timing**: mirroring breakpoints into a test run's compiled copy relies on polling; one UI test covers it.

Suggested next steps: UI test runs on Windows in a separate Windows VM or Windows Sandbox, a second fixture shaped like
a real project, and UI tests for the features listed above.
