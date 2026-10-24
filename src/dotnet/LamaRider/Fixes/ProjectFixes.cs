using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JetBrains.Application.Threading;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.Features.SolutionBuilders;
using JetBrains.ReSharper.Feature.Services.Protocol;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using LamaRider.Model;
using static JetBrains.Rider.Model.SolutionLamaRiderModelEx;

namespace LamaRider.Fixes;

/// <summary>Helpers of the project, architecture, interceptor and analyzer fixes.</summary>
static class ProjectFixHelpers
{
    /// <summary>The project file's text, null when it can't be read.</summary>
    public static string? ProjectText(IProject project)
    {
        try { return File.ReadAllText(project.ProjectFileLocation.FullPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static Fix SetProperty(IProject project, string name, string value, string? note = null) =>
        new($"Set {name} to {value} in '{project.Name}'{note}", ctx => FixOps.SetProjectProperty(ctx, name, value));

    /// <summary>Runs <paramref name="action"/> on the shell thread after the fix (fixes run in a PSI transaction).</summary>
    public static void Later(FixContext c, string name, Action action) =>
        c.Solution.Locks.ExecuteOrQueueEx(c.Solution.GetSolutionLifetimes().UntilSolutionCloseLifetime, "LamaRider." + name, action);

    public static void OpenFile(FixContext c, string path) =>
        Later(c, "OpenFile", () => c.Solution.GetProtocolSolution().GetLamaRiderModel().OpenFile.Fire(path));

    public static void Build(FixContext c, IProject project, bool rebuild) =>
        Later(c, "Build", () =>
        {
            var builder = c.Solution.GetComponent<ISolutionBuilder>();
            builder.ExecuteBuildRequest(builder.CreateBuildRequest(rebuild ? BuildSessionTarget.Rebuild : BuildSessionTarget.Build, new[] { project }, SolutionBuilderRequestSilentMode.Default,
                new SolutionBuilderRequestAdvancedSettings()));
        });

    /// <summary>Adds `using <paramref name="ns"/>;` to <paramref name="file"/> (which may be another file than the context's).</summary>
    public static void AddUsingIn(FixContext c, ICSharpFile? file, string ns)
    {
        if (file == null) return;
        if (file == c.File)
        {
            FixOps.AddUsing(c, ns);
            return;
        }
        var usings = file.Descendants<IUsingDirective>().ToEnumerable().ToList();
        if (usings.OfType<IUsingSymbolDirective>().Any(u => u.ImportedSymbolName?.QualifiedName == ns)) return;
        if (usings.LastOrDefault() is { } last) c.Insert(last.GetDocumentEndOffset(), $"\nusing {ns};");
        else c.Insert(new DocumentOffset(file.GetDocumentRange().Document, 0), $"using {ns};\n\n");
    }

    /// <summary>Adds an attribute above a declaration (fields too: above their whole declaration), with its using.</summary>
    public static void AddAttributeTo(FixContext c, IDeclaration declaration, string attribute, string ns)
    {
        if (declaration is IFieldDeclaration field)
        {
            var owner = (ITreeNode?)field.GetContainingNode<IMultipleFieldDeclaration>() ?? field;
            var anchor = field.Attributes.FirstOrDefault()?.GetContainingNode<IAttributeSection>()?.GetDocumentStartOffset() ?? FixOps.StartAfterAttributes(owner);
            var text = anchor.Document.GetText();
            var lineStart = anchor.Offset == 0 ? 0 : text.LastIndexOf('\n', anchor.Offset - 1) + 1;
            var indent = text.Substring(lineStart, anchor.Offset - lineStart);
            if (indent.Trim().Length > 0) c.Insert(anchor, $"[{attribute}] ");
            else c.Insert(new DocumentOffset(anchor.Document, lineStart), $"{indent}[{attribute}]\n");
        }
        else if (declaration is IAttributesOwnerDeclaration owner) FixOps.AddAttribute(c, owner, attribute);
        else return;
        AddUsingIn(c, declaration.GetContainingFile() as ICSharpFile, ns);
    }

    /// <summary>The source declaration of a class, struct or interface, when it's in the solution.</summary>
    public static IClassLikeDeclaration? SourceOf(ITypeElement? type) =>
        type?.GetDeclarations().OfType<IClassLikeDeclaration>().FirstOrDefault();

    public static ITypeElement? TypeElementOf(IType? type) =>
        (type is IArrayType array ? array.ElementType : type) is IDeclaredType declared ? declared.GetTypeElement() : null;

    /// <summary>
    /// The type named <paramref name="name"/> (as a message writes it, e.g. "List&lt;Foo&gt;" or "Ns.Foo") within
    /// <paramref name="type"/> (itself, its element type, its type arguments), else the type itself.
    /// </summary>
    public static ITypeElement? Named(IType? type, string? name)
    {
        var top = TypeElementOf(type);
        if (name == null || type == null) return top;
        var shortName = Regex.Replace(name, @"<.*$|\[.*$|\?$", "").Split('.').Last();
        var queue = new Queue<IType>([type]);
        for (var guard = 0; queue.Count > 0 && guard < 50; guard++)
        {
            var current = queue.Dequeue();
            if (current is IArrayType array) { queue.Enqueue(array.ElementType); continue; }
            if (current is not IDeclaredType declared || declared.GetTypeElement() is not { } element) continue;
            if (element.ShortName == shortName) return element;
            var substitution = declared.Resolve().Substitution;
            foreach (var parameter in element.TypeParameters) queue.Enqueue(substitution[parameter]);
        }
        return top;
    }

    /// <summary>The field or automatic property the diagnostic is on.</summary>
    public static ICSharpTypeMemberDeclaration? Member(FixContext c) =>
        (ICSharpTypeMemberDeclaration?)c.At<IFieldDeclaration>() ?? c.At<IPropertyDeclaration>()
        ?? (ICSharpTypeMemberDeclaration?)c.Within<IFieldDeclaration>().FirstOrDefault() ?? c.Within<IPropertyDeclaration>().FirstOrDefault()
        ?? c.Target as ICSharpTypeMemberDeclaration;

    public static IClassLikeDeclaration? TypeAt(FixContext c) => c.Target as IClassLikeDeclaration ?? c.At<IClassLikeDeclaration>();

    public static IType? TypeOfMember(IDeclaration? member) => (member?.DeclaredElement as ITypeOwner)?.Type;

    /// <summary>The first base class, when the type has one other than object.</summary>
    public static ITypeElement? BaseClass(IClassLikeDeclaration declaration) =>
        (declaration.DeclaredElement as ITypeElement)?.GetSuperTypes().Select(t => t.GetTypeElement()).OfType<IClass>()
        .FirstOrDefault(t => t.GetClrName().FullName != "System.Object");

    /// <summary>Renames a declaration and its references; for a type also its constructors.</summary>
    public static void Rename(FixContext c, IDeclaration declaration, string newName)
    {
        FixOps.Rename(c, declaration, newName);
        if (declaration is IClassLikeDeclaration type)
            foreach (var constructor in type.ConstructorDeclarations)
                c.Replace(constructor.GetNameDocumentRange(), newName);
    }

    public static string ShortTypeName(string fullName) => fullName.Split('.', '+').Last();
}

/// <summary>LAMA0051/0052/0282: the C# language version of the project; LAMA0232/0282: raw string literals in templates.</summary>
sealed class LanguageVersionFixes : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0051", "LAMA0052", "LAMA0232", "LAMA0282"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        var message = c.Diagnostic.Message;
        var project = c.Project;
        switch (c.Diagnostic.Id)
        {
            case "LAMA0051":
                if (project == null) yield break;
                foreach (var version in ProjectFixLogic.SupportedVersions(message))
                    yield return ProjectFixHelpers.SetProperty(project, "LangVersion", version);
                yield return ProjectFixHelpers.SetProperty(project, "MetalamaAllowPreviewLanguageFeatures", "true", " (at your own risk)");
                break;
            case "LAMA0052":
                if (project == null) yield break;
                var property = ProjectFixLogic.LanguageVersionProperty(message);
                foreach (var version in ProjectFixLogic.SupportedVersions(message))
                    yield return ProjectFixHelpers.SetProperty(project, property, version);
                break;
            case "LAMA0232":
                foreach (var literal in RawLiterals(c.Within<ICSharpExpression>().Concat(c.At<ICSharpExpression>() is { } e ? [e] : [])).Take(1))
                    yield return new Fix("Use a regular string literal", ctx => ctx.Replace(literal.Node, literal.Regular));
                if (project != null && ProjectFixHelpers.ProjectText(project) is { } text && ProjectFixLogic.HasProperty(text, "MetalamaTemplateLanguageVersion"))
                    yield return new Fix($"Remove MetalamaTemplateLanguageVersion from '{project.Name}'", ctx => FixOps.RemoveProjectProperty(ctx, "MetalamaTemplateLanguageVersion"));
                break;
            case "LAMA0282":
                if (project != null && ProjectFixLogic.RequiredLanguageVersion(message) is { } required)
                    yield return ProjectFixHelpers.SetProperty(project, "LangVersion", required);
                if (ProjectFixLogic.Quoted(message).FirstOrDefault() is { } aspect && AspectDeclaration(c, aspect) is { } declaration)
                {
                    var literals = RawLiterals(declaration.Descendants<ICSharpExpression>().ToEnumerable()).ToList();
                    if (literals.Count > 0)
                        yield return new Fix($"Replace the raw string literals of '{declaration.DeclaredName}' with regular ones", ctx =>
                        {
                            foreach (var literal in literals) ctx.Replace(literal.Node, literal.Regular);
                        });
                }
                break;
        }
    }

    /// <summary>Raw string literals among <paramref name="expressions"/>, with their regular equivalent.</summary>
    static IEnumerable<(ICSharpExpression Node, string Regular)> RawLiterals(IEnumerable<ICSharpExpression> expressions)
    {
        var seen = new HashSet<ICSharpExpression>();
        foreach (var expression in expressions)
        {
            if (expression is not (ICSharpLiteralExpression or IInterpolatedStringExpression) || !seen.Add(expression)) continue;
            var text = expression.GetText();
            if (!text.TrimStart('$').StartsWith("\"\"\"")) continue;
            if (ProjectFixLogic.RawToRegular(text) is { } regular) yield return (expression, regular);
        }
    }

    /// <summary>The source declaration of the aspect class a message names ("LogAttribute" or "Ns.LogAttribute").</summary>
    static IClassLikeDeclaration? AspectDeclaration(FixContext c, string name)
    {
        var bare = name.Split('<')[0];
        var info = c.Model?.AspectClasses.FirstOrDefault(a => !a.IsLibrary && (a.Id == "T:" + bare || a.Name == bare || a.Id.EndsWith("." + bare)));
        if (info != null && ProjectFixHelpers.SourceOf(FixOps.TypeOf(c, info.Id)) is { } declaration) return declaration;
        // Not in the model: the aspect attributes on the target.
        var target = c.Target as IAttributesOwnerDeclaration ?? c.At<IAttributesOwnerDeclaration>();
        var shortName = ProjectFixHelpers.ShortTypeName(bare);
        return target?.Attributes
            .Where(a => FixOps.ShortName(a) == shortName || FixOps.ShortName(a) + "Attribute" == shortName)
            .Select(a => ProjectFixHelpers.SourceOf(a.Name?.Reference.Resolve().DeclaredElement as ITypeElement))
            .FirstOrDefault(d => d != null);
    }
}

/// <summary>LAMA0021 (cycle in the aspect order), LAMA0035 (unordered aspect layers).</summary>
sealed class AspectOrderFixes : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0021", "LAMA0035"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if (c.Diagnostic.Id == "LAMA0021")
        {
            foreach (var attribute in CycleAttributes(c))
            {
                var located = c.Range.IsValid() && c.Range.Contains(attribute.GetDocumentRange());
                yield return new Fix(located ? "Remove this [AspectOrder] attribute" : $"Remove [AspectOrder({Describe(attribute)})]",
                    ctx => FixOps.RemoveAttribute(ctx, attribute));
            }
            yield break;
        }

