using System.Collections.Generic;
using System.Linq;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using LamaRider.Model;
using static LamaRider.Fixes.ProjectFixHelpers;

namespace LamaRider.Fixes;

/// <summary>The durability ([Durable], LAMA0870–0878) and immutability ([ImmutableType], LAMA0880–0886) analyzers.</summary>
sealed class AnalyzerFixes : FixProvider
{
    const string Utilities = "Metalama.Framework.Utilities";

    public override IEnumerable<string> Ids =>
        ["LAMA0870", "LAMA0871", "LAMA0872", "LAMA0873", "LAMA0874", "LAMA0875", "LAMA0876", "LAMA0877", "LAMA0878",
         "LAMA0880", "LAMA0881", "LAMA0882", "LAMA0883", "LAMA0884", "LAMA0886"];

    public override IEnumerable<Fix> Fixes(FixContext c)
    {
        var quoted = ProjectFixLogic.Quoted(c.Diagnostic.Message);
        var secondName = quoted.Count > 1 ? quoted[1] : null;
        switch (c.Diagnostic.Id)
        {
            case "LAMA0870":
            case "LAMA0876":
            {
                if (Member(c) is not { } member) break;
                var type = Named(TypeOfMember(member), secondName);
                if (MarkType(type, "Durable") is { } markType) yield return markType;
                // [Durable] on the member waives the check of its declared type (interfaces, object, delegates).
                if (c.Diagnostic.Id == "LAMA0876" || type is IInterface or IDelegate || type?.GetClrName().FullName == "System.Object")
                    if (MarkMember(member, "Durable") is { } markMember) yield return markMember;
                break;
            }
            case "LAMA0871":
                if (AssignedMember(c) is { } assigned && RemoveFrom(assigned, "Durable") is { } removeFromMember) yield return removeFromMember;
                break;
            case "LAMA0872":
                if (c.Node?.GetContainingNode<ICSharpArgument>(returnThis: true)?.MatchingParameter?.Element is { } parameter
                    && RemoveFrom(parameter, "Durable") is { } removeFromParameter)
                    yield return removeFromParameter;
                break;
            case "LAMA0873":
            case "LAMA0883":
            {
                if (TypeAt(c) is not { } declaration) break;
                var attribute = c.Diagnostic.Id == "LAMA0873" ? "Durable" : "ImmutableType";
                var baseClass = BaseClass(declaration);
                if (MarkType(baseClass, attribute) is { } markBase) yield return markBase;
                else if (attribute == "ImmutableType" && DeclareImmutable(c, baseClass) is { } declare) yield return declare;
                if (attribute == "Durable" && declaration.DeclaredElement is { } type && RemoveFrom(type, attribute) is { } removeFromType) yield return removeFromType;
                break;
            }
            case "LAMA0874":
            case "LAMA0886":
            {
                var name = c.Diagnostic.Id == "LAMA0874" ? "Durable" : "ImmutableType";
                var attribute = c.At<IAttribute>() is { } at && FixOps.ShortName(at) == name ? at
                    : TypeAt(c) is { } type ? FixOps.FindAttribute(type, name) : null;
                if (attribute != null) yield return new Fix($"Remove the redundant [{name}]", ctx => FixOps.RemoveAttribute(ctx, attribute));
                break;
            }
            case "LAMA0875":
                if (Member(c) is { } weak && !weak.DeclaredName.EndsWith("Dangerous"))
                    yield return new Fix($"Rename '{weak.DeclaredName}' to '{weak.DeclaredName}Dangerous'", ctx => FixOps.Rename(ctx, weak, weak.DeclaredName + "Dangerous"));
                break;
            case "LAMA0877":
            case "LAMA0880":
            case "LAMA0881":
                foreach (var fix in Confine(c)) yield return fix;
                break;
            case "LAMA0878":
                if (c.At<ILambdaExpression>() is { StaticKeyword: null } lambda)
                    yield return new Fix("Make the lambda static (pass what it captures as parameters)",
                        ctx => ctx.Insert(FixOps.StartAfterAttributes(lambda), "static "));
                break;
            case "LAMA0882":
            case "LAMA0884":
            {
                if (Member(c) is not { } member) break;
                var type = Named(TypeOfMember(member), secondName);
                if (MarkType(type, "ImmutableType") is { } mark) yield return mark;
                else if (DeclareImmutable(c, type) is { } declare) yield return declare;
                break;
            }
        }
    }

    /// <summary>Marks a type of the solution with [Durable] / [ImmutableType], when it isn't yet.</summary>
    static Fix? MarkType(ITypeElement? type, string attribute) =>
        SourceOf(type) is { } declaration && FixOps.FindAttribute(declaration, attribute) == null
            ? new Fix($"Mark '{declaration.DeclaredName}' [{attribute}]", ctx => AddAttributeTo(ctx, declaration, attribute, Utilities))
            : null;

