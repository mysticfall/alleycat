using System.Collections.Immutable;
using AlleyCat.Control.Locomotion;
using AlleyCat.Navigation.Arrival;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Navigation.Arrival;

/// <summary>
/// <see cref="SequencePredictor"/> on synthetic clips with analytic root motion: a static Idle, a forward entry, a forward
/// cycle with double-support and single-swing phases, a forward exit and a turning manoeuvre. Expected values come from
/// the clips' analytic trajectories and the engine's documented tick rules (a 0.15 s linear crossfade at 60 Hz weighs
/// <c>k / 9</c> on its <c>k</c>-th tick and ends on its ninth), not from the predictor's own code.
/// </summary>
public sealed class SequencePredictorTests
{
    private const double Rate = 30d;
    private const double Delta = 1d / 60d;
    private const double Crossfade = 0.15d;
    private const float Tolerance = 0.19f;
    private const float EntrySpeed = 0.6f;
    private const float CycleSpeed = 1.2f;
    private const float ExitSpeed = 0.3f;
    private const float Metres = 1e-5f;

    private static readonly Vector3 _leftBall = new(0.1f, 0f, 0f);
    private static readonly Vector3 _rightBall = new(-0.1f, 0f, 0f);
    private static readonly Vector3 _exitLeftBall = new(0.1f, 0f, -0.05f);

    private static readonly SequencePose _origin = new(Vector2.Zero, 0f);

    /// <summary>A looping cycle crosses its loop point without a root-motion jump and moves along the actor's facing.</summary>
    [Fact]
    public void SteadyCycle_WrapsAndMovesAlongTheActorFacing()
    {
        SequencePredictor predictor = Predictor();
        var pose = new SequencePose(new Vector2(1f, 2f), Mathf.Pi / 2f);

        SequencePrediction prediction = predictor.Predict(SequenceStart.Playing(pose, "wf", 0d, 0d, exitCycles: 1), []);

        Assert.True(prediction.IsValid, prediction.Rejection?.Detail);
        Assert.Equal(60, prediction.Ticks.Length);
        // Facing +90° (left of -Z) is world -X: one whole cycle of 1.2 m/s for 1 s, across the loop point.
        Assert.Equal(1f - CycleSpeed, prediction.EndPose.Position.X, Metres);
        Assert.Equal(2f, prediction.EndPose.Position.Y, Metres);
        Assert.Equal(Mathf.Pi / 2f, prediction.EndPose.Yaw, 1e-6f);
        Assert.All(prediction.Ticks, tick => Assert.Equal(CycleSpeed * Delta, tick.RootDelta.Y, Metres));
        Assert.All(prediction.Ticks, tick => Assert.Equal(1d, tick.Current.Weight, 1e-9));
    }

    /// <summary>On the entry tick the sought clip adds no root motion; the outgoing clip's weighted share continues.</summary>
    [Fact]
    public void EntryTick_EnteredClipGivesNoRootMotionAndTheOutgoingShareContinues()
    {
        SequencePrediction prediction = Predictor().Predict(
            SequenceStart.Playing(_origin, "wf", 0.4d, 0.5d),
            [new SequenceSegment("f2i_r", 0.2d, Crossfade, 0.5d)]);

        Assert.True(prediction.IsValid, prediction.Rejection?.Detail);
        Assert.Equal<int>([6], prediction.EntryTicks);
        PredictedTick before = prediction.Ticks[5];
        PredictedTick entry = prediction.Ticks[6];
        PredictedTick after = prediction.Ticks[7];

        Assert.False(before.IsEntryTick);
        Assert.Equal(0.5d, before.Current.PositionSeconds, 1e-9);
        Assert.Equal(CycleSpeed * Delta, before.RootDelta.Y, Metres);

        Assert.True(entry.IsEntryTick);
        Assert.Equal("f2i_r", entry.Current.Role);
        Assert.Equal(0.2d, entry.Current.PositionSeconds, 1e-9);
        Assert.Equal(1d / 9d, entry.Current.Weight, 1e-6);
        PredictedClipSample outgoing = Assert.NotNull(entry.FadingFrom);
        Assert.Equal("wf", outgoing.Role);
        Assert.Equal(0.5d + Delta, outgoing.PositionSeconds, 1e-9);
        Assert.Equal(8d / 9d, outgoing.Weight, 1e-6);
        // Only the outgoing cycle's weighted share: the exit clip was sought and contributes nothing.
        Assert.Equal(CycleSpeed * Delta * 8f / 9f, entry.RootDelta.Y, Metres);

        Assert.Equal(0.2d + Delta, after.Current.PositionSeconds, 1e-9);
        Assert.Equal((CycleSpeed * Delta * 7f / 9f) + (ExitSpeed * Delta * 2f / 9f), after.RootDelta.Y, Metres);
        // Pre-IK balls are the weight-blended clip balls.
        Vector3 blended = (_exitLeftBall * (2f / 9f)) + (_leftBall * (7f / 9f));
        Assert.True(after.LeftBall.DistanceTo(blended) < Metres, $"{after.LeftBall} vs {blended}");
    }

