# Using LamaRider

How to use each feature, and its limitations. Overview with screenshots: [README](../README.md).

- [Before you start](#before-you-start)
- [LamaRider tool window](#lamarider-tool-window)
- [Code Vision and inlay hints](#code-vision-and-inlay-hints)
- [Transformed code](#transformed-code): popup, Metalama Diff, Open Transformed File
- [Aspects into code](#aspects-into-code): add an aspect as attribute, replace a declaration with its transformed code
- [T# highlighting](#t-highlighting)
- [Metalama errors](#metalama-errors)
- [Debugging transformed code](#debugging-transformed-code)
- [Aspect tests](#aspect-tests)
- [Metalama Premium code](#metalama-premium-code): warnings, `#if METALAMA_PREMIUM`, free and Premium builds
- [Settings](#settings)
- [Known limitations](#known-limitations)

## Before you start

- Rider 2026.2 or 2026.3, .NET SDK 8.0 or newer on `PATH`.
- The solution references `Metalama.Framework` 2026.x and has been restored once (`obj/project.assets.json` exists).
  With several Metalama versions in one solution, one of them is used for all projects (a warning is logged).
- Some Metalama components need a Metalama Professional license, see [Licensing](#licensing).

LamaRider analyzes the solution in a separate process, **LamaRider.Worker**, with Metalama's own introspection API. The
worker is built for your Metalama version on first use (cached in `%LOCALAPPDATA%\LamaRider\worker`,
`~/.local/share/LamaRider/worker` on Linux), so the first refresh takes about a minute; later ones take seconds.

A refresh starts:

- when the solution is opened,
- about one second after a `.cs`, `.csproj`, `Directory.*.props` / `Directory.*.targets` file in the solution folder
  changes (in Rider, git or any other tool), or after a NuGet restore,
- when a Rider build finishes,
- with *Refresh* in the LamaRider tool window.

Between refreshes everything shows the last result. Log: `%LOCALAPPDATA%\LamaRider\logs\last-run.log`.

## LamaRider tool window

Open it from the right tool window bar (llama icon), or by clicking an aspect in Code Vision (see
[Settings](#settings)).

![LamaRider tool window](screenshots/lamarider-window.png)

**Aspect list (top).** Every aspect class of the solution and its packages, with its number of targets; classes
without targets are grey.

1. Type in the search field to filter by class name.
2. *Group By* (toolbar): *Project / Package* (default), *Namespace* or *Ungrouped*.
3. Select a class to see its documentation summary, *Go to source* (library aspects: SourceLink, symbol sources or
   decompiled code) and, for Metalama's own aspects, *Online documentation*.

**Targets tree (bottom).** The selected class's targets as Project › Namespace › Type › Member, with how the aspect got
there (*Attribute*, *Fabric*, *Inherited*, ...), its transformations and its diagnostics.

- Double-click a target to go to it.
- Double-click a transformation for the declaration's transformed code (Metalama Diff when there is no popup for it).
- Double-click a diagnostic to go to its line.

**Track Caret** (locate icon). The targets tree shows every aspect affecting the code at the caret: the member, its
parameters and its containing types. Selecting an item selects its aspect in the list. *Show Only Aspects at Caret*
(filter icon) also reduces the aspect list to those aspects.

![Track Caret](screenshots/track-caret.png)

**Toolbar.** Refresh, Track Caret, Show Only Aspects at Caret, Group By, Expand / Collapse All, *Debug with LamaDebug*,
settings. The gear menu has *Debug Transformed Code*, *Build After Aspect Changes* and *Configure Rider for Metalama
Tests*.

**Status line.** `Refreshed 14:02 · 29 aspects / 4 instances` (plus the number of Metalama errors, if any),
`Refreshing (reason)…`, or `No Metalama projects`. After a failed refresh it starts with `Stale ·` and names the log
file; the tooltip has the full text.

Limitations:

- The window shows saved files, not unsaved editor content, and lags a few seconds behind.
- Projects MSBuild cannot restore or load are missing (the reason is in the log).

## Code Vision and inlay hints

With the default settings:

| Code Vision | Meaning |
|---|---|
| `Log` | An aspect not visible in the code (added by a fabric, inherited, added or required by another aspect). At most two are named. Aspects from attributes are not named: you see the attribute. |
| `3 aspects` | More than two such aspects. |
| `transformed code` | On every declaration with aspects (also only on its parameters or return value). Click for the transformed code. |
| `applied to 2 targets` | On an aspect class: its number of targets. |

Aspects that a fabric, inheritance etc. puts on a parameter or a return value get an **inlay hint** each, after the
parameter name (`a NonNegative`) or the return type (`int NonNegative Multiply`).

**Hover** for where the aspect comes from (*Added by fabric MetalamaSample.Fabric*, *Inherited from ...*, *Added by
aspect ...*, *Required by aspect ...*, *Added by an attribute*, *Added from the IDE*) and its documentation summary.
Aspects whose code fixes need a Metalama Professional license say so in the tooltip (see [Licensing](#licensing)).

**Fewer hints.** *Settings › Tools › LamaRider › Code Vision*:

- *Aspects: Their number on the declaration*: one `N aspects` entry per declaration, counting its parameters' and
  return value's aspects too (attribute aspects included), no inlay hints. The tooltip says where each one is.
- *Transformed code: Item in the aspect menu*: no `transformed code` entry; *Show Transformed Code* moves to the
  declaration's aspect menu. A declaration without named aspects then gets a count entry (`1 aspect`) that opens that
  menu.

![Code Vision and inlay hints](screenshots/code-vision.png)

**Click** an entry or inlay hint (or *Show Metalama Aspects* in the editor menu, for the declaration at the caret):

- `transformed code` opens the [transformed code popup](#transformed-code).
- Otherwise a menu offers:
  1. *Show Transformed Code* (only with *Transformed code: Item in the aspect menu*, not for parameter or return value
     hints).
  2. *Metalama Diff (whole file)*.
  3. The aspects, grouped by member, each parameter and the return value, with their origin (`Log (fabric)`).
  4. On an aspect class: *Show targets in LamaRider*.

**Clicking an aspect** (in that menu or the transformed code popup) follows *Settings › Tools › LamaRider › Code Vision
› Clicking an aspect*: *Go to the aspect's source* (default), *Show it in the LamaRider window*, or *Ask*.

![Code Vision popup](screenshots/code-vision-popup.png)

Limitations:

- Rider shows one Code Vision entry per provider and declaration, so at most two aspects are named; more collapse into
  `N aspects`. The providers can be turned off in *Settings › Editor › Inlay Hints › Code Vision*.
- A newly added `[Log]` shows up after the save has been analyzed.

## Transformed code

**Transformed code popup.** A click on `transformed code`, *Show Transformed Code* (aspect menu, editor menu or Find
Action), or a double-click on a transformation in the LamaRider window. Shows the declaration as Metalama compiles it,
highlighted. Toolbar: a toggle to diff it with the source (not for declarations an aspect introduced) and *Whole File in
Metalama Diff*. The bottom row links the aspects that changed it. The popup can be moved and resized.

![Transformed code popup](screenshots/transformed-code-popup.png)

**Metalama Diff.** Right-click a `.cs` file in the editor or Solution Explorer › *Metalama Diff* (Visual Studio keymap:
`Ctrl+K, 0`): source on the left, the whole transformed file on the right (read-only), scrolled to the caret.

![Metalama Diff](screenshots/metalama-diff.png)

**Open Transformed File** (editor menu) opens the file Metalama wrote to `obj/` while
[debugging transformed code](#debugging-transformed-code) is on: the file the debugger uses.

Limitations:

- The transformed code is from the last refresh. Its layout can differ from what a LamaDebug build writes to `obj/`.
- For a file without a refresh result, Metalama Diff uses the last build's transformed files in `obj/`, if any.

## Aspects into code

Two Alt+Enter actions on the **name** of a declaration. Each also has an editor menu entry (and Find Action) for the
declaration at the caret.

![Editor menu with the aspect actions](screenshots/aspect-actions-menu.png)

### Add an aspect as attribute

For aspects not visible in the code: added by a fabric, inherited, or added or required by another aspect.

1. Put the caret on the name of the type, member or parameter.
2. Alt+Enter › *Add [Log] as attribute (added by fabric MetalamaSample.Fabric)*, one entry per aspect. Or editor menu ›
   *Add Metalama Aspects as Attributes* for all of them.
3. The attribute gets the aspect instance's constructor arguments and the properties that differ from their default.

![Aspect added as attribute](screenshots/add-as-attribute.png)

Limitations:

- The fabric (inheritance, other aspect) still adds the aspect; Metalama runs the attribute's instance. Remove it from
  the fabric yourself to keep only the attribute.
- Not offered when the aspect class is not a public attribute, or a value cannot be written in C# (only constants,
  `null`, enum values, `typeof(...)` and one-dimensional arrays of them can).
- Constructor arguments are read from the properties or fields named like the constructor's parameters (also `_name`
  fields and primary constructor parameters). When no constructor fits, the action is not offered.

### Replace with transformed code

Like `metalama divorce`, for one declaration.

1. Save the file and put the caret on the name of a type or member that aspects transform.
2. Alt+Enter › *Replace with transformed code (remove its aspects)*, or editor menu › *Replace with Metalama
   Transformed Code*.
3. The declaration becomes its transformed code. Aspect attributes are removed (`[Log, Obsolete]` keeps `[Obsolete]`);
   aspects that a fabric, inheritance or another aspect would add again get `[ExcludeAspect(typeof(...))]`.
4. A notification warns when something is not carried over:
   - *&lt;Aspect&gt; on the containing declaration still transforms it*: replace the containing declaration instead, or
     undo.
   - *&lt;Aspect&gt; also changed code outside it (e.g. introduced members)*: that code goes away with the aspect.
5. Ctrl+Z undoes it.

![Replaced with transformed code](screenshots/replace-with-transformed-code.png)

Limitations:

- Only offered when the last refresh matches the file: after editing, save and wait a few seconds.
- Not offered on a parameter: use its member's name.
- The code is Metalama's formatted output, not your layout.

## T# highlighting

In aspect and template code, compile-time code is tinted, and `meta.*` calls and compile-time variables stand out.
Colors: *Settings › Editor › Color Scheme › Metalama T#*.

![T# highlighting](screenshots/tsharp-highlighting.png)

![T# colors](screenshots/tsharp-color-settings.png)

It appears after the first refresh and follows the file as of the last one.

## Metalama errors

- **Squiggles.** Errors with a location, e.g. LAMA0037 (aspect not eligible) or a compile error in a template, are
  underlined a few seconds after you save, also while Rider's own Metalama analysis is paused (LAMA0118).
- **Error info bar.** Errors without a location (e.g. an exception in a fabric, LAMA0041, or LAMA0042 in aspect
  ordering) show in an info bar on every C# editor, with *LAMAxxxx documentation* and *Build solution*. The tooltip
  lists all of them; they are also underlined on your aspect classes in that project.
- **LAMA documentation.** Alt+Enter on a LAMA error › *Open LAMAxxxx documentation*.
- **Build to resume (LAMA0118).** Metalama pauses its analysis in Rider after compile-time code changes until the
  project is built. Alt+Enter in an aspect or template file › *Build '&lt;project&gt;' to resume Metalama analysis*
  (offered when the project's compile-time files changed after its last build). Or
  turn on *Settings › Tools › LamaRider › Build the project after aspect or template code is saved*.

![Metalama error info bar](screenshots/error-banner.png)

Limitations:

- Errors without a location in aspect test inputs are not shown (a test may expect them).
- *Build to resume* is offered in every aspect and template file, whether or not Metalama has paused. Fabric files
  without templates don't get it.

## Debugging transformed code

To step through the code aspects generate, Metalama builds with `MetalamaDebugTransformedCode`. Two ways:

**Debug with LamaDebug** (LamaRider tool window toolbar or gear menu):

1. Click it. If the solution has no `LamaDebug` configuration, LamaRider offers to add one to the `.sln` / `.slnx`
   (copied from Debug). Wait for Rider to reload the solution, then click it again.
2. It selects LamaDebug and debugs the selected run configuration.
3. Step into a method an aspect changed, or set breakpoints in its transformed file (*Open Transformed File*,
   `obj/LamaDebug/<tfm>/metalama/`).

**Every configuration:** *Settings › Tools › LamaRider › Debug transformed code in every build configuration* writes
`MetalamaDebugTransformedCode` to the solution's `Directory.Build.props`. Rebuild, then debug as usual.

![Debugging transformed code](screenshots/debug-transformed-code.png)

**Format transformed code** (on by default): Metalama formats the files it writes for debugging, which is slower and
gives warning LAMA0066. Off (`MetalamaFormatOutput=false`): faster builds, no LAMA0066, less readable files in the
debugger.

Limitations:

- Only the solution-level `Directory.Build.props` is changed. Projects with their own `Directory.Build.props` don't see
  the setting unless it imports the parent one.
- Breakpoints in source files don't stop in transformed code and vice versa: set them in the transformed file.
- To debug the aspects themselves, see [Debug an aspect test](#debug-an-aspect-test).

## Aspect tests

Metalama aspect tests (`Metalama.Testing.AspectTesting`) are snapshot tests: each `.cs` file in an aspect test project
is a test, compared with the `.t.cs` file next to it.

### Create a test

1. Alt+Enter on the aspect class name › *Create Metalama aspect test* (or editor menu › *Create Metalama Aspect Test*).
2. Pick an aspect test project that references the aspect's project, or *Create aspect test project
   '&lt;Project&gt;.AspectTests'* (xUnit, your Metalama version, same target framework, next to the aspect's project).
3. LamaRider writes `<Aspect>Tests/<Aspect>Test.cs` with a target that already uses the aspect (a comment pointing to
   fabrics for aspects that are not attributes), opens it and builds the project so Rider lists the test.

![New aspect test](screenshots/create-aspect-test.png)

### Run and accept

1. Run the test: gutter icon, *Run Metalama Aspect Test* in the editor or Solution Explorer menu, Rider's Unit Tests
   window, or `dotnet test`.
2. The first run fails: there is no snapshot yet.
3. When the output differs from the `.t.cs`, the test file and its `.t.cs` get an info bar (*Test output differs from
   LogTest.t.cs* or *LogTest.t.cs is empty*):
   - *Compare*: Rider's diff, last run's output (read-only) left, the `.t.cs` right. Take single changes, edit the
     snapshot, or *Accept All* in the toolbar. Also *Compare Metalama Test Output* in the editor and Solution Explorer
     menus.
   - *Accept all*: the output becomes the `.t.cs`.
4. Or Alt+Enter › *Accept Metalama test output* (also in the editor menu).
5. Run again: it passes.

Accepting can be undone with Ctrl+Z. Differences only in line endings or trailing whitespace are ignored.

![Test output info bar](screenshots/aspect-test-banner.png)

![Test output diff](screenshots/aspect-test-diff.png)

Test inputs show their aspects like any other code (LamaRider window, Code Vision, transformed code).

![Aspect test file](screenshots/aspect-test.png)

### Debug an aspect test

*Debug Metalama Aspect Test* (gutter icon, editor and Solution Explorer menus): **breakpoints in your aspects,
templates and fabrics stop**, without `Debugger.Break()` or `meta.DebugBreak()`.

The debugger stops in Metalama's compiled copy of your compile-time code, not in your file. The copy opens with an info
bar:

- *Show LogAttribute.cs side by side*: your file in a split on the right. Moving the caret in either one moves the
  other (in every open editor of your file).
- *Open LogAttribute.cs at this line*.
- *Make it readable*: turns on *Readable aspect code while debugging* and asks: *Debug Again* stops debugging,
  rebuilds the aspect test project with its dependencies and debugs the same aspect test again; *Later* leaves it.
  *Debug Again* is offered when you started the aspect test from LamaRider (gutter icon or *Run / Debug Metalama Aspect
  Test*); otherwise the dialog tells you to rebuild and debug again.

![Debugging an aspect test](screenshots/debug-aspect-test.png)

**Readable aspect code while debugging** (*Settings › Tools › LamaRider › Debugging*, off by default): Metalama formats
its compiled copy (no `global::`, normal indentation). Slower builds of aspect projects. Rebuild the aspect project
after changing it.

Limitations:

- The info bar updates when Rider notices the new output: right away for runs in Rider, when Rider gets the focus back
  after a run in a terminal. With several target frameworks, the newest output is compared.
- Breakpoints added or changed during a session take effect in the next session. Breakpoints on lines without a
  statement don't stop.
- If a breakpoint doesn't stop (for example after a Metalama update changed its compiled-copy format), debugging works
  as without LamaRider.
- Rider shows unresolved-symbol errors in the compiled copy, and its debugger hints may log an internal error there;
  both are harmless.
- Aspect test projects are analyzed a second time with Metalama on, which adds to the refresh time.

### Rider setup for aspect tests

Aspect tests need xUnit discovery by *Test Runner* (discovery from *Metadata* finds none) and no prompt about orphaned
processes.

- When a solution uses `Metalama.Testing.AspectTesting` without them, a notification offers *Apply to solution
  (team-shared)*, *Apply for me only* or *Apply globally*.
- *Run / Debug Metalama Aspect Test* turns Test Runner discovery on for you (`.sln.DotSettings.user`).
- *Settings › Tools › LamaRider › Aspect Tests* shows whether they are in place and has *Configure Rider for Metalama
  Tests…*.

## Metalama Premium code

Uses of Metalama's Premium components (see [Licensing](#licensing)) get the warning **Metalama Premium API**: types,
members and extension methods such as `builder.Diagnostics.Suggest`, `CodeFixFactory`, validators and architecture
attributes, and their using directives. Code inside `#if METALAMA_PREMIUM` is not flagged. The severity is set in
*Settings › Editor › Inspection Settings › Inspection Severity*.

![Metalama Premium API warning](screenshots/premium-api.png)

To keep a free build that compiles without a license, Alt+Enter on a warning:

- *Put it in #if METALAMA_PREMIUM*: wraps the statement, member, attribute or using directive of the use, and the
  file's Premium using directives. The code must be on lines of its own.
- *Set up free and Premium builds of this project*: adds the property `MetalamaPremium` (default `false`) to the
  `.csproj`. `MetalamaPremium=true` (`dotnet build -p:MetalamaPremium=true`, or the `MetalamaPremium` environment
  variable) defines `METALAMA_PREMIUM` and references the Premium packages; their `PackageReference`s get a
  `Condition`. A Premium reference outside the project file (`Directory.Build.props`, another project) must be made
  conditional there.

Both are also in *Find Action*: *Put Metalama Premium Code in #if METALAMA_PREMIUM*, *Set Up Free and Metalama Premium
Builds*.

Limitations:

- Rider analyses the free build unless `MetalamaPremium` is set (e.g. as environment variable before starting Rider):
  Premium code is greyed out as inactive.
- A NuGet package is either free or Premium: publish two packages, or build the package with Premium.
- For library aspects, Code Vision tooltips mark the use of `Metalama.Extensions.CodeFixes` only, not of the other
  Premium components.

## Settings

*Settings › Tools › LamaRider* (also the settings icon in the LamaRider tool window):

| Setting | Default | Effect |
|---|---|---|
| Code Vision › Aspects | Each aspect by name | By name, with inlay hints for parameters and return values, or *Their number on the declaration*. See [Code Vision](#code-vision-and-inlay-hints). |
| Code Vision › Transformed code | Hint of its own | A `transformed code` entry, or *Item in the aspect menu*. |
| Code Vision › Clicking an aspect | Go to the aspect's source | Go to source, show it in the LamaRider window, or ask. |
| Build the project after aspect or template code is saved | off | Resumes Metalama's analysis after compile-time changes (LAMA0118). |
| Debug transformed code in every build configuration | off | `MetalamaDebugTransformedCode` in the solution's `Directory.Build.props`. |
| Format transformed code | on | Off: `MetalamaFormatOutput=false` (faster builds, no LAMA0066). |
| Readable aspect code while debugging | off | `MetalamaFormatCompileTimeCode=true`: readable compiled copy, slower aspect project builds. See [Debug an aspect test](#debug-an-aspect-test). |
| Aspect Tests | | Whether Rider is set up for aspect tests, and *Configure Rider for Metalama Tests…*. |

The Code Vision and build settings apply to all solutions.

![LamaRider settings](screenshots/settings.png)

## Known limitations

- Rider 2026.2 and 2026.3, Metalama 2026.x only. PostSharp classic (MSIL) projects are not supported.
- The first refresh takes about a minute, later ones seconds. Projects must be restorable and loadable by MSBuild.
- With several Metalama versions in one solution, one version is used for all projects.
- File paths are compared case-insensitively, also on Linux.

### Licensing

Metalama's Premium components need a Metalama Professional license: `Metalama.Extensions.CodeFixes` (code fixes that
aspects offer), architecture validation (`Metalama.Extensions.Validation`, `Metalama.Extensions.Architecture`), and the
Azure and Redis caching back-ends. Without a license, a project using them fails to build with LAMA0806. Code Vision
tooltips mark aspects whose code fixes need it; [Metalama Premium code](#metalama-premium-code) flags and guards
your own uses.
