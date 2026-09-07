using AlleyCat.IK;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

#pragma warning disable CS1591

/// <summary>Independent mutation and raw/native calibration proof for the post-IK visual pop detector.</summary>
public sealed class NPCFootMotionContinuityDetectorIntegrationTests
{
    private const int Count = 18;
    private const float Delta = 1f / 60f;

    [Headless]
    [Fact]
    public async Task RawClipBounds_UseFixtureDeclaredSkeletonAndClips()
    {
        SceneTree tree = GetSceneTree();
        using var fixture = SyntheticLocomotionRuntimeFixture.Create("foot-motion");
        tree.Root.AddChild(fixture.Root);
        try
        {
            await WaitForNextFrameAsync(tree);
            AnimationPlayer player = fixture.Player;
            Skeleton3D skeleton = fixture.Skeleton;
            string[] clips = ["locomotion/stride_loop", "locomotion/exit_alpha", "locomotion/exit_beta"];
            NPCFootMotionCalibration calibration = NPCFootRawClipBaselineLoader.Load(
                player,
                skeleton,
                clips);

            Assert.True(calibration.IsValid, calibration.BuildReport());
            Assert.Equal(3, calibration.Clips.Count);
            Assert.Equal(20, calibration.Bands.Count);
            Assert.All(calibration.Bands.Values, band =>
            {
                Assert.Equal(band.MaximumOrdinary + band.SafetyMargin, band.High, 5);
                Assert.Equal(band.High * NPCFootMotionContinuityDetector.LowReleaseFraction, band.Low, 5);
                Assert.True(0f < band.Low && band.Low < band.High);
            });

            Assert.True(FootPosePointDefinition.TryCreate(skeleton, LimbSide.Left, out FootPosePointDefinition left));
            Assert.True(FootPosePointDefinition.TryCreate(skeleton, LimbSide.Right, out FootPosePointDefinition right));
            Assert.Contains("runtime imported rest", left.Source, StringComparison.Ordinal);
            Assert.Contains("runtime imported rest", right.Source, StringComparison.Ordinal);
            Assert.Equal("LeftFoot", left.FootBone.ToString());
            Assert.Equal("LeftToes", left.ToeBone.ToString());
            Assert.Equal("RightFoot", right.FootBone.ToString());
            Assert.Equal("RightToes", right.ToeBone.ToString());
            const string SourceClip = "locomotion/stride_loop";
            IReadOnlyList<NPCFootRawClipSample> sourceFrames = calibration.Clips[SourceClip];
            Assert.True(sourceFrames.Count >= 2);
            Assert.Equal((0, 0f), (sourceFrames[0].Frame, sourceFrames[0].TimeSeconds));
            Assert.Equal((1, Delta), (sourceFrames[1].Frame, sourceFrames[1].TimeSeconds));
            Assert.Equal(sourceFrames[0].DurationSeconds, sourceFrames[1].DurationSeconds);
            Transform3D foot = skeleton.GetBoneGlobalPose(skeleton.FindBone("LeftFoot"));
            Transform3D toe = skeleton.GetBoneGlobalPose(skeleton.FindBone("LeftToes"));
            FootPosePointSet original = left.Evaluate(foot, toe);
            Transform3D rotatedFoot = new(new Basis(Vector3.Up, 0.2f) * foot.Basis, foot.Origin);
            FootPosePointSet rotated = left.Evaluate(rotatedFoot, rotatedFoot * foot.AffineInverse() * toe);
            Assert.Equal(original.Ankle.Origin, rotated.Ankle.Origin);
            Assert.True(original.HeelSole.Origin.DistanceTo(rotated.HeelSole.Origin) > 0.001f);
            Assert.True(original.ToeTip.Origin.DistanceTo(rotated.ToeTip.Origin) > 0.001f);
        }
        finally
        {
            fixture.Root.QueueFree();
            await WaitForNextFrameAsync(tree);
        }
    }

