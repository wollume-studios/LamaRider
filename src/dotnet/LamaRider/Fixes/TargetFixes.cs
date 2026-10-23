using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.Util;
using LamaRider.Model;
using LamaRider.Testing;
using static LamaRider.Model.TargetFixLogic;

namespace LamaRider.Fixes;

/// <summary>
/// Diagnostics reported on an aspect's target, fixed on the target (partial, modifiers, accessibility, moving the aspect)
/// or in the aspect's source (templates, eligibility, [AttributeUsage], aspect order).
/// </summary>
public sealed class TargetFixes : FixProvider
{
    public override IEnumerable<string> Ids =>
    [
        "LAMA0048", "LAMA0535", "LAMA0037", "LAMA0003", "LAMA0025", "LAMA0115", "LAMA0223", "LAMA0114", "LAMA0287", "LAMA0247",
        "LAMA0063", "LAMA0650", "LAMA0091", "LAMA0401", "LAMA0702", "LAMA0704", "LAMA0750", "LAMA0652", "LAMA0661",
    ];

    public override IEnumerable<Fix> Fixes(FixContext c) => c.Diagnostic.Id switch
    {
        "LAMA0048" => MakePartial(c),
        "LAMA0535" => MakePartial(c).Concat(RemoveIsPartial(c)),
        "LAMA0037" => Eligibility(c),
        "LAMA0003" => AttributeUsage(c),
        "LAMA0025" => TemplateName(c),
        "LAMA0115" => MetaTarget(c),
        "LAMA0223" => ProceedVariant(c),
        "LAMA0114" => Instance(c, ["This", "Base"], static_: true),
        "LAMA0287" => Instance(c, ["Receiver"], static_: false),
        "LAMA0247" => ReturnTypeEligibility(c),
        "LAMA0063" => InvokerOptions(c, ["Base", "Current"], ["Default", "Final"], withObjectOnly: false),
        "LAMA0650" => InvokerOptions(c, ["Base"], ["Final"], withObjectOnly: true),
        "LAMA0091" => AspectOrder(c),
        "LAMA0401" => RunTimeOrCompileTime(c),
        "LAMA0702" => NonStaticDependency(c),
        "LAMA0704" => DependencyAccessibility(c),
        "LAMA0750" => MoveToUsingType(c),
        "LAMA0652" => NonOverridable(c),
        "LAMA0661" => RenameVariable(c),
        _ => [],
    };

    // --- On the target ---------------------------------------------------------------------------------------------

    static ICSharpTypeDeclaration? TargetType(FixContext c) => c.Target as ICSharpTypeDeclaration ?? c.At<ICSharpTypeDeclaration>();

    /// <summary>LAMA0048, LAMA0535: the type and the types containing it become partial.</summary>
    static IEnumerable<Fix> MakePartial(FixContext c)
    {
        if (TargetType(c) is not { } type || type is IEnumDeclaration || type is IDelegateDeclaration) yield break;
        var types = SelfAndContainingTypes(type).Where(t => !FixOps.HasModifier(t, "partial")).ToList();
        if (types.Count == 0) yield break;
        yield return new Fix($"Make '{type.DeclaredName}' partial", ctx => types.ForEach(t => FixOps.AddModifier(ctx, t, "partial")));
    }

    static IEnumerable<ICSharpTypeDeclaration> SelfAndContainingTypes(ICSharpTypeDeclaration type)
    {
        for (var t = type; t != null; t = t.GetContainingNode<ICSharpTypeDeclaration>()) yield return t;
    }

    /// <summary>LAMA0535: the aspect's template doesn't need to be partial.</summary>
    static IEnumerable<Fix> RemoveIsPartial(FixContext c)
    {
        var member = Quoted(c.Diagnostic.Message).Select(ShortName).ToList();
        foreach (var aspect in AspectClasses(c))
        foreach (var declaration in aspect.MemberDeclarations.OfType<IAttributesOwnerDeclaration>())
            if (member.Contains(declaration.DeclaredName) && FixOps.FindAttribute(declaration, "Template") is { } template
                && template.PropertyAssignments.Any(p => p.PropertyNameIdentifier?.Name == "IsPartial"))
                yield return new Fix($"Remove 'IsPartial = true' from template '{declaration.DeclaredName}'", ctx => FixOps.RemoveAttributeProperty(ctx, template, "IsPartial"));
    }

