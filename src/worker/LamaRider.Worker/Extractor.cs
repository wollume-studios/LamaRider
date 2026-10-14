using Metalama.Framework.Code;
using Metalama.Framework.Engine.CodeModel;
using Metalama.Framework.Introspection;
using Metalama.Framework.Workspaces;

namespace LamaRider.Worker;

public static class Extractor
{
    public const int Schema = 1;

    public static WorkerOutput Extract(Workspace workspace, string outDir)
    {
        var offsets = new Offsets();
        var sourceTypes = workspace.SourceCode.Types
            .GroupBy(t => t.FullName)
            .ToDictionary(g => g.Key, g => g.First());

        string ClassId(IIntrospectionAspectClass c) =>
            sourceTypes.TryGetValue(c.FullName, out var t) ? DocId(t) : "T:" + c.FullName;

        SourceLocation? Locate(IDeclaration? declaration)
        {
            if (declaration == null) return null;
            // Roslyn node, not SourceReference.Span: Metalama 2026.1's SourceSpan swaps StartColumn and EndLine.
            foreach (var source in declaration.Sources)
            {
                var node = source.SyntaxNodeOrToken();
                var path = node.SyntaxTree?.FilePath;
                if (string.IsNullOrEmpty(path)) continue;
                var start = offsets.FromPosition(path, node.Span.Start);
                var end = offsets.FromPosition(path, node.Span.End);
                if (start != null && end != null) return new SourceLocation(path, start.Value, end.Value);
            }
            return null;
        }

        var classes = workspace.AspectClasses
            .GroupBy(c => c.FullName).Select(g => g.First())
            .Select(c =>
            {
                sourceTypes.TryGetValue(c.FullName, out var type);
                return new AspectClassInfo(ClassId(c), c.ShortName, type?.Compilation.Project.Name ?? "", type == null, Locate(type));
            })
            .OrderBy(c => c.Name)
            .ToList();

        var instances = workspace.AspectInstances
            .Where(i => !i.IsSkipped)
            .Select(i => new AspectInstanceInfo(
                ClassId(i.AspectClass),
                DocId(i.TargetDeclaration),
                i.TargetDeclaration.ToDisplayString(),
                (i.TargetDeclaration as INamedType ?? i.TargetDeclaration.GetClosestNamedType())?.FullName ?? "",
                i.TargetDeclaration.DeclarationKind.ToString(),
                i.TargetDeclaration.Compilation.Project.Name ?? "",
                i.Predecessors.IsDefaultOrEmpty ? "Unknown" : i.Predecessors[0].Kind.ToString(),
                Locate(i.TargetDeclaration)))
            .Distinct() // records: collapses the same instance reported once per target framework
            .ToList();

        var transformations = workspace.Transformations
            .Select(t => new TransformationInfo(
                ClassId(t.Advice.AspectInstance.AspectClass),
                DocId(t.TargetDeclaration),
                t.TransformationKind.ToString(),
                t.Description.ToString(),
                Locate(t.TargetDeclaration)))
            .Distinct()
            .ToList();

        var diagnostics = workspace.Diagnostics
            .Where(d => d.Source != IntrospectionDiagnosticSource.CSharp)
            .Select(d => new DiagnosticInfo(d.Id, d.Severity.ToString(), d.Message, d.Declaration == null ? null : DocId(d.Declaration), d.FilePath, d.Line))
            .Distinct()
            .ToList();

        return new WorkerOutput(
            Schema, workspace.MetalamaVersion, classes, instances, transformations, diagnostics,
            TemplateClassifier.Classify(workspace, offsets),
            WriteTransformedFiles(workspace, outDir));
    }

    /// <summary>
    /// C# XML documentation ID (what ReSharper's XMLDocUtil produces). Metalama's serializable id appends
    /// "~ReturnType" to every method; XML doc IDs only do that for conversion operators.
    /// </summary>
    public static string DocId(IDeclaration declaration) => NormalizeId(declaration.ToSerializableId().Id);

    /// <summary>Drops "~ReturnType" but keeps a parameter/return suffix such as ";Parameter=0".</summary>
    public static string NormalizeId(string id)
    {
        var tilde = id.IndexOf('~');
        if (tilde < 0 || id.Contains(".op_Implicit(") || id.Contains(".op_Explicit(")) return id;
        var suffix = id.IndexOf(';', tilde);
        return suffix < 0 ? id.Substring(0, tilde) : id.Substring(0, tilde) + id.Substring(suffix);
    }

    static List<TransformedFileInfo> WriteTransformedFiles(Workspace workspace, string outDir)
    {
        var result = new List<TransformedFileInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(outDir, "transformed");
        Directory.CreateDirectory(dir);
        foreach (var compilation in workspace.TransformedCode.Compilations.OfType<CompilationModel>())
        foreach (var tree in compilation.RoslynCompilation.SyntaxTrees)
        {
            if (!File.Exists(tree.FilePath) || !seen.Add(tree.FilePath)) continue;
            var text = tree.GetText().ToString();
            if (text == File.ReadAllText(tree.FilePath)) continue; // untouched by aspects
            var target = Path.Combine(dir, $"{result.Count}_{Path.GetFileName(tree.FilePath)}");
            File.WriteAllText(target, text);
            result.Add(new TransformedFileInfo(tree.FilePath, target));
        }
        return result;
    }
}
