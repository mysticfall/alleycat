using AlleyCat.Common;
using AlleyCat.IK.Pose;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK.Pose;

/// <summary>
/// Integration coverage for the default hip-limit seam in <see cref="PoseState"/>.
/// </summary>
public sealed partial class PoseStateHipLimitIntegrationTests
{
    private const float PositionTolerance = 1e-4f;

    /// <summary>
    /// Unresolved hip context must fall back to a neutral reference without reading animation data.
    /// </summary>
    [Headless]
    [Fact]
    public void BuildHipLimitFrame_WithoutResolvedHip_UsesNeutralReference()
    {
        TestPoseState state = new();

        HipLimitFrame frame = state.BuildHipLimitFrame(new PoseStateContext());

        Assert.Equal(Vector3.Zero, frame.ReferenceHipLocalPosition);
    }

    /// <summary>
    /// The base seam must use the hip rest/reference position instead of the animated pose.
    /// </summary>
    [Headless]
    [Fact]
    public async Task BuildHipLimitFrame_UsesSkeletonLocalHipRestPositionInsteadOfAnimatedPose()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "PoseStateHipLimitFixture",
        };

        Skeleton3D skeleton = new()
        {
            Name = "Skeleton",
        };
        int spineBoneIndex = skeleton.AddBone("Spine");
        int hipBoneIndex = skeleton.AddBone("Hips");
        skeleton.SetBoneParent(hipBoneIndex, spineBoneIndex);

        Vector3 spineRest = new(-0.20f, 0.40f, 0.18f);
        Vector3 hipRest = new(0.15f, 0.92f, -0.08f);
        Vector3 expectedHipSkeletonLocalRest = spineRest + hipRest;
        Vector3 animatedHipPose = new(-0.45f, 0.31f, 0.67f);
        skeleton.SetBoneRest(spineBoneIndex, new Transform3D(Basis.Identity, spineRest));
        skeleton.SetBoneRest(hipBoneIndex, new Transform3D(Basis.Identity, hipRest));
        skeleton.SetBonePosePosition(hipBoneIndex, animatedHipPose);
        root.AddChild(skeleton);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            TestPoseState state = new();
            HipLimitFrame frame = state.BuildHipLimitFrame(
                new PoseStateContext
                {
                    Skeleton = skeleton,
                    HipBoneIndex = hipBoneIndex,
                });

            Assert.Equal(expectedHipSkeletonLocalRest, frame.ReferenceHipLocalPosition);
            Assert.NotEqual(animatedHipPose, frame.ReferenceHipLocalPosition);
            Assert.NotEqual(hipRest, frame.ReferenceHipLocalPosition);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Standing hip-limit framing must shift from the hip rest expressed in skeleton-local space,
    /// using avatar semantic forward resolved into the rotated rig's local axes.
    /// </summary>
    [Headless]
    [Fact]
    public async Task StandingBuildHipLimitFrame_UsesSkeletonLocalRestSpaceForReferenceShift()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "StandingPoseStateHipLimitFixture",
        };

        Skeleton3D skeleton = new()
        {
            Name = "Skeleton",
        };
        int spineBoneIndex = skeleton.AddBone("Spine");
        int hipBoneIndex = skeleton.AddBone("Hips");
        skeleton.SetBoneParent(hipBoneIndex, spineBoneIndex);

        Vector3 spineRest = new(0.25f, 0.35f, -0.12f);
        Vector3 hipRest = new(0.05f, 0.55f, 0.09f);
        skeleton.SetBoneRest(spineBoneIndex, new Transform3D(Basis.Identity, spineRest));
        skeleton.SetBoneRest(hipBoneIndex, new Transform3D(Basis.Identity, hipRest));
        root.AddChild(skeleton);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            StandingPoseState state = new()
            {
                UprightHipOffsetLimits = new OffsetLimits3D(),
                FullCrouchReferenceHipHeightRatio = 0.35f,
                FullCrouchReferenceForwardShiftRatio = 0.05f,
            };

            HipLimitFrame frame = state.BuildHipLimitFrame(
                new PoseStateContext
                {
                    Skeleton = skeleton,
                    HipBoneIndex = hipBoneIndex,
                    RestHeadHeight = 2.0f,
                    HeadTargetRestTransform = new Transform3D(Basis.Identity, new Vector3(0f, 2.0f, 0f)),
                    HeadTargetTransform = new Transform3D(Basis.Identity, new Vector3(0f, 1.0f, 0f)),
                });

            Assert.True(
                frame.ReferenceHipLocalPosition.IsEqualApprox(new Vector3(0.30f, 0.70f, 0.07f)),
                $"Expected standing reference shift in skeleton-local Y+/avatar-forward axes, got {frame.ReferenceHipLocalPosition}.");
            Assert.NotEqual(new Vector3(0.05f, 0.35f, 0.15f), frame.ReferenceHipLocalPosition);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// An out-of-range hip bone index must use the unresolved-context fallback frame instead of
    /// attempting a rest-pose lookup.
    /// </summary>
    [Headless]
    [Fact]
    public async Task StandingBuildHipLimitFrame_OutOfRangeHipIndex_UsesFallbackFrame()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "StandingPoseStateOutOfRangeHipFixture",
        };

        Skeleton3D skeleton = new()
        {
            Name = "Skeleton",
        };
        int hipBoneIndex = skeleton.AddBone("Hips");
        skeleton.SetBoneRest(hipBoneIndex, new Transform3D(Basis.Identity, new Vector3(0.15f, 0.92f, -0.08f)));
        root.AddChild(skeleton);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            StandingPoseState state = new()
            {
                UprightHipOffsetLimits = new OffsetLimits3D
                {
                    Up = 0.15f,
                    Down = 0.55f,
                    Left = 0.2f,
                    Right = 0.2f,
                    Forward = 0.25f,
                    Back = 0.15f,
                    HasDownLimit = false,
                },
                CrouchedHipOffsetLimits = new OffsetLimits3D
                {
                    Up = 0.05f,
                    Down = 0.03f,
                    Left = 0.06f,
                    Right = 0.06f,
                    Forward = 0.08f,
                    Back = 0.05f,
                    HasUpLimit = false,
                },
                FullCrouchReferenceHipHeightRatio = 0.35f,
            };

            PoseStateContext context = new()
            {
                Skeleton = skeleton,
                HipBoneIndex = skeleton.GetBoneCount(),
                RestHeadHeight = 2.0f,
                HeadTargetRestTransform = new Transform3D(Basis.Identity, new Vector3(0f, 2.0f, 0f)),
                HeadTargetTransform = new Transform3D(Basis.Identity, new Vector3(0f, 1.0f, 0f)),
            };

            HipLimitFrame frame = state.BuildHipLimitFrame(context);

            Assert.Equal(Vector3.Zero, frame.ReferenceHipLocalPosition);
            Assert.Null(frame.AbsoluteBounds);
            Assert.True(frame.OffsetEnvelope.HasValue, "Expected unresolved standing context to keep a fallback envelope.");

            HipLimitEnvelope envelope = frame.OffsetEnvelope.Value;
            Assert.Null(envelope.Up);
            Assert.InRange(envelope.Down!.Value, 0.03f - PositionTolerance, 0.03f + PositionTolerance);
            Assert.InRange(envelope.Left!.Value, 0.06f - PositionTolerance, 0.06f + PositionTolerance);
            Assert.InRange(envelope.Right!.Value, 0.06f - PositionTolerance, 0.06f + PositionTolerance);
            Assert.InRange(envelope.Forward!.Value, 0.05f - PositionTolerance, 0.05f + PositionTolerance);
            Assert.InRange(envelope.Back!.Value, 0.08f - PositionTolerance, 0.08f + PositionTolerance);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Standing authored directional limits must remain avatar-relative even though this rig's
    /// skeleton-local X/Z signs are flipped by the imported container yaw.
    /// </summary>
    [Headless]
    [Fact]
    public async Task StandingBuildHipLimitFrame_AvatarRelativeDirectionalLimitsClampAgainstRotatedSkeletonAxes()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "StandingDirectionalSemanticsFixture",
        };

        Skeleton3D skeleton = CreateStandingHipLimitSkeleton();
        int hipBoneIndex = RequireHipBoneIndex(skeleton);
        root.AddChild(skeleton);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            StandingPoseState state = new()
            {
                UprightHipOffsetLimits = CreateAsymmetricDirectionalLimits(),
                FullCrouchReferenceHipHeightRatio = 0.21f,
                FullCrouchReferenceForwardShiftRatio = 0.04f,
            };
            PoseStateContext context = CreateContext(
                skeleton,
                hipBoneIndex,
                restHeadY: 1.65f,
                currentHeadY: 1.65f,
                restHeadHeight: 1.6f);

            HipLimitFrame frame = state.BuildHipLimitFrame(context);
            Vector3 reference = frame.ReferenceHipLocalPosition;

            AssertAppliedHipPosition(frame, reference + new Vector3(0.40f, 0f, 0f), reference + new Vector3(0.16f, 0f, 0f), context.RestHeadHeight);
            AssertAppliedHipPosition(frame, reference + new Vector3(-0.40f, 0f, 0f), reference + new Vector3(-0.40f, 0f, 0f), context.RestHeadHeight);
            AssertAppliedHipPosition(frame, reference + new Vector3(0f, 0f, 0.40f), reference + new Vector3(0f, 0f, 0.192f), context.RestHeadHeight);
            AssertAppliedHipPosition(frame, reference + new Vector3(0f, 0f, -0.40f), reference + new Vector3(0f, 0f, -0.40f), context.RestHeadHeight);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Pure crouch inside the standing envelope must not synthesise a limited-head correction or
    /// residual compensation at the standing hip-limit seam.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PoseStateMachineTick_PureCrouchWithinStandingEnvelope_DoesNotCreateArtificialCompensation()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "StandingEnvelopePureCrouchFixture",
        };

        Skeleton3D skeleton = CreateStandingHipLimitSkeleton();
        int hipBoneIndex = RequireHipBoneIndex(skeleton);
        root.AddChild(skeleton);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            StandingPoseState state = CreateStandingHipLimitState();
            PoseStateContext context = CreateContext(
                skeleton,
                hipBoneIndex,
                restHeadY: 1.65f,
                currentHeadY: 1.05f,
                restHeadHeight: 1.6f);

            PoseStateMachineTickResult result = CreatePoseStateMachine(state).Tick(context);
            Assert.True(result.HipLocalPosition.HasValue, "Expected pure-crouch tick to resolve a hip target.");
            Vector3 appliedHipLocalPosition = result.HipLocalPosition.Value;

            Assert.Null(result.LimitedHeadTargetTransform);
            Assert.True(
                result.ResidualHipOffset.Length() <= PositionTolerance,
                $"Pure crouch within the standing envelope should not leave residual compensation. Residual={result.ResidualHipOffset}.");
            Assert.True(
                appliedHipLocalPosition.IsEqualApprox(new Vector3(0f, 0.35f, 0f)),
                $"Pure crouch should preserve the unclamped standing-family hip solve. Applied={appliedHipLocalPosition}.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Near full crouch, downward hip motion must clamp against the tightened crouched floor.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PoseStateMachineTick_NearFullCrouch_ClampsAboveTightenedCrouchedFloor()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "StandingEnvelopeCrouchedFloorFixture",
        };

        Skeleton3D skeleton = CreateStandingHipLimitSkeleton();
        int hipBoneIndex = RequireHipBoneIndex(skeleton);
        root.AddChild(skeleton);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            StandingPoseState state = CreateStandingHipLimitState();
            PoseStateContext context = CreateContext(
                skeleton,
                hipBoneIndex,
                restHeadY: 1.65f,
                currentHeadY: 0.95f,
                restHeadHeight: 1.6f);

            HipLimitFrame limitFrame = state.BuildHipLimitFrame(context);
            float crouchedFloorY = limitFrame.ReferenceHipLocalPosition.Y
                - ((state.CrouchedHipOffsetLimits?.Down ?? 0f) * context.RestHeadHeight);

            PoseStateMachineTickResult result = CreatePoseStateMachine(state).Tick(context);
            Assert.True(result.HipLocalPosition.HasValue, "Expected crouched-floor tick to resolve a hip target.");
            Vector3 appliedHipLocalPosition = result.HipLocalPosition.Value;

            Assert.True(result.LimitedHeadTargetTransform.HasValue, "Crouched-floor clamping should produce a limited head target.");
            Assert.True(
                result.ResidualHipOffset.Y < -PositionTolerance,
                $"Crouched-floor clamping should leave a downward residual for later origin compensation. Residual={result.ResidualHipOffset}.");
            Assert.True(
                appliedHipLocalPosition.Y >= crouchedFloorY - PositionTolerance,
                $"Applied hip Y should stay above the tightened crouched floor. AppliedY={appliedHipLocalPosition.Y:F4}, floorY={crouchedFloorY:F4}.");
            Assert.True(
                Mathf.Abs(appliedHipLocalPosition.Y - crouchedFloorY) <= PositionTolerance,
                $"Applied hip Y should clamp to the crouched floor when the desired solve falls below it. AppliedY={appliedHipLocalPosition.Y:F4}, floorY={crouchedFloorY:F4}.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// An upright-only upward limit must remain active at full crouch and stay anchored to the rest
    /// hip reference.
    /// </summary>
    [Headless]
    [Fact]
    public async Task StandingBuildHipLimitFrame_FullCrouch_PreservesRestAnchoredUprightOnlyUpLimit()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "StandingEnvelopeUprightOnlyUpFixture",
        };

        Skeleton3D skeleton = CreateStandingHipLimitSkeleton();
        int hipBoneIndex = RequireHipBoneIndex(skeleton);
        root.AddChild(skeleton);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            StandingPoseState state = CreateStandingHipLimitState();
            PoseStateContext context = CreateContext(
                skeleton,
                hipBoneIndex,
                restHeadY: 1.65f,
                currentHeadY: 1.05f,
                restHeadHeight: 1.6f);

            HipLimitFrame frame = state.BuildHipLimitFrame(context);
            HipReconciliationTickResult result = PoseState.ApplyHipLimitFrame(
                new HipReconciliationProfileResult
                {
                    DesiredHipLocalPosition = new Vector3(0f, 1.25f, 0f),
                },
                frame,
                context.RestHeadHeight,
                Transform3D.Identity);

            Assert.True(
                Mathf.Abs(result.AppliedHipLocalPosition.Y - 1.19f) <= PositionTolerance,
                $"Expected the upright-only up clamp to stay anchored to rest at full crouch. AppliedHip={result.AppliedHipLocalPosition}.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// A crouched-only downward limit must remain active even at the upright end and stay anchored
    /// to the authored full-crouch reference.
    /// </summary>
    [Headless]
    [Fact]
    public async Task StandingBuildHipLimitFrame_Upright_PreservesFullCrouchAnchoredCrouchedOnlyDownLimit()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "StandingEnvelopeCrouchedOnlyDownFixture",
        };

        Skeleton3D skeleton = CreateStandingHipLimitSkeleton();
        int hipBoneIndex = RequireHipBoneIndex(skeleton);
        root.AddChild(skeleton);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            StandingPoseState state = CreateStandingHipLimitState();
            PoseStateContext context = CreateContext(
                skeleton,
                hipBoneIndex,
                restHeadY: 1.65f,
                currentHeadY: 1.65f,
                restHeadHeight: 1.6f);

            HipLimitFrame frame = state.BuildHipLimitFrame(context);
            HipReconciliationTickResult result = PoseState.ApplyHipLimitFrame(
                new HipReconciliationProfileResult
                {
                    DesiredHipLocalPosition = new Vector3(0f, 0.20f, 0f),
                },
                frame,
                context.RestHeadHeight,
                Transform3D.Identity);

            Assert.True(
                Mathf.Abs(result.AppliedHipLocalPosition.Y - 0.288f) <= PositionTolerance,
                $"Expected the crouched-only down clamp to stay anchored to the full-crouch reference even while upright. AppliedHip={result.AppliedHipLocalPosition}.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// The full-crouch seam must not depend on the upright-only upward side disappearing on the
    /// final standing tick.
    /// </summary>
    [Headless]
    [Fact]
    public async Task StandingBuildHipLimitFrame_FullCrouchSeam_DoesNotDropUprightOnlyUpSide()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "StandingEnvelopeFullCrouchSeamFixture",
        };

        Skeleton3D skeleton = CreateStandingHipLimitSkeleton();
        int hipBoneIndex = RequireHipBoneIndex(skeleton);
        root.AddChild(skeleton);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            StandingPoseState state = CreateStandingHipLimitState();
            HipReconciliationProfileResult profileResult = new()
            {
                DesiredHipLocalPosition = new Vector3(0f, 1.25f, 0f),
            };

            PoseStateContext nearFullCrouchContext = CreateContext(
                skeleton,
                hipBoneIndex,
                restHeadY: 1.65f,
                currentHeadY: 1.05f + 0.0001f,
                restHeadHeight: 1.6f);
            PoseStateContext fullCrouchContext = CreateContext(
                skeleton,
                hipBoneIndex,
                restHeadY: 1.65f,
                currentHeadY: 1.05f,
                restHeadHeight: 1.6f);

            HipReconciliationTickResult nearFullCrouch = PoseState.ApplyHipLimitFrame(
                profileResult,
                state.BuildHipLimitFrame(nearFullCrouchContext),
                nearFullCrouchContext.RestHeadHeight,
                Transform3D.Identity);
            HipReconciliationTickResult fullCrouch = PoseState.ApplyHipLimitFrame(
                profileResult,
                state.BuildHipLimitFrame(fullCrouchContext),
                fullCrouchContext.RestHeadHeight,
                Transform3D.Identity);

            Assert.True(
                nearFullCrouch.AppliedHipLocalPosition.IsEqualApprox(fullCrouch.AppliedHipLocalPosition),
                $"Expected the upright-only up clamp to stay continuous across the full-crouch seam. NearFull={nearFullCrouch.AppliedHipLocalPosition}, Full={fullCrouch.AppliedHipLocalPosition}.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// The first mid-crouch contact with the standing envelope should only partially apply the
    /// limited head target so the visible pose does not pop at clamp onset.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PoseStateMachineTick_MidCrouchFirstClampContact_SoftensLimitedHeadSeam()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "StandingEnvelopeClampSeamFixture",
        };

        Skeleton3D skeleton = CreateStandingHipLimitSkeleton();
        int hipBoneIndex = RequireHipBoneIndex(skeleton);
        root.AddChild(skeleton);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            StandingPoseState hardClampState = CreateStandingHipLimitState();
            hardClampState.HeadLimitBlendRangeRatio = 0f;

            StandingPoseState softenedState = CreateStandingHipLimitState();
            softenedState.HeadLimitBlendRangeRatio = 1.0f;
            PoseStateContext firstClampContext = CreateContext(
                skeleton,
                hipBoneIndex,
                restHeadY: 1.65f,
                currentHeadY: 1.05f,
                restHeadHeight: 1.6f,
                currentHeadLocalOffset: new Vector3(0f, 0f, 1.95f));

            PoseStateMachineTickResult hardClampResult = CreatePoseStateMachine(hardClampState).Tick(firstClampContext);
            PoseStateMachineTickResult softenedResult = CreatePoseStateMachine(softenedState).Tick(firstClampContext);

            Assert.True(hardClampResult.LimitedHeadTargetTransform.HasValue, "Expected the regression setup to engage the first clamp seam without soft blending.");
            Assert.True(softenedResult.LimitedHeadTargetTransform.HasValue, "Expected the regression setup to engage the same seam with soft blending enabled.");

            float hardLimitedHeadShift = (
                firstClampContext.HeadTargetTransform.Origin
                - hardClampResult.LimitedHeadTargetTransform.Value.Origin).Length();
            float softenedLimitedHeadShift = (
                firstClampContext.HeadTargetTransform.Origin
                - softenedResult.LimitedHeadTargetTransform.Value.Origin).Length();

            Assert.True(
                softenedLimitedHeadShift < hardLimitedHeadShift,
                $"First clamp contact should soften the limited-head seam. Hard={hardLimitedHeadShift:F4} m, softened={softenedLimitedHeadShift:F4} m.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// When the standing reference frame is shifted and only the forward envelope clamps, the
    /// limited head target must preserve the already-applied crouch depth instead of reconstructing
    /// from the shifted-reference offset alone.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PoseStateMachineTick_ShiftedStandingReference_UsesAppliedHipPoseForLimitedHeadReconstruction()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "StandingEnvelopeShiftedReferenceFixture",
        };

        Skeleton3D skeleton = CreateStandingHipLimitSkeleton();
        int hipBoneIndex = RequireHipBoneIndex(skeleton);
        root.AddChild(skeleton);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            StandingPoseState state = CreateStandingHipLimitState();
            PoseStateContext context = CreateContext(
                skeleton,
                hipBoneIndex,
                restHeadY: 1.65f,
                currentHeadY: 1.05f,
                restHeadHeight: 1.6f,
                currentHeadLocalOffset: new Vector3(0f, 0f, 1.95f));

            PoseStateMachineTickResult result = CreatePoseStateMachine(state).Tick(context);

            Assert.True(result.HipLocalPosition.HasValue, "Expected shifted-reference clamp test to resolve a hip target.");
            Assert.True(result.LimitedHeadTargetTransform.HasValue, "Expected shifted-reference clamp test to produce a limited head target.");

            Vector3 appliedHipLocalPosition = result.HipLocalPosition.Value;
            Vector3 limitedHeadOrigin = result.LimitedHeadTargetTransform.Value.Origin;

            Assert.True(
                Mathf.Abs(appliedHipLocalPosition.Y - 0.7312f) <= 1e-3f,
                $"Expected crouch depth to remain applied at the hip before forward clamping. AppliedHip={appliedHipLocalPosition}.");
            Assert.True(
                Mathf.Abs(limitedHeadOrigin.Y - context.HeadTargetTransform.Origin.Y) <= PositionTolerance,
                $"Forward-only clamp should preserve the crouched head height. LimitedHead={limitedHeadOrigin}, currentHead={context.HeadTargetTransform.Origin}.");
            Assert.True(
                limitedHeadOrigin.Z < context.HeadTargetTransform.Origin.Z,
                $"Forward clamp should pull the limited head target back towards the body. LimitedHead={limitedHeadOrigin}, currentHead={context.HeadTargetTransform.Origin}.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Kneeling explicit limits must anchor to the authored full-crouch frame instead of raw hip rest.
    /// </summary>
    [Headless]
    [Fact]
    public async Task KneelingBuildHipLimitFrame_UsesKneelingReferenceAnchor()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "KneelingPoseStateHipLimitFixture",
        };

        Skeleton3D skeleton = CreateStandingHipLimitSkeleton();
        int hipBoneIndex = RequireHipBoneIndex(skeleton);
        root.AddChild(skeleton);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            OffsetLimits3D limits = new()
            {
                Up = 0.05f,
                Down = 0.03f,
                Left = 0.06f,
                Right = 0.06f,
                Forward = 0.08f,
                Back = 0.05f,
            };
            KneelingPoseState state = new()
            {
                HipOffsetLimits = limits,
            };

            HipLimitFrame frame = state.BuildHipLimitFrame(
                new PoseStateContext
                {
                    Skeleton = skeleton,
                    HipBoneIndex = hipBoneIndex,
                    RestHeadHeight = 1.6f,
                });

            Assert.True(
                frame.ReferenceHipLocalPosition.Z > 0f,
                $"Expected positive forward shift to move along avatar-forward resolved into skeleton-local +Z, got {frame.ReferenceHipLocalPosition}.");
            Assert.True(
                frame.ReferenceHipLocalPosition.IsEqualApprox(new Vector3(0f, 0.256f, 0.144f)),
                $"Expected kneeling reference anchor {frame.ReferenceHipLocalPosition} to use the kneeling reference ratios.");
            Assert.True(frame.OffsetEnvelope.HasValue);
            HipLimitEnvelope envelope = frame.OffsetEnvelope.Value;
            Assert.Equal(limits.Up, envelope.Up);
            Assert.Equal(limits.Down, envelope.Down);
            Assert.Equal(limits.Left, envelope.Left);
            Assert.Equal(limits.Right, envelope.Right);
            Assert.Equal(limits.Back, envelope.Forward);
            Assert.Equal(limits.Forward, envelope.Back);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Kneeling entry must keep the explicit limit frame continuous with the standing full-crouch anchor.
    /// </summary>
    [Headless]
    [Fact]
    public async Task KneelingBuildHipLimitFrame_MatchesStandingFullCrouchReference()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "KneelingStandingContinuityFixture",
        };

        Skeleton3D skeleton = CreateStandingHipLimitSkeleton();
        int hipBoneIndex = RequireHipBoneIndex(skeleton);
        root.AddChild(skeleton);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            StandingPoseState standingState = CreateStandingHipLimitState();
            KneelingPoseState kneelingState = new()
            {
                HipOffsetLimits = new OffsetLimits3D
                {
                    Up = 0.05f,
                    Down = 0.03f,
                    Left = 0.06f,
                    Right = 0.06f,
                    Forward = 0.08f,
                    Back = 0.05f,
                },
                KneelingReferenceHipHeightRatio = standingState.FullCrouchReferenceHipHeightRatio,
                KneelingReferenceForwardShiftRatio = standingState.FullCrouchReferenceForwardShiftRatio,
            };

            PoseStateContext context = CreateContext(
                skeleton,
                hipBoneIndex,
                restHeadY: 1.65f,
                currentHeadY: 1.05f,
                restHeadHeight: 1.6f);

            HipLimitFrame standingFrame = standingState.BuildHipLimitFrame(context);
            HipLimitFrame kneelingFrame = kneelingState.BuildHipLimitFrame(context);

            Assert.True(
                kneelingFrame.ReferenceHipLocalPosition.IsEqualApprox(standingFrame.ReferenceHipLocalPosition),
                $"Expected kneeling reference {kneelingFrame.ReferenceHipLocalPosition} to match standing full-crouch reference {standingFrame.ReferenceHipLocalPosition}.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Kneeling must use the same avatar-relative left/right and forward/back semantics as
    /// standing when limits are authored asymmetrically.
    /// </summary>
    [Headless]
    [Fact]
    public async Task KneelingBuildHipLimitFrame_MatchesStandingDirectionalAxisSemantics()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "KneelingDirectionalSemanticsFixture",
        };

        Skeleton3D skeleton = CreateStandingHipLimitSkeleton();
        int hipBoneIndex = RequireHipBoneIndex(skeleton);
        root.AddChild(skeleton);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            OffsetLimits3D asymmetricLimits = CreateAsymmetricDirectionalLimits();
            StandingPoseState standingState = new()
            {
                UprightHipOffsetLimits = asymmetricLimits,
                FullCrouchReferenceHipHeightRatio = 0.21f,
                FullCrouchReferenceForwardShiftRatio = 0.04f,
            };
            KneelingPoseState kneelingState = new()
            {
                HipOffsetLimits = asymmetricLimits,
                KneelingReferenceHipHeightRatio = 0.21f,
                KneelingReferenceForwardShiftRatio = 0.04f,
            };

            PoseStateContext standingContext = CreateContext(
                skeleton,
                hipBoneIndex,
                restHeadY: 1.65f,
                currentHeadY: 1.65f,
                restHeadHeight: 1.6f);
            PoseStateContext kneelingContext = CreateContext(
                skeleton,
                hipBoneIndex,
                restHeadY: 1.65f,
                currentHeadY: 1.05f,
                restHeadHeight: 1.6f);

            HipLimitFrame standingFrame = standingState.BuildHipLimitFrame(standingContext);
            HipLimitFrame kneelingFrame = kneelingState.BuildHipLimitFrame(kneelingContext);
            Vector3 standingReference = standingFrame.ReferenceHipLocalPosition;
            Vector3 kneelingReference = kneelingFrame.ReferenceHipLocalPosition;

            AssertAppliedHipPosition(standingFrame, standingReference + new Vector3(0.40f, 0f, 0f), standingReference + new Vector3(0.16f, 0f, 0f), standingContext.RestHeadHeight);
            AssertAppliedHipPosition(kneelingFrame, kneelingReference + new Vector3(0.40f, 0f, 0f), kneelingReference + new Vector3(0.16f, 0f, 0f), kneelingContext.RestHeadHeight);

            AssertAppliedHipPosition(standingFrame, standingReference + new Vector3(0f, 0f, 0.40f), standingReference + new Vector3(0f, 0f, 0.192f), standingContext.RestHeadHeight);
            AssertAppliedHipPosition(kneelingFrame, kneelingReference + new Vector3(0f, 0f, 0.40f), kneelingReference + new Vector3(0f, 0f, 0.192f), kneelingContext.RestHeadHeight);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static PoseStateMachine CreatePoseStateMachine(StandingPoseState state)
    {
        PoseStateMachine stateMachine = new()
        {
            States = [state],
            InitialStateId = StandingPoseState.DefaultId,
        };
        stateMachine.EnsureInitialStateResolved();
        return stateMachine;
    }

    private static PoseStateContext CreateContext(
        Skeleton3D skeleton,
        int hipBoneIndex,
        float restHeadY,
        float currentHeadY,
        float restHeadHeight,
        int headBoneIndex = -1,
        Basis? currentHeadBasis = null,
        Vector3? currentHeadLocalOffset = null)
        => new()
        {
            Skeleton = skeleton,
            HipBoneIndex = hipBoneIndex,
            HeadBoneIndex = headBoneIndex,
            RestHeadHeight = restHeadHeight,
            HeadTargetRestTransform = new Transform3D(Basis.Identity, new Vector3(0f, restHeadY, 0f)),
            HeadTargetTransform = new Transform3D(
                currentHeadBasis ?? Basis.Identity,
                new Vector3(0f, currentHeadY, 0f) + (currentHeadLocalOffset ?? Vector3.Zero)),
        };

    private static StandingPoseState CreateStandingHipLimitState()
        => new()
        {
            HipReconciliation = new HeadTrackingHipProfile
            {
                RotationCompensationWeight = 0f,
                VerticalPositionWeight = 1f,
                LateralPositionWeight = 0.5f,
                ForwardPositionWeight = 0.1f,
                MinimumAlignmentWeight = 0.1f,
            },
            UprightHipOffsetLimits = new OffsetLimits3D
            {
                HasDownLimit = false,
                Up = 0.15f,
                Down = 0.55f,
                Left = 0.2f,
                Right = 0.2f,
                Forward = 0.25f,
                Back = 0.15f,
            },
            CrouchedHipOffsetLimits = new OffsetLimits3D
            {
                HasUpLimit = false,
                Up = 0.05f,
                Down = 0.03f,
                Left = 0.06f,
                Right = 0.06f,
                Forward = 0.08f,
                Back = 0.05f,
            },
            FullCrouchReferenceHipHeightRatio = 0.21f,
            FullCrouchReferenceForwardShiftRatio = 0.04f,
        };

    private static OffsetLimits3D CreateAsymmetricDirectionalLimits()
        => new()
        {
            HasUpLimit = false,
            HasDownLimit = false,
            Left = 0.10f,
            Right = 0.30f,
            Forward = 0.12f,
            Back = 0.28f,
        };

    private static Skeleton3D CreateStandingHipLimitSkeleton()
    {
        Skeleton3D skeleton = new()
        {
            Name = "Skeleton",
        };

        int hipBoneIndex = skeleton.AddBone("Hips");
        skeleton.SetBoneRest(hipBoneIndex, new Transform3D(Basis.Identity, new Vector3(0f, 0.95f, 0f)));
        return skeleton;
    }

    private static int RequireHipBoneIndex(Skeleton3D skeleton)
        => RequireBoneIndex(skeleton, "Hips");

    private static void AssertAppliedHipPosition(
        HipLimitFrame frame,
        Vector3 desiredHipLocalPosition,
        Vector3 expectedAppliedHipLocalPosition,
        float restHeadHeight)
    {
        HipReconciliationTickResult result = PoseState.ApplyHipLimitFrame(
            new HipReconciliationProfileResult
            {
                DesiredHipLocalPosition = desiredHipLocalPosition,
            },
            frame,
            restHeadHeight,
            Transform3D.Identity);

        Assert.True(
            result.AppliedHipLocalPosition.IsEqualApprox(expectedAppliedHipLocalPosition),
            $"Expected applied hip position {expectedAppliedHipLocalPosition}, got {result.AppliedHipLocalPosition} for desired {desiredHipLocalPosition}.");
    }

    private static int RequireBoneIndex(Skeleton3D skeleton, string boneName)
    {
        int hipBoneIndex = skeleton.FindBone(boneName);
        Assert.True(hipBoneIndex >= 0, $"Expected test skeleton to expose a {boneName} bone.");
        return hipBoneIndex;
    }

    private sealed partial class TestPoseState : PoseState;
}
