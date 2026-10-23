using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using LamaRider.Actions;
using LamaRider.Model;

namespace LamaRider.Fixes;

/// <summary>
/// Edits for fixes, as text edits on the document computed from PSI nodes (see <see cref="FixContext"/>): modifiers,
/// attributes, arguments, usings, renames, project properties.
/// </summary>
public static class FixOps
{
    static readonly string[] AccessModifiers = ["public", "private", "protected", "internal", "file"];

    // --- Modifiers -------------------------------------------------------------------------------------------------

    public static IEnumerable<ITokenNode> Modifiers(ICSharpModifiersOwnerDeclaration declaration) =>
        (IEnumerable<ITokenNode>?)declaration.ModifiersList?.Modifiers ?? Enumerable.Empty<ITokenNode>();

    public static bool HasModifier(ICSharpModifiersOwnerDeclaration declaration, string modifier) =>
        Modifiers(declaration).Any(m => m.GetText() == modifier);

    /// <summary>Where the declaration proper starts: after its attributes and doc comment.</summary>
    public static DocumentOffset StartAfterAttributes(ITreeNode declaration)
    {
        for (var child = declaration.FirstChild; child != null; child = child.NextSibling)
            if (child is not (IAttributeSectionList or IAttributeSection or IDocCommentBlock or IWhitespaceNode or ICommentNode))
                return child.GetDocumentStartOffset();
        return declaration.GetDocumentStartOffset();
    }

    /// <summary>Adds a modifier, right before `partial` (which must stay last) or after the other modifiers.</summary>
    public static void AddModifier(FixContext c, ICSharpModifiersOwnerDeclaration declaration, string modifier)
    {
        if (HasModifier(declaration, modifier)) return;
        var modifiers = Modifiers(declaration).ToList();
        if (AccessModifiers.Contains(modifier) || modifiers.Count == 0)
        {
            c.Insert(modifiers.FirstOrDefault()?.GetDocumentStartOffset() ?? StartAfterAttributes(declaration), modifier + " ");
            return;
        }
        var partial = modifiers.FirstOrDefault(m => m.GetText() == "partial");
        if (partial != null && modifier != "partial") c.Insert(partial.GetDocumentStartOffset(), modifier + " ");
        else c.Insert(modifiers.Last().GetDocumentEndOffset(), " " + modifier);
    }

    public static void RemoveModifier(FixContext c, ICSharpModifiersOwnerDeclaration declaration, string modifier)
    {
        foreach (var token in Modifiers(declaration).Where(m => m.GetText() == modifier))
            c.Delete(WithFollowingSpace(token));
    }

    /// <summary>Replaces the access modifiers (e.g. "public", "protected internal").</summary>
    public static void SetAccessibility(FixContext c, ICSharpModifiersOwnerDeclaration declaration, string access)
    {
        var existing = Modifiers(declaration).Where(m => AccessModifiers.Contains(m.GetText())).ToList();
        foreach (var token in existing.Skip(1)) c.Delete(WithFollowingSpace(token));
        if (existing.Count > 0) c.Replace(existing[0], access);
        else c.Insert(Modifiers(declaration).FirstOrDefault()?.GetDocumentStartOffset() ?? StartAfterAttributes(declaration), access + " ");
    }

    static DocumentRange WithFollowingSpace(ITreeNode node)
    {
        var range = node.GetDocumentRange();
        var text = range.Document.GetText();
        var end = range.EndOffset.Offset;
        while (end < text.Length && text[end] == ' ') end++;
        return new DocumentRange(range.Document, new JetBrains.Util.TextRange(range.StartOffset.Offset, end));
    }

    // --- Attributes ------------------------------------------------------------------------------------------------

    /// <summary>The short name of an attribute as written ("Log" for [Log] and [LogAttribute]).</summary>
    public static string ShortName(IAttribute attribute)
    {
        var name = attribute.Name?.ShortName ?? "";
        return name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > 9 ? name.Substring(0, name.Length - 9) : name;
    }

