using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util;
using LamaRider.Actions;
using LamaRider.Model;
using Logic = LamaRider.Model.TemplateAuthoringFixLogic;

namespace LamaRider.Fixes;

/// <summary>
/// Fixes for mistakes in compile-time code (aspects, templates, fabrics): missing or misplaced [Template]/[CompileTime],
/// `this` and `dynamic` in templates, scope mismatches, nested compile-time types, primary constructors.
/// </summary>
public sealed class TemplateAuthoringFixes : FixProvider
{
    const string Aspects = "Metalama.Framework.Aspects";
    const string Code = "Metalama.Framework.Code";
    const string MetaDefault = "meta.Default( meta.Target.Method.ReturnType )";

    /// <summary>Attributes that make a member a template or an advice (only one is allowed per declaration).</summary>
    static readonly string[] TemplateAttributes = ["Template", "Introduce", "InterfaceMember", "IntroduceDependency"];

    static readonly Dictionary<string, Func<FixContext, IEnumerable<Fix>>> ById = new()
    {
        ["LAMA0033"] = AddTemplate,
        ["LAMA0117"] = AddCompileTime,
        ["LAMA0291"] = AddCompileTime,
        ["LAMA0119"] = ImportAspects,
        ["LAMA0221"] = UseMetaThis,
        ["LAMA0251"] = DynamicToVar,
        ["LAMA0227"] = DynamicToObject,
        ["LAMA0246"] = DynamicInLocalFunction,
        ["LAMA0261"] = RemoveDuplicateTemplateAttribute,
        ["LAMA0262"] = MoveAccessorAttribute,
        ["LAMA0230"] = MakeNestedPrivate,
        ["LAMA0229"] = MoveNestedType,
        ["LAMA0231"] = NestedFabric,
        ["LAMA0283"] = ExplicitConstructor,
        ["LAMA0278"] = StructToClass,
        ["LAMA0276"] = CompileTimeTypeParameters,
        ["LAMA0272"] = RemoveReturn,
        ["LAMA0267"] = MethodGroupToLambda,
        ["LAMA0286"] = CaptureExpression,
        ["LAMA0256"] = CastToIExpression,
        ["LAMA0253"] = RunTimeMember,
        ["LAMA0242"] = UseValue,
        ["LAMA0245"] = UnwrapCompileTime,
        ["LAMA0104"] = FixScope,
        ["LAMA0108"] = MakeRunTimeVariable,
        ["LAMA0105"] = SplitDeclaration,
        ["LAMA0224"] = InitializeDynamicVariable,
        ["LAMA0225"] = DynamicInitializedWithNull,
        ["LAMA0250"] = DisallowedDynamic,
        ["LAMA0263"] = CastLambdaBody,
        ["LAMA0284"] = LambdaScope,
        ["LAMA0285"] = LocalFunctionAttribute,
    };

    public override IEnumerable<string> Ids => ById.Keys;

    public override IEnumerable<Fix> Fixes(FixContext context) =>
        ById.TryGetValue(context.Diagnostic.Id, out var fixes) ? fixes(context) : [];

    // --- Attributes and declarations ------------------------------------------------------------------------------

    /// <summary>LAMA0033 (reported on the target): [Template] on the member of the aspect class the advice names.</summary>
    static IEnumerable<Fix> AddTemplate(FixContext c)
    {
        if (Logic.TemplateMember(c.Diagnostic.Message) is not { } named || FixOps.TypeOf(c, "T:" + named.Class) is not { } type) yield break;
        var declaration = type.GetMembers().Where(m => m.ShortName == named.Member).SelectMany(m => m.GetDeclarations())
            .OfType<IAttributesOwnerDeclaration>().FirstOrDefault();
        if (declaration == null) yield break;
        yield return new Fix($"Add [Template] to '{named.Member}'", ctx =>
        {
            AddAttributeTo(ctx, declaration, "Template");
            AddUsing(ctx, declaration, Aspects);
        });
    }

    /// <summary>LAMA0117/LAMA0291: [CompileTime] on the declaration that references the compile-time-only type, or its type.</summary>
    static IEnumerable<Fix> AddCompileTime(FixContext c)
    {
        var member = c.Node?.GetContainingNode<IClassMemberDeclaration>(returnThis: true);
        if (member is not null and not ICSharpTypeDeclaration && !HasAttribute(member, "CompileTime"))
            yield return new Fix($"Add [CompileTime] to '{member.DeclaredName}'", ctx =>
            {
                AddAttributeTo(ctx, member, "CompileTime");
                AddUsing(ctx, member, Aspects);
            });
        var type = member as ICSharpTypeDeclaration ?? member?.GetContainingNode<ICSharpTypeDeclaration>();
        // An aspect class is run-time-or-compile-time by design: [CompileTime] goes on its helpers, not on it.
        if (type != null && !HasAttribute(type, "CompileTime") && !IsAspectClass(c, type))
            yield return new Fix($"Add [CompileTime] to '{type.DeclaredName}'", ctx =>
            {
                AddAttributeTo(ctx, type, "CompileTime");
                AddUsing(ctx, type, Aspects);
            });
        if (c.Diagnostic.Id == "LAMA0291" && c.Project != null)
            yield return new Fix($"Set MetalamaRoslynIsCompileTimeOnly = false in '{c.Project.Name}'",
                ctx => FixOps.SetProjectProperty(ctx, "MetalamaRoslynIsCompileTimeOnly", "false"));
    }