    [Headless]
    [Fact]
    public void HealthyFastSwing_AndCoherentActorTransport_AreAccepted()
    {
        Fixture fixture = CreateFixture(stepMetres: 0.035f); // 2.1 m/s, but smooth and within its native bound.
        AssertHealthy(Evaluate("healthy-fast-swing", fixture));
        List<NPCFootMotionContinuitySample> transported = Clone(fixture.Samples);
        ApplyCoherentActorTransport(transported);
        AssertHealthy(Evaluate("coherent-actor-transport", fixture with
        {
            Samples = transported
        }));
    }

    [Headless]
    [Fact]
    public void OneTickPositionPop_ReportsExactGroupedEvidence()
    {
        Fixture fixture = CreateFixture();
        List<NPCFootMotionContinuitySample> samples = Clone(fixture.Samples);
        Offset(samples, 6, LimbSide.Left, FootPosePoint.Midsole, 0.010f, persistent: true);

        NPCFootMotionEvent item = AssertSingleEvent(Evaluate("position-pop", fixture with
        {
            Samples = samples
        }), LimbSide.Left);
        Assert.Equal((106UL, 106UL, 109UL), (item.TriggerTick, item.PeakTick, item.EndTick));
        NPCFootPointEvidence point = Assert.Single(item.Points);
        Assert.Equal(FootPosePoint.Midsole, point.Point);
        AssertSignal(point.Position, 106, 106, 0.012f, 0.012f, PositionBand(fixture, LimbSide.Left, point.Point));
        AssertSignal(point.VelocityVectorChange, 106, 106, 0.60f, 0.60f,
            VelocityBand(fixture, LimbSide.Left, point.Point));
        Assert.DoesNotContain(item.Points, value => value.Point != FootPosePoint.Midsole);
    }

    [Headless]
    [Fact]
    public void VelocityVectorJumpBelowPositionBand_TriggersIndependently()
    {
        Fixture fixture = CreateFixture();
        List<NPCFootMotionContinuitySample> samples = Clone(fixture.Samples);
        Offset(samples, 6, LimbSide.Right, FootPosePoint.Ball, 0.004f, persistent: false);

        NPCFootMotionEvent item = AssertSingleEvent(Evaluate("velocity-only", fixture with
        {
            Samples = samples
        }), LimbSide.Right);
        Assert.Equal((107UL, 107UL, 110UL), (item.TriggerTick, item.PeakTick, item.EndTick));
        NPCFootPointEvidence point = Assert.Single(item.Points);
        Assert.Equal(FootPosePoint.Ball, point.Point);
        Assert.Null(point.Position);
        AssertSignal(point.VelocityVectorChange, 107, 107, 0.48f, 0.48f,
            VelocityBand(fixture, LimbSide.Right, point.Point));
    }

    [Headless]
    [Fact]
    public void HighEntry_TwoLowRelease_AndOneSampleRetrigger_AreOneEvent()
    {
        Fixture fixture = WithVelocityDisabled(CreateFixture());
        List<NPCFootMotionContinuitySample> samples = Clone(fixture.Samples);
        SetOffset(samples, 6, LimbSide.Left, FootPosePoint.HeelSole, 0.010f);
        SetOffset(samples, 7, LimbSide.Left, FootPosePoint.HeelSole, 0.010f); // first below-low sample
        SetOffset(samples, 8, LimbSide.Left, FootPosePoint.HeelSole, 0.020f); // retrigger after one sample
        for (int index = 9; index < samples.Count; index++)
        {
            SetOffset(samples, index, LimbSide.Left, FootPosePoint.HeelSole, 0.020f);
        }

        NPCFootMotionEvent item = AssertSingleEvent(Evaluate("hysteresis-retrigger", fixture with
        {
            Samples = samples
        }), LimbSide.Left);
        Assert.Equal((106UL, 108UL, 110UL), (item.TriggerTick, item.PeakTick, item.EndTick));
        NPCFootPointEvidence point = Assert.Single(item.Points);
        Assert.Equal(FootPosePoint.HeelSole, point.Point);
        AssertSignal(point.Position, 106, 108, 0.012f, 0.012f,
            PositionBand(fixture, LimbSide.Left, FootPosePoint.HeelSole));
        Assert.Null(point.VelocityVectorChange);
    }

