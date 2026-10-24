using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using LamaRider.Actions;
using LamaRider.Model;
using static LamaRider.Model.CompileTimeCodeFixLogic;

namespace LamaRider.Fixes;

// Quick-fixes for compile-time code errors: scopes, templates, fabrics, aspect ordering, serialization.

/// <summary>Edits shared by the compile-time code fixes (in any document, not only the diagnostic's).</summary>
static class CompileTimeOps
{
    public const string Aspects = "Metalama.Framework.Aspects";
    public const string Serialization = "Metalama.Framework.Serialization";
    static readonly string[] ScopeAttributes = ["CompileTime", "RunTimeOrCompileTime"];

    /// <summary>[CompileTime] or [RunTimeOrCompileTime] of a declaration.</summary>
    public static IAttribute? ScopeAttribute(IAttributesOwnerDeclaration declaration) =>
        declaration.Attributes.FirstOrDefault(a => ScopeAttributes.Contains(FixOps.ShortName(a)));

    /// <summary>`using <paramref name="ns"/>;` in the file of <paramref name="node"/> (FixOps.AddUsing only does the diagnostic's file).</summary>
    public static void AddUsingIn(FixContext c, ITreeNode node, string ns)
    {
        if (node.GetContainingFile() is not ICSharpFile file) return;
        var usings = file.Descendants<IUsingDirective>().ToEnumerable().ToList();
        if (usings.OfType<IUsingSymbolDirective>().Any(u => u.ImportedSymbolName?.QualifiedName == ns)) return;
        if (usings.LastOrDefault() is { } last) c.Insert(last.GetDocumentEndOffset(), $"\nusing {ns};");
        else c.Insert(new DocumentOffset(file.GetDocumentRange().Document, 0), $"using {ns};\n\n");
    }

    /// <summary>An attribute on its own line, right above the declaration proper (after its other attributes); fields too.</summary>
    public static void AddAttributeLine(FixContext c, ITreeNode declaration, string attribute)
    {
        var node = declaration is IFieldDeclaration field ? field.GetContainingNode<IMultipleFieldDeclaration>() ?? declaration : declaration;
        var anchor = FixOps.StartAfterAttributes(node);
        var text = anchor.Document.GetText();
        var lineStart = anchor.Offset == 0 ? 0 : text.LastIndexOf('\n', anchor.Offset - 1) + 1;
        c.Insert(new DocumentOffset(anchor.Document, lineStart), $"{FixOps.IndentAt(anchor)}[{attribute}]\n");
    }

    /// <summary>A member at the start of a type's body, indented like its members.</summary>
    public static bool InsertMember(FixContext c, IClassLikeDeclaration type, string member)
    {
        if (type.Body is not { } body || body.Children().OfType<ITokenNode>().FirstOrDefault(t => t.GetText() == "{") is not { } brace) return false;
        var first = body.Children().FirstOrDefault(n => n is not ITokenNode);
        var indent = first != null ? FixOps.IndentAt(first.GetDocumentStartOffset()) : FixOps.IndentAt(type.GetDocumentStartOffset()) + "    ";
        c.Insert(brace.GetDocumentEndOffset(), $"\n{indent}{member}\n");
        return true;
    }

    /// <summary>Adds a base type or interface: `, T` after the existing ones, else ` : T` after the name, type parameters and primary constructor.</summary>
    public static void AddBaseType(FixContext c, IClassLikeDeclaration type, string baseType)
    {
        if (type.ExtendsList is { } list && list.GetText().Trim().Length > 1)
        {
            c.Insert(list.GetDocumentEndOffset(), ", " + baseType);
            return;
        }
        var name = type.GetNameDocumentRange();
        if (!name.IsValid()) return;
        var text = name.Document.GetText();
        var offset = name.EndOffset.Offset;
        var next = offset;
        while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
        if (next < text.Length && text[next] == '<')
            for (var depth = 0; next < text.Length; next++)
            {
                if (text[next] == '<') depth++;
                else if (text[next] == '>' && --depth == 0) { offset = next + 1; break; }
            }
        if (type.PrimaryConstructorDeclaration is { } primary) offset = Math.Max(offset, primary.GetDocumentEndOffset().Offset);
        c.Insert(new DocumentOffset(name.Document, offset), " : " + baseType);
    }

    /// <summary>Deletes a range and the spaces after it.</summary>
    public static void DeleteWithSpaces(FixContext c, DocumentRange range)
    {
        var text = range.Document.GetText();
        var end = range.EndOffset.Offset;
        while (end < text.Length && text[end] == ' ') end++;
        c.Delete(new DocumentRange(range.Document, new JetBrains.Util.TextRange(range.StartOffset.Offset, end)));
    }

    /// <summary>The type a type reference at the diagnostic resolves to.</summary>
    public static ITypeElement? ReferencedType(FixContext c) =>
        c.At<IReferenceName>()?.Reference.Resolve().DeclaredElement as ITypeElement;

    public static ITypeElement? TypeElementOf(IType? type) => (type as IDeclaredType)?.GetTypeElement();

    public static IClassLikeDeclaration? DeclarationOf(ITypeElement? type) =>
        type?.GetDeclarations().OfType<IClassLikeDeclaration>().FirstOrDefault();

    public static ITypeElement? BaseClass(ITypeElement type) =>
        type.GetSuperTypes().Select(t => t.GetTypeElement()).OfType<IClass>().FirstOrDefault();

    /// <summary>Whether <paramref name="type"/> derives from or implements the type named <paramref name="clrName"/>.</summary>
    public static bool Inherits(ITypeElement type, string clrName)
    {
        var seen = new HashSet<ITypeElement>();
        var pending = new Stack<ITypeElement>([type]);
        while (pending.Count > 0)
            foreach (var super in pending.Pop().GetSuperTypes().Select(t => t.GetTypeElement()))
            {
                if (super == null || !seen.Add(super)) continue;
                if (super.GetClrName().FullName == clrName) return true;
                pending.Push(super);
            }
        return false;
    }

    /// <summary>"Mark 'T' [RunTimeOrCompileTime]" when T is in source without a scope attribute.</summary>
    public static Fix? MarkRunTimeOrCompileTime(ITypeElement? type)
    {
        if (DeclarationOf(type) is not { } declaration || ScopeAttribute(declaration) != null) return null;
        return new Fix($"Mark '{type!.ShortName}' [RunTimeOrCompileTime]", ctx =>
        {
            FixOps.AddAttribute(ctx, declaration, "RunTimeOrCompileTime");
            AddUsingIn(ctx, declaration, Aspects);
        });
    }

