using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class RefreshTriggerTests
{
    [Theory]
    [InlineData(@"C:\s\EFA\Class1.cs", RefreshTrigger.Kind.Source)]
    [InlineData(@"C:\s\EFA\EFA.csproj", RefreshTrigger.Kind.Source)]
    [InlineData(@"C:\s\Directory.Build.props", RefreshTrigger.Kind.Source)]
    [InlineData(@"C:\s\Directory.Packages.props", RefreshTrigger.Kind.Source)]
    [InlineData(@"C:\s\EFA\obj\project.assets.json", RefreshTrigger.Kind.Restore)]
    [InlineData(@"C:\s\EFA\obj\Debug\net8.0\EFA.GlobalUsings.g.cs", RefreshTrigger.Kind.None)]
    [InlineData(@"C:\s\EFA\bin\Debug\net8.0\EFA.dll", RefreshTrigger.Kind.None)]
    [InlineData(@"C:\s\.idea\workspace.xml", RefreshTrigger.Kind.None)]
    [InlineData(@"C:\s\.git\index", RefreshTrigger.Kind.None)]
    [InlineData(@"C:\s\EFA\readme.md", RefreshTrigger.Kind.None)]
    [InlineData(@"C:\s\EFA\Class1.cs~RF1a2b.TMP", RefreshTrigger.Kind.None)]
    public void Classifies_changed_paths(string path, RefreshTrigger.Kind expected) =>
        Assert.Equal(expected, RefreshTrigger.Classify(path));
}
