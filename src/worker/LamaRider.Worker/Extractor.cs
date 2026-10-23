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
        var safe = new ResilientWorkspace(workspace, offsets);
        var sourceTypes = safe.Get(w => w.SourceCode.Types)
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

        var classes = safe.Get(w => w.AspectClasses)
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
                        : null,
                    OffersCodeFixes: symbol?.ContainingAssembly.Modules.Any(m => m.ReferencedAssemblies.Any(a => a.Name == "Metalama.Extensions.CodeFixes")) == true
                        || type != null && type.Compilation.Project is { } p && workspace.Projects.Any(wp => wp.Name == p.Name &&
                            wp.RoslynCompilation.ReferencedAssemblyNames.Any(a => a.Name == "Metalama.Extensions.CodeFixes")));
            })
            .OrderBy(c => c.Name)
            .ToList();

        var instances = safe.Get(w => w.AspectInstances)
            .Where(i => !i.IsSkipped)
            .Select(i => new AspectInstanceInfo(
                ClassId(i.AspectClass),
                DocId(i.TargetDeclaration),
                i.TargetDeclaration.ToDisplayString(),
                (i.TargetDeclaration as INamedType ?? i.TargetDeclaration.GetClosestNamedType())?.FullName ?? "",
                i.TargetDeclaration.DeclarationKind.ToString(),
                i.TargetDeclaration.Compilation.Project.Name ?? "",
                i.Predecessors.IsDefaultOrEmpty ? "Unknown" : i.Predecessors[0].Kind.ToString(),
                Locate(i.TargetDeclaration),
                i.Predecessors.IsDefaultOrEmpty ? null : AppliedBy(i.Predecessors[0].Instance),
                IsAttribute(i) ? null : AttributeArguments.Of(i.Aspect)))
            .Distinct() // records: collapses the same instance reported once per target framework
            .ToList();

        var transformations = safe.Get(w => w.Transformations)
            .Select(t => new TransformationInfo(
                ClassId(t.Advice.AspectInstance.AspectClass),
                DocId(t.TargetDeclaration),
                t.TransformationKind.ToString(),
                t.Description.ToString(),
                Locate(t.TargetDeclaration),
                DocId(t.Advice.AspectInstance.TargetDeclaration)))
            .Distinct()
            .ToList();

        var diagnostics = safe.Get(w => w.Diagnostics)
            .Where(d => d.Source != IntrospectionDiagnosticSource.CSharp)
            .Select(d => Located(d, offsets))
            .Concat(safe.Failures) // projects where Metalama threw instead of reporting
            .Distinct() // once per target framework
            .ToList();

        return new WorkerOutput(
            Schema, workspace.MetalamaVersion, classes, instances, transformations, diagnostics,
            TemplateClassifier.Classify(workspace, offsets),
            WriteTransformedFiles(workspace, safe, outDir, offsets, instances));
    }

    /// <summary>
    /// Metalama 2026.1 looks a diagnostic's declaration up in the compilation it was reported for, which throws when the
    /// syntax tree belongs to another project (a test project's workspace also holds the projects it references), and asserts
    /// on declarations it can't resolve (e.g. a LINQ range variable).
    /// </summary>
    static IDeclaration? DeclarationOf(IIntrospectionDiagnostic diagnostic)
    {
        try { return diagnostic.Declaration; }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }


    /// <summary>
    /// A diagnostic with its span, from the Roslyn diagnostic Metalama wraps (internal: when that
    /// changes, only the line remains). IIntrospectionDiagnostic.Line is 0-based; DiagnosticInfo.Line is 1-based.
    /// </summary>
    static DiagnosticInfo Located(IIntrospectionDiagnostic d, Offsets offsets)
    {
        var target = DeclarationOf(d) is { } declaration ? DocId(declaration) : null;
        // ponytail: IntrospectionDiagnostic._diagnostic is internal Metalama state; without it the line is all we have.
        var roslyn = d.GetType().GetField("_diagnostic", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(d)
            as Microsoft.CodeAnalysis.Diagnostic;
        var location = roslyn?.Location is { IsInSource: true } l ? l : null;
        var file = location?.SourceTree?.FilePath is { Length: > 0 } path ? path : d.FilePath;
        var line = location != null ? location.GetLineSpan().StartLinePosition.Line + 1 : d.Line + 1;
        var start = location != null && file != null ? offsets.FromPosition(file, location.SourceSpan.Start) : null;
        var end = location != null && file != null ? offsets.FromPosition(file, location.SourceSpan.End) : null;
        return new DiagnosticInfo(d.Id, d.Severity.ToString(), d.Message, target, file, line, d.Compilation?.Project.Name, start, end);
    }

    static bool IsAttribute(IIntrospectionAspectInstance instance) =>
        !instance.Predecessors.IsDefaultOrEmpty && instance.Predecessors[0].Kind.ToString() == "Attribute";

    /// <summary>The fabric (full name) or aspect instance ("LogAttribute on Calculator.Add(int, int)") that added an aspect.</summary>
    static string? AppliedBy(IIntrospectionAspectPredecessor? predecessor) => predecessor switch
    {
        IIntrospectionFabric fabric => fabric.FullName,
        IIntrospectionAspectInstance aspect => $"{aspect.AspectClass.ShortName} on {aspect.TargetDeclaration.ToDisplayString()}",
        _ => null,
    };

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

    static List<TransformedFileInfo> WriteTransformedFiles(Workspace workspace, ResilientWorkspace safe, string outDir, Offsets offsets, List<AspectInstanceInfo> instances)
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
        foreach (var compilation in safe.Get(w => w.TransformedCode.Compilations).OfType<CompilationModel>())
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
                result.Add(new TransformedFileInfo(tree.FilePath, target, Declarations(formatted, tree, text, sources.GetValueOrDefault(tree.FilePath), offsets, instances)));
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
        Microsoft.CodeAnalysis.Compilation compilation, SyntaxTree tree, string text, Microsoft.CodeAnalysis.Compilation? source, Offsets offsets,
        List<AspectInstanceInfo> instances)
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
                sourceSpan == null ? null : offsets.FromPosition(tree.FilePath, sourceSpan.Value.End),
                sourceSpan == null ? null : Standalone(node, model, instances)));
        }
        return result;
    }

    /// <summary>
    /// A declaration's transformed code that can replace its source (like <c>metalama divorce</c>, for one declaration):
    /// without aspect attributes, and with [ExcludeAspect] for aspects a fabric, inheritance or another aspect would add again.
    /// Null when no aspect is on it or inside it.
    /// </summary>
    static string? Standalone(MemberDeclarationSyntax node, Microsoft.CodeAnalysis.SemanticModel model, List<AspectInstanceInfo> instances)
    {
        var inside = node.DescendantNodesAndSelf()
            .Select(n => (n switch
            {
                MemberDeclarationSyntax m => model.GetDeclaredSymbol(m),
                VariableDeclaratorSyntax v => model.GetDeclaredSymbol(v),
                _ => null,
            })?.GetDocumentationCommentId())
            .OfType<string>().ToHashSet();
        var applied = instances.Where(i => inside.Contains(NormalizeId(i.Target).Split(';')[0])).ToList();
        if (applied.Count == 0) return null;

        var aspectAttributes = node.DescendantNodes().OfType<AttributeSyntax>()
            .Where(a => model.GetTypeInfo(a).Type?.AllInterfaces.Any(t => t.ToDisplayString() == "Metalama.Framework.Aspects.IAspect") == true)
            .ToHashSet();
        // A list goes when all its attributes are aspects; otherwise only the aspects ("[Log, Obsolete]" keeps "[Obsolete]").
        var remove = aspectAttributes.Select(a => (AttributeListSyntax)a.Parent!).Distinct()
            .SelectMany(list => list.Attributes.All(aspectAttributes.Contains) ? [list] : list.Attributes.Where(aspectAttributes.Contains).Cast<Microsoft.CodeAnalysis.SyntaxNode>())
            .ToList();
        var code = (remove.Count == 0 ? node : Microsoft.CodeAnalysis.SyntaxNodeExtensions.RemoveNodes(node, remove, Microsoft.CodeAnalysis.SyntaxRemoveOptions.KeepNoTrivia)!)
            .ToString().TrimStart().Replace("\r\n", "\n"); // LF like the model's offsets (IDE documents are LF)

        var excluded = applied.Where(i => i.AppliedVia != "Attribute").Select(i => i.AspectClass).Distinct()
            .Select(id => Microsoft.CodeAnalysis.DocumentationCommentId.GetFirstSymbolForDeclarationId(id, model.Compilation)?.ToMinimalDisplayString(model, node.SpanStart))
            .OfType<string>().ToList();
        if (excluded.Count == 0) return code;
        var exclude = model.Compilation.GetTypeByMetadataName("Metalama.Framework.Aspects.ExcludeAspectAttribute")?.ToMinimalDisplayString(model, node.SpanStart);
        if (exclude == null) return code;
        if (exclude.EndsWith("Attribute")) exclude = exclude[..^"Attribute".Length];
        var line = node.SyntaxTree.GetText().Lines.GetLineFromPosition(node.SpanStart);
        var indent = line.ToString()[..(node.SpanStart - line.Start)];
        return $"[{exclude}({string.Join(", ", excluded.Select(e => $"typeof({e})"))})]\n{indent}{code}";
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
