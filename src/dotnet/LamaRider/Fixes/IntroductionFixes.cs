using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Application.Progress;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.Util;
using L = LamaRider.Model.IntroductionFixLogic;

namespace LamaRider.Fixes;

/// <summary>
/// Introducing members and implementing interfaces (LAMA05xx). Most are reported on the target and fixed in the aspect:
/// the advice is a declarative attribute ([Introduce], [InterfaceMember]) or a call in BuildAspect (IntroduceMethod,
/// ImplementInterface, ...), found by <see cref="FindAdvice"/>; the others edit the target or its base types.
/// </summary>
public sealed class IntroductionFixes : FixProvider
{
    const string AspectsNs = "Metalama.Framework.Aspects";

    public override IEnumerable<string> Ids =>
    [
        "LAMA0500", "LAMA0502", "LAMA0503", "LAMA0504", "LAMA0505", "LAMA0506", "LAMA0507", "LAMA0508", "LAMA0509", "LAMA0511",
        "LAMA0512", "LAMA0514", "LAMA0516", "LAMA0517", "LAMA0518", "LAMA0519", "LAMA0521", "LAMA0522", "LAMA0527", "LAMA0528",
        "LAMA0529", "LAMA0531", "LAMA0532", "LAMA0533", "LAMA0534", "LAMA0540", "LAMA0542", "LAMA0543", "LAMA0550", "LAMA0551",
    ];

    public override IEnumerable<Fix> Fixes(FixContext c) => c.Diagnostic.Id switch
    {
        "LAMA0500" or "LAMA0509" => MemberExists(c),
        "LAMA0502" or "LAMA0529" => BaseNotOverridable(c),
        "LAMA0503" => DifferentType(c),
        "LAMA0504" => DifferentStaticity(c),
        "LAMA0505" => InstanceIntoStatic(c),
        "LAMA0506" => DifferentKind(c),
        "LAMA0507" => StaticAnd(c, "IsVirtual", "virtual"),
        "LAMA0508" => StaticAnd(c, "IsSealed", "sealed"),
        "LAMA0511" => InterfaceMemberType(c),
        "LAMA0512" or "LAMA0516" => InterfaceStrategy(c),
        "LAMA0514" => InterfaceMemberExists(c),
        "LAMA0517" => MissingAccessor(c),
        "LAMA0518" => ExtraAccessor(c),
        "LAMA0519" => NotPublicTemplate(c),
        "LAMA0521" => AttributeExists(c),
        "LAMA0522" => Finalizer(c),
        "LAMA0527" => StaticIndexer(c),
        "LAMA0528" => ExistingNotPublic(c),
        "LAMA0531" => TypeExists(c),
        "LAMA0532" => AbstractIntoNonAbstract(c),
        "LAMA0533" => AbstractWithStrategy(c),
        "LAMA0534" => FieldIntoInterface(c),
        "LAMA0540" => ExtensionBlockTarget(c),
        "LAMA0542" => InitIndexer(c),
        "LAMA0543" => ExtensionModifier(c),
        "LAMA0550" => InitializeNotVirtual(c),
        "LAMA0551" => BaseConstructorContext(c),
        _ => [],
    };

    // --- Per id -----------------------------------------------------------------------------------------------------

    /// <summary>LAMA0500/0509: the introduced member already exists; New only when it is inherited.</summary>
    static IEnumerable<Fix> MemberExists(FixContext c)
    {
        var m = c.Diagnostic.Message;
        if (QuotedMember(c, 1) is not { } member) yield break;
        var inBase = L.QuotedAfter(m, "already defined in type") is { } definedIn && definedIn != L.QuotedAfter(m, "into type");
        foreach (var a in FindAdvice(c, member, IsIntroduce).Take(1))
        {
            yield return StrategyFix(a, "Override");
            yield return StrategyFix(a, "Ignore");
            if (inBase) yield return StrategyFix(a, "New");
        }
    }

    /// <summary>LAMA0502/0529: the inherited member can't be overridden: make it virtual, or hide/skip it.</summary>
    static IEnumerable<Fix> BaseNotOverridable(FixContext c)
    {
        var m = c.Diagnostic.Message;
        if (L.Quoted(m).ElementAtOrDefault(1) is not { } qualified) yield break;
        var name = L.MemberName(qualified);
        var typeName = L.QuotedAfter(m, "already defined in type") ?? L.DeclaringType(qualified);
        foreach (var d in SourceMembers(c, typeName, name, L.ParameterCount(qualified)).OfType<ICSharpModifiersOwnerDeclaration>().Take(1))
            if (MakeOverridable(d, $"{typeName}.{name}") is { } fix) yield return fix;
        if (c.Diagnostic.Id == "LAMA0502")
            foreach (var a in FindAdvice(c, name, IsIntroduce).Take(1))
            {
                yield return StrategyFix(a, "New");
                yield return StrategyFix(a, "Ignore");
            }
        else if (InterfaceAdvice(c, null) is { Count: 1 } calls)
            yield return StrategyFix(calls[0], "Ignore");
    }