        var layers = ProjectFixLogic.Quoted(c.Diagnostic.Message);
        if (layers.Count < 2) yield break;
        if (c.File != null)
        {
            string Short(string layer) => ProjectFixHelpers.ShortTypeName(layer.Split(':')[0]) + (layer.Contains(":") ? ":" + layer.Split(':')[1] : "");
            yield return new Fix($"Add [assembly: AspectOrder(CompileTime, {Short(layers[0])}, {Short(layers[1])})] (keeps the current order)",
                ctx => AddAssemblyAttribute(ctx, ProjectFixLogic.AspectOrderAttribute("CompileTime", [layers[0], layers[1]])));
            yield return new Fix($"Add [assembly: AspectOrder(CompileTime, {Short(layers[1])}, {Short(layers[0])})]",
                ctx => AddAssemblyAttribute(ctx, ProjectFixLogic.AspectOrderAttribute("CompileTime", [layers[1], layers[0]])));
        }
        if (c.Project is { } project)
            yield return ProjectFixHelpers.SetProperty(project, "MetalamaRequireOrderedAspects", "False");
    }

    /// <summary>The [AspectOrder] attribute reported, else those of this file that name two aspects of the cycle.</summary>
    static IEnumerable<IAttribute> CycleAttributes(FixContext c)
    {
        bool IsAspectOrder(IAttribute a) => FixOps.ShortName(a) == "AspectOrder";
        var reported = (c.At<IAttribute>() is { } at ? [at] : c.Within<IAttribute>()).Where(IsAspectOrder).ToList();
        if (reported.Count > 0) return reported.Take(1);
        if (c.File == null) return [];
        var colon = c.Diagnostic.Message.IndexOf(':');
        var aspects = colon < 0 ? [] : c.Diagnostic.Message.Substring(colon + 1).TrimEnd('.', ' ').Split(',')
            .Select(a => ProjectFixHelpers.ShortTypeName(a.Trim().Split(':')[0])).Where(a => a.Length > 0).ToList();
        return c.File.Descendants<IAttribute>().ToEnumerable().Where(IsAspectOrder)
            .Where(a => aspects.Count(name => Regex.IsMatch(a.GetText(), $@"\b{Regex.Escape(name)}\b")) >= 2);
    }

    static string Describe(IAttribute attribute) =>
        string.Join(", ", attribute.Arguments.Select(a => a.Value?.GetText() ?? "").Where(t => !t.StartsWith("AspectOrderDirection"))
            .Select(t => Regex.Replace(t, @"^typeof\s*\(\s*(.*?)\s*\)$", "$1")));

    /// <summary>Adds an assembly attribute after the usings of the file (with `using Metalama.Framework.Aspects;`).</summary>
    static void AddAssemblyAttribute(FixContext c, string attribute)
    {
        if (c.File == null) return;
        var usings = c.File.Descendants<IUsingDirective>().ToEnumerable().Where(u => u.GetContainingNode<ICSharpNamespaceDeclaration>() == null).ToList();
        var hasUsing = usings.OfType<IUsingSymbolDirective>().Any(u => u.ImportedSymbolName?.QualifiedName == "Metalama.Framework.Aspects");
        if (usings.LastOrDefault() is { } last)
            c.Insert(last.GetDocumentEndOffset(), (hasUsing ? "" : "\nusing Metalama.Framework.Aspects;") + "\n\n" + attribute);
        else
            c.Insert(new DocumentOffset(c.File.GetDocumentRange().Document, 0), (hasUsing ? "" : "using Metalama.Framework.Aspects;\n\n") + attribute + "\n\n");
    }
}

