using AlleyCat.Rigging;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Rigging;

/// <summary>
/// Validates RIG-002 at the smallest authored boundary: each imported reference render rig and its sole forearm
/// modifier. The fixture deliberately contains no player, IK target, XR, Game, or Mirror Room dependencies.
/// </summary>
public sealed class MinimalForearmTwistFixtureIntegrationTests
{
    private const string FixturePath = "res://tests/rigging/forearm_twist/minimal_neutral_recovery.tscn";
    // Local recovery bound. The imported helper rest basis round-trips the glTF stage with a
    // slight non-orthonormal component, so extracting its rotation polar-decomposes ~7e-4 rad away from
    // the quaternion the writer stores through the pose path; 1e-3 bounds that extraction noise while
    // still failing any genuine branch or half-turn retention.
    private const float RotationToleranceRadians = 1.0e-3f;
    private const float CanonicalInputToleranceRadians = 0.002f;
    private const float ActualAuthoredTwistWeight = 0.50f;

    /// <summary>Proves completed bilateral source turns cannot retain a helper-pose branch.</summary>
    [Fact]
    public async Task ImportedReferenceRigs_CompletedPositiveAndNegativeTurnsRecoverNeutralHelperAndSkin()
    {
        SceneTree sceneTree = GetSceneTree();
        using Node fixtureRoot = LoadFixture();
        sceneTree.Root.AddChild(fixtureRoot);
        await WaitForNextFrameAsync(sceneTree);

        try
        {
            AssertFixtureBoundary(fixtureRoot);

            foreach (FixtureRig rig in ResolveRigs(fixtureRoot))
            {
                Assert.Equal(ActualAuthoredTwistWeight, rig.Modifier.TwistWeight);
                rig.Modifier.ValidateProductionTopology(rig.Skeleton);

                foreach (LimbSide side in new[] { LimbSide.Left, LimbSide.Right })
                {
                    RigSide rigSide = rig.ResolveSide(side);
                    AssertNeutral(rig, rigSide, "initial neutral");

                    foreach (float direction in new[] { 1.0f, -1.0f })
                    {
                        ApplyAndProcessCanonicalHandPose(rig, rigSide, direction * Mathf.Pi);
                        AssertCanonicalInputsAreUntouched(rig, rigSide);
                        Assert.True(
                            rig.Skeleton.GetBonePoseRotation(rigSide.Helper).AngleTo(rigSide.HelperRestRotation) > 0.05f,
                            $"{rig.Sex}/{side} {direction:+0;-0} half-turn did not reach its helper.");

                        for (int degrees = 0; degrees < 360; degrees += 10)
                        {
                            ApplyAndProcessCanonicalHandPose(rig, rigSide, direction * Mathf.DegToRad(degrees));
                            AssertCanonicalInputsAreUntouched(rig, rigSide);
                        }

                        ApplyAndProcessCanonicalHandPose(rig, rigSide, 0.0f);
                        AssertNeutral(rig, rigSide, $"{direction:+0;-0} full-turn recovery");

                        // A retained ±π continuous branch would reappear on ordinary subsequent neutral frames.
                        for (int neutralFrame = 0; neutralFrame < 3; neutralFrame++)
                        {
                            ApplyAndProcessCanonicalHandPose(rig, rigSide, 0.0f);
                            AssertNeutral(rig, rigSide, $"{direction:+0;-0} repeated neutral {neutralFrame}");
                        }
                    }
                }
            }
        }
        finally
        {
            fixtureRoot.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static Node LoadFixture()
        => Assert.IsType<PackedScene>(ResourceLoader.Load(FixturePath), exactMatch: false).Instantiate();

    private static IEnumerable<FixtureRig> ResolveRigs(Node fixtureRoot)
    {
        foreach ((string sex, string subjectPath) in new[] { ("female", "Female"), ("male", "Male") })
        {
            Node subject = fixtureRoot.GetNode(subjectPath);
            Skeleton3D skeleton = Assert.Single(subject.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>());
            ForearmTwistModifier modifier = Assert.IsType<ForearmTwistModifier>(
                skeleton.GetNode("ForearmTwistModifier"),
                exactMatch: false);
            yield return new FixtureRig(sex, skeleton, modifier);
        }
    }

    private static void AssertFixtureBoundary(Node fixtureRoot)
    {
        Assert.Equal("MinimalNeutralRecovery", fixtureRoot.Name.ToString());
        Assert.Equal(2, fixtureRoot.GetChildCount());
        Assert.DoesNotContain(
            fixtureRoot.FindChildren("*", string.Empty, true, false),
            node => node.Name.ToString().Contains("target", StringComparison.OrdinalIgnoreCase)
                || node.Name.ToString().Contains("ik", StringComparison.OrdinalIgnoreCase)
                || node.GetType().FullName?.Contains("PlayerVRIK", StringComparison.Ordinal) == true
                || node.GetType().FullName?.Contains("CharacterIK", StringComparison.Ordinal) == true
                || node.GetType().FullName?.Contains("Game", StringComparison.Ordinal) == true
                || node.GetType().FullName?.Contains("XR", StringComparison.Ordinal) == true);

        foreach (FixtureRig rig in ResolveRigs(fixtureRoot))
        {
            _ = Assert.Single(rig.Skeleton.GetChildren().OfType<ForearmTwistModifier>());
            Assert.DoesNotContain(rig.Skeleton.GetChildren(), node => node is SkeletonModifier3D && node != rig.Modifier);
            Assert.Same(rig.Skeleton, rig.Modifier.GetParent());
            Assert.Equal(ActualAuthoredTwistWeight, rig.Modifier.TwistWeight);
            Assert.Equal("LeftLowerArm", rig.Modifier.LeftLowerArmBoneName);
            Assert.Equal("RightLowerArm", rig.Modifier.RightLowerArmBoneName);
            Assert.Equal("LeftHand", rig.Modifier.LeftHandBoneName);
            Assert.Equal("RightHand", rig.Modifier.RightHandBoneName);

            foreach (LimbSide side in new[] { LimbSide.Left, LimbSide.Right })
            {
                RigSide rigSide = rig.ResolveSide(side);
                // RIG-002 TR1 authored chain: LowerArm -> ForearmTwist -> Hand, with exactly one
                // helper bone per side; the twist helper's only child is the hand.
                Assert.Equal(rigSide.LowerArm, rig.Skeleton.GetBoneParent(rigSide.Helper));
                Assert.Equal(rigSide.Helper, rig.Skeleton.GetBoneParent(rigSide.Hand));
                int[] helperChildren = [.. Enumerable.Range(0, rig.Skeleton.GetBoneCount()).Where(bone => rig.Skeleton.GetBoneParent(bone) == rigSide.Helper)];
                Assert.Equal([rigSide.Hand], helperChildren);
            }

        }
    }

    private static void ApplyAndProcessCanonicalHandPose(FixtureRig rig, RigSide side, float rollRadians)
    {
        // Coordinate convention: this writes Skeleton3D global-pose space. The source rotation is rest-relative to
        // LowerArm around the imported lower-arm-to-wrist axis; no world/XR/target frame and no helper pose is used.
        Transform3D lowerPose = rig.Skeleton.GetBoneGlobalPose(side.LowerArm);
        Transform3D lowerRest = rig.Skeleton.GetBoneGlobalRest(side.LowerArm);
        Transform3D handRest = rig.Skeleton.GetBoneGlobalRest(side.Hand);
        Transform3D handRelativeToLowerRest = lowerRest.AffineInverse() * handRest;
        Vector3 lowerArmRestAxis = (lowerRest.Basis.Inverse() * (handRest.Origin - lowerRest.Origin)).Normalized();
        Transform3D suppliedHandPose = lowerPose * new Transform3D(
            new Basis(lowerArmRestAxis, rollRadians) * handRelativeToLowerRest.Basis,
            handRelativeToLowerRest.Origin);

        rig.Skeleton.SetBoneGlobalPose(side.Hand, suppliedHandPose);
        side.SuppliedHandRotation = suppliedHandPose.Basis.GetRotationQuaternion();
        side.SuppliedLowerArmRotation = lowerPose.Basis.GetRotationQuaternion();
        ulong token = ForearmTwistModificationPass.Begin(rig.Skeleton);
        rig.Modifier.SubmitAuthoritySample(
            side.Side,
            new ForearmTwistAuthoritySample(
                suppliedHandPose,
                0.0f,
                true,
                ForearmTwistAuthorityKind.Animation,
                side.Side == LimbSide.Left ? 1UL : 2UL,
                1,
                token));
        rig.Modifier._ProcessModificationWithDelta(0.0d);
    }

    private static void AssertCanonicalInputsAreUntouched(FixtureRig rig, RigSide side)
    {
        Assert.InRange(
            side.SuppliedLowerArmRotation.AngleTo(rig.Skeleton.GetBoneGlobalPose(side.LowerArm).Basis.GetRotationQuaternion()),
            0.0f,
            CanonicalInputToleranceRadians);
        Assert.InRange(
            side.SuppliedHandRotation.AngleTo(rig.Skeleton.GetBoneGlobalPose(side.Hand).Basis.GetRotationQuaternion()),
            0.0f,
            CanonicalInputToleranceRadians);
    }

    private static void AssertNeutral(FixtureRig rig, RigSide side, string checkpoint)
    {
        AssertRotationApproximately(side.HelperRestRotation, rig.Skeleton.GetBonePoseRotation(side.Helper));
        Assert.InRange(
            rig.Skeleton.GetBonePoseRotation(side.Helper).AngleTo(side.HelperRestRotation),
            0.0f,
            RotationToleranceRadians);
        // Global rest-versus-pose comparison: on the RIG-002 helper chain the engine's float32
        // pose-path composition of the duplicated helper rest segments differs from its stored global
        // rest path by the same extraction noise the local bound above documents.
        AssertRotationApproximately(
            rig.Skeleton.GetBoneGlobalRest(side.Hand).Basis.GetRotationQuaternion(),
            rig.Skeleton.GetBoneGlobalPose(side.Hand).Basis.GetRotationQuaternion());
        AssertRotationApproximately(
            rig.Skeleton.GetBoneGlobalRest(side.LowerArm).Basis.GetRotationQuaternion(),
            rig.Skeleton.GetBoneGlobalPose(side.LowerArm).Basis.GetRotationQuaternion());
        Assert.True(
            rig.Skeleton.GetBonePoseRotation(side.Helper).AngleTo(side.HelperRestRotation) < Mathf.Pi * 0.5f,
            $"{rig.Sex}/{side.Side} retained a ±π helper branch at {checkpoint}.");
    }

    private static void AssertRotationApproximately(Quaternion expected, Quaternion actual)
        => Assert.InRange(expected.Normalized().AngleTo(actual.Normalized()), 0.0f, RotationToleranceRadians);

    private sealed class FixtureRig(string sex, Skeleton3D skeleton, ForearmTwistModifier modifier)
    {
        public string Sex { get; } = sex;
        public Skeleton3D Skeleton { get; } = skeleton;
        public ForearmTwistModifier Modifier { get; } = modifier;

        public RigSide ResolveSide(LimbSide side)
        {
            string prefix = side.ToString();
            int lowerArm = Skeleton.FindBone($"{prefix}LowerArm");
            int hand = Skeleton.FindBone($"{prefix}Hand");
            int helper = Skeleton.FindBone($"{prefix}ForearmTwist");
            Assert.True(lowerArm >= 0 && hand >= 0 && helper >= 0, $"{Sex}/{side} canonical bones are incomplete.");
            return new RigSide(side, lowerArm, hand, helper, Skeleton.GetBoneRest(helper).Basis.GetRotationQuaternion());
        }
    }

    private sealed class RigSide(LimbSide side, int lowerArm, int hand, int helper, Quaternion helperRestRotation)
    {
        public LimbSide Side { get; } = side;
        public int LowerArm { get; } = lowerArm;
        public int Hand { get; } = hand;
        public int Helper { get; } = helper;
        public Quaternion HelperRestRotation { get; } = helperRestRotation;
        public Quaternion SuppliedHandRotation { get; set; } = Quaternion.Identity;
        public Quaternion SuppliedLowerArmRotation { get; set; } = Quaternion.Identity;
    }
}
