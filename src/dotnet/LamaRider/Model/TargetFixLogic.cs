using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LamaRider.Model;

/// <summary>
/// Pure logic of the quick-fixes for diagnostics reported on an aspect's target (see Fixes/TargetFixes.cs): what the
/// message says, which code to write.
/// </summary>
public static class TargetFixLogic
{
    /// <summary>The names quoted in a message ('Calculator.Add(int, int)'), in order.</summary>
    public static List<string> Quoted(string message) =>
        Regex.Matches(message, "'([^']*)'").Cast<Match>().Select(m => m.Groups[1].Value).ToList();

    /// <summary>"Log" for "LogAttribute": how an aspect is written as an attribute.</summary>
    public static string AttributeName(string className) =>
        className.EndsWith("Attribute", StringComparison.Ordinal) && className.Length > 9 ? className.Substring(0, className.Length - 9) : className;

    /// <summary>The last segment of a display name without its parameters ("Add" for "Calculator.Add(int, int)").</summary>
    public static string ShortName(string displayName)
    {
        var paren = displayName.IndexOf('(');
        var name = paren < 0 ? displayName : displayName.Substring(0, paren);
        var lt = name.IndexOf('<');
        if (lt >= 0) name = name.Substring(0, lt);
        return name.Substring(name.LastIndexOf('.') + 1);
    }

    // --- LAMA0003: [AttributeUsage] from the IAspect<T> interfaces --------------------------------------------------

    static readonly Dictionary<string, string[]> TargetsOfKind = new()
    {
        ["INamedType"] = ["Class", "Struct"],
        ["IMethod"] = ["Method"],
        ["IConstructor"] = ["Constructor"],
        ["IMethodBase"] = ["Method", "Constructor"],
        ["IProperty"] = ["Property"],
        ["IIndexer"] = ["Property"],
        ["IField"] = ["Field"],
        ["IFieldOrProperty"] = ["Field", "Property"],
        ["IFieldOrPropertyOrIndexer"] = ["Field", "Property"],
        ["IPropertyOrIndexer"] = ["Property"],
        ["IEvent"] = ["Event"],
        ["IParameter"] = ["Parameter"],
        ["ITypeParameter"] = ["GenericParameter"],
        ["ICompilation"] = ["Assembly"],
        ["IMember"] = ["Method", "Constructor", "Property", "Field", "Event"],
        ["IMemberOrNamedType"] = ["Class", "Struct", "Method", "Constructor", "Property", "Field", "Event"],
    };

    static readonly string[] Order = ["Assembly", "Class", "Struct", "Constructor", "Method", "Property", "Field", "Event", "Parameter", "GenericParameter"];

    /// <summary>
    /// "AttributeTargets.Class | AttributeTargets.Struct" for an aspect implementing IAspect&lt;INamedType&gt;; null when a
    /// kind has no attribute target (e.g. IDeclaration) or there is none.
    /// </summary>
    public static string? AttributeTargets(IEnumerable<string> aspectKinds)
    {
        var targets = new HashSet<string>();
        foreach (var kind in aspectKinds)
        {
            if (!TargetsOfKind.TryGetValue(kind, out var t)) return null;
            targets.UnionWith(t);
        }
        return targets.Count == 0 ? null : string.Join(" | ", Order.Where(targets.Contains).Select(t => "AttributeTargets." + t));
    }

    /// <summary>Whether an aspect of these kinds can be applied to a class.</summary>
    public static bool SupportsTypes(IEnumerable<string> aspectKinds) =>
        aspectKinds.Any(k => k is "INamedType" or "IMemberOrNamedType" or "IDeclaration");

    // --- LAMA0037: eligibility conditions on modifiers -----------------------------------------------------------------

    /// <summary>A modifier change that satisfies an eligibility condition of the message, on the declaration it names.</summary>
    public sealed record ModifierChange(bool Add, string Modifier, string Subject);

    static readonly string[] Modifiers = ["static", "abstract", "virtual", "sealed", "readonly", "async", "partial"];

    /// <summary>
    /// "... because 'Calculator.Add(int, int)' must not be static." → remove `static` from 'Calculator.Add(int, int)'.
    /// Conditions on anything else (return type, accessibility, ...) give nothing.
    /// </summary>
    public static List<ModifierChange> ModifierChanges(string message)
    {
        var result = new List<ModifierChange>();
        var because = message.IndexOf(" because ", StringComparison.Ordinal);
        if (because < 0) return result;
        foreach (Match m in Regex.Matches(message.Substring(because), @"'([^']+)' must (not )?be (\w+)"))
        {
            var modifier = m.Groups[3].Value;
            if (!Modifiers.Contains(modifier)) continue;
            result.Add(new ModifierChange(!m.Groups[2].Success, modifier, m.Groups[1].Value));
        }
        return result;
    }

    // --- LAMA0115: meta.Target members ---------------------------------------------------------------------------------