    /// <summary>LAMA0503: give the template the type of the existing member.</summary>
    static IEnumerable<Fix> DifferentType(FixContext c)
    {
        if (QuotedMember(c, 1) is not { } member || L.QuotedAfter(c.Diagnostic.Message, "different type or return type") is not { } type) yield break;
        foreach (var a in FindAdvice(c, member, IsIntroduce).Take(1))
            if (TypeUsage(a.Template) is { } usage)
                yield return new Fix($"Change the type of template '{member}' to '{type}'", ctx => ctx.Replace(usage, type));
    }

    /// <summary>LAMA0504: rename the existing member, whose staticity differs.</summary>
    static IEnumerable<Fix> DifferentStaticity(FixContext c)
    {
        var m = c.Diagnostic.Message;
        if (L.Quoted(m).ElementAtOrDefault(1) is not { } qualified) yield break;
        var name = L.MemberName(qualified);
        foreach (var d in SourceMembers(c, L.QuotedAfter(m, "already defined in type"), name, L.ParameterCount(qualified)).Take(1))
        {
            var newName = L.RenamedExisting(name, d.DeclaredElement is IModifiersOwner { IsStatic: true });
            yield return new Fix($"Rename the existing '{name}' to '{newName}'", ctx => FixOps.Rename(ctx, d, newName));
        }
    }

    /// <summary>LAMA0505: introduce a static member, or make the target class non-static.</summary>
    static IEnumerable<Fix> InstanceIntoStatic(FixContext c)
    {
        if (QuotedMember(c, 1) is { } member)
            foreach (var a in FindAdvice(c, member, IsIntroduce).Take(1))
                yield return new Fix($"Introduce '{member}' as a static member", ctx => SetScope(ctx, a, "IntroductionScope.Static"));
        if (TargetType(c) is IClassDeclaration type && FixOps.HasModifier(type, "static"))
            yield return new Fix($"Make '{type.DeclaredName}' non-static", ctx => FixOps.RemoveModifier(ctx, type, "static"));
    }

    /// <summary>LAMA0506: remove the existing member of another kind.</summary>
    static IEnumerable<Fix> DifferentKind(FixContext c)
    {
        if (QuotedMember(c, 1) is not { } member) yield break;
        foreach (var d in SourceMembers(c, null, member, null).Take(1))
            yield return new Fix($"Remove the existing '{member}' from '{d.GetContainingTypeDeclaration()?.DeclaredName}'", ctx => DeleteMember(ctx, d));
    }

    /// <summary>LAMA0507/0508: a static member can't be virtual/sealed: introduce an instance member, or drop the flag.</summary>
    static IEnumerable<Fix> StaticAnd(FixContext c, string property, string modifier)
    {
        if (QuotedMember(c, 1) is not { } member) yield break;
        foreach (var a in FindAdvice(c, member, IsIntroduce).Take(1))
        {
            if (HasFlag(a, property, modifier))
                yield return new Fix($"Don't make '{member}' {modifier}", ctx => RemoveFlag(ctx, a, property, modifier));
            yield return new Fix($"Introduce '{member}' as an instance member", ctx => MakeInstance(ctx, a));
        }
    }

    /// <summary>LAMA0511: give the [InterfaceMember] template the type of the interface member.</summary>
    static IEnumerable<Fix> InterfaceMemberType(FixContext c)
    {
        var m = c.Diagnostic.Message;
        if (L.QuotedAfter(m, "aspect member") is not { } template || L.QuotedAfter(m, "interface member") is not { } interfaceMember
            || L.DeclaringType(interfaceMember) is not { } interfaceName) yield break;
        var name = L.MemberName(template);
        foreach (var a in FindAdvice(c, name, IsIntroduce).Where(a => a.Template != null).Take(1))
        {
            if (TypeUsage(a.Template) is not { } usage) yield break;
            var module = a.Template!.GetPsiModule();
            var type = module.GetPsiServices().Symbols.GetSymbolScope(module, withReferences: true, caseSensitive: true)
                .GetElementsByShortName(interfaceName).OfType<ITypeElement>()
                .SelectMany(t => t.GetMembers()).Where(x => x.ShortName == L.MemberName(interfaceMember)
                    && (L.ParameterCount(interfaceMember) is not { } n || x is not IParametersOwner p || p.Parameters.Count == n))
                .Select(x => x switch { IMethod method => method.ReturnType, IProperty p => p.Type, IEvent e => e.Type, _ => null })
                .FirstOrDefault(t => t != null)?.GetPresentableName(CSharpLanguage.Instance!);
            if (type != null) yield return new Fix($"Change the type of template '{name}' to '{type}'", ctx => ctx.Replace(usage, type));
        }
    }

    /// <summary>LAMA0512/0516: how ImplementInterface handles a type that already implements the interface.</summary>
    static IEnumerable<Fix> InterfaceStrategy(FixContext c)
    {
        foreach (var a in InterfaceAdvice(c, L.QuotedAfter(c.Diagnostic.Message, "implement interface")).Take(1))
        {
            yield return StrategyFix(a, "Ignore");
            yield return StrategyFix(a, "Override");
            if (c.Diagnostic.Id == "LAMA0516") yield return RemoveStrategyFix(a);
        }
    }

