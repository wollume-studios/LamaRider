using Metalama.Framework.Engine.Formatting;
using Metalama.Framework.Engine.Services;
using Metalama.Framework.Services;
using Metalama.Framework.Workspaces;

namespace LamaRider.Worker;

/// <summary>Metalama's own T# classifier (engine API, the one the Visual Studio extension uses).</summary>
public static class TemplateClassifier
{
    static readonly HashSet<string> Kinds = ["CompileTime", "CompileTimeVariable", "TemplateKeyword", "Dynamic"];

    public static List<FileClassification> Classify(Workspace workspace, Offsets offsets)
    {
        var result = new List<FileClassification>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in workspace.Projects)
        {
            var metalamaCompilation = project.SourceCode.Compilations.FirstOrDefault();
            if (metalamaCompilation == null) continue;
            ProjectServiceProvider services = (ServiceProvider<IProjectService>) metalamaCompilation.Project.ServiceProvider;
            var service = new ClassificationService(services);
            var compilation = project.RoslynCompilation;
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (!File.Exists(tree.FilePath) || !seen.Add(tree.FilePath)) continue;
                if (!ClassificationService.ContainsCompileTimeCode(tree.GetRoot())) continue;
                var spans = service.GetClassifiedTextSpans(compilation.GetSemanticModel(tree), CancellationToken.None)
                    .Where(s => Kinds.Contains(s.Classification.ToString()))
                    .Select(s => (Start: offsets.FromPosition(tree.FilePath, s.Span.Start), End: offsets.FromPosition(tree.FilePath, s.Span.End), Kind: s.Classification.ToString()))
                    .Where(s => s.Start != null && s.End != null && s.End > s.Start)
                    .Select(s => new ClassifiedSpan(s.Start!.Value, s.End!.Value, s.Kind))
                    .ToList();
                if (spans.Count > 0) result.Add(new FileClassification(tree.FilePath, spans));
            }
        }
        return result;
    }
}
