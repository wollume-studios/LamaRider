using LamaRider.Model;
using Xunit;

namespace LamaRider.Model.Tests;

public class LamaDebugConfigurationTests
{
    const string Sln = """
        Microsoft Visual Studio Solution File, Format Version 12.00
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "Lib\Lib.csproj", "{11111111-1111-1111-1111-111111111111}"
        EndProject
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Plain", "Plain\Plain.csproj", "{22222222-2222-2222-2222-222222222222}"
        EndProject
        Global
        	GlobalSection(SolutionConfigurationPlatforms) = preSolution
        		Debug|Any CPU = Debug|Any CPU
        		Release|Any CPU = Release|Any CPU
        	EndGlobalSection
        	GlobalSection(ProjectConfigurationPlatforms) = postSolution
        		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
        		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
        	EndGlobalSection
        EndGlobal
        """;

    static bool IsMetalama(string projectPath) => projectPath.Contains("Lib");

    [Fact]
    public void Sln_gets_a_lama_debug_configuration_copied_from_debug()
    {
        var result = LamaDebugConfiguration.AddToSln(Sln, IsMetalama);
        Assert.Contains("\t\tLamaDebug|Any CPU = LamaDebug|Any CPU", result);
        Assert.Contains("{11111111-1111-1111-1111-111111111111}.LamaDebug|Any CPU.ActiveCfg = LamaDebug|Any CPU", result);
        Assert.Contains("{11111111-1111-1111-1111-111111111111}.LamaDebug|Any CPU.Build.0 = LamaDebug|Any CPU", result);
        // Projects without Metalama keep their Debug build (DEBUG constant etc.).
        Assert.Contains("{22222222-2222-2222-2222-222222222222}.LamaDebug|Any CPU.ActiveCfg = Debug|Any CPU", result);
        Assert.True(LamaDebugConfiguration.Has(result));
        Assert.False(LamaDebugConfiguration.Has(Sln));
    }

    [Fact]
    public void Sln_is_left_alone_when_it_already_has_lama_debug()
    {
        var once = LamaDebugConfiguration.AddToSln(Sln, IsMetalama);
        Assert.Equal(once, LamaDebugConfiguration.AddToSln(once, IsMetalama));
    }

    [Fact]
    public void Slnx_without_configurations_keeps_debug_and_release()
    {
        const string slnx = """
            <Solution>
              <Project Path="Lib/Lib.csproj" />
              <Project Path="Plain/Plain.csproj" />
            </Solution>
            """;
        var result = LamaDebugConfiguration.AddToSlnx(slnx, IsMetalama);
        Assert.Contains("<BuildType Name=\"Debug\" />", result);
        Assert.Contains("<BuildType Name=\"Release\" />", result);
        Assert.Contains("<BuildType Name=\"LamaDebug\" />", result);
        Assert.Contains("<Project Path=\"Lib/Lib.csproj\" />", result);
        Assert.Contains("<BuildType Solution=\"LamaDebug|*\" Project=\"Debug\" />", result);
        Assert.True(LamaDebugConfiguration.Has(result));
    }

    [Fact]
    public void Slnx_with_configurations_gets_one_more_build_type()
    {
        const string slnx = """
            <Solution>
              <Configurations>
                <BuildType Name="Debug" />
                <BuildType Name="Staging" />
              </Configurations>
              <Project Path="Lib/Lib.csproj" />
            </Solution>
            """;
        var result = LamaDebugConfiguration.AddToSlnx(slnx, IsMetalama);
        Assert.Contains("<BuildType Name=\"Staging\" />", result);
        Assert.DoesNotContain("Release", result);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, "Name=\"LamaDebug\""));
    }
}
