using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Resolve;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using LamaRider.Model;

namespace LamaRider.Fixes;

// Fixes for the diagnostics of Metalama.Patterns (contracts, immutability, caching, observability, WPF commands) in user
// code. The message names the declarations; PSI finds them, also in other files of the solution.

/// <summary>Metalama.Patterns.Contracts.</summary>
public sealed class ContractFixes : FixProvider
{
    const string Contracts = "Metalama.Patterns.Contracts";

    public override IEnumerable<string> Ids => ["LAMA5001", "LAMA5002", "LAMA5003", "LAMA5004", "LAMA5005", "LAMA5006", "LAMA5007"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        var message = c.Diagnostic.Message;
        switch (c.Diagnostic.Id)
        {
            case "LAMA5001":
            {
                // "... cannot be satisfied by the type Byte": a wider numeric type.
                if (c.At<IDeclaration>() is not { } declaration || Regex.Match(message, @"by the type (\w+)") is not { Success: true } m
                    || PatternsFixLogic.NumericKeyword(m.Groups[1].Value) is not { } keyword) yield break;
                var name = declaration.DeclaredName;
                var nameStart = declaration.GetNameDocumentRange().StartOffset.Offset;
                var owner = declaration is IFieldDeclaration ? declaration.GetContainingNode<IMultipleFieldDeclaration>() ?? (ITreeNode)declaration : declaration;
                var typeUsage = owner.Descendants<IPredefinedTypeUsage>().ToEnumerable()
                    .FirstOrDefault(t => t.GetText() == keyword && t.GetDocumentEndOffset().Offset <= nameStart);
                if (typeUsage == null) yield break;
                foreach (var type in PatternsFixLogic.WiderNumericTypes(message))
                    yield return new Fix($"Change the type of '{name}' to '{type}'", ctx => ctx.Replace(typeUsage, type));
                break;
            }
            case "LAMA5002":
            {
                if (c.At<IDeclaration>() is not { } declaration) yield break;
                var name = declaration.DeclaredName;
                if (NullableMark(declaration) is { } mark)
                    yield return new Fix($"Make '{name}' non-nullable", ctx => MakeNonNullable(ctx, declaration, mark));
                if (Contract(message) is { } contract && declaration is IAttributesOwnerDeclaration owner && FixOps.FindAttribute(owner, contract) is { } attribute)
                    yield return new Fix($"Remove [{FixOps.ShortName(attribute)}] from '{name}'", ctx => RemoveContract(ctx, attribute));
                break;
            }
            case "LAMA5003":
            case "LAMA5006":
            {
                if (c.At<IDeclaration>() is not IAttributesOwnerDeclaration declaration || Contract(message) is not { } contract
                    || FixOps.FindAttribute(declaration, contract) is not { } attribute) yield break;
                var shortName = FixOps.ShortName(attribute);
                yield return new Fix($"Remove the redundant [{shortName}] from '{declaration.DeclaredName}'", ctx => RemoveContract(ctx, attribute));
                // [NonNegative] on an unsigned type: maybe zero was meant to be excluded.
                if (c.Diagnostic.Id == "LAMA5006" && shortName is "NonNegative" or "Positive")
                    yield return new Fix($"Replace [{shortName}] with [StrictlyPositive]", ctx => RenameAttribute(ctx, attribute, "StrictlyPositive"));
                break;
            }
            case "LAMA5004":
            {
                if (c.At<IMethodDeclaration>() is not { } method) yield break;
                if (c.Project != null && c.File != null)
                    yield return new Fix("Enable invariant suspension in a project fabric", AddInvariantSuspensionFabric);
                if (FixOps.FindAttribute(method, "SuspendInvariants") is { } attribute)
                    yield return new Fix($"Remove [SuspendInvariants] from '{method.DeclaredName}'", ctx => RemoveContract(ctx, attribute));
                break;
            }
            case "LAMA5005":
            {
                if (c.At<IMethodDeclaration>() is not { } method) yield break;
                if (FixOps.FindAttribute(method, "SuspendInvariants") is { } attribute)
                    yield return new Fix($"Remove [SuspendInvariants] from '{method.DeclaredName}'", ctx => RemoveContract(ctx, attribute));
                if (method.GetContainingNode<IClassLikeDeclaration>() is { } type)
                    yield return new Fix($"Add an [Invariant] method to '{type.DeclaredName}'", ctx =>
                    {
                        PatternsOps.AppendMember(ctx, type,
                            "[Invariant]\nprivate void CheckInvariants()\n{\n    // Throw an InvariantViolationException when the object is in an invalid state.\n}");
                        PatternsOps.AddUsing(ctx, type, Contracts);
                    });
                break;
            }
            case "LAMA5007":
            {
                var attribute = c.At<IAttribute>();
                if (attribute == null && c.At<IDeclaration>() is IAttributesOwnerDeclaration owner && Regex.Match(message, @"\[(\w+?)(Attribute)?\] attribute") is { Success: true } m)
                    attribute = FixOps.FindAttribute(owner, m.Groups[1].Value);
                if (attribute == null || PatternsFixLogic.InequalityAlternatives(FixOps.ShortName(attribute)) is not { } alternatives) yield break;
                var shortName = FixOps.ShortName(attribute);
                yield return new Fix($"Replace [{shortName}] with [{alternatives.Strict}]", ctx => RenameAttribute(ctx, attribute, alternatives.Strict));
                yield return new Fix($"Replace [{shortName}] with [{alternatives.NonStrict}]", ctx => RenameAttribute(ctx, attribute, alternatives.NonStrict));
                break;
            }
        }
    }

    /// <summary>The contract the message names: "The [NotNull] contract", "The [NotNullAttribute] contract".</summary>
    static string? Contract(string message) =>
        Regex.Match(message, @"\[(\w+?)(Attribute)?\] contract") is { Success: true } m ? m.Groups[1].Value : null;