    /// <summary>LAMA0119: `using Metalama.Framework.Aspects;`, and its types by their short names.</summary>
    static IEnumerable<Fix> ImportAspects(FixContext c)
    {
        if (c.File is not { } file) yield break;
        yield return new Fix($"Add 'using {Aspects};'", ctx =>
        {
            AddUsing(ctx, file, Aspects);
            foreach (var (node, name) in QualifiedByAspects(file))
                ctx.Delete(Span(node.GetDocumentRange().Document, node.GetDocumentStartOffset().Offset, name.GetDocumentStartOffset().Offset));
        });
    }

    /// <summary>LAMA0261: removes one of the template or advice attributes of the declaration (and its accessors or owner).</summary>
    static IEnumerable<Fix> RemoveDuplicateTemplateAttribute(FixContext c)
    {
        var declaration = c.At<IAttributesOwnerDeclaration>() ?? c.Target as IAttributesOwnerDeclaration;
        if (declaration == null) yield break;
        var named = Logic.AttributeName(c.Diagnostic.Message);
        var candidates = new List<IAttributesOwnerDeclaration> { declaration };
        if (declaration is IAccessorOwnerDeclaration owner) candidates.AddRange(owner.AccessorDeclarations);
        if (declaration is IAccessorDeclaration && declaration.GetContainingNode<IAccessorOwnerDeclaration>() is { } containing) candidates.Add(containing);
        foreach (var candidate in candidates)
            foreach (var attribute in candidate.Attributes.Where(a => IsTemplateAttribute(a) || FixOps.ShortName(a) == named))
                yield return new Fix($"Remove [{FixOps.ShortName(attribute)}] from '{candidate.DeclaredName}'", ctx => FixOps.RemoveAttribute(ctx, attribute));
    }

    /// <summary>LAMA0262: moves the template or advice attribute of an accessor to its property or event.</summary>
    static IEnumerable<Fix> MoveAccessorAttribute(FixContext c)
    {
        var accessor = c.At<IAccessorDeclaration>() ?? c.Target as IAccessorDeclaration
            ?? c.Within<IAccessorDeclaration>().FirstOrDefault(a => a.Attributes.Any(IsTemplateAttribute));
        if (accessor?.GetContainingNode<IAccessorOwnerDeclaration>() is not { } owner) yield break;
        var ownerHasOne = owner.Attributes.Any(IsTemplateAttribute);
        foreach (var attribute in accessor.Attributes.Where(IsTemplateAttribute))
        {
            var name = FixOps.ShortName(attribute);
            if (ownerHasOne)
                yield return new Fix($"Remove [{name}] from the '{accessor.DeclaredName}' accessor", ctx => FixOps.RemoveAttribute(ctx, attribute));
            else
                yield return new Fix($"Move [{name}] to '{owner.DeclaredName}'", ctx =>
                {
                    FixOps.RemoveAttribute(ctx, attribute);
                    FixOps.AddAttribute(ctx, owner, attribute.GetText());
                });
        }
    }

    /// <summary>LAMA0230: a compile-time type nested in a run-time type must be private.</summary>
    static IEnumerable<Fix> MakeNestedPrivate(FixContext c)
    {
        if (TypeAt(c) is not { } type) yield break;
        yield return new Fix($"Make '{type.DeclaredName}' private", ctx => FixOps.SetAccessibility(ctx, type, "private"));
    }

    /// <summary>LAMA0229: moves the nested type after its outermost containing type.</summary>
    static IEnumerable<Fix> MoveNestedType(FixContext c)
    {
        if (TypeAt(c) is not { } type || Outermost(type) is not { } outer) yield break;
        yield return new Fix($"Move '{type.DeclaredName}' to the namespace level", ctx => MoveAfter(ctx, type, outer));
    }

    /// <summary>LAMA0231: a nested fabric becomes a type fabric of its containing type, or moves out of it.</summary>
    static IEnumerable<Fix> NestedFabric(FixContext c)
    {
        if (TypeAt(c) is not IClassLikeDeclaration type) yield break;
        var fabricBase = type.ExtendsList?.Descendants<IReferenceName>().ToEnumerable()
            .FirstOrDefault(n => n.ShortName is "ProjectFabric" or "NamespaceFabric");
        if (fabricBase?.NameIdentifier is { } baseName)
            yield return new Fix($"Derive '{type.DeclaredName}' from TypeFabric", ctx =>
            {
                ctx.Replace(baseName, "TypeFabric");
                foreach (var method in type.MethodDeclarations.Where(m => m.DeclaredName is "AmendProject" or "AmendNamespace"))
                {
                    if (method.NameIdentifier != null) ctx.Replace(method.NameIdentifier, "AmendType");
                    var amender = method.ParameterDeclarations.FirstOrDefault()?.Descendants<IReferenceName>().ToEnumerable()
                        .FirstOrDefault(n => n.ShortName is "IProjectAmender" or "INamespaceAmender");
                    if (amender?.NameIdentifier != null) ctx.Replace(amender.NameIdentifier, "ITypeAmender");
                }
                if (!FixOps.HasModifier(type, "private")) FixOps.SetAccessibility(ctx, type, "private");
                ctx.Tell($"'{type.DeclaredName}' now amends its containing type: review its AmendType method.");
            });
        if (Outermost(type) is { } outer)
            yield return new Fix($"Move '{type.DeclaredName}' to the namespace level", ctx => MoveAfter(ctx, type, outer));
    }

    /// <summary>LAMA0283: the primary constructor becomes an explicit constructor that assigns the fields and properties.</summary>
    static IEnumerable<Fix> ExplicitConstructor(FixContext c)
    {
        if (TypeAt(c) is not IClassLikeDeclaration type || type.TypeDeclarationKeyword?.GetText() == "record"
            || type.PrimaryConstructorDeclaration is not { LPar: { } lpar, RPar: { } rpar } primary) yield break;
        yield return new Fix($"Replace the primary constructor of '{type.DeclaredName}' with an explicit constructor",
            ctx => ToExplicitConstructor(ctx, type, primary, lpar, rpar));
    }

