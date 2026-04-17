using AlleyCat.IK.Pose;
using Xunit;

namespace AlleyCat.Tests.IK.Pose;

/// <summary>
/// Unit coverage for the pose-blend math and travel-decision helper exposed by
/// <see cref="TimeSeekAnimationBinding"/>.
/// </summary>
/// <remarks>
/// The binding itself writes to a live Godot <see cref="Godot.AnimationTree"/> and
/// <see cref="Godot.AnimationNodeStateMachinePlayback"/> and therefore cannot be fully
/// instantiated without the engine. These tests exercise the pure static helpers:
/// <see cref="TimeSeekAnimationBinding.ComputePoseBlend"/> and
/// <see cref="TimeSeekAnimationBinding.ShouldTravel"/>. The latter captures the
/// unconditional travel semantic introduced after removing the buggy per-binding-instance
/// <c>_lastTravelledState</c> cache that suppressed re-travels on round trips such as
/// Standing → Crouching → Standing.
/// </remarks>
public sealed class TimeSeekAnimationBindingTests
{
    private const float Depth = 0.6f;

    /// <summary>
    /// Zero descent must yield a fully standing (0.0) blend.
    /// </summary>
    [Fact]
    public void ComputePoseBlend_ZeroDescent_ReturnsZero()
    {
        float blend = TimeSeekAnimationBinding.ComputePoseBlend(
            restHeadY: 1.6f,
            currentHeadY: 1.6f,
            maximumCrouchDepthMetres: Depth);

        Assert.Equal(0f, blend);
    }

    /// <summary>
    /// Descent equal to the configured maximum depth must yield fully crouched (1.0).
    /// </summary>
    [Fact]
    public void ComputePoseBlend_DescentEqualsDepth_ReturnsOne()
    {
        float blend = TimeSeekAnimationBinding.ComputePoseBlend(
            restHeadY: 1.6f,
            currentHeadY: 1.6f - Depth,
            maximumCrouchDepthMetres: Depth);

        Assert.Equal(1f, blend);
    }

    /// <summary>
    /// Descent beyond maximum depth must clamp at 1.0 rather than extrapolate.
    /// </summary>
    [Fact]
    public void ComputePoseBlend_DescentBeyondDepth_ClampsToOne()
    {
        float blend = TimeSeekAnimationBinding.ComputePoseBlend(
            restHeadY: 1.6f,
            currentHeadY: 1.6f - (Depth * 2f),
            maximumCrouchDepthMetres: Depth);

        Assert.Equal(1f, blend);
    }

    /// <summary>
    /// Negative descent (head above rest) must clamp at 0.0.
    /// </summary>
    [Fact]
    public void ComputePoseBlend_NegativeDescent_ClampsToZero()
    {
        float blend = TimeSeekAnimationBinding.ComputePoseBlend(
            restHeadY: 1.6f,
            currentHeadY: 1.8f,
            maximumCrouchDepthMetres: Depth);

        Assert.Equal(0f, blend);
    }

    /// <summary>
    /// A descent of half the configured depth must produce approximately 0.5 blend.
    /// </summary>
    [Fact]
    public void ComputePoseBlend_HalfDescent_ReturnsHalf()
    {
        float blend = TimeSeekAnimationBinding.ComputePoseBlend(
            restHeadY: 1.6f,
            currentHeadY: 1.6f - (Depth * 0.5f),
            maximumCrouchDepthMetres: Depth);

        Assert.Equal(0.5f, blend, precision: 5);
    }

    /// <summary>
    /// A non-positive configured depth must not produce NaN or flip the sign of the blend —
    /// the internal floor defends against divide-by-zero and keeps behaviour sane.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-0.1f)]
    public void ComputePoseBlend_NonPositiveDepth_DoesNotProduceNaN(float depth)
    {
        float blend = TimeSeekAnimationBinding.ComputePoseBlend(
            restHeadY: 1.6f,
            currentHeadY: 1.4f,
            maximumCrouchDepthMetres: depth);

        Assert.False(float.IsNaN(blend));
        Assert.InRange(blend, 0f, 1f);
    }

