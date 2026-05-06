using AlleyCat.Testing;
using Xunit;

namespace AlleyCat.Tests.Testing;

/// <summary>
/// Tests runtime-context detection for integration-test execution.
/// </summary>
public sealed class RuntimeContextTests
{
    private const string MainScenePath = "res://assets/scenes/main.tscn";
    private const string TestScenePath = "res://tests/speech/voice_test.tscn";

    /// <summary>
    /// Ensures the explicit environment signal marks integration-test context.
    /// </summary>
    [Fact]
    public void IsIntegrationTest_ReturnsTrue_WhenExplicitContextIsIntegrationTest()
    {
        bool result = RuntimeContext.IsIntegrationTest(
            explicitContext: RuntimeContext.IntegrationTestContextValue,
            commandLineArguments: ["game"]);

        Assert.True(result);
    }

    /// <summary>
    /// Ensures explicit context matching is case-insensitive.
    /// </summary>
    [Fact]
    public void IsIntegrationTest_ReturnsTrue_WhenExplicitContextUsesDifferentCasing()
    {
        bool result = RuntimeContext.IsIntegrationTest(
            explicitContext: "InTeGrAtIoN-TeSt",
            commandLineArguments: ["game"]);

        Assert.True(result);
    }

    /// <summary>
    /// Ensures run-fact arguments are accepted as fallback integration-test signals.
    /// </summary>
    [Fact]
    public void IsIntegrationTest_ReturnsTrue_WhenRunFactArgumentIsPresent()
    {
        bool result = RuntimeContext.IsIntegrationTest(
            explicitContext: null,
            commandLineArguments: ["godot-mono", "--", "--integration-run-fact"]);

        Assert.True(result);
    }

    /// <summary>
    /// Ensures probe arguments are accepted as fallback integration-test signals.
    /// </summary>
    [Fact]
    public void IsIntegrationTest_ReturnsTrue_WhenProbeArgumentIsPresent()
    {
        bool result = RuntimeContext.IsIntegrationTest(
            explicitContext: null,
            commandLineArguments: ["godot-mono", "--", "--integration-probe"]);

        Assert.True(result);
    }

    /// <summary>
    /// Ensures normal runtime remains non-integration when no signal is present.
    /// </summary>
    [Fact]
    public void IsIntegrationTest_ReturnsFalse_WhenNoSignalIsPresent()
    {
        bool result = RuntimeContext.IsIntegrationTest(
            explicitContext: null,
            commandLineArguments: ["godot-mono", "--path", "game"]);

        Assert.False(result);
    }

    /// <summary>
    /// Ensures global startup bypasses integration-test runtime regardless of scene.
    /// </summary>
    [Fact]
    public void ShouldBypassGlobalStartup_ReturnsTrue_ForIntegrationTestContext()
    {
        bool result = RuntimeContext.ShouldBypassGlobalStartup(
            explicitContext: RuntimeContext.IntegrationTestContextValue,
            commandLineArguments: ["godot-mono", "--path", "game"],
            currentScenePath: MainScenePath,
            configuredMainScenePath: MainScenePath);

        Assert.True(result);
    }

    /// <summary>
    /// Ensures editor-run manual test scenes bypass global startup.
    /// </summary>
    [Fact]
    public void ShouldBypassGlobalStartup_ReturnsTrue_ForTestSceneOutsideMainScene()
    {
        bool result = RuntimeContext.ShouldBypassGlobalStartup(
            explicitContext: null,
            commandLineArguments: ["godot-mono", "--path", "game"],
            currentScenePath: TestScenePath,
            configuredMainScenePath: MainScenePath);

        Assert.True(result);
    }

    /// <summary>
    /// Ensures the configured main scene and other non-test scenes keep normal startup.
    /// </summary>
    [Theory]
    [InlineData(MainScenePath, MainScenePath)]
    [InlineData("res://assets/scenes/sandbox.tscn", MainScenePath)]
    public void ShouldBypassGlobalStartup_ReturnsFalse_ForMainOrNonTestScenes(
        string currentScenePath,
        string configuredMainScenePath)
    {
        bool result = RuntimeContext.ShouldBypassGlobalStartup(
            explicitContext: null,
            commandLineArguments: ["godot-mono", "--path", "game"],
            currentScenePath: currentScenePath,
            configuredMainScenePath: configuredMainScenePath);

        Assert.False(result);
    }
}
