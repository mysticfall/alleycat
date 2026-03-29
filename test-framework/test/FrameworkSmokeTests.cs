using Xunit;

namespace AlleyCat.TestFramework.Tests;

/// <summary>
/// Basic smoke tests for the AlleyCat test framework project wiring.
/// </summary>
public sealed class FrameworkSmokeTests
{
    /// <summary>
    /// Verifies that the testing platform hook type is available from the framework assembly.
    /// </summary>
    [Fact]
    public void HookType_IsAvailable() =>
        Assert.Equal("AlleyCat.TestFramework.TestingPlatformBuilderHook", typeof(TestingPlatformBuilderHook).FullName);
}