    static void RemoveContract(FixContext c, IAttribute attribute)
    {
        FixOps.RemoveAttribute(c, attribute);
        PatternsOps.RemoveUsingIfUnused(c, attribute, Contracts);
    }

    static void RenameAttribute(FixContext c, IAttribute attribute, string shortName)
    {
        if (attribute.Name?.NameIdentifier is not { } identifier) return;
        c.Replace(identifier, identifier.Name.EndsWith("Attribute", StringComparison.Ordinal) ? shortName + "Attribute" : shortName);
    }

    /// <summary>The `?` of the declaration's type, right before its name.</summary>
    static DocumentRange? NullableMark(IDeclaration declaration)
    {
        var name = declaration.GetNameDocumentRange();
        if (!name.IsValid()) return null;
        var text = name.Document.GetText();
        var i = name.StartOffset.Offset - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        return i >= 0 && text[i] == '?' ? new DocumentRange(name.Document, new JetBrains.Util.TextRange(i, i + 1)) : null;
    }

    /// <summary>Removes the `?`; a string auto-property or field without initializer gets `= ""`, so that it stays non-null.</summary>
    static void MakeNonNullable(FixContext c, IDeclaration declaration, DocumentRange mark)
    {
        c.Delete(mark);
        var before = mark.Document.GetText(new JetBrains.Util.TextRange(Math.Max(0, mark.StartOffset.Offset - 6), mark.StartOffset.Offset));
        if (!before.EndsWith("string", StringComparison.Ordinal)) return;
        switch (declaration)
        {
            case IPropertyDeclaration { Initial: null } property when property.AccessorDeclarations.Count > 0
                && property.AccessorDeclarations.All(a => a.Body == null && a.ArrowClause == null):
                c.Insert(property.GetDocumentEndOffset(), " = \"\";");
                break;
            case IFieldDeclaration { Initial: null } field:
                c.Insert(field.GetNameDocumentRange().EndOffset, " = \"\"");
                break;
        }
    }

    /// <summary>A new ContractsFabric.cs next to the file, which enables invariant suspension for the project.</summary>
    static void AddInvariantSuspensionFabric(FixContext c)
    {
        if (c.File?.GetSourceFile()?.GetLocation().FullPath is not { } path || Path.GetDirectoryName(path) is not { } directory) return;
        var ns = c.File.NamespaceDeclarations.FirstOrDefault()?.DeclaredName;
        var fabric = Path.Combine(directory, "ContractsFabric.cs");
        for (var i = 2; File.Exists(fabric); i++) fabric = Path.Combine(directory, $"ContractsFabric{i}.cs");
        var className = Path.GetFileNameWithoutExtension(fabric);
        var code = $$"""
            using Metalama.Framework.Fabrics;
            using Metalama.Patterns.Contracts;
            {{(string.IsNullOrEmpty(ns) ? "" : $"\nnamespace {ns};\n")}}
            internal class {{className}} : ProjectFabric
            {
                public override void AmendProject( IProjectAmender amender )
                {
                    amender.ConfigureContracts( new ContractOptions { IsInvariantSuspensionSupported = true } );
                }
            }

            """;
        c.CreateFile(fabric, code.Replace("\n", Environment.NewLine));
        c.Tell($"Added {Path.GetFileName(fabric)}, a fabric that enables invariant suspension in project '{c.Project!.Name}'.");
    }
}

