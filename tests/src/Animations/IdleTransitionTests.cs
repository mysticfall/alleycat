using AlleyCat.Animations;
using Xunit;
using static AlleyCat.Tests.Animations.VariantTestEntries;

namespace AlleyCat.Tests.Animations;

/// <summary>ANIM-003 TR18–TR20: settle easing, transition blend weights and the bridge playback plan.</summary>
public sealed class IdleTransitionTests
{
    private const double Bridge = 49.0 / 30;

    /// <summary>Ease-in and ease-out start and end at zero rate and meet the unwarped middle at full rate (TR18).</summary>
    [Fact]
    public void Warp_EasesFromAndToRestWithContinuousRate()
    {
        var warp = BridgeTimeWarp.Create(Bridge, .2, 1.0 / 3);

        Assert.Equal(0, warp.Rate(0));
        Assert.Equal(0, warp.Rate(warp.PlaybackDuration));
        Assert.Equal(1, warp.Rate(warp.EaseInPlayback), 9);
        Assert.Equal(1, warp.Rate(warp.PlaybackDuration - warp.EaseOutPlayback), 9);
        Assert.Equal(0, warp.SourceTime(0));
        Assert.Equal(Bridge, warp.SourceTime(warp.PlaybackDuration), 9);
        Assert.Equal(Bridge + .1 + (1.0 / 6), warp.PlaybackDuration, 9);
        // Each ease covers half its playback span of source time.
        Assert.Equal(.1, warp.SourceTime(.2), 9);
        Assert.Equal(Bridge - (1.0 / 6), warp.SourceTime(warp.PlaybackDuration - (1.0 / 3)), 9);
    }

    /// <summary>Source time never runs backwards or past the bridge, and its slope matches the reported rate.</summary>
    [Fact]
    public void Warp_IsMonotonicAndClamped()
    {
        var warp = BridgeTimeWarp.Create(Bridge, .25, .3);
        double previous = 0;
        for (double t = -.1; t <= warp.PlaybackDuration + .1; t += 1.0 / 240)
        {
            double source = warp.SourceTime(t);
            Assert.InRange(source, 0, Bridge);
            Assert.True(source >= previous - 1e-12);
            if (t > 1e-3 && t < warp.PlaybackDuration - 1e-3)
            {
                double slope = (warp.SourceTime(t + 1e-6) - warp.SourceTime(t - 1e-6)) / 2e-6;
                Assert.Equal(warp.Rate(t), slope, 4);
            }

            previous = source;
        }
    }

    /// <summary>Unwarped bridges play at source rate; eases longer than the bridge shrink rather than overlap.</summary>
    [Fact]
    public void Warp_IdentityAndShortBridges()
    {
        var identity = BridgeTimeWarp.Identity(Bridge);
        Assert.Equal(Bridge, identity.PlaybackDuration);
        Assert.Equal(.7, identity.SourceTime(.7), 12);
        Assert.Equal(1, identity.Rate(0));
        Assert.Equal(1, identity.Rate(Bridge));

        var shortBridge = BridgeTimeWarp.Create(.2, 1, 1);
        Assert.Equal(.2, shortBridge.EaseInSource + shortBridge.EaseOutSource, 12);
        Assert.Equal(.2, shortBridge.SourceTime(shortBridge.PlaybackDuration), 12);
        Assert.Equal(0, shortBridge.Rate(shortBridge.PlaybackDuration));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => BridgeTimeWarp.Create(-1, 0, 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => BridgeTimeWarp.Create(1, double.NaN, 0));
    }

