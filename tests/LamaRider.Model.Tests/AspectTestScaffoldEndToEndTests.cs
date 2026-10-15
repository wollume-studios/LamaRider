using System.Diagnostics;
using LamaRider.Model;

namespace LamaRider.Model.Tests;

/// <summary>The generated test runs in Metalama's test framework: first run writes the output, accepted it passes.</summary>
public class AspectTestScaffoldEndToEndTests : IDisposable
{
    readonly List<string> _copies = [];

    public void Dispose()
    {
        foreach (var copy in _copies)
            try { Directory.Delete(copy, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>What Create Metalama Aspect Test writes (Testing/AspectTestActions.cs).</summary>
    static string WriteTest(string testProject, string aspectClass, AspectTestScaffold.TargetKind kind)
    {
        var folderName = AspectTestScaffold.TestFolder(aspectClass);
        var folder = Path.Combine(testProject, folderName);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, AspectTestScaffold.NextTestFileName(Directory.GetFiles(folder).Select(Path.GetFileName)!, aspectClass));
        var csproj = Directory.GetFiles(testProject, "*.csproj").Single();
        var ns = AspectTestScaffold.TestNamespace(File.ReadAllText(csproj), Path.GetFileNameWithoutExtension(csproj), folderName);
        File.WriteAllText(file, AspectTestScaffold.TestFile(ns, "MetalamaSample", aspectClass, isAttribute: true, kind));
        return file;
    }

    static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void Generated_test_fails_without_snapshot_and_passes_once_accepted()
    {
        var sample = CopySample();
        var testProject = Path.Combine(sample, "MetalamaSample.AspectTests");
        // Next to the existing Log/Add.cs (namespace ...AspectTests.Log): all test inputs compile into one assembly.
        var test = WriteTest(testProject, "LogAttribute", AspectTestScaffold.TargetKind.Method);

        Assert.NotEqual(0, DotnetTest(testProject)); // no snapshot yet: Metalama writes a placeholder and fails the run
        var output = AspectTestScaffold.ActualOutput(testProject, test);
        Assert.NotNull(output);
        Assert.Contains("Entering Add", File.ReadAllText(output!));

        // Snapshot out of date (aspect changed): the test fails until the new output is accepted.
        File.WriteAllText(AspectTestScaffold.SnapshotPath(test), "// outdated");
        Assert.NotEqual(0, DotnetTest(testProject));
        File.Copy(AspectTestScaffold.ActualOutput(testProject, test)!, AspectTestScaffold.SnapshotPath(test), overwrite: true); // "Accept Metalama test output"
        Assert.Equal(0, DotnetTest(testProject));
    }

    [Fact]
    public void Generated_test_project_runs_a_generated_test()
    {
        var sample = CopySample();
        var testProject = Path.Combine(sample, "MetalamaSample.NewAspectTests");
        Directory.CreateDirectory(testProject);
        File.WriteAllText(Path.Combine(testProject, "MetalamaSample.NewAspectTests.csproj"),
            AspectTestScaffold.TestProject("2026.1.30", Path.Combine("..", "MetalamaSample", "MetalamaSample.csproj"), "net8.0", centralPackages: false));
        var logTest = WriteTest(testProject, "LogAttribute", AspectTestScaffold.TargetKind.Method);
        var idTest = WriteTest(testProject, "IdAttribute", AspectTestScaffold.TargetKind.Type);

        Assert.NotEqual(0, DotnetTest(testProject)); // first run: placeholder snapshots ("TODO: Replace this file..."), fails
        var logOutput = AspectTestScaffold.ActualOutput(testProject, logTest);
        Assert.True(logOutput != null, "No test output. dotnet test said:" + Environment.NewLine + LastOutput);
        Assert.Contains("Entering Add", File.ReadAllText(logOutput!));
        Assert.Contains("Guid Id", File.ReadAllText(AspectTestScaffold.ActualOutput(testProject, idTest)!));
        foreach (var test in new[] { logTest, idTest }) // "Accept Metalama test output"
            File.Copy(AspectTestScaffold.ActualOutput(testProject, test)!, AspectTestScaffold.SnapshotPath(test), overwrite: true);
        var code = DotnetTest(testProject);
        Assert.True(code == 0, "Second run failed:" + Environment.NewLine + LastOutput);
    }

    static string LastOutput = "";

    static int DotnetTest(string project)
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", $"test \"{project}\"")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        })!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        LastOutput = stdout.Result + stderr;
        return process.ExitCode;
    }

    string CopySample()
    {
        var source = Path.Combine(Root, "samples", "MetalamaSample");
        var target = Path.Combine(Path.GetTempPath(), "lamarider-scaffold-e2e", Guid.NewGuid().ToString("N"));
        _copies.Add(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or ".idea" || p.StartsWith("_ReSharper"))) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(target, relative))!);
            File.Copy(file, Path.Combine(target, relative));
        }
        return target;
    }
}