    /// <summary>A 0.15 s crossfade at 60 Hz weighs k/9 on its k-th tick and ends on its ninth.</summary>
    [Fact]
    public void Crossfade_IsLinearAndEndsOnItsNinthTick()
    {
        SequencePrediction prediction = Predictor().Predict(
            SequenceStart.Playing(_origin, "wf", 0.4d, 0.5d),
            [new SequenceSegment("f2i_r", 0.2d, Crossfade, 0.5d)]);

        Assert.True(prediction.IsValid, prediction.Rejection?.Detail);
        for (int k = 1; k <= 8; k++)
        {
            PredictedTick tick = prediction.Ticks[5 + k];
            Assert.Equal(k / 9d, tick.Current.Weight, 1e-6);
            Assert.Equal(1d - (k / 9d), Assert.NotNull(tick.FadingFrom).Weight, 1e-6);
        }

        // The ninth tick blends the outgoing clip at the engine epsilon only, below the mixer's zero threshold.
        Assert.Null(prediction.Ticks[14].FadingFrom);
        Assert.Equal(1d, prediction.Ticks[14].Current.Weight, 2e-5);
        Assert.Equal(1d, prediction.Ticks[15].Current.Weight, 1e-9);
    }

    /// <summary>A join requested while the previous crossfade runs is rejected; one requested after it ends is accepted.</summary>
    [Fact]
    public void Join_IsRefusedWhileTheCrossfadeRunsAndAcceptedOnTheTickAfterItEnds()
    {
        SequencePredictor predictor = Predictor();
        var start = SequenceStart.Playing(_origin, "wf", 0.4d, 0.5d);

        SequencePrediction early = predictor.Predict(
            start,
            [new SequenceSegment("f2i_r", 0.2d, Crossfade, 0.2d + (7d / 60d)), new SequenceSegment("idle", 0d, Crossfade, 0.5d)]);
        SequencePrediction onTime = predictor.Predict(
            start,
            [new SequenceSegment("f2i_r", 0.2d, Crossfade, 0.2d + (8d / 60d)), new SequenceSegment("idle", 0d, Crossfade, 0.5d)]);

        Assert.Equal(SequenceRejectionReason.CrossfadeStillRunning, early.Rejection?.Reason);
        Assert.Equal(1, early.Rejection?.Segment);
        Assert.True(onTime.IsValid, onTime.Rejection?.Detail);
        Assert.Equal<int>([6, 15], onTime.EntryTicks);
    }

    /// <summary>Rotating first and mapping the local delta through the rotated basis reproduces the authored root path.</summary>
    [Fact]
    public void TurningClip_RotateThenMapReproducesTheAuthoredRootPath()
    {
        // pl45 moves (0.1, 0.2) m in its start frame while turning 0.8 rad, played once through a teleport start.
        SequencePrediction prediction = Predictor().Predict(
            SequenceStart.Teleport(_origin),
            [new SequenceSegment("pl45", 0d, 0d, 1d)]);

        Assert.True(prediction.IsValid, prediction.Rejection?.Detail);
        Assert.Equal(61, prediction.Ticks.Length);
        Assert.Equal(Vector2.Zero, prediction.Ticks[0].RootDelta);
        Assert.Equal(0f, prediction.Ticks[0].YawDelta);
        // Semantic (right 0.1, forward 0.2) from a pose facing -Z is world (X 0.1, Z -0.2).
        Assert.Equal(0.1f, prediction.EndPose.Position.X, Metres);
        Assert.Equal(-0.2f, prediction.EndPose.Position.Y, Metres);
        Assert.Equal(0.8f, prediction.EndPose.Yaw, 1e-5f);
    }

