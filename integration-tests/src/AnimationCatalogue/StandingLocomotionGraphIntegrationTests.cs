using System.Text.Json;
using AlleyCat.Control.Locomotion;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.AnimationCatalogue;

/// <summary>
/// Focused resource and scene coverage for standing-locomotion graph consumers.
/// </summary>
public sealed class StandingLocomotionGraphIntegrationTests
{
    private const string LibraryPath =
        "res://assets/characters/reference/female/animations/locomotion/standing_locomotion_library.tres";
    private const string CataloguePath =
        "res://assets/characters/reference/female/animations/locomotion/standing_locomotion_catalogue.json";
    private const string MovementParameter = "parameters/States/Walking/Locomotion/Movement/blend_position";
    private const string TurnParameter = "parameters/States/Walking/Locomotion/blend_position";
    private const string PhotoboothPath = "res://tests/locomotion/standing_locomotion_visual.tscn";
    private const string TurnAuthoringPhotoboothPath = "res://tests/navigation/turn_authoring_visual.tscn";

    private static readonly GraphExpectation[] _graphs =
    [
        new("res://assets/characters/templates/animation/animation_tree_root_npc.tres", "reference_female", ["Idle", "Walking"]),
        new("res://assets/characters/templates/animation/animation_tree_root_reference_male_npc.tres", "reference_male", ["Idle", "Walking"]),
        new("res://assets/characters/templates/animation/animation_tree_root_player.tres",
            "reference_female",
            ["StandingCrouching", "KneelingEnter", "Kneeling", "KneelingExit", "AllFoursTransitioning", "AllFours", "AllFoursForward", "Walking"]),
    ];

    private static readonly IReadOnlyDictionary<string, string> _expectedRoleKeys =
        new Dictionary<string, string>
        {
            ["Idle"] = "mixamo_c9ccf750_b96c_11e4_a802_0aaa78deedf9",
            ["ForwardWalk"] = "mixamo_c9ccf814_b96c_11e4_a802_0aaa78deedf9",
            ["BackwardWalk"] = "mixamo_c9ccf998_b96c_11e4_a802_0aaa78deedf9",
            ["WalkArcLeft"] = "mixamo_c9ccf8d5_b96c_11e4_a802_0aaa78deedf9",
            ["WalkArcRight"] = "derived_mirror_c9ccf8d5_b96c_11e4_a802_0aaa78deedf9",
            ["SideStepLeft"] = "mixamo_c9c9d9d6_b96c_11e4_a802_0aaa78deedf9",
            ["SideStepRight"] = "mixamo_c9c9db9e_b96c_11e4_a802_0aaa78deedf9",
            ["TurnInPlaceLeft90"] = "mixamo_c9ceef5f_b96c_11e4_a802_0aaa78deedf9",
            ["TurnInPlaceRight90"] = "mixamo_c9cef01d_b96c_11e4_a802_0aaa78deedf9",
        };

    /// <summary>
    /// Verifies only the Walking state uses the new complete directional topology.
    /// </summary>
    [Headless]
    [Fact]
    public void WalkingGraphs_ExposeDirectionalAndSignedTurnBlendSpacesWithoutRemovingOtherStates()
    {
        foreach (GraphExpectation expectation in _graphs)
        {
            AnimationNodeBlendTree root = Assert.IsType<AnimationNodeBlendTree>(
                ResourceLoader.Load(expectation.Path), exactMatch: false);
            AnimationNodeStateMachine states = Assert.IsType<AnimationNodeStateMachine>(
                root.GetNode("States"), exactMatch: false);
            foreach (string stateName in expectation.RequiredStates)
            {
                Assert.NotNull(states.GetNode(stateName));
            }

            AnimationNodeBlendTree walking = Assert.IsType<AnimationNodeBlendTree>(
                states.GetNode("Walking"), exactMatch: false);
            AnimationNodeBlendSpace2D locomotion = Assert.IsType<AnimationNodeBlendSpace2D>(
                walking.GetNode("Locomotion"), exactMatch: false);
            AssertBlendPointNames(locomotion,
                "Idle", "Movement", "TurnInPlaceLeft90", "TurnInPlaceRight90", "WalkArcLeft", "WalkArcRight");

            int movementIndex = locomotion.FindBlendPointByName("Movement");
            AnimationNodeBlendSpace2D movement = Assert.IsType<AnimationNodeBlendSpace2D>(
                locomotion.GetBlendPointNode(movementIndex), exactMatch: false);
            AssertBlendPointNames(movement, "Idle", "ForwardWalk", "BackwardWalk", "SideStepLeft", "SideStepRight");

            for (int point = 0; point < locomotion.GetBlendPointCount(); point++)
            {
                if (point == movementIndex)
                {
                    continue;
                }

                AssertLibraryAnimationLoopPolicy(locomotion.GetBlendPointNode(point));
            }

            for (int point = 0; point < movement.GetBlendPointCount(); point++)
            {
                AssertLibraryAnimationLoopPolicy(movement.GetBlendPointNode(point));
            }
        }
    }