    public static IAttribute? FindAttribute(IAttributesOwnerDeclaration declaration, string shortName) =>
        declaration.Attributes.FirstOrDefault(a => ShortName(a) == shortName || ShortName(a) + "Attribute" == shortName);

    /// <summary>The CLR name of the attribute's type, when it resolves.</summary>
    public static string? TypeName(IAttribute attribute) =>
        (attribute.Name?.Reference.Resolve().DeclaredElement as ITypeElement)?.GetClrName().FullName;

    /// <summary>Attributes of <paramref name="declaration"/> that are aspects (by the worker's aspect classes, else by name).</summary>
    public static IEnumerable<IAttribute> AspectAttributes(FixContext c, IAttributesOwnerDeclaration declaration) =>
        declaration.Attributes.Where(a => TypeName(a) is { } name && c.Model?.IsAspectClass("T:" + name.Replace('+', '.')) == true);

    /// <summary>
    /// Adds an attribute (the text inside the brackets, e.g. "Template" or "CompileTime"): on its own line above a member
    /// or type, inline before a parameter or type parameter.
    /// </summary>
    public static void AddAttribute(FixContext c, IAttributesOwnerDeclaration declaration, string attribute)
    {
        if (declaration is IParameterDeclaration or ITypeParameterDeclaration)
        {
            c.Insert(declaration.GetDocumentStartOffset(), $"[{attribute}] ");
            return;
        }
        var anchor = declaration.Attributes.FirstOrDefault()?.GetContainingNode<IAttributeSection>()?.GetDocumentStartOffset() ?? StartAfterAttributes(declaration);
        var text = anchor.Document.GetText();
        var lineStart = text.LastIndexOf('\n', Math.Max(0, anchor.Offset - 1)) + 1;
        if (anchor.Offset == 0) lineStart = 0;
        var indent = text.Substring(lineStart, anchor.Offset - lineStart);
        if (indent.Trim().Length > 0) c.Insert(anchor, $"[{attribute}] ");
        else c.Insert(new DocumentOffset(anchor.Document, lineStart), $"{indent}[{attribute}]\n");
    }

    /// <summary>Removes an attribute: its whole section (and line) when it is alone in it, else it and a comma.</summary>
    public static void RemoveAttribute(FixContext c, IAttribute attribute)
    {
        var section = attribute.GetContainingNode<IAttributeSection>();
        var document = attribute.GetDocumentRange().Document;
        var text = document.GetText();
        if (section != null && section.Attributes.Count == 1)
        {
            var range = section.GetDocumentRange();
            int start = range.StartOffset.Offset, end = range.EndOffset.Offset;
            var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
            var lineEnd = text.IndexOf('\n', end);
            if (text.Substring(lineStart, start - lineStart).Trim().Length == 0 && (lineEnd < 0 ? text.Substring(end) : text.Substring(end, lineEnd - end)).Trim().Length == 0)
                c.Delete(new DocumentRange(document, new JetBrains.Util.TextRange(lineStart, lineEnd < 0 ? text.Length : lineEnd + 1)));
            else
            {
                while (end < text.Length && text[end] == ' ') end++;
                c.Delete(new DocumentRange(document, new JetBrains.Util.TextRange(start, end)));
            }
            return;
        }
        var a = attribute.GetDocumentRange();
        int s = a.StartOffset.Offset, e = a.EndOffset.Offset;
        var after = e;
        while (after < text.Length && text[after] == ' ') after++;
        if (after < text.Length && text[after] == ',')
        {
            after++;
            while (after < text.Length && text[after] == ' ') after++;
            c.Delete(new DocumentRange(document, new JetBrains.Util.TextRange(s, after)));
            return;
        }
        var before = s;
        while (before > 0 && text[before - 1] == ' ') before--;
        if (before > 0 && text[before - 1] == ',') before--;
        c.Delete(new DocumentRange(document, new JetBrains.Util.TextRange(before, e)));
    }

