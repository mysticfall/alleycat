using AlleyCat.TestFramework;
using Xunit;
using static AlleyCat.IntegrationTests.Mind.Attention.RestAtLimitExclusion;

namespace AlleyCat.IntegrationTests.Mind.Attention;

/// <summary>Cheap synthetic TR23 boundary replays; no character, rendering or physical-position oracle.</summary>
[Headless]
public sealed class RestAtLimitBoundaryReplaysTests
{
    /// <inheritdoc/>
    [Fact]
    public void AnalyticalReference_IsIndependentOfRecordedCommandAndActualPose()
    {
        FreezeSample sample = Series()[0];
        Context context = Healthy(sample, 0);
        Decision result = Evaluate(sample, context);
        Assert.True(result.Eligible, result.Reason);
        // Independently authored 100° target, 75° envelope, zero lever/offset: 25° residual, no pitch/roll.
        Assert.Equal(75, result.Reference.Yaw);
        Assert.Equal(0, result.Reference.Pitch);
        Assert.Equal(0, result.Reference.Roll);
        Assert.Equal(25, result.ExpectedResidual, 8);
        Assert.Equal(25, result.UnreachableLowerBound, 8);
        Decision wrong = Evaluate(sample, context with
        {
            RecordedCommand = new Pose(70, 3, 1)
        });
        Assert.False(wrong.Eligible);
        Assert.Equal(result.Reference, wrong.Reference);
    }

    /// <inheritdoc/>
    [Fact]
    public void CorrectAttainedUnreachableRest_IsExcludedWithoutChangingRawFreeze()
    {
        FreezeSample[] samples = Series();
        Context?[] contexts = [.. samples.Select(Healthy)];
        Assert.Equal(0.5, FullFreezeDetector.FindLongestFullFreezeSeconds(samples), 12);
        Assert.Equal(0, FullFreezeDetector.FindLongestFullFreezeSeconds(samples, restContexts: contexts));
        Assert.All(samples.Select((s, i) => Evaluate(s, contexts[i])), decision => Assert.True(decision.Eligible));
    }

    /// <inheritdoc/>
    [Fact]
    public void ReachableOrUnsupportedProofs_CannotExcludeLimitOccupancy()
    {
        AssertDetected("reachable inside envelope", 40, c => c with
        {
            Geometry = c.Geometry with
            {
                Anchor = Anchor(35)
            },
            CurrentAnchor = Anchor(35)
        });

        // The pivot ray is beyond the cap, but an upstream lateral lever permits centring at yaw70°:
        // sin(100°-70°)*0.2m = 0.1m. Cap occupancy alone would falsely excuse the frozen yaw75°.
        double residual = Math.Atan2(0.1 - (0.2 * Math.Sin(Radians(25))), 0.2 * Math.Cos(Radians(25))) * 180 / Math.PI;
        AssertDetected("unreachable bound load-bearing", residual, c => c with
        {
            Geometry = c.Geometry with
            {
                Anchor = Anchor(100, distance: 0.2),
                Head = new Vector(-0.1, 0, 0)
            },
            CurrentAnchor = Anchor(100, distance: 0.2)
        }, "unreachable-proof-failed");

        AssertDetected("unsupported yaw-cap proof", 95, c => c with
        {
            Geometry = c.Geometry with
            {
                Anchor = Anchor(170)
            },
            CurrentAnchor = Anchor(170)
        }, "unsupported-yaw-proof");
        AssertDetected("inadmissible analytical pitch branch", 60, c => c with
        {
            Geometry = c.Geometry with
            {
                Anchor = Anchor(100, pitch: 60)
            },
            CurrentAnchor = Anchor(100, pitch: 60)
        }, "unsupported-pitch-branch");
    }

    /// <inheritdoc/>
    [Fact]
    public void WrongCommandsAndUnattainedApproaches_RemainDetected()
    {
        AssertDetected("wrong yaw command", 30, c => c with
        {
            RecordedCommand = new Pose(70, 0, 0),
            CurrentCommand = new Pose(70, 0, 0),
            Actual = new Pose(70, 0, 0)
        }, "wrong-command", actualYaw: 70);
        AssertDetected("wrong pitch command", 25, c => c with
        {
            RecordedCommand = new Pose(75, 0.1, 0),
            CurrentCommand = new Pose(75, 0.1, 0),
            Actual = new Pose(75, 0.1, 0)
        }, "wrong-command", actualPitch: 0.1);
        AssertDetected("non-roll-free command", 25, c => c with
        {
            RecordedCommand = new Pose(75, 0, 0.1),
            CurrentCommand = new Pose(75, 0, 0.1),
            Actual = new Pose(75, 0, 0.1)
        }, "wrong-command", actualRoll: 0.1);
        AssertDetected("still short of the correct command", 25.06, c => c with { Actual = new Pose(74.94, 0, 0) },
            "unattained", actualYaw: 74.94);
    }