/// <summary>Metalama.Patterns.Immutability.</summary>
public sealed class ImmutabilityFixes : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA5020", "LAMA5021", "LAMA5022"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if (c.At<IDeclaration>() is not { } declaration) yield break;
        var name = declaration.DeclaredName;
        var type = declaration.GetContainingNode<IClassLikeDeclaration>();
        var immutable = type == null ? null : FixOps.FindAttribute(type, "Immutable");
        switch (c.Diagnostic.Id)
        {
            case "LAMA5020" when declaration is IFieldDeclaration field:
                yield return new Fix($"Make '{name}' readonly", ctx => PatternsOps.AddFieldModifier(ctx, field, "readonly"));
                break;
            case "LAMA5021" when declaration is IPropertyDeclaration property:
            {
                if (property.AccessorDeclarations.FirstOrDefault(a => a.Kind == AccessorKind.SETTER) is not { } setter) break;
                yield return new Fix($"Remove the setter of '{name}'", ctx => ctx.Delete(PatternsOps.WithPrecedingSpace(setter)));
                if (setter.Descendants<ITokenNode>().ToEnumerable().FirstOrDefault(t => t.GetText() == "set") is { } keyword)
                    yield return new Fix($"Replace 'set' with 'init' in '{name}'", ctx => ctx.Replace(keyword, "init"));
                break;
            }
            case "LAMA5022":
            {
                if (PatternsOps.TypeUsage(declaration) is { } typeUsage && PatternsFixLogic.ImmutableCollectionFor(typeUsage.GetText()) is { } collection)
                    yield return new Fix($"Change the type of '{name}' to '{collection.Type}'", ctx => UseImmutableCollection(ctx, declaration, typeUsage, collection));
                if ((declaration.DeclaredElement as ITypeOwner)?.Type is IDeclaredType declared && declared.GetTypeElement() is { } memberType
                    && memberType.GetDeclarations().OfType<IClassLikeDeclaration>().FirstOrDefault() is { } memberTypeDeclaration)
                    yield return new Fix($"Mark '{memberType.ShortName}' as deeply immutable", ctx =>
                    {
                        if (FixOps.FindAttribute(memberTypeDeclaration, "Immutable") is { } existing) ctx.Replace(existing, "Immutable(ImmutabilityKind.Deep)");
                        else FixOps.AddAttribute(ctx, memberTypeDeclaration, "Immutable(ImmutabilityKind.Deep)");
                        PatternsOps.AddUsing(ctx, memberTypeDeclaration, "Metalama.Patterns.Immutability");
                    });
                if (immutable != null && immutable.Arguments.Count == 1 && immutable.Arguments[0].GetText().Contains("Deep") && immutable.Name is { } attributeName)
                    yield return new Fix($"Make '{type!.DeclaredName}' shallowly immutable", ctx => ctx.Replace(immutable, attributeName.GetText()));
                break;
            }
        }
        if (immutable != null && c.Diagnostic.Id != "LAMA5022")
            yield return new Fix($"Remove [Immutable] from '{type!.DeclaredName}'", ctx => FixOps.RemoveAttribute(ctx, immutable));
    }

    /// <summary>Changes the type and converts what is assigned to the member: its initializer and the assignments in its type.</summary>
    static void UseImmutableCollection(FixContext c, IDeclaration declaration, ITypeUsage typeUsage, PatternsFixLogic.ImmutableCollection collection)
    {
        c.Replace(typeUsage, collection.Type);
        var initializer = declaration switch
        {
            IPropertyDeclaration property => property.Initial,
            IFieldDeclaration field => field.Initial,
            _ => null,
        };
        if ((initializer as IExpressionInitializer)?.Value is { } initial) Convert(c, initial, collection);
        if (declaration.DeclaredElement is { } member && declaration.GetContainingNode<IClassLikeDeclaration>()?.DeclaredElement is { } type)
            foreach (var part in type.GetDeclarations())
            foreach (var assignment in part.Descendants<IAssignmentExpression>().ToEnumerable())
                if (assignment.Dest is IReferenceExpression destination && Equals(destination.Reference.Resolve().DeclaredElement, member) && assignment.Source is { } source)
                    Convert(c, source, collection);
        PatternsOps.AddUsing(c, declaration, "System.Collections.Immutable");
    }

    static void Convert(FixContext c, ICSharpExpression value, PatternsFixLogic.ImmutableCollection collection)
    {
        var text = value.GetText();
        if (text.StartsWith("[", StringComparison.Ordinal)) return; // collection expressions work for immutable collections too
        if (value is IObjectCreationExpression { Initializer: null } creation)
        {
            // new List<T>() or new List<T>(capacity): empty; new List<T>(items): items converted.
            if (creation.Arguments.Count == 0 || creation.Arguments.Count == 1 && creation.Arguments[0].Value is ICSharpLiteralExpression)
            {
                c.Replace(value, collection.Type + ".Empty");
                return;
            }
            if (creation.Arguments.Count == 1 && creation.Arguments[0].Value is { } items)
            {
                c.Replace(value, Primary(items) + "." + collection.Conversion + "()");
                return;
            }
        }
        c.Replace(value, Primary(value) + "." + collection.Conversion + "()");
    }

    static string Primary(ICSharpExpression expression) =>
        expression is IReferenceExpression or IInvocationExpression or IObjectCreationExpression or IElementAccessExpression or IParenthesizedExpression
            ? expression.GetText() : "(" + expression.GetText() + ")";
}

/// <summary>Metalama.Patterns.Caching: [Cache] and [InvalidateCache].</summary>
public sealed class CachingFixes : FixProvider
{
    const string Aspects = "Metalama.Patterns.Caching.Aspects";