/// <summary>Project properties: LAMA0053/0080 (DefineConstants overwritten), 0066 (formatting), 0083 (timeout), 0084 (target frameworks).</summary>
sealed class MsBuildPropertyFixes : FixProvider
{
    static readonly string[] FormattingProperties = ["MetalamaFormatOutput", "MetalamaDebugTransformedCode", "MetalamaWriteHtml"];

    public override IEnumerable<string> Ids => ["LAMA0053", "LAMA0080", "LAMA0066", "LAMA0083", "LAMA0084"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if (c.Project is not { } project) yield break;
        var text = ProjectFixHelpers.ProjectText(project) ?? "";
        switch (c.Diagnostic.Id)
        {
            case "LAMA0053":
            case "LAMA0080":
                if (ProjectFixLogic.OverwritesDefineConstants(text))
                    yield return new Fix($"Append to $(DefineConstants) in '{project.Name}' instead of overwriting it",
                        ctx => ctx.EditProjectFile(project, ProjectFixLogic.AppendDefineConstants));
                yield return ProjectFixHelpers.SetProperty(project, "MetalamaEnabled", "False", " (disable Metalama)");
                break;
            case "LAMA0066":
                foreach (var property in FormattingProperties.Where(p => ProjectFixLogic.HasProperty(text, p)))
                    yield return new Fix($"Remove {property} from '{project.Name}'", ctx => FixOps.RemoveProjectProperty(ctx, property));
                yield return ProjectFixHelpers.SetProperty(project, "MetalamaFormatOutput", "False");
                break;
            case "LAMA0083":
                yield return ProjectFixHelpers.SetProperty(project, ProjectFixLogic.TimeoutProperty(c.Diagnostic.Message), "600000", " (10 minutes)");
                break;
            case "LAMA0084":
                if (ProjectFixLogic.HasProperty(text, "MetalamaCompileTimeTargetFrameworks"))
                    yield return new Fix($"Remove MetalamaCompileTimeTargetFrameworks from '{project.Name}' (use the default)",
                        ctx => FixOps.RemoveProjectProperty(ctx, "MetalamaCompileTimeTargetFrameworks"));
                break;
        }
    }
}

