using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LamaRider.Model;

/// <summary>What to hand Metalama Workspaces: it accepts .sln, .slnf and .csproj only (no .slnx).</summary>
public static class WorkspaceInputs
{
    public static IReadOnlyList<string> For(string solutionPath, IEnumerable<string> projectPaths)
    {
        var extension = Path.GetExtension(solutionPath);
        if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) || extension.Equals(".slnf", StringComparison.OrdinalIgnoreCase))
            return new[] { solutionPath };
        return projectPaths.Where(p => Path.GetExtension(p).Equals(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