    /// <summary>The C# files of a project.</summary>
    public static IEnumerable<ICSharpFile> CSharpFiles(IProject project) =>
        project.GetAllProjectFiles(f => f.Location.ExtensionNoDot == "cs").Select(f => f.ToSourceFile()).OfType<IPsiSourceFile>()
            .Select(f => f.GetDominantPsiFile<CSharpLanguage>()).OfType<ICSharpFile>();

    /// <summary>An assembly attribute after the top-level usings of <paramref name="file"/> (or at its start), with `using Metalama.Framework.Aspects`.</summary>
    public static void InsertAssemblyAttribute(FixContext c, ICSharpFile file, string attribute)
    {
        var last = file.Descendants<IUsingDirective>().ToEnumerable().LastOrDefault(u => u.GetContainingNode<ICSharpNamespaceDeclaration>() == null);
        if (last != null) c.Insert(last.GetDocumentEndOffset(), "\n\n" + attribute);
        else c.Insert(new DocumentOffset(file.GetDocumentRange().Document, 0), attribute + "\n\n");
        // Recorded after the attribute: at the same offset, it lands before it.
        AddUsingIn(c, file, Aspects);
    }

    /// <summary>The full name of an aspect class named in a message ("Log", "LogAttribute", "Ns.LogAttribute"), as known to the worker.</summary>
    public static string AspectFullName(FixContext c, string name) =>
        c.Model?.AspectClasses.FirstOrDefault(a => a.Id == "T:" + name || a.Name == name || IsAspectNamed(a.Id.Substring(2), name))?.Id.Substring(2) ?? name;

    public static bool IsIn(ITreeNode node, ITreeNode ancestor)
    {
        for (var n = node; n != null; n = n.Parent)
            if (n == ancestor) return true;
        return false;
    }

    /// <summary>Removes `using <paramref name="ns"/>;` when nothing outside <paramref name="removed"/> uses the namespace any more.</summary>
    public static void RemoveUsingIfUnused(FixContext c, IFile? file, string ns, ITreeNode removed)
    {
        if (file is not ICSharpFile cs) return;
        if (cs.Descendants<IUsingSymbolDirective>().ToEnumerable().FirstOrDefault(u => u.ImportedSymbolName?.QualifiedName == ns) is not { } directive) return;
        var references = cs.Descendants<IReferenceName>().ToEnumerable().Select(r => (Node: (ITreeNode)r, Element: r.Reference.Resolve().DeclaredElement))
            .Concat(cs.Descendants<IReferenceExpression>().ToEnumerable().Select(r => (Node: (ITreeNode)r, Element: r.Reference.Resolve().DeclaredElement)));
        if (references.Any(r => !IsIn(r.Node, removed) && !IsIn(r.Node, directive) && NamespaceOf(r.Element) == ns)) return;
        FixOps.DeleteWithLine(c, directive);
    }

    static string? NamespaceOf(IDeclaredElement? element) => element switch
    {
        ITypeElement type => type.GetContainingNamespace().QualifiedName,
        ITypeMember member => member.GetContainingType()?.GetContainingNamespace().QualifiedName,
        _ => null,
    };

    /// <summary>The method a call invokes.</summary>
    public static IMethod? InvokedMethod(IInvocationExpression invocation) =>
        (invocation.InvokedExpression as IReferenceExpression)?.Reference.Resolve().DeclaredElement as IMethod;

    /// <summary>Appends an argument before the closing parenthesis of a call.</summary>
    public static void AppendArgument(FixContext c, IInvocationExpression invocation, string argument)
    {
        if (invocation.RPar is not { } rpar) return;
        c.Insert(rpar.GetDocumentStartOffset(), invocation.Arguments.Count == 0 ? argument : ", " + argument);
    }
}

/// <summary>LAMA0101: a LINQ query in a template, rewritten as method calls (only from/where/select queries).</summary>
sealed class LinqQueryFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0101"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if (c.At<IQueryExpression>() is not { From: { Expression: { } source, TypeUsage: null } from } query || query.Continuations.Count > 0) yield break;
        var clauses = query.Descendants<IQueryClause>().ToEnumerable().Where(q => q.GetContainingNode<IQueryExpression>() == query).ToList();
        if (clauses.Count == 0 || clauses.Any(q => q is not (IQueryWhereClause or IQuerySelectClause)) || clauses.OfType<IQuerySelectClause>().Count() != 1
            || clauses[clauses.Count - 1] is not IQuerySelectClause { Expression.Value: { } selected }) yield break;
        var wheres = clauses.OfType<IQueryWhereClause>().Select(w => w.Expression?.Value?.GetText()).ToList();
        if (wheres.Any(w => w == null)) yield break;
        var sourceText = source is IPrimaryExpression ? source.GetText() : $"({source.GetText()})";
        var text = QueryToMethods(sourceText, from.Name, wheres!, selected.GetText());
        yield return new Fix("Use LINQ method calls instead of the query", ctx =>
        {
            ctx.Replace(query, text);
            FixOps.AddUsing(ctx, "System.Linq");
        });
    }
}

/// <summary>LAMA0233: a template-only meta API in compile-time code, replaced with its ExpressionFactory equivalent.</summary>
sealed class TemplateOnlyApiFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0233"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        IReferenceExpression? meta = null;
        for (var n = c.Node; n != null && meta == null; n = n.Parent)
            if (n is IReferenceExpression { QualifierExpression: IReferenceExpression { NameIdentifier.Name: "meta" } } r) meta = r;
        if (meta?.NameIdentifier?.Name is not { } member) yield break;
        var suggested = Regex.Match(c.Diagnostic.Message, $@"Use (\S+?) instead of meta\.{member}\b");
        var replacement = suggested.Success ? suggested.Groups[1].Value : member == "This" ? "ExpressionFactory.This()" : null;
        if (replacement == null) yield break;
        yield return new Fix($"Use '{replacement}'", ctx =>
        {
            ctx.Replace(meta, replacement);
            if (replacement.StartsWith("ExpressionFactory.", StringComparison.Ordinal)) FixOps.AddUsing(ctx, "Metalama.Framework.Code.SyntaxBuilders");
        });
    }
}

