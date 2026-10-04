using System.Collections.Immutable;
using System.Text.Json;
using AlleyCat.Control.Locomotion;
using AlleyCat.Navigation.Arrival;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation.Arrival;

/// <summary>
/// Pure tests of the bind-time seam table and report on synthetic clips at 10 Hz. Expected phase agreement and
/// separations are worked by hand from the synthetic intervals and ball paths. Window-mechanics tests use a zero entry
/// lag (both clips start the window together) so their hand-worked windows stay on the 10 Hz grid; the runtime entry
/// lag, per-pair crossfades and looping manoeuvres have tests of their own.
/// </summary>
public sealed class SeamTableTests
{
    private const double Rate = 10d;
    private const float Tolerance = 1e-5f;

    private static readonly Vector3 _origin = Vector3.Zero;

    [Fact]
    public void Build_IdenticalCyclesAgreeOnlyAtTheSamePhaseWithZeroSeparation()
    {
        // Left planted [0, 0.5), swing [0.5, 1); right the opposite. A 0.15 s window agrees only where both clips
        // stay on the same side of their boundaries.
        SeamClip first = Cycle("first", 1d, Alternating(1d), t => new Vector3((float)t, 0f, 0f));
        SeamClip second = Cycle("second", 1d, Alternating(1d), t => new Vector3((float)t, 0f, 0f));

        var table = SeamTable.Build([first, second], [new(SeamKind.CycleToCycle, "first", "second")], 0.15d, entryLagSeconds: 0d);
        SeamPair pair = Pair(table, "first", "second");

        Assert.Equal(100, pair.EvaluatedCount);
        SeamCandidate same = Assert.Single(pair.Candidates, value => value.OutgoingTime == 0.2d && value.IncomingTime == 0.2d);
        Assert.Equal(0f, same.MaxSeparation, Tolerance);
        Assert.True(same.HasSupportThroughout);
        // Opposite phases never agree.
        Assert.DoesNotContain(pair.Candidates, value => value.OutgoingTime == 0.2d && value.IncomingTime == 0.7d);
        // The window [0.4, 0.55) crosses the 0.5 s boundary; the other clip at 0.3 does not.
        Assert.DoesNotContain(pair.Candidates, value => value.OutgoingTime == 0.4d && value.IncomingTime == 0.3d);
        // Both cross their boundary at the same offset: agreement.
        Assert.Contains(pair.Candidates, value => value.OutgoingTime == 0.4d && value.IncomingTime == 0.4d);
    }

    [Fact]
    public void Constructor_RequiresBallSamplesForBothFeet()
    {
        SeamClip clip = Cycle("first", 1d, Alternating(1d), _ => _origin, _ => _origin);
        ImmutableArray<Vector3> left = [.. Enumerable.Range(0, clip.SampleCount)
            .Select(index => clip.Ball(StandingLocomotionAnatomicalSide.Left, clip.GridTime(index), index == clip.SampleCount - 1))];

        _ = Assert.Throws<ArgumentException>(
            () => new SeamClip(clip.Role, clip.Identity, clip.Kind, clip.DurationSeconds, clip.KeyRateHz, left, [], clip.BallErrorBound, clip.Intervals));
        _ = Assert.Throws<ArgumentException>(
            () => new SeamClip(clip.Role, clip.Identity, clip.Kind, clip.DurationSeconds, clip.KeyRateHz, default, left, clip.BallErrorBound, clip.Intervals));
    }

    [Fact]
    public void Build_WindowEndingExactlyAtABoundaryAgrees()
    {
        // With d = 0.2, the window [0.3, 0.5) of the first clip ends where its left foot lifts; the other clip stays
        // planted. The outgoing clip's weight is zero at τ = d, so the end instant is not compared.
        SeamClip lifting = Cycle("lifting", 1d, Alternating(1d), _ => _origin);
        SeamClip planted = Cycle("planted", 1d, Intervals("planted", true, 1d, [Seg(0d, 1d, true)], [Seg(0d, 0.5d, false), Seg(0.5d, 1d, true)]), _ => _origin);

        var table = SeamTable.Build([lifting, planted], [new(SeamKind.CycleToCycle, "lifting", "planted")], 0.2d, entryLagSeconds: 0d);
        SeamPair pair = Pair(table, "lifting", "planted");

        Assert.Contains(pair.Candidates, value => Near(value.OutgoingTime, 0.3d) && Near(value.IncomingTime, 0.2d));
        Assert.DoesNotContain(pair.Candidates, value => Near(value.OutgoingTime, 0.4d) && Near(value.IncomingTime, 0.2d));
    }

    [Fact]
    public void Build_FiniteClipsMustContainTheWholeWindowAndCyclesWrap()
    {
        SeamClip exit = Clip("exit", SeamClipRole.Exit, 1d, AllPlanted("exit", false, 1d), _ => _origin);
        SeamClip cycle = Cycle("cycle", 1d, AllPlanted("cycle", true, 1d), _ => _origin);
        SeamClip idle = Clip("idle", SeamClipRole.Idle, 1d, AllPlanted("idle", true, 1d), _ => _origin);

        var table = SeamTable.Build(
            [exit, cycle, idle],
            [new(SeamKind.ManoeuvreToIdle, "exit", "idle"), new(SeamKind.CycleToExit, "cycle", "exit")],
            0.15d,
            entryLagSeconds: 0d);

        // The exit plays once: a in {0, 0.1, ..., 0.8} (0.9 + 0.15 > 1). Idle wraps: b in {0, ..., 0.9}.
        SeamPair toIdle = Pair(table, "exit", "idle");
        Assert.Equal(9 * 10, toIdle.EvaluatedCount);
        Assert.DoesNotContain(toIdle.Candidates, value => value.OutgoingTime > 0.85d);
        Assert.Contains(toIdle.Candidates, value => Near(value.OutgoingTime, 0.8d) && Near(value.IncomingTime, 0.9d));

        // The cycle wraps: a = 0.9 is evaluated; the exit entered at b = 0.9 cannot hold the window.
        SeamPair toExit = Pair(table, "cycle", "exit");
        Assert.Equal(10 * 9, toExit.EvaluatedCount);
        Assert.Contains(toExit.Candidates, value => Near(value.OutgoingTime, 0.9d));
        Assert.DoesNotContain(toExit.Candidates, value => value.IncomingTime > 0.85d);
    }

    [Fact]
    public void Build_WrappingWindowComparesStatesAcrossTheLoopPoint()
    {
        // The cycle's right foot swings over [0.9, 1) and is planted from the loop point; a clip that keeps the right
        // foot swinging over its whole window disagrees after the wrap.
        SeamClip cycle = Cycle("cycle", 1d, Intervals("cycle", true, 1d, [Seg(0d, 1d, true)], [Seg(0d, 0.9d, true), Seg(0.9d, 1d, false)]), _ => _origin);
        SeamClip swinging = Clip("swinging", SeamClipRole.Exit, 1d, Intervals("swinging", false, 1d, [Seg(0d, 1d, true)], [Seg(0d, 1d, false)]), _ => _origin);
        SeamClip steady = Clip("steady", SeamClipRole.Exit, 1d, Intervals("steady", false, 1d, [Seg(0d, 1d, true)], [Seg(0d, 0.1d, false), Seg(0.1d, 1d, true)]), _ => _origin);

        var table = SeamTable.Build(
            [cycle, swinging, steady],
            [new(SeamKind.CycleToExit, "cycle", "swinging"), new(SeamKind.CycleToExit, "cycle", "steady")],
            0.15d,
            entryLagSeconds: 0d);

        Assert.DoesNotContain(Pair(table, "cycle", "swinging").Candidates, value => Near(value.OutgoingTime, 0.9d));
        // Right swing [0.9, 1.0) then planted from the wrap, matching steady's swing [0, 0.1) then planted.
        Assert.Contains(Pair(table, "cycle", "steady").Candidates, value => Near(value.OutgoingTime, 0.9d) && Near(value.IncomingTime, 0d));
    }

