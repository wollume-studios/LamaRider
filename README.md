# LamaRider

Metalama tooling for JetBrains Rider — the Rider counterpart of the Visual Studio extension
"PostSharp / Metalama Tools" (`PostSharpTechnologies.PostSharp`).

| Feature | What you get |
|---|---|
| **Aspect Explorer** | Tool window (right): aspect classes in the solution; select one to see its targets as Project › Namespace › Type › Member with their transformations and diagnostics. Double-click navigates; a transformation opens the diff. |
| **Code Vision** | `N aspects` above every declaration an aspect touches (attribute, fabric, inheritance, child aspect); `applied to N targets` above aspect classes. Click for *Preview Transformed Code*, *Show in Aspect Explorer*, *Go to aspect*. |
| **T# highlighting** | Compile-time code in templates is tinted, `meta.*` and compile-time variables stand out. Colors: *Settings › Editor › Color Scheme › Metalama T#*. |
| **Metalama Diff** | Right-click a `.cs` file (editor or Solution Explorer) › *Metalama Diff*: source vs transformed code. `Ctrl+K, 0` in the Visual Studio keymap. |

PostSharp classic (MSIL) projects are not supported.

## Requirements

- Rider 2026.3 (build 263+, EAP6 or later).
- .NET SDK on `PATH` (8.0 or newer).
- Solutions referencing `Metalama.Framework` 2026.x, restored at least once (`obj/project.assets.json` must exist).

## How it works

The backend (ReSharper plugin) detects the solution's Metalama version and runs **LamaRider.Worker** out of process.
The worker uses Metalama's `Metalama.Framework.Workspaces` introspection API and Metalama's own T# classifier, and
writes `model.json` plus the transformed files to `%LOCALAPPDATA%\LamaRider\out\`. It is built from source on first
use against your Metalama version (cached in `%LOCALAPPDATA%\LamaRider\worker\`), so the first refresh takes a minute.

Data refreshes on solution load, after every build, and via the Refresh button in the Aspect Explorer. Between
refreshes, results reflect the last build. Worker output is logged to `%LOCALAPPDATA%\LamaRider\logs\last-run.log`.

## Build and install

```bash
./gradlew buildPlugin
```

Install `build/distributions/LamaRider-<version>.zip` via *Settings › Plugins › ⚙ › Install Plugin from Disk…*.

Try it in a sandboxed Rider with the bundled sample:

```bash
./gradlew runIde -PopenSolution=samples/MetalamaSample/MetalamaSample.sln
```

## Tests

```bash
dotnet test LamaRider.sln
```

## Layout

```
protocol/            rd model shared by backend and frontend
src/dotnet/LamaRider backend: model host, worker runner, Code Vision, T# highlighting
src/rider/           frontend: Aspect Explorer, Code Vision popup, Metalama Diff
src/worker/          LamaRider.Worker (Metalama introspection)
samples/             MetalamaSample fixture used by the tests
tests/               worker and model tests
```