    [Headless]
    [Fact]
    public void ToeOnlyAndHeelOnlyRotationPops_ReportOnlyMovedPhysicalPoints()
    {
        Fixture fixture = WithVelocityDisabled(CreateFixture());
        List<NPCFootMotionContinuitySample> toe = Clone(fixture.Samples);
        RotateSelectedPoints(toe, 6, LimbSide.Right, [FootPosePoint.Ball, FootPosePoint.ToeTip], 0.10f);
        NPCFootMotionEvent toeEvent = AssertSingleEvent(Evaluate("toe-rotation", fixture with
        {
            Samples = toe
        }), LimbSide.Right);
        Assert.Equal([FootPosePoint.Ball, FootPosePoint.ToeTip], toeEvent.Points.Select(point => point.Point));
        Assert.Equal((106UL, 106UL, 109UL), (toeEvent.TriggerTick, toeEvent.PeakTick, toeEvent.EndTick));
        Assert.All(toeEvent.Points, point => AssertSignal(
            point.Position,
            106,
            106,
            PositionStep(toe, LimbSide.Right, point.Point, 6),
            MaximumStep(toe, LimbSide.Right, point.Point, 6, 7),
            PositionBand(fixture, LimbSide.Right, point.Point)));
        Assert.All(toeEvent.Points, point => Assert.Null(point.VelocityVectorChange));

        List<NPCFootMotionContinuitySample> heel = Clone(fixture.Samples);
        RotateSelectedPoints(heel, 6, LimbSide.Left, [FootPosePoint.HeelSole], 0.15f);
        NPCFootMotionEvent heelEvent = AssertSingleEvent(Evaluate("heel-rotation", fixture with
        {
            Samples = heel
        }), LimbSide.Left);
        NPCFootPointEvidence heelPoint = Assert.Single(heelEvent.Points);
        Assert.Equal(FootPosePoint.HeelSole, heelPoint.Point);
        Assert.Equal((106UL, 107UL, 109UL), (heelEvent.TriggerTick, heelEvent.PeakTick, heelEvent.EndTick));
        AssertSignal(heelPoint.Position, 106, 107,
            PositionStep(heel, LimbSide.Left, FootPosePoint.HeelSole, 6),
            MaximumStep(heel, LimbSide.Left, FootPosePoint.HeelSole, 6, 7),
            PositionBand(fixture, LimbSide.Left, FootPosePoint.HeelSole));
        Assert.Null(heelPoint.VelocityVectorChange);
    }

    [Headless]
    [Fact]
    public void CorrelatedPoints_AreGroupedOnce_WithSubordinateEvidence()
    {
        Fixture fixture = CreateFixture();
        List<NPCFootMotionContinuitySample> samples = Clone(fixture.Samples);
        foreach (FootPosePoint point in Enum.GetValues<FootPosePoint>())
        {
            Offset(samples, 6, LimbSide.Right, point, 0.010f, persistent: true);
        }

        NPCFootMotionEvent item = AssertSingleEvent(Evaluate("correlated", fixture with
        {
            Samples = samples
        }), LimbSide.Right);
        Assert.Equal(Enum.GetValues<FootPosePoint>(), item.Points.Select(point => point.Point));
        Assert.Equal((106UL, 106UL, 109UL), (item.TriggerTick, item.PeakTick, item.EndTick));
        Assert.All(item.Points, point => AssertSignal(point.Position, 106, 106, 0.012f, 0.012f,
            PositionBand(fixture, LimbSide.Right, point.Point)));
        Assert.All(item.Points, point => AssertSignal(point.VelocityVectorChange, 106, 106, 0.60f, 0.60f,
            VelocityBand(fixture, LimbSide.Right, point.Point)));
    }