/// <summary>LAMA0236: compile-time code references a run-time-only type: introduce the member, or make the type run-time-or-compile-time.</summary>
sealed class RunTimeReferenceFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0236"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        var member = (ICSharpDeclaration?)c.At<IMultipleFieldDeclaration>()?.Descendants<IFieldDeclaration>().ToEnumerable().FirstOrDefault()
                     ?? c.At<IPropertyDeclaration>();
        if (member is IAttributesOwnerDeclaration owner && FixOps.FindAttribute(owner, "Introduce") == null
            && member.GetContainingNode<IClassLikeDeclaration>() is IClassDeclaration)
            yield return new Fix($"Introduce '{member.DeclaredName}' into the target type ([Introduce])", ctx =>
            {
                CompileTimeOps.AddAttributeLine(ctx, member, "Introduce");
                FixOps.AddUsing(ctx, CompileTimeOps.Aspects);
            });
        if (CompileTimeOps.MarkRunTimeOrCompileTime(CompileTimeOps.ReferencedType(c)) is { } mark) yield return mark;
    }
}

/// <summary>LAMA0237: an abstract template property with a run-time-only type: make it virtual with a default implementation.</summary>
sealed class AbstractRunTimeTemplateFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0237"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if ((c.At<IAccessorOwnerDeclaration>() ?? c.Target as IAccessorOwnerDeclaration) is not { } property) yield break;
        if (FixOps.Modifiers(property).FirstOrDefault(m => m.GetText() == "abstract") is { } abstractToken)
            yield return new Fix($"Make '{property.DeclaredName}' virtual with a default implementation", ctx =>
            {
                ctx.Replace(abstractToken, "virtual");
                // ponytail: the default throws; the user writes the real default (e.g. `new SystemClock()`).
                foreach (var accessor in property.AccessorDeclarations)
                    if (accessor is { Body: null, ArrowClause: null, Semicolon: { } semicolon })
                        ctx.Replace(semicolon, " => throw new System.NotImplementedException();");
            });
        if (CompileTimeOps.MarkRunTimeOrCompileTime(CompileTimeOps.TypeElementOf((property.DeclaredElement as ITypeOwner)?.Type)) is { } mark) yield return mark;
    }
}

/// <summary>LAMA0238: `dynamic` outside a template: give the field a concrete type, or introduce it (it becomes a template).</summary>
sealed class DynamicOutsideTemplateFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0238"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        var member = (ICSharpDeclaration?)c.At<IFieldDeclaration>() ?? c.At<IPropertyDeclaration>() ?? c.Target as ICSharpDeclaration;
        if (member is not (IFieldDeclaration or IPropertyDeclaration)) yield break;
        var scope = member is IFieldDeclaration field ? field.GetContainingNode<IMultipleFieldDeclaration>() ?? (ITreeNode)member : member;
        var name = member.GetNameDocumentRange();
        var dynamicType = scope.Descendants<ITypeUsage>().ToEnumerable()
            .FirstOrDefault(t => t.GetText().TrimEnd('?') == "dynamic" && t.GetDocumentStartOffset().Offset < name.StartOffset.Offset);
        var value = member switch
        {
            IFieldDeclaration f => (f.Initial as IExpressionInitializer)?.Value,
            IPropertyDeclaration p => (p.Initial as IExpressionInitializer)?.Value ?? p.ArrowClause?.Expression,
            _ => null,
        };
        var type = value?.GetExpressionType().ToIType();
        var typeName = type == null || type.IsUnknown ? null : type.GetPresentableName(CSharpLanguage.Instance);
        if (dynamicType != null && typeName != null && typeName.TrimEnd('?') != "dynamic")
        {
            yield return new Fix($"Change the type of '{member.DeclaredName}' to '{typeName}'", ctx => ctx.Replace(dynamicType, typeName));
        }
        if (member is IAttributesOwnerDeclaration owner && FixOps.FindAttribute(owner, "Introduce") == null)
            yield return new Fix($"Introduce '{member.DeclaredName}' into the target type ([Introduce])", ctx =>
            {
                CompileTimeOps.AddAttributeLine(ctx, member, "Introduce");
                FixOps.AddUsing(ctx, CompileTimeOps.Aspects);
            });
    }
}

/// <summary>LAMA0244: a type and its base type or interface have incompatible scopes.</summary>
sealed class BaseTypeScopeFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0244"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        var baseType = CompileTimeOps.ReferencedType(c);
        if (CompileTimeOps.MarkRunTimeOrCompileTime(baseType) is { } mark) yield return mark;
        if (c.At<IExtendsList>() is not { } list || c.At<IClassLikeDeclaration>() is not { } type) yield break;
        var item = list.Children().FirstOrDefault(n => n is ITypeUsage && n.GetDocumentRange().Contains(c.Range));
        if (item == null) yield break;
        yield return new Fix($"Remove '{item.GetText()}' from the base types of '{type.DeclaredName}'", ctx =>
        {
            if (list.Children().OfType<ITypeUsage>().Count() > 1)
            {
                FixOps.DeleteListItem(ctx, item);
                return;
            }
            // The only base type: remove ` : Base` too.
            var range = list.GetDocumentRange();
            var text = range.Document.GetText();
            var start = range.StartOffset.Offset;
            while (start > 0 && text[start - 1] == ' ') start--;
            ctx.Delete(new DocumentRange(range.Document, new JetBrains.Util.TextRange(start, range.EndOffset.Offset)));
        });
    }
}

/// <summary>LAMA0292: a compile-time member in a run-time type: move the scope attribute to the type.</summary>
sealed class MemberScopeFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0292"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if ((c.At<IAttributesOwnerDeclaration>() ?? c.Target as IAttributesOwnerDeclaration) is not { } member
            || member.GetContainingNode<IClassLikeDeclaration>() is not { } type || CompileTimeOps.ScopeAttribute(type) != null) yield break;
        if (CompileTimeOps.ScopeAttribute(member) is { } attribute)
        {
            var name = FixOps.ShortName(attribute);
            yield return new Fix($"Move [{name}] from '{member.DeclaredName}' to '{type.DeclaredName}'", ctx =>
            {
                FixOps.RemoveAttribute(ctx, attribute);
                FixOps.AddAttribute(ctx, type, name);
            });
        }
        else
            yield return new Fix($"Mark '{type.DeclaredName}' [CompileTime]", ctx =>
            {
                FixOps.AddAttribute(ctx, type, "CompileTime");
                FixOps.AddUsing(ctx, CompileTimeOps.Aspects);
            });
    }
}

