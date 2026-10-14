# LamaRider Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rider 2026.3 EAP6 plugin giving Metalama solutions Aspect Explorer, Code Vision, T# highlighting and Metalama Diff.

**Architecture:** ReSharper backend plugin (C#, net472) + IntelliJ frontend plugin (Kotlin) talking over an rd ext model. Aspect data comes from an out-of-process worker (`LamaRider.Worker`, net8.0, `Metalama.Framework.Workspaces`) that the backend builds on demand against the solution's Metalama version and runs after builds; it writes `model.json` + transformed files to a cache dir.

**Tech Stack:** JetBrains.Rider.SDK 2026.3.0-eap06, IntelliJ Platform Gradle Plugin 2.10.5, rd-gen 2025.3.1, Kotlin 2.1.20, Metalama 2026.1.30, xUnit, Newtonsoft.Json (backend), System.Text.Json (worker).

**Spec:** `docs/superpowers/specs/2026-10-14-lamarider-design.md`

## Global Constraints

- Rider target: `ProductVersion=2026.3-EAP6-SNAPSHOT`, `SdkVersion=2026.3.0-eap06`, `since-build="263"`, no `until-build`.
- Plugin ids: frontend `dev.lamarider`, backend assembly `LamaRider`, plugin name `LamaRider`.
- Rider only — no ReSharper/Visual Studio package.
- Worker JSON `schema = 1`; all offsets are **LF-normalized character offsets** (IntelliJ/Rider documents use `\n` only).
- Worker cache: `%LOCALAPPDATA%\LamaRider` (`Environment.SpecialFolder.LocalApplicationData`), worker binaries keyed by `<pluginVersion>-<metalamaVersion>`.
- Worker timeout 5 minutes. One worker run at a time; triggers during a run coalesce into one follow-up run.
- Mixed Metalama versions: use highest, log a warning.
- `Ctrl+K, 0` only in the `Visual Studio` keymap.
- PostSharp classic: out of scope.

## Review Focus

1. **CRLF files** — offsets from disk text must still land on the right characters in Rider documents. Pinned in Task 3 (`Offsets_are_lf_normalized`).
2. **Aspect on a class must not show on its members** (span fallback picking the containing declaration). Pinned in Task 4 (`Span_fallback_does_not_leak_container_aspects`).
3. **Multi-targeted projects** — same instance reported once per TFM must appear once. Pinned in Task 4 (`Duplicate_instances_are_collapsed`).
4. **Solution without Metalama / restore not yet run** — version detection returns null, plugin stays idle. Pinned in Task 4 (`Version_detection_*`).
5. **Worker failure** (bad exit code, missing model.json) keeps the previous model and surfaces stderr. Pinned in Task 5 (`Failed_run_reports_stderr`).

---

## File Structure

```
.gitignore
build.gradle.kts, settings.gradle.kts, gradle.properties, gradle/libs.versions.toml,
gradle/wrapper/*, gradlew, gradlew.bat, dependencies.json, CHANGELOG.md, README.md
LamaRider.sln                                   all .NET projects
protocol/build.gradle.kts
protocol/src/main/kotlin/model/rider/LamaRiderModel.kt     rd model
samples/MetalamaSample/                         fixture solution (Metalama aspects)
src/worker/LamaRider.Worker/                    worker exe
  LamaRider.Worker.csproj, Program.cs, WorkerModel.cs, Offsets.cs, Extractor.cs, TemplateClassifier.cs
tests/LamaRider.Worker.Tests/                   worker integration tests (runs on sample)
src/dotnet/Directory.Build.props, src/dotnet/Plugin.props
src/dotnet/LamaRider/
  LamaRider.Rider.csproj, ILamaRiderZone.cs
  Model/AspectModel.cs                          pure: JSON model + lookups
  Model/MetalamaVersionDetector.cs              pure: version from project.assets.json
  Worker/WorkerRunner.cs                        build + run worker process
  AspectModelHost.cs                            solution component: triggers, rd, daemon invalidation
  CodeVision/AspectCodeInsightsProvider.cs, CodeVision/AspectCodeVisionStage.cs
  TSharp/TSharpHighlighting.cs, TSharp/TSharpHighlightingStage.cs
tests/LamaRider.Model.Tests/                    unit tests for Model/*.cs + WorkerRunner
src/rider/main/resources/META-INF/plugin.xml
src/rider/main/kotlin/dev/lamarider/
  ModelData.kt            Gson data classes + loader
  LamaRiderService.kt     project service: holds model, rd wiring
  LamaRiderProtocolListener.kt
  explorer/AspectExplorerPanel.kt, explorer/AspectExplorerToolWindowFactory.kt
  AspectPopup.kt          Code Vision click popup
  diff/MetalamaDiff.kt, diff/MetalamaDiffAction.kt
  RefreshAction.kt
```

---

### Task 1: Scaffold from template, Rider-only, building against EAP6

**Files:**
- Create: everything under "build" in File Structure, `src/dotnet/LamaRider/LamaRider.Rider.csproj`, `src/dotnet/LamaRider/ILamaRiderZone.cs`, `src/rider/main/resources/META-INF/plugin.xml`, `LamaRider.sln`, `.gitignore`

**Interfaces:**
- Produces: `gradlew buildPlugin` → `build/distributions/LamaRider-<ver>.zip` with `LamaRider/dotnet/LamaRider.dll` and `LamaRider/worker/*`.

- [ ] **Step 1: Copy template content** from the cloned `JetBrains/resharper-rider-plugin` `content/` into repo root, then rename: `SamplePlugin`→`LamaRider`, `ReSharperPlugin.LamaRider`→`LamaRider`. Delete: `src/dotnet/*/ReSharperPlugin.SamplePlugin.csproj` (ReSharper-only), `runVisualStudio.ps1`, `tools/vswhere.exe`, `tools/nuget.exe`, `.idea/**/VisualStudio.xml`, `publishPlugin.ps1`, template Tests project.

- [ ] **Step 2: gradle.properties**

```properties
DotnetPluginId=LamaRider
DotnetSolution=LamaRider.sln
RiderPluginId=dev.lamarider
PluginVersion=0.1.0
BuildConfiguration=Debug
PublishToken="_PLACEHOLDER_"
ProductVersion=2026.3-EAP6-SNAPSHOT
kotlin.stdlib.default.dependency=false
org.jetbrains.intellij.platform.buildFeature.useBinaryReleases=false
```

- [ ] **Step 3: Plugin.props** — `SdkVersion` = `2026.3.0-eap06`, Title `LamaRider`, Description `Metalama tooling for Rider`.

- [ ] **Step 4: LamaRider.Rider.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <AssemblyName>LamaRider</AssemblyName>
    <RootNamespace>LamaRider</RootNamespace>
    <IsPackable>false</IsPackable>
    <DefineConstants>$(DefineConstants);RIDER</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="JetBrains.Rider.SDK" Version="$(SdkVersion)" />
  </ItemGroup>
</Project>
```

`ILamaRiderZone.cs`:

```csharp
using JetBrains.Application.BuildScript.Application.Zones;

namespace LamaRider;

[ZoneDefinition]
public interface ILamaRiderZone : IZone;
```

- [ ] **Step 5: build.gradle.kts changes** vs template:
  - `setBuildTool`: always `dotnet msbuild` (drop vswhere branch).
  - `prepareSandbox`: also copy worker source: `from("src/worker/LamaRider.Worker") { into("${rootProject.name}/worker"); exclude("bin/**", "obj/**") }`.
  - Remove `.nupkg` pack step from `buildPlugin.doLast` and the `publishPlugin` block.
  - `tasks.compileKotlin { dependsOn(":protocol:rdgen") }` and `compileDotNet.dependsOn(":protocol:rdgen")`.
  - `dependencies.intellijPlatform`: `rider(ProductVersion) { useInstaller = false }` (unchanged).
  - `patchPluginXml { sinceBuild.set("263"); untilBuild.set(provider { null }) }`.

- [ ] **Step 6: protocol/build.gradle.kts** — keep template, change outputs:
  `csOutput = File(rootDir, "src/dotnet/LamaRider/Rd")`, `ktOutput = File(rootDir, "src/rider/main/kotlin/dev/lamarider/rd")`.

- [ ] **Step 7: plugin.xml (minimal; extended in later tasks)**

```xml
<idea-plugin require-restart="true">
  <id>dev.lamarider</id>
  <name>LamaRider</name>
  <version>_PLACEHOLDER_</version>
  <vendor>LamaRider</vendor>
  <depends>com.intellij.modules.rider</depends>
  <description><![CDATA[<p>Metalama tooling for Rider: Aspect Explorer, Code Vision, T# highlighting, Metalama Diff.</p>]]></description>
</idea-plugin>
```

- [ ] **Step 8: Placeholder rd model** so rdgen runs (`protocol/src/main/kotlin/model/rider/LamaRiderModel.kt`, full model in Task 6):

```kotlin
package model.rider

import com.jetbrains.rd.generator.nova.*
import com.jetbrains.rd.generator.nova.PredefinedType.*
import com.jetbrains.rider.model.nova.ide.SolutionModel

@Suppress("unused")
object LamaRiderModel : Ext(SolutionModel.Solution) {
    init {
        property("status", string.nullable)
    }
}
```

- [ ] **Step 9: .gitignore** — template's plus `src/dotnet/LamaRider/Rd/`, `src/rider/main/kotlin/dev/lamarider/rd/`, `output/`, `samples/**/bin/`, `samples/**/obj/`, `tests/**/bin/`, `tests/**/obj/`, `src/worker/**/bin/`, `src/worker/**/obj/`.

- [ ] **Step 10: Build**

Run: `./gradlew buildPlugin`
Expected: BUILD SUCCESSFUL; `build/distributions/LamaRider-0.1.0.zip` exists and `unzip -l` lists `LamaRider/dotnet/LamaRider.dll`.

- [ ] **Step 11: Commit** `chore: scaffold Rider plugin from template targeting 2026.3 EAP6`

---

### Task 2: Sample Metalama solution (fixture)

**Files:**
- Create: `samples/MetalamaSample/MetalamaSample.sln`, `samples/MetalamaSample/MetalamaSample/MetalamaSample.csproj`, `LogAttribute.cs`, `IdAttribute.cs`, `Calculator.cs`, `Fabric.cs`, `Program.cs`

**Interfaces:**
- Produces: targets used by tests — `M:MetalamaSample.Calculator.Add(System.Int32,System.Int32)` (Log via attribute), `M:MetalamaSample.Calculator.Multiply(System.Int32,System.Int32)` (Log via fabric), `T:MetalamaSample.Calculator` (Id aspect introducing `Id`), template file `LogAttribute.cs`. `Calculator.cs` is saved with **CRLF** line endings.

- [ ] **Step 1: Write files**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RollForward>Major</RollForward>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Metalama.Framework" Version="2026.1.30" />
  </ItemGroup>
</Project>
```

```csharp
// LogAttribute.cs
using Metalama.Framework.Aspects;

namespace MetalamaSample;

public class LogAttribute : OverrideMethodAspect
{
    public override dynamic? OverrideMethod()
    {
        var name = meta.Target.Method.Name;
        Console.WriteLine($"Entering {name}");
        try
        {
            return meta.Proceed();
        }
        finally
        {
            Console.WriteLine($"Leaving {name}");
        }
    }
}
```

```csharp
// IdAttribute.cs
using Metalama.Framework.Aspects;

namespace MetalamaSample;

public class IdAttribute : TypeAspect
{
    [Introduce]
    public Guid Id { get; } = Guid.NewGuid();
}
```

```csharp
// Calculator.cs  (CRLF line endings)
namespace MetalamaSample;

[Id]
public class Calculator
{
    [Log]
    public int Add(int a, int b) => a + b;

    public int Multiply(int a, int b) => a * b;
}
```

```csharp
// Fabric.cs
using Metalama.Framework.Fabrics;

namespace MetalamaSample;

public class Fabric : ProjectFabric
{
    public override void AmendProject(IProjectAmender amender) =>
        amender.SelectMany(c => c.Types)
            .Where(t => t.Name == nameof(Calculator))
            .SelectMany(t => t.Methods)
            .Where(m => m.Name == nameof(Calculator.Multiply))
            .AddAspect<LogAttribute>();
}
```

```csharp
// Program.cs
Console.WriteLine(new MetalamaSample.Calculator().Multiply(2, 3));
```

- [ ] **Step 2: Force CRLF** for `Calculator.cs`: `unix2dos samples/MetalamaSample/MetalamaSample/Calculator.cs` (or rewrite with `\r\n`), and add `samples/MetalamaSample/MetalamaSample/Calculator.cs -text` to `.gitattributes` so git keeps CRLF.

- [ ] **Step 3: Build and run**

Run: `dotnet run --project samples/MetalamaSample/MetalamaSample`
Expected output contains `Entering Multiply`, `Leaving Multiply`, `6`.

- [ ] **Step 4: Commit** `test: add Metalama sample solution fixture`

---

### Task 3: Worker — extract aspects, transformations, transformed files, T# spans

**Files:**
- Create: `src/worker/LamaRider.Worker/LamaRider.Worker.csproj`, `WorkerModel.cs`, `Offsets.cs`, `Extractor.cs`, `TemplateClassifier.cs`, `Program.cs`
- Test: `tests/LamaRider.Worker.Tests/LamaRider.Worker.Tests.csproj`, `WorkerTests.cs`
- Modify: `LamaRider.sln` (add both)

**Interfaces:**
- Produces CLI: `dotnet LamaRider.Worker.dll <solution-or-project> <outDir>` → exit 0, writes `<outDir>/model.json` (schema 1, camelCase) and `<outDir>/transformed/<n>_<FileName>.cs`. Exit 1 + exception on stderr on failure; exit 2 on bad args.
- JSON shape (camelCase of these records):

```csharp
WorkerOutput(int Schema, string? MetalamaVersion, List<AspectClassInfo> AspectClasses, List<AspectInstanceInfo> Instances,
             List<TransformationInfo> Transformations, List<DiagnosticInfo> Diagnostics,
             List<FileClassification> TemplateClassifications, List<TransformedFileInfo> TransformedFiles)
SourceLocation(string File, int Start, int End)
AspectClassInfo(string Id, string Name, string Project, bool IsLibrary, SourceLocation? Location)
AspectInstanceInfo(string AspectClass, string Target, string TargetName, string TargetType, string TargetKind, string Project, string AppliedVia, SourceLocation? Location)
TransformationInfo(string AspectClass, string Target, string Kind, string Description, SourceLocation? Location)
DiagnosticInfo(string Id, string Severity, string Message, string? Target, string? File, int? Line)
ClassifiedSpan(int Start, int End, string Kind)          // Kind ∈ CompileTime, CompileTimeVariable, TemplateKeyword, Dynamic
FileClassification(string File, List<ClassifiedSpan> Spans)
TransformedFileInfo(string File, string TransformedPath)
```

- [ ] **Step 1: csproj files**

```xml
<!-- src/worker/LamaRider.Worker/LamaRider.Worker.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RollForward>Major</RollForward>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <MetalamaVersion Condition="'$(MetalamaVersion)' == ''">2026.1.30</MetalamaVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Metalama.Framework.Workspaces" Version="$(MetalamaVersion)" />
  </ItemGroup>
</Project>
```

```xml
<!-- tests/LamaRider.Worker.Tests/LamaRider.Worker.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RollForward>Major</RollForward>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\worker\LamaRider.Worker\LamaRider.Worker.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the failing test**

```csharp
// tests/LamaRider.Worker.Tests/WorkerTests.cs
using LamaRider.Worker;
using Metalama.Framework.Workspaces;

namespace LamaRider.Worker.Tests;

public sealed class WorkerTests : IAsyncLifetime
{
    const string Add = "M:MetalamaSample.Calculator.Add(System.Int32,System.Int32)";
    const string Multiply = "M:MetalamaSample.Calculator.Multiply(System.Int32,System.Int32)";
    static readonly string SampleDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples/MetalamaSample"));
    readonly string _outDir = Path.Combine(Path.GetTempPath(), "lamarider-tests", Guid.NewGuid().ToString("N"));
    WorkerOutput _output = null!;

    public async Task InitializeAsync()
    {
        using var workspace = await WorkspaceCollection.Default.LoadAsync(Path.Combine(SampleDir, "MetalamaSample.sln"));
        _output = Extractor.Extract(workspace, _outDir);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Reports_attribute_and_fabric_instances_once()
    {
        var log = _output.Instances.Where(i => i.AspectClass == "T:MetalamaSample.LogAttribute").ToList();
        Assert.Equal(2, log.Count);
        Assert.Equal("Attribute", log.Single(i => i.Target == Add).AppliedVia);
        Assert.Equal("Fabric", log.Single(i => i.Target == Multiply).AppliedVia);
        Assert.Equal("MetalamaSample.Calculator", log[0].TargetType);
    }

    [Fact]
    public void Aspect_class_is_located_in_source()
    {
        var log = _output.AspectClasses.Single(c => c.Id == "T:MetalamaSample.LogAttribute");
        Assert.False(log.IsLibrary);
        Assert.EndsWith("LogAttribute.cs", log.Location!.File);
    }

    [Fact]
    public void Offsets_are_lf_normalized()
    {
        var add = _output.Instances.Single(i => i.Target == Add);
        var lf = File.ReadAllText(add.Location!.File).Replace("\r\n", "\n");
        Assert.Contains("\r\n", File.ReadAllText(add.Location.File)); // fixture really is CRLF
        var text = lf.Substring(add.Location.Start, add.Location.End - add.Location.Start);
        Assert.StartsWith("[Log]", text);
        Assert.EndsWith("a + b;", text);
    }

    [Fact]
    public void Writes_transformed_file_with_introduced_member()
    {
        var calc = _output.TransformedFiles.Single(f => f.File.EndsWith("Calculator.cs"));
        var transformed = File.ReadAllText(calc.TransformedPath);
        Assert.Contains("Guid Id", transformed);
        Assert.Contains("Entering", transformed);
    }

    [Fact]
    public void Lists_transformations_for_targets()
    {
        Assert.Contains(_output.Transformations, t => t.Target == Add);
        Assert.Contains(_output.Transformations, t => t.Target == "T:MetalamaSample.Calculator");
    }

    [Fact]
    public void Classifies_template_code()
    {
        var file = _output.TemplateClassifications.Single(f => f.File.EndsWith("LogAttribute.cs"));
        var lf = File.ReadAllText(file.File).Replace("\r\n", "\n");
        Assert.Contains(file.Spans, s => s.Kind == "TemplateKeyword" && lf.Substring(s.Start, s.End - s.Start) == "meta");
        Assert.Contains(file.Spans, s => s.Kind == "CompileTimeVariable");
    }

    [Fact]
    public void Main_writes_model_json()
    {
        var dir = Path.Combine(_outDir, "cli");
        var code = Program.Run([Path.Combine(SampleDir, "MetalamaSample.sln"), dir]).GetAwaiter().GetResult();
        Assert.Equal(0, code);
        Assert.Contains("\"schema\":1", File.ReadAllText(Path.Combine(dir, "model.json")));
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/LamaRider.Worker.Tests`
Expected: compile errors — `Extractor`, `WorkerOutput`, `Program.Run` not defined.

- [ ] **Step 4: Implement**

```csharp
// WorkerModel.cs
using System.Text.Json;

namespace LamaRider.Worker;

public sealed record WorkerOutput(
    int Schema, string? MetalamaVersion,
    List<AspectClassInfo> AspectClasses, List<AspectInstanceInfo> Instances,
    List<TransformationInfo> Transformations, List<DiagnosticInfo> Diagnostics,
    List<FileClassification> TemplateClassifications, List<TransformedFileInfo> TransformedFiles);

public sealed record SourceLocation(string File, int Start, int End);
public sealed record AspectClassInfo(string Id, string Name, string Project, bool IsLibrary, SourceLocation? Location);
public sealed record AspectInstanceInfo(string AspectClass, string Target, string TargetName, string TargetType, string TargetKind, string Project, string AppliedVia, SourceLocation? Location);
public sealed record TransformationInfo(string AspectClass, string Target, string Kind, string Description, SourceLocation? Location);
public sealed record DiagnosticInfo(string Id, string Severity, string Message, string? Target, string? File, int? Line);
public sealed record ClassifiedSpan(int Start, int End, string Kind);
public sealed record FileClassification(string File, List<ClassifiedSpan> Spans);
public sealed record TransformedFileInfo(string File, string TransformedPath);

public static class WorkerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
```

```csharp
// Offsets.cs — LF-normalized offsets, because Rider/IntelliJ documents contain '\n' only.
using Microsoft.CodeAnalysis.Text;

namespace LamaRider.Worker;

public sealed class Offsets
{
    readonly Dictionary<string, (SourceText Text, int[] LfLineStarts)?> _files = new(StringComparer.OrdinalIgnoreCase);

    public int? FromLineColumn(string path, int line, int column) =>
        Get(path) is { } f && line < f.LfLineStarts.Length ? f.LfLineStarts[line] + column : null;

    public int? FromPosition(string path, int position)
    {
        if (Get(path) is not { } f) return null;
        var lp = f.Text.Lines.GetLinePosition(position);
        return f.LfLineStarts[lp.Line] + lp.Character;
    }

    (SourceText Text, int[] LfLineStarts)? Get(string path)
    {
        if (_files.TryGetValue(path, out var cached)) return cached;
        (SourceText, int[])? result = null;
        if (File.Exists(path))
        {
            var text = SourceText.From(File.ReadAllText(path));
            var starts = new int[text.Lines.Count];
            for (int i = 1; i < starts.Length; i++)
                starts[i] = starts[i - 1] + text.Lines[i - 1].Span.Length + 1;
            result = (text, starts);
        }
        _files[path] = result;
        return result;
    }
}
```

```csharp
// Extractor.cs
using Metalama.Framework.Code;
using Metalama.Framework.Engine.CodeModel;
using Metalama.Framework.Introspection;
using Metalama.Framework.Workspaces;

namespace LamaRider.Worker;

public static class Extractor
{
    public const int Schema = 1;

    public static WorkerOutput Extract(Workspace workspace, string outDir)
    {
        var offsets = new Offsets();
        var sourceTypes = workspace.SourceCode.Types
            .GroupBy(t => t.FullName)
            .ToDictionary(g => g.Key, g => g.First());

        string ClassId(IIntrospectionAspectClass c) =>
            sourceTypes.TryGetValue(c.FullName, out var t) ? t.ToSerializableId().Id : "T:" + c.FullName;

        SourceLocation? Locate(IDeclaration? declaration)
        {
            if (declaration == null) return null;
            foreach (var source in declaration.Sources)
            {
                var span = source.Span;
                if (string.IsNullOrEmpty(span.FilePath)) continue;
                var start = offsets.FromLineColumn(span.FilePath, span.StartLine, span.StartColumn);
                var end = offsets.FromLineColumn(span.FilePath, span.EndLine, span.EndColumn);
                if (start != null && end != null) return new SourceLocation(span.FilePath, start.Value, end.Value);
            }
            return null;
        }

        var classes = workspace.AspectClasses
            .GroupBy(c => c.FullName).Select(g => g.First())
            .Select(c =>
            {
                sourceTypes.TryGetValue(c.FullName, out var type);
                return new AspectClassInfo(ClassId(c), c.ShortName, type?.Compilation.Project.Name ?? "", type == null, Locate(type));
            })
            .OrderBy(c => c.Name)
            .ToList();

        var instances = workspace.AspectInstances
            .Where(i => !i.IsSkipped)
            .Select(i => new AspectInstanceInfo(
                ClassId(i.AspectClass),
                i.TargetDeclaration.ToSerializableId().Id,
                i.TargetDeclaration.ToDisplayString(),
                (i.TargetDeclaration as INamedType ?? i.TargetDeclaration.GetClosestNamedType())?.FullName ?? "",
                i.TargetDeclaration.DeclarationKind.ToString(),
                i.TargetDeclaration.Compilation.Project.Name ?? "",
                i.Predecessors.IsDefaultOrEmpty ? "Unknown" : i.Predecessors[0].Kind.ToString(),
                Locate(i.TargetDeclaration)))
            .Distinct() // records: collapses the same instance reported once per target framework
            .ToList();

        var transformations = workspace.Transformations
            .Select(t => new TransformationInfo(
                ClassId(t.Advice.AspectInstance.AspectClass),
                t.TargetDeclaration.ToSerializableId().Id,
                t.TransformationKind.ToString(),
                t.Description.ToString(),
                Locate(t.TargetDeclaration)))
            .Distinct()
            .ToList();

        var diagnostics = workspace.Diagnostics
            .Where(d => d.Source != IntrospectionDiagnosticSource.CSharp)
            .Select(d => new DiagnosticInfo(d.Id, d.Severity.ToString(), d.Message, d.Declaration?.ToSerializableId().Id, d.FilePath, d.Line))
            .Distinct()
            .ToList();

        return new WorkerOutput(
            Schema, workspace.MetalamaVersion, classes, instances, transformations, diagnostics,
            TemplateClassifier.Classify(workspace, offsets),
            WriteTransformedFiles(workspace, outDir));
    }

    static List<TransformedFileInfo> WriteTransformedFiles(Workspace workspace, string outDir)
    {
        var result = new List<TransformedFileInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(outDir, "transformed");
        Directory.CreateDirectory(dir);
        foreach (var compilation in workspace.TransformedCode.Compilations.OfType<CompilationModel>())
        foreach (var tree in compilation.RoslynCompilation.SyntaxTrees)
        {
            if (!File.Exists(tree.FilePath) || !seen.Add(tree.FilePath)) continue;
            var text = tree.GetText().ToString();
            if (text == File.ReadAllText(tree.FilePath)) continue; // untouched by aspects
            var target = Path.Combine(dir, $"{result.Count}_{Path.GetFileName(tree.FilePath)}");
            File.WriteAllText(target, text);
            result.Add(new TransformedFileInfo(tree.FilePath, target));
        }
        return result;
    }
}
```

```csharp
// TemplateClassifier.cs — Metalama's own T# classifier (engine API, same as the VS extension uses).
using Metalama.Framework.Engine.Formatting;
using Metalama.Framework.Engine.Services;
using Metalama.Framework.Workspaces;

namespace LamaRider.Worker;

public static class TemplateClassifier
{
    static readonly HashSet<string> Kinds = ["CompileTime", "CompileTimeVariable", "TemplateKeyword", "Dynamic"];

    public static List<FileClassification> Classify(Workspace workspace, Offsets offsets)
    {
        var result = new List<FileClassification>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in workspace.Projects)
        {
            var metalamaCompilation = project.SourceCode.Compilations.FirstOrDefault();
            if (metalamaCompilation == null) continue;
            var service = new ClassificationService(metalamaCompilation.Project.ServiceProvider);
            var compilation = project.RoslynCompilation;
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (!File.Exists(tree.FilePath) || !seen.Add(tree.FilePath)) continue;
                if (!ClassificationService.ContainsCompileTimeCode(tree.GetRoot())) continue;
                var spans = service.GetClassifiedTextSpans(compilation.GetSemanticModel(tree), CancellationToken.None)
                    .Where(s => Kinds.Contains(s.Classification.ToString()))
                    .Select(s => (Start: offsets.FromPosition(tree.FilePath, s.Span.Start), End: offsets.FromPosition(tree.FilePath, s.Span.End), Kind: s.Classification.ToString()))
                    .Where(s => s.Start != null && s.End != null && s.End > s.Start)
                    .Select(s => new ClassifiedSpan(s.Start!.Value, s.End!.Value, s.Kind))
                    .ToList();
                if (spans.Count > 0) result.Add(new FileClassification(tree.FilePath, spans));
            }
        }
        return result;
    }
}
```

Spike decision inside this step: if `new ClassificationService(metalamaCompilation.Project.ServiceProvider)` does not compile (constructor takes `in ProjectServiceProvider`), try the explicit conversion `(ProjectServiceProvider)(ServiceProvider<IProjectService>)metalamaCompilation.Project.ServiceProvider`. If neither compiles or `Classifies_template_code` fails at runtime, replace the body of `Classify` with the heuristic below and change the test assertion to `TemplateKeyword`-only:

```csharp
// Heuristic fallback: `meta` identifiers inside files that contain compile-time code.
foreach (var token in tree.GetRoot().DescendantTokens().Where(t => t.ValueText == "meta" && t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken)))
    spans.Add(new ClassifiedSpan(offsets.FromPosition(tree.FilePath, token.SpanStart)!.Value, offsets.FromPosition(tree.FilePath, token.Span.End)!.Value, "TemplateKeyword"));
```

```csharp
// Program.cs
using System.Text.Json;
using LamaRider.Worker;
using Metalama.Framework.Workspaces;

return await Program.Run(args);

public static partial class Program
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: LamaRider.Worker <solution-or-project> <outDir>");
            return 2;
        }
        try
        {
            Directory.CreateDirectory(args[1]);
            using var workspace = await WorkspaceCollection.Default.LoadAsync(args[0]);
            var output = Extractor.Extract(workspace, args[1]);
            await File.WriteAllTextAsync(Path.Combine(args[1], "model.json"), JsonSerializer.Serialize(output, WorkerJson.Options));
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }
}
```

(Top-level statements + `partial class Program` with a public static method: the compiler merges them; the test calls `Program.Run`.)

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/LamaRider.Worker.Tests`
Expected: 7 passed. If `Offsets_are_lf_normalized` fails by a constant shift, Metalama `SourceSpan` lines/columns are 1-based: subtract 1 in `Locate`.

- [ ] **Step 6: Commit** `feat(worker): extract Metalama aspect model via Workspaces API`

---

### Task 4: Backend pure model — JSON parsing, lookups, version detection

**Files:**
- Create: `src/dotnet/LamaRider/Model/AspectModel.cs`, `src/dotnet/LamaRider/Model/MetalamaVersionDetector.cs`
- Test: `tests/LamaRider.Model.Tests/LamaRider.Model.Tests.csproj`, `AspectModelTests.cs`, `MetalamaVersionDetectorTests.cs`

**Interfaces:**
- Consumes: Task 3 JSON shape.
- Produces:

```csharp
namespace LamaRider.Model;
public sealed class AspectModel {
    public static AspectModel Parse(string json);               // throws InvalidDataException on schema != 1
    public IReadOnlyList<AspectInstanceInfo> InstancesFor(string? docId, string file, int nameOffset, int declarationEnd);
    public int TargetCountOf(string aspectClassId);
    public bool IsAspectClass(string docId);
    public FileClassification? ClassificationFor(string file);
    // data: Schema, MetalamaVersion, AspectClasses, Instances, Transformations, Diagnostics, TemplateClassifications, TransformedFiles
}
public static class MetalamaVersionDetector {
    public static string? FromAssetsFiles(IEnumerable<string> assetsJsonContents); // highest Metalama.Framework version, null if none
}
```

- [ ] **Step 1: Test project**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RollForward>Major</RollForward>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="..\..\src\dotnet\LamaRider\Model\*.cs" Link="Model\%(Filename).cs" />
    <Compile Include="..\..\src\dotnet\LamaRider\Worker\WorkerRunner.cs" Link="Worker\WorkerRunner.cs" />
  </ItemGroup>
</Project>
```

(WorkerRunner.cs is created in Task 5; until then comment out that `Compile` line.)

- [ ] **Step 2: Write failing tests**

```csharp
// AspectModelTests.cs
using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class AspectModelTests
{
    const string File1 = @"C:\src\Calc.cs";
    const string Add = "M:Calc.Add(System.Int32)";

    static string Json(string instances, int schema = 1) => $$"""
        {"schema":{{schema}},"metalamaVersion":"2026.1.30",
         "aspectClasses":[{"id":"T:Log","name":"LogAttribute","project":"P","isLibrary":false,"location":null}],
         "instances":[{{instances}}],
         "transformations":[],"diagnostics":[],
         "templateClassifications":[{"file":"C:\\src\\Log.cs","spans":[{"start":1,"end":5,"kind":"TemplateKeyword"}]}],
         "transformedFiles":[]}
        """;

    static string Instance(string target, int start, int end, string cls = "T:Log") =>
        $$"""{"aspectClass":"{{cls}}","target":"{{target}}","targetName":"x","targetType":"Calc","targetKind":"Method","project":"P","appliedVia":"Attribute","location":{"file":"C:\\src\\Calc.cs","start":{{start}},"end":{{end}}}}""";

    [Fact]
    public void Rejects_unknown_schema() =>
        Assert.Throws<InvalidDataException>(() => AspectModel.Parse(Json("", schema: 2)));

    [Fact]
    public void Finds_instances_by_doc_id()
    {
        var model = AspectModel.Parse(Json(Instance(Add, 10, 50)));
        Assert.Single(model.InstancesFor(Add, "other.cs", 0, 0));
    }

    [Fact]
    public void Falls_back_to_span_when_doc_id_differs()
    {
        var model = AspectModel.Parse(Json(Instance("M:Weird", 10, 50)));
        Assert.Single(model.InstancesFor("M:Calc.Add(int)", @"c:\SRC\calc.cs", 20, 50));
    }

    [Fact]
    public void Span_fallback_does_not_leak_container_aspects()
    {
        // class instance spans 0..100, member declaration ends at 50 -> must not match
        var model = AspectModel.Parse(Json(Instance("T:Calc", 0, 100)));
        Assert.Empty(model.InstancesFor("M:Calc.Sub(System.Int32)", File1, 20, 50));
    }

    [Fact]
    public void Duplicate_instances_are_collapsed()
    {
        var model = AspectModel.Parse(Json(Instance(Add, 10, 50) + "," + Instance(Add, 10, 50)));
        Assert.Single(model.InstancesFor(Add, File1, 0, 0));
    }

    [Fact]
    public void Counts_targets_of_aspect_class()
    {
        var model = AspectModel.Parse(Json(Instance(Add, 10, 50) + "," + Instance("M:Calc.Sub", 60, 90)));
        Assert.Equal(2, model.TargetCountOf("T:Log"));
        Assert.True(model.IsAspectClass("T:Log"));
    }

    [Fact]
    public void Finds_classification_by_path_case_insensitively() =>
        Assert.NotNull(AspectModel.Parse(Json("")).ClassificationFor(@"c:\src\LOG.cs"));
}
```

```csharp
// MetalamaVersionDetectorTests.cs
using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class MetalamaVersionDetectorTests
{
    [Fact]
    public void Version_detection_picks_highest() =>
        Assert.Equal("2026.1.30", MetalamaVersionDetector.FromAssetsFiles([
            """{"libraries":{"Metalama.Framework/2026.1.9":{}}}""",
            """{"libraries":{"Metalama.Framework/2026.1.30":{},"Metalama.Framework.Redist/2026.1.30":{}}}"""]));

    [Fact]
    public void Version_detection_returns_null_without_metalama() =>
        Assert.Null(MetalamaVersionDetector.FromAssetsFiles(["""{"libraries":{"Newtonsoft.Json/13.0.3":{}}}"""]));

    [Fact]
    public void Version_detection_keeps_prerelease_suffix() =>
        Assert.Equal("2026.2.1-preview", MetalamaVersionDetector.FromAssetsFiles(["""{"libraries":{"Metalama.Framework/2026.2.1-preview":{}}}"""]));
}
```

- [ ] **Step 3: Run** `dotnet test tests/LamaRider.Model.Tests` — Expected: compile errors (`AspectModel` missing).

- [ ] **Step 4: Implement**

```csharp
// src/dotnet/LamaRider/Model/AspectModel.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace LamaRider.Model;

public sealed class SourceLocation { public string File = ""; public int Start; public int End; }
public sealed class AspectClassInfo { public string Id = ""; public string Name = ""; public string Project = ""; public bool IsLibrary; public SourceLocation? Location; }
public sealed class AspectInstanceInfo
{
    public string AspectClass = ""; public string Target = ""; public string TargetName = ""; public string TargetType = "";
    public string TargetKind = ""; public string Project = ""; public string AppliedVia = ""; public SourceLocation? Location;
    internal string Key => $"{AspectClass}|{Target}|{AppliedVia}";
}
public sealed class TransformationInfo { public string AspectClass = ""; public string Target = ""; public string Kind = ""; public string Description = ""; public SourceLocation? Location; }
public sealed class DiagnosticInfo { public string Id = ""; public string Severity = ""; public string Message = ""; public string? Target; public string? File; public int? Line; }
public sealed class ClassifiedSpan { public int Start; public int End; public string Kind = ""; }
public sealed class FileClassification { public string File = ""; public List<ClassifiedSpan> Spans = new(); }
public sealed class TransformedFileInfo { public string File = ""; public string TransformedPath = ""; }

public sealed class AspectModel
{
    public const int SupportedSchema = 1;

    public int Schema;
    public string? MetalamaVersion;
    public List<AspectClassInfo> AspectClasses = new();
    public List<AspectInstanceInfo> Instances = new();
    public List<TransformationInfo> Transformations = new();
    public List<DiagnosticInfo> Diagnostics = new();
    public List<FileClassification> TemplateClassifications = new();
    public List<TransformedFileInfo> TransformedFiles = new();

    Dictionary<string, List<AspectInstanceInfo>> _byTarget = new();
    Dictionary<string, List<AspectInstanceInfo>> _byFile = new();
    Dictionary<string, FileClassification> _classifications = new();
    HashSet<string> _aspectClassIds = new();

    // Newtonsoft matches camelCase JSON to these fields case-insensitively.
    public static AspectModel Parse(string json)
    {
        var model = JsonConvert.DeserializeObject<AspectModel>(json) ?? throw new InvalidDataException("Empty worker output");
        if (model.Schema != SupportedSchema) throw new InvalidDataException($"Unsupported worker schema {model.Schema}");
        model.Instances = model.Instances.GroupBy(i => i.Key).Select(g => g.First()).ToList();
        model._byTarget = model.Instances.GroupBy(i => i.Target).ToDictionary(g => g.Key, g => g.ToList());
        model._byFile = model.Instances.Where(i => i.Location != null)
            .GroupBy(i => Normalize(i.Location!.File)).ToDictionary(g => g.Key, g => g.ToList());
        model._classifications = model.TemplateClassifications
            .GroupBy(c => Normalize(c.File)).ToDictionary(g => g.Key, g => g.First());
        model._aspectClassIds = new HashSet<string>(model.AspectClasses.Select(c => c.Id));
        return model;
    }

    /// Doc-ID match first; otherwise a declaration in the same file whose span ends where
    /// this declaration ends and contains its name (Roslyn spans exclude leading doc comments,
    /// so starts differ but ends agree; a containing type never ends at a member's end).
    public IReadOnlyList<AspectInstanceInfo> InstancesFor(string? docId, string file, int nameOffset, int declarationEnd)
    {
        if (docId != null && _byTarget.TryGetValue(docId, out var byId)) return byId;
        if (!_byFile.TryGetValue(Normalize(file), out var inFile)) return Array.Empty<AspectInstanceInfo>();
        return inFile.Where(i => i.Location!.End == declarationEnd && i.Location.Start <= nameOffset).ToList();
    }

    public int TargetCountOf(string aspectClassId) => Instances.Count(i => i.AspectClass == aspectClassId);

    public bool IsAspectClass(string docId) => _aspectClassIds.Contains(docId);

    public FileClassification? ClassificationFor(string file) =>
        _classifications.TryGetValue(Normalize(file), out var c) ? c : null;

    // ponytail: case-insensitive paths everywhere; per-OS comparison if Linux users hit collisions.
    static string Normalize(string path) => Path.GetFullPath(path).ToUpperInvariant();
}
```

```csharp
// src/dotnet/LamaRider/Model/MetalamaVersionDetector.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LamaRider.Model;

public static class MetalamaVersionDetector
{
    static readonly Regex Library = new("\"Metalama\\.Framework/([^\"]+)\"", RegexOptions.Compiled);

    public static string? FromAssetsFiles(IEnumerable<string> assetsJsonContents) =>
        assetsJsonContents
            .SelectMany(json => Library.Matches(json).Cast<Match>())
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .OrderByDescending(v => Version.TryParse(v.Split('-')[0], out var parsed) ? parsed : new Version(0, 0))
            .ThenBy(v => v.Contains("-")) // release before prerelease of same number
            .FirstOrDefault();
}
```

- [ ] **Step 5: Run** `dotnet test tests/LamaRider.Model.Tests` — Expected: 10 passed.

- [ ] **Step 6: Commit** `feat(backend): aspect model parsing and Metalama version detection`

---

### Task 5: WorkerRunner — build worker on demand and run it

**Files:**
- Create: `src/dotnet/LamaRider/Worker/WorkerRunner.cs`
- Test: `tests/LamaRider.Model.Tests/WorkerRunnerTests.cs` (enable the `Compile Include` line from Task 4)

**Interfaces:**
- Consumes: `AspectModel.Parse`.
- Produces:

```csharp
namespace LamaRider.Worker;
public sealed class WorkerException : Exception { public WorkerException(string message); }
public sealed class WorkerRunner {
    public WorkerRunner(string workerSourceDir, string cacheRoot, string pluginVersion);
    public string LogPath { get; }                                   // <cacheRoot>/logs/last-run.log
    public AspectModel Run(string metalamaVersion, string solutionPath, CancellationToken ct); // throws WorkerException
    public string LastOutputDir { get; }                             // dir containing model.json of last successful run
}
```

- [ ] **Step 1: Failing test** (uses a fake "worker": a cmd/sh script would be platform-specific, so the test points `Run` at a solution path that does not exist, using the real worker source — build succeeds, run fails):

```csharp
// WorkerRunnerTests.cs
using LamaRider.Worker;

namespace LamaRider.Model.Tests;

public class WorkerRunnerTests
{
    static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    static readonly string WorkerSrc = Path.Combine(Root, "src/worker/LamaRider.Worker");

    [Fact]
    public void Failed_run_reports_stderr()
    {
        var cache = Path.Combine(Path.GetTempPath(), "lamarider-runner", Guid.NewGuid().ToString("N"));
        var runner = new WorkerRunner(WorkerSrc, cache, "test");
        var ex = Assert.Throws<WorkerException>(() => runner.Run("2026.1.30", Path.Combine(cache, "missing.sln"), CancellationToken.None));
        Assert.Contains("missing.sln", ex.Message);
        Assert.True(File.Exists(runner.LogPath));
    }

    [Fact]
    public void Runs_worker_on_sample()
    {
        var cache = Path.Combine(Path.GetTempPath(), "lamarider-runner", Guid.NewGuid().ToString("N"));
        var runner = new WorkerRunner(WorkerSrc, cache, "test");
        var model = runner.Run("2026.1.30", Path.Combine(Root, "samples/MetalamaSample/MetalamaSample.sln"), CancellationToken.None);
        Assert.Equal(2, model.TargetCountOf("T:MetalamaSample.LogAttribute"));
        Assert.True(File.Exists(Path.Combine(runner.LastOutputDir, "model.json")));
    }
}
```

- [ ] **Step 2: Run** `dotnet test tests/LamaRider.Model.Tests --filter WorkerRunner` — Expected: compile error.

- [ ] **Step 3: Implement**

```csharp
// src/dotnet/LamaRider/Worker/WorkerRunner.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using LamaRider.Model;

namespace LamaRider.Worker;

public sealed class WorkerException(string message) : Exception(message);

public sealed class WorkerRunner(string workerSourceDir, string cacheRoot, string pluginVersion)
{
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public string LogPath => Path.Combine(cacheRoot, "logs", "last-run.log");
    public string LastOutputDir { get; private set; } = "";

    public AspectModel Run(string metalamaVersion, string solutionPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, $"LamaRider worker run {DateTime.Now:O}{Environment.NewLine}");

        var dll = EnsureBuilt(metalamaVersion, ct);
        var runsDir = Path.Combine(cacheRoot, "out", Hash(solutionPath));
        var outDir = Path.Combine(runsDir, DateTime.UtcNow.Ticks.ToString());
        Exec($"\"{dll}\" \"{solutionPath}\" \"{outDir}\"", Path.GetDirectoryName(dll)!, ct);

        var modelPath = Path.Combine(outDir, "model.json");
        if (!File.Exists(modelPath)) throw new WorkerException($"Worker produced no model.json. See {LogPath}");
        var model = AspectModel.Parse(File.ReadAllText(modelPath));

        foreach (var old in Directory.GetDirectories(runsDir).Where(d => d != outDir))
            try { Directory.Delete(old, true); } catch (IOException) { /* still open in a diff viewer */ }
        LastOutputDir = outDir;
        return model;
    }

    string EnsureBuilt(string metalamaVersion, CancellationToken ct)
    {
        var key = $"{pluginVersion}-{metalamaVersion}";
        var binDir = Path.Combine(cacheRoot, "worker", key);
        var dll = Path.Combine(binDir, "LamaRider.Worker.dll");
        if (File.Exists(dll)) return dll;

        var srcDir = Path.Combine(cacheRoot, "worker-src", key);
        if (Directory.Exists(srcDir)) Directory.Delete(srcDir, true);
        Directory.CreateDirectory(srcDir);
        foreach (var file in Directory.GetFiles(workerSourceDir, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(srcDir, Path.GetFileName(file)));

        Exec($"build \"{Path.Combine(srcDir, "LamaRider.Worker.csproj")}\" -c Release -p:MetalamaVersion={metalamaVersion} -o \"{binDir}\"", srcDir, ct);
        if (!File.Exists(dll)) throw new WorkerException($"Worker build failed. See {LogPath}");
        return dll;
    }

    void Exec(string arguments, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { Log(e.Data); lock (stderr) stderr.AppendLine(e.Data); } };
        Log($"> dotnet {arguments}");
        try { process.Start(); }
        catch (Exception e) { throw new WorkerException($"Cannot start 'dotnet' ({e.Message}). Is the .NET SDK on PATH?"); }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (ct.Register(() => { try { process.Kill(); } catch (InvalidOperationException) { } }))
        {
            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                throw new WorkerException($"Worker timed out after {Timeout.TotalMinutes} minutes. See {LogPath}");
            }
            process.WaitForExit(); // flush async readers
        }
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new WorkerException(FirstLines(stderr.ToString()) is { Length: > 0 } s ? s : $"dotnet exited with {process.ExitCode}. See {LogPath}");
    }

    void Log(string line)
    {
        lock (this) File.AppendAllText(LogPath, line + Environment.NewLine);
    }

    static string FirstLines(string text) => string.Join(Environment.NewLine, text.Split('\n').Take(5)).Trim();

    static string Hash(string path)
    {
        using var sha = SHA1.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).Replace("-", "").Substring(0, 12);
    }
}
```


- [ ] **Step 4: Run** `dotnet test tests/LamaRider.Model.Tests` — Expected: 12 passed (first run builds the worker; may take ~1 min).

- [ ] **Step 5: Commit** `feat(backend): build and run worker process with timeout and logging`

---

### Task 6: rd model + AspectModelHost (triggers, single-flight, publishing)

**Files:**
- Modify: `protocol/src/main/kotlin/model/rider/LamaRiderModel.kt`
- Create: `src/dotnet/LamaRider/AspectModelHost.cs`

**Interfaces:**
- rd model (generated as `JetBrains.Rider.Model.LamaRiderModel` in C#, `com.jetbrains.rider.model.LamaRiderModel` in Kotlin; accessor `solution.GetProtocolSolution().GetLamaRiderModel()` / `project.solution.lamaRiderModel`):

```kotlin
object LamaRiderModel : Ext(SolutionModel.Solution) {
    init {
        property("status", string.nullable)       // backend -> frontend, human readable
        property("modelPath", string.nullable)    // backend -> frontend, path of latest model.json (changes every run)
        signal("refresh", void)                   // frontend -> backend
        signal("codeVisionClicked", string)       // backend -> frontend: doc id of clicked declaration
    }
}
```

- Produces (C#):

```csharp
namespace LamaRider;
[SolutionComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
public sealed class AspectModelHost {
    public AspectModel? Model { get; }          // latest successful model or null
    public void RequestRefresh(string reason);  // single-flight + coalescing
    public void NotifyCodeVisionClicked(string docId);
}
```

- [ ] **Step 1: Update rd model** (above) and run `./gradlew :protocol:rdgen`. Expected: `src/dotnet/LamaRider/Rd/LamaRiderModel.Generated.cs` and `src/rider/main/kotlin/dev/lamarider/rd/LamaRiderModel.Generated.kt` exist.

- [ ] **Step 2: Implement AspectModelHost**

```csharp
// src/dotnet/LamaRider/AspectModelHost.cs
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Application.Parts;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.Features.SolutionBuilders;
using JetBrains.RdBackend.Common.Features;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.Rider.Model;
using JetBrains.Util;
using LamaRider.Model;
using LamaRider.Worker;

namespace LamaRider;

[SolutionComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
public sealed class AspectModelHost
{
    static readonly ILogger Logger = JetBrains.Util.Logging.Logger.GetLogger<AspectModelHost>();

    readonly Lifetime _lifetime;
    readonly ISolution _solution;
    readonly WorkerRunner _runner;
    readonly object _gate = new();
    bool _running, _pending;
    volatile AspectModel? _model;

    public AspectModelHost(Lifetime lifetime, ISolution solution, ISolutionBuilder solutionBuilder)
    {
        _lifetime = lifetime;
        _solution = solution;
        var pluginDir = Path.GetDirectoryName(Path.GetDirectoryName(typeof(AspectModelHost).Assembly.Location))!;
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LamaRider");
        _runner = new WorkerRunner(Path.Combine(pluginDir, "worker"), cache, typeof(AspectModelHost).Assembly.GetName().Version.ToString());

        _solution.Locks.ExecuteOrQueueEx(lifetime, "LamaRider.Init", () =>
        {
            var rd = RdModel;
            rd.Refresh.Advise(lifetime, _ => RequestRefresh("manual"));
            solutionBuilder.RunningRequest.Change.Advise(lifetime, args =>
            {
                if (args.HasOld && args.Old != null && args.New == null) RequestRefresh("build finished");
            });
            RequestRefresh("solution loaded");
        });
    }

    public AspectModel? Model => _model;

    LamaRiderModel RdModel => _solution.GetProtocolSolution().GetLamaRiderModel();

    public void NotifyCodeVisionClicked(string docId) =>
        _solution.Locks.ExecuteOrQueueEx(_lifetime, "LamaRider.Click", () => RdModel.CodeVisionClicked.Fire(docId));

    public void RequestRefresh(string reason)
    {
        lock (_gate)
        {
            if (_running) { _pending = true; return; }
            _running = true;
        }
        Task.Run(() => RefreshLoop(reason), _lifetime);
    }

    void RefreshLoop(string reason)
    {
        while (true)
        {
            RunOnce(reason);
            lock (_gate)
            {
                if (!_pending || !_lifetime.IsAlive) { _running = false; return; }
                _pending = false;
                reason = "queued";
            }
        }
    }

    void RunOnce(string reason)
    {
        var assets = _solution.GetAllProjects()
            .Select(p => p.ProjectFileLocation?.Directory.Combine("obj").Combine("project.assets.json"))
            .Where(p => p != null && p.ExistsFile)
            .Select(p => File.ReadAllText(p!.FullPath))
            .ToList();
        var version = MetalamaVersionDetector.FromAssetsFiles(assets);
        if (version == null) { Publish("No Metalama projects", null); return; }
        if (assets.Select(a => MetalamaVersionDetector.FromAssetsFiles(new[] { a })).Where(v => v != null).Distinct().Count() > 1)
            Logger.Warn($"LamaRider: mixed Metalama versions in solution, using {version}"); // ponytail: one worker per version if this matters

        Publish($"Refreshing ({reason})…", null);
        try
        {
            var model = _runner.Run(version, _solution.SolutionFilePath.FullPath, _lifetime.ToCancellationToken());
            _model = model;
            Publish($"Refreshed {DateTime.Now:HH:mm} · {model.AspectClasses.Count} aspects / {model.Instances.Count} instances",
                Path.Combine(_runner.LastOutputDir, "model.json"));
            _solution.Locks.ExecuteOrQueueEx(_lifetime, "LamaRider.Invalidate", () => _solution.GetComponent<IDaemon>().Invalidate("LamaRider model refreshed"));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is WorkerException or InvalidDataException or IOException)
        {
            Publish((_model == null ? "" : "Stale · ") + e.Message + " — log: " + _runner.LogPath, null);
        }
    }

    void Publish(string status, string? modelPath) =>
        _solution.Locks.ExecuteOrQueueEx(_lifetime, "LamaRider.Publish", () =>
        {
            RdModel.Status.Value = status;
            if (modelPath != null) RdModel.ModelPath.Value = modelPath;
        });
}
```

Compile-fix notes (the names were verified against RD-263.6800.37 except these, fix by IDE quick-fix if needed): `GetProtocolSolution()` namespace, `Instantiation.ContainerAsyncAnyThreadSafe`, `Lifetime.ToCancellationToken()` (alternative: `_lifetime.CreateNested().Lifetime` + `ToCancellationToken`), `ProjectFileLocation.Directory`.

- [ ] **Step 3: Build** `./gradlew compileDotNet` — Expected: success.

- [ ] **Step 4: Commit** `feat(backend): solution host runs worker after build and publishes over rd`

---

### Task 7: Code Vision

**Files:**
- Create: `src/dotnet/LamaRider/CodeVision/AspectCodeInsightsProvider.cs`, `src/dotnet/LamaRider/CodeVision/AspectCodeVisionStage.cs`

**Interfaces:**
- Consumes: `AspectModelHost.Model`, `AspectModel.InstancesFor/TargetCountOf/IsAspectClass`, `AspectModelHost.NotifyCodeVisionClicked`.
- Produces: highlightings `CodeInsightsHighlighting` with provider id `LamaRider.Aspects`; click fires `codeVisionClicked(docId)`.

- [ ] **Step 1: Provider**

```csharp
// AspectCodeInsightsProvider.cs
using System.Collections.Generic;
using JetBrains.Application.Parts;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Daemon.CodeInsights;
using JetBrains.Rider.Model;

namespace LamaRider.CodeVision;

[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public sealed class AspectCodeInsightsProvider(AspectModelHost host) : ICodeInsightsProvider
{
    public const string Id = "LamaRider.Aspects";
    public string ProviderId => Id;
    public string DisplayName => "Metalama aspects";
    public CodeVisionAnchorKind DefaultAnchor => CodeVisionAnchorKind.Top;
    public ICollection<CodeVisionRelativeOrdering> RelativeOrderings => new CodeVisionRelativeOrdering[] { new CodeVisionRelativeOrderingLast() };
    public bool IsAvailableIn(ISolution solution) => true;

    public void OnClick(CodeInsightHighlightInfo highlightInfo, ISolution solution, CodeInsightsClickInfo? clickInfo)
    {
        if (highlightInfo.CodeInsightsHighlighting is AspectCodeInsightsHighlighting h) host.NotifyCodeVisionClicked(h.DocId);
    }

    public void OnExtraActionClick(CodeInsightHighlightInfo highlightInfo, string actionId, ISolution solution) { }
}
```

- [ ] **Step 2: Stage + highlighting subclass carrying the doc id**

```csharp
// AspectCodeVisionStage.cs
using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Application.Settings;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Daemon.CodeInsights;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.Util;
using LamaRider.Model;

namespace LamaRider.CodeVision;

public sealed class AspectCodeInsightsHighlighting(DocumentRange range, string text, string tooltip, AspectCodeInsightsProvider provider, IDeclaredElement element, string docId)
    : CodeInsightsHighlighting(range, text, tooltip, text, provider, element, null)
{
    public string DocId { get; } = docId;
}

[DaemonStage(HighlightingTypes = new[] { typeof(AspectCodeInsightsHighlighting) })]
public sealed class AspectCodeVisionStage(AspectModelHost host, AspectCodeInsightsProvider provider) : IDaemonStage
{
    public IEnumerable<IDaemonStageProcess> CreateProcess(IDaemonProcess process, IContextBoundSettingsStore settings, DaemonProcessKind processKind)
    {
        if (processKind != DaemonProcessKind.VISIBLE_DOCUMENT || host.Model is not { } model) return Array.Empty<IDaemonStageProcess>();
        if (process.SourceFile.GetPrimaryPsiFile() is not ICSharpFile file) return Array.Empty<IDaemonStageProcess>();
        return new[] { new Process(process, file, model, provider) };
    }

    sealed class Process(IDaemonProcess daemonProcess, ICSharpFile file, AspectModel model, AspectCodeInsightsProvider provider) : IDaemonStageProcess
    {
        public IDaemonProcess DaemonProcess => daemonProcess;

        public void Execute(Action<DaemonStageResult> committer)
        {
            var path = daemonProcess.SourceFile.GetLocation().FullPath;
            var result = new List<HighlightingInfo>();
            foreach (var declaration in file.Descendants<IDeclaration>())
            {
                if (daemonProcess.InterruptFlag) throw new OperationCanceledException();
                if (declaration.DeclaredElement is not { } element) continue;
                var nameRange = declaration.GetNameDocumentRange();
                if (!nameRange.IsValid()) continue;

                var docId = DocId(element);
                var instances = model.InstancesFor(docId, path, nameRange.StartOffset.Offset, declaration.GetDocumentRange().EndOffset.Offset);
                var targets = docId != null && model.IsAspectClass(docId) ? model.TargetCountOf(docId) : 0;
                if (instances.Count == 0 && targets == 0) continue;

                var parts = new List<string>();
                if (instances.Count > 0) parts.Add(instances.Count == 1 ? "1 aspect" : $"{instances.Count} aspects");
                if (targets > 0) parts.Add(targets == 1 ? "applied to 1 target" : $"applied to {targets} targets");
                var tooltip = string.Join("\n", instances.Select(i => $"{Short(i.AspectClass)} ({i.AppliedVia})"));

                var key = docId ?? instances[0].Target;
                result.Add(new HighlightingInfo(nameRange,
                    new AspectCodeInsightsHighlighting(nameRange, string.Join(" · ", parts), tooltip.Length > 0 ? tooltip : "Metalama aspect class", provider, element, key)));
            }
            committer(new DaemonStageResult(result));
        }

        static string Short(string classId) => classId.Substring(classId.LastIndexOf('.') + 1);

        static string? DocId(IDeclaredElement element) => element switch
        {
            ITypeElement type => XMLDocUtil.GetTypeElementXmlDocId(type),
            ITypeMember member => XMLDocUtil.GetTypeMemberXmlDocId(member, member.ShortName),
            _ => null,
        };
    }
}
```

- [ ] **Step 3: Build** `./gradlew compileDotNet` — Expected: success.

- [ ] **Step 4: Commit** `feat(backend): Code Vision for aspect targets and aspect classes`

---

### Task 8: T# highlighting

**Files:**
- Create: `src/dotnet/LamaRider/TSharp/TSharpHighlighting.cs`, `src/dotnet/LamaRider/TSharp/TSharpHighlightingStage.cs`

**Interfaces:**
- Consumes: `AspectModel.ClassificationFor(file)` (`Kind` ∈ CompileTime, CompileTimeVariable, TemplateKeyword, Dynamic).
- Produces: color settings group "Metalama T#" (auto-generated by Rider from `RegisterHighlighterGroup`).

- [ ] **Step 1: Highlighter registration + highlighting**

```csharp
// TSharpHighlighting.cs
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.TextControl.DocumentMarkup;

namespace LamaRider.TSharp;

[RegisterHighlighterGroup(GroupId, "Metalama T#", HighlighterGroupPriority.LANGUAGE_SETTINGS)]
[RegisterHighlighter(CompileTime, GroupId = GroupId, RiderPresentableName = "Compile-time code",
    BackgroundColor = "#EEF3FB", DarkBackgroundColor = "#262D36", EffectType = EffectType.TEXT, Layer = HighlighterLayer.SYNTAX + 1)]
[RegisterHighlighter(CompileTimeVariable, GroupId = GroupId, RiderPresentableName = "Compile-time variable",
    ForegroundColor = "#7A3E9D", DarkForegroundColor = "#C792EA", FontStyle = FontStyle.Bold, EffectType = EffectType.TEXT, Layer = HighlighterLayer.SYNTAX + 2)]
[RegisterHighlighter(TemplateKeyword, GroupId = GroupId, RiderPresentableName = "Template keyword (meta)",
    ForegroundColor = "#B25E00", DarkForegroundColor = "#FFB86C", FontStyle = FontStyle.Bold, EffectType = EffectType.TEXT, Layer = HighlighterLayer.SYNTAX + 2)]
[RegisterHighlighter(Dynamic, GroupId = GroupId, RiderPresentableName = "Dynamic member",
    EffectType = EffectType.SOLID_UNDERLINE, EffectColor = "#B25E00", Layer = HighlighterLayer.SYNTAX + 2)]
public static class TSharpHighlighterIds
{
    public const string GroupId = "LamaRider.TSharp";
    public const string CompileTime = "LamaRider.TSharp.CompileTime";
    public const string CompileTimeVariable = "LamaRider.TSharp.CompileTimeVariable";
    public const string TemplateKeyword = "LamaRider.TSharp.TemplateKeyword";
    public const string Dynamic = "LamaRider.TSharp.Dynamic";

    public static string? ForKind(string kind) => kind switch
    {
        "CompileTime" => CompileTime,
        "CompileTimeVariable" => CompileTimeVariable,
        "TemplateKeyword" => TemplateKeyword,
        "Dynamic" => Dynamic,
        _ => null,
    };
}

[StaticSeverityHighlighting(Severity.INFO, typeof(HighlightingGroupIds.IdentifierHighlightings), OverlapResolve = OverlapResolveKind.NONE, ShowToolTipInStatusBar = false)]
public sealed class TSharpHighlighting(string attributeId, DocumentRange range) : ICustomAttributeIdHighlighting
{
    public string AttributeId => attributeId;
    public string? ToolTip => null;
    public string? ErrorStripeToolTip => null;
    public bool IsValid() => range.IsValid();
    public DocumentRange CalculateRange() => range;
}
```

(`RegisterHighlighter` for `GroupId` on a static class mirrors `UnityHighlightingAttributeIds` in the bundled rider-unity plugin. If `HighlighterLayer.SYNTAX + 1` does not compile, use `(HighlighterLayer)3001`/`3002`.)

- [ ] **Step 2: Stage**

```csharp
// TSharpHighlightingStage.cs
using System;
using System.Collections.Generic;
using JetBrains.Application.Settings;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Feature.Services.Daemon;

namespace LamaRider.TSharp;

[DaemonStage(HighlightingTypes = new[] { typeof(TSharpHighlighting) })]
public sealed class TSharpHighlightingStage(AspectModelHost host) : IDaemonStage
{
    public IEnumerable<IDaemonStageProcess> CreateProcess(IDaemonProcess process, IContextBoundSettingsStore settings, DaemonProcessKind processKind)
    {
        if (processKind != DaemonProcessKind.VISIBLE_DOCUMENT) return Array.Empty<IDaemonStageProcess>();
        var classification = host.Model?.ClassificationFor(process.SourceFile.GetLocation().FullPath);
        return classification == null ? Array.Empty<IDaemonStageProcess>() : new[] { new Process(process, classification) };
    }

    sealed class Process(IDaemonProcess daemonProcess, LamaRider.Model.FileClassification classification) : IDaemonStageProcess
    {
        public IDaemonProcess DaemonProcess => daemonProcess;

        public void Execute(Action<DaemonStageResult> committer)
        {
            var document = daemonProcess.Document;
            var length = document.GetTextLength();
            var result = new List<HighlightingInfo>();
            foreach (var span in classification.Spans)
            {
                if (TSharpHighlighterIds.ForKind(span.Kind) is not { } id || span.End > length) continue; // stale model vs edited doc
                var range = new DocumentRange(document, new JetBrains.Util.TextRange(span.Start, span.End));
                result.Add(new HighlightingInfo(range, new TSharpHighlighting(id, range)));
            }
            committer(new DaemonStageResult(result));
        }
    }
}
```

- [ ] **Step 3: Build** `./gradlew compileDotNet` — Expected: success.

- [ ] **Step 4: Commit** `feat(backend): T# compile-time highlighting with color settings`

---

### Task 9: Frontend — service, rd listener, model loading, refresh action

**Files:**
- Create: `src/rider/main/kotlin/dev/lamarider/ModelData.kt`, `LamaRiderService.kt`, `LamaRiderProtocolListener.kt`, `RefreshAction.kt`
- Modify: `src/rider/main/resources/META-INF/plugin.xml`

**Interfaces:**
- Consumes: rd `LamaRiderModel` (`status`, `modelPath`, `refresh`, `codeVisionClicked`).
- Produces:

```kotlin
@Service(Service.Level.PROJECT) class LamaRiderService(val project: Project) {
    val model: ModelData?            // latest parsed model
    val status: String
    fun refresh()
    fun addListener(disposable: Disposable, listener: () -> Unit)   // fired on EDT when model/status changes
    fun transformedPathFor(file: String): String?
    var onCodeVisionClicked: ((String) -> Unit)?
}
data class ModelData(...)  // Gson mirror of AspectModel JSON
```

- [ ] **Step 1: ModelData.kt**

```kotlin
package dev.lamarider

import com.google.gson.Gson
import java.io.File

data class SourceLocation(val file: String, val start: Int, val end: Int)
data class AspectClassInfo(val id: String, val name: String, val project: String, val isLibrary: Boolean, val location: SourceLocation?)
data class AspectInstanceInfo(val aspectClass: String, val target: String, val targetName: String, val targetType: String,
                              val targetKind: String, val project: String, val appliedVia: String, val location: SourceLocation?)
data class TransformationInfo(val aspectClass: String, val target: String, val kind: String, val description: String, val location: SourceLocation?)
data class DiagnosticInfo(val id: String, val severity: String, val message: String, val target: String?, val file: String?, val line: Int?)
data class TransformedFileInfo(val file: String, val transformedPath: String)

data class ModelData(
    val aspectClasses: List<AspectClassInfo> = emptyList(),
    val instances: List<AspectInstanceInfo> = emptyList(),
    val transformations: List<TransformationInfo> = emptyList(),
    val diagnostics: List<DiagnosticInfo> = emptyList(),
    val transformedFiles: List<TransformedFileInfo> = emptyList(),
) {
    fun instancesOf(aspectClassId: String) = instances.filter { it.aspectClass == aspectClassId }.distinctBy { it.target }
    fun instancesOn(target: String) = instances.filter { it.target == target }
    fun transformationsOf(instance: AspectInstanceInfo) =
        transformations.filter { it.target == instance.target && it.aspectClass == instance.aspectClass }
    fun diagnosticsOn(target: String) = diagnostics.filter { it.target == target }
    fun aspectClass(id: String) = aspectClasses.firstOrNull { it.id == id }

    companion object {
        fun load(path: String): ModelData = File(path).reader().use { Gson().fromJson(it, ModelData::class.java) }
    }
}
```

- [ ] **Step 2: LamaRiderService.kt**

```kotlin
package dev.lamarider

import com.intellij.openapi.Disposable
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.Service
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.jetbrains.rd.util.lifetime.Lifetime
import com.jetbrains.rider.model.LamaRiderModel
import java.io.File
import java.util.concurrent.CopyOnWriteArrayList

@Service(Service.Level.PROJECT)
class LamaRiderService(val project: Project) {
    private val log = logger<LamaRiderService>()
    private val listeners = CopyOnWriteArrayList<() -> Unit>()
    private var rd: LamaRiderModel? = null

    @Volatile var model: ModelData? = null; private set
    @Volatile var status: String = "Waiting for backend…"; private set
    var onCodeVisionClicked: ((String) -> Unit)? = null

    fun attach(lifetime: Lifetime, rdModel: LamaRiderModel) {
        rd = rdModel
        rdModel.status.advise(lifetime) { status = it ?: ""; fire() }
        rdModel.modelPath.advise(lifetime) { path ->
            if (path == null) return@advise
            ApplicationManager.getApplication().executeOnPooledThread {
                try {
                    model = ModelData.load(path)
                } catch (e: Exception) {
                    log.warn("Cannot read $path", e)
                    status = "Cannot read model: ${e.message}"
                }
                ApplicationManager.getApplication().invokeLater { fire() }
            }
        }
        rdModel.codeVisionClicked.advise(lifetime) { id -> onCodeVisionClicked?.invoke(id) }
    }

    fun refresh() { rd?.refresh?.fire(Unit) }

    fun addListener(disposable: Disposable, listener: () -> Unit) {
        listeners += listener
        Disposer.register(disposable) { listeners -= listener }
    }

    fun transformedPathFor(file: String): String? =
        model?.transformedFiles?.firstOrNull { File(it.file).canonicalPath.equals(File(file).canonicalPath, ignoreCase = true) }?.transformedPath

    private fun fire() = listeners.forEach { it() }
}
```

- [ ] **Step 3: LamaRiderProtocolListener.kt**

```kotlin
package dev.lamarider

import com.intellij.openapi.client.ClientProjectSession
import com.intellij.openapi.components.service
import com.jetbrains.rd.protocol.SolutionExtListener
import com.jetbrains.rd.util.lifetime.Lifetime
import com.jetbrains.rider.model.LamaRiderModel

class LamaRiderProtocolListener : SolutionExtListener<LamaRiderModel> {
    override fun extensionCreated(lifetime: Lifetime, session: ClientProjectSession, model: LamaRiderModel) {
        val service = session.project.service<LamaRiderService>()
        service.attach(lifetime, model)
        service.onCodeVisionClicked = { id -> AspectPopup.show(session.project, id) }
    }
}
```

- [ ] **Step 4: RefreshAction.kt**

```kotlin
package dev.lamarider

import com.intellij.icons.AllIcons
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.components.service
import com.intellij.openapi.project.DumbAwareAction

class RefreshAction : DumbAwareAction("Refresh Metalama Aspects", null, AllIcons.Actions.Refresh) {
    override fun actionPerformed(e: AnActionEvent) { e.project?.service<LamaRiderService>()?.refresh() }
}
```

- [ ] **Step 5: plugin.xml additions**

```xml
  <extensions defaultExtensionNs="com.intellij">
    <rd.solutionExtListener endpoint="IDE Frontend" listener="dev.lamarider.LamaRiderProtocolListener"/>
    <toolWindow id="Aspect Explorer" anchor="right" icon="AllIcons.Nodes.Annotationtype"
                factoryClass="dev.lamarider.explorer.AspectExplorerToolWindowFactory"/>
    <notificationGroup id="LamaRider" displayType="BALLOON"/>
  </extensions>
  <actions>
    <action id="LamaRider.Refresh" class="dev.lamarider.RefreshAction"/>
    <action id="LamaRider.MetalamaDiff" class="dev.lamarider.diff.MetalamaDiffAction" text="Metalama Diff"
            description="Compare source with Metalama-transformed code">
      <add-to-group group-id="EditorPopupMenu" anchor="last"/>
      <add-to-group group-id="SolutionExplorerPopupMenu" anchor="last"/>
      <keyboard-shortcut keymap="Visual Studio" first-keystroke="control K" second-keystroke="0"/>
    </action>
  </actions>
```

(If `SolutionExplorerPopupMenu` is not a valid group id in 263, use `ProjectViewPopupMenu`; check with `Help | Find Action` → UI inspector, or grep Rider's `plugin.xml` for `SolutionExplorerPopupMenu`.)

- [ ] **Step 6:** Build only after Tasks 10–11 add the referenced classes. Commit together with Task 10.

---

### Task 10: Frontend — Aspect Explorer + Code Vision popup

**Files:**
- Create: `src/rider/main/kotlin/dev/lamarider/explorer/AspectExplorerToolWindowFactory.kt`, `explorer/AspectExplorerPanel.kt`, `AspectPopup.kt`

**Interfaces:**
- Consumes: `LamaRiderService` (Task 9), `MetalamaDiff.open(project, file, offset)` (Task 11).
- Produces: `AspectExplorerPanel.select(aspectClassId: String)`; `AspectPopup.show(project, docId)`; `Navigation.open(project, location)`.

- [ ] **Step 1: Factory**

```kotlin
package dev.lamarider.explorer

import com.intellij.openapi.project.DumbAware
import com.intellij.openapi.project.Project
import com.intellij.openapi.wm.ToolWindow
import com.intellij.openapi.wm.ToolWindowFactory
import com.intellij.ui.content.ContentFactory

class AspectExplorerToolWindowFactory : ToolWindowFactory, DumbAware {
    override fun createToolWindowContent(project: Project, toolWindow: ToolWindow) {
        val panel = AspectExplorerPanel(project, toolWindow.disposable)
        toolWindow.contentManager.addContent(ContentFactory.getInstance().createContent(panel, "", false))
    }

    companion object {
        const val ID = "Aspect Explorer"
    }
}
```

- [ ] **Step 2: Panel**

```kotlin
package dev.lamarider.explorer

import com.intellij.ide.DefaultTreeExpander
import com.intellij.ide.actions.CollapseAllAction
import com.intellij.ide.actions.ExpandAllAction
import com.intellij.openapi.Disposable
import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.actionSystem.DefaultActionGroup
import com.intellij.openapi.components.service
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.SimpleToolWindowPanel
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.fileEditor.OpenFileDescriptor
import com.intellij.ui.*
import com.intellij.ui.components.JBCheckBox
import com.intellij.ui.components.JBLabel
import com.intellij.ui.components.JBList
import com.intellij.ui.components.JBScrollPane
import com.intellij.ui.treeStructure.Tree
import dev.lamarider.*
import dev.lamarider.diff.MetalamaDiff
import java.awt.BorderLayout
import java.awt.event.MouseAdapter
import java.awt.event.MouseEvent
import javax.swing.*
import javax.swing.tree.DefaultMutableTreeNode
import javax.swing.tree.DefaultTreeModel

class AspectExplorerPanel(private val project: Project, parent: Disposable) : SimpleToolWindowPanel(true, true) {
    private val service = project.service<LamaRiderService>()
    private val search = SearchTextField(false)
    private val includeLibraries = JBCheckBox("Include library aspects")
    private val classes = JBList<AspectClassInfo>()
    private val root = DefaultMutableTreeNode()
    private val tree = Tree(DefaultTreeModel(root))
    private val status = JBLabel()

    init {
        classes.cellRenderer = SimpleListCellRenderer.create("") { c ->
            "${c.name}  (${service.model?.instancesOf(c.id)?.size ?: 0})" + if (c.isLibrary) "  — library" else "  — ${c.project}"
        }
        classes.addListSelectionListener { if (!it.valueIsAdjusting) rebuildTree() }
        search.addDocumentListener(object : DocumentAdapter() { override fun textChanged(e: javax.swing.event.DocumentEvent) = rebuildClasses() })
        includeLibraries.addActionListener { rebuildClasses() }
        tree.isRootVisible = false
        tree.addMouseListener(object : MouseAdapter() {
            override fun mouseClicked(e: MouseEvent) {
                if (e.clickCount == 2) activate((tree.lastSelectedPathComponent as? DefaultMutableTreeNode)?.userObject)
            }
        })

        val top = JPanel(BorderLayout()).apply {
            add(JPanel(BorderLayout()).apply { add(search, BorderLayout.CENTER); add(includeLibraries, BorderLayout.EAST) }, BorderLayout.NORTH)
            add(JBScrollPane(classes), BorderLayout.CENTER)
        }
        val splitter = OnePixelSplitter(true, 0.4f).apply { firstComponent = top; secondComponent = JBScrollPane(tree) }
        setContent(JPanel(BorderLayout()).apply { add(splitter, BorderLayout.CENTER); add(status, BorderLayout.SOUTH) })

        val expander = DefaultTreeExpander(tree)
        val actions = DefaultActionGroup(
            ActionManager.getInstance().getAction("LamaRider.Refresh"),
            ExpandAllAction { expander }, CollapseAllAction { expander })
        toolbar = ActionManager.getInstance().createActionToolbar("AspectExplorer", actions, true).apply { targetComponent = this@AspectExplorerPanel }.component

        service.addListener(parent) { rebuildClasses() }
        current = this
        com.intellij.openapi.util.Disposer.register(parent) { if (current === this) current = null }
        rebuildClasses()
    }

    fun select(aspectClassId: String) {
        search.text = ""
        if (service.model?.aspectClass(aspectClassId)?.isLibrary == true) includeLibraries.isSelected = true
        rebuildClasses()
        val index = (0 until classes.model.size).firstOrNull { classes.model.getElementAt(it).id == aspectClassId } ?: return
        classes.selectedIndex = index
        classes.ensureIndexIsVisible(index)
    }

    private fun rebuildClasses() {
        status.text = service.status
        val selected = classes.selectedValue?.id
        val filter = search.text.trim()
        val items = service.model?.aspectClasses.orEmpty()
            .filter { includeLibraries.isSelected || !it.isLibrary }
            .filter { filter.isEmpty() || it.name.contains(filter, ignoreCase = true) }
        classes.setListData(items.toTypedArray())
        items.indexOfFirst { it.id == selected }.takeIf { it >= 0 }?.let { classes.selectedIndex = it }
        rebuildTree()
    }

    // Project › Namespace › Type › Member, children: transformations + diagnostics
    private fun rebuildTree() {
        root.removeAllChildren()
        val model = service.model
        val aspect = classes.selectedValue
        if (model != null && aspect != null) {
            for ((projectName, inProject) in model.instancesOf(aspect.id).groupBy { it.project }.toSortedMap()) {
                val projectNode = DefaultMutableTreeNode(projectName).also(root::add)
                for ((ns, inNs) in inProject.groupBy { it.targetType.substringBeforeLast('.', "<global>") }.toSortedMap()) {
                    val nsNode = DefaultMutableTreeNode(ns).also(projectNode::add)
                    for ((type, inType) in inNs.groupBy { it.targetType.substringAfterLast('.') }.toSortedMap()) {
                        val typeNode = DefaultMutableTreeNode(type).also(nsNode::add)
                        for (instance in inType.sortedBy { it.targetName }) {
                            val node = DefaultMutableTreeNode(TargetNode(instance)).also(typeNode::add)
                            model.transformationsOf(instance).forEach { node.add(DefaultMutableTreeNode(TransformationNode(it))) }
                            model.diagnosticsOn(instance.target).forEach { node.add(DefaultMutableTreeNode(DiagnosticNode(it))) }
                        }
                    }
                }
            }
        }
        (tree.model as DefaultTreeModel).reload()
        TreeUtil.expandAll(tree)
    }

    private fun activate(node: Any?) {
        when (node) {
            is TargetNode -> Navigation.open(project, node.instance.location)
            is TransformationNode -> node.transformation.location?.let { MetalamaDiff.open(project, it.file, it.start) }
            is DiagnosticNode -> node.diagnostic.file?.let { Navigation.openLine(project, it, (node.diagnostic.line ?: 1) - 1) }
        }
    }

    private class TargetNode(val instance: AspectInstanceInfo) { override fun toString() = "${instance.targetName}  [${instance.appliedVia}]" }
    private class TransformationNode(val transformation: TransformationInfo) { override fun toString() = transformation.description }
    private class DiagnosticNode(val diagnostic: DiagnosticInfo) { override fun toString() = "${diagnostic.severity} ${diagnostic.id}: ${diagnostic.message}" }

    companion object {
        var current: AspectExplorerPanel? = null; private set
    }
}

object Navigation {
    fun open(project: Project, location: SourceLocation?) {
        val file = location?.let { LocalFileSystem.getInstance().findFileByPath(it.file) } ?: return
        OpenFileDescriptor(project, file, location.start).navigate(true)
    }

    fun openLine(project: Project, path: String, line: Int) {
        val file = LocalFileSystem.getInstance().findFileByPath(path) ?: return
        OpenFileDescriptor(project, file, line, 0).navigate(true)
    }
}
```

(Add `import com.intellij.util.ui.tree.TreeUtil`.)

- [ ] **Step 3: AspectPopup.kt** — Code Vision click

```kotlin
package dev.lamarider

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.service
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.popup.JBPopupFactory
import com.intellij.openapi.wm.ToolWindowManager
import dev.lamarider.diff.MetalamaDiff
import dev.lamarider.explorer.AspectExplorerPanel
import dev.lamarider.explorer.AspectExplorerToolWindowFactory
import dev.lamarider.explorer.Navigation

object AspectPopup {
    fun show(project: Project, docId: String) = ApplicationManager.getApplication().invokeLater {
        val model = project.service<LamaRiderService>().model ?: return@invokeLater
        if (model.aspectClass(docId) != null && model.instancesOn(docId).isEmpty()) {
            showInExplorer(project, docId)
            return@invokeLater
        }
        val choices = mutableListOf<Pair<String, () -> Unit>>()
        val first = model.instancesOn(docId).firstOrNull()
        first?.location?.let { loc -> choices += "Preview Transformed Code" to { MetalamaDiff.open(project, loc.file, loc.start) } }
        for (instance in model.instancesOn(docId)) {
            val cls = model.aspectClass(instance.aspectClass)
            val name = cls?.name ?: instance.aspectClass.substringAfterLast('.')
            choices += "Show $name in Aspect Explorer (${instance.appliedVia})" to { showInExplorer(project, instance.aspectClass) }
            cls?.location?.let { loc -> choices += "Go to $name" to { Navigation.open(project, loc) } }
        }
        if (model.aspectClass(docId) != null) choices += "Show targets in Aspect Explorer" to { showInExplorer(project, docId) }
        val editor = FileEditorManager.getInstance(project).selectedTextEditor ?: return@invokeLater

        JBPopupFactory.getInstance()
            .createPopupChooserBuilder(choices.map { it.first })
            .setTitle("Metalama Aspects")
            .setItemChosenCallback { label -> choices.first { it.first == label }.second() }
            .createPopup()
            .showInBestPositionFor(editor)
    }

    private fun showInExplorer(project: Project, aspectClassId: String) {
        ToolWindowManager.getInstance(project).getToolWindow(AspectExplorerToolWindowFactory.ID)?.activate {
            AspectExplorerPanel.current?.select(aspectClassId)
        }
    }
}
```

- [ ] **Step 4:** continue to Task 11 (MetalamaDiff referenced here), then build.

---

### Task 11: Frontend — Metalama Diff

**Files:**
- Create: `src/rider/main/kotlin/dev/lamarider/diff/MetalamaDiff.kt`, `diff/MetalamaDiffAction.kt`

**Interfaces:**
- Consumes: `LamaRiderService.transformedPathFor(file)`.
- Produces: `MetalamaDiff.open(project: Project, file: String, offset: Int)`; `MetalamaDiff.findObjFallback(file: String): String?`.

- [ ] **Step 1: MetalamaDiff.kt**

```kotlin
package dev.lamarider.diff

import com.intellij.diff.DiffContentFactory
import com.intellij.diff.DiffManager
import com.intellij.diff.requests.SimpleDiffRequest
import com.intellij.diff.util.DiffUserDataKeys
import com.intellij.diff.util.Side
import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.components.service
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Pair
import com.intellij.openapi.vfs.LocalFileSystem
import dev.lamarider.LamaRiderService
import java.io.File

object MetalamaDiff {
    fun open(project: Project, file: String, offset: Int = 0) {
        val source = LocalFileSystem.getInstance().findFileByPath(file) ?: return
        val transformedPath = project.service<LamaRiderService>().transformedPathFor(file) ?: findObjFallback(file)
        if (transformedPath == null) {
            NotificationGroupManager.getInstance().getNotificationGroup("LamaRider").createNotification(
                "No Metalama transformations in ${source.name}",
                "If aspects should apply here, build the solution or refresh the Aspect Explorer. " +
                    "Without the worker, set <code>&lt;MetalamaEmitCompilerTransformedFiles&gt;true&lt;/MetalamaEmitCompilerTransformedFiles&gt;</code> in the project.",
                NotificationType.INFORMATION).notify(project)
            return
        }
        val factory = DiffContentFactory.getInstance()
        val transformedText = File(transformedPath).readText().replace("\r\n", "\n")
        val request = SimpleDiffRequest(
            "Metalama Diff: ${source.name}",
            factory.create(project, source),
            factory.create(project, transformedText, source.fileType),
            "Source", "Transformed (read-only)")
        val line = FileDocumentManager.getInstance().getDocument(source)?.let { doc ->
            doc.getLineNumber(offset.coerceIn(0, doc.textLength))
        } ?: 0
        request.putUserData(DiffUserDataKeys.SCROLL_TO_LINE, Pair.create(Side.LEFT, line))
        DiffManager.getInstance().showDiff(project, request)
    }

    /** obj/<Configuration>/<tfm>/metalama/... written when MetalamaEmitCompilerTransformedFiles=true. */
    fun findObjFallback(file: String): String? {
        val source = File(file)
        var dir = source.parentFile
        while (dir != null && dir.listFiles { f -> f.extension == "csproj" }.isNullOrEmpty()) dir = dir.parentFile
        val obj = File(dir ?: return null, "obj").takeIf { it.isDirectory } ?: return null
        return obj.walkTopDown()
            .filter { it.isFile && it.name.equals(source.name, ignoreCase = true) && it.path.contains("${File.separator}metalama${File.separator}") }
            .maxByOrNull { it.lastModified() }?.path
    }
}
```

- [ ] **Step 2: MetalamaDiffAction.kt**

```kotlin
package dev.lamarider.diff

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.project.DumbAwareAction

class MetalamaDiffAction : DumbAwareAction() {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        e.presentation.isEnabledAndVisible = e.project != null && e.getData(CommonDataKeys.VIRTUAL_FILE)?.extension == "cs"
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE) ?: return
        val offset = e.getData(CommonDataKeys.EDITOR)?.caretModel?.offset ?: 0
        MetalamaDiff.open(project, file.path, offset)
    }
}
```

- [ ] **Step 3: Build** `./gradlew buildPlugin` — Expected: BUILD SUCCESSFUL.

- [ ] **Step 4: Commit** `feat(frontend): Aspect Explorer, Code Vision popup, Metalama Diff`

---

### Task 12: End-to-end check in Rider 2026.3 EAP6 + README

**Files:**
- Modify: `README.md`, `CHANGELOG.md`

- [ ] **Step 1: runIde** `./gradlew runIde`, open `samples/MetalamaSample/MetalamaSample.sln`, build it, then verify:
  - Aspect Explorer lists `LogAttribute (2)` and `IdAttribute (1)`; selecting `LogAttribute` shows `MetalamaSample › Calculator › Add [Attribute]` and `Multiply [Fabric]`; double-click navigates.
  - Code Vision above `Add`, `Multiply`, `Calculator` shows `1 aspect`; above `LogAttribute` shows `applied to 2 targets`. Clicking opens the popup / Explorer.
  - `LogAttribute.cs`: `meta` colored, `name` (compile-time variable) bold; *Settings › Editor › Color Scheme › Metalama T#* exists.
  - Right-click `Calculator.cs` › *Metalama Diff* opens diff with introduced `Id` and logging code.
  - Break the worker (rename `dotnet` on PATH is impractical — instead open a non-Metalama solution): Explorer status says `No Metalama projects`.
- [ ] **Step 2: Install zip** from `build/distributions/` into the installed Rider 2026.3 EAP6 via *Settings › Plugins › ⚙ › Install Plugin from Disk*, restart, repeat the Explorer + Diff checks.
- [ ] **Step 3: README** — what it does, requirements (.NET SDK on PATH, Metalama 2026.x), build (`./gradlew buildPlugin`), install from disk, limitations (refresh after build/manual; no PostSharp classic).
- [ ] **Step 4: Commit** `docs: README and changelog for 0.1.0`
