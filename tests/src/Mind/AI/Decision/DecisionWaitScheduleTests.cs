using AlleyCat.Core.Configuration;
using AlleyCat.Mind.AI.Decision;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Decision;

/// <summary>
/// Deterministic coverage for the two-question wait gate's timer rules and timing configuration: immediate
/// verdicts fire whatever is armed, deferred verdicts arm their jittered timeout only when no deadline is armed or
/// theirs is sooner (never extending or resetting), explicit <c>wait(seconds)</c> seeds the initial deadline under
/// the same rules, failures leave the schedule untouched, the jitter stays within its configured bounds, and the
/// <c>DecisionWait</c> section defaults and validates.
/// </summary>
public sealed class DecisionWaitScheduleTests
{
    /// <summary>Timing without jitter so expected deadlines follow directly from the configured seconds.</summary>
    private static readonly DecisionWaitTimingSettings _exactTiming = new(5d, 3d, 10d, 0d);

    /// <summary>A deferred verdict with nothing armed arms its own timeout, sourced from the decision.</summary>
    [Theory]
    [InlineData("ResponsePending", 5d)]
    [InlineData("MayAct", 3d)]
    [InlineData("NoReason", 10d)]
    public void Defer_WithNothingArmed_ArmsTheVerdictTimeout(string armName, double expectedSeconds)
    {
        DecisionWaitArm arm = Enum.Parse<DecisionWaitArm>(armName);
        DecisionWaitSchedule schedule = new(_exactTiming, static () => 0.5d);

        DecisionWaitScheduleStep step = schedule.Apply(DecisionWaitDecision.DeferDecision(arm), 100d);

        Assert.Equal(DecisionWaitScheduleStepKind.Armed, step.Kind);
        Assert.Equal(100d + expectedSeconds, schedule.DeadlineAt);
        Assert.Equal(DecisionWaitDeadlineSource.Decision, schedule.Source);
        Assert.Equal(expectedSeconds, step.RemainingSeconds);
        Assert.False(schedule.IsDue(100d + expectedSeconds - 0.01d));
        Assert.True(schedule.IsDue(100d + expectedSeconds));
    }

    /// <summary>A later verdict with a later deadline never extends or resets the armed one; a sooner one
    /// replaces it.</summary>
    [Fact]
    public void Defer_ShortensButNeverExtends()
    {
        DecisionWaitSchedule schedule = new(_exactTiming, static () => 0.5d);

        _ = schedule.Apply(DecisionWaitDecision.DeferDecision(DecisionWaitArm.NoReason), 0d);
        Assert.Equal(10d, schedule.DeadlineAt);

        // The same verdict again later would push the deadline to 14 s: kept at 10 s.
        DecisionWaitScheduleStep repeated = schedule.Apply(DecisionWaitDecision.DeferDecision(DecisionWaitArm.NoReason), 4d);
        Assert.Equal(DecisionWaitScheduleStepKind.Kept, repeated.Kind);
        Assert.Equal(10d, schedule.DeadlineAt);
        Assert.Equal(6d, repeated.RemainingSeconds);
        Assert.Equal("keep-6.0s", repeated.Token);

        // A may-act verdict at 5 s arms 8 s, sooner than 10 s: replaces it.
        DecisionWaitScheduleStep sooner = schedule.Apply(DecisionWaitDecision.DeferDecision(DecisionWaitArm.MayAct), 5d);
        Assert.Equal(DecisionWaitScheduleStepKind.Armed, sooner.Kind);
        Assert.Equal(8d, schedule.DeadlineAt);
        Assert.Equal("arm-3.0s", sooner.Token);

        // A pending verdict at 6 s would arm 11 s: kept at 8 s.
        DecisionWaitScheduleStep later = schedule.Apply(DecisionWaitDecision.DeferDecision(DecisionWaitArm.ResponsePending), 6d);
        Assert.Equal(DecisionWaitScheduleStepKind.Kept, later.Kind);
        Assert.Equal(8d, schedule.DeadlineAt);
    }

    /// <summary>An immediate verdict fires even while a deadline runs, whoever armed it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActNow_FiresWhileADeadlineRuns(bool explicitSeed)
    {
        DecisionWaitSchedule schedule = new(_exactTiming, static () => 0.5d);
        if (explicitSeed)
        {
            schedule.SeedExplicit(0d, 30d);
        }
        else
        {
            _ = schedule.Apply(DecisionWaitDecision.DeferDecision(DecisionWaitArm.NoReason), 0d);
        }

        DecisionWaitScheduleStep step = schedule.Apply(DecisionWaitDecision.ActNowDecision(), 1d);

        Assert.Equal(DecisionWaitScheduleStepKind.ActNow, step.Kind);
        Assert.Equal("now", step.Token);
    }

