using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class TestSettingsStatusTests
{
    [Fact]
    public void Configured_when_discovery_uses_the_test_runner_and_all_diff_tools_are_ignored()
    {
        var (configured, text) = TestSettingsStatus.Describe(discoveryByTestRunner: true, ignoredDiffTools: 11, diffTools: 11, usesAspectTests: true);
        Assert.True(configured);
        Assert.StartsWith("Configured", text);
    }

    [Fact]
    public void Partly_configured_names_the_diff_tools_that_still_prompt()
    {
        var (configured, text) = TestSettingsStatus.Describe(true, 9, 11, true);
        Assert.False(configured);
        Assert.Contains("2 of 11 diff tools", text);
    }

    [Fact]
    public void Metadata_discovery_is_not_configured()
    {
        var (configured, text) = TestSettingsStatus.Describe(false, 11, 11, true);
        Assert.False(configured);
        Assert.Contains("Metadata", text);
    }

    [Fact]
    public void Mentions_when_the_solution_has_no_aspect_tests()
    {
        Assert.Contains("no aspect tests", TestSettingsStatus.Describe(false, 0, 11, false).Text);
        Assert.DoesNotContain("no aspect tests", TestSettingsStatus.Describe(false, 0, 11, null).Text);
    }
}
