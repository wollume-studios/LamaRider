# LamaRider — Metalama tooling for JetBrains Rider

Date: 2026-10-14
Status: Approved design, pending spec review

## Goal

Bring the functionality of the Visual Studio extension "PostSharp / Metalama Tools"
(marketplace `PostSharpTechnologies.PostSharp`, v2026.1.8) to JetBrains Rider, for
Metalama-based solutions. Rider already runs Metalama's compile-time transformation and
analyzers; it lacks the IDE tooling.

Success: the plugin installs into **Rider 2026.3 EAP6 (build 263.6800.37)** from a zip and,
on `samples/MetalamaSample.sln`, provides all four features below.

## Scope

In scope (v1):

1. **Aspect Explorer** — tool window browsing aspect classes, their targets and transformations.
2. **Code Vision** — inline hints on declarations affected by aspects.
3. **T# highlighting** — compile-time vs run-time code coloring in templates.
4. **Metalama Diff** — side-by-side source vs transformed code.

Out of scope:

- PostSharp classic (MSIL) support, including the PostSharp Debugging Helper.
- ReSharper-in-Visual-Studio packaging (Rider only).
- Live templates / aspect code actions (Rider already surfaces Metalama's Roslyn code fixes where it can).
- JetBrains Marketplace publishing.

## Assumptions

- Users have a .NET SDK on PATH capable of building the worker and loading their solution.
- Users have a Metalama license valid for introspection (Community license is free).
- Results may lag edits until the next refresh; this is acceptable.

## Architecture

Dual-stack Rider plugin plus an out-of-process worker.

```
LamaRider/
├─ protocol/        rd model (Kotlin DSL) -> generated C# + Kotlin bindings
├─ src/dotnet/      ReSharper backend plugin (C#, JetBrains.Rider.SDK 2026.3 EAP)
├─ src/rider/       IntelliJ frontend plugin (Kotlin, IntelliJ Platform Gradle Plugin 2.x)
├─ src/worker/      LamaRider.Worker: .NET console app on Metalama.Framework.Workspaces
└─ samples/         MetalamaSample.sln — test fixture for every layer
```

Scaffolded from JetBrains' `resharper-rider-plugin` dotnet template, trimmed to Rider only.
Build targets `rider 2026.3-EAP6-SNAPSHOT`, `sinceBuild = 263`. `gradlew buildPlugin` produces
the installable zip, which bundles the backend DLLs and the worker source.

### Why the worker runs out of process

- MSBuild, Roslyn and Metalama assemblies would conflict with those already loaded in the
  ReSharper backend process.
- Metalama's engine must match the solution's Metalama version.

The plugin ships the worker **source**. On first use per Metalama version it runs
`dotnet build -p:MetalamaVersion=<detected>` and caches the output in
`%LOCALAPPDATA%\LamaRider\worker\<pluginVersion>-<metalamaVersion>\` (platform-equivalent
cache dir elsewhere). Keying by plugin version means a cached worker always matches the plugin's schema.

### Data flow

1. Backend reads the `Metalama.Framework/<version>` entry from each project's `obj/project.assets.json`.
2. Refresh triggers: solution load (once), build finished, manual *Refresh* action.
3. Backend runs `LamaRider.Worker <solution.sln> <outDir>`; worker writes `<outDir>/model.json` and
   transformed files under `<outDir>/transformed/` (files, not stdout: MSBuild may log to stdout).
4. Backend parses it into an `AspectModel` cache and maps entries to PSI declarations
   (documentation ID first, file + span fallback).
5. Consumers read `AspectModel`: Code Vision provider, T# daemon stage. The backend publishes the
   `model.json` path over rd; the frontend reads it for the Explorer and Diff.

### Worker JSON (schema 1)

```json
{
  "schema": 1,
  "metalamaVersion": "2026.1.30",
  "aspectClasses": [ { "id": "T:Sample.LogAttribute", "name": "LogAttribute", "project": "Sample",
                       "isLibrary": false, "location": { "file": "...", "start": 120, "end": 480 } } ],
  "instances": [ { "aspectClass": "T:Sample.LogAttribute", "target": "M:Sample.Calc.Add(System.Int32,System.Int32)",
                   "targetName": "Sample.Calc.Add(int, int)", "targetType": "Sample.Calc", "targetKind": "Method",
                   "project": "Sample", "appliedVia": "Attribute|Fabric|Inherited|ChildAspect|RequiredAspect",
                   "location": { "file": "...", "start": 900, "end": 1010 } } ],
  "transformations": [ { "aspectClass": "...", "target": "...", "kind": "OverrideMember|IntroduceMember|...",
                         "description": "Override method Add", "location": { ... } } ],
  "diagnostics": [ { "id": "LAMA0001", "severity": "Error", "message": "...", "target": "...", "file": "...", "line": 3 } ],
  "templateClassifications": [ { "file": "...", "spans": [ { "start": 10, "end": 20, "kind": "TemplateKeyword" } ] } ],
  "transformedFiles": [ { "file": "...", "transformedPath": "<outDir>/transformed/0_Calc.cs" } ]
}
```

Offsets are character offsets into the file text **with line breaks normalized to `\n`**, matching
Rider/IntelliJ documents. `appliedVia` values are Metalama's `AspectPredecessorKind` names.

## Features

### Aspect Explorer (frontend tool window, right anchor)

- **Top pane:** aspect classes with instance count and project/package. Search box.
  *Include library aspects* toggle (default off).
- **Bottom pane:** targets of the selected aspect class, grouped
  Project › Namespace › Type › Member. Children of a target: its transformations
  and diagnostics.
- **Double-click:** target navigates to source (backend navigation via rd);
  transformation opens Metalama Diff at that location.
- **Toolbar:** Refresh, Expand all, Collapse all. Status line, e.g.
  `Refreshed 12:03 · 37 aspects / 214 instances`, or the error with a *Show log* link.

### Code Vision (backend)

- On every declaration with ≥1 aspect instance, including fabric- and inheritance-applied:
  `N aspect(s)`. Tooltip lists aspect class + how applied.
- Click opens a popup: *Preview Transformed Code* (Diff scrolled to the declaration),
  *Show in Aspect Explorer*, *Go to aspect class*.
- On aspect class declarations: `applied to N targets` → opens Explorer filtered to that class.

### T# highlighting (backend daemon stage)

- Applies only to files that contain templates (files listed in `templateClassifications`).
- Categories, mirroring Metalama's `TextSpanClassification`:
  CompileTime (background tint), CompileTimeVariable, TemplateKeyword (`meta`), Dynamic.
- Color settings page *Metalama T#* exposes all four.
- **Classification source:** plan task 1 is a spike to use Metalama's engine classifier
  (`Metalama.Framework.Engine.Formatting`) inside the worker. If it is not usable, the worker
  falls back to a Roslyn heuristic: `meta.*` member accesses, members carrying
  `[CompileTime]` / `[Template]`, and locals assigned from compile-time expressions.

### Metalama Diff (frontend action)

- Entry points: editor context menu, Solution Explorer context menu, Code Vision popup,
  Explorer transformation node.
- Shortcut `Ctrl+K, 0` in the Visual Studio keymap only (`Ctrl+K` is Commit in Rider's default).
- Rider `DiffManager`: left = live editable source document, right = transformed text, read-only,
  C# file type.
- Transformed text source: worker `transformedFiles`; fallback to
  `obj/<Configuration>/metalama/**` emitted when `MetalamaEmitCompilerTransformedFiles=true`.
- No transformations for the file → notification instead of an empty diff.

## Error handling

| Situation | Behavior |
|---|---|
| No Metalama in solution | Plugin idle. No worker runs. Explorer shows "No Metalama projects". |
| .NET SDK missing / worker build fails | Balloon with log path. Diff uses `obj/` fallback; balloon explains enabling `MetalamaEmitCompilerTransformedFiles`. |
| Worker crash, 5-minute timeout, license error | Keep previous model, mark stale. Status line shows Metalama's message verbatim. |
| Refresh requested while worker running | Single-flight; pending triggers coalesce into one follow-up run. |
| Mixed Metalama versions across projects | Use highest version, warn. `ponytail:` ceiling — per-version workers if needed. |
| Multi-targeted projects | Deduplicate declarations across TFMs; first TFM wins. |
| JSON `schema` mismatch | Not reachable: worker cache is keyed by plugin version. Parse error surfaces as a worker failure. |

## Testing

- **Fixture:** `samples/MetalamaSample.sln` — a `[Log]` aspect applied by attribute and by a
  fabric, an aspect introducing a property, and a template using `meta`.
- **Worker:** xUnit test runs the worker on the sample and asserts key facts in the JSON
  (instance counts, fabric-applied target present, transformed text contains introduced
  member, T# spans present).
- **Backend:** ReSharper TestFramework — gold-file highlighting tests for the T# stage;
  `AspectModel` mapping tests fed canned JSON (no worker).
- **Frontend:** manual — `gradlew runIde` against the sample, exercise all four features;
  then install the zip into Rider 2026.3 EAP6.

## Open risks

- **T# classifier access** — engine API is not public/stable. Mitigated by spike + heuristic fallback.
- **Rider SDK EAP churn** — backend APIs (Code Vision provider, daemon) may differ in 263.x;
  verify against the EAP SDK at scaffold time.
- **Workspaces API surface** — exact members for source spans and transformed text to be
  confirmed in the worker task; `obj/` fallback covers Diff.