    public override IEnumerable<string> Ids =>
        ["LAMA5100", "LAMA5101", "LAMA5102", "LAMA5103", "LAMA5104", "LAMA5105", "LAMA5106", "LAMA5107", "LAMA5110", "LAMA5112"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if ((c.At<IMethodDeclaration>() ?? c.Target as IMethodDeclaration) is not { } method) yield break;
        var name = method.DeclaredName;
        var invalidate = FixOps.FindAttribute(method, "InvalidateCache");
        var message = c.Diagnostic.Message;
        var id = c.Diagnostic.Id;
        // Metalama 2026.1 reports "applied at most once" as LAMA5110.
        if (id == "LAMA5110" && message.Contains("at most once")) id = "LAMA5112";
        switch (id)
        {
            case "LAMA5100":
                foreach (var cached in CachedMethod(method, invalidate, message))
                    yield return new Fix($"Add [Cache] to '{cached.DeclaredName}'", ctx =>
                    {
                        FixOps.AddAttribute(ctx, cached, "Cache");
                        PatternsOps.AddUsing(ctx, cached, Aspects);
                    });
                if (invalidate != null)
                    yield return new Fix($"Remove [InvalidateCache] from '{name}'", ctx => FixOps.RemoveAttribute(ctx, invalidate));
                break;
            case "LAMA5101":
                foreach (var cached in CachedMethod(method, invalidate, message))
                    if (FixOps.FindAttribute(cached, "Cache") is { } cache)
                        yield return new Fix($"Set IgnoreThisParameter = true on [Cache] of '{cached.DeclaredName}'",
                            ctx => FixOps.SetAttributeProperty(ctx, cache, "IgnoreThisParameter", "true"));
                break;
            case "LAMA5102":
            {
                // "the invalidating method does not contain a parameter named 'productId'"
                if (Regex.Match(message, @"parameter named '(\w+)'") is not { Success: true } m) break;
                var missing = m.Groups[1].Value;
                foreach (var cached in CachedMethod(method, invalidate, message))
                {
                    var cachedParameters = cached.ParameterDeclarations.ToList();
                    if (cachedParameters.FirstOrDefault(p => p.DeclaredName == missing) is not { } cachedParameter) continue;
                    var cachedType = TypeName(cachedParameter);
                    // The parameters of the invalidating method the cached method doesn't have; of the same type when there are such.
                    var candidates = method.ParameterDeclarations.Where(p => cachedParameters.All(q => q.DeclaredName != p.DeclaredName)).ToList();
                    if (candidates.Any(p => TypeName(p) == cachedType)) candidates = candidates.Where(p => TypeName(p) == cachedType).ToList();
                    foreach (var parameter in candidates)
                        yield return new Fix($"Rename parameter '{parameter.DeclaredName}' to '{missing}'", ctx => FixOps.Rename(ctx, parameter, missing));
                    yield return NotCacheKey(cached, cachedParameter);
                }
                break;
            }
            case "LAMA5103":
            {
                // "the type of the 'productId' parameter of the cached method is not compatible"
                if (Regex.Match(message, @"the type of the '(\w+)' parameter") is not { Success: true } m) break;
                var parameterName = m.Groups[1].Value;
                foreach (var cached in CachedMethod(method, invalidate, message))
                {
                    if (cached.ParameterDeclarations.FirstOrDefault(p => p.DeclaredName == parameterName) is not { } cachedParameter
                        || PatternsOps.TypeUsage(cachedParameter) is not { } cachedType) continue;
                    if (method.ParameterDeclarations.FirstOrDefault(p => p.DeclaredName == parameterName) is { } parameter && PatternsOps.TypeUsage(parameter) is { } type)
                        yield return new Fix($"Change the type of '{parameterName}' to '{cachedType.GetText()}'", ctx => ChangeParameterType(ctx, method, parameter, type, cachedType.GetText()));
                    yield return NotCacheKey(cached, cachedParameter);
                }
                break;
            }
            case "LAMA5104":
            case "LAMA5105":
            case "LAMA5106":
            {
                if (invalidate == null) break;
                // 5104: an empty name; 5106: a name that doesn't exist (quoted in the message); 5105: no name at all.
                var badName = id == "LAMA5106" ? PatternsFixLogic.Quoted(message).Skip(1).FirstOrDefault() : null;
                var argument = id == "LAMA5105" ? null : invalidate.Arguments.FirstOrDefault(a =>
                    a.Value is ICSharpLiteralExpression literal && literal.GetText().StartsWith("\"", StringComparison.Ordinal)
                    && (badName == null ? literal.GetText().Trim('"').Trim().Length == 0 : literal.GetText().Trim('"') == badName));
                if (id != "LAMA5105" && argument == null) break;
                foreach (var nameofExpression in CachedNames(method, invalidate))
                    yield return new Fix($"Use {nameofExpression}", ctx =>
                    {
                        if (argument != null) ctx.Replace(argument, nameofExpression);
                        else PatternsOps.AddPositionalArgument(ctx, invalidate, nameofExpression);
                    });
                break;
            }
            case "LAMA5107":
                if (invalidate != null)
                    yield return new Fix("Set AllowMultipleOverloads = true", ctx => FixOps.SetAttributeProperty(ctx, invalidate, "AllowMultipleOverloads", "true"));
                break;
            case "LAMA5110":
                if ((FixOps.FindAttribute(method, "Cache") ?? invalidate) is { } attribute)
                    yield return new Fix($"Set UseDependencyInjection = false on [{FixOps.ShortName(attribute)}]",
                        ctx => FixOps.SetAttributeProperty(ctx, attribute, "UseDependencyInjection", "false"));
                if (FixOps.HasModifier(method, "static"))
                    yield return new Fix($"Make '{name}' non-static", ctx => FixOps.RemoveModifier(ctx, method, "static"));
                break;
            case "LAMA5112":
                if (FixOps.FindAttribute(method, "Cache") is { } duplicate)
                    yield return new Fix($"Remove [Cache] from '{name}' (keep the fabric's)", ctx => FixOps.RemoveAttribute(ctx, duplicate));
                break;
        }
    }

    static Fix NotCacheKey(IMethodDeclaration cached, ICSharpParameterDeclaration parameter) =>
        new($"Add [NotCacheKey] to '{parameter.DeclaredName}' of '{cached.DeclaredName}'", ctx =>
        {
            FixOps.AddAttribute(ctx, parameter, "NotCacheKey");
            PatternsOps.AddUsing(ctx, parameter, "Metalama.Patterns.Caching");
        });

    static string? TypeName(ICSharpParameterDeclaration parameter) =>
        (parameter.DeclaredElement as IParameter)?.Type.GetPresentableName(CSharpLanguage.Instance);

    /// <summary>The type whose methods [InvalidateCache] invalidates: its typeof argument, else the method's type.</summary>
    static ITypeElement? InvalidatedType(IMethodDeclaration method, IAttribute? invalidate) =>
        invalidate?.Arguments.FirstOrDefault()?.Value is ITypeofExpression { ArgumentType: IDeclaredType type }
            ? type.GetTypeElement()
            : method.DeclaredElement?.GetContainingType();

    /// <summary>
    /// The cached method the message names ('ProductCatalogue.GetPrice(string)', the quoted signature that isn't the
    /// invalidating method), in source.
    /// </summary>
    static List<IMethodDeclaration> CachedMethod(IMethodDeclaration method, IAttribute? invalidate, string message)
    {
        var signature = PatternsFixLogic.Quoted(message).LastOrDefault(q => q.Contains("(") && PatternsFixLogic.MemberName(q) != method.DeclaredName);
        if (signature == null || InvalidatedType(method, invalidate) is not { } type) return [];
        var name = PatternsFixLogic.MemberName(signature);
        var parameterTypes = PatternsFixLogic.ParameterTypes(signature);
        var methods = type.Methods.Where(m => m.ShortName == name).ToList();
        var exact = methods.Where(m => parameterTypes != null
            && m.Parameters.Select(p => p.Type.GetPresentableName(CSharpLanguage.Instance)).SequenceEqual(parameterTypes)).ToList();
        return (exact.Count > 0 ? exact : methods).SelectMany(m => m.GetDeclarations()).OfType<IMethodDeclaration>().ToList();
    }

