using AlleyCat.Core.Content;
using Xunit;

namespace AlleyCat.Tests.Core;

/// <summary>
/// Tests the Godot-free content-pack start-scene selection logic.
/// </summary>
public sealed class ContentResolverTests
{
    private const string ContentRoot = "res://content/";
    private const string Fallback = "res://assets/scenes/empty.tscn";
    private const string RequestedPath = "res://content/req/start.tscn";
    private const string DefaultPath = "res://content/def/start.tscn";

    /// <summary>
    /// Integration-test context must always return the fallback regardless of packs.
    /// </summary>
    [Fact]
    public void SelectStartScenePath_ReturnsFallback_WhenIntegrationTest()
    {
        string result = ContentResolver.SelectStartScenePath(
            requestedPackId: "req",
            defaultPackId: "def",
            isIntegrationTest: true,
            sceneExists: _ => throw new InvalidOperationException("Integration-test bypass should not probe content."),
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot);

        Assert.Equal(Fallback, result);
    }

    /// <summary>
    /// A present requested pack must take precedence over the default pack.
    /// </summary>
    [Fact]
    public void SelectStartScenePath_ReturnsRequestedPath_WhenRequestedPackPresent()
    {
        string result = ContentResolver.SelectStartScenePath(
            requestedPackId: "req",
            defaultPackId: "def",
            isIntegrationTest: false,
            sceneExists: p => p == RequestedPath,
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot);

        Assert.Equal(RequestedPath, result);
    }

    /// <summary>
    /// With no requested pack, the present default pack must be used.
    /// </summary>
    [Fact]
    public void SelectStartScenePath_ReturnsDefaultPath_WhenOnlyDefaultPackPresent()
    {
        string result = ContentResolver.SelectStartScenePath(
            requestedPackId: null,
            defaultPackId: "def",
            isIntegrationTest: false,
            sceneExists: p => p == DefaultPath,
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot);

        Assert.Equal(DefaultPath, result);
    }

    /// <summary>
    /// With neither pack present, the fallback must be returned.
    /// </summary>
    [Fact]
    public void SelectStartScenePath_ReturnsFallback_WhenNoPackPresent()
    {
        string result = ContentResolver.SelectStartScenePath(
            requestedPackId: null,
            defaultPackId: null,
            isIntegrationTest: false,
            sceneExists: _ => false,
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot);

        Assert.Equal(Fallback, result);
    }

    /// <summary>
    /// A requested pack whose scene is missing must fail explicitly instead of falling through.
    /// </summary>
    [Fact]
    public void SelectStartScenePath_Throws_WhenRequestedPackSceneMissing()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => ContentResolver.SelectStartScenePath(
            requestedPackId: "req",
            defaultPackId: "def",
            isIntegrationTest: false,
            sceneExists: p => p == DefaultPath,
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot));

        Assert.Contains("req", exception.Message, StringComparison.Ordinal);
        Assert.Contains(RequestedPath, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A default pack whose scene is missing must fall through to the fallback.
    /// </summary>
    [Fact]
    public void SelectStartScenePath_ReturnsFallback_WhenDefaultPackSceneMissing()
    {
        string result = ContentResolver.SelectStartScenePath(
            requestedPackId: null,
            defaultPackId: "def",
            isIntegrationTest: false,
            sceneExists: _ => false,
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot);

        Assert.Equal(Fallback, result);
    }
}
