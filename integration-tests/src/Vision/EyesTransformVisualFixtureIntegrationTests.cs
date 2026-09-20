using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Vision;

/// <summary>
/// Loads the VISION-001 transform-gaze photobooth fixture scene and verifies its
/// non-visual behaviour end to end: valid both-pair eye authoring, active transform
/// mode, coordinated clamped rotation towards a directional target, and a forced blink
/// while the blendshape look blends stay zero.
/// </summary>
public sealed class EyesTransformVisualFixtureIntegrationTests
{
    private const string FixtureScenePath = "res://tests/vision/eyes_transform_visual_test.tscn";
    private const float HorizontalLimitRadians = 0.6108652f;
    private const float VerticalLimitRadians = 0.4363323f;
    private const float AngleToleranceRadians = 0.001f;

    /// <summary>
    /// Verifies the fixture activates the transform backend from its authored eye-node pair,
    /// rests at the exact authored neutral pose, rotates both eyes within the shared limits
    /// towards a directional target, and blinks without emitting blendshape look output.
    /// </summary>
    [Headless]
    [Fact]
    public async Task EyesTransformVisualFixture_ActivatesTransformModeRotatesWithinLimitsAndBlinks()
    {
        SceneTree sceneTree = GetSceneTree();
        PackedScene fixtureScene = ResourceLoader.Load<PackedScene>(FixtureScenePath);
        Assert.NotNull(fixtureScene);

        Node fixtureRoot = fixtureScene.Instantiate();
        Node3D rig = fixtureRoot.GetNode<Node3D>("Subject/EyesTransformRig");
        EyesBehaviour eyes = rig.GetNode<EyesBehaviour>("Eyes");
        Node3D leftEye = rig.GetNode<Node3D>("Head/LeftEyeSocket/LeftEye");
        Node3D rightEye = rig.GetNode<Node3D>("Head/RightEyeSocket/RightEye");
        Node3D eyeOrigin = rig.GetNode<Node3D>("Head/EyeOrigin");
        AnimationTree tree = rig.GetNode<AnimationTree>("AnimationTree");
        StaticVisualCue gazeTarget = rig.GetNode<StaticVisualCue>("GazeTarget");

        Assert.Same(leftEye, eyes.LeftEye);
        Assert.Same(rightEye, eyes.RightEye);
        Assert.Same(eyeOrigin, eyes.EyeOrigin);
        Assert.Same(tree, eyes.AnimationTree);
        Assert.NotEqual(leftEye.Transform.Origin, rightEye.Transform.Origin);
        AssertBasisScaleNonUniform(leftEye.Transform.Basis, "left eye");

        // Capture the authored neutrals before the scene enters the tree and activates.
        Transform3D leftEyeAuthoredLocal = leftEye.Transform;
        Transform3D rightEyeAuthoredLocal = rightEye.Transform;
        eyes.ValidateEyeNodePairAuthoring();

        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, fixtureRoot);
        await WaitForNextFrameAsync(sceneTree);
        await WaitForFramesAsync(sceneTree, 2);

        float initialHorizontalSeek = tree.Get(EyesAnimationTreePaths.GetHorizontalLookSeekParameter()).AsSingle();
        float initialVerticalSeek = tree.Get(EyesAnimationTreePaths.GetVerticalLookSeekParameter()).AsSingle();
        AssertLookBlendsAreZero(tree);
        Assert.Equal(leftEyeAuthoredLocal, leftEye.Transform);
        Assert.Equal(rightEyeAuthoredLocal, rightEye.Transform);

        eyes.LookSmoothingTime = 0f;
        (float expectedHorizontal, float expectedVertical) = PlaceGazeTarget(eyeOrigin, gazeTarget, new Vector3(1f, 0.35f, -0.25f));
        eyes.SetLookTarget(gazeTarget);
        eyes.RefreshLookParametersDeferred();
        await WaitForFramesAsync(sceneTree, 2);

        AssertLookBlendsAreZero(tree);
        Assert.Equal(initialHorizontalSeek, tree.Get(EyesAnimationTreePaths.GetHorizontalLookSeekParameter()).AsSingle(), precision: 7);
        Assert.Equal(initialVerticalSeek, tree.Get(EyesAnimationTreePaths.GetVerticalLookSeekParameter()).AsSingle(), precision: 7);

        Quaternion leftDelta = ResolveWorldGazeDelta(leftEye, leftEyeAuthoredLocal);
        Quaternion rightDelta = ResolveWorldGazeDelta(rightEye, rightEyeAuthoredLocal);
        Assert.True(
            leftDelta.AngleTo(rightDelta) <= 0.005f,
            $"Fixture eyes must rotate as a coordinated pair (divergence {leftDelta.AngleTo(rightDelta):F6} rad).");
        (float actualHorizontal, float actualVertical) = ExtractGazeAnglesInOriginFrame(leftDelta, eyeOrigin);
        Assert.True(
            Mathf.Abs(actualHorizontal - expectedHorizontal) <= AngleToleranceRadians,
            $"Horizontal gaze {actualHorizontal:F6} rad does not match the expected clamped {expectedHorizontal:F6} rad.");
        Assert.True(
            Mathf.Abs(actualVertical - expectedVertical) <= AngleToleranceRadians,
            $"Vertical gaze {actualVertical:F6} rad does not match the expected clamped {expectedVertical:F6} rad.");
        Assert.True(Mathf.Abs(actualHorizontal) <= HorizontalLimitRadians + AngleToleranceRadians, "Horizontal gaze exceeded the shared limit.");
        Assert.True(Mathf.Abs(actualVertical) <= VerticalLimitRadians + AngleToleranceRadians, "Vertical gaze exceeded the shared limit.");
        Assert.Equal(leftEyeAuthoredLocal.Origin, leftEye.Transform.Origin);
        Assert.Equal(rightEyeAuthoredLocal.Origin, rightEye.Transform.Origin);

