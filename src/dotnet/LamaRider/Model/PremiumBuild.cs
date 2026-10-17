using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LamaRider.Model;

/// <summary>
/// Metalama Premium components need a Metalama Professional license: a project that references one fails to build without
/// it (LAMA0806). Code using them can be kept out of a free build with <c>#if METALAMA_PREMIUM</c> and a project property.
/// </summary>
public static class PremiumBuild
{
    public const string Symbol = "METALAMA_PREMIUM";
    public const string Property = "MetalamaPremium";

    /// <summary>Metalama's Premium components (Architecture is built on Validation).</summary>
    static readonly string[] Components =
    [
        "Metalama.Extensions.CodeFixes", "Metalama.Extensions.Validation", "Metalama.Extensions.Architecture",
        "Metalama.Patterns.Caching.Backends.Azure", "Metalama.Patterns.Caching.Backends.Redis",
    ];

    public static bool IsPremium(string assemblyName) => Components.Contains(assemblyName);

    public static bool IsPremiumNamespace(string ns) => Components.Any(c => ns == c || ns.StartsWith(c + ".", StringComparison.Ordinal));

    static readonly Regex Directive = new(@"^\s*#\s*(if|elif|else|endif)\b(.*)$", RegexOptions.Multiline);
    static readonly Regex MentionsSymbol = new($@"(?<!!\s*)\b{Symbol}\b");

    /// <summary>Whether <paramref name="offset"/> is in an <c>#if</c> / <c>#elif</c> branch whose condition requires the symbol.</summary>
    public static bool IsGuarded(string text, int offset)
    {
        var open = new Stack<bool>(); // per #if: is the current branch guarded
        foreach (Match m in Directive.Matches(text))
        {
            if (m.Index >= offset) break;
            switch (m.Groups[1].Value)
            {
                case "if": open.Push(MentionsSymbol.IsMatch(m.Groups[2].Value)); break;
                case "elif" when open.Count > 0: open.Pop(); open.Push(MentionsSymbol.IsMatch(m.Groups[2].Value)); break;
                case "else" when open.Count > 0: open.Pop(); open.Push(false); break;
                case "endif" when open.Count > 0: open.Pop(); break;
            }
        }
        return open.Any(guarded => guarded);
    }

    /// <summary>
    /// Where to put <c>#if</c> (start of the first line) and <c>#endif</c> (end of the last line) around [start, end), when
    /// that code has its lines to itself (a trailing comment is fine).
    /// </summary>
    public static (int Before, int After)? WrapPoints(string text, int start, int end)
    {
        var before = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        if (start > 0 && text[start - 1] == '\n') before = start;
        if (text.Substring(before, start - before).Trim().Length > 0) return null;
        var after = text.IndexOf('\n', end);
        if (after < 0) after = text.Length;
        var rest = text.Substring(end, after - end).Trim();
        if (rest.Length > 0 && !rest.StartsWith("//", StringComparison.Ordinal)) return null;
        if (after > 0 && text[after - 1] == '\r') after--;
        return (before, after);
    }

    public static string? Wrap(string text, int start, int end) =>
        WrapPoints(text, start, end) is var (before, after)
            ? text.Substring(0, before) + $"#if {Symbol}\n" + text.Substring(before, after - before) + "\n#endif" + text.Substring(after)
            : null;

    static readonly Regex PremiumReference = new(
        @"(<PackageReference\s+Include=""(" + string.Join("|", Components.Select(Regex.Escape)) + @")"")(?![^>]*\bCondition=)");

    /// <summary>
    /// A project file whose free build (the default) leaves out the Premium packages and <see cref="Symbol"/>, and whose
    /// build with <c>MetalamaPremium=true</c> (property or environment variable) includes them. Null when already set up.
    /// </summary>
    public static (string Text, List<string> Packages)? SetUp(string csproj)
    {
        if (csproj.Contains(Symbol)) return null;
        var packages = PremiumReference.Matches(csproj).Cast<Match>().Select(m => m.Groups[2].Value).ToList();
        var text = PremiumReference.Replace(csproj, $"$1 Condition=\"'$({Property})' == 'true'\"");

        var anchor = text.IndexOf("<ItemGroup", StringComparison.Ordinal);
        if (anchor < 0) anchor = text.LastIndexOf("</Project>", StringComparison.Ordinal);
        var lineStart = text.LastIndexOf('\n', Math.Max(0, anchor - 1)) + 1;
        var indent = text.Substring(lineStart, anchor - lineStart);
        if (indent.Trim().Length > 0) { lineStart = anchor; indent = "  "; }
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var group =
            $"{indent}<PropertyGroup>{nl}" +
            $"{indent}  <!-- Metalama Premium components need a Metalama Professional license. Licensed builds: -p:{Property}=true or the {Property} environment variable. -->{nl}" +
            $"{indent}  <{Property} Condition=\"'$({Property})' == ''\">false</{Property}>{nl}" +
            $"{indent}  <DefineConstants Condition=\"'$({Property})' == 'true'\">$(DefineConstants);{Symbol}</DefineConstants>{nl}" +
            $"{indent}</PropertyGroup>{nl}{nl}";
        return (text.Substring(0, lineStart) + group + text.Substring(lineStart), packages);
    }
}