    [Headless]
    [Fact]
    public void DistinctPositionAndVelocityPeaks_RetainCoherentSignalRecords()
    {
        Fixture fixture = CreateFixture();
        List<NPCFootMotionContinuitySample> samples = Clone(fixture.Samples);
        for (int index = 6; index < samples.Count; index++)
        {
            SetOffset(samples, index, LimbSide.Left, FootPosePoint.Ankle, index == 6 ? 0.010f : 0.005f);
        }

        NPCFootMotionEvent item = AssertSingleEvent(Evaluate("distinct-peaks", fixture with
        {
            Samples = samples
        }), LimbSide.Left);
        NPCFootPointEvidence point = Assert.Single(item.Points);
        Assert.Equal(FootPosePoint.Ankle, point.Point);
        AssertSignal(point.Position, 106, 106, 0.012f, 0.012f,
            PositionBand(fixture, LimbSide.Left, point.Point));
        AssertSignal(point.VelocityVectorChange, 106, 107, 0.60f, 0.90f,
            VelocityBand(fixture, LimbSide.Left, point.Point));
        Assert.Equal((106UL, 107UL, 110UL), (item.TriggerTick, item.PeakTick, item.EndTick));
    }

    [Headless]
    [Fact]
    public void MissingAttribution_DoesNotHidePrimaryEvent_AndCausalClaimFailsClosed()
    {
        Fixture fixture = CreateFixture();
        List<NPCFootMotionContinuitySample> samples = Clone(fixture.Samples);
        for (int index = 0; index < samples.Count; index++)
        {
            samples[index] = samples[index] with
            {
                PreIK = null,
                LeftContact = null,
                RightContact = null
            };
        }
        Offset(samples, 6, LimbSide.Right, FootPosePoint.ToeTip, 0.010f, persistent: true);

        NPCFootMotionEvent item = AssertSingleEvent(Evaluate("missing-attribution", fixture with
        {
            Samples = samples
        }), LimbSide.Right);
        Assert.Equal((106UL, 106UL, 109UL), (item.TriggerTick, item.PeakTick, item.EndTick));
        NPCFootPointEvidence point = Assert.Single(item.Points);
        Assert.Equal(FootPosePoint.ToeTip, point.Point);
        AssertSignal(point.Position, 106, 106, 0.012f, 0.012f,
            PositionBand(fixture, LimbSide.Right, point.Point));
        AssertSignal(point.VelocityVectorChange, 106, 106, 0.60f, 0.60f,
            VelocityBand(fixture, LimbSide.Right, point.Point));
        Assert.Null(item.Attribution.PreIKStepMetres);
        Assert.Null(item.Attribution.PreviousContactCorrectionMetres);
        Assert.False(item.Attribution.HasCausalClaim);
    }

    [Headless]
    [Fact]
    public void SkippedDuplicateAndOutOfOrderObserverTicks_FailClosed()
    {
        Fixture fixture = CreateFixture();
        foreach ((string name, ulong tick) in new[] { ("skipped", 109UL), ("duplicate", 107UL), ("out-of-order", 106UL) })
        {
            List<NPCFootMotionContinuitySample> samples = Clone(fixture.Samples);
            samples[8] = samples[8] with
            {
                ObserverTick = tick
            };
            NPCFootMotionContinuityResult result = Evaluate(name, fixture with
            {
                Samples = samples
            });
            Assert.False(result.EvidenceValid);
            Assert.Empty(result.Events);
            Assert.NotEmpty(result.EvidenceFailures);
            Assert.All(result.EvidenceFailures,
                failure => Assert.Contains("observer continuity", failure, StringComparison.Ordinal));
        }
    }

    private static NPCFootMotionEvent AssertSingleEvent(NPCFootMotionContinuityResult result, LimbSide side)
    {
        Assert.True(result.EvidenceValid, result.BuildReport());
        NPCFootMotionEvent item = Assert.Single(result.Events);
        Assert.Equal(side, item.Side);
        Assert.NotEmpty(item.Points);
        return item;
    }

    private static void AssertSignal(
        NPCFootSignalEvidence? signal,
        ulong triggerTick,
        ulong peakTick,
        float triggerObserved,
        float peakObserved,
        NPCFootMotionBand band)
    {
        Assert.NotNull(signal);
        Assert.Equal(triggerTick, signal.TriggerTick);
        Assert.Equal(triggerObserved, signal.TriggerObserved, 4);
        Assert.Equal(peakTick, signal.PeakTick);
        Assert.Equal(peakObserved, signal.PeakObserved, 4);
        Assert.Equal(band.Low, signal.LowBand);
        Assert.Equal(band.High, signal.HighBand);
        Assert.True(signal.TriggerObserved > signal.HighBand);
    }

