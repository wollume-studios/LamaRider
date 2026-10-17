using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JetBrains.Application.Progress;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.Protocol;
using JetBrains.ReSharper.Feature.Services.QuickFixes;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Modules;
using JetBrains.ReSharper.Psi.Resolve;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.Rider.Model;
using JetBrains.TextControl;
using JetBrains.Util;
using LamaRider.Model;

namespace LamaRider.Premium;

/// <summary>Uses of Metalama Premium components, which need a Metalama Professional license (LAMA0806 without).</summary>
public static class PremiumUses
{
    /// <summary>The Premium component a reference resolves into, or null.</summary>
    public static string? ComponentOf(ICSharpTreeNode node)
    {
        IReference? reference = node switch
        {
            IReferenceExpression e => e.Reference,
            IReferenceName n => n.Reference,
            _ => null,
        };
        switch (reference?.Resolve().DeclaredElement)
        {
            // Namespaces only in using directives: the types themselves are flagged where they are used.
            case INamespace ns when node.GetContainingNode<IUsingDirective>() != null:
                return PremiumBuild.IsPremiumNamespace(ns.QualifiedName) ? ns.QualifiedName : null;
            case IClrDeclaredElement element when element.Module is IAssemblyPsiModule assembly:
                var name = assembly.Assembly.AssemblyName.Name;
                return PremiumBuild.IsPremium(name) ? name : null;
            default:
                return null;
        }
    }

    /// <summary>The Premium reference at or around <paramref name="node"/>.</summary>
    public static ICSharpTreeNode? UseAt(ITreeNode node)
    {
        foreach (var n in node.ContainingNodes(returnThis: true))
            if (n is ICSharpTreeNode c && ComponentOf(c) != null) return c;
        return null;
    }

    public static bool IsGuarded(ITreeNode node) =>
        PremiumBuild.IsGuarded(node.GetDocumentRange().Document.GetText(), node.GetDocumentStartOffset().Offset);

    /// <summary>
    /// Where to put <c>#if METALAMA_PREMIUM</c> / <c>#endif</c>: around the closest using directive, attribute section,
    /// statement or member of the use that has its lines to itself, and around the file's Premium using directives, which
    /// would not compile in a free build either.
    /// </summary>
    public static List<(int Before, int After)> Wraps(ITreeNode use)
    {
        var text = use.GetDocumentRange().Document.GetText();
        (int, int)? Points(ITreeNode n) =>
            PremiumBuild.WrapPoints(text, n.GetDocumentStartOffset().Offset, n.GetDocumentEndOffset().Offset);

        var result = new List<(int Before, int After)>();
        foreach (var node in use.ContainingNodes(returnThis: true))
        {
            var candidate = node is IUsingDirective or IAttributeSection or IMultipleDeclaration or ITypeMemberDeclaration
                || node is ICSharpStatement && node.Parent is IBlock;
            if (candidate && Points(node) is { } points) { result.Add(points); break; }
        }
        if (use.GetContainingFile() is ICSharpFile file)
            foreach (var directive in file.Descendants<IUsingSymbolDirective>())
                if (directive.ImportedSymbolName?.QualifiedName is { } ns && PremiumBuild.IsPremiumNamespace(ns)
                    && !PremiumBuild.IsGuarded(text, directive.GetDocumentStartOffset().Offset) && Points(directive) is { } points)
                    result.Add(points);
        // Outer blocks first would shift inner ones: from the end of the file, and a block once.
        return result.Distinct().Where(w => !result.Any(o => o != w && o.Before <= w.Before && w.After <= o.After)).OrderByDescending(w => w.Before).ToList();
    }

    /// <summary>A document edit: outside PSI transactions.</summary>
    public static void Wrap(IDocument document, IEnumerable<(int Before, int After)> wraps)
    {
        using (WriteLockCookie.Create())
            foreach (var (before, after) in wraps)
            {
                document.InsertText(after, "\n#endif");
                document.InsertText(before, $"#if {PremiumBuild.Symbol}\n");
            }
    }