    /// <summary>LAMA0514: the target already has the interface member: use it, or implement explicitly.</summary>
    static IEnumerable<Fix> InterfaceMemberExists(FixContext c)
    {
        if (QuotedMember(c, 1) is not { } member) yield break;
        foreach (var a in InterfaceMembers(c, member).Take(1))
        {
            yield return StrategyFix(a, "Ignore", "InterfaceMemberOverrideStrategy");
            yield return StrategyFix(a, "MakeExplicit", "InterfaceMemberOverrideStrategy");
        }
    }

    /// <summary>LAMA0517: the template property lacks an accessor of the interface property.</summary>
    static IEnumerable<Fix> MissingAccessor(FixContext c)
    {
        var m = c.Diagnostic.Message;
        if ((L.QuotedAfter(m, "the template") ?? L.Quoted(m).ElementAtOrDefault(1)) is not { } template
            || L.QuotedAfter(m, "does not have the") is not { } missing) yield break;
        var name = L.MemberName(template);
        foreach (var property in AspectMembers(c, name).OfType<IPropertyDeclaration>().Take(1))
        {
            if (AccessorBraces(property) is not { } list) yield break;
            var accessors = L.AutoPropertyAccessors(property.AccessorDeclarations.Select(a => L.AccessorKind(a.GetText())).OfType<string>(), missing);
            yield return new Fix($"Make template '{name}' an automatic property {accessors}", ctx => ctx.Replace(list, accessors));
        }
    }

    /// <summary>LAMA0518: the explicit template property has an accessor the interface property lacks.</summary>
    static IEnumerable<Fix> ExtraAccessor(FixContext c)
    {
        var m = c.Diagnostic.Message;
        if (L.QuotedAfter(m, "the template") is not { } template || L.QuotedAfter(m, "unexpected") is not { } kind) yield break;
        var name = L.MemberName(template);
        foreach (var property in AspectMembers(c, name).OfType<IPropertyDeclaration>().Take(1))
        {
            foreach (var accessor in property.AccessorDeclarations.Where(a => L.AccessorKind(a.GetText()) == kind).Take(1))
                yield return new Fix($"Remove the '{kind}' accessor from template '{name}'", ctx => ctx.Delete(accessor));
            foreach (var a in InterfaceMembers(c, name).Where(a => HasProperty(a.Attribute!, "IsExplicit")).Take(1))
                yield return new Fix($"Implement '{name}' implicitly (remove IsExplicit)", ctx => RemoveProperty(ctx, a.Attribute!, "IsExplicit"));
        }
    }

    /// <summary>LAMA0519: an implicit implementation must be public: make the template public, or implement explicitly.</summary>
    static IEnumerable<Fix> NotPublicTemplate(FixContext c)
    {
        if (L.QuotedAfter(c.Diagnostic.Message, "using member") is not { } template) yield break;
        var name = L.MemberName(template);
        foreach (var a in InterfaceMembers(c, name).Take(1))
        {
            if (a.Template is ICSharpModifiersOwnerDeclaration t && !FixOps.HasModifier(t, "public"))
                yield return new Fix($"Make template '{name}' public", ctx => FixOps.SetAccessibility(ctx, t, "public"));
            yield return ExplicitFix(a, name);
        }
    }

    /// <summary>LAMA0521: the attribute is already there: tell IntroduceAttribute what to do, or remove it from the target.</summary>
    static IEnumerable<Fix> AttributeExists(FixContext c)
    {
        if (L.QuotedAfter(c.Diagnostic.Message, "custom attribute") is not { } attribute) yield break;
        var shortName = attribute.EndsWith("Attribute") && attribute.Length > 9 ? attribute.Substring(0, attribute.Length - 9) : attribute;
        shortName = shortName.Substring(shortName.LastIndexOf('.') + 1);
        foreach (var a in Single(FindAdvice(c, null, n => n == "IntroduceAttribute"), a => a.Call!.GetText().Contains(shortName)))
        {
            yield return StrategyFix(a, "Ignore");
            yield return StrategyFix(a, "Override");
            yield return StrategyFix(a, "New");
        }
        if (c.Target is IAttributesOwnerDeclaration target && FixOps.FindAttribute(target, shortName) is { } existing)
            yield return new Fix($"Remove [{shortName}] from '{target.DeclaredName}'", ctx => FixOps.RemoveAttribute(ctx, existing));
    }

    /// <summary>LAMA0522: a finalizer can't be introduced with New.</summary>
    static IEnumerable<Fix> Finalizer(FixContext c)
    {
        foreach (var a in FindAdvice(c, null, n => n == "IntroduceFinalizer").Where(a => StrategyArgument(a.Call!) != null).Take(1))
        {
            yield return StrategyFix(a, "Override");
            yield return StrategyFix(a, "Ignore");
            yield return RemoveStrategyFix(a);
        }
    }

    /// <summary>LAMA0527: an indexer can't be static.</summary>
    static IEnumerable<Fix> StaticIndexer(FixContext c)
    {
        foreach (var a in Single(FindAdvice(c, null, n => n == "IntroduceIndexer"),
                     a => NamedArgument(a.Call!, "scope") != null || BuilderAssignments(a.Call!, "IsStatic", null).Any()))
            yield return new Fix("Introduce the indexer as an instance member", ctx => MakeInstance(ctx, a));
    }

