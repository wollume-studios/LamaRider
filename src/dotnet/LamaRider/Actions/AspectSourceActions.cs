using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Application.Progress;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.Metadata.Reader.API;
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
using JetBrains.Rider.Model;
using JetBrains.TextControl;
using JetBrains.Util;
using LamaRider.Model;

namespace LamaRider.Actions;

/// <summary>The declaration at the caret and the worker's target id for it (parameters: "M:...;Parameter=0").</summary>
static class AspectTargets
{
    public static (IDeclaration Declaration, string Target)? AtCaret(ICSharpContextActionDataProvider provider)
    {
        var declaration = provider.GetSelectedElement<IDeclaration>();
        if (declaration?.DeclaredElement is not { } element) return null;
        // Caret on the name, not anywhere in a long body.
        if (!declaration.GetNameDocumentRange().Contains(provider.DocumentSelection)) return null;
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
}

/// <summary>
/// Alt+Enter on a declaration that gets an aspect from a fabric, inheritance or another aspect: write it as an attribute
/// with the same constructor arguments and properties (read from the aspect object by the worker). Metalama then runs the
/// attribute's instance (attributes are primary over fabric-added instances).
/// </summary>
[ContextAction(GroupType = typeof(CSharpContextActions), Name = "Add Metalama aspect as attribute",
    Description = "Writes an aspect that a fabric, inheritance or another aspect adds as an attribute on the declaration, with the same settings.")]
public sealed class AddAspectAsAttributeAction(ICSharpContextActionDataProvider provider) : IContextAction
{
    public IEnumerable<IntentionAction> CreateBulbItems() =>
        Candidates().Select(i => (BulbActionBase)new Item(provider, i)).ToContextActionIntentions();

    public bool IsAvailable(IUserDataHolder cache) => Candidates().Any();

    List<AspectInstanceInfo> Candidates()
    {
        if (AspectTargets.AtCaret(provider) is not var (declaration, target) || declaration is not IAttributesOwnerDeclaration) return [];
        if (provider.Solution.GetComponent<AspectModelHost>().Model is not { } model) return [];
        return model.InstancesOf(AspectModel.Owner(target))
            .Where(i => i.Target == target && i.AppliedVia != "Attribute" && i.Attribute != null)
            .GroupBy(i => i.AspectClass).Select(g => g.First())
            .ToList();
    }

    sealed class Item(ICSharpContextActionDataProvider provider, AspectInstanceInfo instance) : BulbActionBase
    {
        public override string Text
        {
            get
            {
                var name = instance.AspectClass.Substring(instance.AspectClass.LastIndexOf('.') + 1);
                if (name.EndsWith("Attribute") && name.Length > 9) name = name.Substring(0, name.Length - 9);
                return $"Add [{name}] as attribute ({AspectHints.Origin(instance.AppliedVia, instance.AppliedBy).ToLowerInvariant()})";
            }
        }

        protected override Action<ITextControl>? ExecutePsiTransaction(ISolution solution, IProgressIndicator progress)
        {
            if (AspectTargets.AtCaret(provider) is not ({ } declaration, _) || declaration is not IAttributesOwnerDeclaration owner) return null;
            if (AspectTargets.AspectClass(instance.AspectClass, provider.PsiModule) is not { } type) return null;
            var setup = instance.Attribute!;
            var factory = CSharpElementFactory.GetInstance(declaration);
            var attribute = factory.CreateAttribute(type,
                setup.Arguments.Select(a => (ICSharpExpression)factory.CreateExpression(a)).ToArray(),
                setup.Named.Select(n => new Pair<string, ICSharpExpression>(n.Name, factory.CreateExpression(n.Value))).ToArray());
            var added = owner.AddAttributeAfter(attribute, null);
            CodeStyleUtil.ApplyStyle(added); // shortens the global:: names
            solution.GetProtocolSolution().GetLamaRiderModel().Info.Fire(
                $"Added the attribute. {AspectHints.Origin(instance.AppliedVia, instance.AppliedBy)} still adds the aspect too; Metalama runs the attribute's instance.");
            return null;
        }
    }
}

/// <summary>
/// Alt+Enter on a type or member with aspects: replace its source with its transformed code, like <c>metalama divorce</c>
/// for one declaration. The worker removes aspect attributes and adds [ExcludeAspect] for aspects a fabric, inheritance or
/// another aspect would add again.
/// </summary>
[ContextAction(GroupType = typeof(CSharpContextActions), Name = "Replace with Metalama transformed code",
    Description = "Replaces a declaration's source with the code Metalama generates for it and removes its aspects (divorce for one declaration).")]
public sealed class ReplaceWithTransformedCodeAction(ICSharpContextActionDataProvider provider) : ContextActionBase
{
    public override string Text => "Replace with transformed code (remove its aspects)";

    public override bool IsAvailable(IUserDataHolder cache) => Find() != null;

    (IDeclaration Declaration, TransformedDeclaration Code, AspectModel Model)? Find()
    {
        // Parameters: their member is replaced.
        if (AspectTargets.AtCaret(provider) is not var (declaration, target) || target.Contains(';')) return null;
        if (provider.Solution.GetComponent<AspectModelHost>().Model is not { } model) return null;
        if (declaration.GetSourceFile()?.GetLocation().FullPath is not { } path) return null;
        var code = model.StandaloneOf(path, target);
        // Stale model guard: Roslyn's and ReSharper's declarations end at the same offset.
        return code != null && code.SourceEnd == declaration.GetDocumentRange().EndOffset.Offset ? (declaration, code, model) : null;
    }

    protected override Action<ITextControl>? ExecutePsiTransaction(ISolution solution, IProgressIndicator progress)
    {
        if (Find() is not var (declaration, code, model)) return null;
        var inside = declaration.Descendants<IDeclaration>().ToEnumerable().Prepend(declaration)
            .Select(d => d.DeclaredElement is { } e ? AspectTargets.DocId(e) : null).OfType<string>().ToHashSet();
        var warnings = ReplaceWithTransformed.Warnings(model, inside);
        var document = declaration.GetDocumentRange().Document;
        var range = new TextRange(code.SourceStart!.Value, code.SourceEnd!.Value);
        return _ =>
        {
            using (WriteLockCookie.Create()) document.ReplaceText(range, code.Standalone!);
            if (warnings.Count > 0) solution.GetProtocolSolution().GetLamaRiderModel().Info.Fire(string.Join("\n", warnings));
        };
    }
}
