using AlleyCat.IK.Pose;
using Godot;
using Xunit;

namespace AlleyCat.Tests.IK.Pose;

/// <summary>
/// Unit coverage for the directional threshold predicate used by
/// <see cref="HeadOffsetPoseTransition"/>.
/// </summary>
/// <remarks>
/// The transition itself is a Godot <see cref="Resource"/> subclass and therefore cannot
/// be instantiated outside the engine. These tests exercise
/// <see cref="HeadOffsetPoseTransition.Evaluate"/>, the pure static helper that implements
/// the direction-aware predicate. They focus on:
/// <list type="bullet">
///   <item><description>Explicit per-axis direction semantics.</description></item>
///   <item><description>Strict threshold boundaries for deterministic edges.</description></item>
///   <item><description>Positive threshold semantics for negative authored values.</description></item>
/// </list>
/// </remarks>
public sealed class HeadOffsetPoseTransitionTests
{
    private const float Threshold = 0.0133f;

    /// <summary>
    /// Downward direction fires when head Y drops below negative threshold.
    /// </summary>
    [Fact]
    public void Evaluate_DownwardAboveMagnitudeThreshold_ReturnsTrue()
    {
        bool fires = HeadOffsetPoseTransition.Evaluate(
            normalizedHeadLocalOffset: new Vector3(0.0f, -(Threshold + 0.001f), 0.0f),
            threshold: Threshold,
            direction: HeadOffsetPoseTransition.TransitionDirection.Downward);

        Assert.True(fires);
    }

    /// <summary>
    /// Downward direction is strict; exactly at threshold does not fire.
    /// </summary>
    [Fact]
    public void Evaluate_DownwardAtThreshold_ReturnsFalse()
    {
        bool fires = HeadOffsetPoseTransition.Evaluate(
            normalizedHeadLocalOffset: new Vector3(0.0f, -Threshold, 0.0f),
            threshold: Threshold,
            direction: HeadOffsetPoseTransition.TransitionDirection.Downward);

        Assert.False(fires);
    }

    /// <summary>
    /// Upward direction fires when head Y rises above positive threshold.
    /// </summary>
    [Fact]
    public void Evaluate_UpwardAboveThreshold_ReturnsTrue()
    {
        bool fires = HeadOffsetPoseTransition.Evaluate(
            normalizedHeadLocalOffset: new Vector3(0.0f, Threshold + 0.001f, 0.0f),
            threshold: Threshold,
            direction: HeadOffsetPoseTransition.TransitionDirection.Upward);

        Assert.True(fires);
    }

    /// <summary>
    /// Forward direction uses Godot forward axis (-Z).
    /// </summary>
    [Fact]
    public void Evaluate_ForwardAboveThreshold_ReturnsTrue()
    {
        bool fires = HeadOffsetPoseTransition.Evaluate(
            normalizedHeadLocalOffset: new Vector3(0.0f, 0.0f, -(Threshold + 0.001f)),
            threshold: Threshold,
            direction: HeadOffsetPoseTransition.TransitionDirection.Forward);

        Assert.True(fires);
    }

    /// <summary>
    /// Backward direction uses positive local Z.
    /// </summary>
    [Fact]
    public void Evaluate_BackwardAboveThreshold_ReturnsTrue()
    {
        bool fires = HeadOffsetPoseTransition.Evaluate(
            normalizedHeadLocalOffset: new Vector3(0.0f, 0.0f, Threshold + 0.001f),
            threshold: Threshold,
            direction: HeadOffsetPoseTransition.TransitionDirection.Backward);

        Assert.True(fires);
    }

    /// <summary>
    /// Positive threshold semantics are preserved by normalising authored negative thresholds.
    /// </summary>
    [Fact]
    public void Evaluate_NegativeThreshold_UsesAbsoluteMagnitude()
    {
        bool fires = HeadOffsetPoseTransition.Evaluate(
            normalizedHeadLocalOffset: new Vector3(0.0f, -(Threshold + 0.001f), 0.0f),
            threshold: -Threshold,
            direction: HeadOffsetPoseTransition.TransitionDirection.Downward);

        Assert.True(fires);
    }

    /// <summary>
    /// Orthogonal movement does not fire transitions for unrelated axes.
    /// </summary>
    [Fact]
    public void Evaluate_OrthogonalAxisMovement_DoesNotFire()
    {
        bool downwardFires = HeadOffsetPoseTransition.Evaluate(
            normalizedHeadLocalOffset: new Vector3(0.0f, 0.0f, -(Threshold + 0.001f)),
            threshold: Threshold,
            direction: HeadOffsetPoseTransition.TransitionDirection.Downward);

        bool forwardFires = HeadOffsetPoseTransition.Evaluate(
            normalizedHeadLocalOffset: new Vector3(0.0f, Threshold + 0.001f, 0.0f),
            threshold: Threshold,
            direction: HeadOffsetPoseTransition.TransitionDirection.Forward);

        Assert.False(downwardFires);
        Assert.False(forwardFires);
    }

    /// <summary>
    /// Standing/crouching legacy behaviour mapping remains equivalent.
    /// </summary>
    [Theory]
    [InlineData(-0.02f, true)]
    [InlineData(-0.01f, false)]
    [InlineData(0.01f, true)]
    [InlineData(0.005f, false)]
    public void Evaluate_LegacyVerticalMapping_RemainsEquivalent(
        float localY,
        bool expected)
    {
        HeadOffsetPoseTransition.TransitionDirection direction = localY <= 0.0f
            ? HeadOffsetPoseTransition.TransitionDirection.Downward
            : HeadOffsetPoseTransition.TransitionDirection.Upward;

        float mappedThreshold = localY <= 0.0f ? 0.0133f : 0.0067f;

        bool fires = HeadOffsetPoseTransition.Evaluate(
            normalizedHeadLocalOffset: new Vector3(0.0f, localY, 0.0f),
            threshold: mappedThreshold,
            direction: direction);

        Assert.Equal(expected, fires);
    }
}