    /// <summary>
    /// The meta.Target member the template uses ("Field") and what to use instead, the message's suggestion first:
    /// "... cannot use 'meta.Target.Field' ... Consider using meta.Target.FieldOrProperty instead."
    /// </summary>
    public static (string Used, List<string> Replacements)? MetaTargetReplacements(string message)
    {
        var used = Regex.Match(message, @"'meta\.Target\.(\w+)'");
        if (!used.Success) return null;
        var name = used.Groups[1].Value;
        var replacements = new List<string>();
        var suggested = Regex.Match(message, @"using meta\.Target\.(\w+)");
        if (suggested.Success) replacements.Add(suggested.Groups[1].Value);
        replacements.AddRange(name switch
        {
            "Field" or "Property" => ["FieldOrProperty", "FieldOrPropertyOrIndexer", "Member", "Declaration"],
            "Indexer" or "FieldOrProperty" => ["FieldOrPropertyOrIndexer", "Member", "Declaration"],
            "Method" or "Constructor" or "Event" or "FieldOrPropertyOrIndexer" => ["Member", "Declaration"],
            _ => ["Declaration"],
        });
        return (name, replacements.Where(r => r != name).Distinct().ToList());
    }

    // --- LAMA0114 / LAMA0287: guards -----------------------------------------------------------------------------------

    /// <summary>The meta.Target member of the advised declaration: "meta.Target.Method" for "... applied to method '...'".</summary>
    public static string TargetExpression(string message) =>
        Regex.IsMatch(message, @"applied to (the )?method\b") ? "meta.Target.Method" : "meta.Target.Member";

    /// <summary>
    /// The statement without what needs an instance, when it is an interpolation hole with the word before it
    /// ($"Executing {x} on {meta.This}." → $"Executing {x}."); null when it can't be removed so simply.
    /// </summary>
    public static string? WithoutHole(string statement, string metaMember)
    {
        var hole = Regex.Escape("{" + metaMember + "}");
        var withWord = Regex.Replace(statement, @"\s+\w+\s+" + hole, "");
        if (withWord != statement) return withWord;
        var alone = Regex.Replace(statement, @"\s*" + hole, "");
        return alone != statement ? alone : null;
    }

    /// <summary>
    /// `if ( condition ) { then } else { otherwise }` (no else when <paramref name="otherwise"/> is null) replacing a
    /// statement at <paramref name="indent"/>; the statements keep their own line breaks, indented one level deeper.
    /// </summary>
    public static string Guard(string condition, string then, string? otherwise, string indent)
    {
        string Block(string statement) =>
            $"{indent}{{\n{indent}    {statement.Replace("\n", "\n    ")}\n{indent}}}";
        var text = $"if ( {condition} )\n{Block(then)}";
        return otherwise == null ? text : $"{text}\n{indent}else\n{Block(otherwise)}";
    }

    // --- LAMA0091: aspect order -----------------------------------------------------------------------------------------

    /// <summary>
    /// The order of an [AspectOrder]'s types (indexes into the current order) where <paramref name="contributor"/>
    /// executes before every low-level aspect at compile time; null when it already does. With RunTime direction the
    /// list is in run-time order, so compile time runs it backwards.
    /// </summary>
    public static int[]? OrderBeforeWeavers(int count, int contributor, IReadOnlyCollection<int> weavers, bool runTime)
    {
        var order = Enumerable.Range(0, count).Where(i => i != contributor).ToList();
        if (runTime)
        {
            var last = weavers.Where(w => w > contributor).DefaultIfEmpty(-1).Max();
            if (last < 0) return null;
            order.Insert(order.IndexOf(last) + 1, contributor);
        }
        else
        {
            var first = weavers.Where(w => w < contributor).DefaultIfEmpty(-1).Min();
            if (first < 0) return null;
            order.Insert(order.IndexOf(first), contributor);
        }
        return order.ToArray();
    }

    // --- LAMA0661: renamed variables -----------------------------------------------------------------------------------

    /// <summary>(variable, name in the compiled code) from "The lambda parameter 'id' is renamed to 'id_1' ...".</summary>
    public static (string Name, string NewName)? Renamed(string message)
    {
        var m = Regex.Match(message, @"'([^']+)' is renamed to '([^']+)'");
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    /// <summary>"item" for "items", "entry" for "entries"; null when the name isn't a plural.</summary>
    public static string? Singular(string plural)
    {
        if (plural.Length < 3 || !plural.EndsWith("s", StringComparison.Ordinal) || plural.EndsWith("ss", StringComparison.Ordinal)) return null;
        if (plural.EndsWith("ies", StringComparison.Ordinal)) return plural.Substring(0, plural.Length - 3) + "y";
        if (Regex.IsMatch(plural, "(ches|shes|sses|xes)$")) return plural.Substring(0, plural.Length - 2);
        return plural.Substring(0, plural.Length - 1);
    }
}
