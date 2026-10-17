using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LamaRider.Model;

/// <summary>Code Vision / inlay hints for the aspects on a declaration.</summary>
public static class AspectHints
{
    /// <summary>More named aspects than this collapse into one "N aspects" hint.</summary>
    const int MaxNamed = 2;

    const int TooltipWidth = 70;

    /// <param name="TransformedCode">The "transformed code" hint: its click shows the transformed code.</param>
    public sealed record Hint(string Text, string ToolTip, bool TransformedCode);

    /// <summary>Named: each aspect at its location (member, parameter, return value). Count: "N aspects" on the declaration.</summary>
    public enum Style { Named, Count }

    /// <param name="TransformedCodeHint">A "transformed code" hint of its own; otherwise the aspect menu offers it.</param>
    public sealed record Options(Style Style = Style.Named, bool TransformedCodeHint = true);

    /// <summary>
    /// The Code Vision hints of a type or member. Named: one per aspect a fabric, inheritance etc. added to it (attribute
    /// aspects are visible in the code; parameter and return value aspects get inlay hints). Count: one "N aspects" for
    /// it, its parameters and return value. Plus the "transformed code" hint when the options ask for it.
    /// </summary>
    /// <param name="all">Instances on the declaration, its parameters and return value.</param>
    public static IReadOnlyList<Hint> ForDeclaration(IReadOnlyCollection<AspectInstanceInfo> all, string docId, Options options,
        Func<string, string> aspectName, Func<string, string?> summary, Func<string, bool>? offersCodeFixes = null)
    {
        var distinct = all.GroupBy(i => (i.AspectClass, i.Target)).Select(g => g.First()).ToList();
        if (distinct.Count == 0) return [];
        var hints = new List<Hint>();
        if (options.Style == Style.Named)
            hints.AddRange(Hints(distinct.Where(i => i.Target == docId).ToList(), aspectName, summary, offersCodeFixes));
        // Count style, or nothing named and no "transformed code" hint: a count leads to the aspect menu.
        if (options.Style == Style.Count || hints.Count == 0 && !options.TransformedCodeHint)
            hints.Add(new Hint(distinct.Count == 1 ? "1 aspect" : $"{distinct.Count} aspects",
                Describe(distinct, docId, aspectName, summary, offersCodeFixes), TransformedCode: false));
        if (options.TransformedCodeHint)
            hints.Add(new Hint("transformed code", "Show the code Metalama generates for it", TransformedCode: true));
        return hints;
    }

    /// <summary>
    /// One hint per aspect added by a fabric, inheritance etc. ("N aspects" for more than <see cref="MaxNamed"/>).
    /// Aspects added by an attribute are visible in the code, so they are not named.
    /// </summary>
    /// <param name="offersCodeFixes">The aspect class's assembly references Metalama.Extensions.CodeFixes.</param>
    public static IReadOnlyList<Hint> Hints(IReadOnlyCollection<AspectInstanceInfo> instances, Func<string, string> aspectName, Func<string, string?> summary,
        Func<string, bool>? offersCodeFixes = null)
    {
        var named = instances.Where(i => i.AppliedVia != "Attribute").GroupBy(i => i.AspectClass).Select(g => g.First()).ToList();
        if (named.Count > MaxNamed) return [new Hint($"{named.Count} aspects", Describe(named, null, aspectName, summary, offersCodeFixes), TransformedCode: false)];
        // Where the aspect comes from, then its documentation (the hint names the aspect).
        return named.Select(i => new Hint(aspectName(i.AspectClass), Join(Origin(i.AppliedVia, i.AppliedBy), Details(i, summary, offersCodeFixes)), TransformedCode: false)).ToList();
    }

    static string? Details(AspectInstanceInfo i, Func<string, string?> summary, Func<string, bool>? offersCodeFixes)
    {
        var lines = new List<string>();
        if (summary(i.AspectClass) is { Length: > 0 } doc) lines.Add(Wrap(doc));
        if (offersCodeFixes?.Invoke(i.AspectClass) == true) lines.Add(CodeFixesNote);
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>Each aspect, where it is when not on <paramref name="docId"/> itself, and where it comes from.</summary>
    static string Describe(IEnumerable<AspectInstanceInfo> instances, string? docId, Func<string, string> aspectName, Func<string, string?> summary,
        Func<string, bool>? offersCodeFixes) =>
        string.Join("\n\n", instances.Select(i =>
            Join($"{aspectName(i.AspectClass)}{Location(i, docId)} — {Lower(Origin(i.AppliedVia, i.AppliedBy))}", Details(i, summary, offersCodeFixes))));

    static string Location(AspectInstanceInfo i, string? docId)
    {
        if (docId == null || i.Target == docId) return "";
        if (i.Target.EndsWith(";Return")) return " on the return value";
        var slash = i.TargetName.LastIndexOf('/');
        return slash >= 0 ? " on " + i.TargetName.Substring(slash + 1) : "";
    }

    /// <summary>Metalama.Extensions.CodeFixes is a Premium component (without a license the build fails with LAMA0806).</summary>
    public const string CodeFixesNote = "May offer code fixes (Alt+Enter); they need a Metalama Professional license.";

    static string Join(string head, string? details) => details == null ? head : head + "\n" + details;

    /// <summary>Metalama's AspectPredecessorKind and the predecessor (fabric name or aspect instance) in words.</summary>
    public static string Origin(string appliedVia, string? appliedBy) => appliedVia switch
    {
        "Attribute" => "Added by an attribute",
        "Fabric" => appliedBy != null ? $"Added by fabric {appliedBy}" : "Added by a fabric",
        "Inherited" => appliedBy != null ? $"Inherited from {appliedBy}" : "Inherited from a base declaration",
        "ChildAspect" => appliedBy != null ? $"Added by aspect {appliedBy}" : "Added by another aspect",
        "RequiredAspect" => appliedBy != null ? $"Required by aspect {appliedBy}" : "Required by another aspect",
        "Interactive" => "Added from the IDE",
        _ => "Origin unknown",
    };

    /// <summary>Sentence case to mid-sentence: "Added by fabric X" to "added by fabric X".</summary>
    public static string Lower(string sentence) => sentence.Length == 0 ? sentence : char.ToLowerInvariant(sentence[0]) + sentence.Substring(1);

    /// <summary>Word wrap so tooltips stay narrow.</summary>
    static string Wrap(string text)
    {
        var result = new StringBuilder();
        var line = 0;
        foreach (var word in text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line > 0 && line + 1 + word.Length > TooltipWidth)
            {
                result.Append('\n');
                line = 0;
            }
            else if (line > 0)
            {
                result.Append(' ');
                line++;
            }
            result.Append(word);
            line += word.Length;
        }
        return result.ToString();
    }

    /// <summary>A method's return type inlay hint: its return value aspects not added by an attribute.</summary>
    public static IReadOnlyList<AspectInstanceInfo> ForReturn(IEnumerable<AspectInstanceInfo> instances, string memberDocId) =>
        instances.Where(i => i.Target == memberDocId + ";Return" && i.AppliedVia != "Attribute").ToList();

    /// <summary>A parameter's inlay hint: its aspects not added by an attribute.</summary>
    public static IReadOnlyList<AspectInstanceInfo> ForParameter(IEnumerable<AspectInstanceInfo> instances, string memberDocId, int index) =>
        instances.Where(i => i.Target == ParameterTarget(memberDocId, index) && i.AppliedVia != "Attribute").ToList();

    /// <summary>The worker's target id of a parameter (Metalama serializable id suffix).</summary>
    public static string ParameterTarget(string memberDocId, int index) => $"{memberDocId};Parameter={index}";
}
