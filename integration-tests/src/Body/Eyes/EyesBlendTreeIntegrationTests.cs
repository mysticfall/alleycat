using AlleyCat.Body.Eyes;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Body.Eyes;

/// <summary>
/// Integration coverage for BODY-004 Eyes reference tree resources and controller behaviour.
/// </summary>
public sealed class EyesBlendTreeIntegrationTests
{
    private const string PlayerAnimationTreeRootPath = "res://assets/characters/reference/female/animation_tree_root_player.tres";
    private const string NpcAnimationTreeRootPath = "res://assets/characters/reference/female/animation_tree_root_npc.tres";
    private const string ReferenceFemaleScenePath = "res://assets/characters/reference/female/reference_female.tscn";
    private const string ReferenceFemaleBaseScenePath = "res://assets/characters/reference/female/reference_female_base.tscn";
    private const string ReferencePlayerScenePath = "res://assets/characters/reference/player.tscn";
    private const string ReferenceNpcScenePath = "res://assets/characters/reference/female_reference_npc.tscn";
    private const string MirrorRoomScenePath = "res://assets/testing/mirror_room/mirror_room.tscn";
    private const string EyesPhotoboothScenePath = "res://tests/body/eyes/eyes_visual_test.tscn";
    private const string HorizontalLookAnimationResourcePath = "res://assets/characters/reference/female/animations/eyes/eyes_right_left.tres";
    private const string VerticalLookAnimationResourcePath = "res://assets/characters/reference/female/animations/eyes/eyes_up_down.tres";
    private const string BlinkAnimationResourcePath = "res://assets/characters/reference/female/animations/eyes/eyes_blink.tres";
    private const float BlinkClipLengthSeconds = 0.3f;
    private const float BlinkClosedPeakSeconds = 0.15f;

    /// <summary>
    /// Verifies player and NPC roots expose the same eye partial blend graph after the hand overlays.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemalePlayerTree_ConfiguresEyeLookAndBlinkPartialBlends()
        => AssertEyeBlendTree(PlayerAnimationTreeRootPath);

    /// <summary>
    /// Verifies the NPC root exposes the eye partial blend graph after the hand overlays.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemaleNpcTree_ConfiguresEyeLookAndBlinkPartialBlends()
        => AssertEyeBlendTree(NpcAnimationTreeRootPath);

    /// <summary>
    /// Verifies the reference and dependent scenes still load after eye animation library wiring.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemaleAndDependentScenes_LoadWithEyeAnimationLibraryWiring()
    {
        AssertSceneInstantiates(ReferenceFemaleScenePath);
        AssertSceneInstantiates(ReferenceFemaleBaseScenePath);
        AssertSceneInstantiates(ReferencePlayerScenePath);
        AssertSceneInstantiates(ReferenceNpcScenePath);
        AssertSceneInstantiates(EyesPhotoboothScenePath);
    }

    /// <summary>
    /// Verifies authored reference meshes start from neutral eye look blend-shape values.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemaleBaseScene_StartsWithNeutralEyeLookDownBlendShapes()
    {
        PackedScene scene = Assert.IsType<PackedScene>(ResourceLoader.Load(ReferenceFemaleBaseScenePath), exactMatch: false);
        Node instance = scene.Instantiate();
        try
        {
            AssertNeutralEyeLookDown(instance.GetNode("Female_export/GeneralSkeleton/Female_body_export"));
            AssertNeutralEyeLookDown(instance.GetNode("Female_export/GeneralSkeleton/Female_eyelashes01_export"));
            AssertNeutralEyeLookDown(instance.GetNode("Female_export/GeneralSkeleton/Female_high-poly_export"));
        }
        finally
        {
            instance.Free();
        }
    }

