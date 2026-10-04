using AlleyCat.Control.Locomotion;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Control;

/// <summary>
/// Pure tests for the ANIM-002 Planted Combination Rule on synthetic single-clip, crossfade, and slice-join
/// fixtures, including normalised weights just above and below the fixed 0.1 threshold.
/// </summary>
public sealed class PlantedCombinationRuleTests
{
    // "cycle" (looping, 1 s): left planted [0, 0.5), swing [0.5, 1); right swing [0, 0.5), planted [0.5, 1).
    // "stop" (finite, 2 s): left planted throughout; right swing [0, 1), planted [1, 2].
    // "idle" (looping, 4 s): both planted throughout.
    private const string Json = /*lang=json,strict*/ """
        {
          "schema_version": 1,
          "contract": "anim002_per_foot_planted_intervals_v1",
          "character": "unit_character",
          "package": "unit_package",
          "variant": "unit_variant",
          "derivation": {
            "tool": "derive_planted_intervals",
            "tool_version": 3,
            "runtime_library_sha256": "1d19ab6cea1454c45620dc7b4d5e92b28aab1b9103ed9e83de82d05fb918395b"
          },
          "clips": [
            {
              "identity": "cycle", "role": "wf", "capability": "cyclic_forward_primary", "looping": true,
              "duration_seconds": 1.0,
              "feet": {
                "left": { "baseline_ball_height_m": 0.0, "segments": [
                  { "state": "planted", "start_seconds": 0.0, "end_seconds": 0.5 },
                  { "state": "swing", "start_seconds": 0.5, "end_seconds": 1.0 } ] },
                "right": { "baseline_ball_height_m": 0.0, "segments": [
                  { "state": "swing", "start_seconds": 0.0, "end_seconds": 0.5 },
                  { "state": "planted", "start_seconds": 0.5, "end_seconds": 1.0 } ] }
              }
            },
            {
              "identity": "stop", "role": "f2i_l", "capability": "exit_forward_left", "looping": false,
              "duration_seconds": 2.0,
              "feet": {
                "left": { "baseline_ball_height_m": 0.0, "segments": [
                  { "state": "planted", "start_seconds": 0.0, "end_seconds": 2.0 } ] },
                "right": { "baseline_ball_height_m": 0.0, "segments": [
                  { "state": "swing", "start_seconds": 0.0, "end_seconds": 1.0 },
                  { "state": "planted", "start_seconds": 1.0, "end_seconds": 2.0 } ] }
              }
            },
            {
              "identity": "idle", "role": "idle", "capability": "idle", "looping": true,
              "duration_seconds": 4.0,
              "feet": {
                "left": { "baseline_ball_height_m": 0.0, "segments": [
                  { "state": "planted", "start_seconds": 0.0, "end_seconds": 4.0 } ] },
                "right": { "baseline_ball_height_m": 0.0, "segments": [
                  { "state": "planted", "start_seconds": 0.0, "end_seconds": 4.0 } ] }
              }
            }
          ]
        }
        """;

    private static readonly PerFootPlantedIntervals _intervals = PerFootPlantedIntervals.Parse(Json);
    private static readonly PlantedClipIntervals _cycle = _intervals["cycle"];
    private static readonly PlantedClipIntervals _stop = _intervals["stop"];
    private static readonly PlantedClipIntervals _idle = _intervals["idle"];

    [Fact]
    public void WeightThreshold_IsTheFixedSpecConstant() => Assert.Equal(0.1d, PlantedCombinationRule.WeightThreshold);

    [Theory]
    [InlineData(0.25, true, false)]
    [InlineData(0.75, false, true)]
    [InlineData(1.25, true, false)]
    [InlineData(-0.25, false, true)]
    public void SingleClip_AppliesItsOwnIntervalsIncludingLoopWrap(double time, bool left, bool right) =>
        Assert.Equal(new PlantedFeetState(left, right), PlantedCombinationRule.Evaluate([new(_cycle, time, 1d)]));

    [Fact]
    public void SingleClip_WeightNeedNotBeNormalised() =>
        Assert.Equal(new PlantedFeetState(true, false), PlantedCombinationRule.Evaluate([new(_cycle, 0.25d, 0.002d)]));

    [Fact]
    public void SingleFiniteClip_ClampsSourceTime()
    {
        Assert.Equal(new PlantedFeetState(true, true), PlantedCombinationRule.Evaluate([new(_stop, 5d, 1d)]));
        Assert.Equal(new PlantedFeetState(true, false), PlantedCombinationRule.Evaluate([new(_stop, -1d, 1d)]));
    }

    [Fact]
    public void Crossfade_PlantsOnlyFeetEveryDecidingClipPlants()
    {
        // Travel (left planted) fading into stop (left planted, right swing until 1 s), both well above threshold.
        PlantedFeetState state = PlantedCombinationRule.Evaluate([new(_cycle, 0.25d, 0.6d), new(_stop, 0.5d, 0.4d)]);

        Assert.Equal(new PlantedFeetState(true, false), state);
    }

