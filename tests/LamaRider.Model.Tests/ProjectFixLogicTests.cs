using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class ProjectFixLogicTests
{
    [Fact]
    public void Supported_versions_are_read_newest_first() =>
        Assert.Equal(["13.0", "12.0", "11.0", "10.0"], ProjectFixLogic.SupportedVersions(
            "The C# language version '8.0' is not supported. Change the <LangVersion> property of your project file to one of the following supported values: 10.0, 11.0, 12.0, 13.0. Do not use 'latest' or 'latestMajor' because …"));

    [Fact]
    public void Supported_versions_accept_enum_names() =>
        Assert.Equal(["12", "11"], ProjectFixLogic.SupportedVersions("… one of the following supported values: CSharp11, CSharp12. If you want …"));

    [Fact]
    public void The_language_version_property_comes_from_the_message()
    {
        Assert.Equal("MetalamaTemplateLanguageVersion", ProjectFixLogic.LanguageVersionProperty("Change the <MetalamaTemplateLanguageVersion> property of your project"));
        Assert.Equal("LangVersion", ProjectFixLogic.LanguageVersionProperty("something else"));
        Assert.Equal("11", ProjectFixLogic.RequiredLanguageVersion("The aspect 'LogAttribute' uses features of C# 11, but it is used in a project built with C# 10."));
    }

    [Fact]
    public void Star_patterns_become_anchored_regexes() => Assert.Equal("^.*Service$", ProjectFixLogic.StarToRegex("*Service"));

    [Fact]
    public void Names_matching_a_suffix_pattern_replace_the_last_word_first()
    {
        Assert.Equal(["CustomerRepository", "CustomerStoreRepository"], ProjectFixLogic.NamesMatching("CustomerStore", "*Repository"));
        Assert.Equal(["TextReader", "TextLoaderReader"], ProjectFixLogic.NamesMatching("TextLoader", "^.*Reader$"));
        Assert.Equal(["IOrder"], ProjectFixLogic.NamesMatching("Order", "I*"));
        Assert.Empty(ProjectFixLogic.NamesMatching("Order", "^[A-Z]+$"));
    }

    [Fact]
    public void Raw_string_literals_become_regular_ones()
    {
        Assert.Equal("\"Logged by the \\\"Log\\\" aspect.\"", ProjectFixLogic.RawToRegular("\"\"\"Logged by the \"Log\" aspect.\"\"\""));
        Assert.Equal("\"a\\nb\"", ProjectFixLogic.RawToRegular("\"\"\"\n    a\n    b\n    \"\"\""));
        Assert.Equal("$\"x {F(\"q\")} \\\"y\\\"\"", ProjectFixLogic.RawToRegular("$\"\"\"x {F(\"q\")} \"y\"\"\"\""));
        Assert.Null(ProjectFixLogic.RawToRegular("$$\"\"\"{{x}}\"\"\""));
        Assert.Null(ProjectFixLogic.RawToRegular("\"regular\""));
    }

    [Fact]
    public void Paths_are_read_from_crash_messages() =>
        Assert.Equal([@"C:\Temp\Metalama\CrashReports\exception-1.txt"], ProjectFixLogic.Paths(
            "'Exception of type 'System.InvalidOperationException' thrown …: The Category property must be set. Exception details are in 'C:\\Temp\\Metalama\\CrashReports\\exception-1.txt'. To attach …"));

    [Fact]
    public void DefineConstants_is_appended_to()
    {
        const string project = "<Project>\n  <PropertyGroup>\n    <DefineConstants>DEBUG;TRACE</DefineConstants>\n    <DefineConstants Condition=\"x\">$(DefineConstants);A</DefineConstants>\n  </PropertyGroup>\n</Project>\n";
        Assert.True(ProjectFixLogic.OverwritesDefineConstants(project));
        var fixedProject = ProjectFixLogic.AppendDefineConstants(project);
        Assert.Contains("<DefineConstants>$(DefineConstants);DEBUG;TRACE</DefineConstants>", fixedProject);
        Assert.Contains("<DefineConstants Condition=\"x\">$(DefineConstants);A</DefineConstants>", fixedProject);
        Assert.False(ProjectFixLogic.OverwritesDefineConstants(fixedProject));
    }

    [Fact]
    public void Items_are_added_once()
    {
        var once = ProjectFixLogic.AddItem("<Project>\n</Project>\n", "MetalamaImmutableType", "Contoso.Pair`2");
        Assert.Equal("<Project>\n  <ItemGroup>\n    <MetalamaImmutableType Include=\"Contoso.Pair`2\" />\n  </ItemGroup>\n</Project>\n", once);
        Assert.Equal(once, ProjectFixLogic.AddItem(once, "MetalamaImmutableType", "Contoso.Pair`2"));
    }

    [Fact]
    public void Aspect_order_uses_typeof_or_layer_names()
    {
        Assert.Equal("[assembly: AspectOrder( AspectOrderDirection.CompileTime, typeof(Ns.A), typeof(Ns.B.C) )]",
            ProjectFixLogic.AspectOrderAttribute("CompileTime", ["Ns.A", "Ns.B+C"]));
        Assert.Equal("[assembly: AspectOrder( AspectOrderDirection.RunTime, \"Ns.A:Init\", \"Ns.B\" )]",
            ProjectFixLogic.AspectOrderAttribute("RunTime", ["Ns.A:Init", "Ns.B"]));
    }

    [Fact]
    public void Closest_names_are_found() => Assert.Equal(["Print"], ProjectFixLogic.Closest("Prnt", ["Print", "Run", "ToString"]));
}
