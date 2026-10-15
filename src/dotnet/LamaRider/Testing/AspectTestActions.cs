using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JetBrains.Application.Progress;
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
using JetBrains.TextControl;
using JetBrains.Util;
using LamaRider.Model;

namespace LamaRider.Testing;

/// <summary>
/// Alt+Enter on an aspect class: adds a Metalama aspect test (a file applying the aspect to a fitting target) to each
/// aspect test project that references the aspect's project, or creates "&lt;Project&gt;.AspectTests" next to it.
/// </summary>
[ContextAction(GroupType = typeof(CSharpContextActions), Name = "Create Metalama aspect test",
    Description = "Adds a Metalama aspect test for this aspect, creating an aspect test project when there is none.")]
public sealed class CreateAspectTestAction(ICSharpContextActionDataProvider provider) : IContextAction
{
    const string AspectInterface = "Metalama.Framework.Aspects.IAspect`1";

    public IEnumerable<IntentionAction> CreateBulbItems()
    {
        var aspect = Aspect();
        if (aspect == null) return [];
        var (type, project) = aspect.Value;
        var aspectCsproj = project.ProjectFileLocation.FullPath;
        var testProjects = provider.Solution.GetAllProjects()
            .Select(p => p.ProjectFileLocation.FullPath)
            .Where(path => File.Exists(path) && path != aspectCsproj)
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return AspectTestScaffold.IsAspectTestProject(text) && AspectTestScaffold.References(text, Path.GetFileName(aspectCsproj));
            })
            .ToList();

        var items = testProjects
            .Select(path => new AddTestItem(provider.Solution, type, aspectCsproj, path, newProject: false))
            .ToList<BulbActionBase>();
        if (items.Count == 0)
        {
            var name = project.Name + ".AspectTests";
            var csproj = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(aspectCsproj)!)!, name, name + ".csproj");
            items.Add(new AddTestItem(provider.Solution, type, aspectCsproj, csproj, newProject: true));
        }
        return items.ToContextActionIntentions();
    }

    public bool IsAvailable(IUserDataHolder cache) => Aspect() != null;

    (ITypeElement Type, IProject Project)? Aspect()
    {
        var declaration = provider.GetSelectedElement<IClassDeclaration>();
        if (declaration?.DeclaredElement is not { } type || provider.Project is not { } project) return null;
        // Caret on the name or the header, not anywhere in a long class body.
        if (!declaration.GetNameDocumentRange().Contains(provider.DocumentSelection) && provider.SelectedElement?.Parent != declaration) return null;
        return type.GetAllSuperTypes().Any(t => t.GetClrName().FullName == AspectInterface) ? (type, project) : null;
    }

    internal static (AspectTestScaffold.TargetKind Kind, bool IsAttribute) Describe(ITypeElement type)
    {
        var superTypes = type.GetAllSuperTypes().ToList();
        var aspect = superTypes.First(t => t.GetClrName().FullName == AspectInterface);
        var argument = aspect.GetTypeElement() is { TypeParameters.Count: 1 } generic
            ? (aspect.GetSubstitution()[generic.TypeParameters[0]] as IDeclaredType)?.GetClrName().ShortName
            : null;
        var isAttribute = superTypes.Any(t => t.GetClrName().FullName == "System.Attribute");
        return (AspectTestScaffold.KindOf(argument ?? ""), isAttribute);
    }

    sealed class AddTestItem(ISolution solution, ITypeElement aspect, string aspectCsproj, string testCsproj, bool newProject) : BulbActionBase
    {
        public override string Text => newProject
            ? $"Create aspect test project '{Path.GetFileNameWithoutExtension(testCsproj)}' with a test for {aspect.ShortName}"
            : $"Add Metalama aspect test for {aspect.ShortName} to '{Path.GetFileNameWithoutExtension(testCsproj)}'";

        protected override Action<ITextControl>? ExecutePsiTransaction(ISolution _, IProgressIndicator progress)
        {
            var (kind, isAttribute) = Describe(aspect);
            var aspectNamespace = aspect.GetContainingNamespace().QualifiedName;
            var aspectName = aspect.ShortName;
            var solutionPath = solution.SolutionFilePath.FullPath;
            return _ => Task.Run(() => Create(solutionPath, aspectNamespace, aspectName, kind, isAttribute));
        }

        void Create(string solutionPath, string aspectNamespace, string aspectName, AspectTestScaffold.TargetKind kind, bool isAttribute)
        {
            var model = solution.GetProtocolSolution().GetLamaRiderModel();
            try
            {
                var testDir = Path.GetDirectoryName(testCsproj)!;
                var testProjectName = Path.GetFileNameWithoutExtension(testCsproj);
                if (newProject) CreateProject(solutionPath, testDir);

                var folder = Path.Combine(testDir, aspectName.EndsWith("Attribute") && aspectName.Length > 9 ? aspectName.Substring(0, aspectName.Length - 9) : aspectName);
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, AspectTestScaffold.NextTestFileName(Directory.GetFiles(folder).Select(Path.GetFileName), aspectName));
                File.WriteAllText(file, AspectTestScaffold.TestFile(testProjectName.Replace('-', '_'), aspectNamespace, aspectName, isAttribute, kind));
                model.OpenFile.Fire(file);
                model.Info.Fire(newProject
                    ? $"Created {testProjectName} and the test {Path.GetFileName(file)}. Run it: the first run writes the output; accept it with Alt+Enter > Accept Metalama test output."
                    : $"Added the test {Path.GetFileName(file)}. Run it, then accept the output with Alt+Enter > Accept Metalama test output.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                model.Failed.Fire("Cannot create the aspect test: " + e.Message);
            }
        }

        void CreateProject(string solutionPath, string testDir)
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

            Directory.CreateDirectory(testDir);
            var reference = MakeRelative(testDir, aspectCsproj);
            File.WriteAllText(testCsproj, AspectTestScaffold.TestProject(version, reference, framework, central));
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
}

/// <summary>
/// Alt+Enter in an aspect test file: saves the last test run's transformed output as the expected .t.cs snapshot
/// (Metalama's first run writes a placeholder; later runs fail when the aspect's output changed).
/// </summary>
[ContextAction(GroupType = typeof(CSharpContextActions), Name = "Accept Metalama test output",
    Description = "Saves the last run's transformed output of this aspect test as its expected .t.cs snapshot.")]
public sealed class AcceptAspectTestOutputAction(ICSharpContextActionDataProvider provider) : ContextActionBase
{
    public override string Text => "Accept Metalama test output (.t.cs)";

    public override bool IsAvailable(IUserDataHolder cache) => Output() != null;

    (string Test, string Output)? Output()
    {
        var path = provider.SourceFile.GetLocation().FullPath;
        if (path.EndsWith(".t.cs", StringComparison.OrdinalIgnoreCase) || provider.Project is not { } project) return null;
        var csproj = project.ProjectFileLocation.FullPath;
        if (!File.Exists(csproj) || !AspectTestScaffold.IsAspectTestProject(File.ReadAllText(csproj))) return null;
        var output = AspectTestScaffold.ActualOutput(Path.GetDirectoryName(csproj)!, path);
        return output == null ? null : (path, output);
    }

    protected override Action<ITextControl>? ExecutePsiTransaction(ISolution solution, IProgressIndicator progress)
    {
        var found = Output();
        if (found == null) return null;
        var (test, output) = found.Value;
        return _ =>
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
        };
    }
}