    [Fact]
    public void Crossfade_DisagreementOnEitherFootUnplantsIt()
    {
        // Cycle plants the right foot at 0.75 s while stop plants the left foot throughout.
        PlantedFeetState state = PlantedCombinationRule.Evaluate([new(_cycle, 0.75d, 0.5d), new(_stop, 0.5d, 0.5d)]);

        Assert.Equal(new PlantedFeetState(false, false), state);
    }

    [Theory]
    [InlineData(0.1001, false)]
    [InlineData(0.1, true)]
    [InlineData(0.0999, true)]
    public void Crossfade_OnlyNormalisedWeightsAboveThresholdDecide(double swingingWeight, bool rightPlanted)
    {
        // Idle plants both feet; the cycle at 0.25 s swings the right foot and decides only above 0.1.
        PlantedFeetState state = PlantedCombinationRule.Evaluate(
            [new(_idle, 1d, 1d - swingingWeight), new(_cycle, 0.25d, swingingWeight)]);

        Assert.Equal(new PlantedFeetState(true, rightPlanted), state);
    }

    [Theory]
    [InlineData(1.001, false)]
    [InlineData(0.999, true)]
    public void Threshold_AppliesToNormalisedNotRawWeights(double swingingRawWeight, bool rightPlanted)
    {
        // Raw weights 9 : w normalise to w / (9 + w); 1.001 → 0.10001 decides, 0.999 → 0.09990 does not.
        PlantedFeetState state = PlantedCombinationRule.Evaluate(
            [new(_idle, 0d, 9d), new(_cycle, 0.25d, swingingRawWeight)]);

        Assert.Equal(rightPlanted, state.Right);
        Assert.True(state.Left);
    }

    [Fact]
    public void SliceJoin_SameClipAtTwoPositionsIsTwoContributions()
    {
        // Head and tail slices of the cycle both left-planted; tail at 0.6 s swings the left foot.
        Assert.True(PlantedCombinationRule.IsPlanted(
            [new(_cycle, 0.1d, 0.5d), new(_cycle, 0.4d, 0.5d)],
            StandingLocomotionAnatomicalSide.Left));
        Assert.False(PlantedCombinationRule.IsPlanted(
            [new(_cycle, 0.1d, 0.5d), new(_cycle, 0.6d, 0.5d)],
            StandingLocomotionAnatomicalSide.Left));
    }

    [Fact]
    public void BlendSpaceTriangle_ThreeContributionsNormaliseTogether()
    {
        // 0.85 : 0.1 : 0.05 over three clips; the two small ones sit at or below the threshold.
        PlantedFeetState state = PlantedCombinationRule.Evaluate(
            [new(_idle, 0d, 0.85d), new(_cycle, 0.25d, 0.1d), new(_stop, 0.1d, 0.05d)]);

        Assert.Equal(new PlantedFeetState(true, true), state);
    }

    [Fact]
    public void NoDecidingContribution_IsNotPlanted()
    {
        // Ten equal weights normalise to exactly 0.1 each, so none exceeds the threshold; every clip plants both feet,
        // but without a deciding contribution the foot must not be vacuously planted.
        PlantedClipContribution[] contributions = [.. Enumerable.Repeat(new PlantedClipContribution(_idle, 1d, 1d), 10)];

        Assert.Equal(new PlantedFeetState(false, false), PlantedCombinationRule.Evaluate(contributions));
        Assert.False(PlantedCombinationRule.IsPlanted(contributions, StandingLocomotionAnatomicalSide.Left));
    }

    [Fact]
    public void NineEqualContributions_StillDecide() =>
        Assert.Equal(
            new PlantedFeetState(true, true),
            PlantedCombinationRule.Evaluate([.. Enumerable.Repeat(new PlantedClipContribution(_idle, 1d, 1d), 9)]));

    [Fact]
    public void PlantedFeetState_IsPlantedSelectsTheFoot()
    {
        var state = new PlantedFeetState(true, false);

        Assert.True(state.IsPlanted(StandingLocomotionAnatomicalSide.Left));
        Assert.False(state.IsPlanted(StandingLocomotionAnatomicalSide.Right));
    }

    [Fact]
    public void InvalidContributions_FailClosed()
    {
        _ = Assert.Throws<ArgumentException>(() => PlantedCombinationRule.Evaluate([]));
        _ = Assert.Throws<ArgumentException>(() => PlantedCombinationRule.Evaluate([new(_cycle, 0d, 0d)]));
        _ = Assert.Throws<ArgumentException>(() => PlantedCombinationRule.Evaluate([new(_cycle, 0d, -0.1d), new(_idle, 0d, 1d)]));
        _ = Assert.Throws<ArgumentException>(() => PlantedCombinationRule.Evaluate([new(_cycle, 0d, double.NaN)]));
        _ = Assert.Throws<ArgumentNullException>(() => PlantedCombinationRule.Evaluate([new(null!, 0d, 1d)]));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => PlantedCombinationRule.Evaluate([new(_cycle, double.NaN, 1d)]));
    }
}
