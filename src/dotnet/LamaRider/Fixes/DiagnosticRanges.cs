using System;
using System.Collections.Generic;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.Util;
using JetBrains.Util;
using JetBrains.Util.dataStructures.TypedIntrinsics;
using LamaRider.Model;

namespace LamaRider.Fixes;

/// <summary>Where a diagnostic is in a document: its span, else its target's name, else its line.</summary>
public static class DiagnosticRanges
{
    /// <summary>Doc ids of the declarations of <paramref name="file"/>, with their name ranges.</summary>
    public static Dictionary<string, DocumentRange> Names(ICSharpFile file)
    {
        var names = new Dictionary<string, DocumentRange>();
        foreach (var declaration in file.Descendants<IDeclaration>())
        {
            var id = declaration.DeclaredElement switch
            {
                ITypeElement type => XMLDocUtil.GetTypeElementXmlDocId(type),
                ITypeMember member => XMLDocUtil.GetTypeMemberXmlDocId(member, member.ShortName),
                _ => null,
            };
            var range = declaration.GetNameDocumentRange();
            if (id != null && range.IsValid()) names.TryAdd(id, range);
        }
        return names;
    }

    /// <summary>The reported span when it still fits the document (it is the model's, from the last analysis).</summary>
    public static DocumentRange? Of(DiagnosticInfo diagnostic, IDocument document, Dictionary<string, DocumentRange> names)
    {
        var lines = (int)document.GetLineCount();
        if (diagnostic is { Start: { } start, End: { } end, Line: { } spanLine } && end >= start && end <= document.GetTextLength()
            && spanLine >= 1 && spanLine <= lines && (int)document.GetCoordsByOffset(start).Line == spanLine - 1)
            return new DocumentRange(document, new TextRange(start, end == start ? Math.Min(end + 1, document.GetTextLength()) : end));
        if (diagnostic.Target != null && names.TryGetValue(AspectModel.Owner(diagnostic.Target), out var name)) return name;
        if (diagnostic.Line is not { } line || line < 1 || line > lines) return null;
        var docLine = (Int32<DocLine>)(line - 1);
        var lineStart = document.GetLineStartOffset(docLine);
        var lineEnd = document.GetLineEndOffsetNoLineBreak(docLine);
        var text = document.GetText(new TextRange(lineStart, lineEnd));
        var trimmed = lineStart + (text.Length - text.TrimStart().Length);
        return new DocumentRange(document, new TextRange(trimmed, Math.Max(trimmed, lineEnd)));
    }
}
