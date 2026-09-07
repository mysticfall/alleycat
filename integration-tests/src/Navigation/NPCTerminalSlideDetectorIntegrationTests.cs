using AlleyCat.IK;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Navigation;

#pragma warning disable CS1591

/// <summary>Pure, headless mutation contract for the diagnostic-only episode slide detector.</summary>
public sealed class NPCTerminalSlideDetectorIntegrationTests
{
    private const float Delta = 1f / 60f;

    [Headless]
    [Fact]
    public void StablePlantedPatch_DoesNotReportVisibleSlide()
    {
        NPCTerminalSlideResult result = Evaluate(CreateSamples());
        Assert.True(result.EvidenceValid, result.BuildReport());
        Assert.NotEmpty(result.Episodes);
        Assert.All(result.Episodes, episode =>
        {
            Assert.False(episode.VisibleSlide, result.BuildReport());
            Assert.Equal(NPCTerminalSlideCause.None, episode.Cause);
        });
    }

    [Headless]
    [Fact]
    public void AccumulatedSubPerFrameSlide_ExceedsEpisodeBound()
    {
        List<NPCTerminalSlideSample> samples = CreateSamples(count: 500);
        TranslatePatch(samples, 1, samples.Count - 1, index => new Vector3(index * 0.001f, 0f, 0f), translateActor: false);
        NPCTerminalSlideEpisode episode = AssertVisible(Evaluate(samples), NPCTerminalSlideCause.ContactLockTargetDriftOrFailure);
        Assert.True(episode.Points.Max(point => point.MaximumOneTickDriftMetres) < NPCTerminalSlideDetector.AuthoritativePlantedPerTickBoundMetres);
        Assert.True(episode.Points.Max(point => point.WorldPathMetres) > _calibration.ContactEpisodeBoundMetres);
    }

    [Headless]
    [Fact]
    public void AuthoredArticulationWithWorldStablePatch_PassesDespiteLargeChainRotation()
    {
        List<NPCTerminalSlideSample> samples = CreateSamples();
        for (int index = 1; index < samples.Count; index++)
        {
            float angle = index * 0.02f;
            samples[index] = samples[index] with
            {
                LeftChain = RotateChain(samples[index].LeftChain, angle),
            };
        }
        NPCTerminalSlideResult result = Evaluate(samples);
        Assert.True(result.EvidenceValid, result.BuildReport());
        Assert.All(result.Episodes, episode => Assert.False(episode.VisibleSlide, result.BuildReport()));
        Assert.True(Assert.Single(result.Episodes, episode => episode.Side == LimbSide.Left).Angles.TotalPathRadians > 0.10f);
    }

    [Headless]
    [Fact]
    public void StationaryConfirmationSyntheticCommonMode_IsDetectedWithoutLegRotationPrerequisite()
    {
        List<NPCTerminalSlideSample> samples = CreateSamples(contact: false, stationaryConfirmation: true);
        TranslatePatch(samples, 1, samples.Count - 1, index => new Vector3(0f, 0f, -index * 0.001f), translateActor: true);
        for (int index = 1; index < samples.Count; index++)
        {
            samples[index] = samples[index] with
            {
                LeftChain = RotateChain(samples[index].LeftChain, index * 0.03f),
                Movement = samples[index].Movement with
                {
                    Synthetic = new Vector3(0f, 0f, -0.001f)
                },
            };
        }
        NPCTerminalSlideEpisode episode = AssertVisible(Evaluate(samples), NPCTerminalSlideCause.StoppedPoseSyntheticSlide);
        Assert.True(episode.Points.Max(point => point.WorldNetMetres) > _calibration.StationaryMovementToleranceMetres);
        Assert.True(episode.Points.Max(point => point.ActorResidualNetMetres) < 0.00001f);
        Assert.True(episode.Angles.TotalPathRadians > 0.10f);
    }

    [Headless]
    [Fact]
    public void MissingOptionalEndpointCalibration_DoesNotSuppressContactTargetDrift()
    {
        List<NPCTerminalSlideSample> samples = CreateSamples(count: 500);
        TranslatePatch(samples, 1, samples.Count - 1, index => new Vector3(index * 0.001f, 0f, 0f), translateActor: false);
        for (int index = 1; index < samples.Count; index++)
        {
            samples[index] = samples[index] with
            {
                Movement = samples[index].Movement with
                {
                    Contact = new Vector3(0.001f, 0f, 0f)
                },
            };
        }
        NPCTerminalSlideCalibration optionalMissing = NPCTerminalSlideDetector.CreateCalibration([]);
        NPCTerminalSlideResult result = NPCTerminalSlideDetector.Evaluate("optional-missing", samples, optionalMissing);
        NPCTerminalSlideEpisode episode = AssertVisible(result, NPCTerminalSlideCause.ContactLockTargetDriftOrFailure);
        Assert.True(optionalMissing.IsValid, optionalMissing.BuildReport());
        Assert.NotEmpty(optionalMissing.Notes);
        Assert.True(episode.ContactSum.Length() > _calibration.ContactEpisodeBoundMetres);
        Assert.Equal(0f, episode.PreIKWorldPathMetres, 6);
        Assert.True(episode.PostMinusPreIKPathMetres > 0f);
    }

