using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Vision;

/// <summary>
/// Integration coverage for the VISION-001 transform-driven eye-gaze backend: backend inference from
/// paired eye-node authoring, coordinated clamped rotation in each eye's parent frame, neutral-transform
/// preservation, blink parity, and exclusive gaze-output ownership.
/// </summary>
public sealed class EyesTransformIntegrationTests
{
    private const string PlayerAnimationTreeRootPath = "res://assets/characters/templates/animation/animation_tree_root_player.tres";
    private const float HorizontalLimitRadians = 0.6108652f;
    private const float VerticalLimitRadians = 0.4363323f;
    private const float AngleToleranceRadians = 0.001f;

    /// <summary>
    /// Verifies that authoring no eye-node references keeps the blendshape backend: look blends stay 1,
    /// seek times advance towards the target, and eye-node local transforms stay bit-identical.
    /// </summary>
    [Headless]
    [Fact]
    public async Task EyesBehaviour_WithoutEyeNodeReferences_KeepsBlendshapeBackendWithLookBlendsAndSeek()
    {
        SceneTree sceneTree = GetSceneTree();
        TransformGazeRig rig = await TransformGazeRig.CreateAsync(
            sceneTree,
            new RigOptions { AssignLeftEye = false, AssignRightEye = false });

        try
        {
            Vector3 target = PlaceGazeTarget(rig, new Vector3(1f, 0.2f, -0.2f));
            rig.Eyes.SetLookTarget(rig.GazeTarget);
            rig.Eyes.RefreshLookParametersDeferred();
            await WaitForFramesAsync(sceneTree, 2);

            Assert.Equal(1f, rig.Tree.Get(EyesAnimationTreePaths.GetHorizontalLookBlendParameter()).AsSingle(), precision: 5);
            Assert.Equal(1f, rig.Tree.Get(EyesAnimationTreePaths.GetVerticalLookBlendParameter()).AsSingle(), precision: 5);

            (float expectedHorizontal, float expectedVertical) = ResolveExpectedClampedAngles(rig.EyeOrigin, target);
            float expectedHorizontalSeek = Mathf.Clamp(
                0.5f - (expectedHorizontal / (2f * HorizontalLimitRadians)),
                EyesLookMath.MinimumSeekTimeSeconds,
                EyesLookMath.MaximumSeekTimeSeconds);
            float expectedVerticalSeek = Mathf.Clamp(
                0.5f - (expectedVertical / (2f * VerticalLimitRadians)),
                EyesLookMath.MinimumSeekTimeSeconds,
                EyesLookMath.MaximumSeekTimeSeconds);

            Assert.Equal(expectedHorizontalSeek, rig.Eyes.GetHorizontalLookSeekTime(), precision: 4);
            Assert.Equal(expectedVerticalSeek, rig.Eyes.GetVerticalLookSeekTime(), precision: 4);
            Assert.Equal(expectedHorizontalSeek, rig.Tree.Get(EyesAnimationTreePaths.GetHorizontalLookSeekParameter()).AsSingle(), precision: 4);
            Assert.Equal(expectedVerticalSeek, rig.Tree.Get(EyesAnimationTreePaths.GetVerticalLookSeekParameter()).AsSingle(), precision: 4);

            Assert.Equal(rig.LeftEyeAuthoredLocal, rig.LeftEye.Transform);
            Assert.Equal(rig.RightEyeAuthoredLocal, rig.RightEye.Transform);
        }
        finally
        {
            await rig.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies that authoring both eye-node references selects the transform backend: look blends are
    /// driven to 0, no TimeSeek seek requests are emitted while eye rotation changes.
    /// </summary>
    [Headless]
    [Fact]
    public async Task EyesBehaviour_WithBothEyeNodeReferences_WritesZeroLookBlendsAndNoSeekRequests()
    {
        SceneTree sceneTree = GetSceneTree();
        TransformGazeRig rig = await TransformGazeRig.CreateAsync(sceneTree);

        try
        {
            float initialHorizontalSeek = rig.Tree.Get(EyesAnimationTreePaths.GetHorizontalLookSeekParameter()).AsSingle();
            float initialVerticalSeek = rig.Tree.Get(EyesAnimationTreePaths.GetVerticalLookSeekParameter()).AsSingle();

            rig.Eyes.SetLookTarget(rig.GazeTarget);
            foreach (Vector3 localDirection in new[]
                     {
                         new Vector3(1f, 0f, -0.2f),
                         new Vector3(-1f, 1f, -0.1f),
                         new Vector3(0.3f, 0.2f, -1f),
                     })
            {
                Vector3 target = PlaceGazeTarget(rig, localDirection);
                rig.Eyes.RefreshLookParametersDeferred();
                await WaitForFramesAsync(sceneTree, 1);

                Assert.Equal(0f, rig.Tree.Get(EyesAnimationTreePaths.GetHorizontalLookBlendParameter()).AsSingle(), precision: 5);
                Assert.Equal(0f, rig.Tree.Get(EyesAnimationTreePaths.GetVerticalLookBlendParameter()).AsSingle(), precision: 5);
                Assert.Equal(initialHorizontalSeek, rig.Tree.Get(EyesAnimationTreePaths.GetHorizontalLookSeekParameter()).AsSingle(), precision: 7);
                Assert.Equal(initialVerticalSeek, rig.Tree.Get(EyesAnimationTreePaths.GetVerticalLookSeekParameter()).AsSingle(), precision: 7);
                AssertCoordinatedGaze(rig.LeftEye, rig.LeftEyeAuthoredLocal, rig.RightEye, rig.RightEyeAuthoredLocal, rig.EyeOrigin, target, "both-eyes mode");
            }

            Assert.NotEqual(EyesLookMath.NeutralSeekTimeSeconds, rig.Eyes.GetHorizontalLookSeekTime(), precision: 4);
        }
        finally
        {
            await rig.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies that assigning only the left eye node fails activation clearly, naming the unpaired reference.
    /// </summary>
    [Headless]
    [Fact]
    public async Task EyesBehaviour_LeftEyeOnly_FailsActivationWithActionablePairError()
    {
        SceneTree sceneTree = GetSceneTree();
        TransformGazeRig rig = await TransformGazeRig.CreateAsync(
            sceneTree,
            new RigOptions { AssignRightEye = false, ActivateAnimationTree = false });

        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => rig.Eyes.AnimationTree = rig.Tree);

            Assert.Contains(nameof(EyesBehaviour.LeftEye), exception.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(EyesBehaviour.RightEye), exception.Message, StringComparison.Ordinal);
            Assert.Contains("together", exception.Message, StringComparison.Ordinal);
            Assert.Contains($"'{nameof(EyesBehaviour.RightEye)}' is not assigned", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            rig.Eyes.LeftEye = null;
            rig.Eyes.RightEye = null;
            await rig.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies that assigning only the right eye node fails activation clearly, naming the unpaired reference.
    /// </summary>
    [Headless]
    [Fact]
    public async Task EyesBehaviour_RightEyeOnly_FailsActivationWithActionablePairError()
    {
        SceneTree sceneTree = GetSceneTree();
        TransformGazeRig rig = await TransformGazeRig.CreateAsync(
            sceneTree,
            new RigOptions { AssignLeftEye = false, ActivateAnimationTree = false });

        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                rig.Eyes.RefreshLookParametersDeferred);

            Assert.Contains(nameof(EyesBehaviour.LeftEye), exception.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(EyesBehaviour.RightEye), exception.Message, StringComparison.Ordinal);
            Assert.Contains("together", exception.Message, StringComparison.Ordinal);
            Assert.Contains($"'{nameof(EyesBehaviour.LeftEye)}' is not assigned", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            rig.Eyes.LeftEye = null;
            rig.Eyes.RightEye = null;
            await rig.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies horizontal, vertical, diagonal, beyond-limit, and behind targets rotate both eyes with the
    /// same world-space delta that respects the shared limits, including eyes under differently rotated parents.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TransformGaze_CoordinatedRotation_MatchesExpectedClampedAnglesForDirectionalTargets()
    {
        SceneTree sceneTree = GetSceneTree();
        TransformGazeRig rig = await TransformGazeRig.CreateAsync(sceneTree);

        try
        {
            rig.Eyes.SetLookTarget(rig.GazeTarget);
            foreach ((string scenarioName, Vector3 localDirection) in DirectionalScenarios())
            {
                Vector3 target = PlaceGazeTarget(rig, localDirection);
                rig.Eyes.RefreshLookParametersDeferred();
                await WaitForFramesAsync(sceneTree, 1);

                AssertCoordinatedGaze(
                    rig.LeftEye,
                    rig.LeftEyeAuthoredLocal,
                    rig.RightEye,
                    rig.RightEyeAuthoredLocal,
                    rig.EyeOrigin,
                    target,
                    scenarioName);
            }
        }
        finally
        {
            await rig.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies gaze deltas survive a rotated head with positive non-uniform scale on the eye origin, the
    /// eye sockets, and the eyes themselves, matching an identically rotated unscaled reference rig.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TransformGaze_CoordinatedRotation_SurvivesRotatedHeadWithNonUniformScale()
    {
        SceneTree sceneTree = GetSceneTree();
        TransformGazeRig unscaled = await TransformGazeRig.CreateAsync(sceneTree);
        TransformGazeRig scaled = await TransformGazeRig.CreateAsync(
            sceneTree,
            new RigOptions
            {
                EyeOriginScale = new Vector3(1.25f, 0.85f, 1.1f),
                LeftSocketScale = new Vector3(1.2f, 0.95f, 1.05f),
                LeftEyeScale = new Vector3(1.15f, 1.05f, 0.95f),
                RightEyeScale = new Vector3(1.08f, 0.92f, 1.12f),
            });

        try
        {
            unscaled.Eyes.SetLookTarget(unscaled.GazeTarget);
            scaled.Eyes.SetLookTarget(scaled.GazeTarget);
            foreach ((string scenarioName, Vector3 localDirection) in DirectionalScenarios())
            {
                if (scenarioName is not ("Right" or "Up" or "DiagonalWithinLimits" or "BeyondBothLimits"))
                {
                    continue;
                }

                Vector3 target = PlaceGazeTarget(unscaled, localDirection);
                scaled.GazeTarget.GlobalPosition = target;
                unscaled.Eyes.RefreshLookParametersDeferred();
                scaled.Eyes.RefreshLookParametersDeferred();
                await WaitForFramesAsync(sceneTree, 1);

                Quaternion unscaledDelta = ResolveWorldGazeDelta(unscaled.LeftEye, unscaled.LeftEyeAuthoredLocal);
                Quaternion scaledLeftDelta = ResolveWorldGazeDelta(scaled.LeftEye, scaled.LeftEyeAuthoredLocal);
                Quaternion scaledRightDelta = ResolveWorldGazeDelta(scaled.RightEye, scaled.RightEyeAuthoredLocal);

                Assert.True(
                    scaledLeftDelta.AngleTo(scaledRightDelta) <= AngleToleranceRadians,
                    $"'{scenarioName}': scaled-rig eyes must rotate as one coordinated unit.");
                Assert.True(
                    scaledLeftDelta.AngleTo(unscaledDelta) <= AngleToleranceRadians,
                    $"'{scenarioName}': scaled-rig gaze delta must match the unscaled reference rig "
                    + $"(difference {scaledLeftDelta.AngleTo(unscaledDelta):F6} rad).");
                AssertCoordinatedGaze(
                    scaled.LeftEye,
                    scaled.LeftEyeAuthoredLocal,
                    scaled.RightEye,
                    scaled.RightEyeAuthoredLocal,
                    scaled.EyeOrigin,
                    target,
                    $"scaled rig '{scenarioName}'");
            }
        }
        finally
        {
            await scaled.DisposeAsync();
            await unscaled.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies each eye's authored neutral local origin is preserved exactly and its neutral scale components
    /// are preserved while gazing at an extreme target.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TransformGaze_PreservesAuthoredNeutralPositionAndScaleWhileGazing()
    {
        SceneTree sceneTree = GetSceneTree();
        TransformGazeRig rig = await TransformGazeRig.CreateAsync(
            sceneTree,
            new RigOptions
            {
                EyeOriginScale = new Vector3(1.25f, 0.85f, 1.1f),
                LeftEyeScale = new Vector3(1.15f, 1.05f, 0.95f),
                RightEyeScale = new Vector3(1.08f, 0.92f, 1.12f),
            });

        try
        {
            rig.Eyes.SetLookTarget(rig.GazeTarget);
            Vector3 target = PlaceGazeTarget(rig, new Vector3(1f, 1f, -0.1f));
            rig.Eyes.RefreshLookParametersDeferred();
            await WaitForFramesAsync(sceneTree, 2);

            AssertCoordinatedGaze(
                rig.LeftEye,
                rig.LeftEyeAuthoredLocal,
                rig.RightEye,
                rig.RightEyeAuthoredLocal,
                rig.EyeOrigin,
                target,
                "neutral preservation");

            Assert.Equal(rig.LeftEyeAuthoredLocal.Origin, rig.LeftEye.Transform.Origin);
            Assert.Equal(rig.RightEyeAuthoredLocal.Origin, rig.RightEye.Transform.Origin);
            AssertBasisScale(rig.LeftEyeAuthoredLocal.Basis, rig.LeftEye.Transform.Basis, "left eye");
            AssertBasisScale(rig.RightEyeAuthoredLocal.Basis, rig.RightEye.Transform.Basis, "right eye");
        }
        finally
        {
            await rig.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies repeated alternating-target updates over many frames return both eyes to the exact neutral
    /// transform when the target re-centres, without accumulated drift.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TransformGaze_AlternatingTargetsAcrossManyFrames_ReturnsToNeutralWithoutDrift()
    {
        SceneTree sceneTree = GetSceneTree();
        TransformGazeRig rig = await TransformGazeRig.CreateAsync(sceneTree);

        try
        {
            rig.Eyes.SetLookTarget(rig.GazeTarget);

            Vector3 centreTarget = PlaceGazeTarget(rig, new Vector3(0f, 0f, -1f));
            rig.Eyes.RefreshLookParametersDeferred();
            Transform3D firstCentreLeft = rig.LeftEye.Transform;
            Transform3D firstCentreRight = rig.RightEye.Transform;

            Vector3[] alternationTargets =
            [
                PlaceGazeTarget(rig, new Vector3(1f, 1f, -0.1f)),
                PlaceGazeTarget(rig, new Vector3(-1f, -1f, -0.1f)),
                PlaceGazeTarget(rig, new Vector3(-1f, 1f, -0.1f)),
                PlaceGazeTarget(rig, new Vector3(1f, -1f, -0.1f)),
            ];
            for (int cycle = 0; cycle < 40; cycle++)
            {
                rig.GazeTarget.GlobalPosition = alternationTargets[cycle % alternationTargets.Length];
                rig.Eyes.RefreshLookParametersDeferred();
            }

            rig.GazeTarget.GlobalPosition = centreTarget;
            rig.Eyes.RefreshLookParametersDeferred();
            Transform3D settledLeft = rig.LeftEye.Transform;
            Transform3D settledRight = rig.RightEye.Transform;

            Assert.Equal(firstCentreLeft, settledLeft);
            Assert.Equal(firstCentreRight, settledRight);
            AssertNeutralTransform(rig.LeftEyeAuthoredLocal, settledLeft, "left eye");
            AssertNeutralTransform(rig.RightEyeAuthoredLocal, settledRight, "right eye");
        }
        finally
        {
            await rig.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies clearing the look target returns gaze towards the existing forward fallback.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TransformGaze_ClearingLookTarget_ReturnsGazeTowardForwardFallback()
    {
        SceneTree sceneTree = GetSceneTree();
        TransformGazeRig rig = await TransformGazeRig.CreateAsync(sceneTree);

        try
        {
            rig.Eyes.SetLookTarget(rig.GazeTarget);
            _ = PlaceGazeTarget(rig, new Vector3(1f, 0f, -0.2f));
            rig.Eyes.RefreshLookParametersDeferred();
            Quaternion gazedDelta = ResolveWorldGazeDelta(rig.LeftEye, rig.LeftEyeAuthoredLocal);
            Assert.True(gazedDelta.AngleTo(Quaternion.Identity) > 0.1f, "Expected an off-centre target to rotate the eyes first.");

            rig.Eyes.ClearLookTarget();
            rig.Eyes.RefreshLookParametersDeferred();
            await WaitForFramesAsync(sceneTree, 2);

            Quaternion fallbackDelta = ResolveWorldGazeDelta(rig.LeftEye, rig.LeftEyeAuthoredLocal);
            Assert.True(
                fallbackDelta.AngleTo(Quaternion.Identity) <= 0.001f,
                $"Fallback gaze must return the eyes to neutral, but deviated by {fallbackDelta.AngleTo(Quaternion.Identity):F6} rad.");
            Assert.Equal(rig.LeftEyeAuthoredLocal.Origin, rig.LeftEye.Transform.Origin);
            Assert.Equal(EyesLookMath.NeutralSeekTimeSeconds, rig.Eyes.GetHorizontalLookSeekTime(), precision: 4);
            Assert.Equal(EyesLookMath.NeutralSeekTimeSeconds, rig.Eyes.GetVerticalLookSeekTime(), precision: 4);
        }
        finally
        {
            await rig.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies controller-owned smoothing converges the transform gaze towards the clamped target rotation
    /// without snapping on the frame the target moves.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TransformGaze_SmoothedGaze_ConvergesTowardTargetRotation()
    {
        SceneTree sceneTree = GetSceneTree();
        TransformGazeRig rig = await TransformGazeRig.CreateAsync(
            sceneTree,
            new RigOptions { LookSmoothingTime = 0.08f });

        try
        {
            rig.Eyes.SetLookTarget(rig.GazeTarget);
            Vector3 target = PlaceGazeTarget(rig, new Vector3(1f, 0f, -0.2f));

            Quaternion beforeMove = ResolveWorldGazeDelta(rig.LeftEye, rig.LeftEyeAuthoredLocal);
            Quaternion expectedDelta = ResolveExpectedGazeDelta(rig.EyeOrigin, target);
            Assert.True(
                beforeMove.AngleTo(expectedDelta) > 0.1f,
                "Gaze must not snap to the new target before any frame has processed.");

            await WaitForSecondsAsync(sceneTree, 0.8f);

            Quaternion converged = ResolveWorldGazeDelta(rig.LeftEye, rig.LeftEyeAuthoredLocal);
            Assert.True(
                converged.AngleTo(expectedDelta) <= AngleToleranceRadians,
                $"Smoothed gaze must converge to the clamped target rotation (remaining {converged.AngleTo(expectedDelta):F6} rad).");
        }
        finally
        {
            await rig.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies replacing the eye pair at runtime restores the previously controlled eyes' exact neutral
    /// transforms while the new pair becomes gaze-controlled.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TransformGaze_ReplacingEyePairAtRuntime_RestoresPreviousEyesAndControlsNewPair()
    {
        SceneTree sceneTree = GetSceneTree();
        TransformGazeRig rig = await TransformGazeRig.CreateAsync(sceneTree);

        try
        {
            rig.Eyes.SetLookTarget(rig.GazeTarget);
            _ = PlaceGazeTarget(rig, new Vector3(1f, 0f, -0.2f));
            rig.Eyes.RefreshLookParametersDeferred();
            await WaitForFramesAsync(sceneTree, 1);

            Node3D replacementLeft = rig.AddReplacementEye("ReplacementLeftEye", new Vector3(-0.025f, 0.015f, 0.035f));
            Node3D replacementRight = rig.AddReplacementEye("ReplacementRightEye", new Vector3(0.025f, 0.005f, 0.025f));
            Transform3D replacementLeftNeutral = replacementLeft.Transform;
            Transform3D replacementRightNeutral = replacementRight.Transform;

            rig.Eyes.LeftEye = replacementLeft;
            rig.Eyes.RightEye = replacementRight;
            await WaitForFramesAsync(sceneTree, 1);

            Vector3 target = PlaceGazeTarget(rig, new Vector3(-1f, 1f, -0.1f));
            rig.Eyes.RefreshLookParametersDeferred();
            await WaitForFramesAsync(sceneTree, 1);

            Assert.Equal(rig.LeftEyeAuthoredLocal, rig.LeftEye.Transform);
            Assert.Equal(rig.RightEyeAuthoredLocal, rig.RightEye.Transform);
            AssertCoordinatedGaze(
                replacementLeft,
                replacementLeftNeutral,
                replacementRight,
                replacementRightNeutral,
                rig.EyeOrigin,
                target,
                "replacement pair");
        }
        finally
        {
            await rig.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies clearing the eye pair at runtime restores the controlled eyes' exact neutral transforms and
    /// returns the behaviour to the blendshape backend.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TransformGaze_ClearingEyePairAtRuntime_RestoresNeutralAndReturnsToBlendshapeMode()
    {
        SceneTree sceneTree = GetSceneTree();
        TransformGazeRig rig = await TransformGazeRig.CreateAsync(sceneTree);

        try
        {
            rig.Eyes.SetLookTarget(rig.GazeTarget);
            _ = PlaceGazeTarget(rig, new Vector3(1f, 0f, -0.2f));
            rig.Eyes.RefreshLookParametersDeferred();
            await WaitForFramesAsync(sceneTree, 1);

            rig.Eyes.LeftEye = null;
            rig.Eyes.RightEye = null;
            await WaitForFramesAsync(sceneTree, 1);

            Assert.Equal(rig.LeftEyeAuthoredLocal, rig.LeftEye.Transform);
            Assert.Equal(rig.RightEyeAuthoredLocal, rig.RightEye.Transform);
            Assert.Equal(1f, rig.Tree.Get(EyesAnimationTreePaths.GetHorizontalLookBlendParameter()).AsSingle(), precision: 5);
            Assert.Equal(1f, rig.Tree.Get(EyesAnimationTreePaths.GetVerticalLookBlendParameter()).AsSingle(), precision: 5);
            Assert.NotEqual(EyesLookMath.NeutralSeekTimeSeconds, rig.Eyes.GetHorizontalLookSeekTime(), precision: 4);
        }
        finally
        {
            await rig.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies a forced blink still emits the OneShot request and blink time scale in transform mode while
    /// look blends stay zero and no seek requests are emitted.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TransformGaze_ForcedBlink_EmitsOneShotRequestAndTimeScaleWithZeroLookBlends()
    {
        SceneTree sceneTree = GetSceneTree();
        TransformGazeRig rig = await TransformGazeRig.CreateAsync(
            sceneTree,
            new RigOptions { BlinkDuration = 0.2f });

        try
        {
            float initialHorizontalSeek = rig.Tree.Get(EyesAnimationTreePaths.GetHorizontalLookSeekParameter()).AsSingle();
            float initialVerticalSeek = rig.Tree.Get(EyesAnimationTreePaths.GetVerticalLookSeekParameter()).AsSingle();
            rig.Eyes.SetLookTarget(rig.GazeTarget);
            Vector3 target = PlaceGazeTarget(rig, new Vector3(1f, 0f, -0.2f));
            rig.Eyes.RefreshLookParametersDeferred();
            await WaitForFramesAsync(sceneTree, 1);

            rig.Eyes.TriggerBlink();
            await WaitForFramesAsync(sceneTree, 1);

            Assert.Equal(
                (int)AnimationNodeOneShot.OneShotRequest.Fire,
                rig.Tree.Get(EyesAnimationTreePaths.GetBlinkOneShotRequestParameter()).AsInt32());
            Assert.Equal(1.5f, rig.Tree.Get(EyesAnimationTreePaths.GetBlinkTimeScaleParameter()).AsSingle(), precision: 5);
            Assert.Equal(0f, rig.Tree.Get(EyesAnimationTreePaths.GetHorizontalLookBlendParameter()).AsSingle(), precision: 5);
            Assert.Equal(0f, rig.Tree.Get(EyesAnimationTreePaths.GetVerticalLookBlendParameter()).AsSingle(), precision: 5);
            Assert.Equal(initialHorizontalSeek, rig.Tree.Get(EyesAnimationTreePaths.GetHorizontalLookSeekParameter()).AsSingle(), precision: 7);
            Assert.Equal(initialVerticalSeek, rig.Tree.Get(EyesAnimationTreePaths.GetVerticalLookSeekParameter()).AsSingle(), precision: 7);
            AssertCoordinatedGaze(
                rig.LeftEye,
                rig.LeftEyeAuthoredLocal,
                rig.RightEye,
                rig.RightEyeAuthoredLocal,
                rig.EyeOrigin,
                target,
                "forced blink keeps gaze");
        }
        finally
        {
            await rig.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies autonomous blink cadence still fires the OneShot request in transform mode while look blends
    /// stay zero and the gaze state getters keep reporting the active gaze.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TransformGaze_AutonomousBlinkCadence_FiresOneShotWhileLookBlendsStayZero()
    {
        SceneTree sceneTree = GetSceneTree();
        TransformGazeRig rig = await TransformGazeRig.CreateAsync(
            sceneTree,
            new RigOptions { MinimumBlinkInterval = 0f, MaximumBlinkInterval = 0f });

        try
        {
            rig.Eyes.SetLookTarget(rig.GazeTarget);
            _ = PlaceGazeTarget(rig, new Vector3(-1f, 0f, -0.2f));
            await WaitForFramesAsync(sceneTree, 3);

            Assert.Equal(
                (int)AnimationNodeOneShot.OneShotRequest.Fire,
                rig.Tree.Get(EyesAnimationTreePaths.GetBlinkOneShotRequestParameter()).AsInt32());
            Assert.Equal(1f, rig.Tree.Get(EyesAnimationTreePaths.GetBlinkTimeScaleParameter()).AsSingle(), precision: 5);
            Assert.Equal(0f, rig.Tree.Get(EyesAnimationTreePaths.GetHorizontalLookBlendParameter()).AsSingle(), precision: 5);
            Assert.Equal(0f, rig.Tree.Get(EyesAnimationTreePaths.GetVerticalLookBlendParameter()).AsSingle(), precision: 5);
            Assert.NotEqual(EyesLookMath.NeutralSeekTimeSeconds, rig.Eyes.GetHorizontalLookSeekTime(), precision: 4);
        }
        finally
        {
            await rig.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies the fixture hierarchy's AnimationPlayers author no transform tracks for transform-mode eyes,
    /// as a structural scan documenting the exclusive rotation-ownership authoring rule.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TransformRig_AnimationPlayers_CarryNoTransformTracksForTransformModeEyes()
    {
        SceneTree sceneTree = GetSceneTree();
        TransformGazeRig rig = await TransformGazeRig.CreateAsync(sceneTree);

        try
        {
            List<AnimationPlayer> players = [];
            CollectAnimationPlayers(rig.Root, players);
            Assert.NotEmpty(players);
            Assert.True(rig.LeftEye.IsInsideTree() && rig.RightEye.IsInsideTree(), "The scanned hierarchy must contain the eye nodes.");

            int scannedTracks = 0;
            foreach (AnimationPlayer player in players)
            {
                foreach (StringName libraryName in player.GetAnimationLibraryList())
                {
                    AnimationLibrary library = player.GetAnimationLibrary(libraryName);
                    foreach (StringName animationName in library.GetAnimationList())
                    {
                        Animation animation = library.GetAnimation(animationName);
                        for (int trackIndex = 0; trackIndex < animation.GetTrackCount(); trackIndex++)
                        {
                            Animation.TrackType trackType = animation.TrackGetType(trackIndex);
                            Assert.True(
                                trackType is not (Animation.TrackType.Position3D or Animation.TrackType.Rotation3D or Animation.TrackType.Scale3D),
                                $"Animation '{animationName}' authors a {trackType} track '{animation.TrackGetPath(trackIndex)}'; "
                                + "transform-mode eyes must not carry authored AnimationPlayer transform tracks.");
                            scannedTracks++;
                        }
                    }
                }
            }

            Assert.True(scannedTracks > 0, "The structural scan must traverse real authored animation tracks, not an empty hierarchy.");
        }
        finally
        {
            await rig.DisposeAsync();
        }
    }

    private static IEnumerable<(string Name, Vector3 LocalDirection)> DirectionalScenarios()
    {
        yield return ("Forward", new Vector3(0f, 0f, -1f));
        yield return ("Right", new Vector3(1f, 0f, -0.2f));
        yield return ("Left", new Vector3(-1f, 0f, -0.2f));
        yield return ("Up", new Vector3(0f, 1f, -0.2f));
        yield return ("Down", new Vector3(0f, -1f, -0.2f));
        yield return ("DiagonalWithinLimits", new Vector3(0.3f, 0.2f, -1f));
        yield return ("BeyondBothLimits", new Vector3(1f, 1f, -0.1f));
        yield return ("Behind", new Vector3(0f, 0f, 1f));
    }

    /// <summary>
    /// Places the rig's gaze target at the supplied eye-origin-local direction and returns its world position.
    /// </summary>
    private static Vector3 PlaceGazeTarget(TransformGazeRig rig, Vector3 localDirection, float distance = 6f)
    {
        Quaternion originRotation = rig.EyeOrigin.GlobalTransform.Basis.GetRotationQuaternion();
        Vector3 worldPosition = rig.EyeOrigin.GlobalPosition + (originRotation * (localDirection.Normalized() * distance));
        rig.GazeTarget.GlobalPosition = worldPosition;
        return worldPosition;
    }

    /// <summary>
    /// Asserts both eyes carry the identical clamped world gaze delta for the target, matching the expected
    /// first-principles clamped angles in the shared eye-origin frame and respecting the configured limits.
    /// </summary>
    private static void AssertCoordinatedGaze(
        Node3D leftEye,
        Transform3D leftEyeNeutralLocal,
        Node3D rightEye,
        Transform3D rightEyeNeutralLocal,
        Node3D eyeOrigin,
        Vector3 targetWorldPosition,
        string scenarioName)
    {
        Quaternion leftDelta = ResolveWorldGazeDelta(leftEye, leftEyeNeutralLocal);
        Quaternion rightDelta = ResolveWorldGazeDelta(rightEye, rightEyeNeutralLocal);
        Assert.True(
            leftDelta.AngleTo(rightDelta) <= AngleToleranceRadians,
            $"'{scenarioName}': left and right eye world gaze deltas differ by {leftDelta.AngleTo(rightDelta):F6} rad.");

        (float expectedHorizontal, float expectedVertical) = ResolveExpectedClampedAngles(eyeOrigin, targetWorldPosition);
        (float actualHorizontal, float actualVertical) = ExtractGazeDeltaAnglesInOriginFrame(leftDelta, eyeOrigin);
        Assert.True(
            Mathf.Abs(actualHorizontal - expectedHorizontal) <= AngleToleranceRadians,
            $"'{scenarioName}': horizontal gaze angle {actualHorizontal:F6} rad does not match the expected clamped {expectedHorizontal:F6} rad.");
        Assert.True(
            Mathf.Abs(actualVertical - expectedVertical) <= AngleToleranceRadians,
            $"'{scenarioName}': vertical gaze angle {actualVertical:F6} rad does not match the expected clamped {expectedVertical:F6} rad.");
        Assert.True(
            Mathf.Abs(actualHorizontal) <= HorizontalLimitRadians + AngleToleranceRadians,
            $"'{scenarioName}': horizontal gaze angle exceeded the configured limit.");
        Assert.True(
            Mathf.Abs(actualVertical) <= VerticalLimitRadians + AngleToleranceRadians,
            $"'{scenarioName}': vertical gaze angle exceeded the configured limit.");
    }

    /// <summary>
    /// Resolves the world-space rotation an eye has applied relative to its authored neutral local transform.
    /// The eye's global basis can be skewed when a scaled parent sits above a rotated eye, so the delta is
    /// derived from the exact local delta and the parent's orthogonal-column global basis instead.
    /// </summary>
    private static Quaternion ResolveWorldGazeDelta(Node3D eye, Transform3D neutralLocalTransform)
    {
        Node3D parent = eye.GetParentOrNull<Node3D>()
            ?? throw new InvalidOperationException($"Eye '{eye.Name}' must have a Node3D parent.");
        Quaternion localDelta = (eye.Transform.Basis * neutralLocalTransform.Basis.Inverse()).GetRotationQuaternion();
        Quaternion parentRotation = parent.GlobalTransform.Basis.GetRotationQuaternion();
        return parentRotation * localDelta * parentRotation.Inverse();
    }

    /// <summary>
    /// Resolves the expected world gaze delta quaternion from first-principles clamped eye-origin-frame angles
    /// for the supplied target, independent of the production conversion path.
    /// </summary>
    private static Quaternion ResolveExpectedGazeDelta(Node3D eyeOrigin, Vector3 targetWorldPosition)
    {
        (float horizontal, float vertical) = ResolveExpectedClampedAngles(eyeOrigin, targetWorldPosition);
        Quaternion yaw = new(Vector3.Up, -horizontal);
        Quaternion pitch = new(Vector3.Right, vertical);
        Quaternion originRotation = eyeOrigin.GlobalTransform.Basis.GetRotationQuaternion();
        return originRotation * (yaw * pitch) * originRotation.Inverse();
    }

    /// <summary>
    /// Resolves the expected clamped gaze angles by projecting the target direction onto the eye origin's
    /// world axes, independent of the production conversion path.
    /// </summary>
    private static (float Horizontal, float Vertical) ResolveExpectedClampedAngles(Node3D eyeOrigin, Vector3 targetWorldPosition)
    {
        Basis originBasis = eyeOrigin.GlobalTransform.Basis;
        Vector3 right = (originBasis * Vector3.Right).Normalized();
        Vector3 up = (originBasis * Vector3.Up).Normalized();
        Vector3 back = (originBasis * Vector3.Back).Normalized();
        Vector3 direction = (targetWorldPosition - eyeOrigin.GlobalPosition).Normalized();

        float horizontalComponent = direction.Dot(right);
        float verticalComponent = direction.Dot(up);
        float forwardComponent = -direction.Dot(back);
        float horizontal = Mathf.Atan2(horizontalComponent, forwardComponent);
        float vertical = Mathf.Atan2(
            verticalComponent,
            Mathf.Sqrt((horizontalComponent * horizontalComponent) + (forwardComponent * forwardComponent)));

        return (
            Mathf.Clamp(horizontal, -HorizontalLimitRadians, HorizontalLimitRadians),
            Mathf.Clamp(vertical, -VerticalLimitRadians, VerticalLimitRadians));
    }

    /// <summary>
    /// Extracts the yaw/pitch angles a world gaze delta represents in the shared eye-origin frame.
    /// </summary>
    private static (float Horizontal, float Vertical) ExtractGazeDeltaAnglesInOriginFrame(Quaternion worldGazeDelta, Node3D eyeOrigin)
    {
        Quaternion originRotation = eyeOrigin.GlobalTransform.Basis.GetRotationQuaternion();
        Quaternion originFrameDelta = originRotation.Inverse() * worldGazeDelta * originRotation;
        Vector3 gazeDirection = originFrameDelta * new Vector3(0f, 0f, -1f);
        return (
            Mathf.Atan2(gazeDirection.X, -gazeDirection.Z),
            Mathf.Atan2(gazeDirection.Y, Mathf.Sqrt((gazeDirection.X * gazeDirection.X) + (gazeDirection.Z * gazeDirection.Z))));
    }

    private static void AssertBasisScale(Basis neutral, Basis actual, string eyeName)
    {
        Vector3[] neutralColumns = [neutral * Vector3.Right, neutral * Vector3.Up, neutral * Vector3.Back];
        Vector3[] actualColumns = [actual * Vector3.Right, actual * Vector3.Up, actual * Vector3.Back];
        for (int column = 0; column < 3; column++)
        {
            Assert.True(
                Mathf.Abs(neutralColumns[column].Length() - actualColumns[column].Length()) <= 0.00001f,
                $"'{eyeName}' neutral scale component {neutralColumns[column].Length():F6} was not preserved "
                + $"while gazing (found {actualColumns[column].Length():F6}).");
        }
    }

    private static void AssertNeutralTransform(Transform3D neutral, Transform3D actual, string eyeName)
    {
        Assert.Equal(neutral.Origin, actual.Origin);
        Quaternion relative = (actual.Basis * neutral.Basis.Inverse()).GetRotationQuaternion();
        Assert.True(
            relative.AngleTo(Quaternion.Identity) <= 0.000001f,
            $"'{eyeName}' must return to its authored neutral transform, but deviated by {relative.AngleTo(Quaternion.Identity):F8} rad.");
    }

    private static void CollectAnimationPlayers(Node node, List<AnimationPlayer> results)
    {
        if (node is AnimationPlayer player)
        {
            results.Add(player);
        }

        foreach (Node child in node.GetChildren())
        {
            CollectAnimationPlayers(child, results);
        }
    }

    private static async Task AddRigToSceneTreeAsync(SceneTree sceneTree, Node rigRoot)
    {
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, rigRoot);
        await WaitForNextFrameAsync(sceneTree);
        Assert.True(rigRoot.IsInsideTree(), $"Expected '{rigRoot.Name}' to enter the test scene tree.");
    }

    private static Basis ComposeLocalBasis(Quaternion rotation, Vector3? scale)
        => scale is Vector3 scaleValue ? new Basis(rotation).ScaledLocal(scaleValue) : new Basis(rotation);

    /// <summary>
    /// Authoring options for the synthetic transform-gaze rig.
    /// </summary>
    private sealed record RigOptions(
        Vector3? EyeOriginScale = null,
        Vector3? LeftSocketScale = null,
        Vector3? RightSocketScale = null,
        Vector3? LeftEyeScale = null,
        Vector3? RightEyeScale = null,
        bool AssignLeftEye = true,
        bool AssignRightEye = true,
        bool ActivateAnimationTree = true,
        float LookSmoothingTime = 0f,
        float MinimumBlinkInterval = 2.5f,
        float MaximumBlinkInterval = 6f,
        float BlinkDuration = 0.3f);

    /// <summary>
    /// A minimal synthetic transform-gaze rig: a rotated head holding two differently parented eye nodes with
    /// distinct local offsets and orientations, an eye origin, an eye AnimationTree root, and an
    /// AnimationPlayer whose eyes library authors blendshape tracks only.
    /// </summary>
    private sealed class TransformGazeRig : IAsyncDisposable
    {
        private TransformGazeRig(
            Node3D root,
            Node3D head,
            Node3D leftEye,
            Node3D rightEye,
            Node3D eyeOrigin,
            StaticVisualCue gazeTarget,
            EyesBehaviour eyes,
            AnimationTree tree,
            Transform3D leftEyeAuthoredLocal,
            Transform3D rightEyeAuthoredLocal)
        {
            Root = root;
            Head = head;
            LeftEye = leftEye;
            RightEye = rightEye;
            EyeOrigin = eyeOrigin;
            GazeTarget = gazeTarget;
            Eyes = eyes;
            Tree = tree;
            LeftEyeAuthoredLocal = leftEyeAuthoredLocal;
            RightEyeAuthoredLocal = rightEyeAuthoredLocal;
        }

        public Node3D Root
        {
            get;
        }

        public Node3D Head
        {
            get;
        }

        public Node3D LeftEye
        {
            get;
        }

        public Node3D RightEye
        {
            get;
        }

        public Node3D EyeOrigin
        {
            get;
        }

        public StaticVisualCue GazeTarget
        {
            get;
        }

        public EyesBehaviour Eyes
        {
            get;
        }

        public AnimationTree Tree
        {
            get;
        }

        public Transform3D LeftEyeAuthoredLocal
        {
            get;
        }

        public Transform3D RightEyeAuthoredLocal
        {
            get;
        }

        public static async Task<TransformGazeRig> CreateAsync(SceneTree sceneTree, RigOptions? options = null)
        {
            options ??= new RigOptions();
            Node3D root = new()
            {
                Name = "EyesTransformRig",
            };
            Node3D head = new()
            {
                Name = "Head",
                Transform = new Transform3D(
                    new Basis(Quaternion.FromEuler(new Vector3(0.18f, 0.52f, -0.09f))),
                    new Vector3(0.3f, 1.6f, -0.4f)),
            };
            Node3D leftSocket = new()
            {
                Name = "LeftEyeSocket",
                Transform = new Transform3D(
                    ComposeLocalBasis(Quaternion.FromEuler(new Vector3(0f, 0f, 0.3f)), options.LeftSocketScale),
                    new Vector3(-0.08f, 0.02f, 0.05f)),
            };
            Node3D rightSocket = new()
            {
                Name = "RightEyeSocket",
                Transform = new Transform3D(
                    ComposeLocalBasis(Quaternion.FromEuler(new Vector3(-0.22f, 0.15f, 0f)), options.RightSocketScale),
                    new Vector3(0.08f, 0.02f, 0.05f)),
            };
            Node3D leftEye = new()
            {
                Name = "LeftEye",
                Transform = new Transform3D(
                    ComposeLocalBasis(Quaternion.FromEuler(new Vector3(-0.08f, 0.12f, 0.05f)), options.LeftEyeScale),
                    new Vector3(-0.02f, 0.01f, 0.03f)),
            };
            Node3D rightEye = new()
            {
                Name = "RightEye",
                Transform = new Transform3D(
                    ComposeLocalBasis(Quaternion.FromEuler(new Vector3(0.1f, -0.14f, -0.05f)), options.RightEyeScale),
                    new Vector3(0.02f, -0.01f, 0.02f)),
            };
            Node3D eyeOrigin = new()
            {
                Name = "EyeOrigin",
                Transform = new Transform3D(
                    ComposeLocalBasis(Quaternion.FromEuler(new Vector3(0f, 0.1f, 0f)), options.EyeOriginScale),
                    Vector3.Zero),
            };
            StaticVisualCue gazeTarget = new()
            {
                Name = "GazeTarget",
            };
            EyesBehaviour eyes = new()
            {
                Name = "Eyes",
                EyeOrigin = eyeOrigin,
                MaxHorizontalAngleDegrees = 35f,
                MaxVerticalAngleDegrees = 25f,
                SaccadeInterval = 0f,
                SaccadeAmplitude = 0f,
                LookSmoothingTime = options.LookSmoothingTime,
                MinimumBlinkInterval = options.MinimumBlinkInterval,
                MaximumBlinkInterval = options.MaximumBlinkInterval,
                BlinkDuration = options.BlinkDuration,
            };
            AnimationTree tree = new()
            {
                TreeRoot = Assert.IsType<AnimationNodeBlendTree>(ResourceLoader.Load(PlayerAnimationTreeRootPath), exactMatch: false),
            };

            leftSocket.AddChild(leftEye);
            rightSocket.AddChild(rightEye);
            head.AddChild(leftSocket);
            head.AddChild(rightSocket);
            head.AddChild(eyeOrigin);
            root.AddChild(head);
            root.AddChild(CreateEyeAnimationPlayer());
            root.AddChild(eyes);
            root.AddChild(gazeTarget);

            await AddRigToSceneTreeAsync(sceneTree, root);

            // Capture the authored neutrals before the gaze backend activates, since activation may already
            // apply the fallback micro-rotation on the first processed frame.
            Transform3D leftEyeAuthoredLocal = leftEye.Transform;
            Transform3D rightEyeAuthoredLocal = rightEye.Transform;

            if (options.AssignLeftEye)
            {
                eyes.LeftEye = leftEye;
            }

            if (options.AssignRightEye)
            {
                eyes.RightEye = rightEye;
            }

            if (options.ActivateAnimationTree)
            {
                eyes.AnimationTree = tree;
            }

            if (options.ActivateAnimationTree && options.AssignLeftEye && options.AssignRightEye)
            {
                await WaitForFramesAsync(sceneTree, 2);
            }

            return new TransformGazeRig(
                root,
                head,
                leftEye,
                rightEye,
                eyeOrigin,
                gazeTarget,
                eyes,
                tree,
                leftEyeAuthoredLocal,
                rightEyeAuthoredLocal);
        }

        public Node3D AddReplacementEye(string name, Vector3 localPosition)
        {
            Node3D eye = new()
            {
                Name = name,
                Transform = new Transform3D(
                    new Basis(Quaternion.FromEuler(new Vector3(0.06f, -0.1f, 0.02f))),
                    localPosition),
            };
            Head.AddChild(eye);
            return eye;
        }

        public async ValueTask DisposeAsync()
        {
            Root.QueueFree();
            Tree.QueueFree();
            await WaitForNextFrameAsync(GetSceneTree());
        }

        /// <summary>
        /// Creates the fixture AnimationPlayer whose eyes library authors blendshape tracks only, matching
        /// the blink-track authoring the transform backend still requires.
        /// </summary>
        private static AnimationPlayer CreateEyeAnimationPlayer()
        {
            AnimationPlayer player = new()
            {
                Name = "AnimationPlayer",
            };
            AnimationLibrary library = new();
            _ = library.AddAnimation(
                StripEyesLibraryPrefix(EyesAnimationTreePaths.BlinkAnimationName),
                CreateBlendShapeAnimation(EyesAnimationTreePaths.EyeBlinkLeftBlendShapeName, EyesAnimationTreePaths.EyeBlinkRightBlendShapeName));
            _ = library.AddAnimation(
                StripEyesLibraryPrefix(EyesAnimationTreePaths.HorizontalLookAnimationName),
                CreateBlendShapeAnimation(EyesAnimationTreePaths.EyeLookInLeftBlendShapeName));
            _ = library.AddAnimation(
                StripEyesLibraryPrefix(EyesAnimationTreePaths.VerticalLookAnimationName),
                CreateBlendShapeAnimation(EyesAnimationTreePaths.EyeLookUpLeftBlendShapeName));
            _ = player.AddAnimationLibrary(new StringName("eyes"), library);
            return player;
        }

        private static Animation CreateBlendShapeAnimation(params string[] blendShapeNames)
        {
            Animation animation = new();
            foreach (string blendShapeName in blendShapeNames)
            {
                int trackIndex = animation.AddTrack(Animation.TrackType.BlendShape);
                animation.TrackSetPath(trackIndex, new NodePath($"Face:{blendShapeName}"));
                _ = animation.BlendShapeTrackInsertKey(trackIndex, 0.0, 0.0f);
            }

            return animation;
        }

        private static StringName StripEyesLibraryPrefix(string animationName)
            => new(animationName[(animationName.IndexOf('/') + 1)..]);
    }
}