    static void ToExplicitConstructor(FixContext ctx, IClassLikeDeclaration type, IPrimaryConstructorDeclaration primary, ITokenNode lpar, ITokenNode rpar)
    {
        var document = lpar.GetDocumentRange().Document;
        var text = document.GetText();
        var parameterList = Span(lpar, rpar).GetText();
        var parameters = primary.ParameterDeclarations.Select(p => (Name: p.DeclaredName,
            Type: Logic.ParameterType(Span(document, p.GetDocumentStartOffset().Offset, p.GetNameDocumentRange().StartOffset.Offset).GetText()))).ToList();

        var start = lpar.GetDocumentStartOffset().Offset;
        while (start > 0 && text[start - 1] == ' ') start--;
        ctx.Delete(Span(document, start, rpar.GetDocumentEndOffset().Offset));

        string? baseArguments = null;
        if (type.ExtendsList is { } extends && Logic.BaseArguments(extends.GetText()) is { } arguments)
        {
            var offset = extends.GetDocumentStartOffset().Offset;
            baseArguments = extends.GetText().Substring(arguments.Open + 1, arguments.Close - arguments.Open - 1);
            ctx.Delete(Span(document, offset + arguments.Open, offset + arguments.Close + 1));
        }

        // Initializers that use the parameters move into the constructor.
        var assignments = new List<string>();
        var initializers = new List<DocumentRange>();
        foreach (var member in type.MemberDeclarations)
        {
            if (member is IFieldDeclaration { Initial: { } initial, NameIdentifier: { } name } field && parameters.Any(p => Logic.Uses(initial.GetText(), p.Name)))
            {
                assignments.Add($"this.{field.DeclaredName} = {initial.GetText()};");
                initializers.Add(initial.GetDocumentRange());
                ctx.Delete(Span(document, name.GetDocumentEndOffset().Offset, initial.GetDocumentEndOffset().Offset));
            }
            else if (member is IPropertyDeclaration { Initial: { } propertyInitial, RBrace: { } rbrace } property && parameters.Any(p => Logic.Uses(propertyInitial.GetText(), p.Name)))
            {
                assignments.Add($"this.{property.DeclaredName} = {propertyInitial.GetText()};");
                initializers.Add(propertyInitial.GetDocumentRange());
                ctx.Delete(Span(document, rbrace.GetDocumentEndOffset().Offset, ((ITreeNode?)property.Semicolon ?? propertyInitial).GetDocumentEndOffset().Offset));
            }
        }

        // Parameters used elsewhere (methods, accessors) become fields.
        var parameterRange = Span(lpar, rpar);
        var fields = new List<string>();
        foreach (var parameter in parameters)
        {
            var usedElsewhere = type.Descendants<IReferenceExpression>().ToEnumerable().Any(r => !r.IsQualified && r.NameIdentifier?.Name == parameter.Name
                && !parameterRange.Contains(r.GetDocumentRange()) && !initializers.Any(i => i.Contains(r.GetDocumentRange())));
            if (!usedElsewhere) continue;
            fields.Add($"private readonly {parameter.Type} {parameter.Name};");
            assignments.Add($"this.{parameter.Name} = {parameter.Name};");
        }

        var firstMember = type.MemberDeclarations.FirstOrDefault();
        var indent = firstMember != null ? FixOps.IndentAt(firstMember.GetDocumentStartOffset()) : FixOps.IndentAt(type.GetDocumentStartOffset()) + "    ";
        var lastField = type.FieldDeclarations.LastOrDefault();
        ITreeNode? anchor = (ITreeNode?)lastField?.GetContainingNode<IMultipleFieldDeclaration>() ?? (ITreeNode?)lastField ?? type.LBrace;
        if (anchor == null) return;
        var code = string.Concat(fields.Select(f => $"\n{indent}{f}"))
            + $"\n\n{indent}public {type.DeclaredName}{parameterList}"
            + (baseArguments != null ? $"\n{indent}    : base({baseArguments})" : "")
            + $"\n{indent}{{" + string.Concat(assignments.Select(a => $"\n{indent}    {a}")) + $"\n{indent}}}";
        if (anchor == type.LBrace) code = code.Substring(1);
        ctx.Insert(anchor.GetDocumentEndOffset(), code);
    }

    /// <summary>LAMA0278: an aspect must be a class.</summary>
    static IEnumerable<Fix> StructToClass(FixContext c)
    {
        if ((c.At<IStructDeclaration>() ?? c.Target as IStructDeclaration) is not { StructKeyword: { } keyword } type) yield break;
        yield return new Fix($"Make '{type.DeclaredName}' a class", ctx =>
        {
            ctx.Replace(keyword, "class");
            FixOps.RemoveModifier(ctx, type, "readonly");
            FixOps.RemoveModifier(ctx, type, "ref");
        });
    }

    /// <summary>LAMA0276 (reported on the call): [CompileTime] on the type parameters of the called template.</summary>
    static IEnumerable<Fix> CompileTimeTypeParameters(FixContext c)
    {
        var invocation = c.At<IInvocationExpression>() ?? c.Within<IInvocationExpression>().FirstOrDefault();
        if ((invocation?.InvokedExpression as IReferenceExpression)?.Reference.Resolve().DeclaredElement is not IMethod method) yield break;
        foreach (var declaration in method.GetDeclarations().OfType<IMethodDeclaration>())
        {
            var runTime = declaration.TypeParameterDeclarations.Where(t => FixOps.FindAttribute(t, "CompileTime") == null).ToList();
            if (runTime.Count == 0) continue;
            yield return new Fix($"Mark the type parameters of '{declaration.DeclaredName}' [CompileTime]", ctx =>
            {
                foreach (var parameter in runTime) FixOps.AddAttribute(ctx, parameter, "CompileTime");
                AddUsing(ctx, declaration, Aspects);
            });
        }
    }

