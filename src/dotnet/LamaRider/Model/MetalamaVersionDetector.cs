using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LamaRider.Model;

/// <summary>Finds the Metalama version a solution restored, from the contents of its projects' obj/project.assets.json.</summary>
public static class MetalamaVersionDetector
{
    static readonly Regex Library = new("\"Metalama\\.Framework/([^\"]+)\"", RegexOptions.Compiled);

    public static string? FromAssetsFiles(IEnumerable<string> assetsJsonContents) =>
        assetsJsonContents
            .SelectMany(json => Library.Matches(json).Cast<Match>())
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .OrderByDescending(v => Version.TryParse(v.Split('-')[0], out var parsed) ? parsed : new Version(0, 0))
            .ThenBy(v => v.Contains("-")) // release before a prerelease of the same number
            .FirstOrDefault();
}
