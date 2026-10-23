using Metalama.Framework.Workspaces;

namespace LamaRider.Worker;

/// <summary>
/// Workspace-wide results when Metalama computes them for every project; else project by project, leaving out projects
/// where Metalama throws instead of reporting (e.g. LAMA0651 in 2026.1), which become <see cref="Failures"/>. One broken
/// aspect must not blank out the whole solution.
/// </summary>
sealed class ResilientWorkspace(Workspace workspace, Offsets offsets)
{
    readonly HashSet<Project> _failed = new();

    public List<DiagnosticInfo> Failures { get; } = new();

    public List<T> Get<T>(Func<IProjectSet, IEnumerable<T>> get)
    {
        try
        {
            return get(workspace).ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var result = new List<T>();
            foreach (var project in workspace.Projects.Where(p => !_failed.Contains(p)))
            {
                try { result.AddRange(get(project)); }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    _failed.Add(project);
                    Failures.AddRange(Diagnostics(project, failure));
                }
            }
            return result;
        }
    }

    /// <summary>The diagnostics an exception carries (Metalama's DiagnosticException), else one describing it.</summary>
    IEnumerable<DiagnosticInfo> Diagnostics(Project project, Exception failure)
    {
        IEnumerable<Exception> inner = failure is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [failure];
        foreach (var e in inner)
        {
            if (e.GetType().GetProperty("Diagnostic")?.GetValue(e) is Microsoft.CodeAnalysis.Diagnostic d)
            {
                var location = d.Location is { IsInSource: true } l ? l : null;
                var file = location?.SourceTree?.FilePath;
                yield return new DiagnosticInfo(d.Id, d.Severity.ToString(), d.GetMessage(), null, file,
                    location != null ? location.GetLineSpan().StartLinePosition.Line + 1 : null, project.Name,
                    file != null ? offsets.FromPosition(file, location!.SourceSpan.Start) : null,
                    file != null ? offsets.FromPosition(file, location!.SourceSpan.End) : null);
            }
            else
            {
                yield return new DiagnosticInfo("LAMARIDER", "Error", $"Metalama failed to analyze {project.Name}: {e.Message}", null, null, null, project.Name);
            }
        }
    }
}
