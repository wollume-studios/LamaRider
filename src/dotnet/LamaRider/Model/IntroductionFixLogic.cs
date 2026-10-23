using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LamaRider.Model;

/// <summary>
/// Message parsing and naming for the quick-fixes of introduction diagnostics (LAMA05xx): which aspect, member, type and
/// accessor a message names.
/// </summary>
public static class IntroductionFixLogic
{
    /// <summary>The texts in single quotes, in order.</summary>
    public static List<string> Quoted(string message) =>
        Regex.Matches(message, "'([^']*)'").Cast<Match>().Select(m => m.Groups[1].Value).ToList();

    /// <summary>"Resettable" in "The aspect 'Resettable' cannot ...".</summary>
    public static string? AspectName(string message) =>
        Regex.Match(message, @"\baspect '([^']+)'") is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The first quoted text after <paramref name="phrase"/> ("already defined in type" → "Cart").</summary>
    public static string? QuotedAfter(string message, string phrase)
    {
        var at = message.IndexOf(phrase, System.StringComparison.Ordinal);
        return at < 0 ? null : Regex.Match(message.Substring(at + phrase.Length), "'([^']*)'") is { Success: true } m ? m.Groups[1].Value : null;
    }

    /// <summary>"Reset" in "ShoppingCart.Reset()", "this" in "Catalog.this[int]", "Version" in "Document.Version".</summary>
    public static string MemberName(string qualified)
    {
        var name = WithoutParameters(qualified);
        return name.Substring(name.LastIndexOf('.') + 1);
    }

    /// <summary>"ShoppingCart" in "ShoppingCart.Reset()" (the segment before the member), null without one.</summary>
    public static string? DeclaringType(string qualified)
    {
        var name = WithoutParameters(qualified);
        var dot = name.LastIndexOf('.');
        if (dot < 0) return null;
        var type = name.Substring(0, dot);
        return type.Substring(type.LastIndexOf('.') + 1);
    }

    /// <summary>Parameter count of "M(int, string)" (2), "this[int]" (1); null when the member has no parameter list.</summary>
    public static int? ParameterCount(string qualified)
    {
        var open = qualified.IndexOfAny(['(', '[']);
        if (open < 0) return null;
        var inner = qualified.Substring(open + 1, qualified.Length - open - 2);
        if (inner.Trim().Length == 0) return 0;
        int depth = 0, count = 1;
        foreach (var ch in inner)
        {
            if (ch is '<' or '(' or '[') depth++;
            else if (ch is '>' or ')' or ']') depth--;
            else if (ch == ',' && depth == 0) count++;
        }
        return count;
    }

    /// <summary>New name for an existing member that clashes with an introduced one of the other staticity.</summary>
    public static string RenamedExisting(string name, bool isStatic) => name + (isStatic ? "Type" : "Instance");

    /// <summary>New name for an introduced type that clashes with an existing one: the aspect's name when it differs.</summary>
    public static string IntroducedTypeName(string existing, string? aspectName) =>
        !string.IsNullOrEmpty(aspectName) && aspectName != existing ? aspectName! : existing + "2";

    /// <summary>"{ get; set; }": an automatic property (always with a getter) with the missing accessor; it decides set vs init.</summary>
    public static string AutoPropertyAccessors(IEnumerable<string> existing, string missing)
    {
        var writer = missing is "set" or "init" ? missing : existing.FirstOrDefault(k => k is "set" or "init");
        return writer == null ? "{ get; }" : $"{{ get; {writer}; }}";
    }

    /// <summary>"set" for the accessor "private set { }", "get" for "get => x;".</summary>
    public static string? AccessorKind(string accessor) =>
        Regex.Match(accessor, @"\b(get|set|init|add|remove)\b") is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The member an advice argument names: "Describe" for nameof(Describe), nameof(this.Describe) and "Describe".</summary>
    public static string? ReferencedName(string argument)
    {
        var text = argument.Trim();
        if (Regex.Match(text, @"^nameof\s*\(\s*(?:[\w.]*\.)?(\w+)\s*\)$") is { Success: true } n) return n.Groups[1].Value;
        if (Regex.Match(text, "^\"(\\w+)\"$") is { Success: true } s) return s.Groups[1].Value;
        return null;
    }

    /// <summary>The value of an argument (or attribute property) that sets a strategy: true for "OverrideStrategy.New".</summary>
    public static bool IsStrategy(string argument) =>
        Regex.IsMatch(argument.Trim(), @"^(?:[\w.]*\.)?(?:Interface)?(?:Member)?OverrideStrategy\.\w+$");

    static string WithoutParameters(string qualified)
    {
        var open = qualified.IndexOfAny(['(', '[']);
        return open < 0 ? qualified : qualified.Substring(0, open);
    }
}
