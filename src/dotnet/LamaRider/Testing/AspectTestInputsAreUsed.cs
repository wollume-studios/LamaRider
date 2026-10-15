using System;
using System.IO;
using JetBrains.Annotations;
using JetBrains.Application;
using JetBrains.Application.Parts;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Daemon.UsageChecking;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CodeAnnotations;
using LamaRider.Model;

namespace LamaRider.Testing;

/// <summary>
/// Code in an aspect test project is test input: Metalama's test runner compiles each file on its own, nothing in the
/// project calls it. Without this, Rider greys out every test target as unused.
/// </summary>
[ShellComponent(Instantiation.DemandAnyThreadSafe)]
public class AspectTestInputsAreUsed : IUsageInspectionsSuppressor
{
    public ImplicitUseFlags SuppressUsageInspectionsOnElement(IDeclaredElement element)
    {
        foreach (var file in element.GetSourceFiles())
            if (file.GetProject() is { } project && IsAspectTestProject(project))
                return new ImplicitUseFlags(ImplicitUseKindFlags.Default, ImplicitUseTargetFlags.WithMembers);
        return ImplicitUseFlags.Empty;
    }

    static bool IsAspectTestProject(IProject project)
    {
        var csproj = project.ProjectFileLocation.FullPath;
        try { return File.Exists(csproj) && AspectTestScaffold.IsAspectTestProject(File.ReadAllText(csproj)); }
        catch (IOException) { return false; }
    }
}