    /// <summary>LAMA0285: the local function becomes an auxiliary template of the aspect class, or loses the attribute.</summary>
    static IEnumerable<Fix> LocalFunctionAttribute(FixContext c)
    {
        var function = c.At<ILocalFunctionDeclaration>() ?? c.Within<ILocalFunctionDeclaration>().FirstOrDefault(f => f.Attributes.Count > 0);
        if (function == null) yield break;
        var named = Logic.AttributeName(c.Diagnostic.Message);
        var attributes = c.At<IAttribute>() is { } at ? [at] : function.Attributes.Where(a => named == null || FixOps.ShortName(a) == named).ToList();
        var member = function.GetContainingNode<IClassMemberDeclaration>();
        if (member is not null and not ICSharpTypeDeclaration && member.GetContainingNode<ICSharpTypeDeclaration>() is { } type)
            yield return new Fix($"Move '{function.DeclaredName}' to a method of '{type.DeclaredName}'", ctx => LocalFunctionToMethod(ctx, function, member));
        foreach (var attribute in attributes)
            yield return new Fix($"Remove [{FixOps.ShortName(attribute)}] from '{function.DeclaredName}'", ctx => FixOps.RemoveAttribute(ctx, attribute));
    }

    static void LocalFunctionToMethod(FixContext ctx, ILocalFunctionDeclaration function, IClassMemberDeclaration member)
    {
        var range = function.GetDocumentRange();
        var start = range.StartOffset.Offset;
        var text = range.GetText().Insert(FixOps.StartAfterAttributes(function).Offset - start, "private ");
        text = Logic.Dedent(text, FixOps.IndentAt(range.StartOffset).Length - FixOps.IndentAt(member.GetDocumentStartOffset()).Length);

        if (!function.IsStatic && !(member is ICSharpModifiersOwnerDeclaration m && FixOps.HasModifier(m, "static")))
            foreach (var reference in member.Descendants<IReferenceExpression>().ToEnumerable().Where(r => !r.IsQualified
                         && r.NameIdentifier?.Name == function.DeclaredName && !range.Contains(r.GetDocumentRange())
                         && r.Reference.Resolve().DeclaredElement is var e && (e == null || e.Equals(function.DeclaredElement))))
                ctx.Insert(reference.GetDocumentStartOffset(), "this.");

        var statement = function.GetContainingNode<IDeclarationStatement>();
        FixOps.DeleteWithLine(ctx, statement?.LocalFunctionDeclaration == function ? statement : function);
        ctx.Insert(member.GetDocumentEndOffset(), "\n\n" + FixOps.IndentAt(member.GetDocumentStartOffset()) + text);
    }

    // --- Template code ------------------------------------------------------------------------------------------------

    /// <summary>LAMA0221: `this` in a template is the aspect; the target object is meta.This.</summary>
    static IEnumerable<Fix> UseMetaThis(FixContext c)
    {
        if ((c.At<IThisExpression>() ?? c.Within<IThisExpression>().FirstOrDefault()) is not { } self) yield break;
        yield return new Fix("Use 'meta.This'", ctx => ctx.Replace(self, "meta.This"));
    }

    /// <summary>LAMA0251: a variable initialized with a compile-time value is declared with `var`.</summary>
    static IEnumerable<Fix> DynamicToVar(FixContext c)
    {
        var declaration = c.At<ILocalVariableDeclaration>();
        var type = declaration != null ? SingleDeclarationType(declaration)
            : c.Node?.GetContainingNode<ICSharpStatement>(returnThis: true)?.Descendants<ITypeUsage>().ToEnumerable()
                .LastOrDefault(t => IsDynamic(t) && t.GetDocumentStartOffset().Offset <= c.Range.StartOffset.Offset);
        if (type == null || !IsDynamic(type)) yield break;
        yield return new Fix($"Declare '{declaration?.DeclaredName ?? c.Range.GetText()}' with 'var'", ctx => ctx.Replace(type, "var"));
    }

    /// <summary>LAMA0227: `dynamic` as a type argument, array element etc. becomes `object`.</summary>
    static IEnumerable<Fix> DynamicToObject(FixContext c)
    {
        var range = c.At<ITypeUsage>()?.GetDocumentRange() ?? c.Range;
        var text = range.GetText();
        if (!Logic.ContainsDynamic(text)) yield break;
        var replaced = Logic.ReplaceDynamic(text, alwaysNullable: false);
        yield return new Fix($"Use '{replaced}'", ctx => ctx.Replace(range, replaced));
    }

    /// <summary>LAMA0246: `dynamic` in the signature of a local function becomes `object?`.</summary>
    static IEnumerable<Fix> DynamicInLocalFunction(FixContext c)
    {
        var function = c.At<ILocalFunctionDeclaration>();
        var range = c.At<ITypeUsage>()?.GetDocumentRange() ?? c.Range;
        if (function != null)
        {
            var end = ((ITreeNode?)function.Body ?? function.ArrowClause)?.GetDocumentStartOffset() ?? function.GetDocumentEndOffset();
            range = Span(end.Document, FixOps.StartAfterAttributes(function).Offset, end.Offset);
        }
        var text = range.GetText();
        if (!Logic.ContainsDynamic(text)) yield break;
        yield return new Fix(function != null ? $"Use 'object?' instead of 'dynamic' in '{function.DeclaredName}'" : "Use 'object?' instead of 'dynamic'",
            ctx => ctx.Replace(range, Logic.ReplaceDynamic(text, alwaysNullable: true)));
    }

