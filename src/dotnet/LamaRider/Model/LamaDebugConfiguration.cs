using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LamaRider.Model;

/// <summary>
/// Metalama's LamaDebug build configuration (defined by Metalama.Framework per project) needs a solution configuration to be
/// selectable, as the Metalama docs do with Visual Studio's Configuration Manager: copied from Debug. Projects without Metalama
/// keep building as Debug.
/// </summary>
public static class LamaDebugConfiguration
{
    public const string Name = "LamaDebug";

    public static bool Has(string solutionText) =>
        Regex.IsMatch(solutionText, @"LamaDebug\|[^=]*=|Name=""LamaDebug""");

    /// <param name="isMetalamaProject">Project path as written in the solution file.</param>
    public static string AddToSln(string sln, Func<string, bool> isMetalamaProject)
    {
        if (Has(sln)) return sln;
        var newline = sln.Contains("\r\n") ? "\r\n" : "\n";
        var projectPaths = Regex.Matches(sln, @"^Project\(""[^""]*""\)\s*=\s*""[^""]*"",\s*""([^""]*)"",\s*""(\{[^}]*\})""", RegexOptions.Multiline)
            .Cast<Match>()
            .ToDictionary(m => m.Groups[2].Value, m => m.Groups[1].Value, StringComparer.OrdinalIgnoreCase);

        var lines = sln.Split(new[] { newline }, StringSplitOptions.None).ToList();
        var result = new List<string>();
        foreach (var line in lines)
        {
            result.Add(line);
            var solutionConfig = Regex.Match(line, @"^(\s*)Debug\|([^=]+?) = Debug\|(.+)$");
            if (solutionConfig.Success)
            {
                result.Add($"{solutionConfig.Groups[1].Value}LamaDebug|{solutionConfig.Groups[2].Value} = LamaDebug|{solutionConfig.Groups[3].Value}");
                continue;
            }
            var projectConfig = Regex.Match(line, @"^(\s*)(\{[^}]*\})\.Debug\|([^.]+)\.(ActiveCfg|Build\.0|Deploy\.0) = Debug\|(.+)$");
            if (projectConfig.Success)
            {
                var guid = projectConfig.Groups[2].Value;
                var target = projectPaths.TryGetValue(guid, out var path) && isMetalamaProject(path) ? Name : "Debug";
                result.Add($"{projectConfig.Groups[1].Value}{guid}.LamaDebug|{projectConfig.Groups[3].Value}.{projectConfig.Groups[4].Value} = {target}|{projectConfig.Groups[5].Value}");
            }
        }
        return string.Join(newline, result);
    }

    public static string AddToSlnx(string slnx, Func<string, bool> isMetalamaProject)
    {
        if (Has(slnx)) return slnx;
        var document = XDocument.Parse(slnx, LoadOptions.PreserveWhitespace);
        var solution = document.Root!;
        var configurations = solution.Element("Configurations");
        if (configurations == null)
        {
            // Without <Configurations> a .slnx has Debug and Release; listing any build type replaces those defaults.
            configurations = new XElement("Configurations",
                new XElement("BuildType", new XAttribute("Name", "Debug")),
                new XElement("BuildType", new XAttribute("Name", "Release")));
            solution.AddFirst(configurations);
        }
        configurations.Add(new XElement("BuildType", new XAttribute("Name", Name)));

        foreach (var project in solution.Descendants("Project"))
        {
            if (project.Attribute("Path")?.Value is not { } path || isMetalamaProject(path)) continue;
            project.Add(new XElement("BuildType", new XAttribute("Solution", "LamaDebug|*"), new XAttribute("Project", "Debug")));
        }

        // Re-indent the touched elements; the rest of the file keeps its formatting.
        var text = document.Root!.ToString(SaveOptions.None);
        return text;
    }
}