    /// <summary>LAMA0528: the existing member must be public; or implement the interface member explicitly.</summary>
    static IEnumerable<Fix> ExistingNotPublic(FixContext c)
    {
        if (L.Quoted(c.Diagnostic.Message).ElementAtOrDefault(1) is not { } qualified) yield break;
        var name = L.MemberName(qualified);
        var typeName = L.DeclaringType(qualified);
        foreach (var d in SourceMembers(c, typeName, name, L.ParameterCount(qualified)).OfType<ICSharpModifiersOwnerDeclaration>().Take(1))
            yield return new Fix($"Make '{typeName}.{name}' public", ctx => FixOps.SetAccessibility(ctx, d, "public"));
        foreach (var a in InterfaceMembers(c, name).Take(1)) yield return ExplicitFix(a, name);
    }

    /// <summary>LAMA0531: a type with the name exists: rename the introduced type, or keep the existing one.</summary>
    static IEnumerable<Fix> TypeExists(FixContext c)
    {
        if (QuotedMember(c, 1) is not { } name) yield break;
        foreach (var a in FindAdvice(c, name, IsIntroduce).Where(a => a.Call != null).Take(1))
        {
            if (a.Call!.Arguments.FirstOrDefault(x => x.Value?.GetText() == $"\"{name}\"")?.Value is { } literal)
            {
                var newName = L.IntroducedTypeName(name, L.AspectName(c.Diagnostic.Message));
                yield return new Fix($"Rename the introduced type to '{newName}'", ctx => ctx.Replace(literal, $"\"{newName}\""));
            }
            yield return StrategyFix(a, "Ignore");
        }
    }

    /// <summary>LAMA0532: an abstract member needs an abstract target type.</summary>
    static IEnumerable<Fix> AbstractIntoNonAbstract(FixContext c)
    {
        if (TargetType(c) is not IClassDeclaration type || FixOps.HasModifier(type, "static")) yield break;
        yield return new Fix($"Make '{type.DeclaredName}' abstract", ctx =>
        {
            FixOps.RemoveModifier(ctx, type, "sealed");
            FixOps.AddModifier(ctx, type, "abstract");
        });
    }

    /// <summary>LAMA0533: an abstract member can't override or hide: keep the existing one, or fail (the default).</summary>
    static IEnumerable<Fix> AbstractWithStrategy(FixContext c)
    {
        if (QuotedMember(c, 1) is not { } member) yield break;
        foreach (var a in FindAdvice(c, member, IsIntroduce).Take(1))
        {
            yield return StrategyFix(a, "Ignore");
            yield return RemoveStrategyFix(a);
        }
    }

    /// <summary>LAMA0534: move the aspect from the interface to the types implementing it (GenericFixes removes it).</summary>
    static IEnumerable<Fix> FieldIntoInterface(FixContext c)
    {
        if (TargetType(c) is not IInterfaceDeclaration { DeclaredElement: { } iface } declaration || L.AspectName(c.Diagnostic.Message) is not { } aspect) yield break;
        if (declaration.Attributes.FirstOrDefault(a => FixOps.ShortName(a) == aspect) is not { } attribute) yield break;
        var implementers = Implementers(iface).Select(t => t.GetDeclarations().OfType<IClassLikeDeclaration>().FirstOrDefault())
            .OfType<IClassLikeDeclaration>().Where(d => d is IClassDeclaration or IStructDeclaration or IRecordDeclaration).ToList();
        if (implementers.Count == 0) yield break;
        var names = string.Join(", ", implementers.Select(d => $"'{d.DeclaredName}'"));
        yield return new Fix($"Move [{aspect}] to {names}", ctx =>
        {
            FixOps.RemoveAttribute(ctx, attribute);
            foreach (var d in implementers)
            {
                FixOps.AddAttribute(ctx, d, attribute.GetText());
                FixOps.AddModifier(ctx, d, "partial");
            }
        });
    }

    /// <summary>LAMA0540: an extension block needs a static class.</summary>
    static IEnumerable<Fix> ExtensionBlockTarget(FixContext c)
    {
        if (TargetType(c) is IClassDeclaration type && !FixOps.HasModifier(type, "static") && type.GetContainingTypeDeclaration() == null)
            yield return new Fix($"Make '{type.DeclaredName}' static", ctx => FixOps.AddModifier(ctx, type, "static"));
    }

    /// <summary>LAMA0542: an extension indexer can't be init-only.</summary>
    static IEnumerable<Fix> InitIndexer(FixContext c)
    {
        foreach (var a in FindAdvice(c, null, n => n == "IntroduceIndexer").Where(a => BuilderAssignments(a.Call!, "Writeability", "InitOnly").Any()).Take(1))
            yield return new Fix("Don't make the indexer init-only", ctx => RemoveBuilderAssignments(ctx, a.Call!, "Writeability", "InitOnly"));
    }

