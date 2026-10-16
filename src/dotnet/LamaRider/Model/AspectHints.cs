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

    /// <param name="TransformedCode">The "transformed code" hint (only attribute aspects): its click shows the transformed code.</param>
    public sealed record Hint(string Text, string ToolTip, bool TransformedCode);

    /// <summary>
    /// One hint per aspect added by a fabric, inheritance etc. Aspects added by an attribute are visible in the code, so
    /// they are not named: only attribute aspects give one "transformed code" hint.
    /// </summary>
    public static IReadOnlyList<Hint> Hints(IReadOnlyCollection<AspectInstanceInfo> instances, Func<string, string> aspectName, Func<string, string?> summary)
    {
        var all = instances.GroupBy(i => i.AspectClass).Select(g => g.First()).ToList();
        var named = all.Where(i => i.AppliedVia != "Attribute").ToList();
        if (named.Count == 0) return [new Hint("transformed code", Describe(all, aspectName, summary), TransformedCode: true)];
        if (named.Count > MaxNamed) return [new Hint($"{all.Count} aspects", Describe(all, aspectName, summary), TransformedCode: false)];
        return named.Select(i => new Hint(aspectName(i.AspectClass), Describe(i, summary), TransformedCode: false)).ToList();
    }

    /// <summary>Where the aspect comes from, then its documentation summary (the hint names the aspect).</summary>
    static string Describe(AspectInstanceInfo instance, Func<string, string?> summary)
    {
        var origin = Origin(instance.AppliedVia, instance.AppliedBy);
        return summary(instance.AspectClass) is { Length: > 0 } doc ? origin + "\n" + Wrap(doc) : origin;
    }

    static string Describe(IEnumerable<AspectInstanceInfo> instances, Func<string, string> aspectName, Func<string, string?> summary) =>
        string.Join("\n\n", instances.Select(i =>
        {
            var head = $"{aspectName(i.AspectClass)} — {Lower(Origin(i.AppliedVia, i.AppliedBy))}";
            return summary(i.AspectClass) is { Length: > 0 } doc ? head + "\n" + Wrap(doc) : head;
        }));

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

    static string Lower(string sentence) => sentence.Length == 0 ? sentence : char.ToLowerInvariant(sentence[0]) + sentence.Substring(1);

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

    /// <summary>
    /// The member's Code Vision: aspects on the member and its return value, and parameter aspects added by attributes
    /// (visible in the code, so no hint of their own; the member's hint still offers their transformed code).
    /// </summary>
    public static IReadOnlyList<AspectInstanceInfo> ForMember(IEnumerable<AspectInstanceInfo> instances, string memberDocId) =>
        instances.Where(i => AspectModel.Owner(i.Target) == memberDocId && (!IsParameter(i.Target) || i.AppliedVia == "Attribute")).ToList();

    /// <summary>A parameter's inlay hint: its aspects not added by an attribute.</summary>
    public static IReadOnlyList<AspectInstanceInfo> ForParameter(IEnumerable<AspectInstanceInfo> instances, string memberDocId, int index) =>
        instances.Where(i => i.Target == ParameterTarget(memberDocId, index) && i.AppliedVia != "Attribute").ToList();

    static bool IsParameter(string target) => target.Contains(";Parameter=");

    /// <summary>The worker's target id of a parameter (Metalama serializable id suffix).</summary>
    public static string ParameterTarget(string memberDocId, int index) => $"{memberDocId};Parameter={index}";
}
