using AlleyCat.IK.Pose;
using Xunit;

namespace AlleyCat.Tests.IK.Pose;

/// <summary>
/// Unit coverage for all-fours armed-then-continue-forward trigger semantics.
/// </summary>
public sealed class AllFoursEntryPoseTransitionTests
{
    private const float ArmingForwardOffsetThreshold = 0.42f;
    private const float ContinueForwardMargin = 0.06f;

    /// <summary>
    /// Reaching the arming threshold arms the trigger but must not fire immediately.
    /// </summary>
    [Fact]
    public void Evaluate_ReachingArmingThreshold_ArmsButDoesNotTransition()
    {
        (bool shouldTransition, bool isArmed, float armedForwardOffsetRatio) = AllFoursEntryPoseTransition.Evaluate(
            forwardOffset: 0.43f,
            ArmingForwardOffsetThreshold,
            ContinueForwardMargin,
            isArmed: false,
            armedForwardOffsetRatio: 0f);

        Assert.False(shouldTransition);
        Assert.True(isArmed);
        Assert.Equal(0.43f, armedForwardOffsetRatio, precision: 4);
    }

    /// <summary>
    /// After arming, continuing forward beyond the armed point plus the configured margin fires the transition.
    /// </summary>
    [Fact]
    public void Evaluate_ContinuingForwardPastArmedPoint_Transitions()
    {
        (bool armedResult, bool isArmed, float armedForwardOffsetRatio) = AllFoursEntryPoseTransition.Evaluate(
            forwardOffset: 0.43f,
            ArmingForwardOffsetThreshold,
            ContinueForwardMargin,
            isArmed: false,
            armedForwardOffsetRatio: 0f);

        Assert.False(armedResult);
        Assert.True(isArmed);

        (bool shouldTransition, bool updatedIsArmed, float updatedArmedForwardOffsetRatio) = AllFoursEntryPoseTransition.Evaluate(
            forwardOffset: 0.50f,
            ArmingForwardOffsetThreshold,
            ContinueForwardMargin,
            isArmed,
            armedForwardOffsetRatio);

        Assert.True(shouldTransition);
        Assert.False(updatedIsArmed);
        Assert.Equal(0f, updatedArmedForwardOffsetRatio);
    }

    /// <summary>
    /// Retreating below the arming threshold before the continue-forward margin is reached clears the arm state.
    /// </summary>
    [Fact]
    public void Evaluate_RetreatingBackBelowThreshold_DisarmsWithoutTransition()
    {
        (bool armedResult, bool isArmed, float armedForwardOffsetRatio) = AllFoursEntryPoseTransition.Evaluate(
            forwardOffset: 0.43f,
            ArmingForwardOffsetThreshold,
            ContinueForwardMargin,
            isArmed: false,
            armedForwardOffsetRatio: 0f);

        Assert.False(armedResult);
        Assert.True(isArmed);

        (bool shouldTransition, bool updatedIsArmed, float updatedArmedForwardOffsetRatio) = AllFoursEntryPoseTransition.Evaluate(
            forwardOffset: 0.40f,
            ArmingForwardOffsetThreshold,
            ContinueForwardMargin,
            isArmed,
            armedForwardOffsetRatio);

        Assert.False(shouldTransition);
        Assert.False(updatedIsArmed);
        Assert.Equal(0f, updatedArmedForwardOffsetRatio);
    }
}
