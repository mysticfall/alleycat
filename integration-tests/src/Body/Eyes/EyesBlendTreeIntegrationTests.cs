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
    private const string PlayerAnimationTreeRootPath = "res://assets/characters/templates/animation/animation_tree_root_player.tres";
    private const string NpcAnimationTreeRootPath = "res://assets/characters/templates/animation/animation_tree_root_npc.tres";
    private const string ReferenceFemaleScenePath = "res://assets/characters/templates/reference_female/reference_female_base.tscn";
    private const string ReferencePlayerScenePath = "res://assets/characters/reference/player.tscn";
    private const string ReferenceNpcScenePath = "res://assets/characters/reference/ally.tscn";
    private const string EyesPhotoboothScenePath = "res://tests/body/eyes/eyes_visual_test.tscn";
    private const string HorizontalLookAnimationResourcePath = "res://assets/characters/reference/female/animations/eyes/eyes_right_left.tres";
    private const string VerticalLookAnimationResourcePath = "res://assets/characters/reference/female/animations/eyes/eyes_up_down.tres";
    private const string BlinkAnimationResourcePath = "res://assets/characters/reference/female/animations/eyes/eyes_blink.tres";
    private const string ReferenceEyeSkeletonNodeName = "GeneralSkeleton";
    private const float BlinkClipLengthSeconds = 0.3f;
    private const float BlinkClosedPeakSeconds = 0.15f;
    private static readonly string[] _referenceEyeMeshNodeNames =
    [
        "Female_high-poly",
        "Female_eyelashes01",
        "Female_body",
    ];

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
        AssertSceneInstantiates(ReferencePlayerScenePath);
        AssertSceneInstantiates(ReferenceNpcScenePath);
        AssertSceneInstantiates(EyesPhotoboothScenePath);
    }

    /// <summary>
    /// Verifies authored reference meshes start from neutral eye look blend-shape values.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemaleScene_StartsWithNeutralEyeLookDownBlendShapes()
    {
        PackedScene scene = Assert.IsType<PackedScene>(ResourceLoader.Load(ReferenceFemaleScenePath), exactMatch: false);
        Node instance = scene.Instantiate();
        try
        {
            AssertNeutralEyeLookDown(instance.GetNode("Female/GeneralSkeleton/Female_body"));
            AssertNeutralEyeLookDown(instance.GetNode("Female/GeneralSkeleton/Female_eyelashes01"));
            AssertNeutralEyeLookDown(instance.GetNode("Female/GeneralSkeleton/Female_high-poly"));
        }
        finally
        {
            instance.Free();
        }
    }

    /// <summary>
    /// Verifies the authored eye component uses the shared tree root without duplicating filter setup at runtime.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ReferenceFemaleNpcScene_EyesBehaviour_UsesAuthoredAnimationTreeFilters()
    {
        SceneTree sceneTree = GetSceneTree();
        PackedScene scene = LoadPackedScene(ReferenceNpcScenePath);
        Node root = scene.Instantiate();

        try
        {
            await AddChildToRootAsync(sceneTree, root);
            await WaitForFramesAsync(sceneTree, 6);

            AnimationTree tree = root.GetNode<AnimationTree>("AnimationTree");
            AnimationPlayer player = root.GetNode<AnimationPlayer>("AnimationPlayer");
            AnimationNodeBlendTree treeRoot = Assert.IsType<AnimationNodeBlendTree>(tree.TreeRoot, exactMatch: false);

            Assert.NotNull(treeRoot);
            AssertEyeBlendFiltersExactly(
                treeRoot,
                EyesAnimationTreePaths.HorizontalLookBlendNode,
                GetAnimationTrackPaths(player, EyesAnimationTreePaths.HorizontalLookAnimationName),
                assertNoReferenceFemaleFilter: false);
            AssertEyeBlendFiltersExactly(
                treeRoot,
                EyesAnimationTreePaths.VerticalLookBlendNode,
                GetAnimationTrackPaths(player, EyesAnimationTreePaths.VerticalLookAnimationName),
                assertNoReferenceFemaleFilter: false);
            AssertEyeBlendFiltersExactly(
                treeRoot,
                EyesAnimationTreePaths.BlinkOneShotNode,
                GetAnimationTrackPaths(player, EyesAnimationTreePaths.BlinkAnimationName),
                assertNoReferenceFemaleFilter: false);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 12);
        }
    }

    /// <summary>
    /// Verifies reference-female runtime installation derives eye filters from the character's imported tracks.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ReferenceFemaleNpcScene_RuntimeInstall_UsesImportedEyeTrackFilters()
    {
        SceneTree sceneTree = GetSceneTree();
        PackedScene scene = LoadPackedScene(ReferenceNpcScenePath);
        Node root = scene.Instantiate();

        try
        {
            await AddChildToRootAsync(sceneTree, root);
            await WaitForFramesAsync(sceneTree, 4);

            EnsureCharacterRuntimeInstalled(root);

            AnimationTree tree = root.GetNode<AnimationTree>("AnimationTree");
            AnimationPlayer player = root.GetNode<AnimationPlayer>("AnimationPlayer");
            AnimationNodeBlendTree treeRoot = Assert.IsType<AnimationNodeBlendTree>(tree.TreeRoot, exactMatch: false);
            AssertEyeBlendFiltersExactly(
                treeRoot,
                EyesAnimationTreePaths.HorizontalLookBlendNode,
                GetAnimationTrackPaths(player, EyesAnimationTreePaths.HorizontalLookAnimationName),
                assertNoReferenceFemaleFilter: false);
            AssertEyeBlendFiltersExactly(
                treeRoot,
                EyesAnimationTreePaths.VerticalLookBlendNode,
                GetAnimationTrackPaths(player, EyesAnimationTreePaths.VerticalLookAnimationName),
                assertNoReferenceFemaleFilter: false);
            AssertEyeBlendFiltersExactly(
                treeRoot,
                EyesAnimationTreePaths.BlinkOneShotNode,
                GetAnimationTrackPaths(player, EyesAnimationTreePaths.BlinkAnimationName),
                assertNoReferenceFemaleFilter: false);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
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
            EyesAnimationTreePaths.BuildHorizontalLookBlendShapeFilterPaths(
                ReferenceEyeSkeletonNodeName,
                _referenceEyeMeshNodeNames),
            EyesAnimationTreePaths.BuildVerticalLookBlendShapeFilterPaths(
                ReferenceEyeSkeletonNodeName,
                _referenceEyeMeshNodeNames),
            EyesAnimationTreePaths.BuildBlinkBlendShapeFilterPaths(
                ReferenceEyeSkeletonNodeName,
                _referenceEyeMeshNodeNames));
        AssertEyeBlendFilters(
            root,
            EyesAnimationTreePaths.VerticalLookBlendNode,
            EyesAnimationTreePaths.BuildVerticalLookBlendShapeFilterPaths(
                ReferenceEyeSkeletonNodeName,
                _referenceEyeMeshNodeNames),
            EyesAnimationTreePaths.BuildHorizontalLookBlendShapeFilterPaths(
                ReferenceEyeSkeletonNodeName,
                _referenceEyeMeshNodeNames),
            EyesAnimationTreePaths.BuildBlinkBlendShapeFilterPaths(
                ReferenceEyeSkeletonNodeName,
                _referenceEyeMeshNodeNames));
        AssertAnimation(root, EyesAnimationTreePaths.HorizontalLookAnimationNode, EyesAnimationTreePaths.HorizontalLookAnimationName);
        AssertAnimation(root, EyesAnimationTreePaths.VerticalLookAnimationNode, EyesAnimationTreePaths.VerticalLookAnimationName);
        AssertAnimation(root, EyesAnimationTreePaths.BlinkAnimationNode, EyesAnimationTreePaths.BlinkAnimationName);
        AssertTimeSeek(root, EyesAnimationTreePaths.HorizontalLookSeekNode);
        AssertTimeSeek(root, EyesAnimationTreePaths.VerticalLookSeekNode);
        AssertBlinkOneShot(
            root,
            EyesAnimationTreePaths.BlinkOneShotNode,
            EyesAnimationTreePaths.BuildBlinkBlendShapeFilterPaths(
                ReferenceEyeSkeletonNodeName,
                _referenceEyeMeshNodeNames));
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
        EyesController controller = new(tree)
        {
            EyeOriginGlobalTransform = new Transform3D(Basis.Identity, new Vector3(-1f, 0f, 1f)),
            LookSmoothingTime = 0f,
            MinimumBlinkInterval = 0f,
            MaximumBlinkInterval = 0f,
            BlinkDuration = 0.2f,
        };

        controller.Update(10.0, Vector3.Zero);

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
    /// Verifies behaviour-level gaze resolution uses the assigned target and falls back to eye-origin forward.
    /// </summary>
    [Headless]
    [Fact]
    public async Task EyesBehaviour_ResolveWorldLookPoint_UsesTargetThenEyeForwardFallback()
    {
        SceneTree sceneTree = GetSceneTree();
        var root = new Node3D();
        var eyeOrigin = new Node3D();
        var target = new Node3D();
        var eyes = new TestEyesBehaviour
        {
            EyeOrigin = eyeOrigin,
        };
        root.AddChild(eyeOrigin);
        root.AddChild(target);
        root.AddChild(eyes);

        try
        {
            await AddChildToRootAsync(sceneTree, root);
            await WaitForFramesAsync(sceneTree, 2);

            var eyeTransform = new Transform3D(
                new Basis(Vector3.Up, Mathf.Pi / 2f),
                new Vector3(2f, 3f, 4f));
            eyeOrigin.GlobalTransform = eyeTransform;
            target.GlobalPosition = new Vector3(-3f, 5f, 7f);

            eyes.SetLookTarget(target);
            Assert.Equal(target.GlobalPosition, eyes.ResolveWorldLookPointForTest());

            eyes.ClearLookTarget();
            Vector3 actualFallback = eyes.ResolveWorldLookPointForTest();
            Vector3 expectedFallback = eyeTransform.Origin - eyeTransform.Basis.Z.Normalized();
            Assert.Equal(expectedFallback.X, actualFallback.X, precision: 5);
            Assert.Equal(expectedFallback.Y, actualFallback.Y, precision: 5);
            Assert.Equal(expectedFallback.Z, actualFallback.Z, precision: 5);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies behaviour-owned saccades remain bounded around assigned and fallback gaze anchors.
    /// </summary>
    [Headless]
    [Fact]
    public async Task EyesBehaviour_SaccadedLookPoint_RemainsBoundedAroundAssignedAndFallbackAnchors()
    {
        const float saccadeAmplitude = 0.05f;
        SceneTree sceneTree = GetSceneTree();
        var root = new Node3D();
        var eyeOrigin = new Node3D();
        var target = new Node3D();
        var eyes = new TestEyesBehaviour
        {
            EyeOrigin = eyeOrigin,
            SaccadeAmplitude = saccadeAmplitude,
            SaccadeSpeed = 10f,
            SaccadeInterval = 1f,
        };
        root.AddChild(eyeOrigin);
        root.AddChild(target);
        root.AddChild(eyes);

        try
        {
            await AddChildToRootAsync(sceneTree, root);
            await WaitForFramesAsync(sceneTree, 2);

            eyeOrigin.GlobalPosition = new Vector3(1f, 2f, 3f);
            target.GlobalPosition = new Vector3(3f, 4f, -5f);
            eyes.SetLookTarget(target);

            Vector3 assignedLookPoint = eyes.ResolveSaccadedLookPointForTesting(1.0);
            Assert.True(
                assignedLookPoint.DistanceTo(target.GlobalPosition) <= saccadeAmplitude + 0.0001f,
                "Assigned-target saccade offset exceeded configured amplitude.");

            eyes.ClearLookTarget();
            Vector3 fallbackAnchor = eyes.ResolveWorldLookPointForTest();
            Vector3 fallbackLookPoint = eyes.ResolveSaccadedLookPointForTesting(1.0);
            Assert.True(
                fallbackLookPoint.DistanceTo(fallbackAnchor) <= saccadeAmplitude + 0.0001f,
                "Fallback saccade offset exceeded configured amplitude.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a runtime-assigned reference NPC look target reaches the runtime controller and drives look seek values.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ReferenceFemaleNpcEyesBehaviour_RuntimeAssignedLookTargetDrivesSeekTimes()
    {
        SceneTree sceneTree = GetSceneTree();
        PackedScene scene = LoadPackedScene(ReferenceNpcScenePath);
        Node root = scene.Instantiate();

        try
        {
            await AddChildToRootAsync(sceneTree, root);
            await WaitForFramesAsync(sceneTree, 12);

            EnsureCharacterRuntimeInstalled(root);
            Node eyes = Assert.IsType<Node>(root.GetNodeOrNull("Eyes"), exactMatch: false);
            Node3D eyeOrigin = new()
            {
                Name = "RuntimeEyesOrigin",
                TopLevel = true,
            };
            Node3D lookTarget = new()
            {
                Name = "RuntimeEyesLookTarget",
                TopLevel = true,
            };
            root.AddChild(eyeOrigin);
            root.AddChild(lookTarget);

            eyes.Set("LookSmoothingTime", 0f);
            eyes.Set("EyeOrigin", eyeOrigin);
            lookTarget.GlobalPosition = eyeOrigin.GlobalTransform * new Vector3(10f, 10f, -10f);
            eyes.Set("LookTarget", lookTarget);
            _ = eyes.Call("RefreshLookParametersDeferred");
            await WaitForFramesAsync(sceneTree, 2);

            Assert.True(eyes.Call("HasRuntimeLookTarget").AsBool());

            float horizontalSeek = eyes.Call("GetHorizontalLookSeekTime").AsSingle();
            float verticalSeek = eyes.Call("GetVerticalLookSeekTime").AsSingle();

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
    /// Verifies reference NPC eye look overlays stay enabled for forward fallback and runtime target paths.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ReferenceFemaleNpcEyesBehaviour_FallbackAndRuntimeTargetKeepLookBlendsEnabled()
    {
        SceneTree sceneTree = GetSceneTree();
        PackedScene scene = LoadPackedScene(ReferenceNpcScenePath);
        Node root = scene.Instantiate();

        try
        {
            await AddChildToRootAsync(sceneTree, root);
            await WaitForFramesAsync(sceneTree, 4);

            EnsureCharacterRuntimeInstalled(root);
            Node eyes = Assert.IsType<Node>(root.GetNodeOrNull("Eyes"), exactMatch: false);
            AnimationTree tree = Assert.IsType<AnimationTree>(root.GetNodeOrNull("AnimationTree"), exactMatch: false);
            Node3D eyeOrigin = new()
            {
                Name = "RuntimeEyesOrigin",
                TopLevel = true,
            };
            Node3D lookTarget = new()
            {
                Name = "RuntimeEyesLookTarget",
                TopLevel = true,
            };
            root.AddChild(eyeOrigin);
            root.AddChild(lookTarget);

            eyes.Set("LookSmoothingTime", 0f);
            eyes.Set("EyeOrigin", eyeOrigin);
            eyes.Set("SaccadeAmplitude", 0f);
            eyes.Set("SaccadeInterval", 0f);
            _ = eyes.Call("ClearLookTarget");
            _ = eyes.Call("RefreshLookParametersDeferred");
            await WaitForFramesAsync(sceneTree, 6);

            Assert.False(eyes.Call("HasRuntimeLookTarget").AsBool());
            Assert.Equal(1f, tree.Get(EyesAnimationTreePaths.GetHorizontalLookBlendParameter()).AsSingle(), precision: 5);
            Assert.Equal(1f, tree.Get(EyesAnimationTreePaths.GetVerticalLookBlendParameter()).AsSingle(), precision: 5);

            lookTarget.GlobalPosition = eyeOrigin.GlobalTransform * new Vector3(10f, 10f, -10f);
            eyes.Set("LookTarget", lookTarget);
            _ = eyes.Call("RefreshLookParametersDeferred");
            await WaitForFramesAsync(sceneTree, 6);

            Assert.True(eyes.Call("HasRuntimeLookTarget").AsBool());
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

    private static void AssertEyeBlendFiltersExactly(
        AnimationNodeBlendTree root,
        string nodeName,
        IReadOnlyList<NodePath> expectedFilters,
        bool assertNoReferenceFemaleFilter = true)
    {
        AnimationNode node = Assert.IsAssignableFrom<AnimationNode>(root.GetNode(nodeName));
        Assert.True(node.FilterEnabled);
        Godot.Collections.Array actualFilters = node.Get("filters").AsGodotArray();
        Assert.Equal(expectedFilters.Count, actualFilters.Count);

        foreach (NodePath filterPath in expectedFilters)
        {
            Assert.True(node.IsPathFiltered(filterPath), $"Expected {nodeName} to filter imported track {filterPath}.");
        }

        if (assertNoReferenceFemaleFilter)
        {
            Assert.False(
                node.IsPathFiltered(new NodePath("GeneralSkeleton/Female_body:eyeBlinkLeft")),
                $"Expected runtime-installed {nodeName} filters not to retain unrelated reference-female paths.");
        }
    }

    private static IReadOnlyList<NodePath> GetAnimationTrackPaths(AnimationPlayer player, string animationName)
    {
        Animation animation = player.GetAnimation(new StringName(animationName));
        var paths = new NodePath[animation.GetTrackCount()];
        for (int trackIndex = 0; trackIndex < animation.GetTrackCount(); trackIndex++)
        {
            paths[trackIndex] = animation.TrackGetPath(trackIndex);
        }

        return paths;
    }

    private static async Task AddChildToRootAsync(SceneTree sceneTree, Node child)
    {
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, child);
        await WaitForNextFrameAsync(sceneTree);
        Assert.True(child.IsInsideTree(), $"Expected '{child.Name}' to enter the test scene tree.");
    }

    private static void AssertAnimation(AnimationNodeBlendTree root, string nodeName, string animationName)
    {
        AnimationNodeAnimation animation = Assert.IsType<AnimationNodeAnimation>(root.GetNode(nodeName), exactMatch: false);
        Assert.Equal(new StringName(animationName), animation.Animation);
    }

    private static void AssertTimeSeek(AnimationNodeBlendTree root, string nodeName)
        => Assert.IsType<AnimationNodeTimeSeek>(root.GetNode(nodeName), exactMatch: false);

    private static void AssertBlinkOneShot(
        AnimationNodeBlendTree root,
        string nodeName,
        IReadOnlyList<NodePath> expectedBlinkFilters)
    {
        AnimationNodeOneShot oneShot = Assert.IsType<AnimationNodeOneShot>(root.GetNode(nodeName), exactMatch: false);
        Assert.True(oneShot.FilterEnabled);

        foreach (NodePath filterPath in expectedBlinkFilters)
        {
            Assert.True(oneShot.IsPathFiltered(filterPath), $"Expected {nodeName} to filter blink path {filterPath}.");
        }

        foreach (NodePath filterPath in EyesAnimationTreePaths.BuildHorizontalLookBlendShapeFilterPaths(
            ReferenceEyeSkeletonNodeName,
            _referenceEyeMeshNodeNames))
        {
            Assert.False(oneShot.IsPathFiltered(filterPath), $"Expected {nodeName} not to filter horizontal look path {filterPath}.");
        }

        foreach (NodePath filterPath in EyesAnimationTreePaths.BuildVerticalLookBlendShapeFilterPaths(
            ReferenceEyeSkeletonNodeName,
            _referenceEyeMeshNodeNames))
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

    private sealed partial class TestEyesBehaviour : EyesBehaviour
    {
        public Vector3 ResolveWorldLookPointForTest() => ResolveWorldLookPoint();
    }
}