    /// <summary>The local root delta is expressed in the root bone's absolute facing, including its first key's yaw.</summary>
    [Fact]
    public void RootFrame_IsTheRootBoneFacingIncludingItsFirstKeyYaw()
    {
        // A root bone facing 0.3 rad in skeleton space that moves 0.5 m along its own facing moves the actor forward.
        ClipTrajectory tilted = Trajectory(
            "pr45",
            StandingLocomotionCapability.ConfinedPivotRight,
            1d,
            time => ClipTrajectory.RotateSemantic(new Vector2(0f, 0.5f * (float)time), 0.3f),
            _ => 0.3f);
        var clip = SeamClip.FromTrajectory(tilted, SeamClipRole.Confined, AllPlanted("pr45", false, 1d));
        var idle = SeamClip.FromTrajectory(IdleTrajectory(), SeamClipRole.Idle, AllPlanted("idle", true, 2d));
        var predictor = new SequencePredictor(
            SeamTable.Build([clip, idle]),
            new Dictionary<string, ClipTrajectory> { ["pr45"] = tilted, ["idle"] = IdleTrajectory() },
            ["idle"],
            Tolerance);

        SequencePrediction prediction = predictor.Predict(SequenceStart.Teleport(_origin), [new SequenceSegment("pr45", 0d, 0d, 1d)]);

        Assert.True(prediction.IsValid, prediction.Rejection?.Detail);
        Assert.Equal(0f, prediction.EndPose.Position.X, Metres);
        Assert.Equal(-0.5f, prediction.EndPose.Position.Y, Metres);
        Assert.Equal(0f, prediction.EndPose.Yaw, 1e-6f);
    }

    /// <summary>A start inside a running crossfade blends it to completion and applies the Planted Combination Rule.</summary>
    [Fact]
    public void RunningCrossfadeStart_PlantedStateFollowsTheCombinationRule()
    {
        // Fading from the cycle in its left swing (0.3–0.5 s) into the all-planted exit: the swing decides while the
        // cycle weighs more than 0.1.
        var start = SequenceStart.Playing(
            _origin,
            "f2i_r",
            0d,
            0.1d,
            fading: new SequenceCrossfadeProgress("wf", 0.35d, 0.05f, 0.15f));

        SequencePrediction prediction = Predictor().Predict(start, []);

        Assert.True(prediction.IsValid, prediction.Rejection?.Detail);
        Assert.Equal(6, prediction.Ticks.Length);
        for (int index = 0; index < 5; index++)
        {
            Assert.Equal(new PlantedFeetState(false, true), prediction.Ticks[index].Planted);
        }

        Assert.Equal(new PlantedFeetState(true, true), prediction.Ticks[5].Planted);
        Assert.Equal(
            [(SupportPhase.LeftSwing, 0, 4), (SupportPhase.BothPlanted, 5, 5)],
            prediction.Windows.Select(window => (window.Phase, window.FirstTick, window.LastTick)));
    }

    /// <summary>Support windows partition the ticks by phase, contiguously, with their travel.</summary>
    [Fact]
    public void SupportWindows_PartitionTheTicksByPhaseWithTheirTravel()
    {
        SequencePrediction prediction = Predictor().Predict(SequenceStart.Playing(_origin, "wf", 0d, 0d, exitCycles: 1), []);

        Assert.True(prediction.IsValid, prediction.Rejection?.Detail);
        ImmutableArray<SupportWindow> windows = prediction.Windows;
        Assert.Equal(
            [SupportPhase.BothPlanted, SupportPhase.LeftSwing, SupportPhase.BothPlanted, SupportPhase.RightSwing, SupportPhase.BothPlanted],
            windows.Select(window => window.Phase));
        Assert.Equal(0, windows[0].FirstTick);
        Assert.Equal(59, windows[^1].LastTick);
        for (int index = 1; index < windows.Length; index++)
        {
            Assert.Equal(windows[index - 1].LastTick + 1, windows[index].FirstTick);
            Assert.Equal(windows[index - 1].EndPose, windows[index].StartPose);
        }

        // Swing windows last 0.2 s of the 1 s cycle (12 ticks, ±1 at the interval boundaries).
        Assert.InRange(windows[1].TickCount, 11, 13);
        Assert.InRange(windows[3].TickCount, 11, 13);
        foreach (SupportWindow window in windows)
        {
            float travel = window.StartPose.Position.DistanceTo(window.EndPose.Position);
            Assert.Equal(CycleSpeed * Delta * window.TickCount, travel, Metres);
        }
    }