/// <summary>Actions outside the code: LAMA0806/0807 (license), LAMA0118 (build to resume the analysis).</summary>
sealed class ToolFixes : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0806", "LAMA0807", "LAMA0118"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        switch (c.Diagnostic.Id)
        {
            case "LAMA0806":
                yield return new Fix("How to register a Metalama license", ctx => ctx.Tell(
                    "Run in a terminal: 'metalama license register <LICENSE KEY>', or start an evaluation with 'metalama license try'. " +
                    "On a build server, set the MetalamaLicense MSBuild property or environment variable to the license key. " +
                    "'metalama license list' shows the registered licenses."));
                break;
            case "LAMA0807":
                yield return new Fix("How to fix the ignored license key", ctx => ctx.Tell(
                    "Run 'metalama license list' to see the registered license keys. Register a current key with " +
                    "'metalama license register <LICENSE KEY>', or correct the MetalamaLicense property or environment variable. " +
                    "Build with -p:MetalamaTraceLicensing=True to see which keys the build considers."));
                break;
            case "LAMA0118":
                if (c.Project is { } project)
                    yield return new Fix($"Build '{project.Name}' to resume Metalama analysis",
                        ctx => ProjectFixHelpers.Build(ctx, project, rebuild: false));
                break;
        }
    }
}