/// <summary>LAMA0264: a compile-time template parameter with a run-time-only type: make it a run-time parameter.</summary>
sealed class CompileTimeParameterFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0264"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if (c.At<ICSharpParameterDeclaration>() is not { } parameter || FixOps.FindAttribute(parameter, "CompileTime") is not { } attribute) yield break;
        yield return new Fix($"Remove [CompileTime] from '{parameter.DeclaredName}'", ctx => FixOps.RemoveAttribute(ctx, attribute));
    }
}

/// <summary>LAMA0274: a template outside an aspect, fabric or template provider: make the type an ITemplateProvider.</summary>
sealed class TemplateProviderFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0274"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        var node = (ITreeNode?)c.Target ?? c.Node;
        if ((node as IClassDeclaration ?? node?.GetContainingNode<IClassDeclaration>()) is not { } type) yield break;
        yield return new Fix($"Make '{type.DeclaredName}' implement ITemplateProvider", ctx =>
        {
            var isStatic = FixOps.HasModifier(type, "static");
            // ITemplateProvider types are run-time-or-compile-time already.
            if (FixOps.FindAttribute(type, "RunTimeOrCompileTime") is { } scope) FixOps.RemoveAttribute(ctx, scope);
            if (isStatic) FixOps.RemoveModifier(ctx, type, "static");
            CompileTimeOps.AddBaseType(ctx, type, "ITemplateProvider");
            // A static class can't be instantiated; the private constructor keeps it that way.
            if (isStatic && type.ConstructorDeclarations.Count == 0) CompileTimeOps.InsertMember(ctx, type, $"private {type.DeclaredName}() {{ }}\n");
            CompileTimeOps.AddUsingIn(ctx, type, CompileTimeOps.Aspects);
        });
    }
}

/// <summary>LAMA0275: a call to a virtual template omits optional arguments: pass them.</summary>
sealed class VirtualTemplateArgumentsFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0275"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if (c.At<IInvocationExpression>() is not { } call || CompileTimeOps.InvokedMethod(call) is not { } method) yield break;
        var arguments = call.Arguments.OfType<ICSharpArgument>().ToList();
        var positional = arguments.Count(a => a.NameIdentifier == null);
        var named = arguments.Where(a => a.NameIdentifier != null).Select(a => a.NameIdentifier!.Name).ToHashSet();
        var missing = new List<string>();
        for (var i = positional; i < method.Parameters.Count; i++)
        {
            var parameter = method.Parameters[i];
            if (named.Contains(parameter.ShortName) || !parameter.IsOptional) continue;
            if (parameter.GetDeclarations().OfType<ICSharpParameterDeclaration>().FirstOrDefault()?.DefaultValue?.GetText() is not { } value) yield break;
            missing.Add(named.Count > 0 ? $"{parameter.ShortName}: {value}" : value);
        }
        if (missing.Count == 0) yield break;
        yield return new Fix($"Pass all arguments to '{method.ShortName}'", ctx => CompileTimeOps.AppendArgument(ctx, call, string.Join(", ", missing)));
    }
}

/// <summary>LAMA0279: an abstract or empty template is called: give it a default implementation.</summary>
sealed class AbstractTemplateCallFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0279"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if (c.At<IInvocationExpression>() is not { } call || CompileTimeOps.InvokedMethod(call) is not { } method
            || method.GetDeclarations().OfType<IMethodDeclaration>().FirstOrDefault() is not { } declaration) yield break;
        if (FixOps.Modifiers(declaration).FirstOrDefault(m => m.GetText() == "abstract") is { } abstractToken && declaration.Semicolon is { } semicolon)
        {
            var isVoid = declaration.TypeUsage?.GetText() == "void";
            yield return new Fix($"Make '{method.ShortName}' virtual with an empty default implementation", ctx =>
            {
                ctx.Replace(abstractToken, "virtual");
                ctx.Replace(semicolon, isVoid ? " { }" : " => default!;");
            });
        }
        if (FixOps.FindAttribute(declaration, "Template") is { } template && template.PropertyAssignments.Any(p => p.PropertyNameIdentifier?.Name == "IsEmpty"))
            yield return new Fix($"Remove 'IsEmpty' from [Template] of '{method.ShortName}'", ctx => FixOps.RemoveAttributeProperty(ctx, template, "IsEmpty"));
    }
}

/// <summary>LAMA0254: ConfigureAwait after ProceedAsync needs a literal: a compile-time if per value, or a literal.</summary>
sealed class ConfigureAwaitLiteralFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0254"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        var call = c.At<IInvocationExpression>();
        while (call != null && FixOps.MethodName(call) != "ConfigureAwait") call = call.GetContainingNode<IInvocationExpression>();
        if (call?.Arguments.OfType<ICSharpArgument>().FirstOrDefault()?.Value is not { } value) yield break;

        var statement = call.GetContainingNode<ICSharpStatement>();
        while (statement != null && statement.Parent is not IBlock) statement = statement.GetContainingNode<ICSharpStatement>();
        if (statement?.Parent is IBlock block)
        {
            // A declared variable is used by the next statements: they go into both branches.
            var last = statement is IDeclarationStatement ? block.Statements.Last() : statement;
            var start = statement.GetDocumentStartOffset();
            var document = start.Document;
            var code = document.GetText(new JetBrains.Util.TextRange(start.Offset, last.GetDocumentEndOffset().Offset));
            var argStart = value.GetDocumentStartOffset().Offset - start.Offset;
            var text = ConfigureAwaitBranches(code, argStart, argStart + value.GetTextLength(), value.GetText(), FixOps.IndentAt(start));
            yield return new Fix($"Choose the literal with a compile-time 'if ( {value.GetText()} )'", ctx =>
                ctx.Replace(new DocumentRange(document, new JetBrains.Util.TextRange(start.Offset, last.GetDocumentEndOffset().Offset)), text));
        }
        yield return new Fix("Use 'ConfigureAwait( true )'", ctx => ctx.Replace(value, "true"));
        yield return new Fix("Use 'ConfigureAwait( false )'", ctx => ctx.Replace(value, "false"));
    }
}