    /// <summary>LAMA0543: an extension member can't be protected, virtual, abstract, ...: fix the template.</summary>
    static IEnumerable<Fix> ExtensionModifier(FixContext c)
    {
        if (L.Quoted(c.Diagnostic.Message).LastOrDefault() is not { Length: > 0 } modifier) yield break;
        var templates = AspectDeclarations(c).SelectMany(a => a.MemberDeclarations)
            .Where(d => d.Attributes.Any(a => FixOps.ShortName(a) is "Template" or "Introduce") && FixOps.HasModifier(d, modifier));
        foreach (var t in templates)
            yield return modifier is "protected" or "private" or "internal"
                ? new Fix($"Make template '{t.DeclaredName}' public", ctx => FixOps.SetAccessibility(ctx, t, "public"))
                : new Fix($"Remove '{modifier}' from template '{t.DeclaredName}'", ctx => FixOps.RemoveModifier(ctx, t, modifier));
    }

    /// <summary>LAMA0550: Initialize of a non-sealed class must be public virtual; or seal the class.</summary>
    static IEnumerable<Fix> InitializeNotVirtual(FixContext c)
    {
        if (TargetType(c) is not IClassDeclaration type) yield break;
        foreach (var d in type.MemberDeclarations.OfType<IMethodDeclaration>().Where(d => d.DeclaredName == "Initialize").Take(1))
            yield return new Fix("Make 'Initialize' public virtual", ctx =>
            {
                if (!FixOps.Modifiers(d).Any())
                {
                    ctx.Insert(FixOps.StartAfterAttributes(d), "public virtual ");
                    return;
                }
                if (!FixOps.HasModifier(d, "public")) FixOps.SetAccessibility(ctx, d, "public");
                if (!FixOps.HasModifier(d, "virtual") && !FixOps.HasModifier(d, "override")) FixOps.AddModifier(ctx, d, "virtual");
            });
        if (!FixOps.HasModifier(type, "sealed") && !FixOps.HasModifier(type, "static") && !FixOps.HasModifier(type, "abstract"))
            yield return new Fix($"Make '{type.DeclaredName}' sealed", ctx => FixOps.AddModifier(ctx, type, "sealed"));
    }

    /// <summary>LAMA0551: the base type's constructor must accept an InitializationContext and call OnConstructed when unhandled.</summary>
    static IEnumerable<Fix> BaseConstructorContext(FixContext c)
    {
        if (L.QuotedAfter(c.Diagnostic.Message, "base type") is not { } baseName) yield break;
        if (TargetType(c)?.DeclaredElement is not ITypeElement target) yield break;
        var baseDecl = target.GetSuperTypes().Select(t => t.GetTypeElement()).Where(t => t?.ShortName == baseName)
            .SelectMany(t => t!.GetDeclarations()).OfType<IClassDeclaration>().FirstOrDefault();
        if (baseDecl?.Body is not { } body) yield break;
        var constructors = baseDecl.MemberDeclarations.OfType<IConstructorDeclaration>().Where(x => !x.IsStatic).ToList();
        var parameterless = constructors.FirstOrDefault(x => x.ParameterDeclarations.Count == 0);
        if (constructors.Count > 0 && parameterless == null) yield break;
        yield return new Fix($"Add an InitializationContext parameter to the constructor of '{baseName}'", ctx =>
        {
            const string parameters = "( InitializationContext context = default )";
            if (parameterless != null)
            {
                var indent = FixOps.IndentAt(FixOps.StartAfterAttributes(parameterless));
                if (parameterless.LPar is { } lpar && parameterless.RPar is { } rpar)
                    ctx.Replace(new DocumentRange(lpar.GetDocumentStartOffset(), rpar.GetDocumentEndOffset()), parameters);
                if (parameterless.Body is not { } block) return;
                if (block.Statements.Count == 0) ctx.Replace(block, "\n" + indent + OnConstructedBlock(indent));
                else if (block.RBrace is { } rbrace) ctx.Insert(rbrace.GetDocumentStartOffset(), OnConstructedCall(indent + "    ").TrimStart() + "\n" + indent);
            }
            else if (body.LBrace is { } lbrace)
            {
                var indent = FixOps.IndentAt(baseDecl.GetDocumentStartOffset()) + "    ";
                var access = FixOps.HasModifier(baseDecl, "abstract") ? "protected" : "public";
                ctx.Insert(lbrace.GetDocumentEndOffset(), $"\n{indent}{access} {baseName}{parameters}\n{indent}{OnConstructedBlock(indent)}\n");
            }
            AddUsing(ctx, baseDecl, "Metalama.Framework.RunTime.Initialization");
        });
    }

    /// <summary>"{ get => x; }" of a property: from the brace after its name to the brace after its last accessor.</summary>
    static DocumentRange? AccessorBraces(IPropertyDeclaration property)
    {
        if (property.AccessorDeclarations.LastOrDefault() is not { } last) return null;
        var text = last.GetDocumentRange().Document.GetText();
        var open = text.IndexOf('{', property.GetNameDocumentRange().EndOffset.Offset);
        var close = text.IndexOf('}', last.GetDocumentEndOffset().Offset);
        return open < 0 || close < 0 ? null : new DocumentRange(last.GetDocumentRange().Document, new JetBrains.Util.TextRange(open, close + 1));
    }

    static string OnConstructedBlock(string indent) => $"{{\n{OnConstructedCall(indent + "    ")}\n{indent}}}";

