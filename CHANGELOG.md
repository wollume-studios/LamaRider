# Changelog

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