/// <summary>LAMA0252: a partial template: merge its declaration and implementation into one method.</summary>
sealed class PartialTemplateFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0252"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if ((c.At<IMethodDeclaration>() ?? c.Target as IMethodDeclaration) is not { } method
            || method.GetContainingNode<IClassLikeDeclaration>()?.DeclaredElement is not { } type) yield break;
        var parts = type.GetDeclarations().OfType<IClassLikeDeclaration>().ToList();
        var methods = parts.SelectMany(p => p.MethodDeclarations)
            .Where(m => m.DeclaredName == method.DeclaredName && FixOps.HasModifier(m, "partial") && m.Params?.ParameterDeclarations.Count == method.Params?.ParameterDeclarations.Count).ToList();
        var definition = methods.FirstOrDefault(m => m is { Body: null, ArrowClause: null });
        var implementation = methods.FirstOrDefault(m => m != definition && (m.Body != null || m.ArrowClause != null));
        if (definition?.Semicolon is not { } semicolon || implementation == null) yield break;
        var body = implementation.Body?.GetText() ?? implementation.ArrowClause!.GetText() + ";";
        yield return new Fix($"Merge the partial template '{method.DeclaredName}' into one method", ctx =>
        {
            FixOps.RemoveModifier(ctx, definition, "partial");
            ctx.Replace(semicolon, " " + body);
            var implementationPart = implementation.GetContainingNode<IClassLikeDeclaration>();
            var definitionPart = definition.GetContainingNode<IClassLikeDeclaration>();
            if (implementationPart != null && implementationPart != definitionPart && implementationPart.ClassMemberDeclarations.Count == 1)
            {
                // The part held only the implementation: remove it, and `partial` when one part remains.
                FixOps.DeleteWithLine(ctx, implementationPart);
                if (parts.Count == 2 && definitionPart != null) FixOps.RemoveModifier(ctx, definitionPart, "partial");
            }
            else FixOps.DeleteWithLine(ctx, implementation);
        });
    }
}

/// <summary>LAMA0248, LAMA0249: compile-time code or a template can't be unsafe.</summary>
sealed class UnsafeCompileTimeFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0248", "LAMA0249"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        var declaration = c.At<ICSharpModifiersOwnerDeclaration>();
        if (declaration == null || !FixOps.HasModifier(declaration, "unsafe")) declaration = c.Target as ICSharpModifiersOwnerDeclaration;
        if (declaration == null || !FixOps.HasModifier(declaration, "unsafe")) yield break;
        yield return new Fix($"Remove 'unsafe' from '{declaration.DeclaredName}'", ctx => FixOps.RemoveModifier(ctx, declaration, "unsafe"));
    }
}

/// <summary>LAMA0294, LAMA0281: an attribute or C# feature compile-time code can't use (inline arrays).</summary>
sealed class CompileTimeFeatureFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0294", "LAMA0281"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if ((c.Target as IAttributesOwnerDeclaration ?? c.At<IAttributesOwnerDeclaration>()) is not { } declaration) yield break;
        var name = declaration.DeclaredName;

        if (c.Diagnostic.Id == "LAMA0281")
        {
            var named = Quoted(c.Diagnostic.Message).FirstOrDefault()?.Split('.').Last();
            var attribute = c.At<IAttribute>() ?? (named != null ? FixOps.FindAttribute(declaration, named) : null);
            if (attribute != null)
                yield return new Fix($"Remove [{FixOps.ShortName(attribute)}] from '{name}'", ctx => FixOps.RemoveAttribute(ctx, attribute));
        }

        if (declaration is IStructDeclaration { DeclaredElement: { } structType } inlineArray && FixOps.FindAttribute(inlineArray, "InlineArray") is { } inline
            && inline.ConstructorArgumentExpressions.FirstOrDefault()?.GetText() is { } length
            && inlineArray.FieldDeclarations.FirstOrDefault()?.DeclaredElement?.Type is { } elementType)
        {
            var element = elementType.GetPresentableName(CSharpLanguage.Instance);
            yield return new Fix($"Replace '{name}' with an array 'new {element}[{length}]'", ctx =>
            {
                foreach (var reference in inlineArray.GetPsiServices().Finder.FindAllReferences(structType))
                {
                    var node = reference.GetTreeNode();
                    if (CompileTimeOps.IsIn(node, inlineArray)) continue;
                    if (node.GetContainingNode<IObjectCreationExpression>() is { Initializer: null } creation && creation.TypeName == node && creation.Arguments.Count == 0)
                        ctx.Replace(creation, $"new {element}[{length}]");
                    else if (node.GetContainingFile()?.GetDocumentRange(reference.GetTreeTextRange()) is { } range && range.IsValid())
                        ctx.Replace(range, element + "[]");
                }
                FixOps.DeleteWithLine(ctx, inlineArray);
                CompileTimeOps.RemoveUsingIfUnused(ctx, inlineArray.GetContainingFile(), "System.Runtime.CompilerServices", inlineArray);
            });
        }

        if (CompileTimeOps.ScopeAttribute(declaration) is { } scope)
            yield return new Fix($"Remove [{FixOps.ShortName(scope)}] from '{name}' (keep it run-time)", ctx => FixOps.RemoveAttribute(ctx, scope));
    }
}

/// <summary>LAMA0260: a type fabric nested in a compile-time type: make the type run-time, or remove the fabric.</summary>
sealed class NestedFabricFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0260"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if ((c.Target as IClassLikeDeclaration ?? c.At<IClassLikeDeclaration>()) is not { } fabric) yield break;
        for (var type = fabric.GetContainingNode<IClassLikeDeclaration>(); type != null; type = type.GetContainingNode<IClassLikeDeclaration>())
            if (CompileTimeOps.ScopeAttribute(type) is { } scope)
            {
                yield return new Fix($"Remove [{FixOps.ShortName(scope)}] from '{type.DeclaredName}'", ctx => FixOps.RemoveAttribute(ctx, scope));
                break;
            }
        yield return new Fix($"Remove the type fabric '{fabric.DeclaredName}'", ctx => FixOps.DeleteWithLine(ctx, fabric));
    }
}