    /// <summary>The explicit seconds seed the initial deadline exactly (no jitter); a sooner decision deadline
    /// replaces it, a later one never extends it.</summary>
    [Fact]
    public void ExplicitSeconds_SeedTheInitialDeadlineUnderTheSameRules()
    {
        DecisionWaitSchedule schedule = new(new DecisionWaitTimingSettings(5d, 3d, 10d, 0.2d), static () => 0.99d);

        schedule.SeedExplicit(50d, 4d);
        Assert.Equal(54d, schedule.DeadlineAt);
        Assert.Equal(DecisionWaitDeadlineSource.Explicit, schedule.Source);

        // No reason (10 s ± 20%) is later than the 4 s request: the explicit deadline stays.
        DecisionWaitScheduleStep kept = schedule.Apply(DecisionWaitDecision.DeferDecision(DecisionWaitArm.NoReason), 50d);
        Assert.Equal(DecisionWaitScheduleStepKind.Kept, kept.Kind);
        Assert.Equal(54d, schedule.DeadlineAt);
        Assert.Equal(DecisionWaitDeadlineSource.Explicit, schedule.Source);
        Assert.Equal(DecisionWaitDeadlineSource.Explicit, kept.Source);

        DecisionWaitSchedule longWait = new(_exactTiming, static () => 0.5d);
        longWait.SeedExplicit(0d, 20d);
        DecisionWaitScheduleStep replaced = longWait.Apply(DecisionWaitDecision.DeferDecision(DecisionWaitArm.MayAct), 1d);
        Assert.Equal(DecisionWaitScheduleStepKind.Armed, replaced.Kind);
        Assert.Equal(4d, longWait.DeadlineAt);
        Assert.Equal(DecisionWaitDeadlineSource.Decision, longWait.Source);
    }

    /// <summary>A failure with a sooner decision deadline armed keeps it: the failure's no-reason timeout would
    /// be later, so the deadline is neither extended nor fired early.</summary>
    [Fact]
    public void Failure_KeepsASoonerArmedDecisionDeadline()
    {
        DecisionWaitSchedule schedule = new(_exactTiming, static () => 0.5d);
        _ = schedule.Apply(DecisionWaitDecision.DeferDecision(DecisionWaitArm.ResponsePending), 0d);

        DecisionWaitScheduleStep step = schedule.Apply(DecisionWaitDecision.Failed("decision failure Timeout"), 2d);

        Assert.Equal(DecisionWaitScheduleStepKind.Failure, step.Kind);
        Assert.Equal("failed", step.Token);
        Assert.Equal(DecisionWaitArm.NoReason, step.Arm);
        Assert.Equal(10d, step.RequestedSeconds);
        Assert.Equal(3d, step.RemainingSeconds);
        Assert.Equal(5d, schedule.DeadlineAt);
        Assert.Equal(DecisionWaitDeadlineSource.Decision, schedule.Source);
        Assert.True(schedule.IsDue(5d));
    }

    /// <summary>A failure with a sooner explicit deadline keeps it, still sourced from the explicit request.</summary>
    [Fact]
    public void Failure_KeepsASoonerExplicitDeadline()
    {
        DecisionWaitSchedule schedule = new(_exactTiming, static () => 0.5d);
        schedule.SeedExplicit(0d, 4d);

        DecisionWaitScheduleStep step = schedule.Apply(DecisionWaitDecision.Failed("refusal"), 1d);

        Assert.Equal(DecisionWaitScheduleStepKind.Failure, step.Kind);
        Assert.Equal(4d, schedule.DeadlineAt);
        Assert.Equal(DecisionWaitDeadlineSource.Explicit, schedule.Source);
        Assert.Equal(DecisionWaitDeadlineSource.Explicit, step.Source);
    }

    /// <summary>A failure with nothing armed arms the jittered no-reason timeout, so a parked wait always wakes
    /// eventually — never immediately.</summary>
    [Fact]
    public void Failure_WithNothingArmed_ArmsTheJitteredNoReasonTimeout()
    {
        DecisionWaitSchedule schedule = new(new DecisionWaitTimingSettings(5d, 3d, 10d, 0.2d), static () => 0d);

        DecisionWaitScheduleStep step = schedule.Apply(DecisionWaitDecision.Failed("decision failure Transport"), 100d);

        Assert.Equal(DecisionWaitScheduleStepKind.Failure, step.Kind);
        Assert.Equal("failed", step.Token);
        Assert.Equal(DecisionWaitArm.NoReason, step.Arm);
        Assert.Equal(8d, step.RequestedSeconds!.Value, 9);
        Assert.Equal(108d, schedule.DeadlineAt!.Value, 9);
        Assert.Equal(DecisionWaitDeadlineSource.Decision, schedule.Source);
        Assert.False(schedule.IsDue(100d));
    }