    /// <summary>LAMA0037: change the target to satisfy the condition, or add the aspect with a fabric only where eligible.</summary>
    static IEnumerable<Fix> Eligibility(FixContext c)
    {
        var declarations = new List<IDeclaration>();
        if (c.Target is { } target) declarations.Add(target);
        if (c.Target is { } t2 && t2.GetContainingNode<ICSharpTypeDeclaration>() is { } containing) declarations.AddRange(SelfAndContainingTypes(containing));
        foreach (var change in ModifierChanges(c.Diagnostic.Message))
        {
            if (declarations.FirstOrDefault(d => d.DeclaredName == ShortName(change.Subject)) is not ICSharpModifiersOwnerDeclaration declaration) continue;
            if (change.Add && !FixOps.HasModifier(declaration, change.Modifier))
                yield return new Fix($"Make '{declaration.DeclaredName}' {change.Modifier}", ctx => FixOps.AddModifier(ctx, declaration, change.Modifier));
            else if (!change.Add && FixOps.HasModifier(declaration, change.Modifier))
                yield return new Fix($"Remove '{change.Modifier}' from '{declaration.DeclaredName}'", ctx => FixOps.RemoveModifier(ctx, declaration, change.Modifier));
        }

        // Added by a fabric (no attribute on the target): AddAspectIfEligible skips the ineligible declarations.
        var aspectName = Quoted(c.Diagnostic.Message).FirstOrDefault();
        if (aspectName == null || c.Target is IAttributesOwnerDeclaration owner && owner.Attributes.Any(a => FixOps.ShortName(a) == AttributeName(aspectName))) yield break;
        foreach (var type in AspectTypesNamed(c, aspectName))
        foreach (var invocation in References(type).Select(r => r.GetContainingNode<IInvocationExpression>()).OfType<IInvocationExpression>().Distinct())
            if (FixOps.MethodName(invocation) == "AddAspect")
                yield return new Fix($"Use AddAspectIfEligible instead of AddAspect for [{AttributeName(type.ShortName)}]", ctx => FixOps.RenameInvokedMethod(ctx, invocation, "AddAspectIfEligible"));
    }

    /// <summary>LAMA0702: an instance dependency, accessed through `this`.</summary>
    static IEnumerable<Fix> NonStaticDependency(FixContext c)
    {
        if (c.Target is not ICSharpModifiersOwnerDeclaration { DeclaredElement: { } element } member || !FixOps.HasModifier(member, "static")) yield break;
        yield return new Fix($"Make '{member.DeclaredName}' non-static", ctx =>
        {
            FixOps.RemoveModifier(ctx, member, "static");
            foreach (var reference in References(element).OfType<IReferenceExpression>())
            {
                if (reference.GetContainingNode<ICSharpModifiersOwnerDeclaration>() is { } user && FixOps.HasModifier(user, "static")) continue;
                if (reference.QualifierExpression == null) ctx.Insert(reference.GetDocumentStartOffset(), "this.");
                else if (reference.QualifierExpression is IReferenceExpression qualifier && qualifier.Reference.Resolve().DeclaredElement is ITypeElement)
                    ctx.Replace(qualifier, "this");
            }
        });
    }

    /// <summary>LAMA0704: the dependency type at least as accessible as the constructor, or the target type less accessible.</summary>
    static IEnumerable<Fix> DependencyAccessibility(FixContext c)
    {
        if (Quoted(c.Diagnostic.Message).FirstOrDefault() is not { } dependency) yield break;
        foreach (var type in SourceTypesNamed(c, ShortName(dependency)))
            if (type.GetDeclarations().OfType<ICSharpTypeDeclaration>().Where(d => !FixOps.HasModifier(d, "public")).ToList() is { Count: > 0 } declarations)
                yield return new Fix($"Make '{type.ShortName}' public", ctx => declarations.ForEach(d => FixOps.SetAccessibility(ctx, d, "public")));
        if (TargetType(c) is { } target && FixOps.HasModifier(target, "public") && target.GetContainingNode<ICSharpTypeDeclaration>() == null)
            yield return new Fix($"Make '{target.DeclaredName}' internal", ctx => FixOps.SetAccessibility(ctx, target, "internal"));
    }

