using System;
using System.IO;
using System.Linq;

namespace LamaRider.Model;

/// <summary>Which file-system changes in the solution folder should refresh the aspect model.</summary>
public static class RefreshTrigger
{
    public enum Kind
    {
        None,
        /// <summary>Code or project file saved.</summary>
        Source,
        /// <summary>NuGet restore result (packages added/removed). The worker's own restore also writes it.</summary>
        Restore,
    }

    static readonly string[] IgnoredFolders = ["bin", "obj", ".git", ".idea", ".vs", "node_modules"];

    public static Kind Classify(string path)
    {
        var name = Path.GetFileName(path);
        var folders = Path.GetDirectoryName(path)?.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? [];

        if (name.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase)) return Kind.Restore;
        if (folders.Any(f => IgnoredFolders.Contains(f, StringComparer.OrdinalIgnoreCase))) return Kind.None;

        var extension = Path.GetExtension(name);
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            return Kind.Source;
        if (name.StartsWith("Directory.", StringComparison.OrdinalIgnoreCase)
            && (extension.Equals(".props", StringComparison.OrdinalIgnoreCase) || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)))
            return Kind.Source;
        return Kind.None;
    }
}