    /// <summary>Repeated failures never extend the deadline the first one armed.</summary>
    [Fact]
    public void Failure_RepeatedFailuresNeverExtendTheDeadline()
    {
        DecisionWaitSchedule schedule = new(_exactTiming, static () => 0.5d);
        _ = schedule.Apply(DecisionWaitDecision.Failed("refusal"), 0d);
        Assert.Equal(10d, schedule.DeadlineAt);

        for (int second = 1; second <= 9; second++)
        {
            DecisionWaitScheduleStep step = schedule.Apply(DecisionWaitDecision.Failed("refusal"), second);
            Assert.Equal(10d, schedule.DeadlineAt);
            Assert.Equal(10d - second, step.RemainingSeconds);
        }

        Assert.True(schedule.IsDue(10d));
    }

    /// <summary>Jitter scales the base timeout uniformly within ±ratio: the unit-sample extremes map to the bounds
    /// and a seeded sampler's draws all stay inside them.</summary>
    [Fact]
    public void Jitter_StaysWithinTheConfiguredProportionalBounds()
    {
        DecisionWaitTimingSettings timing = new(5d, 3d, 10d, 0.2d);
        Assert.Equal(8d, timing.SampleSeconds(DecisionWaitArm.NoReason, 0d), 9);
        Assert.Equal(10d, timing.SampleSeconds(DecisionWaitArm.NoReason, 0.5d), 9);
        Assert.Equal(12d, timing.SampleSeconds(DecisionWaitArm.NoReason, 1d), 9);

        Random random = new(1234);
        for (int index = 0; index < 200; index++)
        {
            DecisionWaitSchedule fresh = new(timing, random.NextDouble);
            DecisionWaitScheduleStep step = fresh.Apply(DecisionWaitDecision.DeferDecision(DecisionWaitArm.MayAct), 0d);
            Assert.InRange(step.RequestedSeconds!.Value, 2.4d, 3.6d);
        }
    }

    /// <summary>An empty section yields the shipped defaults: 5 s pending, 3 s opening, 10 s no reason, ±20%.</summary>
    [Fact]
    public void Configuration_AbsentSectionYieldsTheDefaults()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        var timing = DecisionWaitTimingSettings.Load(configuration);

        Assert.Equal(new DecisionWaitTimingSettings(5d, 3d, 10d, 0.2d), timing);
        Assert.Equal(DecisionWaitTimingSettings.Default, timing);
    }

    /// <summary>The shipped configuration file declares the same defaults the code falls back to.</summary>
    [Fact]
    public void Configuration_ShippedFileMatchesTheDefaults()
    {
        IConfiguration configuration = GameConfiguration.BuildFile(
            new IdentityPathResolver(),
            RepositoryPath.Get("game", "AlleyCat.yaml"));

        Assert.Equal(DecisionWaitTimingSettings.Default, DecisionWaitTimingSettings.Load(configuration));
    }

    /// <summary>Configured keys override their defaults individually.</summary>
    [Fact]
    public void Configuration_PresentKeysOverrideTheDefaults()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DecisionWait:ResponsePendingTimeout"] = "7.5",
            ["DecisionWait:TimeoutJitter"] = "0",
        }).Build();

        Assert.Equal(
            new DecisionWaitTimingSettings(7.5d, 3d, 10d, 0d),
            DecisionWaitTimingSettings.Load(configuration));
    }

    /// <summary>Invalid values fail with a configuration error naming the key, never silently defaulting.</summary>
    [Theory]
    [InlineData("ResponsePendingTimeout", "0")]
    [InlineData("MayActTimeout", "-1")]
    [InlineData("NoReasonTimeout", "soon")]
    [InlineData("NoReasonTimeout", "NaN")]
    [InlineData("TimeoutJitter", "1.5")]
    [InlineData("TimeoutJitter", "-0.1")]
    public void Configuration_InvalidValuesFailWithTheKey(string key, string value)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"DecisionWait:{key}"] = value,
        }).Build();

        DecisionException exception = Assert.Throws<DecisionException>(() => DecisionWaitTimingSettings.Load(configuration));
        Assert.Equal(DecisionFailure.Configuration, exception.Failure);
        Assert.Equal($"Invalid DecisionWait:{key}.", exception.Message);
    }

    private sealed class IdentityPathResolver : IConfigurationPathResolver
    {
        public string ToPhysicalPath(string path) => path;
    }
}