    /// <summary>LAMA0750: the aspect on a type that uses the enum, which can receive members (and is partial).</summary>
    static IEnumerable<Fix> MoveToUsingType(FixContext c)
    {
        if (c.Target is not ICSharpTypeDeclaration { DeclaredElement: { } enumType } target || c.File == null) yield break;
        foreach (var attribute in AspectAttributes(c, target))
        foreach (var type in c.File.Descendants<IClassLikeDeclaration>().ToEnumerable())
        {
            if (type is IInterfaceDeclaration || type.DeclaredElement is not { } element || FixOps.FindAttribute(type, FixOps.ShortName(attribute)) != null) continue;
            if (!element.GetMembers().OfType<ITypeOwner>().Any(m => m.Type is IDeclaredType t && Equals(t.GetTypeElement(), enumType))) continue;
            yield return new Fix($"Move [{FixOps.ShortName(attribute)}] to '{type.DeclaredName}'", ctx =>
            {
                FixOps.RemoveAttribute(ctx, attribute);
                FixOps.AddAttribute(ctx, type, attribute.GetText());
                foreach (var t in SelfAndContainingTypes(type)) FixOps.AddModifier(ctx, t, "partial");
            });
        }
    }

    /// <summary>LAMA0652: the property can't be overridden.</summary>
    static IEnumerable<Fix> NonOverridable(FixContext c)
    {
        var property = c.Target as IPropertyDeclaration ?? c.At<IPropertyDeclaration>();
        if (property == null && Quoted(c.Diagnostic.Message).Skip(2).FirstOrDefault() is { } name)
            property = c.File?.Descendants<IPropertyDeclaration>().ToEnumerable().FirstOrDefault(p => p.DeclaredName == ShortName(name));
        if (property == null) yield break;
        if (FixOps.HasModifier(property, "virtual"))
            yield return new Fix($"Remove 'virtual' from '{property.DeclaredName}'", ctx => FixOps.RemoveModifier(ctx, property, "virtual"));
        else if (FixOps.HasModifier(property, "override") && !FixOps.HasModifier(property, "sealed"))
            yield return new Fix($"Make '{property.DeclaredName}' sealed", ctx => FixOps.AddModifier(ctx, property, "sealed"));
    }

    /// <summary>LAMA0661: a name that doesn't hide the parameter (the singular of the collection a lambda iterates, Metalama's).</summary>
    static IEnumerable<Fix> RenameVariable(FixContext c)
    {
        if (Renamed(c.Diagnostic.Message) is not { } renamed) yield break;
        var declaration = c.At<IDeclaration>() is { } d && d.DeclaredName == renamed.Name ? d
            : c.Within<IDeclaration>().FirstOrDefault(x => x.DeclaredName == renamed.Name);
        if (declaration == null) yield break;
        var names = new List<string>();
        if (declaration.DeclaredElement is IParameter && declaration.GetContainingNode<ILambdaExpression>()?.GetContainingNode<IInvocationExpression>() is { } call)
            names.AddRange(call.Arguments.Select(a => a.Value).OfType<IReferenceExpression>()
                .Select(r => r.NameIdentifier?.Name is { } n ? Singular(n) : null).OfType<string>());
        names.Add(renamed.NewName);
        foreach (var name in names.Where(n => n != renamed.Name).Distinct())
            yield return new Fix($"Rename '{renamed.Name}' to '{name}'", ctx => FixOps.Rename(ctx, declaration, name));
    }

    // --- In the aspect ---------------------------------------------------------------------------------------------

