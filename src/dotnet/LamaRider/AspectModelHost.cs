using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Application.Parts;
using JetBrains.Application.Threading;
using JetBrains.Application.UI.PopupLayout;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.Features.SolutionBuilders;
using JetBrains.ProjectModel.Tasks;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.Navigation;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Psi.Caches;
using JetBrains.ReSharper.Psi.Modules;
using JetBrains.ReSharper.Feature.Services.Protocol;
using JetBrains.Rider.Model;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.Util;
using LamaRider.Model;
using LamaRider.Testing;
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
    bool _running, _pending, _testSettingsChecked;
    volatile string? _usesAspectTesting; // "true"/"false" once the solution's packages were read
    FileSystemWatcher? _watcher;
    Timer? _debounce;
    volatile bool _autoBuild;
    readonly HashSet<string> _changedCompileTimeFiles = new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);
    volatile AspectModel? _model;

    public AspectModelHost(Lifetime lifetime, ISolution solution, ISolutionBuilder solutionBuilder, ISolutionLoadTasksScheduler loadTasks)
    {
        _lifetime = lifetime;
        _solution = solution;

        // <plugin>/dotnet/LamaRider.dll -> <plugin>/worker
        var pluginDir = Path.GetDirectoryName(Path.GetDirectoryName(typeof(AspectModelHost).Assembly.Location))!;
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LamaRider");
        _runner = new WorkerRunner(Path.Combine(pluginDir, "worker"), cache);
        lifetime.OnTermination(_runner.Dispose); // the serving worker process

        // Project model queries are not allowed while the solution loads.
        loadTasks.EnqueueTask(new SolutionLoadTask(typeof(AspectModelHost), "LamaRider.Init", SolutionLoadTaskKinds.AfterDone, () =>
        {
            RdModel.Refresh.Advise(lifetime, _ => RequestRefresh("manual"));
            var props = DebugTransformedCodeProps.Read(ReadProps());
            RdModel.DebugTransformedCode.Value = props.DebugTransformedCode;
            RdModel.FormatTransformedCode.Value = props.FormatOutput;
            RdModel.FormatCompileTimeCode.Value = props.FormatCompileTimeCode;
            RdModel.SetDebugTransformedCode.Advise(lifetime, enable => WriteProps(o => o with { DebugTransformedCode = enable }));
            RdModel.SetFormatTransformedCode.Advise(lifetime, format => WriteProps(o => o with { FormatOutput = format }));
            RdModel.SetFormatCompileTimeCode.Advise(lifetime, format => WriteProps(o => o with { FormatCompileTimeCode = format }));
            RdModel.SetHintOptions.Advise(lifetime, options =>
            {
                HintOptions = new AspectHints.Options(options.CountOnly ? AspectHints.Style.Count : AspectHints.Style.Named, options.TransformedCodeHint);
                _solution.GetComponent<IDaemon>().Invalidate("LamaRider hint options");
            });
            RdModel.ApplyTestSettings.Advise(lifetime, ApplyTestSettings);
            RdModel.RefreshTestSettingsStatus.Advise(lifetime, _ => UpdateTestSettingsStatus());
            RdModel.NavigateToClass.Advise(lifetime, NavigateToClass);
            RdModel.CreateAspectTest.Advise(lifetime, position => CreateAspectTest(position.File, position.Offset));
            RdModel.AddAspectsAsAttributes.Advise(lifetime, position => EditSource("LamaRider.AddAspectsAsAttributes", position, AddAspectsAsAttributes));
            RdModel.ReplaceWithTransformedCode.Advise(lifetime, position => EditSource("LamaRider.ReplaceWithTransformedCode", position, ReplaceWithTransformedCode));
            RdModel.WrapPremiumCode.Advise(lifetime, WrapPremiumCode);
            // Not under one read lock: applying a fix takes the write lock (FixSelfTest reads PSI under its own read locks).
            RdModel.SelfTestFixes.Advise(lifetime, path => _solution.Locks.ExecuteOrQueueEx(_lifetime, "LamaRider.SelfTestFixes", () =>
            {
                using var _ = CompilationContextCookie.GetExplicitUniversalContextIfNotSet();
                string report;
                try { report = Model is { } model ? Fixes.FixSelfTest.ToJson(Fixes.FixSelfTest.Run(_solution, model)) : "[]"; }
                catch (Exception e) when (e is not OperationCanceledException) { report = "[{\"Id\":\"SELFTEST\",\"Reported\":[],\"Fixes\":[],\"Error\":" + Newtonsoft.Json.JsonConvert.ToString(e.ToString()) + "}]"; }
                File.WriteAllText(path, report);
            }));
            RdModel.RebuildAndDebugAspectTest.Advise(lifetime, RebuildAndDebugAspectTest);
            RdModel.SetUpPremiumBuilds.Advise(lifetime, path => _solution.Locks.ExecuteOrQueueReadLockEx(_lifetime, "LamaRider.SetUpPremiumBuilds", () =>
                RdModel.Info.Fire(SourceFile(path)?.GetProject() is { } project ? Premium.PremiumUses.SetUpBuilds(project) : "Not a file of a project.")));
            RdModel.AcceptAspectTestOutput.Advise(lifetime, AcceptAspectTestOutput);
            RdModel.RunAspectTest.Advise(lifetime, run => RunAspectTest(run.File, run.Debug));
            RdModel.AutoBuild.Advise(lifetime, enabled => _autoBuild = enabled);
            RdModel.AddLamaDebugConfiguration.Advise(lifetime, _ => AddLamaDebugConfiguration());
            solutionBuilder.RunningRequest.Change.Advise(lifetime, args =>
            {
                if (args.HasOld && args.Old != null && args.HasNew && args.New == null) RequestRefresh("build finished"); // no New on shutdown
            });
            StartWatching(_solution.SolutionDirectory.FullPath);
            RequestRefresh("solution loaded");
        }));
    }

    public AspectModel? Model => _model;

    /// <summary>How Code Vision shows aspects (frontend setting, sent over rd).</summary>
    public AspectHints.Options HintOptions { get; private set; } = new();

    LamaRiderModel RdModel => _solution.GetProtocolSolution().GetLamaRiderModel();

    string PropsPath => Path.Combine(_solution.SolutionDirectory.FullPath, DebugTransformedCodeProps.FileName);

    string? ReadProps() => File.Exists(PropsPath) ? File.ReadAllText(PropsPath) : null;

    // ponytail: solution-level Directory.Build.props only; a project-level one stops MSBuild's upward search.
    void WriteProps(Func<DebugTransformedCodeProps.Options, DebugTransformedCodeProps.Options> change)
    {
        try
        {
            var current = ReadProps();
            var options = change(DebugTransformedCodeProps.Read(current));
            var updated = DebugTransformedCodeProps.Write(current, options);
            if (updated == null)
            {
                if (File.Exists(PropsPath)) File.Delete(PropsPath);
            }
            else if (updated != current) File.WriteAllText(PropsPath, updated);
            RdModel.DebugTransformedCode.Value = options.DebugTransformedCode;
            RdModel.FormatTransformedCode.Value = options.FormatOutput;
            RdModel.FormatCompileTimeCode.Value = options.FormatCompileTimeCode;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var actual = DebugTransformedCodeProps.Read(ReadProps());
            RdModel.DebugTransformedCode.Value = actual.DebugTransformedCode;
            RdModel.FormatTransformedCode.Value = actual.FormatOutput;
            RdModel.FormatCompileTimeCode.Value = actual.FormatCompileTimeCode;
            RdModel.Failed.Fire($"Cannot update {PropsPath}: {e.Message}");
        }
    }

    void ApplyTestSettings(string scope)
    {
        try
        {
            var where = _solution.GetComponent<MetalamaTestSettings>().Apply(scope);
            UpdateTestSettingsStatus();
            RdModel.Info.Fire($"Rider is configured for Metalama aspect tests in {where}: xUnit discovery by Test Runner, diff tools ignored as orphaned processes. Rebuild the test project.");
        }
        catch (Exception e)
        {
            Logger.Error(e);
            RdModel.Failed.Fire("Cannot apply Metalama test settings: " + e.Message);
        }
    }

    void UpdateTestSettingsStatus() =>
        _solution.Locks.ExecuteOrQueueReadLockEx(_lifetime, "LamaRider.TestSettingsStatus", () =>
        {
            var (configured, text) = _solution.GetComponent<MetalamaTestSettings>().Status(_usesAspectTesting == null ? null : _usesAspectTesting == "true");
            RdModel.TestSettingsConfigured.Value = configured;
            RdModel.TestSettingsStatus.Value = text;
        });

    public void NotifyCodeVisionClicked(string docId) =>
        _solution.Locks.ExecuteOrQueueEx(_lifetime, "LamaRider.Click", () => RdModel.CodeVisionClicked.Fire(docId));

    public void RequestRefresh(string reason)
    {
        Logger.Info($"LamaRider: refresh requested ({reason})");
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

    // Library aspects have no source in the solution: ReSharper opens SourceLink/symbol sources or decompiles.
    void NavigateToClass(string docId) =>
        _solution.Locks.ExecuteOrQueueReadLockEx(_lifetime, "LamaRider.NavigateToClass", () =>
        {
            var name = docId.StartsWith("T:", StringComparison.Ordinal) ? docId.Substring(2) : docId;
            using var _ = CompilationContextCookie.GetExplicitUniversalContextIfNotSet();
            var type = _solution.GetPsiServices().Symbols.GetSymbolScope(LibrarySymbolScope.FULL, caseSensitive: true)
                .GetElementsByQualifiedName(name).OfType<ITypeElement>().FirstOrDefault();
            if (type == null) RdModel.Info.Fire($"{name} was not found in the solution's references.");
            else _solution.GetComponent<DeclaredElementNavigationService>()
                .Navigate(type, _solution.GetComponent<IMainWindowPopupWindowContext>().Source, true);
        });

    // Editor menu twins of the Alt+Enter actions in Testing/AspectTestActions.cs.
    void CreateAspectTest(string path, int offset) =>
        _solution.Locks.ExecuteOrQueueReadLockEx(_lifetime, "LamaRider.CreateAspectTest", () =>
        {
            using var _ = CompilationContextCookie.GetExplicitUniversalContextIfNotSet(); // super types of the aspect
            var sourceFile = SourceFile(path);
            var psiFile = sourceFile?.GetDominantPsiFile<CSharpLanguage>();
            var node = psiFile?.FindNodeAt(psiFile.Translate(new DocumentOffset(sourceFile!.Document, offset)));
            var declaration = node?.GetContainingNode<IClassDeclaration>(returnThis: true);
            if (declaration?.DeclaredElement is not { } type || !Testing.AspectTests.IsAspect(type) || sourceFile!.GetProject() is not { } project)
            {
                RdModel.Info.Fire("Place the caret in an aspect class to create a Metalama aspect test for it.");
                return;
            }
            var (testCsproj, newProject) = Testing.AspectTests.Targets(_solution, project)[0];
            Testing.AspectTests.Create(_solution, type, project, testCsproj, newProject);
        });

    /// <summary>The editor menu twins of the Alt+Enter actions on the declaration whose name is at the position.</summary>
    void EditSource(string name, EditorPosition position, Action<AspectModel, IDeclaration, string> edit) =>
        _solution.Locks.ExecuteOrQueueReadLockEx(_lifetime, name, () =>
        {
            using var _ = CompilationContextCookie.GetExplicitUniversalContextIfNotSet();
            _solution.GetPsiServices().Files.CommitAllDocuments();
            var sourceFile = SourceFile(position.File);
            if (Model is { } model && sourceFile?.GetDominantPsiFile<CSharpLanguage>() is { } psiFile &&
                Actions.AspectTargets.At(psiFile, new DocumentOffset(sourceFile.Document, position.Offset)) is var (declaration, target))
                edit(model, declaration, target);
            else
                RdModel.Info.Fire("Place the caret on the name of a declaration with Metalama aspects.");
        });

    void WrapPremiumCode(EditorPosition position) =>
        _solution.Locks.ExecuteOrQueueReadLockEx(_lifetime, "LamaRider.WrapPremiumCode", () =>
        {
            using var _ = CompilationContextCookie.GetExplicitUniversalContextIfNotSet();
            _solution.GetPsiServices().Files.CommitAllDocuments();
            var sourceFile = SourceFile(position.File);
            var node = sourceFile?.GetDominantPsiFile<CSharpLanguage>() is { } psiFile
                ? psiFile.FindNodeAt(psiFile.Translate(new DocumentOffset(sourceFile.Document, position.Offset)))
                : null;
            if (node != null && Premium.PremiumUses.UseAt(node) is { } use)
                Premium.PremiumUses.Wrap(sourceFile!.Document, Premium.PremiumUses.Wraps(use));
            else
                RdModel.Info.Fire("No Metalama Premium API at the caret.");
        });

    void AddAspectsAsAttributes(AspectModel model, IDeclaration declaration, string target)
    {
        var candidates = Actions.AddAspectAsAttribute.Candidates(model, declaration, target);
        if (candidates.Count == 0)
        {
            RdModel.Info.Fire("No aspect here that a fabric, inheritance or another aspect adds and that can be written as an attribute.");
            return;
        }
        _solution.GetPsiServices().Transactions.Execute("LamaRider: add aspects as attributes", () =>
        {
            using (WriteLockCookie.Create())
                foreach (var candidate in candidates) Actions.AddAspectAsAttribute.Add(declaration, candidate);
        });
        RdModel.Info.Fire(string.Join("\n", candidates.Select(Actions.AddAspectAsAttribute.Done)));
    }

    void ReplaceWithTransformedCode(AspectModel model, IDeclaration declaration, string target)
    {
        if (Actions.ReplaceWithTransformedCode.Find(model, declaration, target) is { } replacement)
            Actions.ReplaceWithTransformedCode.Apply(_solution, replacement);
        else
            RdModel.Info.Fire("No transformed code for this declaration: it has no aspects, or the file changed since LamaRider analyzed it (save and wait a moment).");
    }

    /// <summary>Rider's build sees nothing to do after only Directory.Build.props changed (e.g. readable compile-time code).</summary>
    void RebuildAndDebugAspectTest(string path) =>
        _solution.Locks.ExecuteOrQueueReadLockEx(_lifetime, "LamaRider.RebuildAndDebugAspectTest", () =>
        {
            if (SourceFile(path)?.GetProject() is not { } project)
            {
                RdModel.Info.Fire("This file is not in a project.");
                return;
            }
            var builder = _solution.GetComponent<ISolutionBuilder>();
            var request = builder.CreateBuildRequest(BuildSessionTarget.Rebuild, [project], SolutionBuilderRequestSilentMode.Default,
                new SolutionBuilderRequestAdvancedSettings());
            request.ContinueWith(_lifetime, _ => RunAspectTest(path, debug: true));
            builder.ExecuteBuildRequest(request);
        });

    void RunAspectTest(string path, bool debug) =>
        _solution.Locks.ExecuteOrQueueReadLockEx(_lifetime, "LamaRider.RunAspectTest", () =>
        {
            if (Testing.AspectTests.Run(_solution, SourceFile(path)?.ToProjectFile(), debug) is { } problem)
                RdModel.Info.Fire(problem);
        });

    void AcceptAspectTestOutput(string path) =>
        _solution.Locks.ExecuteOrQueueReadLockEx(_lifetime, "LamaRider.AcceptAspectTestOutput", () =>
        {
            if (Testing.AspectTests.OutputFor(SourceFile(path)?.GetProject(), path) is var (test, output))
                Testing.AspectTests.Accept(_solution, test, output);
            else
                RdModel.Info.Fire("No Metalama test output for this file: run the aspect test first.");
        });

    /// <summary>
    /// The file a frontend path names ("D:/x/y.cs" on Windows). A file can also be in Rider's Miscellaneous Files
    /// project, which has no project file: the one in a real project wins.
    /// </summary>
    IPsiSourceFile? SourceFile(string path)
    {
        var location = VirtualFileSystemPath.Parse(Path.GetFullPath(path), InteractionContext.SolutionContext);
        var files = _solution.FindProjectItemsByLocation(location).OfType<IProjectFile>().ToList();
        var file = files.FirstOrDefault(f => f.GetProject()?.ProjectFileLocation is { IsEmpty: false } csproj && csproj.ExistsFile) ?? files.FirstOrDefault();
        if (file == null) Logger.Warn($"LamaRider: {path} is in no project");
        return file?.ToSourceFiles().FirstOrDefault();
    }

    void AddLamaDebugConfiguration() =>
        _solution.Locks.ExecuteOrQueueReadLockEx(_lifetime, "LamaRider.AddLamaDebug", () =>
        {
            var path = _solution.SolutionFilePath.FullPath;
            var metalamaProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var location in _solution.GetAllProjects().Select(p => p.ProjectFileLocation).Where(p => !p.IsEmpty))
            {
                var assets = location.Directory.Combine("obj").Combine("project.assets.json").FullPath;
                if (File.Exists(assets) && MetalamaVersionDetector.FromAssetsFiles([File.ReadAllText(assets)]) != null)
                    metalamaProjects.Add(location.FullPath);
            }
            var directory = Path.GetDirectoryName(path)!;
            bool IsMetalama(string relative) =>
                metalamaProjects.Contains(Path.GetFullPath(Path.Combine(directory, relative.Replace('\\', Path.DirectorySeparatorChar))));
            try
            {
                // Keep the BOM Visual Studio writes into .sln files.
                var bytes = File.ReadAllBytes(path);
                var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                var text = new System.Text.UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
                var extension = Path.GetExtension(path).ToLowerInvariant();
                var updated = extension switch
                {
                    ".sln" => LamaDebugConfiguration.AddToSln(text, IsMetalama),
                    ".slnx" => LamaDebugConfiguration.AddToSlnx(text, IsMetalama),
                    _ => null,
                };
                if (updated == null)
                {
                    RdModel.Failed.Fire($"Cannot add LamaDebug to {Path.GetFileName(path)}: only .sln and .slnx files are supported.");
                    return;
                }
                if (updated != text) File.WriteAllText(path, updated, new System.Text.UTF8Encoding(bom));
                RdModel.Info.Fire($"Added the LamaDebug configuration to {Path.GetFileName(path)}. Once Rider has reloaded the solution, run Debug with LamaDebug again.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                RdModel.Failed.Fire($"Cannot add LamaDebug to {Path.GetFileName(path)}: {e.Message}");
            }
        });

    // Project paths need the read lock; the worker run must not hold it.
    void StartRun(string reason) =>
        _solution.Locks.ExecuteOrQueueReadLockEx(_lifetime, "LamaRider.CollectProjects", () =>
        {
            var projectFiles = _solution.GetAllProjects()
                .Select(p => p.ProjectFileLocation)
                .Where(p => !p.IsEmpty)
                .ToList();
            var assetsFiles = projectFiles.Select(p => p.Directory.Combine("obj").Combine("project.assets.json").FullPath).ToList();
            var solutionPath = _solution.SolutionFilePath.FullPath;
            var inputs = WorkspaceInputs.For(solutionPath, projectFiles.Select(p => p.FullPath).Distinct());
            _ = Task.Run(() => RunAndContinue(reason, assetsFiles, solutionPath, inputs));
        });

    void RunAndContinue(string reason, List<string> assetsFiles, string solutionPath, IReadOnlyList<string> inputs)
    {
        try { RunOnce(reason, assetsFiles, solutionPath, inputs); }
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

    // Refresh on its own: saved code/project files and restores (package changes), debounced.
    void StartWatching(string solutionDirectory)
    {
        _debounce = new Timer(_ => OnFilesSettled());
        _watcher = new FileSystemWatcher(solutionDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += (_, e) => OnFileChanged(e.FullPath);
        _watcher.Created += (_, e) => OnFileChanged(e.FullPath);
        _watcher.Deleted += (_, e) => OnFileChanged(e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            OnFileChanged(e.FullPath);
            OnFileChanged(e.OldFullPath);
        };
        _watcher.Error += (_, _) => _debounce.Change(Debounce, Timeout.InfiniteTimeSpan); // buffer overflow: just refresh
        _watcher.EnableRaisingEvents = true;
        _lifetime.OnTermination(() =>
        {
            _watcher.Dispose();
            _debounce.Dispose();
        });
    }

    void OnFileChanged(string path)
    {
        var kind = RefreshTrigger.Classify(path);
        if (kind == RefreshTrigger.Kind.None) return;
        Logger.Info($"LamaRider: {kind} change {path}");
        // The worker restores the solution itself; its own project.assets.json writes must not trigger another refresh.
        if (kind == RefreshTrigger.Kind.Restore)
            lock (_gate)
                if (_running) return;
        if (kind == RefreshTrigger.Kind.Source && _autoBuild && _model?.IsCompileTimeFile(path) == true)
            lock (_gate) _changedCompileTimeFiles.Add(path);
        _debounce?.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    // Auto-build (opt-in): saved aspect/template code is built, so Metalama resumes; the build's end refreshes the model.
    void OnFilesSettled()
    {
        string[] changed;
        lock (_gate)
        {
            changed = _changedCompileTimeFiles.ToArray();
            _changedCompileTimeFiles.Clear();
        }
        if (changed.Length == 0)
        {
            RequestRefresh("files changed");
            return;
        }
        _solution.Locks.ExecuteOrQueueReadLockEx(_lifetime, "LamaRider.AutoBuild", () =>
        {
            var projects = changed
                .SelectMany(f => _solution.FindProjectItemsByLocation(VirtualFileSystemPath.Parse(f, InteractionContext.SolutionContext)))
                .Select(i => i.GetProject())
                .Where(p => p != null)
                .Distinct()
                .ToList();
            if (projects.Count == 0) RequestRefresh("files changed");
            else Errors.MetalamaBuild.Run(_solution, projects!);
        });
    }

    void RunOnce(string reason, List<string> assetsFiles, string solutionPath, IReadOnlyList<string> inputs)
    {
        var assets = assetsFiles.Where(File.Exists).Select(File.ReadAllText).ToList();
        _usesAspectTesting = MetalamaVersionDetector.UsesAspectTesting(assets) ? "true" : "false";
        if (!_testSettingsChecked && MetalamaVersionDetector.UsesAspectTesting(assets))
        {
            _testSettingsChecked = true; // ask once per session
            _solution.Locks.ExecuteOrQueueReadLockEx(_lifetime, "LamaRider.TestSettings", () =>
            {
                if (!_solution.GetComponent<MetalamaTestSettings>().IsConfigured()) RdModel.TestSettingsSuggested.Fire(JetBrains.Core.Unit.Instance);
            });
        }

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
            var model = _runner.Run(version, solutionPath, inputs, ct);
            _model = model;
            Publish(
                $"Refreshed {DateTime.Now:HH:mm} · {model.AspectClasses.Count} aspects / {model.Instances.Count} instances{model.ErrorSummary}",
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
            Logger.Info($"LamaRider: {status}");
            RdModel.Status.Value = status;
            if (modelPath != null) RdModel.ModelPath.Value = modelPath;
        });
}