    static string OnConstructedCall(string indent) =>
        $"{indent}if ( !context.IsHandled( InitializationSlot.OnConstructed ) )\n{indent}{{\n{indent}    this.OnConstructed( context );\n{indent}}}";

    // --- Finding the advice in the aspect ------------------------------------------------------------------------

    /// <summary>A declarative advice (attribute on the template) or a programmatic one (call in BuildAspect).</summary>
    sealed record Advice(IAttribute? Attribute, IInvocationExpression? Call, ICSharpTypeMemberDeclaration? Template)
    {
        public ITreeNode Node => (ITreeNode?)Attribute ?? Call!;
    }

    static bool IsIntroduce(string method) => method.StartsWith("Introduce", StringComparison.Ordinal);

    /// <summary>
    /// The advice of the aspect named in the message: [Introduce]/[InterfaceMember] on the template <paramref name="member"/>,
    /// and calls whose method matches and that name <paramref name="member"/> (nameof or string; null: any call).
    /// </summary>
    static List<Advice> FindAdvice(FixContext c, string? member, Func<string, bool> method)
    {
        var aspects = AspectDeclarations(c);
        var result = new List<Advice>();
        if (member != null)
            foreach (var template in Members(aspects, member))
                foreach (var attribute in template.Attributes.Where(a => FixOps.ShortName(a) is "Introduce" or "InterfaceMember"))
                    result.Add(new Advice(attribute, null, template));
        foreach (var call in aspects.SelectMany(a => a.Descendants<IInvocationExpression>().ToEnumerable()))
        {
            if (FixOps.MethodName(call) is not { } name || !method(name)) continue;
            var referenced = call.Arguments.Select(a => a.Value == null ? null : L.ReferencedName(a.Value.GetText())).FirstOrDefault(n => n != null);
            if (member != null && referenced != member) continue;
            result.Add(new Advice(null, call, referenced == null ? null : Members(aspects, referenced).FirstOrDefault()));
        }
        return result;
    }

    /// <summary>[InterfaceMember] on the template <paramref name="member"/>.</summary>
    static IEnumerable<Advice> InterfaceMembers(FixContext c, string member) =>
        FindAdvice(c, member, _ => false).Where(a => FixOps.ShortName(a.Attribute!) == "InterfaceMember");

    /// <summary>ImplementInterface calls for <paramref name="iface"/> (the only one when none names it).</summary>
    static List<Advice> InterfaceAdvice(FixContext c, string? iface)
    {
        var name = iface?.Split('<')[0];
        name = name?.Substring(name.LastIndexOf('.') + 1);
        return Single(FindAdvice(c, null, n => n == "ImplementInterface"), a => name != null && a.Call!.Arguments.FirstOrDefault()?.GetText().Contains(name) == true).ToList();
    }

    /// <summary>The advices that match, else the only advice.</summary>
    static IEnumerable<Advice> Single(List<Advice> all, Func<Advice, bool> match)
    {
        var matching = all.Where(match).ToList();
        return matching.Count > 0 ? matching.Take(1) : all.Count == 1 ? all : [];
    }

    /// <summary>The source declarations of the aspect class named in the message (by the target's attribute, else the model).</summary>
    static List<IClassLikeDeclaration> AspectDeclarations(FixContext c)
    {
        if (L.AspectName(c.Diagnostic.Message) is not { } name) return [];
        var type = (c.Target as IAttributesOwnerDeclaration)?.Attributes.Where(a => FixOps.ShortName(a) == name)
            .Select(a => a.Name?.Reference.Resolve().DeclaredElement as ITypeElement).FirstOrDefault(t => t != null);
        type ??= c.Model?.AspectClasses.Where(a => a.Name == name && !a.IsLibrary).OrderByDescending(a => a.Project == c.Diagnostic.Project)
            .Select(a => FixOps.TypeOf(c, a.Id)).FirstOrDefault(t => t != null);
        return type?.GetDeclarations().OfType<IClassLikeDeclaration>().ToList() ?? [];
    }

    static IEnumerable<ICSharpTypeMemberDeclaration> Members(IEnumerable<IClassLikeDeclaration> types, string name) =>
        types.SelectMany(t => t.MemberDeclarations).Where(m => m.DeclaredName == name);

    static IEnumerable<ICSharpTypeMemberDeclaration> AspectMembers(FixContext c, string name) => Members(AspectDeclarations(c), name);

    // --- Changing the advice -------------------------------------------------------------------------------------

    static Fix StrategyFix(Advice a, string strategy, string enumType = "OverrideStrategy") =>
        new(a.Attribute != null
                ? $"Set WhenExists = {strategy} on [{FixOps.ShortName(a.Attribute)}] '{a.Template?.DeclaredName}'"
                : $"Pass whenExists: {strategy} to {FixOps.MethodName(a.Call!)}",
            ctx => SetStrategy(ctx, a, $"{enumType}.{strategy}"));

    static Fix RemoveStrategyFix(Advice a) =>
        new(a.Attribute != null ? $"Remove WhenExists from [{FixOps.ShortName(a.Attribute)}] (fail when the member exists)"
                : $"Remove whenExists from {FixOps.MethodName(a.Call!)} (fail when it exists)",
            ctx =>
            {
                if (a.Attribute != null) RemoveProperty(ctx, a.Attribute, "WhenExists");
                else if (StrategyArgument(a.Call!) is { } argument) RemoveArgument(ctx, a.Call!, argument);
            });