/// <summary>Metalama crashes and exceptions of compile-time code: open the crash report / stack trace, rebuild.</summary>
sealed class CrashFixes : FixProvider
{
    public override IEnumerable<string> Ids =>
        ["LAMA0001", "LAMA0041", "LAMA0049", "LAMA0064", "LAMA0073", "LAMA0074", "LAMA0088", "LAMA0099", "LAMA0109", "LAMA0222", "LAMA0295", "LAMA0405", "LAMA0524"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        foreach (var path in ProjectFixLogic.Paths(c.Diagnostic.Message))
        {
            // LAMA0222 names a directory with errors.txt and the transformed compile-time code.
            var file = Directory.Exists(path) ? Path.Combine(path, "errors.txt") : path;
            if (File.Exists(file))
                yield return new Fix($"Open '{Path.GetFileName(file)}'", ctx => ProjectFixHelpers.OpenFile(ctx, file));
        }
        if (c.Project is { } project)
            yield return new Fix($"Rebuild '{project.Name}'", ctx => ProjectFixHelpers.Build(ctx, project, rebuild: true));
    }
}

/// <summary>Metalama.Extensions.Architecture: LAMA0900–0908.</summary>
sealed class ArchitectureFixes : FixProvider
{
    const string ArchitectureAspects = "Metalama.Extensions.Architecture.Aspects";

    public override IEnumerable<string> Ids => ["LAMA0900", "LAMA0901", "LAMA0902", "LAMA0903", "LAMA0906", "LAMA0907", "LAMA0908"];

    public override IEnumerable<Fix> Fixes(FixContext c) => c.Diagnostic.Id switch
    {
        "LAMA0900" => Experimental(c),
        "LAMA0901" => InternalImplementation(c),
        "LAMA0902" => RegexPattern(c),
        "LAMA0903" or "LAMA0906" => NamingConvention(c),
        // 2026.1 reports a missing predicate constructor as LAMA0907 (with the message of LAMA0908).
        "LAMA0907" when c.Diagnostic.Message.Contains("constructor") => PredicateConstructor(c),
        "LAMA0908" => PredicateConstructor(c),
        _ => [],
    };

    static IEnumerable<Fix> Experimental(FixContext c)
    {
        var member = c.Target ?? (IDeclaration?)c.At<ICSharpTypeMemberDeclaration>();
        for (var declaration = member; declaration != null; declaration = declaration.GetContainingNode<IClassLikeDeclaration>())
        {
            if (declaration is IAttributesOwnerDeclaration owner && FixOps.FindAttribute(owner, "Experimental") != null) yield break;
            var target = declaration;
            yield return new Fix($"Mark '{target.DeclaredName}' [Experimental]", ctx => ProjectFixHelpers.AddAttributeTo(ctx, target, "Experimental", ArchitectureAspects));
        }
    }

