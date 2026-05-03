using AlleyCat.IK.Pose;
using Xunit;

namespace AlleyCat.Tests.IK.Pose;

/// <summary>
/// Unit coverage for all-fours return-to-standing gating.
/// </summary>
public sealed class AllFoursToStandingPoseTransitionTests
{
    /// <summary>
    /// Return to standing should only fire while the animation tree is in the all-fours transitioning node.
    /// </summary>
    [Fact]
    public void Evaluate_CrawlingPhase_DoesNotTransition()
    {
        bool shouldTransition = AllFoursToStandingPoseTransition.Evaluate(
            isTransitioningPhaseActive: false,
            forwardOffset: 0.33f,
            entryForwardOffsetThreshold: 0.42f,
            returnForwardMargin: 0.05f);

        Assert.False(shouldTransition);
    }

    /// <summary>
    /// Return to standing fires once the player backs out below the configured threshold while in transitioning.
    /// </summary>
    [Fact]
    public void Evaluate_TransitioningPhaseAndBackedOut_Transitions()
    {
        bool shouldTransition = AllFoursToStandingPoseTransition.Evaluate(
            isTransitioningPhaseActive: true,
            forwardOffset: 0.33f,
            entryForwardOffsetThreshold: 0.42f,
            returnForwardMargin: 0.05f);

        Assert.True(shouldTransition);
    }
}
