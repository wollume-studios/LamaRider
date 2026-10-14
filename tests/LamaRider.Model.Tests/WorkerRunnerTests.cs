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
        var runner = new WorkerRunner(WorkerSrc, cache, "test");
        var ex = Assert.Throws<WorkerException>(() => runner.Run("2026.1.30", Path.Combine(cache, "missing.sln"), CancellationToken.None));
        Assert.Contains("missing.sln", ex.Message);
        Assert.True(File.Exists(runner.LogPath));
    }

    [Fact]
    public void Runs_worker_on_sample()
    {
        var runner = new WorkerRunner(WorkerSrc, NewCache(), "test");
        var model = runner.Run("2026.1.30", Path.Combine(Root, "samples/MetalamaSample/MetalamaSample.sln"), CancellationToken.None);
        Assert.Equal(2, model.TargetCountOf("T:MetalamaSample.LogAttribute"));
        Assert.True(File.Exists(Path.Combine(runner.LastOutputDir, "model.json")));
    }

    [Fact]
    public void Rejects_unsafe_metalama_version()
    {
        var cache = NewCache();
        var runner = new WorkerRunner(WorkerSrc, cache, "test");
        Assert.Throws<WorkerException>(() => runner.Run("../../evil", Path.Combine(cache, "x.sln"), CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(cache, "worker-src")));
    }
}
