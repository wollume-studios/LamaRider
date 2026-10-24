using System.Collections.Generic;
using System.Linq;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Feature.Services.Protocol;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using LamaRider.Model;
using static JetBrains.Rider.Model.SolutionLamaRiderModelEx;

namespace LamaRider.Fixes;

/// <summary>Fixes every Metalama diagnostic has: look it up, suppress a warning, remove an ineligible aspect from its target.</summary>
static class GenericFixes
{
    /// <summary>Aspect errors reported on the target that removing the aspect there resolves.</summary>
    static readonly HashSet<string> AspectOnTargetErrors =
        ["LAMA0003", "LAMA0037", "LAMA0114", "LAMA0247", "LAMA0287", "LAMA0534", "LAMA0700", "LAMA0703", "LAMA0750", "LAMA5111"];

    public static IEnumerable<Fix> For(FixContext c)
    {
        var id = c.Diagnostic.Id;
        if (AspectOnTargetErrors.Contains(id))
            foreach (var attribute in AspectAttributesAt(c))
                yield return new Fix($"Remove [{FixOps.ShortName(attribute)}] from this declaration", ctx => FixOps.RemoveAttribute(ctx, attribute));

        if (c.Diagnostic.Severity is "Warning" or "Info" && c.Range.IsValid())
        {
            yield return new Fix($"Suppress {id} here (#pragma)", ctx => Suppress(ctx, id));
            if (c.Project != null)
                yield return new Fix($"Suppress {id} in project '{c.Project.Name}' (NoWarn)",
                    ctx => ctx.EditProjectFile(ctx.Project!, text => MsBuildEdits.AddNoWarn(text, id)));
        }

        if (id.StartsWith("LAMA"))
            yield return new Fix($"Look up {id} online", ctx => ctx.Solution.GetProtocolSolution().GetLamaRiderModel().OpenDiagnosticDocs.Fire(id));
    }

    /// <summary>The aspect attribute the diagnostic is on, else the aspect attributes of its target.</summary>
    static IEnumerable<IAttribute> AspectAttributesAt(FixContext c)
    {
        if (c.At<IAttribute>() is { } attribute) return [attribute];
        if (c.Target is IAttributesOwnerDeclaration target)
        {
            var aspects = FixOps.AspectAttributes(c, target).ToList();
            // The message names the aspect: prefer that one when the target has several.
            var named = aspects.Where(a => c.Diagnostic.Message.Contains(FixOps.ShortName(a))).ToList();
            return named.Count > 0 ? named : aspects;
        }
        return [];
    }

    /// <summary>#pragma warning disable/restore around the reported lines.</summary>
    static void Suppress(FixContext c, string id)
    {
        var document = c.Range.Document;
        var text = document.GetText();
        var start = c.Range.StartOffset.Offset;
        var end = c.Range.EndOffset.Offset;
        var lineStart = text.LastIndexOf('\n', System.Math.Max(0, start - 1)) + 1;
        if (start == 0) lineStart = 0;
        var lineEnd = text.IndexOf('\n', end);
        if (lineEnd < 0) lineEnd = text.Length;
        c.Insert(new DocumentOffset(document, lineEnd), $"\n#pragma warning restore {id}");
        c.Insert(new DocumentOffset(document, lineStart), $"#pragma warning disable {id}\n");
    }
}