    /// <summary>nameof(...) of each method with [Cache] in the invalidated type.</summary>
    static IEnumerable<string> CachedNames(IMethodDeclaration method, IAttribute invalidate)
    {
        if (InvalidatedType(method, invalidate) is not { } type) return [];
        var prefix = Equals(type, method.DeclaredElement?.GetContainingType()) ? "" : type.ShortName + ".";
        return type.Methods
            .Where(m => m.GetDeclarations().OfType<IMethodDeclaration>().Any(d => FixOps.FindAttribute(d, "Cache") != null))
            .Select(m => $"nameof({prefix}{m.ShortName})").Distinct();
    }

    /// <summary>Changes the parameter's type; to string, `p.ToString()` in the method becomes `p`.</summary>
    static void ChangeParameterType(FixContext c, IMethodDeclaration method, ICSharpParameterDeclaration parameter, ITypeUsage type, string newType)
    {
        c.Replace(type, newType);
        if (newType != "string" || parameter.DeclaredElement is not { } element) return;
        foreach (var invocation in method.Descendants<IInvocationExpression>().ToEnumerable())
            if (invocation.Arguments.Count == 0 && invocation.InvokedExpression is IReferenceExpression { NameIdentifier.Name: "ToString", QualifierExpression: IReferenceExpression qualifier }
                && Equals(qualifier.Reference.Resolve().DeclaredElement, element))
                c.Replace(invocation, qualifier.GetText());
    }
}

/// <summary>Metalama.Patterns.Observability: [Observable].</summary>
public sealed class ObservabilityFixes : FixProvider
{
    const string Observability = "Metalama.Patterns.Observability";
    static readonly string[] NotifyMethods = ["OnPropertyChanged", "NotifyOfPropertyChange", "RaisePropertyChanged"];