    static IEnumerable<Fix> InternalImplementation(FixContext c)
    {
        if (ProjectFixHelpers.TypeAt(c) is not { DeclaredElement: { } type } implementation) yield break;
        var named = ProjectFixLogic.Quoted(c.Diagnostic.Message).FirstOrDefault();
        var interfaces = type.GetSuperTypes().Select(t => t.GetTypeElement()).OfType<IInterface>()
            .Select(i => ProjectFixHelpers.SourceOf(i)).OfType<IClassLikeDeclaration>()
            .Select(d => (Declaration: d, Attribute: FixOps.FindAttribute(d, "InternalOnlyImplement")))
            .Where(x => x.Attribute != null)
            .OrderBy(x => named != null && (named == x.Declaration.DeclaredName || named.EndsWith("." + x.Declaration.DeclaredName)) ? 0 : 1)
            .ToList();
        foreach (var (declaration, attribute) in interfaces)
        {
            var sameNamespace = declaration.DeclaredElement?.GetContainingNamespace().QualifiedName == type.GetContainingNamespace().QualifiedName;
            var topLevel = implementation.GetContainingNode<IClassLikeDeclaration>() == null;
            if (sameNamespace && topLevel && declaration.GetContainingFile() != implementation.GetContainingFile())
            {
                var projectName = declaration.GetSourceFile()?.GetProject()?.Name ?? "the project of the interface";
                yield return new Fix($"Move '{implementation.DeclaredName}' to '{projectName}' (next to '{declaration.DeclaredName}')", ctx => Move(ctx, implementation, declaration));
            }
            yield return new Fix($"Remove [InternalOnlyImplement] from '{declaration.DeclaredName}'", ctx => FixOps.RemoveAttribute(ctx, attribute!));
        }
    }

    /// <summary>Moves a type after the last type declared next to <paramref name="neighbour"/>, made public.</summary>
    static void Move(FixContext c, IClassLikeDeclaration type, IClassLikeDeclaration neighbour)
    {
        var start = type.GetDocumentStartOffset().Offset;
        var text = type.GetText();
        var edits = new List<(int Start, int End, string Text)>();
        var access = FixOps.Modifiers(type).Where(m => m.GetText() is "public" or "private" or "protected" or "internal" or "file").ToList();
        if (access.Count == 0) edits.Add((FixOps.StartAfterAttributes(type).Offset - start, FixOps.StartAfterAttributes(type).Offset - start, "public "));
        for (var i = 0; i < access.Count; i++)
        {
            var range = access[i].GetDocumentRange();
            int s = range.StartOffset.Offset - start, e = range.EndOffset.Offset - start;
            if (i > 0) while (e < text.Length && text[e] == ' ') e++;
            edits.Add((s, e, i == 0 ? "public" : ""));
        }
        foreach (var edit in edits.OrderByDescending(x => x.Start))
            text = text.Substring(0, edit.Start) + edit.Text + text.Substring(edit.End);

        var anchor = neighbour.Parent?.Children().OfType<ICSharpTypeDeclaration>().LastOrDefault() ?? (ITreeNode)neighbour;
        var sourceIndent = FixOps.IndentAt(type.GetDocumentStartOffset());
        var targetIndent = FixOps.IndentAt(anchor.GetDocumentStartOffset());
        text = text.Replace("\n" + sourceIndent, "\n" + targetIndent);
        c.Insert(anchor.GetDocumentEndOffset(), "\n\n" + targetIndent + text);
        FixOps.DeleteWithLine(c, type);
    }

    static IEnumerable<Fix> RegexPattern(FixContext c)
    {
        // Reported on the literal, or on the type whose attribute has it: then the literal with the pattern of the message.
        var named = Regex.Match(c.Diagnostic.Message, "string [`'](?<p>.+?)[`'] is not").Groups["p"].Value;
        var literal = c.At<ICSharpLiteralExpression>() ?? c.Within<ICSharpLiteralExpression>().FirstOrDefault()
            ?? ((ITreeNode?)c.Target ?? c.File)?.Descendants<ICSharpLiteralExpression>().ToEnumerable()
                .Where(l => PlainString(l) is { } value && value.Contains("*"))
                .OrderBy(l => PlainString(l) == named ? 0 : 1).FirstOrDefault();
        if (literal == null || PlainString(literal) is not { } pattern || !pattern.Contains("*")) yield break;
        var regex = ProjectFixLogic.StarToRegex(pattern);
        yield return new Fix($"Use the regular expression '{regex}'", ctx => ctx.Replace(literal, ProjectFixLogic.CSharpString(regex)));
        if (literal.GetContainingNode<IAttribute>() is { Name: { } name } && name.GetText().Contains("RegexNamingConvention"))
        {
            var starName = name.GetText().Replace("RegexNamingConvention", "NamingConvention");
            yield return new Fix($"Use [{starName}] with the star pattern", ctx => ctx.Replace(name, starName));
        }
        else if (literal.GetContainingNode<IInvocationExpression>() is { } invocation && FixOps.MethodName(invocation) == "MustRespectRegexNamingConvention")
            yield return new Fix("Use MustRespectNamingConvention with the star pattern", ctx => FixOps.RenameInvokedMethod(ctx, invocation, "MustRespectNamingConvention"));
    }