    /// <summary>
    /// Verifies loop ownership is imported from the persisted ANIM-001 pose seam result.
    /// </summary>
    [Headless]
    [Fact]
    public void StandingLocomotionResources_UseSourceOwnedLoopPolicy()
    {
        using var catalogue = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(CataloguePath)));
        AnimationLibrary library = Assert.IsType<AnimationLibrary>(ResourceLoader.Load(LibraryPath), exactMatch: false);

        foreach (JsonElement clip in catalogue.RootElement.GetProperty("clips").EnumerateArray())
        {
            string key = Assert.IsType<string>(clip.GetProperty("key").GetString());
            Assert.DoesNotContain("-loop", key, StringComparison.Ordinal);

            bool effectiveIntent = clip.GetProperty("loop_intent").GetProperty("effective_loop_intent").GetBoolean();
            Animation.LoopModeEnum expectedLoopMode = effectiveIntent ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
            Assert.Equal(expectedLoopMode, library.GetAnimation(key).LoopMode);
        }
    }

    /// <summary>
    /// Verifies the extracted root-motion track uses the AnimationPlayer root-relative path used by every locomotion clip.
    /// </summary>
    [Headless]
    [Fact]
    public void CharacterAnimationTree_RootMotionTrack_MatchesLocomotionClipTrack()
    {
        const string animationTreePath = "res://assets/characters/templates/animation/animation_tree.tscn";
        PackedScene scene = Assert.IsType<PackedScene>(ResourceLoader.Load(animationTreePath), exactMatch: false);
        AnimationTree animationTree = Assert.IsType<AnimationTree>(scene.Instantiate(), exactMatch: false);
        try
        {
            Assert.Equal(new NodePath("%GeneralSkeleton:Root"), animationTree.RootMotionTrack);
            Assert.Equal(AnimationMixer.AnimationCallbackModeProcess.Physics, animationTree.CallbackModeProcess);

            AnimationLibrary library = Assert.IsType<AnimationLibrary>(ResourceLoader.Load(LibraryPath), exactMatch: false);
            Animation forwards = library.GetAnimation("mixamo_c9ccf814_b96c_11e4_a802_0aaa78deedf9");
            Assert.True(
                forwards.FindTrack(animationTree.RootMotionTrack, Animation.TrackType.Position3D) >= 0,
                $"Root-motion track '{animationTree.RootMotionTrack}' must resolve a position track in the forward locomotion clip.");
            Assert.True(
                forwards.FindTrack(animationTree.RootMotionTrack, Animation.TrackType.Rotation3D) >= 0,
                $"Root-motion track '{animationTree.RootMotionTrack}' must resolve a rotation track in the forward locomotion clip.");
        }
        finally
        {
            animationTree.Free();
        }
    }

    /// <summary>
    /// Verifies the production AnimationTree resolves the unique Root target while sampling forward and held-pivot states.
    /// </summary>
    [Fact]
    public async Task ProductionAnimationTree_UsesUniqueRootMotionTargetForForwardAndPivotPlayback()
    {
        const string scenePath = "res://assets/characters/templates/reference_female/reference_female_base.tscn";
        Node root = Assert.IsType<PackedScene>(ResourceLoader.Load(scenePath), exactMatch: false).Instantiate();
        SceneTree sceneTree = Assert.IsType<SceneTree>(Engine.GetMainLoop(), exactMatch: false);
        sceneTree.Root.AddChild(root);
        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 4);
            AnimationTree animationTree = root.GetNode<AnimationTree>("AnimationTree");
            foreach (SkeletonModifier3D modifier in root.FindChildren("*", nameof(SkeletonModifier3D), recursive: true, owned: false)
                         .OfType<SkeletonModifier3D>())
            {
                modifier.Active = false;
            }
            animationTree.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
            animationTree.SetProcess(false);
            animationTree.SetPhysicsProcess(false);
            animationTree.Active = true;
            AnimationNodeStateMachinePlayback playback = animationTree.Get("parameters/States/playback")
                .As<AnimationNodeStateMachinePlayback>();
            Skeleton3D skeleton = root.GetNode<Skeleton3D>("Female/GeneralSkeleton");
            Assert.Same(skeleton, animationTree.GetNode<Skeleton3D>("%GeneralSkeleton"));
            Assert.Equal(new NodePath("%GeneralSkeleton:Root"), animationTree.RootMotionTrack);
            playback.Start("Walking", true);
            animationTree.Set(MovementParameter, Vector2.Up);
            animationTree.Set(TurnParameter, new Vector2(0.0f, 1.0f));
            animationTree.Advance(0.0);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies female and male character templates attach the same library and bind exact locomotion parameters.
    /// </summary>
    [Headless]
    [Theory]
    [InlineData("res://assets/characters/templates/reference_female/reference_female_base.tscn")]
    [InlineData("res://assets/characters/templates/reference_male/reference_male_base.tscn")]
    public void CharacterTemplate_AttachesSharedLibraryAndBindsGraphParameters(string scenePath)
    {
        PackedScene packedScene = Assert.IsType<PackedScene>(ResourceLoader.Load(scenePath), exactMatch: false);
        Node root = packedScene.Instantiate();
        try
        {
            AnimationPlayer animationPlayer = root.GetNode<AnimationPlayer>("AnimationPlayer");
            AnimationLibrary library = animationPlayer.GetAnimationLibrary("locomotion");
            Assert.NotNull(library);
            Assert.Equal(LibraryPath, library.ResourcePath);

            CharacterLocomotion locomotion = root.GetNode<CharacterLocomotion>("Locomotion");
            Assert.Equal(MovementParameter, locomotion.AnimationBlendParameter.ToString());
            Assert.Equal(TurnParameter, locomotion.AnimationTurnBlendParameter.ToString());

            AnimationTree animationTree = root.GetNode<AnimationTree>("AnimationTree");
            Assert.Equal(Variant.Type.Vector2, animationTree.Get(MovementParameter).VariantType);
            Assert.Equal(Variant.Type.Vector2, animationTree.Get(TurnParameter).VariantType);
        }
        finally
        {
            root.Free();
        }
    }

    /// <summary>
    /// Guards the directional semantics used by the visual fixture in the reference rig's skeleton-local frame.
    /// </summary>
    [Headless]
    [Fact]
    public void WalkingGraphs_SelectedRootMotionMatchesVisibleDirectionAndTurnSigns()
    {
        foreach (GraphExpectation expectation in _graphs)
        {
            AnimationNodeBlendTree root = Assert.IsType<AnimationNodeBlendTree>(ResourceLoader.Load(expectation.Path), exactMatch: false);
            AnimationNodeStateMachine states = Assert.IsType<AnimationNodeStateMachine>(root.GetNode("States"), exactMatch: false);
            AnimationNodeBlendTree walking = Assert.IsType<AnimationNodeBlendTree>(states.GetNode("Walking"), exactMatch: false);
            AnimationNodeBlendSpace2D locomotion = Assert.IsType<AnimationNodeBlendSpace2D>(walking.GetNode("Locomotion"), exactMatch: false);
            AnimationNodeBlendSpace2D movement = Assert.IsType<AnimationNodeBlendSpace2D>(
                locomotion.GetBlendPointNode(locomotion.FindBlendPointByName("Movement")), exactMatch: false);

            AssertRootTranslation(movement, "ForwardWalk", delta => delta.Z > 0.5f);
            AssertRootTranslation(movement, "BackwardWalk", delta => delta.Z < -0.5f);
            AssertRootTranslation(movement, "SideStepLeft", delta => delta.X > 0.5f);
            AssertRootTranslation(movement, "SideStepRight", delta => delta.X < -0.5f);
            AssertRootTranslation(locomotion, "WalkArcLeft", delta => delta.Z > 0.5f);
            AssertRootTranslation(locomotion, "WalkArcRight", delta => delta.Z > 0.5f);
            AssertRootYaw(locomotion, "WalkArcLeft", yaw => yaw > 0.1f);
            AssertRootYaw(locomotion, "WalkArcRight", yaw => yaw < -0.1f);
            AssertStationaryPivot(locomotion, "TurnInPlaceLeft90", 1f);
            AssertStationaryPivot(locomotion, "TurnInPlaceRight90", -1f);
        }
    }

    /// <summary>
    /// Locks every shipped graph point to the corresponding selected role and shared-library key.
    /// </summary>
    [Headless]
    [Fact]
    public void WalkingGraphs_NineRoleBindingsMatchExactCatalogueAuthoring()
    {
        using var catalogue = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(CataloguePath)));
        JsonElement roleMaps = catalogue.RootElement.GetProperty("role_maps");
        AnimationLibrary library = Assert.IsType<AnimationLibrary>(ResourceLoader.Load(LibraryPath), exactMatch: false);

        foreach (GraphExpectation graph in _graphs)
        {
            AnimationNodeBlendTree root = Assert.IsType<AnimationNodeBlendTree>(ResourceLoader.Load(graph.Path), exactMatch: false);
            AnimationNodeStateMachine states = Assert.IsType<AnimationNodeStateMachine>(root.GetNode("States"), exactMatch: false);
            AnimationNodeBlendTree walking = Assert.IsType<AnimationNodeBlendTree>(states.GetNode("Walking"), exactMatch: false);
            AnimationNodeBlendSpace2D locomotion = Assert.IsType<AnimationNodeBlendSpace2D>(walking.GetNode("Locomotion"), exactMatch: false);

            var roles = roleMaps.GetProperty(graph.CatalogueMap)
                .EnumerateArray()
                .ToDictionary(
                    entry => Assert.IsType<string>(entry.GetProperty("graph_role").GetString()),
                    entry => entry.Clone());
            foreach ((string role, string key) in _expectedRoleKeys)
            {
                Assert.Equal(key, roles[role].GetProperty("library_key").GetString());
                Animation animation = ResolveRoleAnimation(locomotion, role);
                Assert.Same(library.GetAnimation(key), animation);
            }
        }
    }

    /// <summary>
    /// Verifies the visual fixture uses the established full-body framing and correctly labelled directional cameras.
    /// </summary>
    [Headless]
    [Fact]
    public void LocomotionPhotobooth_ProvidesFemaleSubjectAndDirectionalCameraFrame()
    {
        PackedScene scene = Assert.IsType<PackedScene>(ResourceLoader.Load(PhotoboothPath), exactMatch: false);
        Node3D root = Assert.IsType<Node3D>(scene.Instantiate(), exactMatch: false);
        try
        {
            Assert.NotNull(root.GetNodeOrNull<Node3D>("Subject/Female"));
            Node3D worldOriginReference = Assert.IsType<Node3D>(root.GetNode("WorldOriginReference"), exactMatch: false);
            Assert.Equal(Vector3.Zero, worldOriginReference.GlobalPosition);
            Node3D front = Assert.IsType<Node3D>(root.GetNode("Cameras/FrontCamera"), exactMatch: false);
            Node3D right = Assert.IsType<Node3D>(root.GetNode("Cameras/RightCamera"), exactMatch: false);
            Assert.True(front.Position.Z < -0.5f, $"Front camera must remain in front of the avatar; got {front.Position}.");
            Assert.True(right.Position.X > 0.5f, $"Right camera must remain on avatar-right; got {right.Position}.");
        }
        finally
        {
            root.Free();
        }
    }

    /// <summary>
    /// Verifies the authoring-specific visual fixture uses a downward orthographic camera that can read root paths.
    /// </summary>
    [Headless]
    [Fact]
    public void TurnAuthoringPhotobooth_ProvidesTrajectoryReadableTopCamera()
    {
        PackedScene scene = Assert.IsType<PackedScene>(ResourceLoader.Load(TurnAuthoringPhotoboothPath), exactMatch: false);
        Node3D root = Assert.IsType<Node3D>(scene.Instantiate(), exactMatch: false);
        try
        {
            Node3D cameraRig = Assert.IsType<Node3D>(root.GetNode("Cameras/TopTrajectoryCamera"), exactMatch: false);
            Camera3D camera = Assert.IsType<Camera3D>(cameraRig.GetNode("Viewport/Camera"), exactMatch: false);
            Assert.True(cameraRig.Position.Y >= 5.0f, $"Top camera must remain above the authored path; got {cameraRig.Position}.");
            Assert.True((-cameraRig.Basis.Z).Dot(Vector3.Down) > 0.99f,
                $"Top camera must point down for unambiguous trajectory signs; got {-cameraRig.Basis.Z}.");
            Assert.Equal(Camera3D.ProjectionType.Orthogonal, camera.Projection);
        }
        finally
        {
            root.Free();
        }
    }

    private static void AssertBlendPointNames(AnimationNodeBlendSpace2D blendSpace, params string[] expected)
        => Assert.Equal(expected.Order(), Enumerable.Range(0, blendSpace.GetBlendPointCount())
            .Select(index => blendSpace.GetBlendPointName(index).ToString()).Order());

    private static void AssertLibraryAnimationLoopPolicy(AnimationRootNode node)
    {
        AnimationNodeAnimation animation = Assert.IsType<AnimationNodeAnimation>(node, exactMatch: false);
        Assert.StartsWith("locomotion/", animation.Animation.ToString(), StringComparison.Ordinal);
        Assert.False(animation.UseCustomTimeline);
        string key = animation.Animation.ToString()["locomotion/".Length..];
        Assert.True(
            key.StartsWith("mixamo_", StringComparison.Ordinal)
                || key.StartsWith("derived_mirror_", StringComparison.Ordinal),
            $"Locomotion key '{key}' must be a native or approved derived-mirror identity.");
        AnimationLibrary library = Assert.IsType<AnimationLibrary>(ResourceLoader.Load(LibraryPath), exactMatch: false);
        Animation.LoopModeEnum expectedLoopMode = PersistedLoopIntent(key)
            ? Animation.LoopModeEnum.Linear
            : Animation.LoopModeEnum.None;
        Assert.Equal(expectedLoopMode, library.GetAnimation(key).LoopMode);
    }

    private static void AssertRootTranslation(
        AnimationNodeBlendSpace2D blendSpace,
        string pointName,
        Func<Vector3, bool> predicate)
    {
        Animation animation = ResolveAnimation(blendSpace, pointName);
        int track = animation.FindTrack(new NodePath("%GeneralSkeleton:Root"), Animation.TrackType.Position3D);
        Assert.True(track >= 0);
        Vector3 delta = animation.PositionTrackInterpolate(track, animation.Length)
            - animation.PositionTrackInterpolate(track, 0.0);
        Assert.True(delta.IsFinite() && predicate(delta), $"Unexpected {pointName} root delta: {delta}.");
    }

    private static void AssertStationaryPivot(
        AnimationNodeBlendSpace2D blendSpace,
        string pointName,
        float expectedYawSign)
    {
        Animation animation = ResolveAnimation(blendSpace, pointName);
        string key = Assert.IsType<AnimationNodeAnimation>(
            blendSpace.GetBlendPointNode(blendSpace.FindBlendPointByName(pointName)), exactMatch: false)
            .Animation.ToString()["locomotion/".Length..];
        Assert.Equal(PersistedLoopIntent(key) ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None, animation.LoopMode);
        Assert.True(double.IsFinite(animation.Length) && animation.Length > 0.0);

        int positionTrack = animation.FindTrack(new NodePath("%GeneralSkeleton:Root"), Animation.TrackType.Position3D);
        int rotationTrack = animation.FindTrack(new NodePath("%GeneralSkeleton:Root"), Animation.TrackType.Rotation3D);
        Assert.True(positionTrack >= 0);
        Assert.True(rotationTrack >= 0);

        Vector3 translation = animation.PositionTrackInterpolate(positionTrack, animation.Length)
            - animation.PositionTrackInterpolate(positionTrack, 0.0);
        Quaternion start = animation.RotationTrackInterpolate(rotationTrack, 0.0);
        Quaternion finish = animation.RotationTrackInterpolate(rotationTrack, animation.Length);
        float yaw = (start.Inverse() * finish).GetEuler().Y;
        Assert.True(translation.IsFinite() && new Vector2(translation.X, translation.Z).Length() < 0.2f,
            $"Expected {pointName} to remain stationary; got Root translation {translation}.");
        Assert.True(float.IsFinite(yaw) && yaw * expectedYawSign > 0.0f,
            $"Expected {pointName} Root yaw to have sign {expectedYawSign}; got {yaw:F6}.");
        // Preserve the authored source turn rather than snapping the track to a metadata ramp.
        Assert.InRange(Mathf.Abs(yaw), (Mathf.Pi / 2.0f) - 0.12f, (Mathf.Pi / 2.0f) + 0.12f);
    }

    private static bool PersistedLoopIntent(string key)
    {
        using var catalogue = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(CataloguePath)));
        return catalogue.RootElement.GetProperty("clips").EnumerateArray()
            .Single(clip => clip.GetProperty("key").GetString() == key)
            .GetProperty("loop_intent").GetProperty("effective_loop_intent").GetBoolean();
    }

    private static void AssertRootYaw(
        AnimationNodeBlendSpace2D blendSpace,
        string pointName,
        Func<float, bool> predicate)
    {
        Animation animation = ResolveAnimation(blendSpace, pointName);
        int track = animation.FindTrack(new NodePath("%GeneralSkeleton:Root"), Animation.TrackType.Rotation3D);
        Assert.True(track >= 0);
        Quaternion start = animation.RotationTrackInterpolate(track, 0.0);
        Quaternion finish = animation.RotationTrackInterpolate(track, animation.Length);
        float yaw = (start.Inverse() * finish).GetEuler().Y;
        Assert.True(float.IsFinite(yaw) && predicate(yaw), $"Unexpected {pointName} root yaw: {yaw}.");
    }

    private static Animation ResolveAnimation(AnimationNodeBlendSpace2D blendSpace, string pointName)
    {
        AnimationNodeAnimation node = Assert.IsType<AnimationNodeAnimation>(
            blendSpace.GetBlendPointNode(blendSpace.FindBlendPointByName(pointName)), exactMatch: false);
        string key = node.Animation.ToString()["locomotion/".Length..];
        AnimationLibrary library = Assert.IsType<AnimationLibrary>(ResourceLoader.Load(LibraryPath), exactMatch: false);
        return library.GetAnimation(key);
    }

    private static Animation ResolveRoleAnimation(AnimationNodeBlendSpace2D locomotion, string role)
    {
        AnimationNodeBlendSpace2D source = (role is "ForwardWalk" or "BackwardWalk" or "SideStepLeft" or "SideStepRight")
            ? Assert.IsType<AnimationNodeBlendSpace2D>(
                locomotion.GetBlendPointNode(locomotion.FindBlendPointByName("Movement")), exactMatch: false)
            : locomotion;
        return ResolveAnimation(source, role);
    }

    private sealed record GraphExpectation(string Path, string CatalogueMap, string[] RequiredStates);

}
