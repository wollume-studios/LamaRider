using LamaRider.Model;

namespace LamaRider.Model.Tests;

public class MetalamaVersionDetectorTests
{
    [Fact]
    public void Version_detection_picks_highest() =>
        Assert.Equal("2026.1.30", MetalamaVersionDetector.FromAssetsFiles([
            """{"libraries":{"Metalama.Framework/2026.1.9":{}}}""",
            """{"libraries":{"Metalama.Framework/2026.1.30":{},"Metalama.Framework.Redist/2026.1.30":{}}}"""]));

    [Fact]
    public void Version_detection_returns_null_without_metalama() =>
        Assert.Null(MetalamaVersionDetector.FromAssetsFiles(["""{"libraries":{"Newtonsoft.Json/13.0.3":{}}}"""]));

    [Fact]
    public void Version_detection_keeps_prerelease_suffix() =>
        Assert.Equal("2026.2.1-preview", MetalamaVersionDetector.FromAssetsFiles(["""{"libraries":{"Metalama.Framework/2026.2.1-preview":{}}}"""]));

    [Fact]
    public void Version_detection_ignores_versions_that_could_inject_arguments_or_paths() =>
        Assert.Equal("2026.1.9", MetalamaVersionDetector.FromAssetsFiles([
            """{"libraries":{"Metalama.Framework/2026.1.9":{},"Metalama.Framework/9999.0.0 -p:Evil=1":{},"Metalama.Framework/9999.0.0/../../x":{}}}"""]));

    [Fact]
    public void Detects_aspect_testing_projects() =>
        Assert.True(MetalamaVersionDetector.UsesAspectTesting([
            """{"libraries":{"Metalama.Framework/2026.1.30":{}}}""",
            """{"libraries":{"Metalama.Testing.AspectTesting/2026.1.30":{}}}"""]));

    [Fact]
    public void No_aspect_testing_without_the_package() =>
        Assert.False(MetalamaVersionDetector.UsesAspectTesting(["""{"libraries":{"Metalama.Framework/2026.1.30":{},"Metalama.Testing.UnitTesting/2026.1.30":{}}}"""]));
}