    /// <summary>The value of "…" or @"…" without escapes, else null.</summary>
    static string? PlainString(ICSharpLiteralExpression literal)
    {
        var text = literal.GetText();
        if (text.StartsWith("@\"") && text.EndsWith("\"") && text.Length >= 3) return text.Substring(2, text.Length - 3).Replace("\"\"", "\"");
        if (text.StartsWith("\"") && text.EndsWith("\"") && text.Length >= 2 && !text.Contains("\\")) return text.Substring(1, text.Length - 2);
        return null;
    }

    static IEnumerable<Fix> NamingConvention(FixContext c)
    {
        var declaration = c.Target ?? c.At<IDeclaration>();
        if (declaration == null || ProjectFixLogic.Pattern(c.Diagnostic.Message) is not { } pattern) yield break;
        // A name equal to a base type's would make the type derive from itself.
        var taken = (declaration.DeclaredElement as ITypeElement)?.GetSuperTypes().Select(t => t.GetClrName().ShortName).ToHashSet() ?? new HashSet<string>();
        foreach (var name in ProjectFixLogic.NamesMatching(declaration.DeclaredName, pattern).Where(n => !taken.Contains(n)))
            yield return new Fix($"Rename '{declaration.DeclaredName}' to '{name}'", ctx => ProjectFixHelpers.Rename(ctx, declaration, name));
    }

    static IEnumerable<Fix> PredicateConstructor(FixContext c)
    {
        var scope = c.At<IAttribute>() is { } attribute ? [attribute]
            : (c.Target as IAttributesOwnerDeclaration)?.Attributes.Cast<ITreeNode>().ToList() ?? [];
        var predicate = scope.SelectMany(n => n.Descendants<ITypeofExpression>().ToEnumerable()).Concat(c.Within<ITypeofExpression>())
            .Select(t => ProjectFixHelpers.TypeElementOf(t.ArgumentType)).FirstOrDefault(IsReferencePredicate);
        if (ProjectFixHelpers.SourceOf(predicate) is not IClassDeclaration declaration) yield break;
        var name = declaration.DeclaredName;
        var existing = declaration.ConstructorDeclarations.FirstOrDefault(k => !k.IsStatic && k.ParameterDeclarations.Count == 1
            && ProjectFixHelpers.TypeElementOf(k.ParameterDeclarations[0].Type)?.ShortName == "ReferencePredicateBuilder");
        if (existing != null)
        {
            if (!FixOps.HasModifier(existing, "public"))
                yield return new Fix($"Make the constructor of '{name}' public", ctx => FixOps.SetAccessibility(ctx, existing, "public"));
            yield break;
        }
        yield return new Fix($"Add a constructor '{name}(ReferencePredicateBuilder)'", ctx =>
        {
            var constructor = $"public {name}( ReferencePredicateBuilder builder ) : base( builder ) {{ }}";
            if (declaration.ConstructorDeclarations.FirstOrDefault() is { } first) FixOps.InsertLineAbove(ctx, first, constructor + "\n");
            else if (declaration.Body?.LBrace is { } brace)
                ctx.Insert(brace.GetDocumentEndOffset(), "\n" + FixOps.IndentAt(declaration.GetDocumentStartOffset()) + "    " + constructor + "\n");
            ProjectFixHelpers.AddUsingIn(ctx, declaration.GetContainingFile() as ICSharpFile, "Metalama.Extensions.Architecture.Predicates");
        });
    }

    static bool IsReferencePredicate(ITypeElement? type)
    {
        for (var guard = 0; type != null && guard < 20; guard++)
        {
            if (type.ShortName == "ReferencePredicate") return true;
            type = type.GetSuperTypes().Select(t => t.GetTypeElement()).OfType<IClass>().FirstOrDefault();
        }
        return false;
    }
}

