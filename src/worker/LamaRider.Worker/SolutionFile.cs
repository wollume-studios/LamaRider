using System.Security.Cryptography;
using System.Text;

namespace LamaRider.Worker;

/// <summary>
/// Classic .sln for a list of projects. Loading several .csproj files one by one fails when a later one is already
/// in the workspace as a reference of an earlier one; a solution load de-duplicates. Used for .slnx solutions.
/// </summary>
public static class SolutionFile
{
    const string SdkProjectType = "9A19103F-16F7-4668-BE54-9A1E7A4F7556";

    public static string Write(string directory, IEnumerable<string> projects)
    {
        var entries = projects.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => (Path: p, Name: Path.GetFileNameWithoutExtension(p), Guid: GuidFor(p))).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
        foreach (var e in entries)
            sb.AppendLine($"Project(\"{{{SdkProjectType}}}\") = \"{e.Name}\", \"{e.Path}\", \"{{{e.Guid}}}\"").AppendLine("EndProject");
        sb.AppendLine("Global");
        sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        sb.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU");
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
        foreach (var e in entries)
        {
            sb.AppendLine($"\t\t{{{e.Guid}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
            sb.AppendLine($"\t\t{{{e.Guid}}}.Debug|Any CPU.Build.0 = Debug|Any CPU");
        }
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("EndGlobal");

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "inputs.sln");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    static string GuidFor(string path) =>
        new Guid(MD5.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).ToString("D").ToUpperInvariant();
}
