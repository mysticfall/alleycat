using AlleyCat.Core.Content;
using Xunit;

namespace AlleyCat.IntegrationTests.Core;

/// <summary>
/// Best-effort runtime coverage for <see cref="ContentResolver"/> selection behaviour.
/// </summary>
public sealed class ContentResolverIntegrationTests
{
    /// <summary>
    /// Ensures the resolver constructs and applies the Godot-free selection logic.
    /// </summary>
    [Fact]
    public void ResolveStartScenePath_ReturnsFallback_WhenIntegrationTest()
    {
        ContentResolver resolver = new();

        string result = ContentResolver.SelectStartScenePath(
            requestedPackId: "req",
            defaultPackId: "def",
            isIntegrationTest: true,
            sceneExists: _ => true,
            fallbackStartScenePath: "res://assets/scenes/empty.tscn");

        Assert.Equal("res://assets/scenes/empty.tscn", result);
    }
}
