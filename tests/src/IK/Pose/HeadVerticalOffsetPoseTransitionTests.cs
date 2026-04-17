using AlleyCat.IK.Pose;
using Xunit;

namespace AlleyCat.Tests.IK.Pose;

/// <summary>
/// Unit coverage for the hysteresis predicate used by
/// <see cref="HeadVerticalOffsetPoseTransition"/>.
/// </summary>
/// <remarks>
/// The transition itself is a Godot <see cref="Godot.Resource"/> subclass and therefore cannot
/// be instantiated outside the engine. These tests exercise
/// <see cref="HeadVerticalOffsetPoseTransition.Evaluate"/>, the pure static helper that
/// implements the descent/ascent predicate. They focus on:
/// <list type="bullet">
///   <item><description>Descent trigger threshold (Standing → Crouching).</description></item>
///   <item><description>Ascent release threshold (Crouching → Standing).</description></item>
///   <item><description>Hysteresis dead-band between release and trigger thresholds.</description></item>
/// </list>
/// </remarks>
public sealed class HeadVerticalOffsetPoseTransitionTests
{
    private const float TriggerOffset = 0.15f;
    private const float ReleaseOffset = 0.08f;

    /// <summary>
    /// Descent above the trigger threshold must fire the descent-trigger transition.
    /// </summary>
    [Fact]
    public void Evaluate_DescentAboveTrigger_ReturnsTrue()
    {
        bool fires = HeadVerticalOffsetPoseTransition.Evaluate(
            descentMetres: TriggerOffset + 0.01f,
            triggerOffsetMetres: TriggerOffset,
            releaseOffsetMetres: ReleaseOffset,
            triggerOnDescent: true);

        Assert.True(fires);
    }

    /// <summary>
    /// Descent strictly at the trigger threshold must not fire (strict greater-than semantics
    /// give the hysteresis band a deterministic boundary).
    /// </summary>
    [Fact]
    public void Evaluate_DescentAtTrigger_ReturnsFalse()
    {
        bool fires = HeadVerticalOffsetPoseTransition.Evaluate(
            descentMetres: TriggerOffset,
            triggerOffsetMetres: TriggerOffset,
            releaseOffsetMetres: ReleaseOffset,
            triggerOnDescent: true);

        Assert.False(fires);
    }

    /// <summary>
    /// Descent below the trigger threshold must not fire the descent-trigger transition.
    /// </summary>
    [Fact]
    public void Evaluate_DescentBelowTrigger_ReturnsFalse()
    {
        bool fires = HeadVerticalOffsetPoseTransition.Evaluate(
            descentMetres: TriggerOffset - 0.01f,
            triggerOffsetMetres: TriggerOffset,
            releaseOffsetMetres: ReleaseOffset,
            triggerOnDescent: true);

        Assert.False(fires);
    }

    /// <summary>
    /// Release (ascent) transition fires once descent falls below the release threshold.
    /// </summary>
    [Fact]
    public void Evaluate_AscentBelowRelease_ReturnsTrue()
    {
        bool fires = HeadVerticalOffsetPoseTransition.Evaluate(
            descentMetres: ReleaseOffset - 0.01f,
            triggerOffsetMetres: TriggerOffset,
            releaseOffsetMetres: ReleaseOffset,
            triggerOnDescent: false);

        Assert.True(fires);
    }

    /// <summary>
    /// Descent strictly at the release threshold must not fire the release transition.
    /// </summary>
    [Fact]
    public void Evaluate_DescentAtRelease_ReturnsFalseForAscent()
    {
        bool fires = HeadVerticalOffsetPoseTransition.Evaluate(
            descentMetres: ReleaseOffset,
            triggerOffsetMetres: TriggerOffset,
            releaseOffsetMetres: ReleaseOffset,
            triggerOnDescent: false);

        Assert.False(fires);
    }

    /// <summary>
    /// Values inside the hysteresis band (between release and trigger thresholds) must not fire
    /// either the descent-trigger or the ascent-release transition. This test bundles both
    /// edges to make the flicker contract explicit.
    /// </summary>
    [Theory]
    [InlineData(0.09f)]
    [InlineData(0.12f)]
    [InlineData(0.145f)]
    public void Evaluate_InsideHysteresisBand_NeitherEdgeFires(float descent)
    {
        bool descentFires = HeadVerticalOffsetPoseTransition.Evaluate(
            descent,
            TriggerOffset,
            ReleaseOffset,
            triggerOnDescent: true);

        bool ascentFires = HeadVerticalOffsetPoseTransition.Evaluate(
            descent,
            TriggerOffset,
            ReleaseOffset,
            triggerOnDescent: false);

        Assert.False(descentFires);
        Assert.False(ascentFires);
    }

    /// <summary>
    /// Negative descent (head above rest) must not fire the descent-trigger transition.
    /// </summary>
    [Fact]
    public void Evaluate_HeadAboveRest_DoesNotFireDescentEdge()
    {
        bool fires = HeadVerticalOffsetPoseTransition.Evaluate(
            descentMetres: -0.05f,
            triggerOffsetMetres: TriggerOffset,
            releaseOffsetMetres: ReleaseOffset,
            triggerOnDescent: true);

        Assert.False(fires);
    }
}