    static Fix ExplicitFix(Advice a, string name) =>
        new($"Implement '{name}' explicitly (IsExplicit = true)", ctx => FixOps.SetAttributeProperty(ctx, a.Attribute!, "IsExplicit", "true"));

    /// <summary>Sets WhenExists / whenExists (ImplementInterface takes it positionally, as the docs write it).</summary>
    static void SetStrategy(FixContext c, Advice a, string value)
    {
        if (a.Attribute != null) FixOps.SetAttributeProperty(c, a.Attribute, "WhenExists", value);
        else if (a.Call != null)
        {
            var arguments = a.Call.Arguments;
            if (StrategyArgument(a.Call)?.Value is { } existing) c.Replace(existing, value);
            else if (FixOps.MethodName(a.Call) == "ImplementInterface" && arguments.Count == 1) c.Insert(arguments[0].GetDocumentEndOffset(), ", " + value);
            else FixOps.SetNamedArgument(c, a.Call, "whenExists", value);
        }
        AddUsing(c, a.Node, AspectsNs);
    }

    /// <summary>The whenExists argument, named or positional.</summary>
    static ICSharpArgument? StrategyArgument(IInvocationExpression call) =>
        NamedArgument(call, "whenExists") ?? call.Arguments.FirstOrDefault(x => x.NameIdentifier == null && x.Value != null && L.IsStrategy(x.Value.GetText()));

    static ICSharpArgument? NamedArgument(IInvocationExpression call, string name) => call.Arguments.FirstOrDefault(a => a.NameIdentifier?.Name == name);

    static void SetScope(FixContext c, Advice a, string value)
    {
        if (a.Attribute != null) FixOps.SetAttributeProperty(c, a.Attribute, "Scope", value);
        else FixOps.SetNamedArgument(c, a.Call!, "scope", value);
        AddUsing(c, a.Node, AspectsNs);
    }

    /// <summary>Introduces an instance member: no static scope or IsStatic; IntroductionScope.Instance for a static template.</summary>
    static void MakeInstance(FixContext c, Advice a)
    {
        if (a.Call != null) RemoveBuilderAssignments(c, a.Call, "IsStatic", null);
        if (a.Template is { } template && FixOps.HasModifier(template, "static")) SetScope(c, a, "IntroductionScope.Instance");
        else if (a.Attribute != null) RemoveProperty(c, a.Attribute, "Scope");
        else if (NamedArgument(a.Call!, "scope") is { } scope) RemoveArgument(c, a.Call!, scope);
    }

    static bool HasFlag(Advice a, string property, string modifier) =>
        (a.Attribute != null && HasProperty(a.Attribute, property)) || (a.Call != null && BuilderAssignments(a.Call, property, null).Any())
        || (a.Template != null && FixOps.HasModifier(a.Template, modifier));

    /// <summary>Removes IsVirtual/IsSealed: the attribute property, the assignment in the build lambda, the template's modifier.</summary>
    static void RemoveFlag(FixContext c, Advice a, string property, string modifier)
    {
        if (a.Attribute != null) RemoveProperty(c, a.Attribute, property);
        if (a.Call != null) RemoveBuilderAssignments(c, a.Call, property, null);
        if (a.Template != null) FixOps.RemoveModifier(c, a.Template, modifier);
    }

    /// <summary>`x.Property = ...` (ending with <paramref name="value"/>) in the arguments of a call, e.g. its build lambda.</summary>
    static List<(ICSharpArgument Argument, IAssignmentExpression Assignment)> BuilderAssignments(IInvocationExpression call, string property, string? value) =>
        call.Arguments.SelectMany(argument => argument.Descendants<IAssignmentExpression>().ToEnumerable()
                .Where(x => x.Dest is IReferenceExpression r && r.NameIdentifier?.Name == property
                    && (value == null || x.Source?.GetText().EndsWith(value, StringComparison.Ordinal) == true))
                .Select(x => (argument, x)))
            .ToList();

    /// <summary>Removes the assignments: the whole argument when the lambda does nothing else, else the statement.</summary>
    static void RemoveBuilderAssignments(FixContext c, IInvocationExpression call, string property, string? value)
    {
        foreach (var (argument, assignment) in BuilderAssignments(call, property, value))
        {
            if (argument.Value is ILambdaExpression lambda && lambda.BodyExpression == assignment) RemoveArgument(c, call, argument);
            else if (assignment.Parent is IExpressionStatement statement) FixOps.DeleteWithLine(c, statement);
        }
    }

    /// <summary>Removes an argument with the comma (and comments) between it and its neighbor.</summary>
    static void RemoveArgument(FixContext c, IInvocationExpression call, ICSharpArgument argument)
    {
        var arguments = call.Arguments.ToList();
        var i = arguments.IndexOf(argument);
        if (i > 0) c.Delete(new DocumentRange(arguments[i - 1].GetDocumentEndOffset(), argument.GetDocumentEndOffset()));
        else if (arguments.Count > 1) c.Delete(new DocumentRange(argument.GetDocumentStartOffset(), arguments[1].GetDocumentStartOffset()));
        else c.Delete(argument);
    }