    static Fix? MarkMember(ICSharpTypeMemberDeclaration member, string attribute) =>
        member is IAttributesOwnerDeclaration owner && FixOps.FindAttribute(owner, attribute) == null
            ? new Fix($"Mark '{member.DeclaredName}' [{attribute}]", ctx => AddAttributeTo(ctx, member, attribute, Utilities))
            : null;

    /// <summary>Removes the attribute from the source declaration of an element.</summary>
    static Fix? RemoveFrom(IDeclaredElement element, string attribute) =>
        element.GetDeclarations().OfType<IAttributesOwnerDeclaration>().Select(d => FixOps.FindAttribute(d, attribute)).FirstOrDefault(a => a != null) is { } found
            ? new Fix($"Remove [{attribute}] from '{element.ShortName}'", ctx => FixOps.RemoveAttribute(ctx, found))
            : null;

    /// <summary>A type outside the solution (and outside System) declared immutable with a MetalamaImmutableType item.</summary>
    static Fix? DeclareImmutable(FixContext c, ITypeElement? type)
    {
        if (type == null || c.Project is not { } project || SourceOf(type) != null) return null;
        var name = type.GetClrName().FullName;
        if (name.StartsWith("System.") || name.StartsWith("Microsoft.")) return null;
        return new Fix($"Declare '{type.ShortName}' immutable in '{project.Name}' (MetalamaImmutableType)",
            ctx => ctx.EditProjectFile(project, text => ProjectFixLogic.AddItem(text, "MetalamaImmutableType", name)));
    }

    /// <summary>The member a non-durable value is assigned to (assignment, initializer or out argument).</summary>
    static IDeclaredElement? AssignedMember(FixContext c)
    {
        var node = c.Node;
        if (node?.GetContainingNode<IAssignmentExpression>(returnThis: true) is { } assignment && assignment.Source is { } source
            && source.GetDocumentRange().Contains(node.GetDocumentRange()))
            return (assignment.Dest as IReferenceExpression)?.Reference.Resolve().DeclaredElement;
        if (node?.GetContainingNode<IFieldDeclaration>(returnThis: true) is { } field) return field.DeclaredElement;
        if (node?.GetContainingNode<IPropertyDeclaration>(returnThis: true) is { } property) return property.DeclaredElement;
        return (node?.GetContainingNode<ICSharpArgument>(returnThis: true)?.Value as IReferenceExpression)?.Reference.Resolve().DeclaredElement;
    }

    /// <summary>LAMA0877/0880/0881: a member writable from outside its type: make it read-only or private.</summary>
    static IEnumerable<Fix> Confine(FixContext c)
    {
        switch (Member(c))
        {
            case IFieldDeclaration field:
                if (!FixOps.HasModifier(field, "readonly"))
                    yield return new Fix($"Add 'readonly' to '{field.DeclaredName}'", ctx => FixOps.AddModifier(ctx, field, "readonly"));
                if (!FixOps.HasModifier(field, "private"))
                    yield return new Fix($"Make '{field.DeclaredName}' private", ctx => FixOps.SetAccessibility(ctx, field, "private"));
                break;
            case IPropertyDeclaration property:
                if (property.AccessorDeclarations.FirstOrDefault(a => a.NameIdentifier?.Name is "set" or "init") is not { } setter) break;
                var name = property.DeclaredName;
                if (setter.NameIdentifier!.Name == "set" && c.Diagnostic.Id == "LAMA0881")
                    yield return new Fix($"Replace 'set' with 'init' in '{name}'", ctx => ctx.Replace(setter.NameIdentifier!, "init"));
                // The accessor's own modifiers (not the property's).
                var access = ((IEnumerable<ITokenNode>?)setter.OwnModifiersList?.Modifiers ?? []).Where(m => m.GetText() is "public" or "protected" or "internal" or "private").ToList();
                if (!access.Any(m => m.GetText() == "private"))
                    yield return new Fix($"Make the setter of '{name}' private", ctx =>
                    {
                        if (access.Count == 0) ctx.Insert(setter.NameIdentifier!.GetDocumentStartOffset(), "private ");
                        else
                        {
                            ctx.Replace(access[0], "private");
                            foreach (var extra in access.Skip(1)) ctx.Delete(extra);
                        }
                    });
                yield return new Fix($"Remove the setter of '{name}'", ctx =>
                {
                    var range = setter.GetDocumentRange();
                    var text = range.Document.GetText();
                    var start = range.StartOffset.Offset;
                    while (start > 0 && text[start - 1] is ' ' or '\t') start--;
                    ctx.Delete(new JetBrains.DocumentModel.DocumentRange(range.Document, new JetBrains.Util.TextRange(start, range.EndOffset.Offset)));
                });
                break;
        }
    }
}