    private static void AssertEyeBlendTree(string treePath)
    {
        AnimationNodeBlendTree root = Assert.IsType<AnimationNodeBlendTree>(
            ResourceLoader.Load(treePath),
            exactMatch: false);

        AssertEyeBlendFilters(
            root,
            EyesAnimationTreePaths.HorizontalLookBlendNode,
            EyesAnimationTreePaths.GetHorizontalLookBlendShapeFilterPaths(),
            EyesAnimationTreePaths.GetVerticalLookBlendShapeFilterPaths(),
            EyesAnimationTreePaths.GetBlinkBlendShapeFilterPaths());
        AssertEyeBlendFilters(
            root,
            EyesAnimationTreePaths.VerticalLookBlendNode,
            EyesAnimationTreePaths.GetVerticalLookBlendShapeFilterPaths(),
            EyesAnimationTreePaths.GetHorizontalLookBlendShapeFilterPaths(),
            EyesAnimationTreePaths.GetBlinkBlendShapeFilterPaths());
        AssertAnimation(root, EyesAnimationTreePaths.HorizontalLookAnimationNode, EyesAnimationTreePaths.HorizontalLookAnimationName);
        AssertAnimation(root, EyesAnimationTreePaths.VerticalLookAnimationNode, EyesAnimationTreePaths.VerticalLookAnimationName);
        AssertAnimation(root, EyesAnimationTreePaths.BlinkAnimationNode, EyesAnimationTreePaths.BlinkAnimationName);
        AssertTimeSeek(root, EyesAnimationTreePaths.HorizontalLookSeekNode);
        AssertTimeSeek(root, EyesAnimationTreePaths.VerticalLookSeekNode);
        AssertBlinkOneShot(root, EyesAnimationTreePaths.BlinkOneShotNode);
        AssertTimeScale(root, EyesAnimationTreePaths.BlinkTimeScaleNode);

        AssertConnection(root, EyesAnimationTreePaths.HorizontalLookSeekNode, 0, EyesAnimationTreePaths.HorizontalLookAnimationNode);
        AssertConnection(root, EyesAnimationTreePaths.HorizontalLookBlendNode, 1, EyesAnimationTreePaths.HorizontalLookSeekNode);
        AssertConnection(root, EyesAnimationTreePaths.VerticalLookSeekNode, 0, EyesAnimationTreePaths.VerticalLookAnimationNode);
        AssertConnection(root, EyesAnimationTreePaths.VerticalLookBlendNode, 0, EyesAnimationTreePaths.HorizontalLookBlendNode);
        AssertConnection(root, EyesAnimationTreePaths.VerticalLookBlendNode, 1, EyesAnimationTreePaths.VerticalLookSeekNode);
        AssertConnection(root, EyesAnimationTreePaths.BlinkOneShotNode, 0, EyesAnimationTreePaths.VerticalLookBlendNode);
        AssertConnection(root, EyesAnimationTreePaths.BlinkOneShotNode, 1, EyesAnimationTreePaths.BlinkTimeScaleNode);
        AssertConnection(root, EyesAnimationTreePaths.BlinkTimeScaleNode, 0, EyesAnimationTreePaths.BlinkAnimationNode);
        AssertConnection(root, "output", 0, EyesAnimationTreePaths.BlinkOneShotNode);
    }

    private static void AssertSceneInstantiates(string scenePath)
    {
        PackedScene scene = Assert.IsType<PackedScene>(ResourceLoader.Load(scenePath), exactMatch: false);
        Node instance = scene.Instantiate();
        try
        {
            Assert.NotNull(instance);
        }
        finally
        {
            instance.Free();
        }
    }

    private static void AssertNeutralEyeLookDown(Node node)
    {
        Assert.Equal(0f, node.Get("blend_shapes/eyeLookDownLeft").AsSingle(), precision: 5);
        Assert.Equal(0f, node.Get("blend_shapes/eyeLookDownRight").AsSingle(), precision: 5);
    }

    /// <summary>
    /// Verifies look animation resources remain normalised so 0.5 seconds is neutral.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceLookAnimations_UseNormalisedOneSecondTimelines()
    {
        AssertNormalisedLookAnimation(HorizontalLookAnimationResourcePath);
        AssertNormalisedLookAnimation(VerticalLookAnimationResourcePath);
    }

