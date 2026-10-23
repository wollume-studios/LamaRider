"""Writes a Visual Studio solution file for a list of SDK-style projects, each in a folder of its own name."""

CSHARP = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}"


def write_solution(path, projects, first_guid_number=0):
    lines = ["", "Microsoft Visual Studio Solution File, Format Version 12.00", "# Visual Studio Version 17"]
    guids = []
    for i, project in enumerate(projects):
        guid = "{F1A50000-0000-4000-8000-%012X}" % (first_guid_number + i + 1)
        guids.append(guid)
        lines.append('Project("%s") = "%s", "%s\\%s.csproj", "%s"' % (CSHARP, project, project, project, guid))
        lines.append("EndProject")
    lines.append("Global")
    lines.append("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution")
    lines.append("\t\tDebug|Any CPU = Debug|Any CPU")
    lines.append("\tEndGlobalSection")
    lines.append("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution")
    for guid in guids:
        lines.append("\t\t%s.Debug|Any CPU.ActiveCfg = Debug|Any CPU" % guid)
        lines.append("\t\t%s.Debug|Any CPU.Build.0 = Debug|Any CPU" % guid)
    lines.append("\tEndGlobalSection")
    lines.append("EndGlobal")
    lines.append("")
    with open(path, "w", encoding="utf-8", newline="\n") as out:
        out.write("\n".join(lines))