        eyes.TriggerBlink();
        // The fixture's AnimationTree is active and processes the one-shot on the next
        // frame, so the Fire request is asserted synchronously and the activated state
        // after a frame.
        Assert.Equal(
            (int)AnimationNodeOneShot.OneShotRequest.Fire,
            tree.Get(EyesAnimationTreePaths.GetBlinkOneShotRequestParameter()).AsInt32());
        await WaitForFramesAsync(sceneTree, 1);
        Assert.True((bool)tree.Get("parameters/EyesBlinkOneShot/active"), "Forced blink must drive the blink one-shot node.");
        AssertLookBlendsAreZero(tree);
        Assert.Equal(initialHorizontalSeek, tree.Get(EyesAnimationTreePaths.GetHorizontalLookSeekParameter()).AsSingle(), precision: 7);
        Assert.Equal(initialVerticalSeek, tree.Get(EyesAnimationTreePaths.GetVerticalLookSeekParameter()).AsSingle(), precision: 7);

        fixtureRoot.QueueFree();
        await WaitForNextFrameAsync(sceneTree);
    }

    private static (float Horizontal, float Vertical) PlaceGazeTarget(Node3D eyeOrigin, Node3D gazeTarget, Vector3 localDirection)
    {
        Quaternion originRotation = eyeOrigin.GlobalTransform.Basis.GetRotationQuaternion();
        gazeTarget.GlobalPosition = eyeOrigin.GlobalPosition + (originRotation * (localDirection.Normalized() * 6f));

        Basis originBasis = eyeOrigin.GlobalTransform.Basis;
        Vector3 right = (originBasis * Vector3.Right).Normalized();
        Vector3 up = (originBasis * Vector3.Up).Normalized();
        Vector3 back = (originBasis * Vector3.Back).Normalized();
        Vector3 direction = (gazeTarget.GlobalPosition - eyeOrigin.GlobalPosition).Normalized();
        float horizontalComponent = direction.Dot(right);
        float verticalComponent = direction.Dot(up);
        float forwardComponent = -direction.Dot(back);
        return (
            Mathf.Clamp(Mathf.Atan2(horizontalComponent, forwardComponent), -HorizontalLimitRadians, HorizontalLimitRadians),
            Mathf.Clamp(
                Mathf.Atan2(verticalComponent, Mathf.Sqrt((horizontalComponent * horizontalComponent) + (forwardComponent * forwardComponent))),
                -VerticalLimitRadians,
                VerticalLimitRadians));
    }

    private static Quaternion ResolveWorldGazeDelta(Node3D eye, Transform3D neutralLocalTransform)
    {
        Node3D parent = eye.GetParentOrNull<Node3D>()
            ?? throw new InvalidOperationException($"Eye '{eye.Name}' must have a Node3D parent.");
        Quaternion localDelta = (eye.Transform.Basis * neutralLocalTransform.Basis.Inverse()).GetRotationQuaternion();
        Quaternion parentRotation = parent.GlobalTransform.Basis.GetRotationQuaternion();
        return parentRotation * localDelta * parentRotation.Inverse();
    }

    private static (float Horizontal, float Vertical) ExtractGazeAnglesInOriginFrame(Quaternion worldGazeDelta, Node3D eyeOrigin)
    {
        Quaternion originRotation = eyeOrigin.GlobalTransform.Basis.GetRotationQuaternion();
        Quaternion originFrameDelta = originRotation.Inverse() * worldGazeDelta * originRotation;
        Vector3 gazeDirection = originFrameDelta * new Vector3(0f, 0f, -1f);
        return (
            Mathf.Atan2(gazeDirection.X, -gazeDirection.Z),
            Mathf.Atan2(gazeDirection.Y, Mathf.Sqrt((gazeDirection.X * gazeDirection.X) + (gazeDirection.Z * gazeDirection.Z))));
    }

    private static void AssertLookBlendsAreZero(AnimationTree tree)
    {
        Assert.Equal(0f, tree.Get(EyesAnimationTreePaths.GetHorizontalLookBlendParameter()).AsSingle(), precision: 5);
        Assert.Equal(0f, tree.Get(EyesAnimationTreePaths.GetVerticalLookBlendParameter()).AsSingle(), precision: 5);
    }

    private static void AssertBasisScaleNonUniform(Basis basis, string nodeName)
    {
        float xAxisLength = (basis * Vector3.Right).Length();
        float yAxisLength = (basis * Vector3.Up).Length();
        Assert.True(
            Mathf.Abs(xAxisLength - yAxisLength) > 0.00001f,
            $"'{nodeName}' fixture authoring must keep a non-uniform positive scale (found {xAxisLength:F4} x {yAxisLength:F4}).");
    }
}
