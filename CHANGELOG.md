# Changelog

## Unreleased
- Code Vision shows one entry per aspect added by a fabric, inheritance etc. (Rider shows one entry per provider, so there are three: *Metalama aspects*, *Metalama aspects (second aspect)*, *Metalama aspect targets*); more than two still collapse to "N aspects". Parameter inlay hints are one per aspect too.
- Hint tooltips start with where the aspect comes from, naming the fabric (full type name) or the aspect instance it is inherited from, added or required by, then its documentation summary, wrapped to about 70 characters. "N aspects" and "transformed code" list each aspect with its name.
- The "transformed code" hint always opens the transformed code directly.
- Fixed *Run / Debug Metalama Aspect Test* saying "This file is not in a Metalama aspect test project" for a test file: the lookup accepts Windows paths with '/' and prefers the file's real project over Miscellaneous Files.
- Code Vision names aspects added by fabrics, inheritance etc.; aspects added by an attribute are not named (the hint reads "transformed code" and opens the transformed code). More than two collapse to "N aspects". The tooltip lists each aspect's origin and documentation summary.
- Parameters get their own inlay hint for aspects not added by an attribute; the Code Vision menu groups aspects by member, parameter and return value. In the LamaRider window each parameter shows only its own transformations.
- *Clicking an aspect* setting (Settings › Tools › LamaRider › Code Vision): go to its source (default, library aspects via SourceLink or the decompiler), show it in the LamaRider window, or ask.
- The transformed-code popup links its aspects and opens the whole file in Metalama Diff.
- Banner on Metalama's compiled copy of compile-time code (where the debugger stops in aspects): *Open <file> at this line*.
- Documentation: step-by-step usage guide with limitations (docs/usage.md), new screenshots with captions in the README.
- Aspect tests run and debug from the file: the run icon in the gutter, the editor and Solution Explorer menus (*Run / Debug Metalama Aspect Test*). Rider builds and discovers the test first; Test Runner discovery, which aspect tests need, is turned on for the solution if it was off.
- Breakpoints in aspects, templates and fabrics stop while debugging: LamaRider mirrors them into Metalama's compiled copy of that code (no more `Debugger.Break()` / `meta.DebugBreak()` needed).
- Aspect test inputs show their aspects (LamaRider window, Code Vision, Track Caret, transformed code) although Metalama.Testing.AspectTesting turns Metalama off in the test project; Rider no longer greys them out as unused.
- New aspect tests go to `<Aspect>Tests/` with the namespace Rider expects (no redundant using); the test project is built right away so Rider lists the test, and never compiles `.t.cs` snapshots.
- Aspect tests: Alt+Enter on an aspect class (or editor menu *Create Metalama Aspect Test*) adds a prefilled test file to the aspect test project next to the aspect's project, creating that project (xUnit, Metalama.Testing.AspectTesting, added to the solution) when there is none. *Accept Metalama Test Output* saves the last run's output as the test's `.t.cs`.
- Track Caret (Rider's locate icon) fills the targets tree with every aspect affecting the code at the caret; selecting an item shows and selects its aspect. *Show Only Aspects at Caret* filters the aspect list; *Group By*: ungrouped, project/package, namespace.
- Metalama errors located in code (e.g. LAMA0037) are underlined from LamaRider's own analysis seconds after a save; Rider's own Metalama squiggles wait for a build after compile-time changes.
- *Format transformed code* setting: off avoids LAMA0066 and speeds up builds when debugging transformed code.
- UI tests in a real Rider (`./gradlew uiTest`), run on Windows and Linux.
- LamaRider window: *Track Caret* shows only the aspects affecting the code at the caret (member, its parameters, containing types).
- No error balloon any more: located errors are Rider's squiggles, the others the editor info bar.
- Long status and settings texts no longer overflow.
- A failed workspace reload is retried with a clean load.

## 0.6.0
- Faster refresh: the worker stays loaded and reloads the solution (about 3 s instead of 8 s on a small solution); changes are picked up 1 s after saving.
- Refreshes when a Rider build finishes.
- Error info bar only for Metalama errors without a location; located ones keep Rider's squiggles.
- Plugin description shows screenshots (public repository).

## 0.5.1
- Transformed-code popup: highlighted like Metalama Diff; toggle to show the diff with the declaration's source (remembered).

## 0.5.0
- Settings › Tools › LamaRider shows whether Rider is set up for aspect tests, updated live.
- Show Transformed Code from Code Vision and the LamaRider tool window opens next to the declaration.
- Plugin description with feature overview and screenshots.

## 0.4.0
- Tool window renamed to LamaRider (new llama icon); its toolbar and gear menu replace the Tools menu entries; new *Settings › Tools › LamaRider* page.
- *Debug with LamaDebug*: selects Metalama's LamaDebug configuration (adds it to the solution when missing) and starts debugging.
- *Show Transformed Code*: transformed code of one declaration in a popup (Code Vision, editor context menu, LamaRider tool window).
- Code Vision counts aspects on parameters for their method; an aspect on a type no longer shows the type twice in the targets tree.

## 0.3.0
- Refreshes automatically when code, projects or packages change.
- Metalama errors: info bar on C# editors (go to error, documentation, build); errors without a location (e.g. LAMA0042) underlined on your aspect classes, Alt+Enter opens the LAMA documentation.
- Alt+Enter in aspect/template files: *Build project to resume Metalama analysis* (LAMA0118). *Tools › Metalama: Build After Aspect Changes* does it on save (off by default).
- Aspect Explorer: icons and kinds for targets, *Go to source* for library aspects (sources or decompiled), contracts on parameters link to their transformed method.
- Transformed code is formatted like Metalama's own output.

## 0.2.0
- Supports .slnx solutions.
- Show Metalama Aspects, Open Transformed File, Debug Transformed Code.
- Configure Rider for Metalama aspect tests (xUnit Test Runner discovery, diff tools).
- Fix: UI updates from the backend now run on the UI thread.

## 0.1.0
- Aspect Explorer, Code Vision, T# highlighting and Metalama Diff for Metalama solutions.
