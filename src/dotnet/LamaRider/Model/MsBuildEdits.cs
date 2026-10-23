using System;
using System.Text.RegularExpressions;

namespace LamaRider.Model;

/// <summary>Project file edits for quick-fixes, as text: the file keeps its formatting.</summary>
public static class MsBuildEdits
{
    static Regex Element(string name) => new($@"<{Regex.Escape(name)}(\s[^>]*)?>(?<value>[^<]*)</{Regex.Escape(name)}>");

    /// <summary>Sets the first unconditional occurrence of the property, else adds it to the first property group.</summary>
    public static string SetProperty(string project, string name, string value)
    {
        var existing = Element(name).Match(project);
        if (existing.Success)
        {
            var group = existing.Groups["value"];
            return project.Substring(0, group.Index) + value + project.Substring(group.Index + group.Length);
        }
        var nl = project.Contains("\r\n") ? "\r\n" : "\n";
        var close = project.IndexOf("</PropertyGroup>", StringComparison.Ordinal);
        if (close < 0)
        {
            var end = project.LastIndexOf("</Project>", StringComparison.Ordinal);
            return project.Substring(0, end) + $"  <PropertyGroup>{nl}    <{name}>{value}</{name}>{nl}  </PropertyGroup>{nl}" + project.Substring(end);
        }
        var lineStart = project.LastIndexOf('\n', close) + 1;
        var indent = project.Substring(lineStart, close - lineStart);
        return project.Substring(0, lineStart) + $"{indent}  <{name}>{value}</{name}>{nl}" + project.Substring(lineStart);
    }

    public static string RemoveProperty(string project, string name)
    {
        var existing = Element(name).Match(project);
        if (!existing.Success) return project;
        var lineStart = project.LastIndexOf('\n', existing.Index) + 1;
        var lineEnd = project.IndexOf('\n', existing.Index + existing.Length);
        var wholeLine = project.Substring(lineStart, existing.Index - lineStart).Trim().Length == 0
                        && (lineEnd < 0 ? project.Substring(existing.Index + existing.Length) : project.Substring(existing.Index + existing.Length, lineEnd - existing.Index - existing.Length)).Trim().Length == 0;
        return wholeLine
            ? project.Substring(0, lineStart) + (lineEnd < 0 ? "" : project.Substring(lineEnd + 1))
            : project.Substring(0, existing.Index) + project.Substring(existing.Index + existing.Length);
    }

    /// <summary>Adds a warning id to NoWarn, keeping the ids set elsewhere ($(NoWarn)).</summary>
    public static string AddNoWarn(string project, string id)
    {
        var existing = Element("NoWarn").Match(project);
        if (!existing.Success) return SetProperty(project, "NoWarn", "$(NoWarn);" + id);
        var value = existing.Groups["value"].Value;
        return Array.Exists(value.Split(';', ',', ' '), v => v.Trim() == id) ? project : SetProperty(project, "NoWarn", value + ";" + id);
    }
}
