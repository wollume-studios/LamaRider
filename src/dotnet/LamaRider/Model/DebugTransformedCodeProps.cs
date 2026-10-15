using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LamaRider.Model;

/// <summary>
/// Our labelled PropertyGroup in the solution's Directory.Build.props (LamaRider settings that are MSBuild properties),
/// leaving the rest of the file untouched: MetalamaDebugTransformedCode and MetalamaFormatOutput.
/// </summary>
public static class DebugTransformedCodeProps
{
    public const string FileName = "Directory.Build.props";

    /// <param name="DebugTransformedCode">Debug Metalama-transformed code in every configuration.</param>
    /// <param name="FormatOutput">False: Metalama does not format transformed code when debugging it (faster builds, no LAMA0066).</param>
    public sealed record Options(bool DebugTransformedCode, bool FormatOutput = true);

    static readonly Regex OurGroup = new("[ \\t]*<PropertyGroup Label=\"LamaRider\">.*?</PropertyGroup>\\r?\\n?", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex EmptyProject = new(@"^\s*<Project>\s*</Project>\s*$", RegexOptions.Compiled);

    public static bool IsEnabled(string? text) => Read(text).DebugTransformedCode;

    public static Options Read(string? text)
    {
        var group = text == null ? null : OurGroup.Match(text) is { Success: true } m ? m.Value : null;
        bool? Value(string property) =>
            group != null && Regex.Match(group, $"<{property}>\\s*(true|false)\\s*</{property}>", RegexOptions.IgnoreCase) is { Success: true } v
                ? v.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase)
                : null;
        // Older versions wrote the group with MetalamaDebugTransformedCode only.
        return new Options(Value("MetalamaDebugTransformedCode") ?? false, Value("MetalamaFormatOutput") ?? true);
    }

    /// <summary>New content, or null when the file would hold nothing but an empty project (delete it).</summary>
    public static string? Write(string? text, Options options)
    {
        var without = text == null ? "" : OurGroup.Replace(text, "");
        var group = Group(options);
        if (group == null)
            return string.IsNullOrWhiteSpace(without) || EmptyProject.IsMatch(without) ? null : without;
        if (string.IsNullOrWhiteSpace(without)) return "<Project>\n" + group + "</Project>\n";
        var end = without.LastIndexOf("</Project>", StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException($"{FileName} has no </Project> element");
        return without.Substring(0, end) + group + without.Substring(end);
    }

    public static string Enable(string? text) => Write(text, Read(text) with { DebugTransformedCode = true })!;

    /// <summary>New content, or null when nothing but our group was in the file (delete it).</summary>
    public static string? Disable(string text) => Write(text, Read(text) with { DebugTransformedCode = false });

    static string? Group(Options options)
    {
        if (!options.DebugTransformedCode && options.FormatOutput) return null;
        var sb = new StringBuilder("  <PropertyGroup Label=\"LamaRider\">\n");
        sb.Append("    <!-- Added by LamaRider (Settings > Tools > LamaRider) -->\n");
        if (options.DebugTransformedCode)
            sb.Append("    <!-- Debug Metalama-transformed code (obj/<Configuration>/<TFM>/metalama) -->\n")
              .Append("    <MetalamaDebugTransformedCode>true</MetalamaDebugTransformedCode>\n");
        if (!options.FormatOutput)
            sb.Append("    <!-- Don't format transformed code: faster builds (LAMA0066), less readable transformed files -->\n")
              .Append("    <MetalamaFormatOutput>false</MetalamaFormatOutput>\n");
        return sb.Append("  </PropertyGroup>\n").ToString();
    }
}
