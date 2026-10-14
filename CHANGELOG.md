# Changelog

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
