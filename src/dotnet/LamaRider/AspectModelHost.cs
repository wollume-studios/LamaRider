using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Application.Parts;
using JetBrains.Application.Threading;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.Features.SolutionBuilders;
using JetBrains.ProjectModel.Tasks;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.Protocol;
using JetBrains.Rider.Model;
using JetBrains.Util;
using LamaRider.Model;
using LamaRider.Worker;

namespace LamaRider;

/// <summary>
/// Owns the aspect model of the open solution: runs the worker on solution load, after builds and on request
/// (single-flight, coalescing), publishes status and model path over rd and re-runs the daemon.
/// </summary>
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

    public AspectModelHost(Lifetime lifetime, ISolution solution, ISolutionBuilder solutionBuilder, ISolutionLoadTasksScheduler loadTasks)
    {
        _lifetime = lifetime;
        _solution = solution;

        // <plugin>/dotnet/LamaRider.dll -> <plugin>/worker
        var pluginDir = Path.GetDirectoryName(Path.GetDirectoryName(typeof(AspectModelHost).Assembly.Location))!;
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LamaRider");
        _runner = new WorkerRunner(Path.Combine(pluginDir, "worker"), cache);

        // Project model queries are not allowed while the solution loads.
        loadTasks.EnqueueTask(new SolutionLoadTask(typeof(AspectModelHost), "LamaRider.Init", SolutionLoadTaskKinds.AfterDone, () =>
        {
            RdModel.Refresh.Advise(lifetime, _ => RequestRefresh("manual"));
            RdModel.DebugTransformedCode.Value = DebugTransformedCodeProps.IsEnabled(ReadProps());
            RdModel.SetDebugTransformedCode.Advise(lifetime, SetDebugTransformedCode);
            solutionBuilder.RunningRequest.Change.Advise(lifetime, args =>
            {
                if (args.HasOld && args.Old != null && args.New == null) RequestRefresh("build finished");
            });
            RequestRefresh("solution loaded");
        }));
    }

    public AspectModel? Model => _model;

    LamaRiderModel RdModel => _solution.GetProtocolSolution().GetLamaRiderModel();

    string PropsPath => Path.Combine(_solution.SolutionDirectory.FullPath, DebugTransformedCodeProps.FileName);

    string? ReadProps() => File.Exists(PropsPath) ? File.ReadAllText(PropsPath) : null;

    // ponytail: solution-level Directory.Build.props only; a project-level one stops MSBuild's upward search.
    void SetDebugTransformedCode(bool enable)
    {
        try
        {
            var current = ReadProps();
            var updated = enable ? DebugTransformedCodeProps.Enable(current) : current == null ? null : DebugTransformedCodeProps.Disable(current);
            if (updated == null)
            {
                if (File.Exists(PropsPath)) File.Delete(PropsPath);
            }
            else if (updated != current) File.WriteAllText(PropsPath, updated);
            RdModel.DebugTransformedCode.Value = enable;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RdModel.DebugTransformedCode.Value = DebugTransformedCodeProps.IsEnabled(ReadProps());
            RdModel.Failed.Fire($"Cannot update {PropsPath}: {e.Message}");
        }
    }

    public void NotifyCodeVisionClicked(string docId) =>
        _solution.Locks.ExecuteOrQueueEx(_lifetime, "LamaRider.Click", () => RdModel.CodeVisionClicked.Fire(docId));

    public void RequestRefresh(string reason)
    {
        lock (_gate)
        {
            if (_running)
            {
                _pending = true;
                return;
            }
            _running = true;
        }
        StartRun(reason);
    }

    // Project paths need the read lock; the worker run must not hold it.
    void StartRun(string reason) =>
        _solution.Locks.ExecuteOrQueueReadLockEx(_lifetime, "LamaRider.CollectProjects", () =>
        {
            var assetsFiles = _solution.GetAllProjects()
                .Select(p => p.ProjectFileLocation)
                .Where(p => !p.IsEmpty)
                .Select(p => p.Directory.Combine("obj").Combine("project.assets.json").FullPath)
                .ToList();
            var solutionPath = _solution.SolutionFilePath.FullPath;
            _ = Task.Run(() => RunAndContinue(reason, assetsFiles, solutionPath));
        });

    void RunAndContinue(string reason, List<string> assetsFiles, string solutionPath)
    {
        try { RunOnce(reason, assetsFiles, solutionPath); }
        catch (Exception e) { Logger.Error(e); }

        lock (_gate)
        {
            if (!_pending || !_lifetime.IsAlive)
            {
                _running = false;
                return;
            }
            _pending = false;
        }
        StartRun("queued");
    }

    void RunOnce(string reason, List<string> assetsFiles, string solutionPath)
    {
        var assets = assetsFiles.Where(File.Exists).Select(File.ReadAllText).ToList();
        var version = MetalamaVersionDetector.FromAssetsFiles(assets);
        if (version == null)
        {
            Publish("No Metalama projects", null);
            return;
        }
        if (assets.Select(a => MetalamaVersionDetector.FromAssetsFiles(new[] { a })).Where(v => v != null).Distinct().Count() > 1)
            Logger.Warn($"LamaRider: mixed Metalama versions in solution, using {version}"); // ponytail: one worker per version if this matters

        Publish($"Refreshing ({reason})…", null);
        try
        {
            CancellationToken ct = _lifetime;
            var model = _runner.Run(version, solutionPath, ct);
            _model = model;
            Publish(
                $"Refreshed {DateTime.Now:HH:mm} · {model.AspectClasses.Count} aspects / {model.Instances.Count} instances",
                Path.Combine(_runner.LastOutputDir, "model.json"));
            _solution.Locks.ExecuteOrQueueEx(_lifetime, "LamaRider.Invalidate",
                () => _solution.GetComponent<IDaemon>().Invalidate("LamaRider model refreshed"));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var message = e.Message + " — log: " + _runner.LogPath;
            Publish((_model == null ? "" : "Stale · ") + message, null);
            _solution.Locks.ExecuteOrQueueEx(_lifetime, "LamaRider.Failed", () => RdModel.Failed.Fire(message));
        }
    }

    void Publish(string status, string? modelPath) =>
        _solution.Locks.ExecuteOrQueueEx(_lifetime, "LamaRider.Publish", () =>
        {
            RdModel.Status.Value = status;
            if (modelPath != null) RdModel.ModelPath.Value = modelPath;
        });
}
