using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LamaRider.Model;

/// <summary>
/// Pure logic of the project-level, architecture, interceptor and analyzer quick-fixes: reading names out of Metalama's
/// messages, computing new names, patterns and literals, and project file edits.
/// </summary>
public static class ProjectFixLogic
{
    /// <summary>The '…'-quoted parts of a message, in order.</summary>
    public static List<string> Quoted(string message) =>
        Regex.Matches(message, "'([^']*)'").Cast<Match>().Select(m => m.Groups[1].Value).ToList();

    /// <summary>The "…"-quoted naming pattern of LAMA0903/0906 ("*Repository", "^.*Reader$").</summary>
    public static string? Pattern(string message) =>
        Regex.Match(message, "\"(?<p>[^\"]+)\" pattern") is { Success: true } m ? m.Groups["p"].Value : null;

    // --- C# language version (LAMA0051/0052/0282) --------------------------------------------------------------------

    /// <summary>The versions listed after "supported values:", newest first, as LangVersion values ("12", "11.0").</summary>
    public static List<string> SupportedVersions(string message)
    {
        var list = Regex.Match(message, @"supported values:\s*(?<v>.+?)\.(\s|$)");
        if (!list.Success) return [];
        return Regex.Split(list.Groups["v"].Value, @"[,;]|\s+or\s+|\s+and\s+")
            .Select(v => v.Trim().Trim('\'', '"', '`'))
            .Select(v => v.StartsWith("CSharp", StringComparison.OrdinalIgnoreCase) ? v.Substring(6).Replace('_', '.') : v)
            .Where(v => Regex.IsMatch(v, @"^[A-Za-z0-9.]+$"))
            .Distinct()
            .OrderByDescending(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.MinValue)
            .ToList();
    }

    /// <summary>The MSBuild property LAMA0052 names ("Change the &lt;LangVersion&gt; property"), LangVersion by default.</summary>
    public static string LanguageVersionProperty(string message) =>
        Regex.Match(message, @"Change the <?(?<p>\w+)>? property") is { Success: true } m ? m.Groups["p"].Value : "LangVersion";

    /// <summary>The C# version an aspect needs (LAMA0282: "uses features of C# 11").</summary>
    public static string? RequiredLanguageVersion(string message) =>
        Regex.Match(message, @"uses features of C# (?<v>[\d.]+\d)") is { Success: true } m ? m.Groups["v"].Value : null;

    /// <summary>The timeout property LAMA0083 names.</summary>
    public static string TimeoutProperty(string message) =>
        Regex.Match(message, @"setting the '(?<p>\w+)' MSBuild property") is { Success: true } m ? m.Groups["p"].Value : "MetalamaReferenceAssemblyRestoreTimeout";

    // --- Aspect order (LAMA0035) ---------------------------------------------------------------------------------------

    /// <summary>
    /// [assembly: AspectOrder(…)] ordering <paramref name="layers"/> ("Ns.Aspect" or "Ns.Aspect:Layer"): typeof() for
    /// aspect classes, layer names as strings when a layer is named.
    /// </summary>
    public static string AspectOrderAttribute(string direction, IReadOnlyList<string> layers)
    {
        var named = layers.Any(l => l.Contains(":"));
        var items = layers.Select(l => named ? CSharpString(l) : $"typeof({l.Replace('+', '.')})");
        return $"[assembly: AspectOrder( AspectOrderDirection.{direction}, {string.Join(", ", items)} )]";
    }

    // --- Naming conventions (LAMA0902/0903/0906) ---------------------------------------------------------------------

    /// <summary>The regular expression equivalent to a star pattern ("*Service" → "^.*Service$").</summary>
    public static string StarToRegex(string star) => "^" + string.Join(".*", star.Split('*').Select(Regex.Escape)) + "$";

    /// <summary>
    /// New names for <paramref name="name"/> that match a "*Suffix"/"Prefix*" pattern (or its regex form "^.*Suffix$"):
    /// the last (first) word replaced, then the affix added. Empty when the pattern isn't of that form.
    /// </summary>
    public static List<string> NamesMatching(string name, string pattern)
    {
        var p = pattern;
        var regex = p.StartsWith("^") || p.EndsWith("$") || p.Contains(".*");
        if (regex) p = p.TrimStart('^').TrimEnd('$').Replace(".*", "*");
        string? suffix = null, prefix = null;
        if (p.StartsWith("*") && p.IndexOf('*', 1) < 0) suffix = p.Substring(1);
        else if (p.EndsWith("*") && p.IndexOf('*') == p.Length - 1) prefix = p.Substring(0, p.Length - 1);
        var affix = suffix ?? prefix;
        if (string.IsNullOrEmpty(affix)) return [];
        if (regex)
        {
            try { affix = Regex.Unescape(affix!); }
            catch (ArgumentException) { return []; }
        }
        if (!Regex.IsMatch(affix!, @"^\w+$")) return [];

        var words = Regex.Split(name, @"(?<!^)(?=\p{Lu})");
        var names = new List<string>();
        if (suffix != null)
        {
            if (words.Length > 1) names.Add(string.Concat(words.Take(words.Length - 1)) + affix);
            names.Add(name + affix);
        }
        else
        {
            if (words.Length > 1) names.Add(affix + string.Concat(words.Skip(1)));
            names.Add(affix + name);
        }
        return names.Where(n => n != name).Distinct().ToList();
    }

    // --- Literals ------------------------------------------------------------------------------------------------------

