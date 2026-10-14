using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LamaRider.Model;

/// <summary>Finds the Metalama version a solution restored, from the contents of its projects' obj/project.assets.json.</summary>
public static class MetalamaVersionDetector
{
    static readonly Regex Library = new("\"Metalama\\.Framework/([^\"]+)\"", RegexOptions.Compiled);

    // The version ends up in a dotnet command line and a cache path, and comes from files of the opened
    // (possibly untrusted) solution: accept plain NuGet versions only.
    static readonly Regex SafeVersion = new(@"^\d+(\.\d+){1,3}(-[0-9A-Za-z.]+)?$", RegexOptions.Compiled);

    public static bool IsSafe(string version) => SafeVersion.IsMatch(version);

    public static string? FromAssetsFiles(IEnumerable<string> assetsJsonContents) =>
        assetsJsonContents
            .SelectMany(json => Library.Matches(json).Cast<Match>())
            .Select(m => m.Groups[1].Value)
            .Where(IsSafe)
            .Distinct()
            .OrderByDescending(v => Version.TryParse(v.Split('-')[0], out var parsed) ? parsed : new Version(0, 0))
            .ThenBy(v => v.Contains("-")) // release before a prerelease of the same number
            .FirstOrDefault();
}
