using Metalama.Framework.Workspaces;

namespace LamaRider.Worker.Tests;

/// <summary>Diagnostics of a sample with an aspect error, located precisely enough for quick-fixes.</summary>
public class DiagnosticLocationTests : IAsyncLifetime
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "lamarider-tests", Guid.NewGuid().ToString("N"));
    WorkerOutput _output = null!;

    public async Task InitializeAsync()
    {
        var sample = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples/MetalamaSample"));
        foreach (var file in Directory.GetFiles(sample, "*", SearchOption.AllDirectories)
                     .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj" or "_ReSharper.Caches")))
        {
            var target = Path.Combine(_dir, Path.GetRelativePath(sample, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        // [Log] (an OverrideMethodAspect) is not eligible on an abstract method: LAMA0037 on the attribute.
        File.WriteAllText(Path.Combine(_dir, "MetalamaSample", "Bad.cs"),
            "namespace MetalamaSample;\n\npublic abstract class Bad\n{\n    [Log]\n    public abstract int Abs();\n}\n");
        using var workspace = await WorkspaceCollection.Default.LoadAsync(Path.Combine(_dir, "MetalamaSample.sln"));
        _output = Extractor.Extract(workspace, Path.Combine(_dir, "out"));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void A_diagnostic_has_its_span_and_one_based_line()
    {
        var diagnostic = _output.Diagnostics.Single(d => d.Id == "LAMA0037");
        var text = File.ReadAllText(Path.Combine(_dir, "MetalamaSample", "Bad.cs")).Replace("\r\n", "\n");
        Assert.Equal("Log", text[diagnostic.Start!.Value..diagnostic.End!.Value]);
        Assert.Equal(5, diagnostic.Line);
        Assert.Equal("M:MetalamaSample.Bad.Abs", diagnostic.Target);
    }
}
