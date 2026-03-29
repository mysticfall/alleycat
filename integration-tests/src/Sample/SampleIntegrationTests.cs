using Xunit;

namespace AlleyCat.IntegrationTests.Sample;

/// <summary>
/// Minimal integration sample used to validate remote MTP test discovery and execution.
/// </summary>
public sealed class SampleIntegrationTests
{
    /// <summary>
    /// Verifies the integration host can discover and execute a sample test.
    /// </summary>
    [Fact]
    public void GodotIsTheLatestStableVersion() => Assert.True(true);
}