    private static float MaximumStep(
        IReadOnlyList<NPCFootMotionContinuitySample> samples,
        LimbSide side,
        FootPosePoint point,
        params int[] indices)
        => indices.Max(index => samples[index].PostIK.Point(side, point).Origin.DistanceTo(
            samples[index - 1].PostIK.Point(side, point).Origin));

    private static float PositionStep(
        IReadOnlyList<NPCFootMotionContinuitySample> samples,
        LimbSide side,
        FootPosePoint point,
        int index)
        => samples[index].PostIK.Point(side, point).Origin.DistanceTo(
            samples[index - 1].PostIK.Point(side, point).Origin);

    private static void AssertHealthy(NPCFootMotionContinuityResult result)
    {
        Assert.True(result.EvidenceValid, result.BuildReport());
        Assert.Empty(result.Events);
    }

    private static NPCFootMotionContinuityResult Evaluate(string name, Fixture fixture)
        => NPCFootMotionContinuityDetector.Evaluate(name, fixture.Samples, fixture.Calibration);

    private static Fixture CreateFixture(float stepMetres = 0.002f)
    {
        List<NPCFootRawClipSample> raw = [];
        List<NPCFootMotionContinuitySample> samples = [];
        for (int index = 0; index < Count; index++)
        {
            float time = index * Delta;
            Vector3 travel = new(index * stepMetres, 0f, 0f);
            FootPosePointSet left = Points(new Vector3(-0.1f, 0.05f, 0f) + travel);
            FootPosePointSet right = Points(new Vector3(0.1f, 0.05f, 0f) + travel);
            raw.Add(new("native/wf", (Count - 1) * Delta, index, time, index / (float)Count, left, right));
            NPCFootMotionPose pose = new(At(Vector3.Zero), At(new Vector3(0f, 0.9f, 0f)),
                left.Ankle, right.Ankle, left, right);
            samples.Add(new(
                (ulong)(100 + index),
                time,
                Transform3D.Identity,
                pose,
                pose,
                new("NPCTravel", "", time, 0f, index / (float)Count, 1f, "wf", "native/wf",
                    "SustainedTravel", "NPCTravel->NPCTravel", "Left", "release_l", "f2i_l"),
                _noneContact,
                _noneContact));
        }
        NPCFootMotionCalibration calibration = NPCFootMotionContinuityDetector.CreateCalibration(raw);
        Assert.True(calibration.IsValid, calibration.BuildReport());
        return new(samples, calibration);
    }

    private static Fixture WithVelocityDisabled(Fixture fixture)
    {
        var bands = fixture.Calibration.Bands.ToDictionary();
        foreach (NPCFootBaselineKey key in bands.Keys.Where(key => key.Signal == NPCFootMotionSignal.VelocityVectorChange).ToArray())
        {
            bands[key] = bands[key] with
            {
                Low = 50f,
                High = 100f
            };
        }
        return fixture with
        {
            Calibration = fixture.Calibration with
            {
                Bands = bands
            }
        };
    }

    private static NPCFootMotionBand PositionBand(Fixture fixture, LimbSide side, FootPosePoint point)
        => fixture.Calibration.Bands[new(side, point, NPCFootMotionSignal.Position)];

    private static NPCFootMotionBand VelocityBand(Fixture fixture, LimbSide side, FootPosePoint point)
        => fixture.Calibration.Bands[new(side, point, NPCFootMotionSignal.VelocityVectorChange)];

    private static void Offset(
        List<NPCFootMotionContinuitySample> samples,
        int index,
        LimbSide side,
        FootPosePoint point,
        float x,
        bool persistent)
    {
        int end = persistent ? samples.Count : index + 1;
        for (int current = index; current < end; current++)
        {
            SetOffset(samples, current, side, point, x);
        }
    }

    private static void SetOffset(
        List<NPCFootMotionContinuitySample> samples,
        int index,
        LimbSide side,
        FootPosePoint point,
        float x)
    {
        NPCFootMotionContinuitySample sample = samples[index];
        NPCFootMotionPose pose = SetPoint(sample.PostIK, side, point, sample.PostIK.Point(side, point).Origin + new Vector3(x, 0f, 0f));
        samples[index] = sample with
        {
            PostIK = pose
        };
    }

