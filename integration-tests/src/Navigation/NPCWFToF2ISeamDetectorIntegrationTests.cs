using AlleyCat.IntegrationTests.Support;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>Mutation/reliability contract for the narrowly scoped wf-to-f2i seam oracle.</summary>
public sealed class NPCWFToF2ISeamDetectorIntegrationTests
{
    [Headless]
    [Fact]
    public void WFToF2ISeamDetector_CoherentMultiChannelSeam_Passes()
        => Assert.True(Evaluate(CreateSamples()).Passed, Evaluate(CreateSamples()).BuildReport());

    [Headless]
    [Fact]
    public void WFToF2ISeamDetector_RequiredMutations_FailClosed()
    {
        AssertFails(Mutate(3, sample => sample with { Release = sample.Release with { SelectedEvent = "release_r" } }), "LaneEvent");
        AssertFails(MutatePose(3, pose => pose with { LeftFootSkeletonLocal = At(0.20f, 0f, 0f), LeftFootWorld = At(0.20f, 0f, 0f) }), "LeftFoot");
        AssertFails(MutatePose(3, pose => pose with { HipsSkeletonLocal = At(0f, 1.10f, 0f), HipsWorld = At(0f, 1.10f, 0f) }), "Hips");
        AssertFails(MutatePose(3, pose => pose with { RightFootWorld = At(0.17f, 0f, 0f) }), "SupportFoot");
        List<NPCWFToF2ISeamSample> jerk = MutatePose(3, pose => pose with
        {
            RightFootSkeletonLocal = At(0.09f, 0.05f, -0.03f),
            RightFootWorld = At(0.09f, 0.05f, -0.03f),
        });
        NPCWFToF2ISeamResult jerkResult = Evaluate(jerk);
        Assert.False(jerkResult.Passed, jerkResult.BuildReport());
        Assert.Contains(jerkResult.Rejections, rejection => rejection.Reason.Contains("jerk", StringComparison.Ordinal));
        AssertFails(Mutate(2, sample => sample with { PreIK = null }), "Evidence");
        Assert.Contains(
            NPCWFToF2ISeamDetector.Evaluate("missing-authored", CreateSamples(), null).Rejections,
            rejection => rejection.Channel == "Evidence");

        List<NPCWFToF2ISeamSample> held = CreateSamples();
        for (int index = 1; index <= 3; index++)
        {
            held[index] = held[index] with
            {
                RemainingDistance = 0.318f,
                PreIK = held[0].PreIK,
                ActorWorld = held[0].ActorWorld,
                Release = held[index].Release with
                {
                    TerminalApproachActive = true,
                    CurrentGraphState = "NPCTravel",
                    CurrentAuthoritativePhase = 1f,
                    ActualTransitionTick = null,
                    ExecutionSucceeded = false,
                },
            };
        }
        AssertFails(held, "TerminalHold");
    }

    [Headless]
    [Fact]
    public void WFToF2ISeamDetector_RootOnlyMutationWouldMissFootDefect()
    {
        List<NPCWFToF2ISeamSample> samples = MutatePose(3, pose => pose with
        {
            LeftFootSkeletonLocal = At(0.20f, 0f, 0f),
            LeftFootWorld = At(0.20f, 0f, 0f),
        });
        Assert.All(samples.Zip(samples.Skip(1)), pair => Assert.True(
            pair.First.PreIK!.RootSkeletonLocal.Origin.DistanceTo(pair.Second.PreIK!.RootSkeletonLocal.Origin) <= 0.01001f));
        AssertFails(samples, "LeftFoot");
    }

    private static NPCWFToF2ISeamResult Evaluate(IReadOnlyList<NPCWFToF2ISeamSample> samples)
        => NPCWFToF2ISeamDetector.Evaluate("synthetic-wf-f2i", samples, Authored());

    private static void AssertFails(IReadOnlyList<NPCWFToF2ISeamSample> samples, string channel)
    {
        NPCWFToF2ISeamResult result = Evaluate(samples);
        Assert.False(result.Passed, result.BuildReport());
        Assert.Contains(result.Rejections, rejection => rejection.Channel == channel);
        Assert.Contains("Raw seam window", result.BuildReport(), StringComparison.Ordinal);
    }

    private static List<NPCWFToF2ISeamSample> Mutate(int index, Func<NPCWFToF2ISeamSample, NPCWFToF2ISeamSample> mutation)
    {
        List<NPCWFToF2ISeamSample> samples = CreateSamples();
        samples[index] = mutation(samples[index]);
        return samples;
    }

    private static List<NPCWFToF2ISeamSample> MutatePose(int index, Func<NPCWFToF2IPreIKPose, NPCWFToF2IPreIKPose> mutation)
        => Mutate(index, sample => sample with { PreIK = mutation(sample.PreIK!) });

    private static List<NPCWFToF2ISeamSample> CreateSamples()
    {
        List<NPCWFToF2ISeamSample> samples = [];
        for (int index = 0; index < 8; index++)
        {
            float z = -0.010f * index;
            bool transitioned = index >= 3;
            NPCWFToF2IPreIKPose pose = Pose(
                root: new(0f, 0f, z), hips: new(0f, 0.92f, z),
                left: new(-0.10f, 0.05f, 0f), right: new(0.10f + (0.004f * index), 0.05f, z));
            pose = pose with
            {
                RightFootWorld = At(0.10f, 0.05f, 0f),
            };
            samples.Add(new(
                (ulong)(100 + index), index / 60f, 0.30f - (index * 0.02f), At(0f, 0f, z), pose,
                new("Left", "release_l", "f2i_l", 0f, index == 0 ? 0.94f : 0.94f + ((index - 1) * 0.015f),
                0.94f + (index * 0.015f), 0.015f, index < 3, "Bounded",
                    transitioned && index == 3, transitioned && index == 3, transitioned && index == 3,
                    transitioned ? "NPCF2IL" : "NPCTravel", transitioned ? "NPCTravel" : "", transitioned ? 103UL : null),
                "Right", "Right"));
        }
        return samples;
    }

    private static NPCWFToF2IAuthoredStitchEvidence Authored()
        => new("wf", "release_l", "f2i_l", "release_l", Pose(Vector3.Zero, new(0f, 0.92f, 0f), new(-0.1f, 0.05f, 0f), new(0.1f, 0.05f, 0f)), Pose(Vector3.Zero, new(0f, 0.92f, 0f), new(-0.1f, 0.05f, 0f), new(0.1f, 0.05f, 0f)), "synthetic independent authored samples");

    private static NPCWFToF2IPreIKPose Pose(Vector3 root, Vector3 hips, Vector3 left, Vector3 right)
        => new(At(root), At(hips), At(left), At(right), At(root), At(hips), At(left), At(right), "diagnostic modifier index 0 before all IK modifiers");

    private static Transform3D At(float x, float y, float z) => At(new Vector3(x, y, z));
    private static Transform3D At(Vector3 position) => new(Basis.Identity, position);
}