    public override IEnumerable<string> Ids => ["LAMA5150", "LAMA5151", "LAMA5154", "LAMA5156", "LAMA5161", "LAMA5162", "LAMA5164", "LAMA5166"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        switch (c.Diagnostic.Id)
        {
            case "LAMA5150":
            {
                if (c.At<IClassLikeDeclaration>() is not { } type) yield break;
                var bases = BaseTypes(type).ToList();
                var method = bases.SelectMany(t => t.Methods).FirstOrDefault(m => NotifyMethods.Contains(m.ShortName) && m.Parameters.Count == 1);
                if (method?.GetDeclarations().OfType<IMethodDeclaration>().FirstOrDefault() is { } declaration)
                {
                    var access = method.GetAccessRights();
                    var makeProtected = access is not (AccessRights.PUBLIC or AccessRights.PROTECTED or AccessRights.PROTECTED_OR_INTERNAL);
                    var makeVirtual = !(method.IsVirtual || method.IsOverride || method.IsAbstract) || method.IsSealed;
                    if (makeProtected || makeVirtual)
                        yield return new Fix($"Make '{method.GetContainingType()?.ShortName}.{method.ShortName}' {(makeProtected ? "protected " : "")}virtual", ctx =>
                        {
                            if (makeProtected) FixOps.SetAccessibility(ctx, declaration, "protected");
                            FixOps.RemoveModifier(ctx, declaration, "sealed");
                            if (!(method.IsVirtual || method.IsOverride || method.IsAbstract)) FixOps.AddModifier(ctx, declaration, "virtual");
                        });
                }
                else if (bases.FirstOrDefault(t => t.Events.Any(e => e.ShortName == "PropertyChanged"))?.GetDeclarations().OfType<IClassLikeDeclaration>().FirstOrDefault() is { } eventOwner)
                    yield return new Fix($"Add 'protected virtual void OnPropertyChanged(string)' to '{eventOwner.DeclaredName}'", ctx =>
                    {
                        PatternsOps.AppendMember(ctx, eventOwner,
                            "protected virtual void OnPropertyChanged( string propertyName )\n    => this.PropertyChanged?.Invoke( this, new PropertyChangedEventArgs( propertyName ) );");
                        PatternsOps.AddUsing(ctx, eventOwner, "System.ComponentModel");
                    });
                if (FixOps.FindAttribute(type, "Observable") is { } observable)
                    yield return new Fix($"Remove [Observable] from '{type.DeclaredName}'", ctx => FixOps.RemoveAttribute(ctx, observable));
                break;
            }
            case "LAMA5151":
                if (c.Project == null) yield break;
                yield return new Fix($"Remove InpcDiagnosticCommentVerbosity from '{c.Project.Name}'", ctx => FixOps.RemoveProjectProperty(ctx, "InpcDiagnosticCommentVerbosity"));
                yield return new Fix("Set InpcDiagnosticCommentVerbosity to 0", ctx => FixOps.SetProjectProperty(ctx, "InpcDiagnosticCommentVerbosity", "0"));
                break;
            case "LAMA5154":
            {
                if (c.At<IPropertyDeclaration>() is not { } property) yield break;
                yield return new Fix($"Remove 'virtual' from '{property.DeclaredName}'", ctx => FixOps.RemoveModifier(ctx, property, "virtual"));
                yield return new Fix($"Add [NotObservable] to '{property.DeclaredName}'", ctx =>
                {
                    FixOps.AddAttribute(ctx, property, "NotObservable");
                    PatternsOps.AddUsing(ctx, property, Observability);
                });
                break;
            }
            case "LAMA5156":
            {
                if (c.At<IClassLikeDeclaration>() is not { } type) yield break;
                // A notify method taking PropertyChangedEventArgs, in the class or a base class: add the string overload next to it.
                var method = SelfAndBaseTypes(type).SelectMany(t => t.Methods).FirstOrDefault(m => NotifyMethods.Contains(m.ShortName)
                    && m.Parameters.Count == 1 && m.Parameters[0].Type.GetPresentableName(CSharpLanguage.Instance) == "PropertyChangedEventArgs");
                if (method?.GetDeclarations().FirstOrDefault()?.GetContainingNode<IClassLikeDeclaration>() is not { } owner) yield break;
                yield return new Fix($"Add '{method.ShortName}(string)' to '{owner.DeclaredName}'", ctx =>
                {
                    PatternsOps.AppendMember(ctx, owner,
                        $"protected void {method.ShortName}( string propertyName )\n{{\n    this.{method.ShortName}( new PropertyChangedEventArgs( propertyName ) );\n}}");
                    PatternsOps.AddUsing(ctx, owner, "System.ComponentModel");
                });
                break;
            }
            case "LAMA5161":
            case "LAMA5166":
            {
                // 5161: the type of the child property ('Person' in this.Person.Name); 5166: the base type declaring the property.
                var referenced = c.At<IReferenceExpression>()?.Reference.Resolve().DeclaredElement;
                var childType = c.Diagnostic.Id == "LAMA5161"
                    ? ((referenced as ITypeOwner)?.Type as IDeclaredType)?.GetTypeElement()
                    : (referenced as ITypeMember)?.GetContainingType();
                var typeName = c.Diagnostic.Id == "LAMA5161" ? PatternsFixLogic.Quoted(c.Diagnostic.Message).FirstOrDefault()
                    : PatternsFixLogic.Quoted(c.Diagnostic.Message).Skip(1).FirstOrDefault();
                if (childType == null || typeName != null && childType.ShortName != typeName)
                    childType = c.File?.Descendants<IClassLikeDeclaration>().ToEnumerable().FirstOrDefault(d => d.DeclaredName == typeName)?.DeclaredElement;
                if (childType?.GetDeclarations().OfType<IClassDeclaration>().FirstOrDefault() is { } declaration)
                {
                    yield return new Fix($"Add [Observable] to '{declaration.DeclaredName}'", ctx =>
                    {
                        FixOps.AddAttribute(ctx, declaration, "Observable");
                        FixOps.AddModifier(ctx, declaration, "partial");
                        PatternsOps.AddUsing(ctx, declaration, Observability);
                    });
                    if (c.Diagnostic.Id == "LAMA5161")
                        yield return new Fix($"Add [Immutable] to '{declaration.DeclaredName}'", ctx =>
                        {
                            FixOps.AddAttribute(ctx, declaration, "Immutable");
                            PatternsOps.AddUsing(ctx, declaration, "Metalama.Patterns.Immutability");
                        });
                }
                if (c.Diagnostic.Id == "LAMA5161" && c.At<IPropertyDeclaration>() is { } computed)
                    yield return new Fix($"Add [SuppressObservabilityWarnings] to '{computed.DeclaredName}'", ctx =>
                    {
                        FixOps.AddAttribute(ctx, computed, "SuppressObservabilityWarnings");
                        PatternsOps.AddUsing(ctx, computed, Observability);
                    });
                break;
            }
            case "LAMA5162":
            {
                if (c.At<IReferenceExpression>()?.Reference.Resolve().DeclaredElement is not IMethod method
                    || method.GetDeclarations().OfType<IMethodDeclaration>().FirstOrDefault() is not { } declaration) yield break;
                yield return new Fix($"Add [Constant] to '{method.ShortName}'", ctx =>
                {
                    FixOps.AddAttribute(ctx, declaration, "Constant");
                    PatternsOps.AddUsing(ctx, declaration, Observability);
                });
                if (!method.IsStatic && !UsesInstance(declaration))
                    yield return new Fix($"Make '{method.ShortName}' static", ctx =>
                    {
                        FixOps.AddModifier(ctx, declaration, "static");
                        // this.M(...) doesn't compile for a static method.
                        foreach (var reference in declaration.GetPsiServices().Finder.FindAllReferences(method))
                            if (reference.GetTreeNode() is IReferenceExpression { QualifierExpression: IThisExpression qualifier, NameIdentifier: { } identifier })
                                ctx.Delete(new DocumentRange(qualifier.GetDocumentStartOffset(), identifier.GetDocumentStartOffset()));
                    });
                break;
            }
            case "LAMA5164":
            {
                if (c.At<IReferenceExpression>()?.Reference.Resolve().DeclaredElement is not IField field
                    || field.GetDeclarations().OfType<IFieldDeclaration>().FirstOrDefault() is not { } declaration) yield break;
                var name = field.ShortName;
                var multiple = declaration.GetContainingNode<IMultipleFieldDeclaration>();
                var readOnly = field.IsReadonly || field.IsConstant;
                if (!readOnly && multiple != null && multiple.Declarators.Count == 1 && declaration.NameIdentifier is { } identifier)
                    yield return new Fix($"Replace field '{name}' with an auto-property", ctx =>
                    {
                        var initial = declaration.Initial?.GetText();
                        ctx.Replace(new DocumentRange(identifier.GetDocumentEndOffset(), multiple.GetDocumentEndOffset()),
                            " { get; set; }" + (initial == null ? "" : $" = {initial};"));
                    });
                if (field.GetAccessRights() != AccessRights.PRIVATE)
                    yield return new Fix($"Make '{name}' private", ctx => FixOps.SetAccessibility(ctx, declaration, "private"));
                if (!readOnly)
                    yield return new Fix($"Make '{name}' readonly", ctx => PatternsOps.AddFieldModifier(ctx, declaration, "readonly"));
                break;
            }
        }
    }

    static IEnumerable<ITypeElement> BaseTypes(IClassLikeDeclaration type) => SelfAndBaseTypes(type).Skip(1);

    static IEnumerable<ITypeElement> SelfAndBaseTypes(IClassLikeDeclaration type)
    {
        var seen = new HashSet<ITypeElement>();
        for (var current = type.DeclaredElement; current != null && seen.Add(current); current = (current as IClass)?.GetBaseClassType()?.GetTypeElement())
            yield return current;
    }