    /// <summary>LAMA0003: the aspect on the type, and/or [AttributeUsage] so that C# reports wrong usages.</summary>
    static IEnumerable<Fix> AttributeUsage(FixContext c)
    {
        if (c.Target is not IAttributesOwnerDeclaration target) yield break;
        foreach (var attribute in AspectAttributes(c, target))
        {
            if (Resolve(attribute) is not { } type) continue;
            var kinds = AspectKinds(type);
            var name = FixOps.ShortName(attribute);
            var containing = target.GetContainingNode<IClassLikeDeclaration>();
            var move = SupportsTypes(kinds) && containing is not (null or IInterfaceDeclaration) && FixOps.FindAttribute(containing, name) == null;
            var aspect = type.GetDeclarations().OfType<IClassLikeDeclaration>().FirstOrDefault();
            var usage = aspect != null && type.GetDeclarations().OfType<IClassLikeDeclaration>().All(d => FixOps.FindAttribute(d, "AttributeUsage") == null)
                ? AttributeTargets(kinds) : null;

            void Move(FixContext ctx)
            {
                FixOps.RemoveAttribute(ctx, attribute);
                FixOps.AddAttribute(ctx, containing!, attribute.GetText());
            }

            void Restrict(FixContext ctx)
            {
                FixOps.AddAttribute(ctx, aspect!, $"AttributeUsage( {usage} )");
                AddUsing(ctx, aspect!, "System");
            }

            if (move) yield return new Fix($"Move [{name}] to '{containing!.DeclaredName}'", Move);
            if (usage != null) yield return new Fix($"Add [AttributeUsage( {usage} )] to '{type.ShortName}'", Restrict);
            if (move && usage != null)
                yield return new Fix($"Move [{name}] to '{containing!.DeclaredName}' and add [AttributeUsage] to '{type.ShortName}'", ctx =>
                {
                    Move(ctx);
                    Restrict(ctx);
                });
        }
    }

    /// <summary>LAMA0025: nameof(an existing template) instead of the string.</summary>
    static IEnumerable<Fix> TemplateName(FixContext c)
    {
        var quoted = Quoted(c.Diagnostic.Message);
        if (quoted.Count < 2) yield break;
        var missing = quoted[1];
        foreach (var aspect in AspectClasses(c).Where(a => Mentions(c, a.DeclaredName)))
        {
            if (aspect.MemberDeclarations.Any(m => m.DeclaredName == missing)) continue;
            var literals = aspect.Descendants<ICSharpLiteralExpression>().ToEnumerable().Where(l => l.GetText() == $"\"{missing}\"").ToList();
            if (literals.Count == 0) continue;
            foreach (var template in aspect.MemberDeclarations.OfType<IAttributesOwnerDeclaration>().Where(m => FixOps.FindAttribute(m, "Template") != null))
                yield return new Fix($"Use nameof({template.DeclaredName})", ctx => literals.ForEach(l => ctx.Replace(l, $"nameof({template.DeclaredName})")));
        }
    }

    /// <summary>LAMA0115: a meta.Target member available for every target.</summary>
    static IEnumerable<Fix> MetaTarget(FixContext c)
    {
        if (MetaTargetReplacements(c.Diagnostic.Message) is not { } replacements) yield break;
        // The advice ('LogReadAttribute.OverrideProperty.get'): its member when found, else the whole aspect.
        var advice = Quoted(c.Diagnostic.Message).FirstOrDefault()?.Split('.');
        foreach (var aspect in AspectClasses(c))
        {
            ITreeNode scope = advice is { Length: > 1 } && aspect.MemberDeclarations.FirstOrDefault(m => m.DeclaredName == advice[1]) is { } member ? member : aspect;
            var uses = scope.Descendants<IReferenceExpression>().ToEnumerable()
                .Where(r => r.NameIdentifier?.Name == replacements.Used && r.QualifierExpression is IReferenceExpression q && IsMeta(q, "Target")).ToList();
            if (uses.Count == 0) continue;
            foreach (var replacement in replacements.Replacements)
                yield return new Fix($"Use 'meta.Target.{replacement}'", ctx => uses.ForEach(u => ctx.Replace(u.NameIdentifier, replacement)));
        }
    }

    /// <summary>LAMA0223: meta.Proceed(), which adapts to the return type.</summary>
    static IEnumerable<Fix> ProceedVariant(FixContext c)
    {
        var variant = Regex.Match(c.Diagnostic.Message, @"\b(Proceed\w+)\b") is { Success: true } m ? m.Groups[1].Value : null;
        if (variant == null) yield break;
        foreach (var aspect in AspectClasses(c))
        {
            var calls = aspect.Descendants<IReferenceExpression>().ToEnumerable().Where(r => IsMeta(r, variant)).ToList();
            if (calls.Count > 0)
                yield return new Fix($"Use 'meta.Proceed()' in '{aspect.DeclaredName}'", ctx => calls.ForEach(r => ctx.Replace(r.NameIdentifier, "Proceed")));
        }
    }