    /// <summary>
    /// A populated target must always request a travel, regardless of the previously
    /// travelled state. This is the core of the round-trip fix: the previous per-binding
    /// cache caused the <c>lastTravelled == currentTargetName</c> check to suppress
    /// legitimate re-travels; the new contract ignores <c>lastTravelled</c> entirely.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Idle")]
    [InlineData("Crouch-seek")]
    public void ShouldTravel_PopulatedTarget_IgnoresLastTravelled(string? lastTravelled) =>
        Assert.True(TimeSeekAnimationBinding.ShouldTravel("Idle", lastTravelled));

    /// <summary>
    /// Simulates the Standing → Crouching → Standing round trip across two independent
    /// binding instances. Each instance owns its own <c>lastTravelled</c> observation; the
    /// helper must report a travel on every switch, including the return to Standing where
    /// the old per-instance cache on the Standing binding still held <c>Idle</c>.
    /// </summary>
    /// <remarks>
    /// Before the fix, <c>ShouldTravel("Idle", "Idle")</c> on tick 3 returned
    /// <see langword="false"/> because the Standing binding's cache was never invalidated
    /// while Crouching was active. The new unconditional semantic returns
    /// <see langword="true"/> here so the AnimationTree returns to <c>Idle</c> and is not
    /// stranded on <c>Crouch-seek</c>.
    /// </remarks>
    [Fact]
    public void ShouldTravel_StandingCrouchingStandingRoundTrip_RequestsTravelEachSwitch()
    {
        // Tick 1: Standing active — its binding has never travelled before.
        string? standingLastTravelled = null;
        bool standingFirstTravel = TimeSeekAnimationBinding.ShouldTravel(
            currentTargetName: "Idle",
            lastTravelled: standingLastTravelled);
        // After this tick the binding would previously have cached "Idle".
        standingLastTravelled = "Idle";

        // Tick 2: Crouching active — its binding requests a travel to Crouch-seek. The
        // Standing binding is dormant this tick and its cache is therefore not refreshed.
        string? crouchingLastTravelled = null;
        bool crouchingTravel = TimeSeekAnimationBinding.ShouldTravel(
            currentTargetName: "Crouch-seek",
            lastTravelled: crouchingLastTravelled);
        crouchingLastTravelled = "Crouch-seek";

        // Tick 3: Standing active again. The Standing binding's cached lastTravelled is
        // still "Idle" from tick 1. The old cached implementation skipped the travel here
        // and left the AnimationTree on Crouch-seek. The new contract must report a
        // travel.
        bool standingReturnTravel = TimeSeekAnimationBinding.ShouldTravel(
            currentTargetName: "Idle",
            lastTravelled: standingLastTravelled);

        Assert.True(standingFirstTravel);
        Assert.True(crouchingTravel);
        Assert.True(standingReturnTravel);

        // The crouchingLastTravelled variable is retained to document the independent
        // per-instance caches; asserting it suppresses unused-variable diagnostics without
        // changing the test's intent.
        Assert.Equal("Crouch-seek", crouchingLastTravelled);
    }

    /// <summary>
    /// The helper is idempotent for same-target ticks, matching the per-frame invocation
    /// pattern from <see cref="TimeSeekAnimationBinding.Apply"/>.
    /// </summary>
    [Fact]
    public void ShouldTravel_SameTargetAcrossTicks_AlwaysRequestsTravel()
    {
        for (int tick = 0; tick < 5; tick++)
        {
            Assert.True(TimeSeekAnimationBinding.ShouldTravel(
                currentTargetName: "Idle",
                lastTravelled: "Idle"));
        }
    }

    /// <summary>
    /// A null or empty target state name indicates the owning pose state does not map to
    /// an AnimationTree state-machine state; the helper must suppress the travel call.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ShouldTravel_NullOrEmptyTarget_ReturnsFalse(string? currentTarget)
    {
        Assert.False(TimeSeekAnimationBinding.ShouldTravel(
            currentTargetName: currentTarget,
            lastTravelled: "Idle"));
    }
}