    [Fact]
    public void Build_SeparationIsTheLargestPerFootValueOverTheWholeWindow()
    {
        // Outgoing left ball peaks at the 0.3 s key (x = 0.3) between keys of 0.1 at 0.2 and 0.4; the incoming ball is
        // fixed at x = 0. With a = 0.2 and d = 0.15 the window [0.2, 0.35] includes the key peak; the window end
        // (0.35) interpolates to 0.2. The right balls differ by a constant 0.05 in y.
        SeamClip peaked = Cycle(
            "peaked",
            1d,
            AllPlanted("peaked", true, 1d),
            t => new Vector3(Math.Abs(t - 0.3d) < 1e-9 ? 0.3f : 0.1f, 0f, 0f),
            _ => new Vector3(0f, 0.05f, 0f));
        SeamClip flat = Cycle("flat", 1d, AllPlanted("flat", true, 1d), _ => _origin);

        var table = SeamTable.Build([peaked, flat], [new(SeamKind.CycleToCycle, "peaked", "flat")], 0.15d, entryLagSeconds: 0d);
        SeamCandidate seam = Assert.Single(
            Pair(table, "peaked", "flat").Candidates,
            value => Near(value.OutgoingTime, 0.2d) && Near(value.IncomingTime, 0d));
        Assert.Equal(0.3f, seam.LeftSeparation, Tolerance);
        Assert.Equal(0.05f, seam.RightSeparation, Tolerance);
        Assert.Equal(0.3f, seam.MaxSeparation, Tolerance);

        // a = 0.4: window [0.4, 0.55] never reaches the peak.
        SeamCandidate later = Assert.Single(
            Pair(table, "peaked", "flat").Candidates,
            value => Near(value.OutgoingTime, 0.4d) && Near(value.IncomingTime, 0d));
        Assert.Equal(0.1f, later.LeftSeparation, Tolerance);
    }

    [Fact]
    public void Build_WindowEndBetweenKeysIsInterpolated()
    {
        // Outgoing ball x = t; incoming fixed at 0: the largest separation over [0.5, 0.65] is at the window end.
        SeamClip moving = Cycle("moving", 1d, AllPlanted("moving", true, 1d), t => new Vector3((float)t, 0f, 0f));
        SeamClip flat = Cycle("flat", 1d, AllPlanted("flat", true, 1d), _ => _origin);

        var table = SeamTable.Build([moving, flat], [new(SeamKind.CycleToCycle, "moving", "flat")], 0.15d, entryLagSeconds: 0d);
        SeamCandidate seam = Assert.Single(
            Pair(table, "moving", "flat").Candidates,
            value => Near(value.OutgoingTime, 0.5d) && Near(value.IncomingTime, 0d));
        Assert.Equal(0.65f, seam.LeftSeparation, Tolerance);
    }

    [Fact]
    public void Build_RecordsWhetherAFootStaysPlantedThroughout()
    {
        // Both feet swing over [0.4, 0.6) in both clips: phase agrees, but the window has no support there.
        PlantedClipIntervals flight = Intervals(
            "flight",
            true,
            1d,
            [Seg(0d, 0.4d, true), Seg(0.4d, 1d, false)],
            [Seg(0d, 0.6d, false), Seg(0.6d, 1d, true)]);
        SeamClip first = Cycle("first", 1d, flight, _ => _origin);
        SeamClip second = Cycle("second", 1d, Intervals("second", true, 1d, flight.Left, flight.Right), _ => _origin);

        var table = SeamTable.Build([first, second], [new(SeamKind.CycleToCycle, "first", "second")], 0.15d, entryLagSeconds: 0d);
        SeamPair pair = Pair(table, "first", "second");

        Assert.False(Assert.Single(pair.Candidates, value => Near(value.OutgoingTime, 0.4d) && Near(value.IncomingTime, 0.4d)).HasSupportThroughout);
        Assert.True(Assert.Single(pair.Candidates, value => Near(value.OutgoingTime, 0.1d) && Near(value.IncomingTime, 0.1d)).HasSupportThroughout);
    }

    [Theory]
    [InlineData(SeamKind.SliceJoin, 0.6d)]
    [InlineData(SeamKind.CycleToExit, 0.4d)]
    public void Queries_SkipSliceJoinSeamsWithoutSupportThroughout(SeamKind kind, double expectedOutgoing)
    {
        // Both feet swing over [0.4, 0.6) in both clips. The windows at a = 0.4 and 0.5 agree in phase but have no
        // planted foot throughout, so only a slice join skips them; the first supported window is at a = 0.6.
        PlantedClipIntervals flight = Intervals(
            "entry",
            false,
            1d,
            [Seg(0d, 0.4d, true), Seg(0.4d, 1d, false)],
            [Seg(0d, 0.6d, false), Seg(0.6d, 1d, true)]);
        SeamClip entry = Clip("entry", SeamClipRole.Entry, 1d, flight, _ => _origin);
        SeamClip exit = Clip("exit", SeamClipRole.Exit, 1d, Intervals("exit", false, 1d, flight.Left, flight.Right), _ => _origin);
        var table = SeamTable.Build([entry, exit], [new(kind, "entry", "exit")], 0.15d, entryLagSeconds: 0d);

        Assert.True(table.TryFindNextAligned("entry", "exit", 0.35d, 1f, out SeamChoice next));
        Assert.Equal(expectedOutgoing, next.Candidate.OutgoingTime, 9);
        Assert.True(table.TryFindNextAligned("entry", ["exit"], 0.35d, 1f, out SeamChoice across));
        Assert.Equal(expectedOutgoing, across.Candidate.OutgoingTime, 9);
        Assert.Equal(
            kind != SeamKind.SliceJoin,
            table.TryFindLowestSeparationAligned("entry", "exit", 0.35d, 1f, 0.1d, out _));
        Assert.True(table.TryFindLowestSeparationAligned("entry", ["exit"], 0.35d, 1f, double.PositiveInfinity, out SeamChoice lowest));
        Assert.True(kind != SeamKind.SliceJoin || lowest.Candidate.HasSupportThroughout);
        Assert.Equal(kind != SeamKind.SliceJoin, table.AlignedSeams("entry", "exit", 1f).Any(value => !value.HasSupportThroughout));
    }

    [Fact]
    public void TryEvaluateSeam_RechecksShiftedSeamsOfPlannerSetPairs()
    {
        SeamClip first = Cycle("first", 1d, Alternating(1d), t => new Vector3((float)t, 0f, 0f));
        SeamClip second = Cycle("second", 1d, Alternating(1d), t => new Vector3((float)t, 0f, 0f));
        var table = SeamTable.Build([first, second], [new(SeamKind.CycleToCycle, "first", "second")], 0.15d, entryLagSeconds: 0d);

        // Off the native grid, the same-phase shift still agrees, with the separation of the 0.01 s phase offset.
        Assert.True(table.TryEvaluateSeam("first", "second", 0.21d, 0.2d, out SeamCandidate shifted));
        Assert.Equal(0.21d, shifted.OutgoingTime, 9);
        Assert.Equal(0.01f, shifted.MaxSeparation, Tolerance);
        // A shift across a planted boundary fails phase agreement.
        Assert.False(table.TryEvaluateSeam("first", "second", 0.4d, 0.2d, out _));
        // Only planner-set pairs are re-checked.
        Assert.False(table.TryEvaluateSeam("second", "first", 0.2d, 0.2d, out _));
        Assert.False(table.IsInPlannerSet("second", "first"));
        Assert.True(table.IsInPlannerSet("first", "second"));
    }