    /// <summary>Whether a method uses `this` or instance members of its type (then it can't be made static).</summary>
    static bool UsesInstance(IMethodDeclaration method) =>
        method.Descendants<IThisExpression>().ToEnumerable().Any() || method.Descendants<IBaseExpression>().ToEnumerable().Any()
        || method.Descendants<IReferenceExpression>().ToEnumerable().Any(r => r.QualifierExpression == null
            && r.Reference.Resolve().DeclaredElement is ITypeMember { IsStatic: false } member and not ITypeElement
            && member.GetContainingType() is { } owner && method.DeclaredElement?.GetContainingType() is { } type
            && (Equals(owner, type) || type.IsDescendantOf(owner)));
}

/// <summary>Metalama.Patterns.Wpf: [Command] and [DependencyProperty].</summary>
public sealed class WpfFixes : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA5201", "LAMA5202", "LAMA5203", "LAMA5206", "LAMA5208"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        var message = c.Diagnostic.Message;
        switch (c.Diagnostic.Id)
        {
            case "LAMA5201":
            {
                if (Command(c) is not { } command) yield break;
                foreach (var property in new[] { "CanExecuteProperty", "CanExecuteMethod" })
                    if (command.PropertyAssignments.Any(p => p.PropertyNameIdentifier?.Name == property))
                        yield return new Fix($"Remove {property} from [Command]", ctx => FixOps.RemoveAttributeProperty(ctx, command, property));
                break;
            }
            case "LAMA5202":
            {
                if (c.At<IPropertyDeclaration>() is not { } property) yield break;
                yield return new Fix($"Make '{property.DeclaredName}' public", ctx => FixOps.SetAccessibility(ctx, property, "public"));
                // The command method: 'DocumentViewModel.Save()'.
                var methodName = PatternsFixLogic.Quoted(message).Select(PatternsFixLogic.MemberName).FirstOrDefault();
                var command = property.GetContainingNode<IClassLikeDeclaration>()?.DeclaredElement?.Methods.Where(m => m.ShortName == methodName)
                    .SelectMany(m => m.GetDeclarations()).OfType<IMethodDeclaration>().Select(m => FixOps.FindAttribute(m, "Command")).FirstOrDefault(a => a != null);
                if (command != null)
                    yield return new Fix($"Set EnableINotifyPropertyChangedIntegration = false on [Command] of '{methodName}'",
                        ctx => FixOps.SetAttributeProperty(ctx, command, "EnableINotifyPropertyChangedIntegration", "false"));
                break;
            }
            case "LAMA5203":
            {
                // "The name of the existing field 'ProductControl.QuantityProperty' ... conflicts": the hand-written member the aspect generates.
                if (c.At<IDeclaration>()?.GetContainingNode<IClassLikeDeclaration>()?.DeclaredElement is not { } type
                    || PatternsFixLogic.Quoted(message).FirstOrDefault() is not { } existing) yield break;
                var memberName = PatternsFixLogic.MemberName(existing);
                foreach (var member in type.GetMembers().Where(m => m.ShortName == memberName))
                foreach (var declaration in member.GetDeclarations())
                {
                    var node = declaration is IFieldDeclaration field ? field.GetContainingNode<IMultipleFieldDeclaration>() is { Declarators.Count: 1 } multiple ? multiple : null : (ITreeNode)declaration;
                    if (node != null)
                        yield return new Fix($"Remove '{memberName}' (the aspect generates it)", ctx => FixOps.DeleteWithLine(ctx, node));
                }
                break;
            }
            case "LAMA5206":
            {
                // "No can-execute method or can-execute property matching 'CanSaveDocument' was found for the command 'Document.Save()'."
                if (c.At<IDeclaration>() is not IAttributesOwnerDeclaration target || PatternsFixLogic.Quoted(message).FirstOrDefault() is not { } missing) yield break;
                var attribute = FixOps.FindAttribute(target, "Command") ?? FixOps.FindAttribute(target, "DependencyProperty");
                var assignment = attribute?.PropertyAssignments.FirstOrDefault(p => p.Source?.GetText().Trim('"') == missing);
                if (attribute == null || assignment?.PropertyNameIdentifier?.Name is not { } property) yield break;
                if (target.GetContainingNode<IClassLikeDeclaration>()?.DeclaredElement is { } type)
                {
                    var wantsProperty = property.EndsWith("Property", StringComparison.Ordinal);
                    var wantsBool = property.StartsWith("CanExecute", StringComparison.Ordinal);
                    var candidates = wantsProperty
                        ? type.Properties.Where(p => !wantsBool || IsBool(p.Type)).Select(p => p.ShortName)
                        : type.Methods.Where(m => !wantsBool || IsBool(m.ReturnType)).Select(m => m.ShortName);
                    foreach (var candidate in candidates.Where(n => n != target.DeclaredName).Distinct())
                        yield return new Fix($"Use nameof({candidate})", ctx => ctx.Replace(assignment.Source!, $"nameof({candidate})"));
                }
                yield return new Fix($"Remove {property} from [{FixOps.ShortName(attribute)}]", ctx => FixOps.RemoveAttributeProperty(ctx, attribute, property));
                break;
            }
            case "LAMA5208":
            {
                if (Command(c) is { } command)
                    yield return new Fix("Set Background = true on [Command]", ctx => FixOps.SetAttributeProperty(ctx, command, "Background", "true"));
                if ((c.At<IMethodDeclaration>() ?? c.Target as IMethodDeclaration)?.ParameterDeclarations.LastOrDefault() is { } token
                    && (token.DeclaredElement as IParameter)?.Type.GetPresentableName(CSharpLanguage.Instance) == "CancellationToken")
                    yield return new Fix($"Remove the CancellationToken parameter '{token.DeclaredName}'", ctx => FixOps.DeleteListItem(ctx, token));
                break;
            }
        }
    }

    static IAttribute? Command(FixContext c) =>
        (c.At<IMethodDeclaration>() ?? c.Target as IMethodDeclaration) is { } method ? FixOps.FindAttribute(method, "Command") : null;

    static bool IsBool(IType type) => type.GetPresentableName(CSharpLanguage.Instance) == "bool";
}

