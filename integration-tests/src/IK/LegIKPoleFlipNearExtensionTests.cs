using AlleyCat.IK;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Regression coverage for ANIM-002: the knee pole must remain in front of the
/// leg near full forward extension, never snapping to the back of the leg.
/// </summary>
public sealed class LegIKPoleFlipNearExtensionTests
{
    private const string VerificationScenePath = "res://tests/ik/leg_feet_ik_test.tscn";
    private const string LeftFootTargetPath = "Markers/LeftFootTarget";
    private const string LeftPoleTargetPath = "Markers/LeftKneePoleTarget";
    private const string LeftLegIKControllerPath = "Subject/Female/Female/GeneralSkeleton/LeftLegIKController";
    private const string HipsHarnessPath = "Subject/Female/Female/GeneralSkeleton/HipsOverrideHarness";
    private const string HipsPoseMarkersPath = "Markers/HipsOverridePoses";

    private const float MinimumForwardSignedOffset = 0.05f;
    private const float MaximumSideToForwardRatio = 0.35f;
    private const float MinimumPoleContinuityDot = 0.8f;

    /// <summary>
    /// Drives the left leg into a forward-extended, near-straight pose with the
    /// lower-leg bone placed behind the hip-foot midline (the ambiguous regime
    /// that reproduces the knee-pole flip) and asserts the resolved pole stays
    /// in front of the leg across the sweep.
    /// </summary>
    /// <remarks>
    /// The skeleton modifier pipeline does not auto-invoke scene-placed C#
    /// <see cref="SkeletonModifier3D"/> instances in this headless integration
    /// harness, so the resolver is exercised directly via the public
    /// <c>_ProcessModificationWithDelta</c> entry point after the bones are
    /// positioned. This drives the exact code path fixed for ANIM-002.
    /// </remarks>
    [Headless]
    [Fact]
    public async Task LegIK_Pole_RemainsForwardOfLeg_NearExtension()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(VerificationScenePath));
        Assert.Equal(Error.Ok, changeSceneError);
        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected verification scene to become current scene.");

        Node3D leftFootTarget = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(LeftFootTargetPath), exactMatch: false);
        Node3D leftPoleTarget = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(LeftPoleTargetPath), exactMatch: false);
        LegIKController legIkController = Assert.IsType<LegIKController>(
            sceneRoot.GetNodeOrNull(LeftLegIKControllerPath), exactMatch: false);

        Node3D hipsPoseMarkers = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(HipsPoseMarkersPath), exactMatch: false);
        Node3D hipsNeutral = RequireNode3D(hipsPoseMarkers, "HipsNeutral");
        CopyTransformModifier3D hipsModifier = Assert.IsType<CopyTransformModifier3D>(
            sceneRoot.GetNodeOrNull(HipsHarnessPath), exactMatch: false);
        hipsModifier.Set("settings/0/reference_node", hipsNeutral.GetPath());

        Skeleton3D skeleton = FindFirstSkeleton(sceneRoot)
            ?? throw new Xunit.Sdk.XunitException("Expected at least one Skeleton3D in verification scene.");
        int upperLegIdx = RequireBone(skeleton, "LeftUpperLeg");
        int lowerLegIdx = RequireBone(skeleton, "LeftLowerLeg");
        int footIdx = RequireBone(skeleton, "LeftFoot");

        Vector3 upperLegWorld = BoneWorldPosition(skeleton, upperLegIdx);
        Vector3 skeletonUp = skeleton.GlobalTransform.Basis.Orthonormalized().Column1.Normalized();
        Vector3 avatarForward = (skeleton.GlobalTransform.Basis.Orthonormalized() * Vector3.Back).Normalized();
        Vector3 horizontalForward = new Vector3(avatarForward.X, 0.0f, avatarForward.Z).Normalized();
        Vector3 horizontalSide = skeletonUp.Cross(horizontalForward).Normalized();

        Transform3D lowerLegRestLocal = skeleton.GetBoneGlobalRest(lowerLegIdx);
        Transform3D footRestLocal = skeleton.GetBoneGlobalRest(footIdx);
        Transform3D skeletonGlobalInv = skeleton.GlobalTransform.AffineInverse();

        sceneRoot.FindChild("AnimationTree", owned: true, recursive: true)?.Set("active", false);
        await WaitForFramesAsync(sceneTree, 2);

        Vector3 previousPoleDirection;
        const int extensionSteps = 3;
        const int magnitudeSteps = 3;
        Basis[] footTargetBases =
        [
            // Sideways foot-forward orientation reproduces the later +preferredForward SIDE spike:
            // the foot-local forward projection is valid but anatomically wrong for a humanoid
            // knee pole near extension, so the resolver must prefer the stable avatar-forward frame.
            new Basis(-horizontalForward, skeletonUp, horizontalSide),
            // Baseline forward-foot orientation catches the original sign-preserving BACK spike.
            new Basis(horizontalSide, skeletonUp, horizontalForward),
        ];

        for (int basisIndex = 0; basisIndex < footTargetBases.Length; basisIndex++)
        {
            Basis footTargetBasis = footTargetBases[basisIndex];

            for (int e = 0; e < extensionSteps; e++)
            {
                float extension = 0.4f + (0.2f * e);
                Vector3 footTargetWorld = upperLegWorld + (horizontalForward * extension) + (skeletonUp * -0.4f);
                leftFootTarget.GlobalBasis = footTargetBasis;
                leftFootTarget.GlobalPosition = footTargetWorld;
                Vector3 footTargetLocal = skeletonGlobalInv * footTargetWorld;
                skeleton.SetBoneGlobalPose(footIdx, new Transform3D(footRestLocal.Basis, footTargetLocal));
                previousPoleDirection = Vector3.Zero;

                for (int m = 0; m < magnitudeSteps; m++)
                {
                    float magnitude = 0.10f + (0.10f * m);
                    Vector3 sharedFootMidpointWorld = (upperLegWorld + footTargetWorld) * 0.5f;
                    Vector3 injectedDirection = basisIndex == 0
                        ? ComputeProjectedDirection(skeletonUp, footTargetWorld - upperLegWorld)
                        : -(horizontalForward + skeletonUp).Normalized();
                    Vector3 injectedWorld = sharedFootMidpointWorld + (injectedDirection * magnitude);
                    Vector3 injectedLocal = skeletonGlobalInv * injectedWorld;
                    skeleton.SetBoneGlobalPose(lowerLegIdx, new Transform3D(lowerLegRestLocal.Basis, injectedLocal));

                    legIkController._ProcessModificationWithDelta(0.016);

                    // Animated and target foot positions are aligned in this fixture, making IK-003 o2 and
                    // desired-foot o1 the same point. The assertion therefore verifies pole direction without
                    // depending on the runtime-selected placement origin.
                    Vector3 midpointToPole = leftPoleTarget.GlobalPosition - sharedFootMidpointWorld;
                    float signedForwardOffset = midpointToPole.Dot(horizontalForward);
                    float signedSideOffset = midpointToPole.Dot(horizontalSide);

                    if (basisIndex == 0)
                    {
                        Assert.True(
                            Mathf.Abs(signedSideOffset) <= signedForwardOffset * MaximumSideToForwardRatio,
                            "Left knee pole should not resolve into a side spike near forward extension. " +
                            $"extension={extension:F2} magnitude={magnitude:F2} signedForwardOffset={signedForwardOffset:F4} " +
                            $"signedSideOffset={signedSideOffset:F4} maxSideRatio={MaximumSideToForwardRatio:F2}.");
                    }

                    Assert.True(
                        signedForwardOffset >= MinimumForwardSignedOffset,
                        "Left knee pole should remain in front of the leg near extension (no flip to back). " +
                        $"extension={extension:F2} magnitude={magnitude:F2} signedForwardOffset={signedForwardOffset:F4}.");

                    Vector3 poleDirection = midpointToPole.Normalized();
                    if (previousPoleDirection != Vector3.Zero)
                    {
                        Assert.True(
                            previousPoleDirection.Dot(poleDirection) >= MinimumPoleContinuityDot,
                            "Resolved pole direction should remain continuous across the sweep near extension. " +
                            $"extension={extension:F2} magnitude={magnitude:F2} " +
                            $"continuity={previousPoleDirection.Dot(poleDirection):F4}.");
                    }

                    previousPoleDirection = poleDirection;
                }
            }
        }
    }

    private static Vector3 ComputeProjectedDirection(Vector3 vector, Vector3 normal)
    {
        Vector3 normalisedNormal = normal.Normalized();
        Vector3 projected = vector - (vector.Dot(normalisedNormal) * normalisedNormal);

        return projected.LengthSquared() <= 1e-6f
            ? vector.Normalized()
            : projected.Normalized();
    }

    private static Vector3 BoneWorldPosition(Skeleton3D skeleton, int boneIndex) =>
        skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(boneIndex).Origin;

    private static Node3D RequireNode3D(Node parent, string childName) =>
        Assert.IsType<Node3D>(parent.GetNodeOrNull(childName), exactMatch: false);

    private static Skeleton3D? FindFirstSkeleton(Node root)
    {
        if (root is Skeleton3D skeleton)
        {
            return skeleton;
        }

        foreach (Node child in root.GetChildren())
        {
            Skeleton3D? found = FindFirstSkeleton(child);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static int RequireBone(Skeleton3D skeleton, string boneName)
    {
        int index = skeleton.FindBone(boneName);

        return index >= 0
            ? index
            : throw new Xunit.Sdk.XunitException($"Expected bone '{boneName}' to exist in skeleton.");
    }
}