    static bool HasProperty(IAttribute attribute, string name) => attribute.PropertyAssignments.Any(p => p.PropertyNameIdentifier?.Name == name);

    /// <summary>Removes a named property of an attribute, and the parentheses when it was the only argument.</summary>
    static void RemoveProperty(FixContext c, IAttribute attribute, string name)
    {
        if (!HasProperty(attribute, name)) return;
        if (attribute.PropertyAssignments.Count == 1 && attribute.Arguments.Count == 0 && attribute.Name != null) c.Replace(attribute, attribute.Name.GetText());
        else FixOps.RemoveAttributeProperty(c, attribute, name);
    }

    /// <summary>Adds `using <paramref name="ns"/>;` to the file of <paramref name="node"/> (FixOps.AddUsing only does the context's file).</summary>
    static void AddUsing(FixContext c, ITreeNode node, string ns)
    {
        if (node.GetContainingFile() is not ICSharpFile file) return;
        var usings = file.Descendants<IUsingDirective>().ToEnumerable().ToList();
        if (usings.OfType<IUsingSymbolDirective>().Any(u => u.ImportedSymbolName?.QualifiedName == ns)) return;
        if (usings.LastOrDefault() is { } last) c.Insert(last.GetDocumentEndOffset(), $"\nusing {ns};");
        else c.Insert(new DocumentOffset(file.GetDocumentRange().Document, 0), $"using {ns};\n\n");
    }

    // --- The target ----------------------------------------------------------------------------------------------------

    static string? QuotedMember(FixContext c, int index) =>
        L.Quoted(c.Diagnostic.Message).ElementAtOrDefault(index) is { Length: > 0 } qualified ? L.MemberName(qualified) : null;

    static IClassLikeDeclaration? TargetType(FixContext c) => c.Target as IClassLikeDeclaration ?? c.At<IClassLikeDeclaration>();

    /// <summary>
    /// Source declarations of <paramref name="name"/> in the target type or the base type named <paramref name="typeName"/>
    /// (null: the first type of the hierarchy that has it), with <paramref name="parameters"/> parameters when given.
    /// </summary>
    static List<ICSharpTypeMemberDeclaration> SourceMembers(FixContext c, string? typeName, string name, int? parameters)
    {
        if (TargetType(c)?.DeclaredElement is not ITypeElement target) return [];
        var types = new[] { target }.Concat(target.GetAllSuperTypes().Select(t => t.GetTypeElement()).OfType<ITypeElement>());
        foreach (var type in types.Where(t => typeName == null || t.ShortName == typeName))
        {
            var found = type.GetMembers().Where(m => m.ShortName == name && (parameters == null || m is not IParametersOwner p || p.Parameters.Count == parameters))
                .SelectMany(m => m.GetDeclarations()).OfType<ICSharpTypeMemberDeclaration>().ToList();
            if (found.Count > 0 || typeName != null) return found;
        }
        return [];
    }

    static Fix? MakeOverridable(ICSharpModifiersOwnerDeclaration d, string label)
    {
        if (FixOps.HasModifier(d, "sealed")) return new Fix($"Remove 'sealed' from '{label}'", ctx => FixOps.RemoveModifier(ctx, d, "sealed"));
        if (new[] { "static", "virtual", "abstract", "override", "private" }.Any(m => FixOps.HasModifier(d, m))) return null;
        return new Fix($"Make '{label}' virtual", ctx => FixOps.AddModifier(ctx, d, "virtual"));
    }

    static ITypeUsage? TypeUsage(ICSharpTypeMemberDeclaration? d) => d switch
    {
        IMethodDeclaration m => m.TypeUsage,
        IPropertyDeclaration p => p.TypeUsage,
        IIndexerDeclaration i => i.TypeUsage,
        IFieldDeclaration f => (f.Parent as IMultipleFieldDeclaration)?.TypeUsage,
        _ => null,
    };

    /// <summary>Deletes a member with its line; a field or event declared with others only loses its declarator.</summary>
    static void DeleteMember(FixContext c, ICSharpTypeMemberDeclaration d)
    {
        if (d.Parent is IMultipleDeclaration multiple)
        {
            if (multiple.Declarators.Count == 1) FixOps.DeleteWithLine(c, multiple);
            else FixOps.DeleteListItem(c, d);
        }
        else FixOps.DeleteWithLine(c, d);
    }

    /// <summary>Classes and structs that list <paramref name="iface"/> among their base types.</summary>
    static List<ITypeElement> Implementers(ITypeElement iface)
    {
        var result = new List<ITypeElement>();
        var services = iface.GetPsiServices();
        services.Finder.FindInheritors(iface, services.SearchDomainFactory.CreateSearchDomain(iface.GetSolution(), false),
            new FindResultConsumer(r =>
            {
                if (r is FindResultDeclaredElement { DeclaredElement: ITypeElement t } && t is IClass or IStruct
                    && t.GetSuperTypes().Any(s => Equals(s.GetTypeElement(), iface)))
                    result.Add(t);
                return FindExecution.Continue;
            }), NullProgressIndicator.Create());
        return result;
    }
}