    /// <summary>A full Idle → entry → cycle → exit → Idle walk at aligned seams is accepted with its entry ticks.</summary>
    [Fact]
    public void FullWalk_IsAcceptedAtAlignedSeams()
    {
        SequencePrediction prediction = Predictor().Predict(
            SequenceStart.Teleport(_origin),
            [
                new SequenceSegment("idle", 0d, 0d, 0.5d),
                new SequenceSegment("i2f_r", 0d, Crossfade, 0.6d),
                new SequenceSegment("wf", 0.5d, Crossfade, 0.5d, ExitCycles: 1),
                new SequenceSegment("f2i_r", 0d, Crossfade, 0.8d),
                new SequenceSegment("idle", 0d, Crossfade, 0.5d),
            ]);

        Assert.True(prediction.IsValid, prediction.Rejection?.Detail);
        Assert.Equal<int>([0, 31, 68, 129, 178], prediction.EntryTicks);
        Assert.Equal(178 + 30, prediction.Ticks[^1].Index);
        Assert.All(prediction.EntryTicks, entry => Assert.True(prediction.Ticks[entry].IsEntryTick));
    }

    /// <summary>Invalid sequences from the cycle, with the expected rejection.</summary>
    public static TheoryData<string, SequenceSegment[], SequenceRejectionReason, int> Rejections => new()
    {
        { "outside the planner set", [new("idle", 0d, Crossfade, 0.5d)], SequenceRejectionReason.NotInPlannerSet, 0 },
        { "phase disagreement", [new("f2i_r", 0.2d, Crossfade, 0.5d)], SequenceRejectionReason.NotAlignedSeam, 0 },
        { "zero crossfade after the start", [new("f2i_r", 0.2d, 0d, 0.5d)], SequenceRejectionReason.InvalidCrossfade, 0 },
        { "crossfade other than the seam table's", [new("f2i_r", 0.2d, 0.2d, 0.5d)], SequenceRejectionReason.InvalidCrossfade, 0 },
        { "exit between ticks", [new("f2i_r", 0.2d, Crossfade, 0.5d + (1d / 120d))], SequenceRejectionReason.OffTickGrid, 0 },
        { "exit before entry of a clip played once", [new("f2i_r", 0.5d, Crossfade, 0.2d)], SequenceRejectionReason.InvalidTiming, 0 },
        { "unknown clip", [new("missing", 0d, Crossfade, 0.5d)], SequenceRejectionReason.UnknownClip, 0 },
    };

    /// <summary>Joins that are not usable aligned seams, or have invalid crossfades or timing, are rejected.</summary>
    [Theory]
    [MemberData(nameof(Rejections))]
    public void InvalidJoins_AreRejected(string label, SequenceSegment[] segments, SequenceRejectionReason reason, int segment)
    {
        // The start leaves the cycle at 0.35 s, inside its left swing: only the phase case depends on it.
        double exit = label == "phase disagreement" ? 0.35d : 0.5d;

        SequencePrediction prediction = Predictor().Predict(SequenceStart.Playing(_origin, "wf", 0.3d, exit), segments);

        Assert.False(prediction.IsValid, label);
        Assert.Equal(reason, prediction.Rejection?.Reason);
        Assert.Equal(segment, prediction.Rejection?.Segment);
        Assert.Empty(prediction.Ticks);
    }