/// <summary>LAMA0060: a template returns a reference: return the value.</summary>
sealed class RefTemplateFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0060"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if ((c.At<ICSharpTypeMemberDeclaration>() ?? c.Target as ICSharpTypeMemberDeclaration) is not { } member) yield break;
        var nameStart = member.GetNameDocumentRange().StartOffset.Offset;
        var refs = new List<DocumentRange>();
        foreach (var token in member.Descendants<ITokenNode>().ToEnumerable().Where(t => t.GetText() == "ref"))
        {
            var range = token.GetDocumentRange();
            var before = range.Document.GetText(new JetBrains.Util.TextRange(0, range.StartOffset.Offset)).TrimEnd();
            var returnType = range.StartOffset.Offset < nameStart && token.GetContainingNode<IFormalParameterList>() == null;
            if (!returnType && !before.EndsWith("=>", StringComparison.Ordinal) && !Regex.IsMatch(before, @"\breturn$")) continue;
            refs.Add(range);
            // `ref readonly T`: readonly goes too.
            if (returnType && token.GetNextToken() is { } next && (next is IWhitespaceNode ? next.GetNextToken() : next) is { } after && after.GetText() == "readonly")
                refs.Add(after.GetDocumentRange());
        }
        if (refs.Count == 0) yield break;
        yield return new Fix($"Return '{member.DeclaredName}' by value", ctx =>
        {
            foreach (var range in refs) CompileTimeOps.DeleteWithSpaces(ctx, range);
        });
    }
}

/// <summary>LAMA0040: a fabric needs a public parameterless constructor.</summary>
sealed class FabricConstructorFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0040"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if (Quoted(c.Diagnostic.Message).FirstOrDefault() is not { } name || c.File == null) yield break;
        var type = (c.Target as IClassLikeDeclaration is { } target && target.DeclaredName == name ? target : null)
                   ?? c.File.Descendants<IClassLikeDeclaration>().ToEnumerable().FirstOrDefault(t => t.DeclaredName == name)
                   ?? Fabrics(c, name).FirstOrDefault();
        if (type == null) yield break;
        foreach (var fix in ParameterlessConstructor.Fixes(type)) yield return fix;
    }

    /// <summary>Fabric types of that short name in the solution (the diagnostic may have no location).</summary>
    static IEnumerable<IClassLikeDeclaration> Fabrics(FixContext c, string name)
    {
        if (c.File?.GetPsiModule() is not { } module) return [];
        var scope = module.GetPsiServices().Symbols.GetSymbolScope(module, withReferences: true, caseSensitive: true);
        return scope.GetElementsByShortName(name).OfType<ITypeElement>()
            .Where(t => CompileTimeOps.Inherits(t, "Metalama.Framework.Fabrics.Fabric"))
            .Select(CompileTimeOps.DeclarationOf).OfType<IClassLikeDeclaration>();
    }
}

static class ParameterlessConstructor
{
    /// <summary>Make the parameterless constructor public, or add one.</summary>
    public static IEnumerable<Fix> Fixes(IClassLikeDeclaration type)
    {
        var name = type.DeclaredName;
        var parameterless = type.ConstructorDeclarations.FirstOrDefault(d => d.Params?.ParameterDeclarations.Count == 0 && !FixOps.HasModifier(d, "static"));
        if (parameterless != null)
        {
            if (!FixOps.HasModifier(parameterless, "public"))
                yield return new Fix($"Make the constructor of '{name}' public", ctx => FixOps.SetAccessibility(ctx, parameterless, "public"));
            yield break;
        }
        yield return new Fix($"Add a public parameterless constructor to '{name}'", ctx =>
        {
            if (type.ConstructorDeclarations.FirstOrDefault() is { } first) FixOps.InsertLineAbove(ctx, first, $"public {name}() {{ }}\n");
            else CompileTimeOps.InsertMember(ctx, type, $"public {name}() {{ }}");
        });
    }
}

/// <summary>LAMA0045: a live template aspect needs a parameterless constructor.</summary>
sealed class LiveTemplateConstructorFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0045"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if ((c.Target as IClassLikeDeclaration ?? c.At<IClassLikeDeclaration>()) is not { } type) yield break;
        foreach (var fix in ParameterlessConstructor.Fixes(type)) yield return fix;
        if (FixOps.FindAttribute(type, "EditorExperience") is { } attribute && attribute.PropertyAssignments.Any(p => p.PropertyNameIdentifier?.Name == "SuggestAsLiveTemplate"))
            yield return new Fix("Remove 'SuggestAsLiveTemplate = true'", ctx =>
            {
                if (attribute.PropertyAssignments.Count == 1 && attribute.ConstructorArgumentExpressions.Count == 0) FixOps.RemoveAttribute(ctx, attribute);
                else FixOps.RemoveAttributeProperty(ctx, attribute, "SuggestAsLiveTemplate");
            });
    }
}

/// <summary>LAMA0020: TypeFactory.GetType with a dotted nested type name: use the reflection name (Outer+Nested).</summary>
sealed class ReflectionTypeNameFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0020"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if (Quoted(c.Diagnostic.Message).FirstOrDefault() is not { } name || c.File?.GetPsiModule() is not { } module
            || AspectTargets.AspectClass(name, module)?.GetClrName().FullName is not { } reflectionName || reflectionName == name) yield break;
        // The string is in compile-time code: the aspect classes' files, or this one.
        var files = new HashSet<IFile> { c.File };
        foreach (var aspect in c.Model?.AspectClasses.Where(a => !a.IsLibrary) ?? [])
            foreach (var declaration in FixOps.TypeOf(c, aspect.Id)?.GetDeclarations() ?? [])
                if (declaration.GetContainingFile() is { } file) files.Add(file);
        var literals = files.SelectMany(f => f.Descendants<ICSharpLiteralExpression>().ToEnumerable()).Where(l => l.GetText() == $"\"{name}\"").ToList();
        if (literals.Count == 0) yield break;
        yield return new Fix($"Use the reflection name '{reflectionName}'", ctx =>
        {
            foreach (var literal in literals) ctx.Replace(literal, $"\"{reflectionName}\"");
        });
    }
}

/// <summary>LAMA0039: a child aspect added to an ineligible declaration: AddAspect → AddAspectIfEligible.</summary>
sealed class AddAspectIfEligibleFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0039"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        var scope = c.At<IInvocationExpression>() is { } call && FixOps.MethodName(call) == "AddAspect" ? call : (ITreeNode?)c.Target ?? c.At<IClassLikeDeclaration>();
        if (scope == null) yield break;
        var calls = scope.Descendants<IInvocationExpression>().ToEnumerable().Prepend(scope as IInvocationExpression).OfType<IInvocationExpression>()
            .Where(i => FixOps.MethodName(i) == "AddAspect").Distinct().ToList();
        var aspect = Regex.Match(c.Diagnostic.Message, "of type '([^']+)'").Groups[1].Value;
        var named = calls.Where(i => aspect.Length > 0 && i.GetText().Contains(aspect)).ToList();
        if (named.Count > 0) calls = named;
        if (calls.Count == 0) yield break;
        yield return new Fix("Use 'AddAspectIfEligible'", ctx =>
        {
            foreach (var i in calls) FixOps.RenameInvokedMethod(ctx, i, "AddAspectIfEligible");
        });
    }
}

