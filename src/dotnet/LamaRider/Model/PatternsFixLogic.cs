using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace LamaRider.Model;

/// <summary>Pure logic of the fixes for Metalama.Patterns diagnostics (contracts, immutability, caching, observability, WPF).</summary>
public static class PatternsFixLogic
{
    /// <summary>The '...' quoted parts of a diagnostic message.</summary>
    public static List<string> Quoted(string message) =>
        Regex.Matches(message, "'([^']*)'").Cast<Match>().Select(m => m.Groups[1].Value).ToList();

    /// <summary>"GetPrice" for "ProductCatalogue.GetPrice(string)".</summary>
    public static string MemberName(string qualified)
    {
        var paren = qualified.IndexOf('(');
        var name = paren < 0 ? qualified : qualified.Substring(0, paren);
        return name.Substring(name.LastIndexOf('.') + 1);
    }

    /// <summary>"ProductCatalogue" for "ProductCatalogue.GetPrice(string)" (null without a dot).</summary>
    public static string? DeclaringTypeName(string qualified)
    {
        var paren = qualified.IndexOf('(');
        var name = paren < 0 ? qualified : qualified.Substring(0, paren);
        var dot = name.LastIndexOf('.');
        if (dot < 0) return null;
        name = name.Substring(0, dot);
        return name.Substring(name.LastIndexOf('.') + 1);
    }

    /// <summary>The parameter types of a signature, split at top-level commas: ["string", "Dictionary&lt;string, int&gt;"].</summary>
    public static List<string>? ParameterTypes(string signature)
    {
        var open = signature.IndexOf('(');
        var close = signature.LastIndexOf(')');
        if (open < 0 || close < open) return null;
        var inner = signature.Substring(open + 1, close - open - 1);
        var result = new List<string>();
        if (inner.Trim().Length == 0) return result;
        int depth = 0, start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var ch = inner[i];
            if (ch is '<' or '(' or '[') depth++;
            else if (ch is '>' or ')' or ']') depth--;
            else if (ch == ',' && depth == 0)
            {
                result.Add(inner.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }
        result.Add(inner.Substring(start).Trim());
        return result;
    }

    // --- Contracts -------------------------------------------------------------------------------------------------------

    /// <summary>The unambiguous replacements of an ambiguous inequality contract (LAMA5007): (strict, non-strict).</summary>
    public static (string Strict, string NonStrict)? InequalityAlternatives(string contract) => contract switch
    {
        "Positive" => ("StrictlyPositive", "NonNegative"),
        "Negative" => ("StrictlyNegative", "NonPositive"),
        "GreaterThan" => ("StrictlyGreaterThan", "GreaterThanOrEqual"),
        "LessThan" => ("StrictlyLessThan", "LessThanOrEqual"),
        _ => null,
    };

    sealed record Numeric(string Keyword, string Clr, double Min, double Max, int Size, bool Floating);

    static readonly Numeric[] Numerics =
    [
        new("sbyte", "SByte", sbyte.MinValue, sbyte.MaxValue, 1, false),
        new("byte", "Byte", byte.MinValue, byte.MaxValue, 1, false),
        new("short", "Int16", short.MinValue, short.MaxValue, 2, false),
        new("ushort", "UInt16", ushort.MinValue, ushort.MaxValue, 2, false),
        new("int", "Int32", int.MinValue, int.MaxValue, 4, false),
        new("uint", "UInt32", uint.MinValue, uint.MaxValue, 4, false),
        new("long", "Int64", long.MinValue, long.MaxValue, 8, false),
        new("ulong", "UInt64", ulong.MinValue, ulong.MaxValue, 8, false),
        new("float", "Single", float.MinValue, float.MaxValue, 4, true),
        new("double", "Double", double.MinValue, double.MaxValue, 8, true),
        new("decimal", "Decimal", (double)decimal.MinValue, (double)decimal.MaxValue, 16, true),
    ];

    /// <summary>The C# keyword of a numeric CLR type name ("Byte" → "byte"), else null.</summary>
    public static string? NumericKeyword(string clrName) => Numerics.FirstOrDefault(n => n.Clr == clrName || n.Keyword == clrName)?.Keyword;

    /// <summary>
    /// LAMA5001 ("the value range [1024, 65535] cannot be satisfied by the type Byte"): up to three numeric types that hold
    /// the range, at least as wide as the current one, integral types first when the current type is integral.
    /// </summary>
    public static List<string> WiderNumericTypes(string message)
    {
        // "[a, b]" includes both bounds, "]a" and "b[" exclude them.
        var match = Regex.Match(message, @"value range ([\[\]])(.+?), (.+?)([\[\]]) cannot be satisfied by the type (\w+)");
        if (!match.Success || NumericKeyword(match.Groups[5].Value) is not { } keyword) return [];
        if (!TryBound(match.Groups[2].Value, out var min) || !TryBound(match.Groups[3].Value, out var max)) return [];
        bool minIncluded = match.Groups[1].Value == "[", maxIncluded = match.Groups[4].Value == "]";
        var current = Numerics.First(n => n.Keyword == keyword);
        var fitting = Numerics.Where(n => n != current
            && (double.IsNegativeInfinity(min) || n.Min <= min) && (double.IsPositiveInfinity(max) || max <= n.Max)
            && (maxIncluded ? n.Min <= max : n.Min < max) && (minIncluded ? min <= n.Max : min < n.Max)).ToList();
        var wider = fitting.Where(n => n.Size >= current.Size).ToList();
        if (wider.Count > 0) fitting = wider;
        if (!current.Floating && fitting.Any(n => !n.Floating)) fitting = fitting.Where(n => !n.Floating).ToList();
        return fitting.Take(3).Select(n => n.Keyword).ToList();
    }

    static bool TryBound(string text, out double value)
    {
        text = text.Trim();
        if (text is "∞" or "+∞" or "Infinity" or "+Infinity") { value = double.PositiveInfinity; return true; }
        if (text is "-∞" or "-Infinity") { value = double.NegativeInfinity; return true; }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // --- Immutability ----------------------------------------------------------------------------------------------------

    /// <summary>A deeply immutable collection type for a mutable one (LAMA5022): "List&lt;string&gt;" → ImmutableArray&lt;string&gt;.</summary>
    public sealed record ImmutableCollection(string Type, string Conversion);

    public static ImmutableCollection? ImmutableCollectionFor(string type)
    {
        type = Regex.Replace(type.Trim(), @"^(global::)?System\.Collections\.(Generic\.|ObjectModel\.)?", "");
        if (type.EndsWith("[]", StringComparison.Ordinal) && !type.EndsWith("[][]", StringComparison.Ordinal))
            return new($"ImmutableArray<{type.Substring(0, type.Length - 2)}>", "ToImmutableArray");
        var match = Regex.Match(type, @"^(\w+)<(.+)>$");
        if (!match.Success) return null;
        var args = match.Groups[2].Value;
        return match.Groups[1].Value switch
        {
            "List" or "IList" or "ICollection" or "IEnumerable" or "IReadOnlyList" or "IReadOnlyCollection" or "Collection"
                or "ReadOnlyCollection" => new($"ImmutableArray<{args}>", "ToImmutableArray"),
            "Dictionary" or "IDictionary" or "IReadOnlyDictionary" or "SortedDictionary" => new($"ImmutableDictionary<{args}>", "ToImmutableDictionary"),
            "HashSet" or "ISet" or "IReadOnlySet" => new($"ImmutableHashSet<{args}>", "ToImmutableHashSet"),
            "SortedSet" => new($"ImmutableSortedSet<{args}>", "ToImmutableSortedSet"),
            _ => null,
        };
    }
}