    /// <summary>LAMA0272: removes the redundant `return;` of a void template.</summary>
    static IEnumerable<Fix> RemoveReturn(FixContext c)
    {
        var statement = c.At<IReturnStatement>() ?? c.Within<IReturnStatement>().FirstOrDefault();
        if (statement is not { Value: null, Parent: IBlock }) yield break;
        yield return new Fix("Remove the redundant 'return'", ctx => FixOps.DeleteWithLine(ctx, statement));
    }

    /// <summary>LAMA0267: an extension method group becomes a lambda that invokes it.</summary>
    static IEnumerable<Fix> MethodGroupToLambda(FixContext c)
    {
        var candidates = c.At<IReferenceExpression>() is { } at ? [at] : c.Within<IReferenceExpression>().ToList();
        foreach (var reference in candidates)
        {
            if (!reference.IsQualified || reference.Parent is IInvocationExpression invocation && invocation.InvokedExpression == reference) continue;
            var method = reference.Reference.Resolve().DeclaredElement as IMethod;
            if (method is not { IsExtensionMethod: true } && !(method == null && c.Diagnostic.Message.Contains("." + reference.NameIdentifier?.Name))) continue;
            var lambda = Logic.LambdaFor(reference.GetText(), method != null ? Math.Max(0, method.Parameters.Count - 1) : 0);
            yield return new Fix($"Use '{lambda}'", ctx => ctx.Replace(reference, lambda));
            yield break;
        }
    }

    /// <summary>LAMA0286: a run-time expression becomes an IExpression with ExpressionFactory.Capture.</summary>
    static IEnumerable<Fix> CaptureExpression(FixContext c)
    {
        var cast = c.At<ICastExpression>() ?? c.Within<ICastExpression>().FirstOrDefault();
        if (cast?.Op is not { } operand) yield break;
        var code = $"ExpressionFactory.Capture( {operand.GetText()} )";
        yield return new Fix($"Use '{code}'", ctx =>
        {
            ctx.Replace(cast, code);
            AddUsing(ctx, cast, "Metalama.Framework.Code.SyntaxBuilders");
        });
    }

    /// <summary>LAMA0256: a dynamic argument of a compile-time method is cast to IExpression (or the IExpression itself is passed).</summary>
    static IEnumerable<Fix> CastToIExpression(FixContext c)
    {
        if (ExpressionAt(c) is not { } expression) yield break;
        var text = expression.GetText();
        var operand = expression is IPrimaryExpression ? text : $"({text})";
        yield return new Fix($"Cast '{text}' to 'IExpression'", ctx =>
        {
            ctx.Replace(expression, $"(IExpression) {operand}{(text.EndsWith("!") ? "" : "!")}");
            AddUsing(ctx, expression, Code);
        });
        if (expression is IReferenceExpression { NameIdentifier.Name: "Value", QualifierExpression: { } qualifier })
            yield return new Fix($"Pass '{qualifier.GetText()}' instead of '{text}'", ctx => ctx.Replace(expression, qualifier.GetText()));
    }

    /// <summary>LAMA0253: a compile-time object passed to run-time code is replaced by its name, value or reflection object.</summary>
    static IEnumerable<Fix> RunTimeMember(FixContext c)
    {
        if (ExpressionAt(c) is not { } expression) yield break;
        var text = expression.GetText();
        foreach (var member in Logic.RunTimeMembers(Logic.CompileTimeType(c.Diagnostic.Message)))
            yield return new Fix($"Pass '{text}.{member}'", ctx => ctx.Insert(expression.GetDocumentEndOffset(), "." + member));
    }

    /// <summary>LAMA0242: the compile-time sub-expression of a run-time expression is replaced by its run-time Value.</summary>
    static IEnumerable<Fix> UseValue(FixContext c)
    {
        var parent = ExpressionAt(c);
        var child = Logic.SubExpression(c.Diagnostic.Message) is { } sub
            ? c.Within<ICSharpExpression>().FirstOrDefault(e => SameCode(e.GetText(), sub))
            : parent switch
            {
                IAsExpression a => a.Operand,
                ICastExpression cast => cast.Op,
                _ => null,
            };
        if (child == null) yield break;
        var text = child.GetText();
        yield return new Fix($"Use '{text}.Value'", ctx => ctx.Insert(child.GetDocumentEndOffset(), ".Value"));
    }

    /// <summary>LAMA0245: meta.CompileTime(...) around an expression that involves run-time types is removed.</summary>
    static IEnumerable<Fix> UnwrapCompileTime(FixContext c)
    {
        var invocation = c.Node?.GetContainingNode<IInvocationExpression>(returnThis: true);
        while (invocation != null && FixOps.MethodName(invocation) != "CompileTime") invocation = invocation.GetContainingNode<IInvocationExpression>();
        invocation ??= c.Within<IInvocationExpression>().FirstOrDefault(i => FixOps.MethodName(i) == "CompileTime");
        if (invocation is not { Arguments.Count: 1 } || invocation.Arguments[0].Value is not { } argument) yield break;
        yield return new Fix($"Remove meta.CompileTime(...) around '{argument.GetText()}'", ctx => ctx.Replace(invocation, argument.GetText()));
    }

