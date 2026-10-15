using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class AspectTestScaffoldTests
{
    [Theory]
    [InlineData("IMethod", AspectTestScaffold.TargetKind.Method)]
    [InlineData("INamedType", AspectTestScaffold.TargetKind.Type)]
    [InlineData("IFieldOrProperty", AspectTestScaffold.TargetKind.FieldOrProperty)]
    [InlineData("IProperty", AspectTestScaffold.TargetKind.FieldOrProperty)]
    [InlineData("IFieldOrPropertyOrIndexer", AspectTestScaffold.TargetKind.FieldOrProperty)]
    [InlineData("IParameter", AspectTestScaffold.TargetKind.Parameter)]
    [InlineData("IConstructor", AspectTestScaffold.TargetKind.Constructor)]
    [InlineData("IEvent", AspectTestScaffold.TargetKind.Event)]
    [InlineData("IDeclaration", AspectTestScaffold.TargetKind.Type)]
    public void Kind_follows_the_IAspect_type_argument(string typeArgument, AspectTestScaffold.TargetKind kind) =>
        Assert.Equal(kind, AspectTestScaffold.KindOf(typeArgument));

    [Fact]
    public void Method_aspect_test_applies_the_attribute_to_a_method()
    {
        var text = AspectTestScaffold.TestFile("Sample.AspectTests", "Sample", "LogAttribute", isAttribute: true, AspectTestScaffold.TargetKind.Method);
        Assert.Contains("using Sample;", text);
        Assert.Contains("namespace Sample.AspectTests;", text);
        Assert.Contains("    [Log]\n    public int Add(int a, int b) => a + b;", text);
        Assert.Contains(".t.cs", text); // explains the snapshot
    }

    [Theory]
    [InlineData(AspectTestScaffold.TargetKind.Type, "[Url]\npublic partial class UrlTarget")]
    [InlineData(AspectTestScaffold.TargetKind.FieldOrProperty, "    [Url]\n    public string Name { get; set; }")]
    [InlineData(AspectTestScaffold.TargetKind.Parameter, "public string Greet([Url] string name)")]
    [InlineData(AspectTestScaffold.TargetKind.Constructor, "    [Url]\n    public UrlTarget(")]
    [InlineData(AspectTestScaffold.TargetKind.Event, "    [Url]\n    public event EventHandler? Changed;")]
    public void Target_fits_the_aspect_kind(AspectTestScaffold.TargetKind kind, string expected) =>
        Assert.Contains(expected, AspectTestScaffold.TestFile("T", "Lib", "Url", isAttribute: true, kind));

    [Fact]
    public void Aspect_without_attribute_says_how_to_apply_it() =>
        Assert.Contains("fabric", AspectTestScaffold.TestFile("T", "Lib", "Audit", isAttribute: false, AspectTestScaffold.TargetKind.Method));

    [Fact]
    public void Test_file_names_count_up_and_never_contain_dots()
    {
        Assert.Equal("LogTest.cs", AspectTestScaffold.NextTestFileName([], "LogAttribute"));
        Assert.Equal("LogTest2.cs", AspectTestScaffold.NextTestFileName(["LogTest.cs", "LogTest.t.cs"], "LogAttribute"));
        Assert.Equal("LogTest3.cs", AspectTestScaffold.NextTestFileName(["logtest.cs", "LogTest2.cs"], "LogAttribute"));
    }

    [Fact]
    public void Project_for_metalama_2026_uses_xunit_2()
    {
        var csproj = AspectTestScaffold.TestProject("2026.1.30", @"..\Sample\Sample.csproj", "net8.0", centralPackages: false);
        Assert.Contains("<PackageReference Include=\"Metalama.Testing.AspectTesting\" Version=\"2026.1.30\" />", csproj);
        Assert.Contains("<PackageReference Include=\"xunit\" Version=", csproj);
        Assert.Contains("<ProjectReference Include=\"..\\Sample\\Sample.csproj\" />", csproj);
        Assert.Contains("<TargetFramework>net8.0</TargetFramework>", csproj);
    }

    [Fact]
    public void Project_for_metalama_2027_uses_xunit_v3_as_executable()
    {
        var csproj = AspectTestScaffold.TestProject("2027.0.4", "../Sample/Sample.csproj", "net10.0", centralPackages: false);
        Assert.Contains("<PackageReference Include=\"xunit.v3\" Version=", csproj);
        Assert.Contains("<OutputType>Exe</OutputType>", csproj);
    }

    [Fact]
    public void Central_package_management_moves_versions_to_the_props()
    {
        var csproj = AspectTestScaffold.TestProject("2026.1.30", "../Sample/Sample.csproj", "net8.0", centralPackages: true);
        Assert.DoesNotContain("Version=", csproj);

        const string props = "<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"xunit\" Version=\"2.9.0\" />\n  </ItemGroup>\n</Project>\n";
        var updated = AspectTestScaffold.AddPackageVersions(props, "2026.1.30");
        Assert.Contains("<PackageVersion Include=\"xunit\" Version=\"2.9.0\" />", updated); // existing pins win
        Assert.Contains("<PackageVersion Include=\"Metalama.Testing.AspectTesting\" Version=\"2026.1.30\" />", updated);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(updated, "Include=\"xunit\""));
    }

    [Fact]
    public void Test_projects_are_recognized_by_package_and_reference()
    {
        const string csproj = "<Project><ItemGroup><PackageReference Include=\"Metalama.Testing.AspectTesting\" />" +
                              "<ProjectReference Include=\"..\\Sample\\Sample.csproj\" /></ItemGroup></Project>";
        Assert.True(AspectTestScaffold.IsAspectTestProject(csproj));
        Assert.True(AspectTestScaffold.References(csproj, "Sample.csproj"));
        Assert.False(AspectTestScaffold.References(csproj, "Other.csproj"));
        Assert.False(AspectTestScaffold.IsAspectTestProject("<Project />"));
    }

    [Fact]
    public void Test_output_is_found_under_obj_transformed()
    {
        var project = Path.Combine(Path.GetTempPath(), "lamarider-scaffold", Guid.NewGuid().ToString("N"));
        var test = Path.Combine(project, "Log", "Add.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(test)!);
        File.WriteAllText(test, "x");
        Assert.Null(AspectTestScaffold.ActualOutput(project, test));

        var output = Path.Combine(project, "obj", "transformed", "net8.0", "Log", "Add.t.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, "transformed");
        var elsewhere = Path.Combine(project, "obj", "transformed", "net8.0", "Other", "Add.t.cs"); // same name, other test
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        File.WriteAllText(elsewhere, "other");
        Assert.Equal(output, AspectTestScaffold.ActualOutput(project, test));
        Assert.Equal(Path.Combine(project, "Log", "Add.t.cs"), AspectTestScaffold.SnapshotPath(test));
    }
}
