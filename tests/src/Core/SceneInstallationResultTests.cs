using AlleyCat.Core.Installer;
using Xunit;

namespace AlleyCat.Tests.Core;

/// <summary>
/// Unit coverage for Godot-independent installer result handling.
/// </summary>
public sealed class SceneInstallationResultTests
{
    /// <summary>
    /// Successful results merge without errors.
    /// </summary>
    [Fact]
    public void Merge_AllSuccessful_ReturnsSuccessfulResult()
    {
        var result = SceneInstallationResult.Merge(
            [SceneInstallationResult.Successful(), SceneInstallationResult.Successful()]);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// Failed results preserve all errors in source order.
    /// </summary>
    [Fact]
    public void Merge_Failures_ReturnsAllErrorsInOrder()
    {
        var result = SceneInstallationResult.Merge(
            [
                SceneInstallationResult.Successful(),
                SceneInstallationResult.Failed("first"),
                SceneInstallationResult.Failed("second", "third"),
            ]);

        Assert.False(result.Succeeded);
        Assert.Equal(["first", "second", "third"], result.Errors);
    }

    /// <summary>
    /// Failed results can fail fast at authoring boundaries with useful diagnostics.
    /// </summary>
    [Fact]
    public void ThrowIfFailed_FailedResult_ThrowsWithErrors()
    {
        var result = SceneInstallationResult.Failed("first", "second");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(result.ThrowIfFailed);

        Assert.Contains("first", ex.Message, StringComparison.Ordinal);
        Assert.Contains("second", ex.Message, StringComparison.Ordinal);
    }
}