    /// <summary>
    /// LAMA0114, LAMA0287: use meta.This/Base/Receiver only when the target has an instance, or make the aspect
    /// ineligible on static members.
    /// </summary>
    static IEnumerable<Fix> Instance(FixContext c, string[] metaMembers, bool static_)
    {
        var target = TargetExpression(c.Diagnostic.Message);
        foreach (var aspect in AspectClasses(c))
        {
            var uses = aspect.Descendants<IReferenceExpression>().ToEnumerable().Where(r => metaMembers.Any(m => IsMeta(r, m))).ToList();
            if (uses.Count == 0) continue;
            var statements = uses.Select(u => u.GetContainingNode<IExpressionStatement>()).ToList();
            if (statements.All(s => s?.Parent is IBlock))
            {
                var distinct = statements.Distinct().ToList();
                var used = uses[0].NameIdentifier.Name;
                yield return new Fix(static_
                    ? $"Use 'meta.{used}' only when the target isn't static"
                    : $"Use 'meta.{used}' only when the target has a receiver", ctx =>
                {
                    foreach (var statement in distinct)
                    {
                        var text = statement!.GetText();
                        var indent = FixOps.IndentAt(statement.GetDocumentStartOffset());
                        string? without = text;
                        foreach (var m in metaMembers)
                            if (without != null && without.Contains("meta." + m)) without = WithoutHole(without, "meta." + m);
                        ctx.Replace(statement, static_
                            ? without != null ? Guard($"{target}.IsStatic", without, text, indent) : Guard($"!{target}.IsStatic", text, null, indent)
                            : Guard($"{target}.HasReceiver()", text, without, indent));
                    }
                    if (!static_) AddUsing(ctx, aspect, "Metalama.Framework.Code");
                });
            }
            if (EligibilityFix(c, aspect, "MustNotBeStatic()", null) is { } fix) yield return fix;
        }
    }

    /// <summary>LAMA0247: the aspect only for methods returning the type the template needs.</summary>
    static IEnumerable<Fix> ReturnTypeEligibility(FixContext c)
    {
        var desired = Regex.Match(c.Diagnostic.Message, @"desired type '(\w+\??)'");
        if (!desired.Success || !Regex.IsMatch(desired.Groups[1].Value, @"^(bool|byte|sbyte|short|ushort|int|uint|long|ulong|float|double|decimal|char|string|object)\??$")) yield break;
        var aspectName = Quoted(c.Diagnostic.Message).FirstOrDefault();
        foreach (var aspect in AspectClasses(c).Where(a => aspectName != null && AttributeName(a.DeclaredName) == AttributeName(aspectName)))
            if (EligibilityFix(c, aspect, $"ReturnType().MustBe<{desired.Groups[1].Value}>()", "IMethod") is { } fix)
                yield return fix;
    }

    /// <summary>LAMA0063, LAMA0650: invoker options allowed for the member.</summary>
    static IEnumerable<Fix> InvokerOptions(FixContext c, string[] wrong, string[] replacements, bool withObjectOnly)
    {
        foreach (var aspect in AspectClasses(c))
        {
            var uses = aspect.Descendants<IReferenceExpression>().ToEnumerable()
                .Where(r => wrong.Contains(r.NameIdentifier?.Name) && r.QualifierExpression?.GetText().EndsWith("InvokerOptions") == true).ToList();
            // The invocations of another object's member: chains with WithObject(...).
            var onObject = uses.Where(u => u.GetContainingNode<IInvocationExpression>()?.GetText().Contains("WithObject") == true).ToList();
            if (onObject.Count > 0 || withObjectOnly) uses = onObject;
            if (uses.Count == 0) continue;
            foreach (var replacement in replacements)
                yield return new Fix($"Use 'InvokerOptions.{replacement}'", ctx => uses.ForEach(u => ctx.Replace(u.NameIdentifier, replacement)));
        }
    }