    /// <summary>
    /// Every seam the table certifies agrees in phase on every tick the runtime blends both clips. The runtime enters
    /// the incoming clip at <c>b</c> on the tick after the request, while the outgoing clip advances from <c>a</c> to
    /// <c>a + Δ</c> on that tick, so on the <c>k</c>-th crossfade tick the outgoing clip is at <c>a + kΔ</c> and the
    /// incoming one at <c>b + (k − 1)Δ</c>. Two identical cycles whose left foot lifts at 0.5 s make a window that
    /// crosses the lift-off expose any seam certified without that one-tick lead: the outgoing clip lifts one tick
    /// before the incoming one.
    /// </summary>
    [Fact]
    public void TableCertifiedSeams_AgreeInPhaseOnEveryRecordedCrossfadeTick()
    {
        ClipTrajectory first = Trajectory("first", StandingLocomotionCapability.CyclicForwardPrimary, 1d, time => new Vector2(0f, (float)time));
        ClipTrajectory second = Trajectory("second", StandingLocomotionCapability.CyclicArcLeftRightLane, 1d, time => new Vector2(0f, (float)time));
        var outgoing = SeamClip.FromTrajectory(first, SeamClipRole.Cycle, AlternatingHalves("first"));
        var incoming = SeamClip.FromTrajectory(second, SeamClipRole.Cycle, AlternatingHalves("second"));
        var table = SeamTable.Build([outgoing, incoming], [new SeamPairSpec(SeamKind.CycleToCycle, "first", "second")]);
        var predictor = new SequencePredictor(
            table,
            new Dictionary<string, ClipTrajectory> { ["first"] = first, ["second"] = second },
            ["first", "second"],
            Tolerance);
        Assert.True(table.TryGetPair("first", "second", out SeamPair? pair));
        Assert.NotEmpty(pair!.Candidates);

        List<string> failures = [];
        int blendedTicks = 0;
        foreach (SeamCandidate seam in pair.Candidates)
        {
            // Playing the outgoing cycle at its seam time: the join is requested now and entered on the next tick.
            SequencePrediction prediction = predictor.Predict(
                SequenceStart.Playing(_origin, "first", seam.OutgoingTime, seam.OutgoingTime),
                [new SequenceSegment("second", seam.IncomingTime, Crossfade, (seam.IncomingTime + 0.2d) % 1d)]);
            Assert.True(prediction.IsValid, prediction.Rejection?.Detail);
            foreach (PredictedTick tick in prediction.Ticks)
            {
                if (tick.FadingFrom is not { } fading)
                {
                    continue;
                }

                blendedTicks++;
                foreach (StandingLocomotionAnatomicalSide foot in Enum.GetValues<StandingLocomotionAnatomicalSide>())
                {
                    if (outgoing.IsPlanted(foot, fading.PositionSeconds) != incoming.IsPlanted(foot, tick.Current.PositionSeconds))
                    {
                        failures.Add(FormattableString.Invariant(
                            $"seam ({seam.OutgoingTime:0.####}, {seam.IncomingTime:0.####}) tick {tick.Index}: {foot} first@{fading.PositionSeconds:0.####} vs second@{tick.Current.PositionSeconds:0.####}"));
                    }
                }
            }
        }

        Assert.True(blendedTicks > 0, "SETUP: no crossfade tick blended both clips.");
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    /// <summary>
    /// The seam table certifies seams for one entry lag, so the predictor refuses another physics delta, and a
    /// planner-set clip must wrap in the table exactly when it loops in the runtime tree.
    /// </summary>
    [Fact]
    public void Binding_RequiresThePhysicsDeltaAndLoopModesTheSeamTableAssumes()
    {
        SequencePrediction prediction = Predictor().Predict(SequenceStart.Playing(_origin, "wf", 0d, 0.5d), [], 1d / 30d);
        Assert.Equal(SequenceRejectionReason.InvalidRequest, prediction.Rejection?.Reason);

        ClipTrajectory step = Trajectory("sl", StandingLocomotionCapability.ConfinedSideStepLeft, 1d, time => new Vector2(0.3f * (float)time, 0f));
        var looping = SeamClip.FromTrajectory(step, SeamClipRole.Confined, AllPlanted("sl", true, 1d));
        var idle = SeamClip.FromTrajectory(IdleTrajectory(), SeamClipRole.Idle, AllPlanted("idle", true, 2d));
        Assert.True(looping.WrapsPlayback);
        _ = Assert.Throws<ArgumentException>(() => new SequencePredictor(
            SeamTable.Build([looping, idle]),
            new Dictionary<string, ClipTrajectory> { ["sl"] = step, ["idle"] = IdleTrajectory() },
            ["idle"],
            Tolerance));

        // The other way round: a clip the tree loops but the table plays once.
        var once = SeamClip.FromTrajectory(step, SeamClipRole.Confined, AllPlanted("sl", false, 1d));
        Assert.False(once.WrapsPlayback);
        _ = Assert.Throws<ArgumentException>(() => new SequencePredictor(
            SeamTable.Build([once, idle]),
            new Dictionary<string, ClipTrajectory> { ["sl"] = step, ["idle"] = IdleTrajectory() },
            ["idle", "sl"],
            Tolerance));
        // Agreement both ways binds.
        _ = new SequencePredictor(
            SeamTable.Build([looping, idle]),
            new Dictionary<string, ClipTrajectory> { ["sl"] = step, ["idle"] = IdleTrajectory() },
            ["idle", "sl"],
            Tolerance);
    }

    /// <summary>
    /// A looping side-step joined from Idle in its double support plays across its loop point and returns to Idle at
    /// the next aligned seam, covering its whole step.
    /// </summary>
    [Fact]
    public void LoopingSideStep_JoinedFromIdleCoversItsWholeStepAcrossTheLoopPoint()
    {
        // Moves 0.3 m right per 1 s loop; left swings [0, 0.3), right swings [0.3, 0.6), both planted [0.6, 1).
        ClipTrajectory step = Trajectory("sl", StandingLocomotionCapability.ConfinedSideStepLeft, 1d, time => new Vector2(0.3f * (float)time, 0f));
        PlantedClipIntervals intervals = Intervals(
            "sl",
            true,
            1d,
            [Seg(0d, 0.3d, false), Seg(0.3d, 1d, true)],
            [Seg(0d, 0.3d, true), Seg(0.3d, 0.6d, false), Seg(0.6d, 1d, true)]);
        var table = SeamTable.Build([
            SeamClip.FromTrajectory(step, SeamClipRole.Confined, intervals),
            SeamClip.FromTrajectory(IdleTrajectory(), SeamClipRole.Idle, AllPlanted("idle", true, 2d)),
        ]);
        var predictor = new SequencePredictor(
            table,
            new Dictionary<string, ClipTrajectory> { ["sl"] = step, ["idle"] = IdleTrajectory() },
            ["idle", "sl"],
            Tolerance);

        // Idle → sl at 0.8 s (in double support), through the loop point, → Idle when sl reaches 0.6 s again.
        Assert.True(table.TryFindNextAligned("sl", "idle", 0.9d, Tolerance, out SeamChoice exit));
        Assert.Equal(0.6d, exit.Candidate.OutgoingTime, 9);
        SequencePrediction prediction = predictor.Predict(
            SequenceStart.Teleport(_origin),
            [
                new SequenceSegment("idle", 0d, 0d, 0.5d),
                new SequenceSegment("sl", 0.8d, Crossfade, exit.Candidate.OutgoingTime),
                new SequenceSegment("idle", exit.Candidate.IncomingTime, Crossfade, (exit.Candidate.IncomingTime + 0.5d) % 2d),
            ]);

        Assert.True(prediction.IsValid, prediction.Rejection?.Detail);
        // sl plays 0.8 s (0.8 → 1.0 → 0.6), 48 ticks; the first and last crossfades blend it with the static Idle.
        Assert.Equal<int>([0, 31, 80], prediction.EntryTicks);
        Assert.Contains(prediction.Ticks, tick => tick.Current.Role == "sl" && tick.Current.PositionSeconds < 0.3d);
        // Travel at 0.3 m/s, 0.005 m per tick: sl adds nothing on its entry tick, weighs k/9 on its ticks k = 2..9, then
        // 1 for 40 ticks until its exit, then 1 − k/9 on the exit crossfade's ticks k = 1..8.
        float ticks = 40f + (44f / 9f) + 4f;
        Assert.Equal(0.3f * ticks / 60f, prediction.EndPose.Position.X, Metres);
    }

    /// <summary>
    /// Summary mode predicts the same end pose, tick count and support windows as the per-tick prediction, keeping
    /// only their tally unless asked for the windows, and rejects the same sequences without formatting details.
    /// </summary>
    [Fact]
    public void Summary_MatchesTheFullPredictionAndRejectsWithoutDetail()
    {
        SequencePredictor predictor = Predictor();
        SequenceSegment[] walk =
        [
            new SequenceSegment("idle", 0d, 0d, 0.5d),
            new SequenceSegment("i2f_r", 0d, Crossfade, 0.6d),
            new SequenceSegment("wf", 0.5d, Crossfade, 0.5d, ExitCycles: 1),
            new SequenceSegment("f2i_r", 0d, Crossfade, 0.8d),
            new SequenceSegment("idle", 0d, Crossfade, 0.5d),
        ];

        SequencePrediction full = predictor.Predict(SequenceStart.Teleport(_origin), walk);
        SequenceSummary tally = predictor.PredictSummary(SequenceStart.Teleport(_origin), walk);
        SequenceSummary windows = predictor.PredictSummary(SequenceStart.Teleport(_origin), walk, collectWindows: true);
        SequenceSummary rejected = predictor.PredictSummary(SequenceStart.Playing(_origin, "wf", 0.3d, 0.35d), [new("f2i_r", 0.2d, Crossfade, 0.5d)]);

        Assert.True(full.IsValid, full.Rejection?.Detail);
        Assert.True(tally.IsValid);
        Assert.Equal(full.EndPose, tally.EndPose);
        Assert.Equal(full.Ticks.Length, tally.TickCount);
        Assert.Empty(tally.Windows);
        Assert.True(full.Windows.SequenceEqual(windows.Windows));
        Assert.Equal(full.Windows.Count(window => window.Phase.IsSingleSwing()), tally.SingleSwingWindows);
        Assert.Equal(full.Windows.Where(window => window.Phase.IsSingleSwing()).Sum(window => window.TickCount), tally.SingleSwingTicks);
        Assert.Equal(full.Windows.Count(window => window.Phase == SupportPhase.BothSwing), tally.BothSwingWindows);
        Assert.Equal(SequenceRejectionReason.NotAlignedSeam, rejected.Rejection);
        Assert.Equal(0, rejected.RejectedSegment);
        Assert.Equal(_origin, rejected.EndPose);
    }

    /// <summary>
    /// The planner's motion model: a crossfade's motion and a clip's solo playback compose to the per-tick prediction's
    /// end pose, and the crossfade blends for the ticks the tick model runs it.
    /// </summary>
    [Fact]
    public void MotionModel_ComposesToThePerTickPrediction()
    {
        SequencePredictor predictor = Predictor();
        SequencePrediction prediction = predictor.Predict(
            SequenceStart.Playing(_origin, "wf", 0.4d, 0.5d),
            [new SequenceSegment("f2i_r", 0.2d, Crossfade, 0.5d)]);
        Assert.True(prediction.IsValid, prediction.Rejection?.Detail);

        int ticks = SequencePredictor.CrossfadeTicks(Crossfade, Delta);
        PlanarMotion motion = predictor.SoloMotion("wf", 0.4d, 0.1d)
            .Then(predictor.CrossfadeMotion("wf", 0.5d, "f2i_r", 0.2d, Crossfade, Delta))
            .Then(predictor.SoloMotion("f2i_r", 0.2d + ((ticks - 1) * Delta), 0.3d - ((ticks - 1) * Delta)));
        SequencePose end = motion.From(_origin);

        Assert.Equal(9, ticks);
        Assert.True(end.Position.DistanceTo(prediction.EndPose.Position) < Metres, $"{end} vs {prediction.EndPose}");
        Assert.Equal(prediction.EndPose.Yaw, end.Yaw, 1e-5f);
    }

    /// <summary>A teleport start's first segment must enter with a zero crossfade.</summary>
    [Fact]
    public void TeleportStart_RequiresAZeroCrossfadeFirstSegment()
    {
        SequencePrediction prediction = Predictor().Predict(SequenceStart.Teleport(_origin), [new SequenceSegment("idle", 0d, Crossfade, 0.5d)]);

        Assert.Equal(SequenceRejectionReason.InvalidCrossfade, prediction.Rejection?.Reason);
    }

    private static SequencePredictor Predictor()
    {
        ClipTrajectory[] trajectories =
        [
            IdleTrajectory(),
            Trajectory("i2f_r", StandingLocomotionCapability.EntryForwardRight, 1d, time => new Vector2(0f, EntrySpeed * (float)time)),
            Trajectory("wf", StandingLocomotionCapability.CyclicForwardPrimary, 1d, time => new Vector2(0f, CycleSpeed * (float)time)),
            Trajectory("f2i_r", StandingLocomotionCapability.ExitForwardRight, 1d, time => new Vector2(0f, ExitSpeed * (float)time), leftBall: _exitLeftBall),
            Trajectory("pl45", StandingLocomotionCapability.ConfinedPivotLeft, 1d, time => new Vector2(0.1f * (float)time, 0.2f * (float)time), time => 0.8f * (float)time),
        ];
        PlantedClipIntervals cycle = Intervals(
            "wf",
            true,
            1d,
            [Seg(0d, 0.3d, true), Seg(0.3d, 0.5d, false), Seg(0.5d, 1d, true)],
            [Seg(0d, 0.8d, true), Seg(0.8d, 1d, false)]);
        SeamClip[] clips =
        [
            SeamClip.FromTrajectory(trajectories[0], SeamClipRole.Idle, AllPlanted("idle", true, 2d)),
            SeamClip.FromTrajectory(trajectories[1], SeamClipRole.Entry, AllPlanted("i2f_r", false, 1d)),
            SeamClip.FromTrajectory(trajectories[2], SeamClipRole.Cycle, cycle),
            SeamClip.FromTrajectory(trajectories[3], SeamClipRole.Exit, AllPlanted("f2i_r", false, 1d)),
            SeamClip.FromTrajectory(trajectories[4], SeamClipRole.Confined, AllPlanted("pl45", false, 1d)),
        ];
        return new SequencePredictor(
            SeamTable.Build(clips),
            trajectories.ToDictionary(trajectory => trajectory.Role),
            ["idle", "wf"],
            Tolerance);
    }

    private static ClipTrajectory IdleTrajectory() => Trajectory("idle", null, 2d, _ => Vector2.Zero);

    private static ClipTrajectory Trajectory(
        string role,
        StandingLocomotionCapability? capability,
        double duration,
        Func<double, Vector2> planar,
        Func<double, float>? yaw = null,
        Vector3? leftBall = null)
    {
        int count = ClipTrajectory.ExpectedSampleCount(duration, Rate);
        double[] times = [.. Enumerable.Range(0, count).Select(index => ClipTrajectory.SampleTimeOf(index, duration, Rate))];
        return new ClipTrajectory(
            role,
            role,
            capability,
            duration,
            Rate,
            [.. times.Select(planar)],
            [.. times.Select(time => yaw?.Invoke(time) ?? 0f)],
            [.. times.Select(_ => leftBall ?? _leftBall)],
            [.. times.Select(_ => _rightBall)],
            1f,
            new ClipTrajectoryErrorBounds(0f, 0f, 0f));
    }

    /// <summary>A 1 s cycle: left planted over the first half, right over the second.</summary>
    private static PlantedClipIntervals AlternatingHalves(string identity)
        => Intervals(identity, true, 1d, [Seg(0d, 0.5d, true), Seg(0.5d, 1d, false)], [Seg(0d, 0.5d, false), Seg(0.5d, 1d, true)]);

    private static PlantedClipIntervals AllPlanted(string identity, bool looping, double duration)
        => Intervals(identity, looping, duration, [Seg(0d, duration, true)], [Seg(0d, duration, true)]);

    private static PlantedClipIntervals Intervals(
        string identity,
        bool looping,
        double duration,
        ImmutableArray<PlantedIntervalSegment> left,
        ImmutableArray<PlantedIntervalSegment> right)
        => new(identity, identity, "synthetic", looping, duration, left, right);

    private static PlantedIntervalSegment Seg(double start, double end, bool planted) => new(start, end, planted);
}