    /// <summary>Sets a named property argument of an attribute ("WhenExists = OverrideStrategy.Ignore").</summary>
    public static void SetAttributeProperty(FixContext c, IAttribute attribute, string name, string value)
    {
        var existing = attribute.PropertyAssignments.FirstOrDefault(p => p.PropertyNameIdentifier?.Name == name);
        if (existing?.Source != null)
        {
            c.Replace(existing.Source, value);
            return;
        }
        AppendArgument(c, attribute, $"{name} = {value}");
    }

    /// <summary>Removes a named property argument of an attribute.</summary>
    public static void RemoveAttributeProperty(FixContext c, IAttribute attribute, string name)
    {
        if (attribute.PropertyAssignments.FirstOrDefault(p => p.PropertyNameIdentifier?.Name == name) is { } assignment)
            DeleteListItem(c, assignment);
    }

    static void AppendArgument(FixContext c, ITreeNode owner, string argument)
    {
        var range = owner.GetDocumentRange();
        var text = range.GetText();
        var close = text.LastIndexOf(')');
        if (close < 0 || text.IndexOf('(') < 0)
        {
            c.Insert(range.EndOffset, $"({argument})");
            return;
        }
        var open = text.IndexOf('(');
        var empty = text.Substring(open + 1, close - open - 1).Trim().Length == 0;
        c.Insert(new DocumentOffset(range.Document, range.StartOffset.Offset + close), empty ? argument : ", " + argument);
    }

    /// <summary>Deletes an item of a comma-separated list (argument, attribute argument) with its comma.</summary>
    public static void DeleteListItem(FixContext c, ITreeNode item)
    {
        var range = item.GetDocumentRange();
        var text = range.Document.GetText();
        int s = range.StartOffset.Offset, e = range.EndOffset.Offset;
        var after = e;
        while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
        if (after < text.Length && text[after] == ',')
        {
            after++;
            while (after < text.Length && text[after] == ' ') after++;
            c.Delete(new DocumentRange(range.Document, new JetBrains.Util.TextRange(s, after)));
            return;
        }
        var before = s;
        while (before > 0 && char.IsWhiteSpace(text[before - 1])) before--;
        if (before > 0 && text[before - 1] == ',') s = before - 1;
        c.Delete(new DocumentRange(range.Document, new JetBrains.Util.TextRange(s, e)));
    }

    // --- Invocations -----------------------------------------------------------------------------------------------

    /// <summary>Sets a named argument of a call ("whenExists: OverrideStrategy.Ignore"), adding it when missing.</summary>
    public static void SetNamedArgument(FixContext c, IInvocationExpression invocation, string name, string value)
    {
        var existing = invocation.Arguments.FirstOrDefault(a => a.NameIdentifier?.Name == name);
        if (existing?.Value != null) c.Replace(existing.Value, value);
        else AppendArgument(c, invocation, $"{name}: {value}");
    }

    /// <summary>The method name a call invokes ("AddAspect" for amender.Outbound.AddAspect&lt;X&gt;()).</summary>
    public static string? MethodName(IInvocationExpression invocation) =>
        (invocation.InvokedExpression as IReferenceExpression)?.NameIdentifier?.Name;

    /// <summary>Renames the method a call invokes, keeping type arguments.</summary>
    public static void RenameInvokedMethod(FixContext c, IInvocationExpression invocation, string newName)
    {
        if ((invocation.InvokedExpression as IReferenceExpression)?.NameIdentifier is { } name) c.Replace(name, newName);
    }

    // --- Code --------------------------------------------------------------------------------------------------------

