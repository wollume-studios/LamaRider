namespace LamaRider.Model;

/// <summary>Whether Rider is set up for Metalama aspect tests, as shown on the LamaRider settings page.</summary>
public static class TestSettingsStatus
{
    /// <param name="usesAspectTests">Null while the solution has not been analyzed yet.</param>
    public static (bool Configured, string Text) Describe(bool discoveryByTestRunner, int ignoredDiffTools, int diffTools, bool? usesAspectTests)
    {
        var allIgnored = ignoredDiffTools >= diffTools;
        var text = (discoveryByTestRunner, allIgnored) switch
        {
            (true, true) => "Configured: xUnit discovery by Test Runner, diff tools ignored as orphaned processes.",
            (true, false) => $"Partly configured: xUnit discovery by Test Runner, but {diffTools - ignoredDiffTools} of {diffTools} diff tools still prompt as orphaned processes.",
            _ => "Not configured: xUnit discovery is set to Metadata, which does not find Metalama aspect tests.",
        };
        if (usesAspectTests == false) text += " This solution has no aspect tests.";
        return (discoveryByTestRunner && allIgnored, text);
    }
}