    [Headless]
    [Fact]
    public void MissingAndNonContiguousEvidence_FailsClosed()
    {
        NPCTerminalSlideResult missing = NPCTerminalSlideDetector.Evaluate("missing", null, _calibration);
        Assert.False(missing.EvidenceValid);
        Assert.Empty(missing.Episodes);

        List<NPCTerminalSlideSample> samples = CreateSamples();
        samples[6] = samples[6] with
        {
            Motion = samples[6].Motion with
            {
                ObserverTick = 999,
                SourceTick = 0
            },
        };
        NPCTerminalSlideResult discontinuous = Evaluate(samples);
        Assert.False(discontinuous.EvidenceValid);
        Assert.Empty(discontinuous.Episodes);
        Assert.Contains(discontinuous.EvidenceFailures, failure => failure.Contains("non-contiguous", StringComparison.Ordinal));
    }

    private static NPCTerminalSlideEpisode AssertVisible(NPCTerminalSlideResult result, NPCTerminalSlideCause cause)
    {
        Assert.True(result.EvidenceValid, result.BuildReport());
        Assert.Contains(result.Episodes, item => item.Side == LimbSide.Left && item.VisibleSlide && item.Cause == cause);
        return result.Episodes.First(item => item.Side == LimbSide.Left && item.VisibleSlide && item.Cause == cause);
    }

    private static NPCTerminalSlideResult Evaluate(IReadOnlyList<NPCTerminalSlideSample> samples)
        => NPCTerminalSlideDetector.Evaluate("mutation", samples, _calibration);

    private static List<NPCTerminalSlideSample> CreateSamples(bool contact = true, bool stationaryConfirmation = false, int count = 16)
    {
        List<NPCTerminalSlideSample> samples = [];
        for (int index = 0; index < count; index++)
        {
            FootPosePointSet left = Points(new Vector3(-0.1f, 0.05f, 0f));
            FootPosePointSet right = Points(new Vector3(0.1f, 0.05f, 0f));
            NPCFootMotionPose pose = Pose(left, right);
            ContactLockLifecycle lifecycle = contact ? ContactLockLifecycle.Support : ContactLockLifecycle.Release;
            NPCFootContactEvidence evidence = new(lifecycle, lifecycle.ToString(), contact, Transform3D.Identity, contact, contact);
            NPCFootGraphEvidence graph = contact || stationaryConfirmation
                ? new("NPCF2IL", "", index * Delta, 0f, 0.85f + (index * 0.005f), 1f, "f2i_l", "native/f2i_l", "Exit", "none", "Left", "release_l", "f2i_l")
                : new("NPCIdle", "NPCF2IL", index * Delta, 0f, 1f, 1f, "idle", "native/idle", "Complete", "NPCF2IL->NPCIdle", "Left", "release_l", "f2i_l", "native/f2i_l", index / 15f, 0.25f);
            NPCFootMotionContinuitySample motion = new((ulong)(100 + index), index * Delta, Transform3D.Identity,
                pose, pose, graph, evidence, evidence, (ulong)(500 + index));
            NPCFootLegChainPose leftChain = Chain(new Vector3(-0.1f, 0f, 0f));
            NPCFootLegChainPose rightChain = Chain(new Vector3(0.1f, 0f, 0f));
            if (stationaryConfirmation)
            {
                motion = motion with
                {
                    Graph = motion.Graph with
                    {
                        NormalisedPhase = 1f,
                        StateClock = 0.8333333f
                    }
                };
            }
            samples.Add(new(motion, leftChain, rightChain, default, new Vector3(0f, 0f, -1f),
                Vector3.Forward, stationaryConfirmation ? 0.8333333f : index * Delta, 0.8333333f));
        }
        return samples;
    }

    private static void TranslatePatch(List<NPCTerminalSlideSample> samples, int start, int end,
        Func<int, Vector3> offset, bool translateActor)
    {
        for (int index = start; index <= end; index++)
        {
            NPCTerminalSlideSample sample = samples[index];
            Transform3D translation = new(Basis.Identity, offset(index));
            NPCFootMotionPose post = TransformPose(translation, sample.Motion.PostIK);
            samples[index] = sample with
            {
                Motion = sample.Motion with
                {
                    ActorWorld = translateActor ? translation : sample.Motion.ActorWorld,
                    PostIK = post,
                },
            };
        }
    }

    private static NPCFootLegChainPose RotateChain(NPCFootLegChainPose chain, float angle)
    {
        Basis rotation = new(Vector3.Right, angle);
        Transform3D Rotate(Transform3D value)
        {
            return new(rotation * value.Basis, value.Origin);
        }

        return chain with
        {
            LowerLeg = Rotate(chain.LowerLeg),
            Foot = Rotate(chain.Foot),
            Toe = Rotate(chain.Toe)
        };
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

    private static NPCFootMotionPose Pose(FootPosePointSet left, FootPosePointSet right)
        => new(At(Vector3.Zero), At(new Vector3(0f, 0.9f, 0f)), left.Ankle, right.Ankle, left, right);

    private static NPCFootLegChainPose Chain(Vector3 x)
        => new(At(new Vector3(0f, 0.9f, 0f)), At(x + new Vector3(0f, 0.75f, 0f)),
            At(x + new Vector3(0f, 0.4f, 0f)), At(x + new Vector3(0f, 0.08f, 0f)), At(x));

    private static FootPosePointSet Points(Vector3 ankle)
        => new(At(ankle), At(ankle + new Vector3(0f, -0.04f, -0.07f)),
            At(ankle + new Vector3(0f, -0.04f, 0.05f)), At(ankle + new Vector3(0f, -0.02f, 0.12f)),
            At(ankle + new Vector3(0f, -0.02f, 0.20f)));

    private static Transform3D At(Vector3 position) => new(Basis.Identity, position);

    private static readonly NPCTerminalSlideCalibration _calibration = new(
        0.008f, 0.45f, 0.0001f, []);
}