    /// <summary>A regular C# string literal for <paramref name="value"/>.</summary>
    public static string CSharpString(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

    /// <summary>
    /// A raw string literal (C# 11, """…""" or $"""…""") rewritten as a regular one; null when it isn't one, or when it is
    /// interpolated with several $ (its braces would need rewriting).
    /// </summary>
    public static string? RawToRegular(string literal)
    {
        var dollars = 0;
        while (dollars < literal.Length && literal[dollars] == '$') dollars++;
        if (dollars > 1) return null;
        var rest = literal.Substring(dollars);
        var suffix = "";
        if (rest.EndsWith("u8", StringComparison.OrdinalIgnoreCase))
        {
            suffix = rest.Substring(rest.Length - 2);
            rest = rest.Substring(0, rest.Length - 2);
        }
        var quotes = 0;
        while (quotes < rest.Length && rest[quotes] == '"') quotes++;
        if (quotes < 3 || rest.Length < 2 * quotes || !rest.EndsWith(new string('"', quotes))) return null;
        var content = rest.Substring(quotes, rest.Length - 2 * quotes);

        string value;
        if (content.Contains("\n"))
        {
            // Multi-line: the content is between the opening and the closing line, less the closing line's indentation.
            var nl = content.Contains("\r\n") ? "\r\n" : "\n";
            var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count < 2 || lines[0].Trim().Length > 0 || lines[lines.Count - 1].Trim().Length > 0) return null;
            var indent = lines[lines.Count - 1];
            value = string.Join(nl, lines.Skip(1).Take(lines.Count - 2)
                .Select(l => l.StartsWith(indent, StringComparison.Ordinal) ? l.Substring(indent.Length) : l.Trim().Length == 0 ? "" : l));
        }
        else value = content;

        var result = new StringBuilder(dollars == 1 ? "$\"" : "\"");
        var depth = 0;
        foreach (var ch in value)
        {
            if (dollars == 1 && ch == '{') depth++;
            if (depth > 0) result.Append(ch);
            else
                result.Append(ch switch
                {
                    '\\' => "\\\\",
                    '"' => "\\\"",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => ch.ToString(),
                });
            if (dollars == 1 && ch == '}' && depth > 0) depth--;
        }
        return result.Append('"').Append(suffix).ToString();
    }

    // --- Files named in messages (crash reports, stack traces) --------------------------------------------------------

    /// <summary>Absolute paths quoted in a message ('C:\…\exception-….txt', '/tmp/…').</summary>
    public static List<string> Paths(string message) =>
        Regex.Matches(message, @"'(?<p>(?:[A-Za-z]:[\\/]|\\\\|/)[^'<>|]+)'").Cast<Match>().Select(m => m.Groups["p"].Value.TrimEnd('.')).Distinct().ToList();

    // --- Project file edits --------------------------------------------------------------------------------------------

    static readonly Regex DefineConstants = new(@"<DefineConstants(\s[^>]*)?>(?<v>[^<]*)</DefineConstants>");

    /// <summary>Whether the project assigns DefineConstants without keeping the inherited value (LAMA0053/0080).</summary>
    public static bool OverwritesDefineConstants(string project) =>
        DefineConstants.Matches(project).Cast<Match>().Any(m => !m.Groups["v"].Value.Contains("$(DefineConstants)"));

    /// <summary>Every DefineConstants assignment appends to $(DefineConstants) instead of replacing it.</summary>
    public static string AppendDefineConstants(string project) =>
        DefineConstants.Replace(project, m =>
        {
            var value = m.Groups["v"];
            if (value.Value.Contains("$(DefineConstants)")) return m.Value;
            var appended = value.Value.Trim().Length == 0 ? "$(DefineConstants)" : "$(DefineConstants);" + value.Value.TrimStart(';');
            return m.Value.Substring(0, value.Index - m.Index) + appended + m.Value.Substring(value.Index - m.Index + value.Length);
        });

    /// <summary>Whether the project file sets <paramref name="property"/> itself (not in an imported file).</summary>
    public static bool HasProperty(string project, string property) =>
        Regex.IsMatch(project, $@"<{Regex.Escape(property)}(\s[^>]*)?>");

    /// <summary>Adds an item (e.g. &lt;MetalamaImmutableType Include="Ns.Type" /&gt;) in an ItemGroup of its own.</summary>
    public static string AddItem(string project, string item, string include)
    {
        if (Regex.IsMatch(project, $@"<{Regex.Escape(item)}\s+Include=""{Regex.Escape(include)}""")) return project;
        var nl = project.Contains("\r\n") ? "\r\n" : "\n";
        var end = project.LastIndexOf("</Project>", StringComparison.Ordinal);
        if (end < 0) return project;
        var escaped = include.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;");
        return project.Substring(0, end) + $"  <ItemGroup>{nl}    <{item} Include=\"{escaped}\" />{nl}  </ItemGroup>{nl}" + project.Substring(end);
    }

    // --- Names close to a misspelled one (LAMA1008) ------------------------------------------------------------------

    /// <summary>The candidates closest to <paramref name="name"/> (edit distance, case-insensitive), at most three.</summary>
    public static List<string> Closest(string name, IEnumerable<string> candidates)
    {
        var limit = Math.Max(2, name.Length / 2);
        return candidates.Distinct()
            .Select(c => (Name: c, Distance: Distance(name.ToLowerInvariant(), c.ToLowerInvariant())))
            .Where(c => c.Distance <= limit && c.Name != name)
            .OrderBy(c => c.Distance).ThenBy(c => c.Name, StringComparer.Ordinal)
            .Take(3).Select(c => c.Name).ToList();
    }

    static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[b.Length];
    }
}