    /// <summary>LAMA0104: makes a variable compile-time (meta.CompileTime on its initializer), or a value run-time (meta.RunTime).</summary>
    static IEnumerable<Fix> FixScope(FixContext c)
    {
        if (Logic.ExpectsCompileTime(c.Diagnostic.Message) is not { } compileTime || ExpressionAt(c) is not { } expression) yield break;
        var text = expression.GetText();
        if (!compileTime)
        {
            yield return new Fix($"Wrap '{text}' in meta.RunTime(...)", ctx => ctx.Replace(expression, $"meta.RunTime( {text} )"));
            yield break;
        }
        if (LocalDeclarationOf(expression) is { Initial: IExpressionInitializer { Value: { } value } } declaration)
            yield return new Fix($"Make '{declaration.DeclaredName}' compile-time with meta.CompileTime(...)",
                ctx => ctx.Replace(value, $"meta.CompileTime( {value.GetText()} )"));
        else if (expression is not IReferenceExpression)
            yield return new Fix($"Wrap '{text}' in meta.CompileTime(...)", ctx => ctx.Replace(expression, $"meta.CompileTime( {text} )"));
    }

    /// <summary>LAMA0108: a compile-time variable set under a run-time condition becomes run-time.</summary>
    static IEnumerable<Fix> MakeRunTimeVariable(FixContext c)
    {
        var name = Regex.Match(c.Diagnostic.Message, @"variable '([^']+)'").Groups[1].Value;
        var reference = ExpressionAt(c) as IReferenceExpression ?? c.Within<IReferenceExpression>().FirstOrDefault(r => r.NameIdentifier?.Name == name);
        if (LocalDeclarationOf(reference) is not { Initial: IExpressionInitializer { Value: IInvocationExpression invocation } } declaration
            || FixOps.MethodName(invocation) != "CompileTime" || invocation.Arguments.Count != 1 || invocation.Arguments[0].Value is not { } value) yield break;
        yield return new Fix($"Make '{declaration.DeclaredName}' run-time (remove meta.CompileTime)", ctx => ctx.Replace(invocation, value.GetText()));
    }

    /// <summary>LAMA0105: one declaration per variable, so that compile-time and run-time variables are separate.</summary>
    static IEnumerable<Fix> SplitDeclaration(FixContext c)
    {
        var statement = c.At<IDeclarationStatement>() ?? c.Within<IDeclarationStatement>().FirstOrDefault(s => s.Declaration is IMultipleLocalVariableDeclaration { Declarators.Count: > 1 });
        if (statement?.Declaration is not IMultipleLocalVariableDeclaration { Declarators.Count: > 1 } declaration) yield break;
        var document = statement.GetDocumentRange().Document;
        var prefix = Span(document, declaration.GetDocumentStartOffset().Offset, declaration.Declarators[0].GetDocumentStartOffset().Offset).GetText();
        var names = string.Join(", ", declaration.Declarators.Select(d => $"'{d.DeclaredName}'"));
        yield return new Fix($"Declare {names} separately", ctx => ctx.Replace(statement,
            Logic.SplitDeclaration(prefix, declaration.Declarators.Select(d => d.GetText()), FixOps.IndentAt(statement.GetDocumentStartOffset()))));
    }

    /// <summary>LAMA0224: the dynamic variable is declared where it is first assigned, or initialized with meta.Default.</summary>
    static IEnumerable<Fix> InitializeDynamicVariable(FixContext c)
    {
        if (c.At<ILocalVariableDeclaration>() is not { Initial: null, NameIdentifier: { } name } declaration) yield break;
        var statement = declaration.GetContainingNode<IDeclarationStatement>();
        if (statement is { Parent: IBlock block, Declaration: IMultipleLocalVariableDeclaration { Declarators.Count: 1 } } && FirstAssignment(block, statement, name.Name) is { } assignment)
            yield return new Fix($"Declare '{name.Name}' where it is first assigned", ctx => DeclareAtAssignment(ctx, block, statement, assignment, name.Name));
        yield return new Fix($"Initialize '{name.Name}' with {MetaDefault}", ctx => ctx.Insert(name.GetDocumentEndOffset(), $" = {MetaDefault}"));
    }

    /// <summary>The first `name = ...;` statement after the declaration, in the block or in a try block of it (not a loop or branch).</summary>
    static IExpressionStatement? FirstAssignment(IBlock block, IDeclarationStatement declaration, string name)
    {
        var after = declaration.GetDocumentEndOffset().Offset;
        foreach (var assignment in block.Descendants<IAssignmentExpression>().ToEnumerable())
        {
            if (assignment.GetDocumentStartOffset().Offset < after || assignment.Dest is not IReferenceExpression { IsQualified: false } dest || dest.NameIdentifier?.Name != name) continue;
            if (assignment.OperatorSign?.GetText() != "=" || assignment.Parent is not IExpressionStatement statement) return null;
            for (var node = statement.Parent; node != block; node = node.Parent)
                if (node is not (IBlock or ITryStatement)) return null;
            return statement;
        }
        return null;
    }

    static void DeclareAtAssignment(FixContext ctx, IBlock block, IDeclarationStatement declaration, IExpressionStatement assignment, string name)
    {
        FixOps.DeleteWithLine(ctx, declaration);
        if (assignment.Expression is IAssignmentExpression { Dest: { } dest }) ctx.Replace(dest, "var " + name);
        if (assignment.Parent is not IBlock inner || inner == block) return;
        // The statements after the one that contains the assignment move to the end of the inner block, the variable's new scope.
        var statements = block.Statements.ToList();
        var outer = statements.FirstOrDefault(s => s.GetDocumentRange().Contains(assignment.GetDocumentRange()));
        if (outer == null || statements.IndexOf(outer) == statements.Count - 1) return;
        var document = block.GetDocumentRange().Document;
        var range = Span(document, outer.GetDocumentEndOffset().Offset, statements.Last().GetDocumentEndOffset().Offset);
        var outerIndent = FixOps.IndentAt(outer.GetDocumentStartOffset());
        var innerIndent = FixOps.IndentAt(assignment.GetDocumentStartOffset());
        var moved = Logic.Indent(range.GetText(), innerIndent.Length > outerIndent.Length ? innerIndent.Substring(outerIndent.Length) : "");
        ctx.Delete(range);
        ctx.Insert(inner.Statements.Last().GetDocumentEndOffset(), moved);
    }