    /// <summary>LAMA0091: the aspect making the contribution ordered before the low-level aspects in [AspectOrder].</summary>
    static IEnumerable<Fix> AspectOrder(FixContext c)
    {
        if (Quoted(c.Diagnostic.Message).FirstOrDefault() is not { } name) yield break;
        foreach (var type in AspectTypesNamed(c, name))
        foreach (var attribute in References(type).Select(r => r.GetContainingNode<ITypeofExpression>()?.GetContainingNode<IAttribute>())
                     .OfType<IAttribute>().Where(a => FixOps.ShortName(a) == "AspectOrder").Distinct())
        {
            var types = attribute.Arguments.Select(a => a.Value).OfType<ITypeofExpression>().ToList();
            var contributor = types.FindIndex(t => Equals(TypeOf(t), type));
            var weavers = Enumerable.Range(0, types.Count).Where(i => TypeOf(types[i]) is { } t && RequiresWeaver(t)).ToList();
            var runTime = !attribute.Arguments.Any(a => a.Value?.GetText().Contains("CompileTime") == true);
            if (contributor < 0 || OrderBeforeWeavers(types.Count, contributor, weavers, runTime) is not { } order) continue;
            var texts = types.Select(t => t.GetText()).ToList();
            yield return new Fix($"Order [{AttributeName(type.ShortName)}] before the low-level aspects in [AspectOrder]", ctx =>
            {
                for (var i = 0; i < types.Count; i++)
                    if (order[i] != i) ctx.Replace(types[i], texts[order[i]]);
            });
        }
    }

    /// <summary>LAMA0401: the attribute type available at compile time.</summary>
    static IEnumerable<Fix> RunTimeOrCompileTime(FixContext c)
    {
        if (Quoted(c.Diagnostic.Message).FirstOrDefault() is not { } name) yield break;
        foreach (var type in SourceTypesNamed(c, ShortName(name)))
        {
            var declarations = type.GetDeclarations().OfType<IClassLikeDeclaration>().ToList();
            if (declarations.Count == 0 || declarations.Any(d => FixOps.FindAttribute(d, "RunTimeOrCompileTime") != null || FixOps.FindAttribute(d, "CompileTime") != null)) continue;
            yield return new Fix($"Add [RunTimeOrCompileTime] to '{type.ShortName}'", ctx =>
            {
                AddAttributeLast(ctx, declarations[0], "RunTimeOrCompileTime");
                AddUsing(ctx, declarations[0], "Metalama.Framework.Aspects");
            });
        }
    }

    // --- Helpers -----------------------------------------------------------------------------------------------------

    static ITypeElement? Resolve(IAttribute attribute) => attribute.Name?.Reference.Resolve().DeclaredElement as ITypeElement;

    static ITypeElement? TypeOf(ITypeofExpression typeOf) => (typeOf.ArgumentType as IDeclaredType)?.GetTypeElement();

    static bool RequiresWeaver(ITypeElement type) =>
        type.GetAttributeInstances(AttributesSource.All).Any(a => a.GetClrName().ShortName == "RequireAspectWeaverAttribute");

    /// <summary>`meta.<paramref name="member"/>`.</summary>
    static bool IsMeta(IReferenceExpression reference, string member) =>
        reference.NameIdentifier?.Name == member && reference.QualifierExpression is IReferenceExpression { QualifierExpression: null } q && q.NameIdentifier?.Name == "meta";

    /// <summary>The kinds of declarations an aspect type supports, from its IAspect&lt;T&gt; interfaces ("INamedType").</summary>
    static List<string> AspectKinds(ITypeElement type) =>
        type.GetAllSuperTypes().Where(t => t.GetClrName().FullName == "Metalama.Framework.Aspects.IAspect`1")
            .Select(t => t.GetTypeElement() is { TypeParameters.Count: 1 } generic ? (t.GetSubstitution()[generic.TypeParameters[0]] as IDeclaredType)?.GetClrName().ShortName : null)
            .OfType<string>().Distinct().ToList();

    static bool Mentions(FixContext c, string typeName)
    {
        var message = c.Diagnostic.Message;
        var name = AttributeName(typeName);
        return message.Contains($"'{name}'") || message.Contains($"'{typeName}") || message.Contains($"[{name}");
    }

    /// <summary>Aspect attributes on the declaration, the one the message names first.</summary>
    static IEnumerable<IAttribute> AspectAttributes(FixContext c, IAttributesOwnerDeclaration declaration)
    {
        var aspects = declaration.Attributes.Where(a => Resolve(a) is { } t && AspectTests.IsAspect(t)).ToList();
        var named = aspects.Where(a => Mentions(c, FixOps.ShortName(a))).ToList();
        return named.Count > 0 ? named : aspects;
    }

