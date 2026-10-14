using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class WorkspaceInputsTests
{
    static readonly string[] Projects = [@"C:\s\A\A.csproj", @"C:\s\B\B.fsproj", @"C:\s\C\C.csproj"];

    [Theory]
    [InlineData(@"C:\s\All.sln")]
    [InlineData(@"C:\s\Part.slnf")]
    public void Solutions_metalama_can_load_are_passed_through(string solution) =>
        Assert.Equal([solution], WorkspaceInputs.For(solution, Projects));

    [Fact]
    public void Slnx_is_replaced_by_its_csharp_projects() =>
        Assert.Equal([@"C:\s\A\A.csproj", @"C:\s\C\C.csproj"], WorkspaceInputs.For(@"C:\s\All.slnx", Projects));

    [Fact]
    public void Extension_check_ignores_case() =>
        Assert.Equal([@"C:\s\ALL.SLN"], WorkspaceInputs.For(@"C:\s\ALL.SLN", Projects));
}
