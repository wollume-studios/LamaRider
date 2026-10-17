using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class PremiumBuildTests
{
    [Theory]
    [InlineData("Metalama.Extensions.CodeFixes", true)]
    [InlineData("Metalama.Extensions.Validation", true)]
    [InlineData("Metalama.Extensions.Architecture", true)]
    [InlineData("Metalama.Patterns.Caching.Backends.Redis", true)]
    [InlineData("Metalama.Patterns.Caching.Backends.Azure", true)]
    [InlineData("Metalama.Framework", false)]
    [InlineData("Metalama.Patterns.Caching", false)]
    [InlineData("Metalama.Patterns.Contracts", false)]
    public void Premium_components_are_the_ones_metalama_licenses(string assembly, bool premium) =>
        Assert.Equal(premium, PremiumBuild.IsPremium(assembly));

    [Theory]
    [InlineData("Metalama.Extensions.CodeFixes", true)]
    [InlineData("Metalama.Extensions.Architecture.Predicates", true)]
    [InlineData("Metalama.Extensions.CodeFixesX", false)]
    [InlineData("Metalama.Framework.Aspects", false)]
    public void Premium_namespaces(string ns, bool premium) => Assert.Equal(premium, PremiumBuild.IsPremiumNamespace(ns));

    [Fact]
    public void Code_in_an_if_premium_block_is_guarded()
    {
        var text = "A1\n#if METALAMA_PREMIUM\nB1\n#else\nC1\n#endif\nD1\n#if DEBUG && METALAMA_PREMIUM\nE1\n#endif\n#if !METALAMA_PREMIUM\nF1\n#endif\n";
        bool Guarded(string marker) => PremiumBuild.IsGuarded(text, text.IndexOf(marker, StringComparison.Ordinal));
        Assert.False(Guarded("A1"));
        Assert.True(Guarded("B1"));
        Assert.False(Guarded("C1"));
        Assert.False(Guarded("D1"));
        Assert.True(Guarded("E1"));
        Assert.False(Guarded("F1"));
    }

    [Fact]
    public void Wrapping_puts_whole_lines_in_an_if_premium_block()
    {
        var text = "{\n    builder.Diagnostics.Suggest(fix);\n}\n";
        var start = text.IndexOf("builder", StringComparison.Ordinal);
        var end = text.IndexOf(';') + 1;
        Assert.Equal("{\n#if METALAMA_PREMIUM\n    builder.Diagnostics.Suggest(fix);\n#endif\n}\n", PremiumBuild.Wrap(text, start, end));
    }

    [Fact]
    public void Wrapping_needs_the_code_on_lines_of_its_own() =>
        Assert.Null(PremiumBuild.Wrap("var x = 1; Suggest(fix);\n", 11, 24));

    const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Metalama.Framework" Version="2026.1.30" />
            <PackageReference Include="Metalama.Extensions.CodeFixes" Version="2026.1.30" />
          </ItemGroup>
        </Project>
        """;

    [Fact]
    public void Setup_defines_the_symbol_only_for_premium_builds_and_references_premium_packages_only_then()
    {
        var (text, packages) = PremiumBuild.SetUp(Csproj)!.Value;
        Assert.Contains("""<MetalamaPremium Condition="'$(MetalamaPremium)' == ''">false</MetalamaPremium>""", text);
        Assert.Contains("""<DefineConstants Condition="'$(MetalamaPremium)' == 'true'">$(DefineConstants);METALAMA_PREMIUM</DefineConstants>""", text);
        Assert.Contains("""<PackageReference Include="Metalama.Extensions.CodeFixes" Condition="'$(MetalamaPremium)' == 'true'" Version="2026.1.30" />""", text);
        Assert.Contains("""<PackageReference Include="Metalama.Framework" Version="2026.1.30" />""", text);
        Assert.Equal(["Metalama.Extensions.CodeFixes"], packages);
        Assert.True(System.Xml.Linq.XDocument.Parse(text).Root != null);
    }

    [Fact]
    public void Setup_is_done_once() => Assert.Null(PremiumBuild.SetUp(PremiumBuild.SetUp(Csproj)!.Value.Text));
}