/// <summary>Metalama.Extensions.Interceptors: LAMA1008 (no method of that name), LAMA1017 (interceptor placement).</summary>
sealed class InterceptorFixes : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA1008", "LAMA1017"];

    public override IEnumerable<Fix> Fixes(FixContext c) => c.Diagnostic.Id == "LAMA1008" ? MethodName(c) : Placement(c);

    static IEnumerable<Fix> MethodName(FixContext c)
    {
        var match = Regex.Match(c.Diagnostic.Message, "methods named '(?<name>[^']+)'");
        if (!match.Success || c.File == null) yield break;
        var name = match.Groups["name"].Value;
        var quoted = "\"" + name + "\"";
        bool IsName(ICSharpLiteralExpression l) => l.GetText() == quoted;
        var scope = (ITreeNode?)c.Target ?? c.File;
        var literals = scope.Descendants<ICSharpLiteralExpression>().ToEnumerable().Where(IsName).ToList();
        if (literals.Count == 0) literals = c.File.Descendants<ICSharpLiteralExpression>().ToEnumerable().Where(IsName).ToList();
        foreach (var literal in literals.Take(1))
        {
            if (SelectedType(literal) is not { } type) yield break;
            var names = new List<string>();
            for (var t = type; t != null && t.GetClrName().FullName != "System.Object"; t = t.GetSuperTypes().Select(s => s.GetTypeElement()).OfType<IClass>().FirstOrDefault())
                names.AddRange(t.Methods.Select(m => m.ShortName));
            foreach (var candidate in ProjectFixLogic.Closest(name, names))
                yield return new Fix($"Change '{name}' to '{candidate}'", ctx => ctx.Replace(literal, ProjectFixLogic.CSharpString(candidate)));
        }
    }

    /// <summary>The type of `.Type(typeof(X))` earlier in the fluent chain of <paramref name="literal"/>.</summary>
    static ITypeElement? SelectedType(ITreeNode literal)
    {
        var invocation = literal.GetContainingNode<IInvocationExpression>();
        for (var guard = 0; invocation != null && guard < 20; guard++)
        {
            if (FixOps.MethodName(invocation) == "Type" && invocation.Arguments.FirstOrDefault()?.Value is ITypeofExpression typeOf)
                return ProjectFixHelpers.TypeElementOf(typeOf.ArgumentType);
            invocation = (invocation.InvokedExpression as IReferenceExpression)?.QualifierExpression as IInvocationExpression;
        }
        return null;
    }

    static IEnumerable<Fix> Placement(FixContext c)
    {
        var redirects = Redirects(c).ToList();
        // Prefer the registrations that name the intercepted method.
        var method = (c.Target ?? c.At<IDeclaration>())?.DeclaredName;
        var naming = redirects.Where(r => method != null && Regex.IsMatch(Chain(r), $@"\b{Regex.Escape(method)}\b")).ToList();
        foreach (var redirect in (naming.Count > 0 ? naming : redirects).Take(1))
            yield return new Fix("Place the interceptor in the calling type (.PlaceInCallingType())", ctx =>
            {
                var name = (redirect.InvokedExpression as IReferenceExpression)?.NameIdentifier;
                ctx.Insert(redirect.GetDocumentEndOffset(), "\n" + FixOps.IndentAt((name ?? (ITreeNode)redirect).GetDocumentStartOffset()) + ".PlaceInCallingType()");
            });
    }

    /// <summary>
    /// `.RedirectToSynthesizedMethod(…)` calls without a placement, in this file and in the aspects applied to the target
    /// and its containing types.
    /// </summary>
    static IEnumerable<IInvocationExpression> Redirects(FixContext c)
    {
        var files = new List<ICSharpFile>();
        if (c.File != null) files.Add(c.File);
        for (var declaration = c.Target ?? c.At<IDeclaration>(); declaration != null; declaration = declaration.GetContainingNode<IClassLikeDeclaration>())
            if (declaration is IAttributesOwnerDeclaration owner)
                foreach (var attribute in owner.Attributes)
                    if (attribute.Name?.Reference.Resolve().DeclaredElement is ITypeElement type)
                        files.AddRange(type.GetDeclarations().Select(d => d.GetContainingFile()).OfType<ICSharpFile>());
        return files.Distinct().SelectMany(f => f.Descendants<IInvocationExpression>().ToEnumerable())
            .Where(i => FixOps.MethodName(i) == "RedirectToSynthesizedMethod"
                        && !(ReferenceExpressionNavigator.GetByQualifierExpression(i)?.NameIdentifier?.Name.StartsWith("PlaceIn") ?? false));
    }

    /// <summary>The text of the fluent chain a call belongs to.</summary>
    static string Chain(IInvocationExpression invocation)
    {
        ITreeNode top = invocation;
        while (top.Parent is IReferenceExpression or IInvocationExpression) top = top.Parent;
        return top.GetText();
    }
}