    /// <inheritdoc/>
    [Fact]
    public void MissingAndStaleEvidence_FailsClosed()
    {
        (string Name, Func<Context, Context?> Change)[] cases =
        [
            ("missing context", _ => null),
            ("missing geometry", c => c with { Geometry = null! }),
            ("invalid packet", c => c with { PacketValid = false }),
            ("changed target identity", c => c with { Current = c.Current with { Anchor = 2 } }),
            ("changed target position", c => c with { CurrentAnchor = c.CurrentAnchor + new Vector(1, 0, 0) }),
            ("stale root", c => c with { CurrentEnvelope = c.CurrentEnvelope with { Origin = new Vector(1, 0, 0) } }),
            ("stale generation/binding", c => c with { Current = c.Current with { Generation = 2 } }),
            ("stale upstream sequence", c => c with { Current = c.Current with { Sequence = c.Current.Sequence + 1 } }),
            ("stale command sequence", c => c with { Current = c.Current with { CommandSequence = 0 } }),
            ("stale skeleton binding", c => c with { Current = c.Current with { Skeleton = 2 } }),
            ("changed skeleton transform", c => c with { CurrentSkeleton = c.CurrentSkeleton with { Origin = new Vector(0, 1, 0) } }),
            ("stale presentation frame", c => c with { Current = c.Current with { Frame = -1 } }),
            ("stale observation time", c => c with { SampleTime = c.SampleTime + 0.001 }),
            ("invalid upstream basis", c => c with { Geometry = c.Geometry with { Upstream = new Basis(new Vector(2, 0, 0), new Vector(0, 1, 0), new Vector(0, 0, 1)) } }),
            ("stale actual-pose channel", c => c with { Actual = new Pose(74, 0, 0) }),
            ("changed current command", c => c with { CurrentCommand = new Pose(70, 0, 0) })
        ];
        foreach ((string name, Func<Context, Context?> change) in cases)
        {
            AssertDetected(name, 25, change);
        }
    }

    /// <inheritdoc/>
    [Fact]
    public void EligibilityChangesInsideRawFreeze_PreserveApproachAndInvalidatedTails()
    {
        FreezeSample[] samples = Series();
        Context?[] approach = [.. samples.Select((s, i) => i < 7 ? null : Healthy(s, i))];
        Assert.Equal(0.15, FullFreezeDetector.FindLongestFullFreezeSeconds(samples, restContexts: approach), 12);

        Context?[] changedGeometry = [.. samples.Select((s, i) =>
        {
            Context c = Healthy(s, i);
            // Freshly stamped upstream geometry changes the independent pitch reference; old commands
            // must not inherit eligibility from the preceding rest, even at the same yaw cap.
            return i < 13 ? c : c with
            {
                Geometry = c.Geometry with { Head = new Vector(0, 0.05, 0) },
                Current = c.Current with { Sequence = c.Current.Sequence + 1, CommandSequence = c.Current.Sequence + 1 },
                Captured = c.Captured with { Sequence = c.Captured.Sequence + 1, CommandSequence = c.Captured.Sequence + 1 }
            };
        })];
        Assert.Equal(0.175, FullFreezeDetector.FindLongestFullFreezeSeconds(samples, restContexts: changedGeometry), 12);
        Assert.Equal(0.5, FullFreezeDetector.FindLongestFullFreezeSeconds(samples), 12);
    }

    /// <inheritdoc/>
    [Fact]
    public void ResidualConsistency_IsIndependentAndLoadBearing()
        => AssertDetected("residual disagrees with upstream/requested geometry", 25.1, c => c, "inconsistent-residual");

    /// <inheritdoc/>
    [Fact]
    public void MisSizedContextList_CannotTrimSamplesOrExcludeAnything()
    {
        FreezeSample[] samples = Series();
        Context?[] incomplete = [.. samples.Take(samples.Length - 1).Select(Healthy)];
        Assert.Equal(0.5, FullFreezeDetector.FindLongestFullFreezeSeconds(samples, restContexts: incomplete), 12);
    }

    private static FreezeSample[] Series(double residual = 25, double yaw = 75, double pitch = 0, double roll = 0)
        => [.. Enumerable.Range(0, 21).Select(i => new FreezeSample(i * 0.025, yaw, pitch, roll, residual, 1))];

    private static Context Healthy(FreezeSample sample, int frame)
    {
        var identity = new Transform(Basis.Identity, new Vector(0, 0, 0));
        var geometry = new Geometry(Anchor(100), new Vector(0, 0, 0), new Vector(0, 0, 0), Basis.Identity,
            new Vector(0, 0, 0), identity, 75, 40, 55);
        var stamp = new Stamp(1, 1, (ulong)frame + 1, 1, frame, (ulong)frame + 1);
        var pose = new Pose(75, 0, 0);
        return new Context(geometry, stamp, stamp, geometry.Anchor, identity, identity, identity,
            pose, pose, pose, true, sample.TimeSeconds);
    }

    private static void AssertDetected(string name, double residual, Func<Context, Context?> change,
        string? reason = null, double actualYaw = 75, double actualPitch = 0, double actualRoll = 0)
    {
        FreezeSample[] samples = Series(residual, actualYaw, actualPitch, actualRoll);
        Context?[] contexts = [.. samples.Select((s, i) => change(Healthy(s, i)))];
        for (int i = 0; i < samples.Length; i++)
        {
            Decision decision = Evaluate(samples[i], contexts[i]);
            Assert.False(decision.Eligible, name);
            if (reason is not null)
            {
                Assert.Equal(reason, decision.Reason);
            }
        }

        Assert.Equal(0.5, FullFreezeDetector.FindLongestFullFreezeSeconds(samples, restContexts: contexts), 12);
    }

    private static Vector Anchor(double yaw, double pitch = 0, double distance = 5)
        => new(-distance * Math.Sin(Radians(yaw)) * Math.Cos(Radians(pitch)), distance * Math.Sin(Radians(pitch)),
            -distance * Math.Cos(Radians(yaw)) * Math.Cos(Radians(pitch)));

    private static double Radians(double degrees) => degrees * Math.PI / 180;
}