    /// <summary>
    /// Source declarations of the aspects applied to the diagnostic's target (attributes on it or on a containing
    /// declaration, the worker's instances) and of the ones the message names; the named ones first.
    /// </summary>
    static List<IClassLikeDeclaration> AspectClasses(FixContext c)
    {
        var types = new List<ITypeElement>();
        for (var node = (ITreeNode?)c.Target ?? c.Node; node != null; node = node.Parent)
            if (node is IAttributesOwnerDeclaration owner)
                types.AddRange(owner.Attributes.Select(Resolve).OfType<ITypeElement>().Where(AspectTests.IsAspect));
        if (c.Model is { } model)
        {
            var target = c.Diagnostic.Target is { } id ? AspectModel.Owner(id) : null;
            var ids = model.Instances.Where(i => target != null && AspectModel.Owner(i.Target) == target).Select(i => i.AspectClass)
                .Concat(model.AspectClasses.Where(a => !a.IsLibrary && Mentions(c, a.Id.Substring(a.Id.LastIndexOf('.') + 1))).Select(a => a.Id));
            types.AddRange(ids.Distinct().Select(i => FixOps.TypeOf(c, i)).OfType<ITypeElement>());
        }
        return types.Distinct().OrderByDescending(t => Mentions(c, t.ShortName))
            .SelectMany(t => t.GetDeclarations().OfType<IClassLikeDeclaration>()).Distinct().ToList();
    }

    /// <summary>Aspect types named "Log" (or "LogAttribute") applied around the target or known to the worker.</summary>
    static IEnumerable<ITypeElement> AspectTypesNamed(FixContext c, string name) =>
        AspectClasses(c).Select(d => d.DeclaredElement).OfType<ITypeElement>().Concat(SourceTypesNamed(c, name))
            .Where(t => AttributeName(t.ShortName) == AttributeName(name) && AspectTests.IsAspect(t)).Distinct();

    /// <summary>Types with source declarations named <paramref name="name"/> or <paramref name="name"/>Attribute, visible from the file.</summary>
    static IEnumerable<ITypeElement> SourceTypesNamed(FixContext c, string name)
    {
        if (c.File?.GetPsiModule() is not { } module) return [];
        var scope = module.GetPsiServices().Symbols.GetSymbolScope(module, withReferences: true, caseSensitive: true);
        return new[] { name, name + "Attribute" }.SelectMany(n => scope.GetElementsByShortName(n)).OfType<ITypeElement>()
            .Where(t => t.GetDeclarations().Count > 0).Distinct();
    }

    static IEnumerable<ITreeNode> References(IDeclaredElement element) =>
        element.GetPsiServices().Finder.FindAllReferences(element).Select(r => r.GetTreeNode());

    /// <summary>
    /// A condition (e.g. "MustNotBeStatic()") added to the aspect's BuildEligibility, or a new BuildEligibility; null when
    /// the eligibility isn't for <paramref name="kind"/> (when given) or its signature is unknown.
    /// </summary>
    static Fix? EligibilityFix(FixContext c, IClassLikeDeclaration aspect, string condition, string? kind)
    {
        var text = $"Add '{condition}' to the eligibility of '{aspect.DeclaredName}'";
        var existing = aspect.MethodDeclarations.FirstOrDefault(m => m.DeclaredName == "BuildEligibility" && m.Params?.ParameterDeclarations.Count == 1);
        if (existing != null)
        {
            var parameter = existing.Params.ParameterDeclarations[0];
            if (existing.Body?.RBrace is not { } close || kind != null && parameter.TypeUsage?.GetText().Contains($"<{kind}>") != true) return null;
            if (existing.Body.Statements.Any(s => s.GetText().Contains(condition))) return null;
            return new Fix(text, ctx =>
            {
                var statement = $"{parameter.DeclaredName}.{condition};";
                if (LineStartIfAlone(close) is { } lineStart) ctx.Insert(lineStart, $"{FixOps.IndentAt(close.GetDocumentStartOffset())}    {statement}\n");
                else ctx.Insert(close.GetDocumentStartOffset(), statement + " ");
                AddUsing(ctx, aspect, "Metalama.Framework.Eligibility");
            });
        }
        if (aspect.DeclaredElement is not { } type || aspect.Body?.RBrace is not { } classClose) return null;
        string method;
        if (BaseBuildEligibility(type) is { } baseParameter)
            method = $"public override void BuildEligibility( {baseParameter} builder )\n{{\n    base.BuildEligibility( builder );\n    builder.{condition};\n}}";
        else if (AspectKinds(type) is { Count: 1 } kinds)
            method = $"public void BuildEligibility( IEligibilityBuilder<{kinds[0]}> builder )\n{{\n    builder.{condition};\n}}";
        else return null;
        if (kind != null && !method.Contains($"<{kind}>")) return null;
        return new Fix(text, ctx =>
        {
            var classIndent = FixOps.IndentAt(classClose.GetDocumentStartOffset());
            var memberIndent = aspect.MemberDeclarations.FirstOrDefault() is { } first ? FixOps.IndentAt(first.GetDocumentStartOffset()) : classIndent + "    ";
            var indented = memberIndent + method.Replace("\n", "\n" + memberIndent);
            if (LineStartIfAlone(classClose) is { } lineStart)
                ctx.Insert(lineStart, (aspect.MemberDeclarations.Any() ? "\n" : "") + indented + "\n");
            else ctx.Insert(classClose.GetDocumentStartOffset(), "\n" + indented + "\n" + classIndent);
            AddUsing(ctx, aspect, "Metalama.Framework.Eligibility");
            AddUsing(ctx, aspect, "Metalama.Framework.Code");
        });
    }

