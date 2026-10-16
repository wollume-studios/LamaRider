using System;
using System.Collections.Generic;
using System.Linq;

namespace LamaRider.Model;

/// <summary>Code Vision / inlay hint text for the aspects on a declaration.</summary>
public static class AspectHints
{
    /// <summary>More named aspects than this collapse to "N aspects".</summary>
    const int MaxNamed = 2;

    /// <summary>
    /// Aspects added by an attribute are visible in the code, so they are not named; aspects added by fabrics,
    /// inheritance etc. are (their origin is in the <see cref="Tooltip"/>). Only attribute aspects: "transformed code"
    /// (the click shows it).
    /// </summary>
    public static string Text(IReadOnlyCollection<AspectInstanceInfo> instances, Func<string, string> aspectName)
    {
        var named = instances.Where(i => i.AppliedVia != "Attribute")
            .GroupBy(i => i.AspectClass).Select(g => g.First())
            .ToList();
        if (named.Count == 0) return "transformed code";
        if (named.Count > MaxNamed) return $"{instances.Select(i => i.AspectClass).Distinct().Count()} aspects";
        return string.Join(", ", named.Select(i => aspectName(i.AspectClass)));
    }

    /// <summary>One line per aspect: name, where it comes from and its documentation summary.</summary>
    public static string Tooltip(IEnumerable<AspectInstanceInfo> instances, Func<string, string> aspectName, Func<string, string?> summary) =>
        string.Join("\n", instances.GroupBy(i => (i.AspectClass, i.AppliedVia)).Select(g =>
        {
            var line = $"{aspectName(g.Key.AspectClass)} — {OriginDescription(g.Key.AppliedVia)}";
            return summary(g.Key.AspectClass) is { Length: > 0 } doc ? $"{line}: {doc}" : line;
        }));

    /// <summary>Metalama's AspectPredecessorKind in words.</summary>
    public static string OriginDescription(string appliedVia) => appliedVia switch
    {
        "Attribute" => "attribute",
        "Fabric" => "added by a fabric",
        "Inherited" => "inherited from a base declaration",
        "ChildAspect" => "added by another aspect",
        "RequiredAspect" => "required by another aspect",
        "Interactive" => "added from the IDE",
        _ => "origin unknown",
    };

    /// <summary>"Fabric" -> "fabric", "ChildAspect" -> "child aspect".</summary>
    public static string Origin(string appliedVia) => appliedVia switch
    {
        "ChildAspect" => "child aspect",
        "RequiredAspect" => "required aspect",
        "" => "unknown",
        _ => appliedVia.ToLowerInvariant(),
    };

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
