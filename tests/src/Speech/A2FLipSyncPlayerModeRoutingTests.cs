using AlleyCat.Speech.LipSync;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for the Audio2Face inference mode routing decision: regression mode selects the
/// streaming endpoint while diffusion (including v3 models auto-adjusted away from regression) uses the
/// batch endpoint. The endpoint selection itself is asserted end-to-end by the scripted-server
/// integration tests.
/// </summary>
public sealed class A2FLipSyncPlayerModeRoutingTests
{
    /// <summary>
    /// Regression-family models must auto-adjust a diffusion request back to regression.
    /// </summary>
    [Theory]
    [InlineData("mark")]
    [InlineData("claire")]
    [InlineData("james")]
    public void ResolveEffectiveModeForModel_WithRegressionModelAndDiffusionRequest_AutoAdjustsToRegression(string modelId)
        => Assert.Equal(
            "regression",
            A2FLipSyncPlayer.ResolveEffectiveModeForModel(modelId, "diffusion", autoAdjustForKnownModel: true));

    /// <summary>
    /// The v3 diffusion model must auto-adjust a regression request to diffusion, so streaming playback
    /// is never requested for a model the streaming endpoint rejects.
    /// </summary>
    [Fact]
    public void ResolveEffectiveModeForModel_WithV3ModelAndRegressionRequest_AutoAdjustsToDiffusion()
        => Assert.Equal(
            "diffusion",
            A2FLipSyncPlayer.ResolveEffectiveModeForModel("v3", "regression", autoAdjustForKnownModel: true));

    /// <summary>
    /// A diffusion request for the v3 model must stay diffusion.
    /// </summary>
    [Fact]
    public void ResolveEffectiveModeForModel_WithV3ModelAndDiffusionRequest_StaysDiffusion()
        => Assert.Equal(
            "diffusion",
            A2FLipSyncPlayer.ResolveEffectiveModeForModel("v3", "diffusion", autoAdjustForKnownModel: true));

    /// <summary>
    /// Unknown model families must keep the requested mode untouched so custom server-side models can
    /// opt into either mode.
    /// </summary>
    [Fact]
    public void ResolveEffectiveModeForModel_WithUnknownModel_KeepsRequestedMode()
    {
        Assert.Equal(
            "diffusion",
            A2FLipSyncPlayer.ResolveEffectiveModeForModel("custom-experiment", "diffusion", autoAdjustForKnownModel: true));
        Assert.Equal(
            "regression",
            A2FLipSyncPlayer.ResolveEffectiveModeForModel(string.Empty, "regression", autoAdjustForKnownModel: true));
    }

    /// <summary>
    /// Disabling the auto-adjust must keep even a mismatched requested mode.
    /// </summary>
    [Fact]
    public void ResolveEffectiveModeForModel_WithAutoAdjustDisabled_KeepsRequestedMode()
    {
        Assert.Equal(
            "diffusion",
            A2FLipSyncPlayer.ResolveEffectiveModeForModel("mark", "diffusion", autoAdjustForKnownModel: false));
        Assert.Equal(
            "regression",
            A2FLipSyncPlayer.ResolveEffectiveModeForModel("v3", "regression", autoAdjustForKnownModel: false));
    }
}
