using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JetBrains.Application.Progress;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Bulbs;
using JetBrains.ReSharper.Feature.Services.ContextActions;
using JetBrains.ReSharper.Feature.Services.CSharp.ContextActions;
using JetBrains.ReSharper.Feature.Services.Intentions;
using JetBrains.ReSharper.Feature.Services.Protocol;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.Util;
using JetBrains.Rider.Model;
using JetBrains.ReSharper.UnitTestFramework;
using JetBrains.ReSharper.UnitTestFramework.Criteria;
using JetBrains.ReSharper.UnitTestFramework.Execution.Hosting;
using JetBrains.ReSharper.UnitTestFramework.Execution.Launch;
using JetBrains.TextControl;
using JetBrains.Util;
using LamaRider.Model;

namespace LamaRider.Testing;

/// <summary>
/// Metalama aspect tests for an aspect class: added to each aspect test project referencing the aspect's project, or to a new
/// "&lt;Project&gt;.AspectTests" next to it; and accepting a test's output as its .t.cs snapshot. Used by the Alt+Enter actions
/// below and by the editor menu actions (via the protocol).
/// </summary>
public static class AspectTests
{
    const string AspectInterface = "Metalama.Framework.Aspects.IAspect`1";

    public static bool IsAspect(ITypeElement type) => type.GetAllSuperTypes().Any(t => t.GetClrName().FullName == AspectInterface);

