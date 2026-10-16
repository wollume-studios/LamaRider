using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Application.Progress;
using JetBrains.DocumentModel;
using JetBrains.Metadata.Reader.Impl;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Bulbs;
using JetBrains.ReSharper.Feature.Services.ContextActions;
using JetBrains.ReSharper.Feature.Services.CSharp.ContextActions;
using JetBrains.ReSharper.Feature.Services.Intentions;
using JetBrains.ReSharper.Feature.Services.Protocol;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CodeStyle;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Modules;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.Util;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.Rider.Model;
using JetBrains.TextControl;
using JetBrains.Util;
using LamaRider.Model;

namespace LamaRider.Actions;

/// <summary>The declaration at the caret and the worker's target id for it (parameters: "M:...;Parameter=0").</summary>
static class AspectTargets
{
    public static (IDeclaration Declaration, string Target)? AtCaret(ICSharpContextActionDataProvider provider) =>
        Of(provider.GetSelectedElement<IDeclaration>(), provider.DocumentSelection);

    /// <summary>The editor menu twins: the declaration whose name is at <paramref name="offset"/>.</summary>
    public static (IDeclaration Declaration, string Target)? At(IFile file, DocumentOffset offset)
    {
        var range = new DocumentRange(offset);
        return Of(file.FindNodeAt(file.Translate(offset))?.GetContainingNode<IDeclaration>(returnThis: true), range);
    }

    static (IDeclaration Declaration, string Target)? Of(IDeclaration? declaration, DocumentRange caret)
    {
        if (declaration?.DeclaredElement is not { } element) return null;
        // Caret on the name, not anywhere in a long body.
        if (!declaration.GetNameDocumentRange().Contains(caret)) return null;
        if (element is IParameter { ContainingParametersOwner: ITypeMember owner } parameter && owner is IParametersOwner parameters)
            return DocId(owner) is { } ownerId ? (declaration, AspectHints.ParameterTarget(ownerId, parameters.Parameters.IndexOf(parameter))) : null;
        return DocId(element) is { } id ? (declaration, id) : null;
    }

    public static string? DocId(IDeclaredElement element) => element switch
    {
        ITypeElement type => XMLDocUtil.GetTypeElementXmlDocId(type),
        ITypeMember member => XMLDocUtil.GetTypeMemberXmlDocId(member, member.ShortName),
        _ => null,
    };

    /// <summary>The aspect class of a worker id ("T:Ns.Outer.Inner"): nested types are '+' in CLR names.</summary>
    public static ITypeElement? AspectClass(string classId, IPsiModule module)
    {
        var name = classId.StartsWith("T:") ? classId.Substring(2) : classId;
        var scope = module.GetPsiServices().Symbols.GetSymbolScope(module, withReferences: true, caseSensitive: true);
        for (var candidate = name; ; )
        {
            if (scope.GetTypeElementByCLRName(new ClrTypeName(candidate)) is { } type) return type;
            var dot = candidate.LastIndexOf('.');
            if (dot < 0) return null;
            candidate = candidate.Substring(0, dot) + "+" + candidate.Substring(dot + 1);
        }
    }

    public static void Info(ISolution solution, string message) => solution.GetProtocolSolution().GetLamaRiderModel().Info.Fire(message);
}

/// <summary>
/// Writes an aspect that a fabric, inheritance or another aspect adds as an attribute on the declaration, with the same
/// constructor arguments and properties (read from the aspect object by the worker). Metalama then runs the attribute's
/// instance (attributes are primary over fabric-added instances).
/// </summary>
public static class AddAspectAsAttribute
{
    public static List<AspectInstanceInfo> Candidates(AspectModel model, IDeclaration declaration, string target) =>
        declaration is not IAttributesOwnerDeclaration ? [] : model.InstancesOf(AspectModel.Owner(target))
            .Where(i => i.Target == target && i.AppliedVia != "Attribute" && i.Attribute != null)
            .GroupBy(i => i.AspectClass).Select(g => g.First())
            .ToList();

    public static string Name(AspectInstanceInfo instance)
    {
        var name = instance.AspectClass.Substring(instance.AspectClass.LastIndexOf('.') + 1);
        return name.EndsWith("Attribute") && name.Length > 9 ? name.Substring(0, name.Length - 9) : name;
    }

    /// <summary>Call in a PSI transaction.</summary>
    public static bool Add(IDeclaration declaration, AspectInstanceInfo instance)
    {
        if (declaration is not IAttributesOwnerDeclaration owner || instance.Attribute is not { } setup) return false;
        if (AspectTargets.AspectClass(instance.AspectClass, declaration.GetPsiModule()) is not { } type) return false;
        var factory = CSharpElementFactory.GetInstance(declaration);
        var attribute = factory.CreateAttribute(type,
            setup.Arguments.Select(a => (ICSharpExpression)factory.CreateExpression(a)).ToArray(),
            setup.Named.Select(n => new Pair<string, ICSharpExpression>(n.Name, factory.CreateExpression(n.Value))).ToArray());
        CodeStyleUtil.ApplyStyle(owner.AddAttributeAfter(attribute, null)); // shortens the global:: names
        return true;
    }

