using System.Collections.Immutable;
using Metalama.Framework.Workspaces;

namespace LamaRider.Worker;

/// <summary>
/// Metalama.Testing.AspectTesting sets MetalamaEnabled=False (its runner weaves each test file on its own), so the
/// solution's workspace sees no aspects in test inputs. These projects get a workspace of their own with Metalama on.
/// </summary>
public static class AspectTestProjects
{
    static readonly ImmutableDictionary<string, string> MetalamaOn = ImmutableDictionary<string, string>.Empty.Add("MetalamaEnabled", "True");

    public static bool Is(string csproj) =>
        csproj.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(csproj) &&
        File.ReadAllText(csproj).Contains("Metalama.Testing.AspectTesting", StringComparison.OrdinalIgnoreCase);

    public static ImmutableArray<string> In(Workspace workspace) =>
        workspace.Projects.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Where(Is).ToImmutableArray();

    public static Task<Workspace> LoadAsync(ImmutableArray<string> projects) =>
        WorkspaceCollection.Default.LoadAsync(projects, MetalamaOn, restore: false);

    /// <summary>The test projects' part of their workspace (it also holds the projects they reference), merged into <paramref name="main"/>.</summary>
    public static WorkerOutput Merge(WorkerOutput main, WorkerOutput tests, ImmutableArray<string> testProjectNames)
    {
        bool InTests(string? project) => project != null && testProjectNames.Contains(project);
        var testFiles = tests.Instances.Where(i => InTests(i.Project) && i.Location != null).Select(i => i.Location!.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool InTestFile(string? file) => file != null && testFiles.Contains(file);
        var classIds = main.AspectClasses.Select(c => c.Id).ToHashSet();

        return main with
        {
            AspectClasses = main.AspectClasses.Concat(tests.AspectClasses.Where(c => !classIds.Contains(c.Id))).OrderBy(c => c.Name).ToList(),
            Instances = main.Instances.Concat(tests.Instances.Where(i => InTests(i.Project))).ToList(),
            Transformations = main.Transformations.Concat(tests.Transformations.Where(t => InTestFile(t.Location?.File))).ToList(),
            // Project-wide errors of test inputs are the runner's business (a test may expect them); located ones are squiggles.
            Diagnostics = main.Diagnostics.Concat(tests.Diagnostics.Where(d => InTests(d.Project) && d.File != null)).ToList(),
            TransformedFiles = main.TransformedFiles.Concat(tests.TransformedFiles.Where(f => InTestFile(f.File))).ToList(),
        };
    }
}