    /// <summary>LAMA0225: a dynamic variable is initialized with meta.Default, or gets a non-dynamic type.</summary>
    static IEnumerable<Fix> DynamicInitializedWithNull(FixContext c)
    {
        if (c.At<ILocalVariableDeclaration>() is not { Initial: IExpressionInitializer { Value: { } value } } declaration) yield break;
        yield return new Fix($"Initialize '{declaration.DeclaredName}' with {MetaDefault}", ctx => ctx.Replace(value, MetaDefault));
        if (SingleDeclarationType(declaration) is { } type && IsDynamic(type))
            yield return new Fix($"Declare '{declaration.DeclaredName}' as 'object?'", ctx => ctx.Replace(type, "object?"));
    }

    /// <summary>LAMA0250: default(dynamic) becomes meta.Default(...); casts to dynamic are removed.</summary>
    static IEnumerable<Fix> DisallowedDynamic(FixContext c)
    {
        var node = c.Node;
        var @default = node?.GetContainingNode<IDefaultExpression>(returnThis: true) ?? c.Within<IDefaultExpression>().FirstOrDefault(d => d.TypeName != null && IsDynamic(d.TypeName));
        if (@default?.TypeName != null && IsDynamic(@default.TypeName))
        {
            yield return new Fix($"Use {MetaDefault}", ctx => ctx.Replace(@default, MetaDefault));
            yield break;
        }
        var cast = node?.GetContainingNode<ICastExpression>(returnThis: true) ?? c.Within<ICastExpression>().FirstOrDefault(x => x.TargetType != null && IsDynamic(x.TargetType));
        if (cast is { TargetType: { } target, Op: { } operand } && IsDynamic(target))
        {
            yield return new Fix("Remove the cast to 'dynamic'", ctx => ctx.Replace(cast, operand.GetText()));
            yield break;
        }
        var @as = node?.GetContainingNode<IAsExpression>(returnThis: true) ?? c.Within<IAsExpression>().FirstOrDefault(x => x.TypeOperand != null && IsDynamic(x.TypeOperand));
        if (@as is { TypeOperand: { } asType, Operand: { } asOperand } && IsDynamic(asType))
            yield return new Fix("Remove 'as dynamic'", ctx => ctx.Replace(@as, asOperand.GetText()));
    }

    /// <summary>LAMA0263: the dynamic body of an expression lambda is cast to a non-dynamic type.</summary>
    static IEnumerable<Fix> CastLambdaBody(FixContext c)
    {
        var lambda = c.At<ILambdaExpression>() ?? c.Within<ILambdaExpression>().FirstOrDefault(l => l.BodyExpression != null);
        if (lambda?.BodyExpression is not { } body) yield break;
        var text = body is IPrimaryExpression ? body.GetText() : $"({body.GetText()})";
        yield return new Fix("Cast the lambda body to 'object?'", ctx => ctx.Replace(body, $"(object?) {text}"));
        yield return new Fix("Cast the lambda body to 'IExpression'", ctx =>
        {
            ctx.Replace(body, $"(IExpression) {text}");
            AddUsing(ctx, body, Code);
        });
    }

    /// <summary>LAMA0284: a lambda with a block body assigned to a delegate variable gets an explicit scope.</summary>
    static IEnumerable<Fix> LambdaScope(FixContext c)
    {
        var function = c.At<IAnonymousFunctionExpression>() ?? c.Within<IAnonymousFunctionExpression>().FirstOrDefault();
        if (function?.Parent is not IExpressionInitializer { Parent: ILocalVariableDeclaration declaration }) yield break;
        var multiple = declaration.MultipleDeclaration as IMultipleLocalVariableDeclaration;
        if ((multiple?.TypeUsage ?? declaration.TypeUsage) is not { } type || type.GetText() == "var") yield break;
        var single = multiple == null || multiple.Declarators.Count == 1;
        foreach (var (scope, adjective) in new[] { ("RunTime", "run-time"), ("CompileTime", "compile-time") })
            yield return new Fix($"Make the lambda {adjective} with meta.{scope}(...)", ctx =>
            {
                if (single) ctx.Replace(type, "var");
                ctx.Replace(function, $"meta.{scope}( new {type.GetText()}( {function.GetText()} ) )");
            });
    }

    // --- Helpers ------------------------------------------------------------------------------------------------------

    static DocumentRange Span(IDocument document, int start, int end) => new(document, new TextRange(start, end));

    static DocumentRange Span(ITreeNode from, ITreeNode to) =>
        Span(from.GetDocumentRange().Document, from.GetDocumentStartOffset().Offset, to.GetDocumentEndOffset().Offset);

    static bool SameCode(string a, string b) => Regex.Replace(a, @"\s+", "") == Regex.Replace(b, @"\s+", "");

    static bool IsDynamic(ITypeUsage type) => type.GetText().Trim() is "dynamic" or "dynamic?";

    static bool IsTemplateAttribute(IAttribute attribute) => TemplateAttributes.Contains(FixOps.ShortName(attribute));

    static bool HasAttribute(ITreeNode declaration, string name) =>
        declaration is IAttributesOwnerDeclaration owner && FixOps.FindAttribute(owner, name) != null;