/// <summary>LAMA0032: two templates with the same name: rename this one and the advice calls that pass its arguments.</summary>
sealed class DuplicateTemplateNameFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0032"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if ((c.At<IMethodDeclaration>() ?? c.Target as IMethodDeclaration) is not { NameIdentifier: { } identifier } method
            || method.GetContainingNode<IClassLikeDeclaration>()?.DeclaredElement is not { } type) yield break;
        var name = method.DeclaredName;
        var parameters = method.Params?.ParameterDeclarations.ToList() ?? [];
        var newName = TemplateName(name, parameters.Select(p => p.DeclaredName).ToList(), type.GetMembers().Select(m => m.ShortName).ToHashSet());
        // The advice calls of this template pass exactly its compile-time parameters (and type parameters) in `args`.
        var compileTime = parameters.Where(p => FixOps.FindAttribute(p, "CompileTime") != null).Select(p => p.DeclaredName)
            .Concat(method.TypeParameterDeclarations.Where(p => FixOps.FindAttribute(p, "CompileTime") != null).Select(p => p.DeclaredName)).ToHashSet();
        var nameofPattern = new Regex($@"^nameof\s*\((.*?\b){Regex.Escape(name)}\s*\)$");
        var adviceNames = type.GetDeclarations().SelectMany(d => d.Descendants<IInvocationExpression>().ToEnumerable())
            .Select(call => (Call: call, Name: call.Arguments.OfType<ICSharpArgument>().FirstOrDefault()?.Value))
            .Where(x => x.Name != null && (x.Name.GetText() == $"\"{name}\"" || nameofPattern.IsMatch(x.Name.GetText())))
            .Where(x => ArgsNames(x.Call).SetEquals(compileTime))
            .Select(x => x.Name!).ToList();
        yield return new Fix($"Rename the template to '{newName}'", ctx =>
        {
            ctx.Replace(identifier, newName);
            foreach (var argument in adviceNames)
                ctx.Replace(argument, argument.GetText().StartsWith("\"") ? $"\"{newName}\"" : nameofPattern.Replace(argument.GetText(), $"nameof($1{newName})"));
        });
    }

    /// <summary>Member names of the anonymous object passed as `args:` (none without it).</summary>
    static HashSet<string> ArgsNames(IInvocationExpression call) =>
        (call.Arguments.OfType<ICSharpArgument>().FirstOrDefault(a => a.NameIdentifier?.Name == "args")?.Value as IAnonymousObjectCreationExpression)
        ?.AnonymousInitializer?.MemberInitializers.Select(m => m.NameIdentifier?.Name ?? m.Expression?.GetText().Split('.').Last() ?? "").ToHashSet()
        ?? [];
}

/// <summary>LAMA0036: a template with the name of a base class template: override it, or rename it.</summary>
sealed class BaseTemplateNameFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0036"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if ((c.At<IMethodDeclaration>() ?? c.Target as IMethodDeclaration) is not { DeclaredElement: { } method } derived
            || method.GetContainingType() is not { } type) yield break;
        IMethod? baseMethod = null;
        for (var t = CompileTimeOps.BaseClass(type); t != null && baseMethod == null; t = CompileTimeOps.BaseClass(t))
            baseMethod = t.Methods.FirstOrDefault(m => m.ShortName == method.ShortName && m.Parameters.Count == method.Parameters.Count);
        var baseDeclaration = baseMethod?.GetDeclarations().OfType<IMethodDeclaration>().FirstOrDefault();
        if (baseMethod != null && (baseMethod.IsVirtual || baseMethod.IsAbstract || baseMethod.IsOverride || baseDeclaration != null))
            yield return new Fix($"Override the base template '{method.ShortName}'", ctx =>
            {
                if (baseDeclaration != null && !(baseMethod.IsVirtual || baseMethod.IsAbstract || baseMethod.IsOverride)) FixOps.AddModifier(ctx, baseDeclaration, "virtual");
                // The override inherits [Template] from the base method.
                if (FixOps.FindAttribute(derived, "Template") is { } template) FixOps.RemoveAttribute(ctx, template);
                if (FixOps.Modifiers(derived).FirstOrDefault(m => m.GetText() == "new") is { } newToken) ctx.Replace(newToken, "override");
                else FixOps.AddModifier(ctx, derived, "override");
            });
        var newName = TemplateName(method.ShortName, [], type.GetMembers().Select(m => m.ShortName).Concat(baseMethod?.GetContainingType()?.GetMembers().Select(m => m.ShortName) ?? []).ToHashSet());
        yield return new Fix($"Rename the template to '{newName}'", ctx => FixOps.Rename(ctx, derived, newName));
    }
}

/// <summary>LAMA0022: a child aspect ordered before its parent: swap them in [assembly: AspectOrder], or add one.</summary>
sealed class ChildAspectOrderFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0022"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if (ParentAndChild(c.Diagnostic.Message) is not { } names) yield break;
        var (parent, child) = names;
        var parentName = c.Diagnostic.Target is { } target && target.StartsWith("T:") ? target.Substring(2) : CompileTimeOps.AspectFullName(c, parent);
        var childName = CompileTimeOps.AspectFullName(c, child);

        var projects = new HashSet<IProject>();
        if (c.Project != null) projects.Add(c.Project);
        foreach (var id in new[] { parentName, childName })
            foreach (var declaration in FixOps.TypeOf(c, "T:" + id)?.GetDeclarations() ?? [])
                if (declaration.GetSourceFile()?.GetProject() is { } project) projects.Add(project);
        IEnumerable<ICSharpFile> files = c.File != null ? [c.File] : [];
        files = files.Concat(projects.SelectMany(CompileTimeOps.CSharpFiles)).Distinct();

        foreach (var attribute in files.SelectMany(f => f.Descendants<IAttribute>().ToEnumerable()).Where(a => FixOps.ShortName(a) == "AspectOrder"))
        {
            var types = attribute.ConstructorArgumentExpressions.OfType<ITypeofExpression>().ToList();
            var parentType = types.FirstOrDefault(t => t.TypeName != null && IsAspectNamed(t.TypeName.GetText(), parent));
            var childType = types.FirstOrDefault(t => t.TypeName != null && IsAspectNamed(t.TypeName.GetText(), child));
            if (parentType?.TypeName is not { } parentUsage || childType?.TypeName is not { } childUsage || parentType == childType) continue;
            yield return new Fix($"Swap '{child}' and '{parent}' in [AspectOrder]", ctx =>
            {
                ctx.Replace(parentUsage, childUsage.GetText());
                ctx.Replace(childUsage, parentUsage.GetText());
            });
            yield break;
        }

        if (c.File != null && !parentName.Contains('`') && !childName.Contains('`'))
            yield return new Fix($"Add [assembly: AspectOrder] processing '{parent}' before '{child}'",
                ctx => CompileTimeOps.InsertAssemblyAttribute(ctx, c.File, AspectOrder(parentName, childName)));
    }
}