    [Fact]
    public void TryFindNextAligned_WrapsCyclesAndStopsFiniteClips()
    {
        // Only right-planted windows agree with the exit, which keeps the left foot swinging: the cycle's left swing
        // is [0.5, 1), so aligned seams start at a in {0.5, 0.6, 0.7, 0.8}.
        SeamClip cycle = Cycle("cycle", 1d, Alternating(1d), _ => _origin);
        SeamClip exit = Clip("exit", SeamClipRole.Exit, 1d, Intervals("exit", false, 1d, [Seg(0d, 1d, false)], [Seg(0d, 1d, true)]), _ => _origin);
        var table = SeamTable.Build([cycle, exit], [new(SeamKind.CycleToExit, "cycle", "exit")], 0.15d, entryLagSeconds: 0d);

        Assert.True(table.TryFindNextAligned("cycle", "exit", 0.55d, 0.01f, out SeamChoice next));
        Assert.Equal(0.6d, next.Candidate.OutgoingTime, 9);
        Assert.Equal(0.05d, next.DelaySeconds, 9);

        // After the last aligned seam of this cycle, the next one is in the following cycle.
        Assert.True(table.TryFindNextAligned("cycle", "exit", 0.85d, 0.01f, out SeamChoice wrapped));
        Assert.Equal(0.5d, wrapped.Candidate.OutgoingTime, 9);
        Assert.Equal(0.65d, wrapped.DelaySeconds, 9);

        // Playback positions beyond the cycle wrap before searching.
        Assert.True(table.TryFindNextAligned("cycle", "exit", 3.55d, 0.01f, out SeamChoice lap));
        Assert.Equal(0.6d, lap.Candidate.OutgoingTime, 9);

        Assert.False(table.TryFindNextAligned("cycle", "unknown", 0d, 1f, out _));
    }

    [Fact]
    public void TryFindNextAligned_FiniteOutgoingClipHasNoSeamAfterItsLast()
    {
        SeamClip entry = Clip("entry", SeamClipRole.Entry, 1d, AllPlanted("entry", false, 1d), _ => _origin);
        SeamClip cycle = Cycle("cycle", 1d, AllPlanted("cycle", true, 1d), _ => _origin);
        var table = SeamTable.Build([entry, cycle], [new(SeamKind.EntryToCycle, "entry", "cycle")], 0.15d, entryLagSeconds: 0d);

        Assert.True(table.TryFindNextAligned("entry", "cycle", 0.75d, 0.01f, out SeamChoice last));
        Assert.Equal(0.8d, last.Candidate.OutgoingTime, 9);
        Assert.False(table.TryFindNextAligned("entry", "cycle", 0.85d, 0.01f, out _));
    }

    [Fact]
    public void TryFindNextAligned_AcrossExitLanesChoosesTheEarliestThenTheTightest()
    {
        // Lane "l" agrees only while the cycle's left foot is planted ([0, 0.5)), lane "r" only while it swings.
        SeamClip cycle = Cycle("cycle", 1d, Alternating(1d), _ => _origin);
        SeamClip left = Clip("l", SeamClipRole.Exit, 1d, Intervals("l", false, 1d, [Seg(0d, 1d, true)], [Seg(0d, 1d, false)]), _ => new Vector3(0.02f, 0f, 0f));
        SeamClip right = Clip("r", SeamClipRole.Exit, 1d, Intervals("r", false, 1d, [Seg(0d, 1d, false)], [Seg(0d, 1d, true)]), _ => _origin);
        var table = SeamTable.Build(
            [cycle, left, right],
            [new(SeamKind.CycleToExit, "cycle", "l"), new(SeamKind.CycleToExit, "cycle", "r")],
            0.15d,
            entryLagSeconds: 0d);

        // Stride phase selects the lane: from 0.45 s only lane "r" has an aligned seam this cycle.
        Assert.True(table.TryFindNextAligned("cycle", ["l", "r"], 0.45d, 0.05f, out SeamChoice phaseSelected));
        Assert.Equal("r", phaseSelected.Pair.Incoming.Role);
        Assert.Equal(0.5d, phaseSelected.Candidate.OutgoingTime, 9);
        Assert.Equal(0.05d, phaseSelected.DelaySeconds, 9);

        // The earliest seam wins even when a later lane is tighter: lane "l" is reachable at once at 0.1 s and 20 mm
        // loose, while lane "r" is tight but only at 0.5 s.
        Assert.True(table.TryFindNextAligned("cycle", ["l", "r"], 0.1d, 0.05f, out SeamChoice earliest));
        Assert.Equal("l", earliest.Pair.Incoming.Role);
        Assert.Equal(0.1d, earliest.Candidate.OutgoingTime, 9);
        Assert.Equal(0d, earliest.DelaySeconds, 9);

        // A tolerance below lane "l"'s 20 mm separation leaves only lane "r".
        Assert.True(table.TryFindNextAligned("cycle", ["l", "r"], 0.1d, 0.01f, out SeamChoice tight));
        Assert.Equal("r", tight.Pair.Incoming.Role);
        Assert.Equal(0.4d, tight.DelaySeconds, 9);
    }

    [Fact]
    public void AlignedSeams_OrdersBySeparationAndAppliesTheTolerance()
    {
        SeamClip moving = Cycle("moving", 1d, AllPlanted("moving", true, 1d), t => new Vector3((float)t, 0f, 0f));
        SeamClip flat = Clip("flat", SeamClipRole.Exit, 1d, AllPlanted("flat", false, 1d), _ => _origin);
        var table = SeamTable.Build([moving, flat], [new(SeamKind.CycleToExit, "moving", "flat")], 0.15d, entryLagSeconds: 0d);

        SeamCandidate[] aligned = [.. table.AlignedSeams("moving", "flat", 0.2f)];

        // Separation is the window-end x = a + 0.15, and 1.0 for a = 0.9, whose window reaches the loop point from
        // below before wrapping to x = 0; only a = 0 is within 0.2.
        Assert.NotEmpty(aligned);
        Assert.All(aligned, value => Assert.True(value.MaxSeparation <= 0.2f));
        Assert.All(aligned, value => Assert.True(Near(value.OutgoingTime, 0d)));
        Assert.Equal(0.15f, aligned[0].MaxSeparation, Tolerance);
        SeamPair pair = Pair(table, "moving", "flat");
        Assert.Equal(1f, Assert.Single(pair.Candidates, value => Near(value.OutgoingTime, 0.9d) && Near(value.IncomingTime, 0d)).MaxSeparation, Tolerance);
        Assert.Empty(table.AlignedSeams("flat", "moving", 1f));
    }

