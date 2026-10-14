<p align="center">
  <img src="docs/images/app-icon.png" width="96" alt="LamaRider logo"><br>
  <img src="docs/images/lamarider.png" width="260" alt="LamaRider">
</p>

# LamaRider

Metalama tooling for JetBrains Rider — the Rider counterpart of the Visual Studio extension
"PostSharp / Metalama Tools" (`PostSharpTechnologies.PostSharp`).

| Feature | What you get |
|---|---|
| **LamaRider tool window** | Right side bar (llama icon): aspect classes grouped by project and package; select one to see its documentation and its targets as Project › Namespace › Type › Member with kind icons, transformations and diagnostics. Double-click a transformation for its transformed code. *Go to source* also works for library aspects (Rider opens sources or decompiles). Refreshes on its own when code, projects or packages change. Toolbar: *Debug with LamaDebug*, settings; gear menu: all LamaRider toggles. |
| **Code Vision** | `N aspects` above every declaration an aspect touches (attribute, fabric, inheritance, child aspect; aspects on parameters count for their method); `applied to N targets` above aspect classes. Click for *Show Transformed Code* (popup), *Metalama Diff*, *Show in LamaRider*, *Go to aspect*. |
| **T# highlighting** | Compile-time code in templates is tinted, `meta.*` and compile-time variables stand out. Colors: *Settings › Editor › Color Scheme › Metalama T#*. |
| **Metalama Diff** | Right-click a `.cs` file (editor or Solution Explorer) › *Metalama Diff*: source vs transformed code. `Ctrl+K, 0` in the Visual Studio keymap. |
| **Show Transformed Code** | Editor context menu / Find Action: the transformed code of the declaration at the caret in a popup, with the aspects that changed it. |
| **Show Metalama Aspects** | Editor context menu / Find Action: the Code Vision popup for the declaration at the caret (keyboard access). |
| **Rider setup for aspect tests** | When a solution references `Metalama.Testing.AspectTesting` and xUnit discovery is still *Metadata* (Metalama's docs: aspect tests need *Test Runner* discovery), a notification offers to fix it — team-shared (`.sln.DotSettings`), personal, or global. It also stops the orphaned-processes prompt for diff tools that failing aspect tests open. Also on demand: *Settings › Tools › LamaRider*. Rider 2026.3 already defaults to Test Runner, so usually nothing is needed. |
| **Metalama errors** | An info bar on C# editors when Metalama reports errors (go to error, LAMA documentation, build). Errors without a location, such as LAMA0042 aspect ordering, are underlined on your aspect classes. Alt+Enter in aspect/template code: *Build project to resume Metalama analysis* (LAMA0118); *Settings › Tools › LamaRider* can build on save (off by default). |
| **Debug transformed code** | *Debug with LamaDebug* (LamaRider tool window): selects Metalama's `LamaDebug` configuration, adding it to the `.sln`/`.slnx` first if needed (copied from Debug; projects without Metalama stay Debug), and starts debugging. Step into aspect code or set breakpoints in *Open Transformed File* (`obj/LamaDebug/…/metalama`). Alternatively *Settings › Tools › LamaRider › Debug transformed code in every build configuration* (`Directory.Build.props`). |

PostSharp classic (MSIL) projects are not supported.

| Code Vision | T# highlighting |
|---|---|
| ![Code Vision](docs/screenshots/code-vision.png) | ![T# highlighting](docs/screenshots/tsharp-highlighting.png) |

| Aspect Explorer | Metalama Diff |
|---|---|
| ![Aspect Explorer](docs/screenshots/aspect-explorer.png) | ![Metalama Diff](docs/screenshots/metalama-diff.png) |

| Aspect-test settings | Configure on demand |
|---|---|
| ![Aspect-test settings notification](docs/screenshots/test-settings-notification.png) | ![Configure Rider for Metalama Tests](docs/screenshots/configure-tests.png) |

| Show Metalama Aspects | Debugging transformed code |
|---|---|
| ![Show Metalama Aspects](docs/screenshots/show-aspects-popup.png) | ![Debugging transformed code](docs/screenshots/debug-transformed-code.png) |

## Requirements

- Rider 2026.3 (build 263+, EAP6 or later).
- .NET SDK on `PATH` (8.0 or newer).
- Solutions referencing `Metalama.Framework` 2026.x, restored at least once (`obj/project.assets.json` must exist).
- No Metalama license needed: everything runs on the open-source Metalama framework.

## How it works

The backend (ReSharper plugin) detects the solution's Metalama version and runs **LamaRider.Worker** out of process.
The worker uses Metalama's `Metalama.Framework.Workspaces` introspection API and Metalama's own T# classifier, and
writes `model.json` plus the transformed files to `%LOCALAPPDATA%\LamaRider\out\`. It is built from source on first
use against your Metalama version (cached in `%LOCALAPPDATA%\LamaRider\worker\`), so the first refresh takes a minute.

Data refreshes on solution load, after every build, and via the Refresh button in the LamaRider tool window. Between
refreshes, results reflect the last build. Worker output is logged to `%LOCALAPPDATA%\LamaRider\logs\last-run.log`.

## Build and install

```bash
./gradlew buildPlugin
```

Install `build/distributions/LamaRider-<version>.zip` via *Settings › Plugins › ⚙ › Install Plugin from Disk…*.

### Automatic updates from a local plugin repository

```bash
./gradlew publishLocal
```

Builds the plugin with a fresh version (`<PluginVersion>.<yyMMddHHmm>`), copies it to `~/.lamarider/plugin-repo/`
(override with `-PlocalRepoDir=<dir>`, e.g. a network share) and writes `updatePlugins.xml` there. Once, in Rider:
*Settings › Plugins › ⚙ › Manage Plugin Repositories… › +* and add the printed URL, e.g.
`file:///C:/Users/<you>/.lamarider/plugin-repo/updatePlugins.xml`. Rider then offers every `publishLocal` build as
a plugin update.

### JetBrains Marketplace

`PUBLISH_TOKEN=<token> ./gradlew publishPlugin` uploads to the `eap` channel (`-PPublishChannel=<name>` for another).
The very first upload of a new plugin has to be done by hand on plugins.jetbrains.com.

Try it in a sandboxed Rider with the bundled sample:

```bash
./gradlew runIde -PopenSolution=samples/MetalamaSample/MetalamaSample.sln
```

## Tests

```bash
dotnet test LamaRider.sln
```

```bash
./gradlew unitTest
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
