using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Application.Parts;
using JetBrains.Application.Threading;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.Features.SolutionBuilders;
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

    public AspectModelHost(Lifetime lifetime, ISolution solution, ISolutionBuilder solutionBuilder)
    {
        _lifetime = lifetime;
        _solution = solution;

        // <plugin>/dotnet/LamaRider.dll -> <plugin>/worker
        var pluginDir = Path.GetDirectoryName(Path.GetDirectoryName(typeof(AspectModelHost).Assembly.Location))!;
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LamaRider");
        _runner = new WorkerRunner(Path.Combine(pluginDir, "worker"), cache, typeof(AspectModelHost).Assembly.GetName().Version.ToString());

        _solution.Locks.ExecuteOrQueueEx(lifetime, "LamaRider.Init", () =>
        {
            RdModel.Refresh.Advise(lifetime, _ => RequestRefresh("manual"));
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
            if (_running)
            {
                _pending = true;
                return;
            }
            _running = true;
        }
        _ = Task.Run(() => RefreshLoop(reason)); // RefreshLoop logs its own failures
    }

    void RefreshLoop(string reason)
    {
        while (true)
        {
            try { RunOnce(reason); }
            catch (Exception e) { Logger.Error(e); }

            lock (_gate)
            {
                if (!_pending || !_lifetime.IsAlive)
                {
                    _running = false;
                    return;
                }
                _pending = false;
                reason = "queued";
            }
        }
    }

    void RunOnce(string reason)
    {
        var assets = _solution.GetAllProjects()
            .Select(p => p.ProjectFileLocation)
            .Where(p => !p.IsEmpty)
            .Select(p => p.Directory.Combine("obj").Combine("project.assets.json"))
            .Where(p => p.ExistsFile)
            .Select(p => File.ReadAllText(p.FullPath))
            .ToList();
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
            var model = _runner.Run(version, _solution.SolutionFilePath.FullPath, ct);
            _model = model;
            Publish(
                $"Refreshed {DateTime.Now:HH:mm} · {model.AspectClasses.Count} aspects / {model.Instances.Count} instances",
                Path.Combine(_runner.LastOutputDir, "model.json"));
            _solution.Locks.ExecuteOrQueueEx(_lifetime, "LamaRider.Invalidate",
                () => _solution.GetComponent<IDaemon>().Invalidate("LamaRider model refreshed"));
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
