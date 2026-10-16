using Metalama.Framework.Code;
using Metalama.Framework.Engine.Formatting;
using Metalama.Framework.Engine.CodeModel;
using Metalama.Framework.Introspection;
using Metalama.Framework.Workspaces;
using SyntaxTree = Microsoft.CodeAnalysis.SyntaxTree;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
            .Where(c => !c.ShortName.StartsWith("<") && !c.FullName.Contains('<')) // Metalama-internal (<Fabric>, transitive helper aspects)
            .Select(c =>
            {
                sourceTypes.TryGetValue(c.FullName, out var type);
                var symbol = workspace.Projects.Select(p => p.RoslynCompilation.GetTypeByMetadataName(c.FullName)).FirstOrDefault(s => s != null);
                var isLibrary = type == null;
                var project = type?.Compilation.Project.Name ?? "";
                return new AspectClassInfo(
                    ClassId(c), c.ShortName, project, isLibrary, Locate(type),
                    Package: isLibrary ? symbol?.ContainingAssembly.Name ?? "" : project,
                    Summary: Documentation.Summary(symbol?.GetDocumentationCommentXml()),
                    DocUrl: isLibrary && c.FullName.StartsWith("Metalama.", StringComparison.Ordinal)
                        ? "https://doc.metalama.net/api/" + c.FullName.ToLowerInvariant().Replace('.', '-')
                        : null);
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
                Locate(t.TargetDeclaration),
                DocId(t.Advice.AspectInstance.TargetDeclaration)))
            .Distinct()
            .ToList();

        var diagnostics = workspace.Diagnostics
            .Where(d => d.Source != IntrospectionDiagnosticSource.CSharp)
            .Select(d => new DiagnosticInfo(d.Id, d.Severity.ToString(), d.Message, DeclarationOf(d) is { } declaration ? DocId(declaration) : null, d.FilePath, d.Line,
                d.Compilation?.Project.Name))
            .Distinct()
            .ToList();

        return new WorkerOutput(
            Schema, workspace.MetalamaVersion, classes, instances, transformations, diagnostics,
            TemplateClassifier.Classify(workspace, offsets),
            WriteTransformedFiles(workspace, outDir, offsets));
    }

    /// <summary>
    /// Metalama 2026.1 looks a diagnostic's declaration up in the compilation it was reported for, which throws when the
    /// syntax tree belongs to another project (a test project's workspace also holds the projects it references).
    /// </summary>
    static IDeclaration? DeclarationOf(IIntrospectionDiagnostic diagnostic)
    {
        try { return diagnostic.Declaration; }
        catch (ArgumentException) { return null; }
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

    static List<TransformedFileInfo> WriteTransformedFiles(Workspace workspace, string outDir, Offsets offsets)
    {
        // Source compilation per file: the popup's diff shows a declaration's source next to its transformed code.
        var sources = new Dictionary<string, Microsoft.CodeAnalysis.Compilation>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in workspace.Projects)
        foreach (var sourceTree in project.RoslynCompilation.SyntaxTrees)
            sources.TryAdd(sourceTree.FilePath, project.RoslynCompilation);

        var result = new List<TransformedFileInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(outDir, "transformed");
        Directory.CreateDirectory(dir);
        foreach (var compilation in workspace.TransformedCode.Compilations.OfType<CompilationModel>())
        {
            var formatted = Format(compilation.RoslynCompilation);
            foreach (var tree in formatted.SyntaxTrees)
            {
                if (!File.Exists(tree.FilePath) || !seen.Add(tree.FilePath)) continue;
                var text = tree.GetText().ToString();
                var original = ReadOrNull(tree.FilePath); // null: being rewritten (obj/ by a build)
                if (original == null || text == original) continue; // untouched by aspects
                var target = Path.Combine(dir, $"{result.Count}_{Path.GetFileName(tree.FilePath)}");
                File.WriteAllText(target, text);
                result.Add(new TransformedFileInfo(tree.FilePath, target, Declarations(formatted, tree, text, sources.GetValueOrDefault(tree.FilePath), offsets)));
            }
        }
        return result;
    }

    static string? ReadOrNull(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }
    }

    /// <summary>Types and members of a transformed file with their LF offsets: the IDE shows one declaration's transformed code.</summary>
    static List<TransformedDeclaration> Declarations(
        Microsoft.CodeAnalysis.Compilation compilation, SyntaxTree tree, string text, Microsoft.CodeAnalysis.Compilation? source, Offsets offsets)
    {
        // CR count before each position: offsets are LF-normalized like the rest of the model.
        var crBefore = new int[text.Length + 1];
        for (var i = 0; i < text.Length; i++) crBefore[i + 1] = crBefore[i] + (text[i] == '\r' ? 1 : 0);

        var model = compilation.GetSemanticModel(tree);
        var seen = new HashSet<string>();
        var result = new List<TransformedDeclaration>();
        foreach (var node in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            if (node is not (BaseTypeDeclarationSyntax or BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax)) continue;
            var id = model.GetDeclaredSymbol(node)?.GetDocumentationCommentId();
            if (id == null || !seen.Add(id)) continue;
            // Declarations an aspect introduced have no source.
            var sourceSpan = source == null ? null
                : Microsoft.CodeAnalysis.DocumentationCommentId.GetFirstSymbolForDeclarationId(id, source)?.DeclaringSyntaxReferences
                    .FirstOrDefault(r => string.Equals(r.SyntaxTree.FilePath, tree.FilePath, StringComparison.OrdinalIgnoreCase))?.Span;
            result.Add(new TransformedDeclaration(id, node.Span.Start - crBefore[node.Span.Start], node.Span.End - crBefore[node.Span.End],
                sourceSpan == null ? null : offsets.FromPosition(tree.FilePath, sourceSpan.Value.Start),
                sourceSpan == null ? null : offsets.FromPosition(tree.FilePath, sourceSpan.Value.End)));
        }
        return result;
    }

    /// <summary>Metalama's own output formatter (what MetalamaFormatOutput uses): simplified names, line breaks.</summary>
    static Microsoft.CodeAnalysis.Compilation Format(Microsoft.CodeAnalysis.Compilation compilation)
    {
        try
        {
            return new CodeFormatter().FormatAllAsync(compilation, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Formatting of transformed code skipped: {e.Message}");
            return compilation;
        }
    }
}
