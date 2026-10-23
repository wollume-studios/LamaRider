using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class MsBuildEditsTests
{
    const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n";

    [Fact]
    public void A_new_property_goes_into_the_first_property_group() =>
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n    <LangVersion>12</LangVersion>\n  </PropertyGroup>\n</Project>\n",
            MsBuildEdits.SetProperty(Project, "LangVersion", "12"));

    [Fact]
    public void An_existing_property_gets_the_new_value() =>
        Assert.Contains("<TargetFramework>net9.0</TargetFramework>", MsBuildEdits.SetProperty(Project, "TargetFramework", "net9.0"));

    [Fact]
    public void A_property_is_removed_with_its_line() =>
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n  </PropertyGroup>\n</Project>\n", MsBuildEdits.RemoveProperty(Project, "TargetFramework"));

    [Fact]
    public void A_project_without_property_group_gets_one() =>
        Assert.Equal("<Project>\n  <PropertyGroup>\n    <A>1</A>\n  </PropertyGroup>\n</Project>\n", MsBuildEdits.SetProperty("<Project>\n</Project>\n", "A", "1"));

    [Fact]
    public void NoWarn_keeps_existing_ids()
    {
        var once = MsBuildEdits.AddNoWarn(Project, "LAMA0066");
        Assert.Contains("<NoWarn>$(NoWarn);LAMA0066</NoWarn>", once);
        Assert.Contains("<NoWarn>$(NoWarn);LAMA0066;LAMA0078</NoWarn>", MsBuildEdits.AddNoWarn(once, "LAMA0078"));
        Assert.Equal(once, MsBuildEdits.AddNoWarn(once, "LAMA0066"));
    }
}