    /// <summary>The velocity blend weight starts and ends flat, so neither side gets a velocity step (TR20).</summary>
    [Fact]
    public void Blend_IsSmoothAndBounded()
    {
        const double duration = 5.0 / 30;
        Assert.Equal(0, TransitionBlend.Weight(0, duration));
        Assert.Equal(1, TransitionBlend.Weight(duration, duration));
        Assert.Equal(.5f, TransitionBlend.Weight(duration / 2, duration), 6);
        Assert.Equal(1, TransitionBlend.Weight(0, 0));
        double edge = (TransitionBlend.Weight(1e-4, duration) - TransitionBlend.Weight(0, duration)) / 1e-4;
        double middle = (TransitionBlend.Weight((duration / 2) + 1e-4, duration) - TransitionBlend.Weight(duration / 2, duration)) / 1e-4;
        Assert.True(edge < middle / 100);
    }

    /// <summary>Easing only touches catalogue-idle bridges; interruptions blend; idle↔idle holds the actor.</summary>
    [Theory]
    [InlineData(BridgeSource.NativeIdle, AnimationCategory.Idle, AnimationCategory.Idle, true, true, true, true, AnimationTransitionKind.IdleToIdle)]
    [InlineData(BridgeSource.NativeIdle, AnimationCategory.Idle, AnimationCategory.Locomotion, true, false, false, true, AnimationTransitionKind.IdleToLocomotion)]
    [InlineData(BridgeSource.StationaryBridge, AnimationCategory.Idle, AnimationCategory.Idle, false, true, true, true, AnimationTransitionKind.IdleToIdle)]
    [InlineData(BridgeSource.StationaryBridge, AnimationCategory.Idle, AnimationCategory.Locomotion, false, false, false, true, AnimationTransitionKind.IdleToLocomotion)]
    [InlineData(BridgeSource.TravellingBridge, AnimationCategory.Idle, AnimationCategory.Locomotion, false, false, false, true, AnimationTransitionKind.IdleToLocomotion)]
    [InlineData(BridgeSource.TravellingBridge, AnimationCategory.Locomotion, AnimationCategory.Idle, false, true, false, true, AnimationTransitionKind.LocomotionToIdle)]
    [InlineData(BridgeSource.Locomotion, AnimationCategory.Locomotion, AnimationCategory.Idle, false, true, false, false, AnimationTransitionKind.LocomotionToIdle)]
    [InlineData(BridgeSource.Locomotion, AnimationCategory.Locomotion, AnimationCategory.Locomotion, false, false, false, false, AnimationTransitionKind.LocomotionToLocomotion)]
    public void Planner_DecidesEasingBlendAndKind(BridgeSource source, AnimationCategory from, AnimationCategory to,
        bool easeIn, bool easeOut, bool stationary, bool blend, AnimationTransitionKind kind)
        => Assert.Equal(new IdleBridgePlan(easeIn, easeOut, stationary, blend, kind), IdleBridgePlanner.Plan(source, from, to));

    /// <summary>
    /// A NAV arrival target placed away from the actor travels even from an idle source; only an in-place idle change
    /// holds the actor (a short successor destination from a settled idle must move).
    /// </summary>
    [Theory]
    [InlineData(BridgeSource.NativeIdle)]
    [InlineData(BridgeSource.StationaryBridge)]
    public void Planner_TravellingIdleTargetIsNotStationary(BridgeSource source)
    {
        IdleBridgePlan held = IdleBridgePlanner.Plan(source, AnimationCategory.Idle, AnimationCategory.Idle);
        IdleBridgePlan travelling = IdleBridgePlanner.Plan(source, AnimationCategory.Idle, AnimationCategory.Idle, travellingTarget: true);

        Assert.True(held.Stationary);
        Assert.Equal(held with
        {
            Stationary = false
        }, travelling);
    }

    /// <summary>One announcement per bridge; an interruption announces only a different target.</summary>
    [Fact]
    public void Planner_AnnouncesOnlyNewTargets()
    {
        IReadOnlyList<AnimationEntry> entries = Entries((AnimationCategory.Idle, "sad"), (AnimationCategory.Idle, "slump"));
        Assert.True(IdleBridgePlanner.Announces(null, entries[0]));
        Assert.True(IdleBridgePlanner.Announces(entries[0], entries[1]));
        Assert.False(IdleBridgePlanner.Announces(entries[1], entries[1]));
    }
}