    /// <summary>
    /// Verifies the blink animation remains authored in seconds with a closed midpoint peak.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceBlinkAnimation_UsesAuthoredSecondsTimelineWithClosedMidpoint()
    {
        Animation animation = Assert.IsType<Animation>(ResourceLoader.Load(BlinkAnimationResourcePath), exactMatch: false);
        Assert.Equal(BlinkClipLengthSeconds, animation.Length, precision: 5);

        for (int trackIndex = 0; trackIndex < animation.GetTrackCount(); trackIndex++)
        {
            Assert.Equal(Animation.TrackType.BlendShape, animation.TrackGetType(trackIndex));
            Assert.Equal(3, animation.TrackGetKeyCount(trackIndex));
            Assert.Equal(0f, (float)animation.TrackGetKeyTime(trackIndex, 0), precision: 5);
            Assert.Equal(BlinkClosedPeakSeconds, (float)animation.TrackGetKeyTime(trackIndex, 1), precision: 5);
            Assert.Equal(BlinkClipLengthSeconds, (float)animation.TrackGetKeyTime(trackIndex, 2), precision: 5);
            Assert.Equal(0f, animation.TrackGetKeyValue(trackIndex, 0).AsSingle(), precision: 5);
            Assert.Equal(1f, animation.TrackGetKeyValue(trackIndex, 1).AsSingle(), precision: 5);
            Assert.Equal(0f, animation.TrackGetKeyValue(trackIndex, 2).AsSingle(), precision: 5);
        }
    }

    /// <summary>
    /// Verifies the controller writes look seek times and triggers blink one-shot playback.
    /// </summary>
    [Headless]
    [Fact]
    public void EyesController_WritesLookSeekAndBlinkOneShotParameters()
    {
        AnimationTree tree = new()
        {
            TreeRoot = Assert.IsType<AnimationNodeBlendTree>(ResourceLoader.Load(PlayerAnimationTreeRootPath), exactMatch: false),
        };
        var target = new Node3D();
        EyesController controller = new(tree)
        {
            LookTarget = target,
            EyeOriginGlobalTransform = new Transform3D(Basis.Identity, new Vector3(-1f, 0f, 1f)),
            LookSmoothingTime = 0f,
            MinimumBlinkInterval = 0f,
            MaximumBlinkInterval = 0f,
            BlinkDuration = 0.2f,
        };

        controller.Update(10.0);

        Assert.Equal(0f, tree.Get(EyesAnimationTreePaths.GetHorizontalLookSeekParameter()).AsSingle());
        Assert.Equal(0.5f, tree.Get(EyesAnimationTreePaths.GetVerticalLookSeekParameter()).AsSingle());
        Assert.Equal(1f, tree.Get(EyesAnimationTreePaths.GetHorizontalLookBlendParameter()).AsSingle());
        Assert.Equal(1f, tree.Get(EyesAnimationTreePaths.GetVerticalLookBlendParameter()).AsSingle());
        Assert.Equal(1.5f, tree.Get(EyesAnimationTreePaths.GetBlinkTimeScaleParameter()).AsSingle(), precision: 5);
        Assert.Equal(
            (int)AnimationNodeOneShot.OneShotRequest.Fire,
            tree.Get(EyesAnimationTreePaths.GetBlinkOneShotRequestParameter()).AsInt32());
    }