/// <summary>Edits the Patterns fixes share, beyond <see cref="FixOps"/>: usings in any file, members, arguments.</summary>
static class PatternsOps
{
    /// <summary>Adds `using <paramref name="ns"/>;` to the file of <paramref name="node"/> (which may not be the diagnostic's).</summary>
    public static void AddUsing(FixContext c, ITreeNode node, string ns)
    {
        if (node.GetContainingFile() is not ICSharpFile file) return;
        var usings = file.Descendants<IUsingDirective>().ToEnumerable().ToList();
        if (usings.OfType<IUsingSymbolDirective>().Any(u => u.ImportedSymbolName?.QualifiedName == ns)) return;
        if (usings.LastOrDefault() is { } last) c.Insert(last.GetDocumentEndOffset(), $"\nusing {ns};");
        else c.Insert(new DocumentOffset(file.GetDocumentRange().Document, 0), $"using {ns};\n\n");
    }

    /// <summary>
    /// Removes `using <paramref name="ns"/>;` when nothing else in the file uses it once <paramref name="removed"/> is gone.
    /// Keeps it when a reference doesn't resolve (it might come from the namespace).
    /// </summary>
    public static void RemoveUsingIfUnused(FixContext c, ITreeNode removed, string ns)
    {
        if (removed.GetContainingFile() is not ICSharpFile file) return;
        var directive = file.Descendants<IUsingSymbolDirective>().ToEnumerable().FirstOrDefault(u => u.ImportedSymbolName?.QualifiedName == ns);
        if (directive == null) return;
        var skip = removed.GetDocumentRange();
        var references = file.Descendants<IReferenceName>().ToEnumerable().Select(r => ((ITreeNode)r, (IReference)r.Reference))
            .Concat(file.Descendants<IReferenceExpression>().ToEnumerable().Select(r => ((ITreeNode)r, (IReference)r.Reference)));
        foreach (var (node, reference) in references)
        {
            if (node.GetContainingNode<IUsingDirective>() != null || skip.Contains(node.GetDocumentRange())) continue;
            var element = reference.Resolve().DeclaredElement;
            if (element == null) return;
            var type = element as ITypeElement ?? (element as ITypeMember)?.GetContainingType();
            if (type?.GetContainingNamespace().QualifiedName == ns) return;
        }
        FixOps.DeleteWithLine(c, directive);
    }

    /// <summary>Adds a member (lines without indentation) at the end of a type, after a blank line.</summary>
    public static void AppendMember(FixContext c, IClassLikeDeclaration type, string code)
    {
        if (type.Body?.RBrace is not { } rbrace) return;
        var start = rbrace.GetDocumentStartOffset();
        var text = start.Document.GetText();
        var lineStart = text.LastIndexOf('\n', Math.Max(0, start.Offset - 1)) + 1;
        var braceAlone = text.Substring(lineStart, start.Offset - lineStart).Trim().Length == 0;
        var indent = FixOps.IndentAt(start) + "    ";
        var member = string.Join("\n", code.Split('\n').Select(l => l.Length == 0 ? l : indent + l));
        if (braceAlone) c.Insert(new DocumentOffset(start.Document, lineStart), "\n" + member + "\n");
        else c.Insert(start, "\n" + member + "\n");
    }

    /// <summary>Adds a positional argument to an attribute: after the other positional ones, before the named ones.</summary>
    public static void AddPositionalArgument(FixContext c, IAttribute attribute, string argument)
    {
        if (attribute.PropertyAssignments.FirstOrDefault() is { } named) c.Insert(named.GetDocumentStartOffset(), argument + ", ");
        else if (attribute.Arguments.LastOrDefault() is { } last) c.Insert(last.GetDocumentEndOffset(), ", " + argument);
        else
        {
            var range = attribute.GetDocumentRange();
            var text = range.GetText();
            var close = text.LastIndexOf(')');
            if (text.IndexOf('(') >= 0 && close >= 0) c.Insert(new DocumentOffset(range.Document, range.StartOffset.Offset + close), argument);
            else c.Insert(range.EndOffset, $"({argument})");
        }
    }

    /// <summary>
    /// <see cref="FixOps.AddModifier"/> for a field: the modifiers belong to its multiple declaration, so without any the
    /// modifier goes before the type, not before the field's name.
    /// </summary>
    public static void AddFieldModifier(FixContext c, IFieldDeclaration field, string modifier)
    {
        if (FixOps.Modifiers(field).Any() || TypeUsage(field) is not { } type) FixOps.AddModifier(c, field, modifier);
        else c.Insert(type.GetDocumentStartOffset(), modifier + " ");
    }

    /// <summary>The written type of a property, field or parameter.</summary>
    public static ITypeUsage? TypeUsage(ITreeNode declaration) => declaration switch
    {
        IPropertyDeclaration property => property.TypeUsage,
        IFieldDeclaration field => field.GetContainingNode<IMultipleFieldDeclaration>()?.TypeUsage,
        ICSharpRegularParameterDeclaration parameter => parameter.TypeUsage,
        _ => null,
    };

    /// <summary>The node's range with the whitespace before it (an accessor in `{ get; set; }`).</summary>
    public static DocumentRange WithPrecedingSpace(ITreeNode node)
    {
        var range = node.GetDocumentRange();
        var text = range.Document.GetText();
        var start = range.StartOffset.Offset;
        while (start > 0 && char.IsWhiteSpace(text[start - 1])) start--;
        return new DocumentRange(range.Document, new JetBrains.Util.TextRange(start, range.EndOffset.Offset));
    }
}