    /// <summary>Where a test can go: existing aspect test projects referencing the aspect's project, else a new one.</summary>
    public static IReadOnlyList<(string TestCsproj, bool NewProject)> Targets(ISolution solution, IProject aspectProject)
    {
        var aspectCsproj = aspectProject.ProjectFileLocation.FullPath;
        var existing = solution.GetAllProjects()
            .Select(p => p.ProjectFileLocation.FullPath)
            .Where(path => File.Exists(path) && path != aspectCsproj)
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return AspectTestScaffold.IsAspectTestProject(text) && AspectTestScaffold.References(text, Path.GetFileName(aspectCsproj));
            })
            .Select(path => (path, false))
            .ToList();
        if (existing.Count > 0) return existing;
        var name = aspectProject.Name + ".AspectTests";
        return [(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(aspectCsproj)!)!, name, name + ".csproj"), true)];
    }

    public static string Describe(string testCsproj, bool newProject, string aspectName) => newProject
        ? $"Create aspect test project '{Path.GetFileNameWithoutExtension(testCsproj)}' with a test for {aspectName}"
        : $"Add Metalama aspect test for {aspectName} to '{Path.GetFileNameWithoutExtension(testCsproj)}'";

    /// <summary>Reads the aspect (call under a read lock), then writes files and runs dotnet in the background.</summary>
    public static void Create(ISolution solution, ITypeElement aspect, IProject aspectProject, string testCsproj, bool newProject)
    {
        var superTypes = aspect.GetAllSuperTypes().ToList();
        var aspectInterface = superTypes.First(t => t.GetClrName().FullName == AspectInterface);
        var argument = aspectInterface.GetTypeElement() is { TypeParameters.Count: 1 } generic
            ? (aspectInterface.GetSubstitution()[generic.TypeParameters[0]] as IDeclaredType)?.GetClrName().ShortName
            : null;
        var kind = AspectTestScaffold.KindOf(argument ?? "");
        var isAttribute = superTypes.Any(t => t.GetClrName().FullName == "System.Attribute");
        var aspectNamespace = aspect.GetContainingNamespace().QualifiedName;
        var aspectName = aspect.ShortName;
        var aspectCsproj = aspectProject.ProjectFileLocation.FullPath;
        var solutionPath = solution.SolutionFilePath.FullPath;
        var model = solution.GetProtocolSolution().GetLamaRiderModel();

        Task.Run(() =>
        {
            try
            {
                var testDir = Path.GetDirectoryName(testCsproj)!;
                var testProjectName = Path.GetFileNameWithoutExtension(testCsproj);
                if (newProject) CreateProject(solutionPath, aspectCsproj, testCsproj);

                var folderName = AspectTestScaffold.TestFolder(aspectName);
                var folder = Path.Combine(testDir, folderName);
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, AspectTestScaffold.NextTestFileName(Directory.GetFiles(folder).Select(Path.GetFileName), aspectName));
                var testNamespace = AspectTestScaffold.TestNamespace(File.ReadAllText(testCsproj), testProjectName, folderName);
                File.WriteAllText(file, AspectTestScaffold.TestFile(testNamespace, aspectNamespace, aspectName, isAttribute, kind));
                OnShell(solution, () => model.OpenFile.Fire(file));

                // Rider lists the test cases of an aspect test project only once it has been built.
                var (code, output) = Run("dotnet", $"build \"{testCsproj}\"", testDir);
                var created = newProject ? $"Created {testProjectName} and the test {Path.GetFileName(file)}." : $"Added the test {Path.GetFileName(file)}.";
                OnShell(solution, () => model.Info.Fire(code == 0
                    ? created + " Run or debug it from the Unit Tests window (debugging stops at Debugger.Break() in BuildAspect and meta.DebugBreak() in templates). The first run fails and writes the output; accept it with Alt+Enter > Accept Metalama test output."
                    : created + " Building it failed, so Rider does not list the test yet: " + LastLine(output)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                OnShell(solution, () => model.Failed.Fire("Cannot create the aspect test: " + e.Message));
            }
        });
    }

    /// <summary>The test file and its last output, when <paramref name="path"/> is a test in an aspect test project that ran.</summary>
    public static (string Test, string Output)? OutputFor(IProject? project, string path)
    {
        if (path.EndsWith(".t.cs", StringComparison.OrdinalIgnoreCase) || project == null) return null;
        var csproj = project.ProjectFileLocation.FullPath;
        if (!File.Exists(csproj) || !AspectTestScaffold.IsAspectTestProject(File.ReadAllText(csproj))) return null;
        var output = AspectTestScaffold.ActualOutput(Path.GetDirectoryName(csproj)!, path);
        return output == null ? null : (path, output);
    }

    /// <summary>Runs or debugs the aspect test of <paramref name="file"/> in Rider's unit test runner; the reason when it can't.</summary>
    public static string? Run(ISolution solution, IProjectFile? file, bool debug)
    {
        var project = file?.GetProject();
        var csproj = project?.ProjectFileLocation.FullPath;
        if (file == null || project == null || !File.Exists(csproj) || !AspectTestScaffold.IsAspectTestProject(File.ReadAllText(csproj)))
            return "This file is not in a Metalama aspect test project.";

        // Discovery from metadata does not see aspect tests (they are found by running Metalama's test framework).
        var settings = solution.GetComponent<MetalamaTestSettings>();
        string? note = null;
        if (!settings.IsConfigured())
            note = $"Rider finds aspect tests only with Test Runner discovery: turned on in {settings.Apply(MetalamaTestSettings.Personal)}. " +
                   "Settings › Tools › LamaRider can share it with the team.";

        var facade = solution.GetComponent<IUnitTestingFacade>();
        var relative = file.Location.MakeRelativeTo(project.Location).FullPath;
        // Aspect tests are found by running the test runner (not in source): a criterion, not elements, lets Rider build
        // the project and discover them first when it has not yet.
        var criterion = new ProjectCriterion(project).And(new TestIdCriterion(AspectTestScaffold.TestId(project.Name, relative)));
        var elements = facade.ElementRepository.Query(new ProjectCriterion(project)).ToList();
        var known = elements.Where(e => !e.Children.Any() && AspectTestScaffold.IsTestFor(e.ShortName, e.NaturalId.TestId, relative)).ToList();
        if (known.Count == 0 && elements.Count > 0)
            JetBrains.Util.Logging.Logger.GetLogger(typeof(AspectTests)).Warn($"LamaRider: no test element for {relative} among: " +
                string.Join(", ", elements.Take(20).Select(e => $"{e.ShortName} [{e.NaturalId.TestId}]")));

        var host = UnitTestHost.Instance.GetProvider(debug ? WellKnownHostProvidersIds.DebugProviderId : WellKnownHostProvidersIds.RunProviderId);
        facade.Run(known.Count > 0 ? new UnitTestElements(new TestElementCriterion(known), known) : new UnitTestElements(criterion))
            .Using(host, null, null, null, false)
            .AndOpenWindow(true)
            .In.CurrentOrNewSession(file.Name);
        return note;
    }

    public static void Accept(ISolution solution, string test, string output)
    {
        var model = solution.GetProtocolSolution().GetLamaRiderModel();
        try
        {
            var snapshot = AspectTestScaffold.SnapshotPath(test);
            File.Copy(output, snapshot, overwrite: true);
            model.OpenFile.Fire(snapshot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            model.Failed.Fire("Cannot save the test snapshot: " + e.Message);
        }
    }

    // rd signals must fire on the shell thread, not on the thread pool.
    static void OnShell(ISolution solution, Action fire) =>
        solution.Locks.ExecuteOrQueueEx(solution.GetSolutionLifetimes().UntilSolutionCloseLifetime, "LamaRider.AspectTest", fire);

    static void CreateProject(string solutionPath, string aspectCsproj, string testCsproj)
    {
        if (File.Exists(testCsproj)) throw new InvalidOperationException($"{testCsproj} already exists but does not reference {Path.GetFileName(aspectCsproj)}");
        var aspectDir = Path.GetDirectoryName(aspectCsproj)!;
        var assets = Path.Combine(aspectDir, "obj", "project.assets.json");
        var version = File.Exists(assets) ? MetalamaVersionDetector.FromAssetsFiles([File.ReadAllText(assets)]) : null;
        if (version == null || !MetalamaVersionDetector.IsSafe(version))
            throw new InvalidOperationException($"No Metalama version found for {Path.GetFileName(aspectCsproj)}: restore or build it first");

        var props = FindUp(aspectDir, Path.GetDirectoryName(solutionPath)!, "Directory.Packages.props");
        var central = props != null && AspectTestScaffold.UsesCentralPackages(File.ReadAllText(props));
        var framework = AspectTestScaffold.TargetFramework(File.ReadAllText(aspectCsproj)) ?? "net8.0";

        var testDir = Path.GetDirectoryName(testCsproj)!;
        Directory.CreateDirectory(testDir);
        File.WriteAllText(testCsproj, AspectTestScaffold.TestProject(version, MakeRelative(testDir, aspectCsproj), framework, central));
        if (central) File.WriteAllText(props!, AspectTestScaffold.AddPackageVersions(File.ReadAllText(props!), version));

        var (code, output) = Run("dotnet", $"sln \"{solutionPath}\" add \"{testCsproj}\"", Path.GetDirectoryName(solutionPath)!);
        if (code != 0) throw new InvalidOperationException($"'dotnet sln add' failed, add {Path.GetFileName(testCsproj)} to the solution by hand: {output.Trim()}");
    }

    static string? FindUp(string from, string stopAt, string fileName)
    {
        for (var dir = new DirectoryInfo(from); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
            if (string.Equals(dir.FullName.TrimEnd(Path.DirectorySeparatorChar), stopAt.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
        }
        return null;
    }

    static string MakeRelative(string fromDir, string path)
    {
        var from = new Uri(fromDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        return Uri.UnescapeDataString(from.MakeRelativeUri(new Uri(path)).ToString()).Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>The first error of a build's output, or its last line.</summary>
    static string LastLine(string output)
    {
        var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l != "").ToList();
        return lines.FirstOrDefault(l => l.Contains(" error ")) ?? lines.LastOrDefault() ?? "";
    }

    static (int Code, string Output) Run(string file, string arguments, string workingDir)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            WorkingDirectory = workingDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(); } catch (Exception) { /* exited meanwhile */ }
            return (-1, "timed out");
        }
        return (process.ExitCode, stdout.Result + stderr);
    }
}

/// <summary>Alt+Enter on an aspect class: one item per place the test can go (see <see cref="AspectTests"/>).</summary>
[ContextAction(GroupType = typeof(CSharpContextActions), Name = "Create Metalama aspect test",
    Description = "Adds a Metalama aspect test for this aspect, creating an aspect test project when there is none.")]
public sealed class CreateAspectTestAction(ICSharpContextActionDataProvider provider) : IContextAction
{
    public IEnumerable<IntentionAction> CreateBulbItems()
    {
        if (Aspect() is not var (type, project)) return [];
        return AspectTests.Targets(provider.Solution, project)
            .Select(t => (BulbActionBase)new Item(provider.Solution, type, project, t.TestCsproj, t.NewProject))
            .ToContextActionIntentions();
    }

    public bool IsAvailable(IUserDataHolder cache) => Aspect() != null;

    (ITypeElement Type, IProject Project)? Aspect()
    {
        var declaration = provider.GetSelectedElement<IClassDeclaration>();
        if (declaration?.DeclaredElement is not { } type || provider.Project is not { } project) return null;
        // Caret on the name or the header, not anywhere in a long class body.
        if (!declaration.GetNameDocumentRange().Contains(provider.DocumentSelection) && provider.SelectedElement?.Parent != declaration) return null;
        return AspectTests.IsAspect(type) ? (type, project) : null;
    }

    sealed class Item(ISolution solution, ITypeElement aspect, IProject project, string testCsproj, bool newProject) : BulbActionBase
    {
        public override string Text => AspectTests.Describe(testCsproj, newProject, aspect.ShortName);

        protected override Action<ITextControl>? ExecutePsiTransaction(ISolution _, IProgressIndicator progress)
        {
            AspectTests.Create(solution, aspect, project, testCsproj, newProject);
            return null;
        }
    }
}

/// <summary>Alt+Enter in an aspect test file that ran: saves its transformed output as the expected .t.cs snapshot.</summary>
[ContextAction(GroupType = typeof(CSharpContextActions), Name = "Accept Metalama test output",
    Description = "Saves the last run's transformed output of this aspect test as its expected .t.cs snapshot.")]
public sealed class AcceptAspectTestOutputAction(ICSharpContextActionDataProvider provider) : ContextActionBase
{
    public override string Text => "Accept Metalama test output (.t.cs)";

    public override bool IsAvailable(IUserDataHolder cache) =>
        AspectTests.OutputFor(provider.Project, provider.SourceFile.GetLocation().FullPath) != null;

    protected override Action<ITextControl>? ExecutePsiTransaction(ISolution solution, IProgressIndicator progress)
    {
        if (AspectTests.OutputFor(provider.Project, provider.SourceFile.GetLocation().FullPath) is var (test, output))
            AspectTests.Accept(solution, test, output);
        return null;
    }
}