    /// <summary>The parameter type of the overridable BuildEligibility of a base class ("IEligibilityBuilder&lt;IMethod&gt;").</summary>
    static string? BaseBuildEligibility(ITypeElement type)
    {
        for (var baseType = (type as IClass)?.GetBaseClassType()?.GetTypeElement() as IClass; baseType != null; baseType = baseType.GetBaseClassType()?.GetTypeElement() as IClass)
            if (baseType.Methods.FirstOrDefault(m => m.ShortName == "BuildEligibility" && m.Parameters.Count == 1 && (m.IsVirtual || m.IsAbstract || m.IsOverride)) is { } method)
                return method.Parameters[0].Type.GetPresentableName(CSharpLanguage.Instance);
        return null;
    }

    /// <summary>The start of <paramref name="node"/>'s line when only whitespace precedes it there.</summary>
    static DocumentOffset? LineStartIfAlone(ITreeNode node) => LineStartIfAlone(node.GetDocumentStartOffset());

    static DocumentOffset? LineStartIfAlone(DocumentOffset start)
    {
        var text = start.Document.GetText();
        var lineStart = text.LastIndexOf('\n', Math.Max(0, start.Offset - 1)) + 1;
        return text.Substring(lineStart, start.Offset - lineStart).Trim().Length == 0 ? new DocumentOffset(start.Document, lineStart) : null;
    }

    /// <summary>An attribute on its own line after the existing ones (FixOps.AddAttribute puts it first).</summary>
    static void AddAttributeLast(FixContext c, IAttributesOwnerDeclaration declaration, string attribute)
    {
        var anchor = FixOps.StartAfterAttributes(declaration);
        if (LineStartIfAlone(anchor) is { } lineStart) c.Insert(lineStart, $"{FixOps.IndentAt(anchor)}[{attribute}]\n");
        else c.Insert(anchor, $"[{attribute}] ");
    }

    /// <summary>`using <paramref name="ns"/>;` in the file of <paramref name="node"/>, in alphabetical order among the usings.</summary>
    static void AddUsing(FixContext c, ITreeNode node, string ns)
    {
        if (node.GetContainingFile() is not ICSharpFile file || file.GetSourceFile()?.Document is not { } document) return;
        var usings = file.Descendants<IUsingSymbolDirective>().ToEnumerable().ToList();
        if (usings.Any(u => u.ImportedSymbolName?.QualifiedName == ns)) return;
        if (usings.FirstOrDefault(u => string.CompareOrdinal(u.ImportedSymbolName?.QualifiedName, ns) > 0) is { } next)
            c.Insert(next.GetDocumentStartOffset(), $"using {ns};\n{FixOps.IndentAt(next.GetDocumentStartOffset())}");
        else if (usings.LastOrDefault() is { } last) c.Insert(last.GetDocumentEndOffset(), $"\nusing {ns};");
        else c.Insert(new DocumentOffset(document, 0), $"using {ns};\n\n");
    }
}
