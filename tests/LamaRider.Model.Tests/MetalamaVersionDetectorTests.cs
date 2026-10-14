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
}