    static bool IsAspectClass(FixContext c, ICSharpTypeDeclaration type) =>
        type.DeclaredElement is { } element && AspectTargets.DocId(element) is { } id && c.Model?.IsAspectClass(id) == true;

    /// <summary>The type declaration the diagnostic is on (its name), else the one it names.</summary>
    static ICSharpTypeDeclaration? TypeAt(FixContext c) => c.At<ICSharpTypeDeclaration>() ?? c.Target as ICSharpTypeDeclaration;

    static ICSharpTypeDeclaration? Outermost(ICSharpTypeDeclaration type)
    {
        ICSharpTypeDeclaration? outer = null;
        for (var t = type.GetContainingNode<ICSharpTypeDeclaration>(); t != null; t = t.GetContainingNode<ICSharpTypeDeclaration>()) outer = t;
        return outer;
    }

    /// <summary>The expression exactly at the reported span, else the smallest one around it.</summary>
    static ICSharpExpression? ExpressionAt(FixContext c) =>
        c.Within<ICSharpExpression>().FirstOrDefault(e => e.GetDocumentRange().TextRange == c.Range.TextRange) ?? c.At<ICSharpExpression>();

    static ILocalVariableDeclaration? LocalDeclarationOf(ICSharpExpression? expression) =>
        (expression as IReferenceExpression)?.Reference.Resolve().DeclaredElement is ILocalVariable local
            ? local.GetDeclarations().OfType<ILocalVariableDeclaration>().FirstOrDefault() : null;

    /// <summary>The type of a local variable declaration with a single variable (where `var` is allowed).</summary>
    static ITypeUsage? SingleDeclarationType(ILocalVariableDeclaration declaration) =>
        declaration.MultipleDeclaration is IMultipleLocalVariableDeclaration multiple
            ? multiple.Declarators.Count == 1 ? multiple.TypeUsage : null
            : declaration.TypeUsage;

    /// <summary>Adds an attribute on its own line; a field's goes above its whole declaration (`int a, b;`).</summary>
    static void AddAttributeTo(FixContext c, IAttributesOwnerDeclaration declaration, string attribute)
    {
        if (declaration is IFieldDeclaration field) FixOps.InsertLineAbove(c, field.GetContainingNode<IMultipleFieldDeclaration>() ?? (ITreeNode)field, $"[{attribute}]");
        else FixOps.AddAttribute(c, declaration, attribute);
    }

    /// <summary>`using <paramref name="ns"/>;` in the file of <paramref name="node"/>, in alphabetical order among its usings.</summary>
    static void AddUsing(FixContext c, ITreeNode node, string ns)
    {
        if ((node as ICSharpFile ?? node.GetContainingFile()) is not ICSharpFile file) return;
        var usings = file.Descendants<IUsingSymbolDirective>().ToEnumerable().Where(u => !u.IsAlias && !u.IsStatic).ToList();
        if (usings.Any(u => u.ImportedSymbolName?.QualifiedName == ns)) return;
        if (usings.LastOrDefault(u => string.CompareOrdinal(u.ImportedSymbolName?.QualifiedName, ns) < 0) is { } previous)
            c.Insert(previous.GetDocumentEndOffset(), $"\n{FixOps.IndentAt(previous.GetDocumentStartOffset())}using {ns};");
        else if (usings.FirstOrDefault() is { } first)
            c.Insert(first.GetDocumentStartOffset(), $"using {ns};\n{FixOps.IndentAt(first.GetDocumentStartOffset())}");
        else
            c.Insert(new DocumentOffset(file.GetDocumentRange().Document, 0), $"using {ns};\n\n");
    }

    /// <summary>References written `Metalama.Framework.Aspects.X`, with the name that remains once the qualifier is removed.</summary>
    static IEnumerable<(ITreeNode Node, ITreeNode Name)> QualifiedByAspects(ICSharpFile file)
    {
        bool IsAspects(ITreeNode? qualifier) => qualifier != null && Regex.Replace(qualifier.GetText(), @"\s+", "") is Aspects or "global::" + Aspects;
        foreach (var name in file.Descendants<IReferenceName>().ToEnumerable())
            if (IsAspects(name.Qualifier) && name.NameIdentifier != null && name.GetContainingNode<IUsingDirective>() == null)
                yield return (name, name.NameIdentifier);
        foreach (var reference in file.Descendants<IReferenceExpression>().ToEnumerable())
            if (IsAspects(reference.QualifierExpression) && reference.NameIdentifier != null)
                yield return (reference, reference.NameIdentifier);
    }

    /// <summary>Moves a nested type after <paramref name="anchor"/> (its outermost containing type), as an internal type if it was private or protected.</summary>
    static void MoveAfter(FixContext ctx, ICSharpTypeDeclaration type, ICSharpTypeDeclaration anchor)
    {
        var range = type.GetDocumentRange();
        var start = range.StartOffset.Offset;
        var text = range.GetText();
        var access = FixOps.Modifiers(type).Where(m => m.GetText() is "private" or "protected").ToList();
        for (var i = access.Count - 1; i >= 0; i--)
        {
            var s = access[i].GetDocumentStartOffset().Offset - start;
            var e = access[i].GetDocumentEndOffset().Offset - start;
            if (i > 0) while (e < text.Length && text[e] == ' ') e++;
            text = text.Substring(0, s) + (i == 0 ? "internal" : "") + text.Substring(e);
        }
        text = Logic.Dedent(text, FixOps.IndentAt(range.StartOffset).Length - FixOps.IndentAt(anchor.GetDocumentStartOffset()).Length);
        FixOps.DeleteWithLine(ctx, type);
        ctx.Insert(anchor.GetDocumentEndOffset(), "\n\n" + FixOps.IndentAt(anchor.GetDocumentStartOffset()) + text);
    }
}
