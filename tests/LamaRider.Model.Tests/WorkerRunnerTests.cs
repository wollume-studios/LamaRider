using LamaRider.Worker;

namespace LamaRider.Model.Tests;

public class WorkerRunnerTests
{
    static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    static readonly string WorkerSrc = Path.Combine(Root, "src/worker/LamaRider.Worker");

    static string NewCache() => Path.Combine(Path.GetTempPath(), "lamarider-runner", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Failed_run_reports_stderr()
    {
        var cache = NewCache();
        var runner = new WorkerRunner(WorkerSrc, cache);
        var ex = Assert.Throws<WorkerException>(() => runner.Run("2026.1.30", Path.Combine(cache, "missing.sln"), CancellationToken.None));
        Assert.Contains("missing.sln", ex.Message);
        Assert.True(File.Exists(runner.LogPath));
    }

    [Fact]
    public void Runs_worker_on_sample()
    {
        var runner = new WorkerRunner(WorkerSrc, NewCache());
        var model = runner.Run("2026.1.30", Path.Combine(Root, "samples/MetalamaSample/MetalamaSample.sln"), CancellationToken.None);
        Assert.Equal(2, model.TargetCountOf("T:MetalamaSample.LogAttribute"));
        Assert.True(File.Exists(Path.Combine(runner.LastOutputDir, "model.json")));
    }

    [Fact]
    public void Later_runs_reuse_the_worker_process_and_see_file_changes()
    {
        // Copy of the sample: the second run must reload the edited file, not serve the first workspace again.
        var copy = Path.Combine(NewCache(), "sample");
        var sampleDir = Path.Combine(Root, "samples/MetalamaSample");
        foreach (var file in Directory.GetFiles(sampleDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sampleDir, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or ".idea" || p.StartsWith("_ReSharper"))) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(copy, relative))!);
            File.Copy(file, Path.Combine(copy, relative));
        }
        var sln = Path.Combine(copy, "MetalamaSample.sln");
        using var runner = new WorkerRunner(WorkerSrc, NewCache());
        Assert.Equal(2, runner.Run("2026.1.30", sln, CancellationToken.None).TargetCountOf("T:MetalamaSample.LogAttribute"));
        var firstProcess = runner.WorkerProcessId;

        var links = Path.Combine(copy, "MetalamaSample", "Links.cs");
        File.WriteAllText(links, File.ReadAllText(links).Replace("    public string Open(", "    [Log]\n    public string Open("));
        Assert.Equal(3, runner.Run("2026.1.30", sln, CancellationToken.None).TargetCountOf("T:MetalamaSample.LogAttribute"));
        Assert.Equal(firstProcess, runner.WorkerProcessId);

        // A new file (git checkout, another editor): the project's file globs must be evaluated again.
        File.WriteAllText(Path.Combine(copy, "MetalamaSample", "Added.cs"),
            "namespace MetalamaSample;\npublic class Added\n{\n    [Log]\n    public int Twice(int x) => 2 * x;\n}\n");
        Assert.Equal(4, runner.Run("2026.1.30", sln, CancellationToken.None).TargetCountOf("T:MetalamaSample.LogAttribute"));
    }

    [Fact]
    public void Rejects_unsafe_metalama_version()
    {
        var cache = NewCache();
        var runner = new WorkerRunner(WorkerSrc, cache);
        Assert.Throws<WorkerException>(() => runner.Run("../../evil", Path.Combine(cache, "x.sln"), CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(cache, "worker-src")));
    }

    [Fact]
    public void Locked_log_file_does_not_break_the_run()
    {
        var cache = NewCache();
        var runner = new WorkerRunner(WorkerSrc, cache);
        Directory.CreateDirectory(Path.GetDirectoryName(runner.LogPath)!);
        using var lockedLog = new FileStream(runner.LogPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<WorkerException>(() => runner.Run("2026.1.30", Path.Combine(cache, "missing.sln"), CancellationToken.None));
    }

    [Fact]
    public void Build_failure_reports_the_dotnet_error()
    {
        var runner = new WorkerRunner(WorkerSrc, NewCache());
        var ex = Assert.Throws<WorkerException>(() => runner.Run("1999.1.1", Path.Combine(Root, "samples/MetalamaSample/MetalamaSample.sln"), CancellationToken.None));
        Assert.Contains(": error ", ex.Message); // NuGet may fall back to a nearby version; either way the error line must surface
    }

    [Fact]
    public void Worker_cache_key_follows_worker_source()
    {
        var copy = Path.Combine(NewCache(), "src");
        Directory.CreateDirectory(copy);
        foreach (var f in Directory.GetFiles(WorkerSrc)) File.Copy(f, Path.Combine(copy, Path.GetFileName(f)));
        var before = WorkerRunner.SourceKey(copy);
        File.AppendAllText(Path.Combine(copy, "Program.cs"), "// changed");
        Assert.NotEqual(before, WorkerRunner.SourceKey(copy));
        Assert.Equal(WorkerRunner.SourceKey(WorkerSrc), WorkerRunner.SourceKey(WorkerSrc));
    }

    [Fact]
    public void Runs_worker_on_project_files_when_the_solution_format_is_not_loadable()
    {
        var runner = new WorkerRunner(WorkerSrc, NewCache());
        var project = Path.Combine(Root, "samples/MetalamaSample/MetalamaSample/MetalamaSample.csproj");
        var model = runner.Run("2026.1.30", Path.Combine(Root, "samples/MetalamaSample/MetalamaSample.slnx"), [project], CancellationToken.None);
        Assert.Equal(2, model.TargetCountOf("T:MetalamaSample.LogAttribute"));
    }
}