    /// <summary>Adds `using <paramref name="ns"/>;` after the file's last using directive (or at its start).</summary>
    public static void AddUsing(FixContext c, string ns)
    {
        if (c.File == null) return;
        var usings = c.File.Descendants<IUsingDirective>().ToEnumerable().ToList();
        if (usings.OfType<IUsingSymbolDirective>().Any(u => u.ImportedSymbolName?.QualifiedName == ns)) return;
        if (usings.LastOrDefault() is { } last)
        {
            c.Insert(last.GetDocumentEndOffset(), $"\nusing {ns};");
            return;
        }
        var document = c.Document ?? c.File.GetDocumentRange().Document;
        c.Insert(new DocumentOffset(document, 0), $"using {ns};\n\n");
    }

    /// <summary>Renames a declaration and the references to it in the solution.</summary>
    public static void Rename(FixContext c, IDeclaration declaration, string newName)
    {
        c.Replace(declaration.GetNameDocumentRange(), newName);
        if (declaration.DeclaredElement is not { } element) return;
        foreach (var reference in declaration.GetPsiServices().Finder.FindAllReferences(element))
            if (reference.GetTreeNode().GetContainingFile()?.GetDocumentRange(reference.GetTreeTextRange()) is { } range && range.IsValid())
                c.Replace(range, newName);
    }

    /// <summary>The indentation of the line <paramref name="offset"/> is on.</summary>
    public static string IndentAt(DocumentOffset offset)
    {
        var text = offset.Document.GetText();
        var lineStart = text.LastIndexOf('\n', Math.Max(0, offset.Offset - 1)) + 1;
        var end = lineStart;
        while (end < text.Length && (text[end] == ' ' || text[end] == '\t')) end++;
        return text.Substring(lineStart, end - lineStart);
    }

    /// <summary>Inserts a line (indented like <paramref name="before"/>'s line) above the line of <paramref name="before"/>.</summary>
    public static void InsertLineAbove(FixContext c, ITreeNode before, string line)
    {
        var start = before.GetDocumentStartOffset();
        var text = start.Document.GetText();
        var lineStart = text.LastIndexOf('\n', Math.Max(0, start.Offset - 1)) + 1;
        if (start.Offset == 0) lineStart = 0;
        c.Insert(new DocumentOffset(start.Document, lineStart), IndentAt(start) + line + "\n");
    }

    /// <summary>Removes a statement or member with its line, when it is alone on it.</summary>
    public static void DeleteWithLine(FixContext c, ITreeNode node)
    {
        var range = node.GetDocumentRange();
        var text = range.Document.GetText();
        int start = range.StartOffset.Offset, end = range.EndOffset.Offset;
        var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        var lineEnd = text.IndexOf('\n', end);
        if (text.Substring(lineStart, start - lineStart).Trim().Length == 0 && (lineEnd < 0 ? text.Substring(end) : text.Substring(end, lineEnd - end)).Trim().Length == 0)
            c.Delete(new DocumentRange(range.Document, new JetBrains.Util.TextRange(lineStart, lineEnd < 0 ? text.Length : lineEnd + 1)));
        else c.Delete(node);
    }

    // --- Project -------------------------------------------------------------------------------------------------------

    public static void SetProjectProperty(FixContext c, string name, string value)
    {
        if (c.Project is { } project) c.EditProjectFile(project, text => MsBuildEdits.SetProperty(text, name, value));
    }

    public static void RemoveProjectProperty(FixContext c, string name)
    {
        if (c.Project is { } project) c.EditProjectFile(project, text => MsBuildEdits.RemoveProperty(text, name));
    }

    // --- Declarations --------------------------------------------------------------------------------------------------

    /// <summary>The declarations of a declared element in source (e.g. an aspect class in another file).</summary>
    public static IEnumerable<IDeclaration> Declarations(IDeclaredElement element) => element.GetDeclarations();

    /// <summary>The type element of a worker class id ("T:Ns.Aspect"), when it's in a module visible from the context.</summary>
    public static ITypeElement? TypeOf(FixContext c, string classId) =>
        c.File?.GetPsiModule() is { } module ? AspectTargets.AspectClass(classId, module) : null;
}