    [Fact]
    public void Report_ListsAlignedExitsUnavailableCyclesAndImpossibleKinds()
    {
        // "good" exits within 10 mm; "bad" only at 100 mm. The slice pair's phases never agree over the window.
        SeamClip good = Cycle("good", 1d, AllPlanted("good", true, 1d), _ => _origin);
        SeamClip bad = Cycle("bad", 1d, AllPlanted("bad", true, 1d), _ => new Vector3(0.1f, 0f, 0f));
        SeamClip exit = Clip("exit", SeamClipRole.Exit, 1d, AllPlanted("exit", false, 1d), _ => _origin);
        SeamClip entry = Clip("entry", SeamClipRole.Entry, 1d, Intervals("entry", false, 1d, [Seg(0d, 1d, false)], [Seg(0d, 1d, false)]), _ => _origin);
        var table = SeamTable.Build(
            [good, bad, exit, entry],
            [
                new(SeamKind.CycleToExit, "good", "exit"),
                new(SeamKind.CycleToExit, "bad", "exit"),
                new(SeamKind.SliceJoin, "entry", "exit"),
            ],
            0.15d,
            entryLagSeconds: 0d);

        var tolerance = new SeamTolerance(0.01f, SeamToleranceAgreementStatus.Pending, "pending");
        var report = SeamReport.Create("synthetic", table, tolerance, [0.2f, 0.01f]);

        Assert.Equal(["bad"], report.UnavailableCycles);
        SeamCycleExits goodExits = Assert.Single(report.Cycles, cycle => cycle.CycleRole == "good");
        Assert.Equal(10, goodExits.Exits.Count);
        Assert.Equal(0.1d, goodExits.LongestWaitSeconds!.Value, 9);
        Assert.Equal("entry", Assert.Single(report.ImpossiblePairs).OutgoingRole);
        Assert.Equal([SeamKind.SliceJoin], report.ImpossibleKinds);

        SeamPairStatistics badStats = Assert.Single(report.Pairs, pair => pair.OutgoingRole == "bad");
        Assert.Equal(0.1f, badStats.Minimum!.Value, Tolerance);
        Assert.Equal(0.1f, badStats.Median!.Value, Tolerance);

        // The sweep is ordered by tolerance; at 200 mm both cycles are available.
        Assert.Equal([0.01f, 0.2f], report.Sweep.Select(row => row.ToleranceMetres));
        Assert.Equal((1, 2), report.Sweep[0].AlignedPairsByKind[SeamKind.CycleToExit]);
        Assert.Equal((2, 2), report.Sweep[1].AlignedPairsByKind[SeamKind.CycleToExit]);
        Assert.All(report.Sweep[1].Cycles, cycle => Assert.True(cycle.IsAvailable));

        using var json = JsonDocument.Parse(report.ToJson());
        JsonElement root = json.RootElement;
        Assert.Equal("Pending", root.GetProperty("seam_tolerance").GetProperty("agreement_status").GetString());
        Assert.Equal(0.01f, root.GetProperty("seam_tolerance").GetProperty("tolerance_m").GetSingle(), Tolerance);
        Assert.Equal("bad", root.GetProperty("unavailable_cyclic_clips_and_looping_manoeuvres")[0].GetString());
        Assert.Equal("SliceJoin", root.GetProperty("impossible_kinds")[0].GetString());
        Assert.Equal(3, root.GetProperty("measured_pairs").GetArrayLength());
        Assert.Equal(2, root.GetProperty("tolerance_sweep").GetArrayLength());
        Assert.Contains("Unavailable cyclic clips and looping manoeuvres: bad", report.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void TryFindLowestSeparationAligned_TradesDelayAgainstSeparation()
    {
        // Outgoing ball x = 1 - t, incoming fixed at 0: the separation of a seam at a <= 0.8 is 1 - a (its window
        // start), and a = 0.9 reaches the loop point, where x returns to 1.
        SeamClip falling = Cycle("falling", 1d, AllPlanted("falling", true, 1d), t => new Vector3((float)(1d - t), 0f, 0f));
        SeamClip flat = Clip("flat", SeamClipRole.Exit, 1d, AllPlanted("flat", false, 1d), _ => _origin);
        var table = SeamTable.Build([falling, flat], [new(SeamKind.CycleToExit, "falling", "flat")], 0.15d, entryLagSeconds: 0d);

        Assert.True(table.TryFindNextAligned("falling", "flat", 0.35d, 1f, out SeamChoice earliest));
        Assert.Equal(0.4d, earliest.Candidate.OutgoingTime, 9);
        Assert.Equal(0.6f, earliest.Separation, Tolerance);

        Assert.True(table.TryFindLowestSeparationAligned("falling", "flat", 0.35d, 1f, 0.3d, out SeamChoice bounded));
        Assert.Equal(0.6d, bounded.Candidate.OutgoingTime, 9);
        Assert.Equal(0.25d, bounded.DelaySeconds, 9);
        Assert.Equal(0.4f, bounded.Separation, Tolerance);

        Assert.True(table.TryFindLowestSeparationAligned("falling", ["flat", "missing"], 0.35d, 1f, double.PositiveInfinity, out SeamChoice best));
        Assert.Equal(0.8d, best.Candidate.OutgoingTime, 9);
        Assert.Equal(0.2f, best.Separation, Tolerance);

        // The tolerance still excludes seams: nothing within 0.1 m.
        Assert.False(table.TryFindLowestSeparationAligned("falling", "flat", 0d, 0.1f, double.PositiveInfinity, out _));
    }

    [Fact]
    public void Validate_RejectsNonPositiveTolerancesAndAgreementsWithoutRecord()
    {
        _ = Assert.Throws<ArgumentException>(() => new SeamTolerance(0f, SeamToleranceAgreementStatus.Pending, "").Validate());
        _ = Assert.Throws<ArgumentException>(() => new SeamTolerance(float.NaN, SeamToleranceAgreementStatus.Pending, "").Validate());
        _ = Assert.Throws<ArgumentException>(() => new SeamTolerance(0.1f, SeamToleranceAgreementStatus.Agreed, " ").Validate());
        var agreed = new SeamTolerance(0.1f, SeamToleranceAgreementStatus.Agreed, "agreed");
        Assert.Same(agreed, agreed.Validate());
    }

    [Fact]
    public void Report_WarnsAboutArtefactsReachRisksAndUnalignedPairs()
    {
        // "near" has a best seam of 50 mm, "far" of 100 mm; the tolerance in force (90 mm) admits only "near".
        SeamClip near = Cycle("near", 1d, AllPlanted("near", true, 1d), _ => new Vector3(0.05f, 0f, 0f));
        SeamClip far = Cycle("far", 1d, AllPlanted("far", true, 1d), _ => new Vector3(0.1f, 0f, 0f));
        SeamClip exit = Clip("exit", SeamClipRole.Exit, 1d, AllPlanted("exit", false, 1d), _ => _origin);
        var table = SeamTable.Build(
            [near, far, exit],
            [new(SeamKind.CycleToExit, "near", "exit"), new(SeamKind.CycleToExit, "far", "exit")],
            0.15d,
            entryLagSeconds: 0d);

        var report = SeamReport.Create(
            "synthetic",
            table,
            new SeamTolerance(0.09f, SeamToleranceAgreementStatus.Agreed, "agreed for the test"),
            reach:
            [
                new("near", "exit", StandingLocomotionAnatomicalSide.Left, 0.990f, 0.980f),
                new("far", "exit", StandingLocomotionAnatomicalSide.Left, 0.996f, 0.985f),
                new("far", "exit", StandingLocomotionAnatomicalSide.Right, 1.010f, 0.990f),
            ]);

        Assert.Equal(["far->exit"], report.UnalignedPairs.Select(pair => $"{pair.OutgoingRole}->{pair.IncomingRole}"));
        Assert.Equal(["far"], report.ArtefactWarnings.Select(pair => pair.OutgoingRole));
        SeamReachMeasurement risk = Assert.Single(report.ReachRisks);
        Assert.Equal(StandingLocomotionAnatomicalSide.Right, risk.Foot);
        Assert.Equal(1.010f, risk.PeakUsage, Tolerance);

        using var json = JsonDocument.Parse(report.ToJson());
        JsonElement root = json.RootElement;
        Assert.Equal("Agreed", root.GetProperty("seam_tolerance").GetProperty("agreement_status").GetString());
        Assert.Equal(SeamTolerance.StrictRecommendationMetres, root.GetProperty("seam_tolerance").GetProperty("strict_recommendation_m").GetSingle(), Tolerance);
        Assert.Equal("far->exit", root.GetProperty("artefact_warnings")[0].GetString());
        Assert.Equal("far->exit", root.GetProperty("unaligned_pairs")[0].GetString());
        Assert.Equal(1, root.GetProperty("reach_risks").GetArrayLength());
        Assert.Contains("reach risk", report.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Relevant_DerivesThePairsOfEachSeamKindFromPlannerRoles()
    {
        SeamClip[] clips =
        [
            Package("wf", SeamClipRole.Cycle, StandingLocomotionCapability.CyclicForwardPrimary),
            Package("wb", SeamClipRole.Cycle, StandingLocomotionCapability.CyclicBackwardPrimary),
            Package("al_l", SeamClipRole.Cycle, StandingLocomotionCapability.CyclicArcLeftLeftLane),
            Package("al_r", SeamClipRole.Cycle, StandingLocomotionCapability.CyclicArcLeftRightLane),
            Package("ar_r", SeamClipRole.Cycle, StandingLocomotionCapability.CyclicArcRightRightLane),
            Package("xl_l", SeamClipRole.Cycle, StandingLocomotionCapability.CyclicLateralLeftLeftLane),
            Package("i2f_l", SeamClipRole.Entry, StandingLocomotionCapability.EntryForwardLeft),
            Package("i2f_r", SeamClipRole.Entry, StandingLocomotionCapability.EntryForwardRight),
            Package("i2b_r", SeamClipRole.Entry, StandingLocomotionCapability.EntryBackwardRight),
            Package("f2i_r", SeamClipRole.Exit, StandingLocomotionCapability.ExitForwardRight),
            Package("b2i_l", SeamClipRole.Exit, StandingLocomotionCapability.ExitBackwardLeft),
            Package("sl", SeamClipRole.Confined, StandingLocomotionCapability.ConfinedSideStepLeft),
            Clip("idle", SeamClipRole.Idle, 1d, AllPlanted("idle", true, 1d), _ => _origin),
        ];

        HashSet<SeamPairSpec> pairs = [.. SeamPairPolicy.Relevant(clips)];

        // The synthetic entries never lift a foot, so their slice heads start at the clip end.
        SeamPairSpec[] expected =
        [
            new(SeamKind.EntryToCycle, "i2f_l", "wf"),
            new(SeamKind.EntryToCycle, "i2f_l", "al_l"),
            new(SeamKind.EntryToCycle, "i2f_l", "al_r"),
            new(SeamKind.EntryToCycle, "i2f_r", "wf"),
            new(SeamKind.EntryToCycle, "i2f_r", "al_r"),
            new(SeamKind.EntryToCycle, "i2f_r", "ar_r"),
            new(SeamKind.EntryToCycle, "i2b_r", "wb"),
            new(SeamKind.CycleToCycle, "wf", "al_l"),
            new(SeamKind.CycleToCycle, "wf", "al_r"),
            new(SeamKind.CycleToCycle, "wf", "ar_r"),
            new(SeamKind.CycleToCycle, "al_l", "wf"),
            new(SeamKind.CycleToCycle, "al_l", "al_r"),
            new(SeamKind.CycleToCycle, "al_r", "wf"),
            new(SeamKind.CycleToCycle, "al_r", "al_l"),
            new(SeamKind.CycleToCycle, "al_r", "ar_r"),
            new(SeamKind.CycleToCycle, "ar_r", "wf"),
            new(SeamKind.CycleToCycle, "ar_r", "al_r"),
            new(SeamKind.CycleToExit, "wf", "f2i_r"),
            new(SeamKind.CycleToExit, "al_l", "f2i_r"),
            new(SeamKind.CycleToExit, "al_r", "f2i_r"),
            new(SeamKind.CycleToExit, "ar_r", "f2i_r"),
            new(SeamKind.CycleToExit, "wb", "b2i_l"),
            new(SeamKind.SliceJoin, "i2f_l", "f2i_r") { MinimumOutgoingSeconds = 1d },
            new(SeamKind.SliceJoin, "i2f_r", "f2i_r") { MinimumOutgoingSeconds = 1d },
            new(SeamKind.SliceJoin, "i2b_r", "b2i_l") { MinimumOutgoingSeconds = 1d },
            new(SeamKind.IdleToManoeuvre, "idle", "i2f_l"),
            new(SeamKind.IdleToManoeuvre, "idle", "i2f_r"),
            new(SeamKind.IdleToManoeuvre, "idle", "i2b_r"),
            new(SeamKind.IdleToManoeuvre, "idle", "sl"),
            new(SeamKind.ManoeuvreToIdle, "f2i_r", "idle"),
            new(SeamKind.ManoeuvreToIdle, "b2i_l", "idle"),
            new(SeamKind.ManoeuvreToIdle, "sl", "idle"),
        ];
        Assert.Equal(expected.Length, pairs.Count);
        Assert.All(expected, pair => Assert.Contains(pair, pairs));
        // Wrong-lane entries into the turning-side-lane arcs, switches between those arcs, and every lateral pair are
        // not planned.
        Assert.DoesNotContain(new SeamPairSpec(SeamKind.EntryToCycle, "i2f_r", "al_l"), pairs);
        Assert.DoesNotContain(new SeamPairSpec(SeamKind.EntryToCycle, "i2f_l", "ar_r"), pairs);
        Assert.DoesNotContain(new SeamPairSpec(SeamKind.CycleToCycle, "al_l", "ar_r"), pairs);
        Assert.DoesNotContain(pairs, pair => pair.OutgoingRole == "xl_l" || pair.IncomingRole == "xl_l");
    }

    [Fact]
    public void SliceJoins_StartNoEarlierThanTheEntrysFirstLiftOff()
    {
        // The entry stands on both feet until its right foot lifts at 0.3 s.
        SeamClip entry = Clip(
            "i2f_l",
            SeamClipRole.Entry,
            1d,
            Intervals("i2f_l", false, 1d, [Seg(0d, 1d, true)], [Seg(0d, 0.3d, true), Seg(0.3d, 1d, false)]),
            _ => _origin,
            capability: StandingLocomotionCapability.EntryForwardLeft);
        SeamClip exit = Clip(
            "f2i_l",
            SeamClipRole.Exit,
            1d,
            AllPlanted("f2i_l", false, 1d),
            _ => _origin,
            capability: StandingLocomotionCapability.ExitForwardLeft);

        Assert.Equal(0.3d, SeamPairPolicy.FirstLiftOffSeconds(entry), 9);
        SeamPairSpec slice = Assert.Single(SeamPairPolicy.Relevant([entry, exit]), pair => pair.Kind == SeamKind.SliceJoin);
        Assert.Equal(0.3d, slice.MinimumOutgoingSeconds, 9);

        var table = SeamTable.Build([entry, exit], [slice], 0.15d, entryLagSeconds: 0d);
        SeamPair pair = Pair(table, "i2f_l", "f2i_l");
        Assert.Equal(0.3d, pair.MinimumOutgoingSeconds, 9);
        // a in {0.3, ..., 0.8} and b in {0, ..., 0.8}; the standing start (a < 0.3) is never evaluated.
        Assert.Equal(6 * 9, pair.EvaluatedCount);
        Assert.All(pair.Candidates, value => Assert.True(value.OutgoingTime >= 0.3d - 1e-9));
    }

    [Fact]
    public void Report_ListsCyclesOutsideEveryPairAsExcludedRatherThanUnavailable()
    {
        SeamClip planned = Cycle("planned", 1d, AllPlanted("planned", true, 1d), _ => new Vector3(1f, 0f, 0f));
        SeamClip lateral = Cycle("lateral", 1d, AllPlanted("lateral", true, 1d), _ => _origin);
        SeamClip exit = Clip("exit", SeamClipRole.Exit, 1d, AllPlanted("exit", false, 1d), _ => _origin);
        var table = SeamTable.Build([planned, lateral, exit], [new(SeamKind.CycleToExit, "planned", "exit")], 0.15d, entryLagSeconds: 0d);

        var report = SeamReport.Create("synthetic", table, new SeamTolerance(0.1f, SeamToleranceAgreementStatus.Pending, "pending"));

        // Every clip is either in the planner set or excluded with a reason; availability covers planner-set cycles only.
        Assert.Equal(["exit", "planned"], report.PlannerSetClips);
        SeamClipExclusion excluded = Assert.Single(report.ExcludedClips);
        Assert.Equal(new SeamClipExclusion("lateral", SeamExclusionReason.NoPlannedPair), excluded);
        Assert.True(table.IsInPlannerSet("planned"));
        Assert.False(table.IsInPlannerSet("lateral"));
        Assert.Equal(["planned"], report.UnavailableCycles);
        Assert.Equal(["planned"], report.Cycles.Select(cycle => cycle.CycleRole));
        Assert.False(table.IsPlannable("planned", 0.1f));
        Assert.True(table.IsPlannable("planned", 1f));
        Assert.False(table.IsPlannable("lateral", 1f));
        Assert.Contains("Excluded clips (not planned; distinct from unavailable): lateral (NoPlannedPair)", report.ToText(), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(report.ToJson());
        Assert.Equal("lateral", json.RootElement.GetProperty("excluded_clips")[0].GetProperty("role").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("planner_set_clips").GetArrayLength());
    }

    [Fact]
    public void PlannerSetBuild_ReportsEveryClipAsPlannedOrExcludedAndListsExcludedPairs()
    {
        SeamClip[] clips =
        [
            Package("wf", SeamClipRole.Cycle, StandingLocomotionCapability.CyclicForwardPrimary),
            Package("al_l", SeamClipRole.Cycle, StandingLocomotionCapability.CyclicArcLeftLeftLane),
            Package("ar_r", SeamClipRole.Cycle, StandingLocomotionCapability.CyclicArcRightRightLane),
            Package("xl_l", SeamClipRole.Cycle, StandingLocomotionCapability.CyclicLateralLeftLeftLane),
            Package("i2f_l", SeamClipRole.Entry, StandingLocomotionCapability.EntryForwardLeft),
            Package("f2i_r", SeamClipRole.Exit, StandingLocomotionCapability.ExitForwardRight),
            Clip("idle", SeamClipRole.Idle, 1d, AllPlanted("idle", true, 1d), _ => _origin),
        ];

        var table = SeamTable.Build(clips);
        var report = SeamReport.Create("synthetic", table, new SeamTolerance(0.1f, SeamToleranceAgreementStatus.Pending, "pending"));

        Assert.Equal(["al_l", "ar_r", "f2i_r", "i2f_l", "idle", "wf"], report.PlannerSetClips);
        Assert.Equal([new SeamClipExclusion("xl_l", SeamExclusionReason.LateralCycle)], report.ExcludedClips);
        Assert.Contains(report.ExcludedPairs, value => value.Pair.OutgoingRole == "al_l" && value.Pair.IncomingRole == "ar_r" && value.Reason == SeamExclusionReason.TurningSideLaneArcSwitch);
        Assert.Contains(report.ExcludedPairs, value => value.Pair.OutgoingRole == "i2f_l" && value.Pair.IncomingRole == "ar_r" && value.Reason == SeamExclusionReason.WrongLaneArcEntry);
        Assert.Contains(report.ExcludedPairs, value => value.Pair.OutgoingRole == "xl_l" && value.Reason == SeamExclusionReason.LateralCycle);
        Assert.All(report.ExcludedPairs, value => Assert.False(table.IsInPlannerSet(value.Pair.OutgoingRole, value.Pair.IncomingRole)));
        Assert.True(table.IsInPlannerSet("wf", "al_l"));
        // Lateral cycles never count as unavailable: only planner-set cycles are listed.
        Assert.Equal(["al_l", "ar_r", "wf"], report.Cycles.Select(cycle => cycle.CycleRole));
        Assert.DoesNotContain("xl_l", report.UnavailableCycles);
        Assert.False(table.IsPlannable("xl_l", 1f));
        Assert.True(table.IsPlannable("wf", 1f));
        using var json = JsonDocument.Parse(report.ToJson());
        Assert.Equal(report.ExcludedPairs.Count, json.RootElement.GetProperty("excluded_pairs").GetArrayLength());
    }

    [Fact]
    public void Build_EvaluatesTheOutgoingClipOneEntryLagAheadOfTheIncoming()
    {
        // Identical cycles, ball x = t. With the runtime entry lag λ = 1/60 s, seam (a, b) blends the outgoing clip
        // from a + λ against the incoming one from b over w = 0.15 − λ.
        SeamClip first = Cycle("first", 1d, Alternating(1d), t => new Vector3((float)t, 0f, 0f));
        SeamClip second = Cycle("second", 1d, Alternating(1d), t => new Vector3((float)t, 0f, 0f));
        const double lag = 1d / 60d;
        var table = SeamTable.Build([first, second], [new(SeamKind.CycleToCycle, "first", "second")], 0.15d);
        SeamPair pair = Pair(table, "first", "second");

        Assert.Equal(lag, table.EntryLagSeconds, 12);
        Assert.Equal(0.15d, pair.CrossfadeSeconds, 12);
        Assert.Equal(0.15d - lag, pair.WindowSeconds, 12);
        // Same-phase seams keep the one-tick lead as a constant λ of ball separation.
        SeamCandidate same = Assert.Single(pair.Candidates, value => Near(value.OutgoingTime, 0.2d) && Near(value.IncomingTime, 0.2d));
        Assert.Equal((float)lag, same.MaxSeparation, Tolerance);
        // (0.4, 0.4) crosses the 0.5 s lift-off: the outgoing clip lifts at τ = 0.1 − λ, the incoming one at τ = 0.1.
        Assert.DoesNotContain(pair.Candidates, value => Near(value.OutgoingTime, 0.4d) && Near(value.IncomingTime, 0.4d));
        Assert.False(table.TryEvaluateSeam("first", "second", 0.4d, 0.4d, out _));
        // Entering the incoming clip one lag later crosses both lift-offs together.
        Assert.True(table.TryEvaluateSeam("first", "second", 0.4d, 0.4d + lag, out SeamCandidate shifted));
        Assert.Equal(0f, shifted.MaxSeparation, Tolerance);

        // Without the lag, the same pairs behave the other way round.
        var together = SeamTable.Build([first, second], [new(SeamKind.CycleToCycle, "first", "second")], 0.15d, entryLagSeconds: 0d);
        Assert.True(together.TryEvaluateSeam("first", "second", 0.4d, 0.4d, out _));
        Assert.False(together.TryEvaluateSeam("first", "second", 0.4d, 0.4d + lag, out _));
    }

    [Fact]
    public void Build_WindowsOfClipsPlayedOnceFitTheirLaggedParts()
    {
        // The outgoing exit plays [a + λ, a + d], so a + d must fit; the incoming exit plays [b, b + d − λ].
        SeamClip exit = Clip("exit", SeamClipRole.Exit, 1d, AllPlanted("exit", false, 1d), _ => _origin);
        SeamClip idle = Clip("idle", SeamClipRole.Idle, 1d, AllPlanted("idle", true, 1d), _ => _origin);
        SeamClip cycle = Cycle("cycle", 1d, AllPlanted("cycle", true, 1d), _ => _origin);
        var table = SeamTable.Build(
            [exit, idle, cycle],
            [new(SeamKind.ManoeuvreToIdle, "exit", "idle"), new(SeamKind.CycleToExit, "cycle", "exit")],
            0.2d,
            entryLagSeconds: 0.1d);

        // Outgoing: a + 0.2 <= 1, so a <= 0.8. Incoming: b + 0.1 <= 1, so b <= 0.9.
        Assert.Contains(Pair(table, "exit", "idle").Candidates, value => Near(value.OutgoingTime, 0.8d));
        Assert.DoesNotContain(Pair(table, "exit", "idle").Candidates, value => value.OutgoingTime > 0.85d);
        Assert.Contains(Pair(table, "cycle", "exit").Candidates, value => Near(value.IncomingTime, 0.9d));
        _ = Assert.Throws<ArgumentException>(() => SeamTable.Build([exit, idle], [new(SeamKind.ManoeuvreToIdle, "exit", "idle")], 0.1d, entryLagSeconds: 0.1d));
    }

    [Fact]
    public void CrossfadePolicy_ShortensIdleJoinsWhoseDoubleSupportRunIsShorterThanTheBaseWindow()
    {
        // The entry stands on both feet only until 0.1 s; the base window (0.15 − 1/60 s) cannot fit that run.
        SeamClip entry = Clip(
            "i2f_l",
            SeamClipRole.Entry,
            1d,
            Intervals("i2f_l", false, 1d, [Seg(0d, 1d, true)], [Seg(0d, 0.1d, true), Seg(0.1d, 1d, false)]),
            _ => _origin,
            capability: StandingLocomotionCapability.EntryForwardLeft);
        // The exit lands its last foot at 0.8 s: its final run (0.2 s) fits the base window.
        SeamClip exit = Clip(
            "f2i_l",
            SeamClipRole.Exit,
            1d,
            Intervals("f2i_l", false, 1d, [Seg(0d, 1d, true)], [Seg(0d, 0.8d, false), Seg(0.8d, 1d, true)]),
            _ => _origin,
            capability: StandingLocomotionCapability.ExitForwardLeft);
        // A clip that never stands on both feet: no crossfade duration helps.
        SeamClip swinging = Clip(
            "i2b_l",
            SeamClipRole.Entry,
            1d,
            Intervals("i2b_l", false, 1d, [Seg(0d, 0.2d, false), Seg(0.2d, 1d, true)], [Seg(0d, 0.2d, true), Seg(0.2d, 1d, false)]),
            _ => _origin,
            capability: StandingLocomotionCapability.EntryBackwardLeft);
        SeamClip idle = Clip("idle", SeamClipRole.Idle, 1d, AllPlanted("idle", true, 1d), _ => _origin);
        SeamCrossfadePolicy policy = SeamCrossfadePolicy.Default;

        Assert.Equal(0.1d, SeamCrossfadePolicy.EntrySupportSeconds(entry), 9);
        Assert.Equal(0.2d, SeamCrossfadePolicy.ExitSupportSeconds(exit), 9);
        Assert.Equal(0d, SeamCrossfadePolicy.EntrySupportSeconds(swinging), 9);
        // Window d − λ fits 0.1 s less the one-tick margin: d = 0.1 s, six ticks.
        Assert.Equal(0.1d, policy.PairCrossfadeSeconds(SeamKind.IdleToManoeuvre, idle, entry)!.Value, 9);
        Assert.Null(policy.PairCrossfadeSeconds(SeamKind.ManoeuvreToIdle, exit, idle));
        Assert.Null(policy.PairCrossfadeSeconds(SeamKind.IdleToManoeuvre, idle, swinging));
        Assert.Null(policy.PairCrossfadeSeconds(SeamKind.SliceJoin, entry, exit));
        // A very short run is floored at two ticks.
        Assert.Equal(2d / 60d, (policy with
        {
            MarginSeconds = 0.09d
        }).PairCrossfadeSeconds(SeamKind.IdleToManoeuvre, idle, entry)!.Value, 9);

        var table = SeamTable.Build([entry, exit, swinging, idle]);
        SeamPair shortened = Pair(table, "idle", "i2f_l");
        Assert.Equal(0.1d, shortened.CrossfadeSeconds, 9);
        Assert.Contains(shortened.Candidates, value => Near(value.IncomingTime, 0d));
        Assert.Equal(SeamTable.DefaultCrossfadeSeconds, Pair(table, "f2i_l", "idle").CrossfadeSeconds, 12);
        Assert.True(Pair(table, "idle", "i2b_l").IsImpossible);
        // At the base duration the same join is impossible.
        var unshortened = SeamTable.Build([entry, idle], [new(SeamKind.IdleToManoeuvre, "idle", "i2f_l")]);
        Assert.True(Pair(unshortened, "idle", "i2f_l").IsImpossible);

        var report = SeamReport.Create("synthetic", table, new SeamTolerance(0.1f, SeamToleranceAgreementStatus.Pending, "pending"));
        Assert.Equal(["idle->i2f_l"], report.ShortenedCrossfades.Select(pair => $"{pair.OutgoingRole}->{pair.IncomingRole}"));
        Assert.Contains("idle->i2b_l", report.ImpossiblePairs.Select(pair => $"{pair.OutgoingRole}->{pair.IncomingRole}"));
        using var json = JsonDocument.Parse(report.ToJson());
        Assert.Contains(
            json.RootElement.GetProperty("measured_pairs").EnumerateArray(),
            pair => pair.GetProperty("incoming").GetString() == "i2f_l" && Math.Abs(pair.GetProperty("crossfade_seconds").GetDouble() - 0.1d) < 1e-9);
    }

    [Fact]
    public void LoopingConfinedManoeuvre_WrapsSoAWholeStepCrossesItsLoopPoint()
    {
        // A looping side-step that starts mid-step: left swings [0, 0.3), right swings [0.3, 0.6), both planted from
        // 0.6 s until the next lift-off at the loop point. A join from Idle agrees only in [0.6, 1).
        static PlantedClipIntervals Step(bool looping)
        {
            return Intervals(
                looping ? "sl" : "sl_once",
                looping,
                1d,
                [Seg(0d, 0.3d, false), Seg(0.3d, 1d, true)],
                [Seg(0d, 0.3d, true), Seg(0.3d, 0.6d, false), Seg(0.6d, 1d, true)]);
        }

        SeamClip step = Clip("sl", SeamClipRole.Confined, 1d, Step(true), _ => _origin, capability: StandingLocomotionCapability.ConfinedSideStepLeft);
        SeamClip once = Clip("sl_once", SeamClipRole.Confined, 1d, Step(false), _ => _origin, capability: StandingLocomotionCapability.ConfinedSideStepLeft);
        SeamClip idle = Clip("idle", SeamClipRole.Idle, 1d, AllPlanted("idle", true, 1d), _ => _origin);

        Assert.True(step.WrapsPlayback);
        Assert.False(once.WrapsPlayback);
        Assert.Equal(0.4d, SeamCrossfadePolicy.EntrySupportSeconds(step), 9);
        Assert.Equal(0d, SeamCrossfadePolicy.EntrySupportSeconds(once), 9);

        var table = SeamTable.Build(
            [step, once, idle],
            [
                new(SeamKind.IdleToManoeuvre, "idle", "sl"),
                new(SeamKind.ManoeuvreToIdle, "sl", "idle"),
                new(SeamKind.ManoeuvreToIdle, "sl_once", "idle"),
            ]);
        Assert.All(Pair(table, "idle", "sl").Candidates, value => Assert.InRange(value.IncomingTime, 0.6d - 1e-9, 1d));
        // Entered late in its run (0.8 s), the step continues across the loop point and returns to Idle once both
        // feet have stepped: the next aligned exit is at 0.6 s of the following lap.
        Assert.True(table.TryFindNextAligned("sl", "idle", 0.9d, 0.01f, out SeamChoice wrapped));
        Assert.Equal(0.6d, wrapped.Candidate.OutgoingTime, 9);
        Assert.Equal(0.7d, wrapped.DelaySeconds, 9);
        // Played once, the same clip has no exit after its window's last fit (a + d <= 1).
        Assert.False(table.TryFindNextAligned("sl_once", "idle", 0.9d, 0.01f, out _));
    }

    [Fact]
    public void Availability_CoversLoopingManoeuvresThroughTheirIdleExits()
    {
        // Two looping side-steps: "sl" returns to Idle in its double support; "stuck" stands only where its balls are
        // 100 mm from Idle's, so under a 50 mm tolerance it has no aligned exit into Idle in any loop. A pivot played
        // once is not subject to availability.
        static PlantedClipIntervals Step(string identity, bool looping)
        {
            return Intervals(
                identity,
                looping,
                1d,
                [Seg(0d, 0.3d, false), Seg(0.3d, 1d, true)],
                [Seg(0d, 0.3d, true), Seg(0.3d, 0.6d, false), Seg(0.6d, 1d, true)]);
        }

        SeamClip step = Clip("sl", SeamClipRole.Confined, 1d, Step("sl", true), _ => _origin, capability: StandingLocomotionCapability.ConfinedSideStepLeft);
        SeamClip stuck = Clip("sr", SeamClipRole.Confined, 1d, Step("sr", true), _ => new Vector3(0.1f, 0f, 0f), capability: StandingLocomotionCapability.ConfinedSideStepRight);
        SeamClip once = Clip("pl45", SeamClipRole.Confined, 1d, AllPlanted("pl45", false, 1d), _ => _origin, capability: StandingLocomotionCapability.ConfinedPivotLeft);
        SeamClip cycle = Cycle("wf", 1d, AllPlanted("wf", true, 1d), _ => _origin);
        SeamClip exit = Clip("f2i_r", SeamClipRole.Exit, 1d, AllPlanted("f2i_r", false, 1d), _ => _origin);
        SeamClip idle = Clip("idle", SeamClipRole.Idle, 1d, AllPlanted("idle", true, 1d), _ => _origin);
        var table = SeamTable.Build(
            [step, stuck, once, cycle, exit, idle],
            [
                new(SeamKind.IdleToManoeuvre, "idle", "sl"),
                new(SeamKind.ManoeuvreToIdle, "sl", "idle"),
                new(SeamKind.IdleToManoeuvre, "idle", "sr"),
                new(SeamKind.ManoeuvreToIdle, "sr", "idle"),
                new(SeamKind.ManoeuvreToIdle, "pl45", "idle"),
                new(SeamKind.CycleToExit, "wf", "f2i_r"),
            ]);

        Assert.Equal(SeamKind.ManoeuvreToIdle, SeamTable.AvailabilityExitKind(step));
        Assert.Equal(SeamKind.CycleToExit, SeamTable.AvailabilityExitKind(cycle));
        Assert.Null(SeamTable.AvailabilityExitKind(once));
        Assert.Null(SeamTable.AvailabilityExitKind(idle));
        Assert.True(table.IsPlannable("sl", 0.05f));
        Assert.False(table.IsPlannable("sr", 0.05f));
        Assert.True(table.IsPlannable("sr", 0.2f));
        Assert.False(table.IsPlannable("pl45", 1f));
        Assert.True(table.IsPlannable("wf", 0.05f));

        var report = SeamReport.Create("synthetic", table, new SeamTolerance(0.05f, SeamToleranceAgreementStatus.Pending, "pending"), [0.2f]);

        Assert.Equal(["sl", "sr", "wf"], report.Cycles.Select(value => value.CycleRole));
        Assert.Equal([SeamClipRole.Confined, SeamClipRole.Confined, SeamClipRole.Cycle], report.Cycles.Select(value => value.Kind));
        Assert.Equal(["sr"], report.UnavailableCycles);
        SeamCycleExits steps = Assert.Single(report.Cycles, value => value.CycleRole == "sl");
        // Aligned exits into Idle wherever the loop stands on both feet for the whole window: a + λ in [0.6, 1 − w].
        Assert.All(steps.Exits, value => Assert.Equal("idle", value.ExitRole));
        Assert.All(steps.Exits, value => Assert.InRange(value.OutgoingTime, 0.6d - (1d / 60d) - 1e-9, 1d));
        Assert.True(Assert.Single(report.Sweep).Cycles.All(value => value.IsAvailable));
        Assert.Contains("Unavailable cyclic clips and looping manoeuvres: sr", report.ToText(), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(report.ToJson());
        Assert.Equal("sr", json.RootElement.GetProperty("unavailable_cyclic_clips_and_looping_manoeuvres")[0].GetString());
        Assert.Contains(
            json.RootElement.GetProperty("cyclic_clips_and_looping_manoeuvres").EnumerateArray(),
            value => value.GetProperty("cycle").GetString() == "sl" && value.GetProperty("kind").GetString() == "Confined" && value.GetProperty("available").GetBoolean());
    }

    [Fact]
    public void Build_RejectsDuplicateAndUnknownRoles()
    {
        SeamClip cycle = Cycle("cycle", 1d, AllPlanted("cycle", true, 1d), _ => _origin);

        _ = Assert.Throws<ArgumentException>(() => SeamTable.Build([cycle, cycle], []));
        _ = Assert.Throws<ArgumentException>(() => SeamTable.Build([cycle], [new(SeamKind.CycleToCycle, "cycle", "missing")]));
        _ = Assert.Throws<ArgumentException>(() => SeamTable.Build([cycle], [], 0d));
    }

    private static SeamPair Pair(SeamTable table, string outgoing, string incoming)
    {
        Assert.True(table.TryGetPair(outgoing, incoming, out SeamPair? pair));
        return pair!;
    }

    private static bool Near(double value, double expected) => Math.Abs(value - expected) < 1e-9;

    private static SeamClip Package(string role, SeamClipRole kind, StandingLocomotionCapability capability)
        => Clip(role, kind, 1d, AllPlanted(role, kind == SeamClipRole.Cycle, 1d), _ => _origin, capability: capability);

    private static SeamClip Cycle(
        string role,
        double duration,
        PlantedClipIntervals intervals,
        Func<double, Vector3> left,
        Func<double, Vector3>? right = null)
        => Clip(role, SeamClipRole.Cycle, duration, intervals, left, right);

    private static SeamClip Clip(
        string role,
        SeamClipRole kind,
        double duration,
        PlantedClipIntervals intervals,
        Func<double, Vector3> left,
        Func<double, Vector3>? right = null,
        StandingLocomotionCapability? capability = null)
    {
        int count = ClipTrajectory.ExpectedSampleCount(duration, Rate);
        ImmutableArray<Vector3> Samples(Func<double, Vector3> path)
        {
            return [.. Enumerable.Range(0, count).Select(index => path(ClipTrajectory.SampleTimeOf(index, duration, Rate)))];
        }

        return new SeamClip(
            role,
            intervals.Identity,
            kind,
            duration,
            Rate,
            Samples(left),
            Samples(right ?? left),
            0f,
            intervals)
        {
            Capability = capability,
        };
    }

    /// <summary>Left planted over the first half, right planted over the second half.</summary>
    private static PlantedClipIntervals Alternating(double duration)
        => Intervals(
            Guid.NewGuid().ToString("N"),
            true,
            duration,
            [Seg(0d, duration / 2d, true), Seg(duration / 2d, duration, false)],
            [Seg(0d, duration / 2d, false), Seg(duration / 2d, duration, true)]);

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
