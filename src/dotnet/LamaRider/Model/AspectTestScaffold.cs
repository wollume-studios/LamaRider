using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LamaRider.Model;

/// <summary>
/// Files for Metalama aspect (snapshot) tests: a test project referencing the aspect project, a test file per case
/// with a target that fits the aspect, and the snapshot (.t.cs) taken from the last test run.
/// See https://doc.metalama.net/conceptual/aspects/testing/aspect-testing.
/// </summary>
public static class AspectTestScaffold
{
    public enum TargetKind { Type, Method, FieldOrProperty, Parameter, Constructor, Event }

    /// <param name="aspectTypeArgument">T of the aspect's IAspect&lt;T&gt;, e.g. "IMethod".</param>
    public static TargetKind KindOf(string aspectTypeArgument) => aspectTypeArgument switch
    {
        "IMethod" => TargetKind.Method,
        "IFieldOrProperty" or "IProperty" or "IField" or "IFieldOrPropertyOrIndexer" or "IPropertyOrIndexer" => TargetKind.FieldOrProperty,
        "IParameter" => TargetKind.Parameter,
        "IConstructor" => TargetKind.Constructor,
        "IEvent" => TargetKind.Event,
        _ => TargetKind.Type,
    };

    /// <summary>Main test file: some code the aspect is applied to. Metalama compares its transformed code with the .t.cs snapshot.</summary>
    public static string TestFile(string testNamespace, string aspectNamespace, string aspectClass, bool isAttribute, TargetKind kind)
    {
        var shortName = aspectClass.EndsWith("Attribute", StringComparison.Ordinal) && aspectClass.Length > "Attribute".Length
            ? aspectClass.Substring(0, aspectClass.Length - "Attribute".Length)
            : aspectClass;
        var attr = isAttribute ? $"[{shortName}]" : "";
        var target = $"{shortName}Target";
        string Member(string line) => isAttribute ? $"    {attr}\n    {line}" : $"    {line}";

        var body = kind switch
        {
            TargetKind.Type => (isAttribute ? attr + "\n" : "") + $"public partial class {target}\n{{\n    public int Add(int a, int b) => a + b;\n}}\n",
            TargetKind.Method => $"public class {target}\n{{\n{Member("public int Add(int a, int b) => a + b;")}\n}}\n",
            TargetKind.FieldOrProperty => $"public class {target}\n{{\n{Member("public string Name { get; set; } = \"\";")}\n}}\n",
            TargetKind.Parameter => $"public class {target}\n{{\n    public string Greet({(isAttribute ? attr + " " : "")}string name) => \"Hello \" + name;\n}}\n",
            TargetKind.Constructor => $"public class {target}\n{{\n    private readonly int _value;\n\n{Member($"public {target}(int value) => _value = value;")}\n}}\n",
            TargetKind.Event => $"public class {target}\n{{\n{Member("public event EventHandler? Changed;")}\n}}\n",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var sb = new StringBuilder();
        sb.Append($"using {aspectNamespace};\n\n");
        sb.Append($"namespace {testNamespace};\n\n");
        sb.Append($"// Metalama aspect test for {aspectClass}: every .cs file in this project is a test case. The test compares the\n");
        sb.Append("// transformed code of this file with the .t.cs snapshot next to it. The first run fails and writes a placeholder\n");
        sb.Append("// snapshot: check the test output, then save it as the snapshot with Alt+Enter > Accept Metalama test output.\n");
        if (!isAttribute)
            sb.Append($"// {aspectClass} is not an attribute: add it with a fabric, see https://doc.metalama.net/conceptual/using/fabrics.\n");
        sb.Append('\n').Append(body);
        return sb.ToString();
    }

    /// <summary>"LogTest.cs", then "LogTest2.cs", ... (Metalama: no '.' in a main test file name besides the extension).</summary>
    public static string NextTestFileName(IEnumerable<string> existingFileNames, string aspectClass)
    {
        var shortName = aspectClass.EndsWith("Attribute", StringComparison.Ordinal) && aspectClass.Length > 9 ? aspectClass.Substring(0, aspectClass.Length - 9) : aspectClass;
        var existing = new HashSet<string>(existingFileNames, StringComparer.OrdinalIgnoreCase);
        for (var i = 1; ; i++)
        {
            var name = $"{shortName}Test{(i == 1 ? "" : i.ToString())}.cs";
            if (!existing.Contains(name)) return name;
        }
    }

    static readonly string[] Xunit2Packages = ["Microsoft.NET.Test.Sdk:17.12.0", "xunit:2.9.3", "xunit.runner.visualstudio:2.8.2"];
    static readonly string[] Xunit3Packages = ["Microsoft.NET.Test.Sdk:17.14.1", "xunit.v3:4.0.1", "xunit.runner.visualstudio:4.0.0"];

    /// <summary>Metalama 2027 aspect tests run on xUnit v3 (an executable); earlier versions on xUnit 2.</summary>
    static bool UsesXunit3(string metalamaVersion) =>
        int.TryParse(metalamaVersion.Split('.')[0], out var major) && major >= 2027;

    static IEnumerable<(string Id, string Version)> Packages(string metalamaVersion) =>
        (UsesXunit3(metalamaVersion) ? Xunit3Packages : Xunit2Packages)
        .Select(p => (p.Split(':')[0], p.Split(':')[1]))
        .Prepend(("Metalama.Testing.AspectTesting", metalamaVersion));

    /// <param name="aspectProject">Path of the aspect project relative to the test project's folder.</param>
    /// <param name="centralPackages">Central package management: versions go to Directory.Packages.props (<see cref="AddPackageVersions"/>).</param>
    public static string TestProject(string metalamaVersion, string aspectProject, string targetFramework, bool centralPackages)
    {
        var sb = new StringBuilder("<Project Sdk=\"Microsoft.NET.Sdk\">\n\n  <PropertyGroup>\n");
        if (UsesXunit3(metalamaVersion)) sb.Append("    <OutputType>Exe</OutputType>\n");
        sb.Append($"    <TargetFramework>{targetFramework}</TargetFramework>\n");
        sb.Append("    <Nullable>enable</Nullable>\n    <ImplicitUsings>enable</ImplicitUsings>\n    <IsPackable>false</IsPackable>\n");
        sb.Append("  </PropertyGroup>\n\n  <ItemGroup>\n");
        foreach (var (id, version) in Packages(metalamaVersion))
            sb.Append(centralPackages ? $"    <PackageReference Include=\"{id}\" />\n" : $"    <PackageReference Include=\"{id}\" Version=\"{version}\" />\n");
        sb.Append("  </ItemGroup>\n\n  <ItemGroup>\n");
        sb.Append($"    <ProjectReference Include=\"{aspectProject}\" />\n");
        sb.Append("  </ItemGroup>\n\n</Project>\n");
        return sb.ToString();
    }

    /// <summary>Adds PackageVersion items the test project needs to Directory.Packages.props; existing pins win.</summary>
    public static string AddPackageVersions(string propsText, string metalamaVersion)
    {
        var missing = Packages(metalamaVersion)
            .Where(p => !Regex.IsMatch(propsText, $"<PackageVersion\\s+Include=\"{Regex.Escape(p.Id)}\"", RegexOptions.IgnoreCase))
            .Select(p => $"    <PackageVersion Include=\"{p.Id}\" Version=\"{p.Version}\" />\n")
            .ToList();
        if (missing.Count == 0) return propsText;
        var end = propsText.LastIndexOf("</Project>", StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException("Directory.Packages.props has no </Project> element");
        return propsText.Substring(0, end) + "  <ItemGroup>\n" + string.Concat(missing) + "  </ItemGroup>\n" + propsText.Substring(end);
    }

    public static bool UsesCentralPackages(string? propsText) =>
        propsText != null && Regex.IsMatch(propsText, @"<ManagePackageVersionsCentrally>\s*true\s*</ManagePackageVersionsCentrally>", RegexOptions.IgnoreCase);

    public static bool IsAspectTestProject(string csprojText) =>
        csprojText.IndexOf("Metalama.Testing.AspectTesting", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Whether a project file references <paramref name="projectFileName"/> (e.g. "Sample.csproj").</summary>
    public static bool References(string csprojText, string projectFileName) =>
        Regex.Matches(csprojText, "<ProjectReference\\s+Include=\"([^\"]+)\"", RegexOptions.IgnoreCase).Cast<Match>()
            .Any(m => Path.GetFileName(m.Groups[1].Value.Replace('\\', '/')).Equals(projectFileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>First target framework of a project file, if it declares one.</summary>
    public static string? TargetFramework(string csprojText) =>
        Regex.Match(csprojText, "<TargetFrameworks?>\\s*([^;<\\s]+)") is { Success: true } m ? m.Groups[1].Value : null;

    public static string SnapshotPath(string testFile) => Path.ChangeExtension(testFile, ".t.cs");

    /// <summary>
    /// Output of the last run of <paramref name="testFile"/>: obj/transformed/&lt;tfm&gt;/&lt;path in project&gt;/Name.t.cs (newest).
    /// </summary>
    public static string? ActualOutput(string testProjectDir, string testFile)
    {
        var transformed = Path.Combine(testProjectDir, "obj", "transformed");
        if (!Directory.Exists(transformed)) return null;
        var relative = MakeRelative(testProjectDir, SnapshotPath(testFile));
        return Directory.GetDirectories(transformed)
            .Select(tfm => Path.Combine(tfm, relative))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    static string MakeRelative(string baseDir, string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : Path.GetFileName(full);
    }
}