/// <summary>LAMA0042: a low-level aspect between two layers of another aspect: order it before or after all of them.</summary>
sealed class WeaverAspectOrderFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0042"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if (c.File == null || LayeredAndWeaver(c.Diagnostic.Message) is not { } names) yield break;
        var (aspect, weaver) = names;
        var aspectName = CompileTimeOps.AspectFullName(c, aspect);
        var weaverName = CompileTimeOps.AspectFullName(c, weaver);
        yield return new Fix($"Order '{weaver}' before all layers of '{aspect}' ([assembly: AspectOrder])",
            ctx => CompileTimeOps.InsertAssemblyAttribute(ctx, c.File, AspectOrder(weaverName, aspectName)));
        yield return new Fix($"Order '{weaver}' after all layers of '{aspect}' ([assembly: AspectOrder])",
            ctx => CompileTimeOps.InsertAssemblyAttribute(ctx, c.File, AspectOrder(aspectName, weaverName)));
    }
}

/// <summary>LAMA0205: the base type of a serializable type is neither serializable nor default-constructible.</summary>
sealed class SerializableBaseFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0205"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if ((c.Target as IClassLikeDeclaration ?? c.At<IClassLikeDeclaration>())?.DeclaredElement is not { } type
            || CompileTimeOps.BaseClass(type) is not { } baseType || CompileTimeOps.DeclarationOf(baseType) is not { } declaration) yield break;
        yield return new Fix($"Make '{baseType.ShortName}' implement ICompileTimeSerializable", ctx =>
        {
            CompileTimeOps.AddBaseType(ctx, declaration, "ICompileTimeSerializable");
            CompileTimeOps.AddUsingIn(ctx, declaration, CompileTimeOps.Serialization);
        });
        yield return new Fix($"Add a protected parameterless constructor to '{baseType.ShortName}'",
            ctx => CompileTimeOps.InsertMember(ctx, declaration, $"protected {baseType.ShortName}() {{ }}\n"));
    }
}

/// <summary>LAMA0207: the serializer of the base type has no accessible parameterless constructor.</summary>
sealed class BaseSerializerConstructorFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0207"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if ((c.Target as IClassLikeDeclaration ?? c.At<IClassLikeDeclaration>())?.DeclaredElement is not { } type
            || CompileTimeOps.BaseClass(type) is not { } baseType) yield break;
        foreach (var serializer in baseType.NestedTypes.Where(t => CompileTimeOps.Inherits(t, "Metalama.Framework.Serialization.ISerializer")))
            if (CompileTimeOps.DeclarationOf(serializer) is { } declaration)
                // ponytail: an empty constructor; the user decides what the other constructor's arguments default to.
                yield return new Fix($"Add a public parameterless constructor to '{baseType.ShortName}.{serializer.ShortName}'",
                    ctx => CompileTimeOps.InsertMember(ctx, declaration, $"public {serializer.ShortName}() {{ }}\n"));
    }
}

/// <summary>LAMA0208: several nested serializers: remove one.</summary>
sealed class DuplicateSerializerFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0208"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if ((c.Target as IClassLikeDeclaration ?? c.At<IClassLikeDeclaration>()) is not { } type) yield break;
        var serializers = type.NestedTypeDeclarations.OfType<IClassLikeDeclaration>()
            .Where(d => d.DeclaredElement is { } e && CompileTimeOps.Inherits(e, "Metalama.Framework.Serialization.ISerializer")).ToList();
        if (serializers.Count < 2) yield break;
        foreach (var serializer in serializers)
            yield return new Fix($"Remove the serializer '{serializer.DeclaredName}'", ctx => FixOps.DeleteWithLine(ctx, serializer));
    }
}

/// <summary>LAMA0210: a positional record can't get a generated serializer: add a nested one.</summary>
sealed class RecordSerializerFix : FixProvider
{
    public override IEnumerable<string> Ids => ["LAMA0210"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        if ((c.Target as IRecordDeclaration ?? c.At<IRecordDeclaration>()) is not { PrimaryConstructorDeclaration.Params: { } parameterList } record) yield break;
        var parameters = parameterList.ParameterDeclarations.OfType<ICSharpParameterDeclaration>()
            .Select(p => (Type: p.TypeUsage?.GetText() ?? "", Name: p.DeclaredName)).ToList();
        if (parameters.Count == 0 || parameters.Any(p => p.Type.Length == 0)) yield break;
        var typeParameters = Regex.Match(record.GetText(), $@"\b{Regex.Escape(record.DeclaredName)}\s*(<[^>]*>)").Groups[1].Value;
        var recordType = record.DeclaredName + typeParameters;
        var indent = FixOps.IndentAt(record.GetDocumentStartOffset());
        var serializer = RecordSerializer(recordType, parameters, record.IsStruct, indent + "    ");
        var range = record.GetDocumentRange();
        var text = record.GetText();
        yield return new Fix($"Add a serializer to '{record.DeclaredName}'", ctx =>
        {
            if (text.TrimEnd().EndsWith(";"))
            {
                var semicolon = range.StartOffset.Offset + text.LastIndexOf(';');
                ctx.Replace(new DocumentRange(range.Document, new JetBrains.Util.TextRange(semicolon, semicolon + 1)), $"\n{indent}{{\n{serializer}\n{indent}}}");
            }
            else CompileTimeOps.InsertMember(ctx, record, serializer.TrimStart() + "\n");
            CompileTimeOps.AddUsingIn(ctx, record, CompileTimeOps.Serialization);
        });
    }
}