    /// <summary>
    /// Verifies the mirror-room inherited scene override reaches the runtime controller and drives look seek values.
    /// </summary>
    [Headless]
    [Fact]
    public async Task MirrorRoomFemaleEyesBehaviour_SceneAssignedLookTargetDrivesSeekTimes()
    {
        SceneTree sceneTree = GetSceneTree();
        PackedScene scene = LoadPackedScene(MirrorRoomScenePath);
        Node root = scene.Instantiate();

        try
        {
            sceneTree.Root.AddChild(root);
            await WaitForFramesAsync(sceneTree, 2);

            Node eyes = root.GetNode("Actors/Female/EyesBehaviour");
            AnimationTree tree = root.GetNode<AnimationTree>("Actors/Female/AnimationTree");

            Node3D eyeOrigin = Assert.IsType<Node3D>(eyes.Get("EyeOrigin").AsGodotObject(), exactMatch: false);
            Node3D lookTarget = Assert.IsType<Node3D>(eyes.Get("LookTarget").AsGodotObject(), exactMatch: false);

            eyes.Set("LookSmoothingTime", 0f);
            lookTarget.TopLevel = true;
            lookTarget.GlobalPosition = eyeOrigin.GlobalTransform * new Vector3(10f, 10f, -10f);

            await WaitForNextFrameAsync(sceneTree);

            float horizontalSeek = tree.Get(EyesAnimationTreePaths.GetHorizontalLookSeekParameter()).AsSingle();
            float verticalSeek = tree.Get(EyesAnimationTreePaths.GetVerticalLookSeekParameter()).AsSingle();

            Assert.NotEqual(EyesLookMath.NeutralSeekTimeSeconds, horizontalSeek, precision: 5);
            Assert.NotEqual(EyesLookMath.NeutralSeekTimeSeconds, verticalSeek, precision: 5);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the mirror-room target keeps the eye look overlays enabled after scene overrides are applied.
    /// </summary>
    [Headless]
    [Fact]
    public async Task MirrorRoomFemaleEyesBehaviour_PlayerFaceTargetKeepsLookBlendsEnabled()
    {
        SceneTree sceneTree = GetSceneTree();
        PackedScene scene = LoadPackedScene(MirrorRoomScenePath);
        Node root = scene.Instantiate();

        try
        {
            sceneTree.Root.AddChild(root);
            await WaitForFramesAsync(sceneTree, 4);

            Node eyes = root.GetNode("Actors/Female/EyesBehaviour");
            AnimationTree tree = root.GetNode<AnimationTree>("Actors/Female/AnimationTree");
            Node3D eyeOrigin = Assert.IsType<Node3D>(eyes.Get("EyeOrigin").AsGodotObject(), exactMatch: false);
            Node3D lookTarget = Assert.IsType<Node3D>(eyes.Get("LookTarget").AsGodotObject(), exactMatch: false);

            eyes.Set("LookSmoothingTime", 0f);
            await WaitForFramesAsync(sceneTree, 3);

            Transform3D eyeTransform = eyeOrigin.GlobalTransform;
            Vector3 targetPosition = lookTarget.GlobalPosition;
            Vector2 expectedSeekTimes = EyesLookMath.ResolveLookSeekTimes(
                eyeTransform,
                targetPosition,
                Mathf.DegToRad(35f),
                Mathf.DegToRad(25f));

            Assert.Equal(EyesLookMath.NeutralSeekTimeSeconds, expectedSeekTimes.X, precision: 3);
            Assert.Equal(EyesLookMath.NeutralSeekTimeSeconds, expectedSeekTimes.Y, precision: 3);
            Assert.Equal(1f, tree.Get(EyesAnimationTreePaths.GetHorizontalLookBlendParameter()).AsSingle(), precision: 5);
            Assert.Equal(1f, tree.Get(EyesAnimationTreePaths.GetVerticalLookBlendParameter()).AsSingle(), precision: 5);

            lookTarget.TopLevel = true;
            lookTarget.GlobalPosition = eyeOrigin.GlobalTransform * new Vector3(10f, 10f, -10f);
            await WaitForFramesAsync(sceneTree, 3);

            Assert.Equal(1f, tree.Get(EyesAnimationTreePaths.GetHorizontalLookBlendParameter()).AsSingle(), precision: 5);
            Assert.Equal(1f, tree.Get(EyesAnimationTreePaths.GetVerticalLookBlendParameter()).AsSingle(), precision: 5);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static void AssertEyeBlendFilters(
        AnimationNodeBlendTree root,
        string nodeName,
        IReadOnlyList<NodePath> expectedFilters,
        params IReadOnlyList<NodePath>[] excludedFilterGroups)
    {
        AnimationNodeBlend2 blend = Assert.IsType<AnimationNodeBlend2>(root.GetNode(nodeName), exactMatch: false);
        Assert.True(blend.FilterEnabled);

        foreach (NodePath filterPath in expectedFilters)
        {
            Assert.True(blend.IsPathFiltered(filterPath), $"Expected {nodeName} to filter {filterPath}.");
        }

        foreach (IReadOnlyList<NodePath> excludedFilterGroup in excludedFilterGroups)
        {
            foreach (NodePath filterPath in excludedFilterGroup)
            {
                Assert.False(blend.IsPathFiltered(filterPath), $"Expected {nodeName} not to filter unrelated eye path {filterPath}.");
            }
        }

        Assert.False(blend.IsPathFiltered(new NodePath("%GeneralSkeleton:Head")));
        Assert.False(blend.IsPathFiltered(new NodePath("%GeneralSkeleton:LeftHand")));
    }

    private static void AssertAnimation(AnimationNodeBlendTree root, string nodeName, string animationName)
    {
        AnimationNodeAnimation animation = Assert.IsType<AnimationNodeAnimation>(root.GetNode(nodeName), exactMatch: false);
        Assert.Equal(new StringName(animationName), animation.Animation);
    }

    private static void AssertTimeSeek(AnimationNodeBlendTree root, string nodeName)
        => Assert.IsType<AnimationNodeTimeSeek>(root.GetNode(nodeName), exactMatch: false);

    private static void AssertBlinkOneShot(AnimationNodeBlendTree root, string nodeName)
    {
        AnimationNodeOneShot oneShot = Assert.IsType<AnimationNodeOneShot>(root.GetNode(nodeName), exactMatch: false);
        Assert.True(oneShot.FilterEnabled);

        foreach (NodePath filterPath in EyesAnimationTreePaths.GetBlinkBlendShapeFilterPaths())
        {
            Assert.True(oneShot.IsPathFiltered(filterPath), $"Expected {nodeName} to filter blink path {filterPath}.");
        }

        foreach (NodePath filterPath in EyesAnimationTreePaths.GetHorizontalLookBlendShapeFilterPaths())
        {
            Assert.False(oneShot.IsPathFiltered(filterPath), $"Expected {nodeName} not to filter horizontal look path {filterPath}.");
        }

        foreach (NodePath filterPath in EyesAnimationTreePaths.GetVerticalLookBlendShapeFilterPaths())
        {
            Assert.False(oneShot.IsPathFiltered(filterPath), $"Expected {nodeName} not to filter vertical look path {filterPath}.");
        }

        Assert.False(oneShot.IsPathFiltered(new NodePath("%GeneralSkeleton:Hips")));
        Assert.False(oneShot.IsPathFiltered(new NodePath("%GeneralSkeleton:Head")));
    }

    private static void AssertTimeScale(AnimationNodeBlendTree root, string nodeName)
        => Assert.IsType<AnimationNodeTimeScale>(root.GetNode(nodeName), exactMatch: false);

    private static void AssertNormalisedLookAnimation(string animationPath)
    {
        Animation animation = Assert.IsType<Animation>(ResourceLoader.Load(animationPath), exactMatch: false);
        Assert.Equal(1f, animation.Length, precision: 5);

        for (int trackIndex = 0; trackIndex < animation.GetTrackCount(); trackIndex++)
        {
            Assert.Equal(Animation.TrackType.BlendShape, animation.TrackGetType(trackIndex));
            Assert.Equal(3, animation.TrackGetKeyCount(trackIndex));
            Assert.Equal(EyesLookMath.MinimumSeekTimeSeconds, (float)animation.TrackGetKeyTime(trackIndex, 0), precision: 5);
            Assert.Equal(EyesLookMath.NeutralSeekTimeSeconds, (float)animation.TrackGetKeyTime(trackIndex, 1), precision: 5);
            Assert.Equal(EyesLookMath.MaximumSeekTimeSeconds, (float)animation.TrackGetKeyTime(trackIndex, 2), precision: 5);
            Assert.Equal(0f, animation.TrackGetKeyValue(trackIndex, 1).AsSingle(), precision: 5);
        }
    }

    private static void AssertConnection(
        AnimationNodeBlendTree tree,
        string inputNode,
        int inputIndex,
        string outputNode)
    {
        Godot.Collections.Array connections = tree.Get("node_connections").AsGodotArray();
        for (int index = 0; index < connections.Count; index += 3)
        {
            if (connections[index].AsStringName() == new StringName(inputNode)
                && connections[index + 1].AsInt32() == inputIndex
                && connections[index + 2].AsStringName() == new StringName(outputNode))
            {
                return;
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"Expected connection {inputNode}[{inputIndex}] <- {outputNode} in {tree.ResourceName}.");
    }
}