    private static void RotateSelectedPoints(
        List<NPCFootMotionContinuitySample> samples,
        int index,
        LimbSide side,
        IReadOnlyList<FootPosePoint> selected,
        float yaw)
    {
        NPCFootMotionContinuitySample sample = samples[index];
        NPCFootMotionPose pose = sample.PostIK;
        Vector3 ankle = pose.Point(side, FootPosePoint.Ankle).Origin;
        Basis rotation = new(Vector3.Up, yaw);
        foreach (FootPosePoint point in selected)
        {
            Vector3 current = pose.Point(side, point).Origin;
            pose = SetPoint(pose, side, point, ankle + (rotation * (current - ankle)));
        }
        samples[index] = sample with
        {
            PostIK = pose
        };
    }

    private static void ApplyCoherentActorTransport(List<NPCFootMotionContinuitySample> samples)
    {
        for (int index = 0; index < samples.Count; index++)
        {
            Transform3D actor = new(new Basis(Vector3.Up, index * 0.01f), new Vector3(index * 0.02f, 0f, -index * 0.01f));
            NPCFootMotionContinuitySample sample = samples[index];
            samples[index] = sample with
            {
                ActorWorld = actor,
                PostIK = TransformPose(actor, sample.PostIK),
                PreIK = sample.PreIK is NPCFootMotionPose preIK ? TransformPose(actor, preIK) : null,
            };
        }
    }

    private static NPCFootMotionPose TransformPose(Transform3D transform, NPCFootMotionPose pose)
        => pose with
        {
            RootWorld = transform * pose.RootWorld,
            HipsWorld = transform * pose.HipsWorld,
            LeftFootWorld = transform * pose.LeftFootWorld,
            RightFootWorld = transform * pose.RightFootWorld,
            LeftPointsWorld = TransformPoints(transform, pose.LeftPointsWorld),
            RightPointsWorld = TransformPoints(transform, pose.RightPointsWorld),
        };

    private static FootPosePointSet TransformPoints(Transform3D transform, FootPosePointSet points)
        => new(transform * points.Ankle, transform * points.HeelSole, transform * points.Midsole,
            transform * points.Ball, transform * points.ToeTip);

    private static NPCFootMotionPose SetPoint(NPCFootMotionPose pose, LimbSide side, FootPosePoint point, Vector3 position)
    {
        FootPosePointSet points = side == LimbSide.Left ? pose.LeftPointsWorld : pose.RightPointsWorld;
        Transform3D changed = new(points.Get(point).Basis, position);
        points = point switch
        {
            FootPosePoint.Ankle => points with { Ankle = changed },
            FootPosePoint.HeelSole => points with { HeelSole = changed },
            FootPosePoint.Midsole => points with { Midsole = changed },
            FootPosePoint.Ball => points with { Ball = changed },
            FootPosePoint.ToeTip => points with { ToeTip = changed },
            _ => throw new ArgumentOutOfRangeException(nameof(point), point, null),
        };
        return side == LimbSide.Left ? pose with
        {
            LeftPointsWorld = points
        } : pose with
        {
            RightPointsWorld = points
        };
    }

    private static FootPosePointSet Points(Vector3 ankle)
        => new(
            At(ankle),
            At(ankle + new Vector3(0f, -0.04f, -0.07f)),
            At(ankle + new Vector3(0f, -0.04f, 0.05f)),
            At(ankle + new Vector3(0f, -0.02f, 0.12f)),
            At(ankle + new Vector3(0f, -0.02f, 0.20f)));

    private static List<NPCFootMotionContinuitySample> Clone(IReadOnlyList<NPCFootMotionContinuitySample> samples) => [.. samples];
    private static Transform3D At(Vector3 position) => new(Basis.Identity, position);

    private static readonly NPCFootContactEvidence _noneContact = new(
        ContactLockLifecycle.None, "none", false, Transform3D.Identity, false, false);

    private sealed record Fixture(List<NPCFootMotionContinuitySample> Samples, NPCFootMotionCalibration Calibration);
}