    public static string Done(AspectInstanceInfo instance) =>
        $"Added [{Name(instance)}]. {AspectHints.Origin(instance.AppliedVia, instance.AppliedBy)} still adds the aspect too; Metalama runs the attribute's instance.";
}

[ContextAction(GroupType = typeof(CSharpContextActions), Name = "Add Metalama aspect as attribute",
    Description = "Writes an aspect that a fabric, inheritance or another aspect adds as an attribute on the declaration, with the same settings.")]
public sealed class AddAspectAsAttributeAction(ICSharpContextActionDataProvider provider) : IContextAction
{
    public IEnumerable<IntentionAction> CreateBulbItems() =>
        Candidates().Select(i => (BulbActionBase)new Item(provider, i)).ToContextActionIntentions();

    public bool IsAvailable(IUserDataHolder cache) => Candidates().Any();

    List<AspectInstanceInfo> Candidates() =>
        AspectTargets.AtCaret(provider) is var (declaration, target) && provider.Solution.GetComponent<AspectModelHost>().Model is { } model
            ? AddAspectAsAttribute.Candidates(model, declaration, target)
            : [];

    sealed class Item(ICSharpContextActionDataProvider provider, AspectInstanceInfo instance) : BulbActionBase
    {
        public override string Text =>
            $"Add [{AddAspectAsAttribute.Name(instance)}] as attribute ({AspectHints.Lower(AspectHints.Origin(instance.AppliedVia, instance.AppliedBy))})";

        protected override Action<ITextControl>? ExecutePsiTransaction(ISolution solution, IProgressIndicator progress)
        {
            if (AspectTargets.AtCaret(provider) is var (declaration, _) && AddAspectAsAttribute.Add(declaration, instance))
                AspectTargets.Info(solution, AddAspectAsAttribute.Done(instance));
            return null;
        }
    }
}

/// <summary>
/// Replaces a type's or member's source with its transformed code, like <c>metalama divorce</c> for one declaration. The
/// worker removes aspect attributes and adds [ExcludeAspect] for aspects a fabric, inheritance or another aspect would add again.
/// </summary>
public static class ReplaceWithTransformedCode
{
    public sealed record Replacement(IDocument Document, TextRange Range, string Code, IReadOnlyList<string> Warnings);

    public static Replacement? Find(AspectModel model, IDeclaration declaration, string target)
    {
        // Parameters: their member is replaced.
        if (target.Contains(';') || declaration.GetSourceFile()?.GetLocation().FullPath is not { } path) return null;
        var code = model.StandaloneOf(path, target);
        var range = declaration.GetDocumentRange();
        // Stale model guard: Roslyn's and ReSharper's declarations end at the same offset.
        if (code == null || code.SourceEnd != range.EndOffset.Offset) return null;
        var inside = declaration.Descendants<IDeclaration>().ToEnumerable().Prepend(declaration)
            .Select(d => d.DeclaredElement is { } e ? AspectTargets.DocId(e) : null).OfType<string>().ToHashSet();
        return new Replacement(range.Document, new TextRange(code.SourceStart!.Value, code.SourceEnd.Value), code.Standalone!,
            ReplaceWithTransformed.Warnings(model, inside));
    }

    /// <summary>A document edit: outside PSI transactions.</summary>
    public static void Apply(ISolution solution, Replacement replacement)
    {
        using (WriteLockCookie.Create()) replacement.Document.ReplaceText(replacement.Range, replacement.Code);
        if (replacement.Warnings.Count > 0) AspectTargets.Info(solution, string.Join("\n", replacement.Warnings));
    }
}

[ContextAction(GroupType = typeof(CSharpContextActions), Name = "Replace with Metalama transformed code",
    Description = "Replaces a declaration's source with the code Metalama generates for it and removes its aspects (divorce for one declaration).")]
public sealed class ReplaceWithTransformedCodeAction(ICSharpContextActionDataProvider provider) : ContextActionBase
{
    public override string Text => "Replace with transformed code (remove its aspects)";

    public override bool IsAvailable(IUserDataHolder cache) => Find() != null;

    ReplaceWithTransformedCode.Replacement? Find() =>
        AspectTargets.AtCaret(provider) is var (declaration, target) && provider.Solution.GetComponent<AspectModelHost>().Model is { } model
            ? ReplaceWithTransformedCode.Find(model, declaration, target)
            : null;

    protected override Action<ITextControl>? ExecutePsiTransaction(ISolution solution, IProgressIndicator progress) =>
        Find() is { } replacement ? _ => ReplaceWithTransformedCode.Apply(solution, replacement) : null;
}