    /// <summary>Sets up the free and Premium builds in the project file; what was done, for the user.</summary>
    public static string SetUpBuilds(IProject project)
    {
        var path = project.ProjectFileLocation.FullPath;
        try
        {
            if (PremiumBuild.SetUp(File.ReadAllText(path)) is not var (text, packages))
                return $"{project.Name} already has free and Premium builds ({PremiumBuild.Symbol}).";
            File.WriteAllText(path, text);
            return $"{project.Name} now builds without Metalama Premium by default. Premium build: -p:{PremiumBuild.Property}=true or the {PremiumBuild.Property} " +
                   "environment variable. " + (packages.Count > 0
                       ? $"Only the Premium build references {string.Join(", ", packages)}."
                       : "The project file references no Premium package itself: make that reference conditional where it is (Directory.Build.props, another project).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Cannot change {Path.GetFileName(path)}: {e.Message}";
        }
    }

    public static bool HasBuilds(IProject? project) =>
        project?.ProjectFileLocation.FullPath is { } path && File.Exists(path) && File.ReadAllText(path).Contains(PremiumBuild.Symbol);
}

[ElementProblemAnalyzer(typeof(IReferenceExpression), typeof(IReferenceName), HighlightingTypes = new[] { typeof(MetalamaPremiumUseHighlighting) })]
public sealed class PremiumUseAnalyzer : ElementProblemAnalyzer<ICSharpTreeNode>
{
    protected override void Run(ICSharpTreeNode element, ElementProblemAnalyzerData data, IHighlightingConsumer consumer)
    {
        if (PremiumUses.ComponentOf(element) is not { } component) return;
        // One warning for "CodeFixFactory.AddAttribute": on the member, not also on its qualifier.
        if (element.Parent is IReferenceExpression parent && parent.QualifierExpression == element && PremiumUses.ComponentOf(parent) != null) return;
        if (element.Parent is IReferenceName outer && outer.Qualifier == element) return;
        if (PremiumUses.IsGuarded(element)) return;
        var range = element switch
        {
            IReferenceExpression e => e.NameIdentifier?.GetDocumentRange() ?? e.GetDocumentRange(),
            IReferenceName n => n.NameIdentifier?.GetDocumentRange() ?? n.GetDocumentRange(),
            _ => element.GetDocumentRange(),
        };
        consumer.AddHighlighting(new MetalamaPremiumUseHighlighting(element, component, range));
    }
}

[RegisterConfigurableSeverity(SeverityId, null, HighlightingGroupIds.CodeSmell, "Metalama Premium API",
    "Uses a Metalama Premium component, which needs a Metalama Professional license: without it, the build fails with LAMA0806.", JetBrains.ReSharper.Feature.Services.Daemon.Severity.WARNING)]
[ConfigurableSeverityHighlighting(SeverityId, CSharpLanguage.Name, OverlapResolve = OverlapResolveKind.WARNING)]
public sealed class MetalamaPremiumUseHighlighting(ICSharpTreeNode node, string component, DocumentRange range) : IHighlighting
{
    public const string SeverityId = "LamaRider.MetalamaPremiumApi";

    public ICSharpTreeNode Node => node;
    public string ToolTip =>
        $"Metalama Premium ({component}): needs a Metalama Professional license; without it, the build fails (LAMA0806). " +
        $"For a free build, put it in #if {PremiumBuild.Symbol}.";
    public string ErrorStripeToolTip => ToolTip;
    public bool IsValid() => node.IsValid();
    public DocumentRange CalculateRange() => range;
}

[QuickFix]
public sealed class WrapInPremiumIfFix(MetalamaPremiumUseHighlighting highlighting) : QuickFixBase
{
    public override string Text => $"Put it in #if {PremiumBuild.Symbol}";
    public override bool IsAvailable(IUserDataHolder cache) => highlighting.IsValid() && PremiumUses.Wraps(highlighting.Node).Count > 0;

    protected override Action<ITextControl>? ExecutePsiTransaction(ISolution solution, IProgressIndicator progress)
    {
        var wraps = PremiumUses.Wraps(highlighting.Node);
        var document = highlighting.Node.GetDocumentRange().Document;
        var project = highlighting.Node.GetProject();
        return _ =>
        {
            PremiumUses.Wrap(document, wraps);
            if (!PremiumUses.HasBuilds(project))
                solution.GetProtocolSolution().GetLamaRiderModel().Info.Fire(
                    $"{PremiumBuild.Symbol} is not defined yet: Alt+Enter > Set up free and Premium builds adds it to the project.");
        };
    }
}

[QuickFix]
public sealed class SetUpPremiumBuildsFix(MetalamaPremiumUseHighlighting highlighting) : QuickFixBase
{
    public override string Text => "Set up free and Premium builds of this project";
    public override bool IsAvailable(IUserDataHolder cache) => highlighting.IsValid() && highlighting.Node.GetProject() is { } p && !PremiumUses.HasBuilds(p);

    protected override Action<ITextControl>? ExecutePsiTransaction(ISolution solution, IProgressIndicator progress)
    {
        var project = highlighting.Node.GetProject()!;
        return _ => solution.GetProtocolSolution().GetLamaRiderModel().Info.Fire(PremiumUses.SetUpBuilds(project));
    }
}
