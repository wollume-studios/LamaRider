using System;
using System.IO;
using System.Text.RegularExpressions;

namespace LamaRider.Model;

/// <summary>
/// Adds/removes our labelled PropertyGroup setting MetalamaDebugTransformedCode in the solution's Directory.Build.props,
/// leaving the rest of the file untouched.
/// </summary>
public static class DebugTransformedCodeProps
{
    public const string FileName = "Directory.Build.props";

    const string Group =
        "  <PropertyGroup Label=\"LamaRider\">\n" +
        "    <!-- Added by LamaRider 'Debug Transformed Code': debug Metalama-transformed code (obj/<Configuration>/<TFM>/metalama) -->\n" +
        "    <MetalamaDebugTransformedCode>true</MetalamaDebugTransformedCode>\n" +
        "  </PropertyGroup>\n";

    static readonly Regex OurGroup = new("[ \\t]*<PropertyGroup Label=\"LamaRider\">.*?</PropertyGroup>\\r?\\n?", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex EmptyProject = new(@"^\s*<Project>\s*</Project>\s*$", RegexOptions.Compiled);

    public static bool IsEnabled(string? text) => text != null && OurGroup.IsMatch(text);

    public static string Enable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "<Project>\n" + Group + "</Project>\n";
        if (IsEnabled(text)) return text!;
        var end = text!.LastIndexOf("</Project>", StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException($"{FileName} has no </Project> element");
        return text.Substring(0, end) + Group + text.Substring(end);
    }

    /// <summary>New content, or null when nothing but our group was in the file (delete it).</summary>
    public static string? Disable(string text)
    {
        var result = OurGroup.Replace(text, "");
        return EmptyProject.IsMatch(result) ? null : result;
    }
}
