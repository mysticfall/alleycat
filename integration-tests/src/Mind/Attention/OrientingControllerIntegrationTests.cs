using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.IK;
using AlleyCat.Mind.Attention;
using AlleyCat.Scene;
using AlleyCat.Sense;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.Attention;

/// <summary>
/// Focused runtime coverage for the AI-009 attention-driven head-orientation controller, its NPC role-template
/// composition, and its NPC installer wiring into the head IK provider slot.
/// </summary>
public sealed class OrientingControllerIntegrationTests
{
    private const string FemaleNpcTemplatePath =
        "res://assets/characters/templates/reference_female/reference_female_npc.tscn";

    private const string MaleNpcTemplatePath =
        "res://assets/characters/templates/reference_male/reference_male_npc.tscn";

    private const string PlayerTemplatePath =
        "res://assets/characters/templates/reference_female/reference_female_player.tscn";

    private const string PlayerBaseTemplatePath =
        "res://assets/characters/templates/reference_female/reference_female_base.tscn";

    private const string PlayerInstallerPath =
        "res://assets/characters/templates/installers/player_installer.tscn";

    private const string VrikTemplatePath =
        "res://assets/characters/templates/ik/vrik.tscn";

    private const string AllyNpcScenePath =
        "res://assets/characters/reference/ally_npc.tscn";

    private const double FrameDelta = 1d / 60d;
    private const float AnchorDistanceMetres = 2f;
    private const float CentredAngleToleranceDegrees = 1.5f;
    private const float FullCentringMinimumDegrees = 3f;

    /// <summary>
    /// NPC role templates compose the controller as a direct Mind child beside the AI-007 selector and author the
    /// head provider slot wiring the installer pipeline rebases and validates.
    /// </summary>
    [Headless]
    [Fact]
    public void NpcTemplates_ComposeControllerAsDirectMindChildAndAuthorHeadProviderWiring()
    {
        AssertTemplateWiresController(FemaleNpcTemplatePath, "Female");
        AssertTemplateWiresController(MaleNpcTemplatePath, "Male");
    }

    /// <summary>
    /// Player and shared player-base templates stay free of the controller and keep the XR-owned head path.
    /// </summary>
    [Headless]
    [Fact]
    public void PlayerTemplates_RemainFreeOfControllerAndKeepXrOwnedHeadPath()
    {
        string[] playerAuthoredTexts =
        [
            ReadProjectFile(PlayerTemplatePath),
            ReadProjectFile(PlayerBaseTemplatePath),
            ReadProjectFile(PlayerInstallerPath),
            ReadProjectFile(VrikTemplatePath),
        ];

        foreach (string authoredText in playerAuthoredTexts)
        {
            Assert.DoesNotContain("OrientingController", authoredText, StringComparison.Ordinal);
        }

        Node player = LoadPackedScene(PlayerTemplatePath).Instantiate();
        try
        {
            Assert.Null(FindDescendant<OrientingController>(player));

            PlayerVRIK vrik = player.GetNode<PlayerVRIK>("VRIK");
            Assert.Null(vrik.HeadTargetIntentProvider);
            _ = Assert.IsType<XRHeadTargetIntentProvider>(vrik.HeadFallbackIntentProvider);
        }
        finally
        {
            player.Free();
        }
    }

    /// <summary>
    /// The NPC installer installs the template-authored controller into the character and rebinds its wiring to the
    /// installed character's own solved head viewpoint, without making it a character component.
    /// </summary>
    [Headless]
    [Fact]
    public async Task NpcInstaller_WiresControllerIntoInstalledHeadProviderSlot()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = LoadPackedScene(AllyNpcScenePath).Instantiate();
        sceneTree.Root.AddChild(root);
        try
        {
            await WaitForFramesAsync(sceneTree, 12);
            EnsureCharacterRuntimeInstalled(root);

            CharacterIK characterIk = root.GetNode<CharacterIK>("CharacterIK");
            OrientingController controller = Assert.IsType<OrientingController>(characterIk.HeadTargetIntentProvider);

            MindBase mind = Assert.IsAssignableFrom<MindBase>(controller.GetParent());
            Assert.Same(root.GetNode("Mind"), mind);
            Assert.NotNull(root.GetNode("Mind/AttentionGazeTargetSelector"));
            Assert.False(controller is IComponent);
            Assert.Same(root.GetNode("Female/GeneralSkeleton/Head/Viewpoint"), controller.Viewpoint);
            Assert.Equal(0f, controller.GetTargetIntent().DesiredInfluence);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    /// <summary>
    /// The controller centres a sustained in-cone anchor fully after the centring delay, holds neutral during the
    /// brief glance window, releases to zero influence on clear, and re-earns centring after re-assignment.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Controller_CentresSustainedAnchorHoldsGlanceAndReleasesOnClear()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 1);
        var root = new Node3D { Name = "OrientingControllerFixture" };
        sceneTree.Root.AddChild(root);
        try
        {
            TestVision vision = new();
            TestCharacter character = new(hasComponentProjection: true, vision);
            TestMind mind = new(character);
            MutableSceneContext scene = new();
            mind.SetSceneContextLoaderForTesting(() => scene);
            root.AddChild(mind);

            // The solved head frame starts yawed left so every angle conversion runs through a non-identity frame.
            // The fixture head then follows the commanded head intent exactly while influence is engaged, modelling
            // a converged IK solve and closing the servo loop the controller is designed around.
            var head = new Node3D
            {
                Name = "Head",
                Basis = Basis.FromEuler(new Vector3(0f, (float)DegreesToRadians(30d), 0f)),
            };
            root.AddChild(head);
            var viewpoint = new Marker3D
            {
                Name = "Viewpoint",
                Transform = new Transform3D(Basis.Identity, new Vector3(0f, 0.1f, 0f)),
            };
            head.AddChild(viewpoint);
            var anchor = new Node3D { Name = "Anchor" };
            root.AddChild(anchor);

            var controller = new OrientingController { Name = "OrientingController", Viewpoint = viewpoint };
            mind.AddChild(controller);

            // Let one engine frame flush the freshly authored transform caches before any manual evaluation.
            await WaitForFramesAsync(sceneTree, 1);

            // Without an assigned anchor the controller provides idle intent with zero influence.
            controller._Process(FrameDelta);
            Assert.Equal(0f, controller.GetTargetIntent().DesiredInfluence);

            // Sustained in-cone anchor 10° left of the head frame: a brief glance stays eyes-only first.
            PlaceAnchorAtLocalAngles(anchor, head, viewpoint, localYawDegrees: 10f, localPitchDegrees: 0f);
            vision.LookTarget = anchor;

            RunFramesWithFollowedHead(controller, head, 20);
            Assert.True(
                AngleToAnchorDegrees(controller, anchor, viewpoint) is > 8f and < 12f,
                "A brief in-cone glance should hold the head near neutral, but the intent pointed "
                + $"{AngleToAnchorDegrees(controller, anchor, viewpoint):F2}° off the anchor.");
            Assert.True(controller.GetTargetIntent().DesiredInfluence > 0.99f);

            // After the centring delay the head centres the anchor fully — half-centring would leave ~5° behind.
            RunFramesWithFollowedHead(controller, head, 90);
            AssertCentredOn(controller, anchor, viewpoint);
            Assert.True(vision.LookTargetReads > 0);
            Assert.Equal(0, vision.SetLookTargetCalls);
            Assert.Equal(0, vision.ClearLookTargetCalls);

            // Clearing the anchor ramps the influence down to exactly zero and eases the head back to neutral.
            vision.LookTarget = null;
            RunFramesWithFollowedHead(controller, head, 90);
            Assert.Equal(0f, controller.GetTargetIntent().DesiredInfluence);

            // Re-assigning the same anchor node — moved in-cone to the opposite side — restarts the centring
            // timer: the head first holds the last sustained aim instead of centring the new anchor position.
            PlaceAnchorAtLocalAngles(anchor, head, viewpoint, localYawDegrees: -8f, localPitchDegrees: 0f);
            vision.LookTarget = anchor;
            RunFramesWithFollowedHead(controller, head, 20);
            Assert.True(
                AngleToAnchorDegrees(controller, anchor, viewpoint) > 8f,
                "Re-assignment after a clear should restart the centring timer and hold the last sustained aim "
                + $"instead of centring immediately (found {AngleToAnchorDegrees(controller, anchor, viewpoint):F2}° off).");
            RunFramesWithFollowedHead(controller, head, 90);
            AssertCentredOn(controller, anchor, viewpoint);

            // A different anchor straight after a sustained one is a glance reset, then recentres on both axes.
            var secondAnchor = new Node3D { Name = "SecondAnchor" };
            root.AddChild(secondAnchor);
            PlaceAnchorAtLocalAngles(
                secondAnchor,
                head,
                viewpoint,
                localYawDegrees: -25f,
                localPitchDegrees: 12f);
            vision.LookTarget = secondAnchor;

            RunFramesWithFollowedHead(controller, head, 20);
            Assert.True(
                AngleToAnchorDegrees(controller, secondAnchor, viewpoint) > FullCentringMinimumDegrees,
                "An anchor change should not recentre the head before the engagement timing has re-elapsed.");
            RunFramesWithFollowedHead(controller, head, 120);
            AssertCentredOn(controller, secondAnchor, viewpoint);

            Assert.Equal(0, vision.SetLookTargetCalls);
            Assert.Equal(0, vision.ClearLookTargetCalls);
        }
        finally
        {
            root.QueueFree();
        }
    }

    /// <summary>
    /// The controller waits for component projection before binding Vision, rebinds safely on projection refresh,
    /// and tears down to idle intent without leaving a stale Vision binding.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Lifecycle_WaitsForProjectionRebindsSafelyAndTearsDownToIdleIntent()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 1);
        var root = new Node3D { Name = "OrientingControllerLifecycleFixture" };
        sceneTree.Root.AddChild(root);
        try
        {
            var viewpoint = new Marker3D
            {
                Name = "Viewpoint",
                Transform = new Transform3D(Basis.Identity, new Vector3(0f, 0.1f, 0f)),
            };
            root.AddChild(viewpoint);
            var anchor = new Node3D
            {
                Name = "Anchor",
                Position = new Vector3(0f, 0.1f, -2f),
            };
            root.AddChild(anchor);
            await WaitForFramesAsync(sceneTree, 1);

            TestVision firstVision = new();
            TestCharacter character = new(hasComponentProjection: false, firstVision);
            TestMind mind = new(character);
            var controller = new OrientingController { Name = "OrientingController", Viewpoint = viewpoint };
            mind.AddChild(controller);
            try
            {
                controller._Ready();
                controller._Process(FrameDelta);

                Assert.Equal(0f, controller.GetTargetIntent().DesiredInfluence);
                Assert.Equal(0, firstVision.LookTargetReads);

                TestVision secondVision = new()
                {
                    LookTarget = anchor
                };
                character.RefreshComponents(secondVision);
                controller._Process(FrameDelta);

                Assert.True(secondVision.LookTargetReads > 0);
                Assert.Equal(0, firstVision.LookTargetReads);
                Assert.Equal(0, secondVision.SetLookTargetCalls);
                Assert.Equal(0, secondVision.ClearLookTargetCalls);
                Assert.True(controller.GetTargetIntent().DesiredInfluence > 0f);

                controller._ExitTree();

                Assert.Equal(0f, controller.GetTargetIntent().DesiredInfluence);
                controller._Process(FrameDelta);
                Assert.Equal(0, secondVision.SetLookTargetCalls);
                Assert.Equal(0, secondVision.ClearLookTargetCalls);
            }
            finally
            {
                controller._ExitTree();
                controller.Free();
                mind.Free();
            }
        }
        finally
        {
            root.QueueFree();
        }
    }

    /// <summary>
    /// Invalid exported orienting authoring is rejected before the controller can activate or bind runtime
    /// dependencies, including the envelope-exceeds-comfort-cone contract.
    /// </summary>
    [Headless]
    [Fact]
    public void Authoring_RejectsInvalidSettingsBeforeActivation()
    {
        AssertInvalidAuthoring(controller => controller.EnvelopeHorizontalDegrees = controller.ComfortConeHorizontalDegrees);
        AssertInvalidAuthoring(controller => controller.CentringDelaySeconds = 0f);
        AssertInvalidAuthoring(controller => controller.ReactionDelaySeconds = float.NaN);
        AssertInvalidAuthoring(controller => controller.ComfortConeUpDegrees = 0f);
    }

    private static void AssertTemplateWiresController(string templatePath, string bodyName)
    {
        string sceneText = ReadProjectFile(templatePath);

        Assert.Contains("path=\"res://src/Mind/Attention/OrientingController.cs\"", sceneText, StringComparison.Ordinal);
        Assert.Contains("[node name=\"OrientingController\" type=\"Node\" parent=\"Mind\"", sceneText, StringComparison.Ordinal);
        Assert.Contains(
            $"Viewpoint = NodePath(\"../../{bodyName}/GeneralSkeleton/Head/Viewpoint\")",
            sceneText,
            StringComparison.Ordinal);
        Assert.Contains(
            "HeadTargetIntentProvider = NodePath(\"../Mind/OrientingController\")",
            sceneText,
            StringComparison.Ordinal);

        Node template = LoadPackedScene(templatePath).Instantiate();
        try
        {
            OrientingController controller = template.GetNode<OrientingController>("Mind/OrientingController");
            MindBase mind = Assert.IsAssignableFrom<MindBase>(controller.GetParent());
            Assert.NotNull(template.GetNodeOrNull("Mind/AttentionGazeTargetSelector"));
            Assert.False(controller is IComponent);
            _ = Assert.IsAssignableFrom<IKTargetIntentProvider>(controller);

            CharacterIK characterIk = template.GetNode<CharacterIK>("CharacterIK");
            Assert.Same(controller, characterIk.HeadTargetIntentProvider);
            Assert.Same(template.GetNode($"{bodyName}/GeneralSkeleton/Head/Viewpoint"), controller.Viewpoint);
        }
        finally
        {
            template.Free();
        }
    }

    private static void AssertInvalidAuthoring(Action<OrientingController> configure)
    {
        TestMind mind = new(new TestCharacter(hasComponentProjection: false));
        var controller = new OrientingController { Name = "OrientingController" };
        mind.AddChild(controller);
        configure(controller);
        try
        {
            _ = Assert.Throws<InvalidOperationException>(controller._Ready);
        }
        finally
        {
            controller.Free();
            mind.Free();
        }
    }

    private static void RunFramesWithFollowedHead(OrientingController controller, Node3D head, int frameCount)
    {
        for (int frame = 0; frame < frameCount; frame++)
        {
            controller._Process(FrameDelta);
            IKTargetIntent intent = controller.GetTargetIntent();
            if (intent.DesiredInfluence > 0f)
            {
                head.GlobalTransform = intent.WorldTransform;
            }
        }
    }

    private static void AssertCentredOn(OrientingController controller, Node3D anchor, Marker3D viewpoint)
    {
        float angleDegrees = AngleToAnchorDegrees(controller, anchor, viewpoint);
        Assert.True(
            angleDegrees < CentredAngleToleranceDegrees,
            $"The sustained head intent should fully centre the anchor, but pointed {angleDegrees:F2}° off it.");
    }

    private static float AngleToAnchorDegrees(OrientingController controller, Node3D anchor, Marker3D viewpoint)
    {
        IKTargetIntent intent = controller.GetTargetIntent();
        Vector3 intentForward = -intent.WorldTransform.Basis.Z;
        Vector3 anchorDirection = (anchor.GlobalPosition - viewpoint.GlobalPosition).Normalized();
        return Mathf.RadToDeg(intentForward.AngleTo(anchorDirection));
    }

    private static void PlaceAnchorAtLocalAngles(
        Node3D anchor,
        Node3D head,
        Marker3D viewpoint,
        float localYawDegrees,
        float localPitchDegrees)
    {
        double yawRadians = DegreesToRadians(localYawDegrees);
        double pitchRadians = DegreesToRadians(localPitchDegrees);
        // Local sign convention: positive yaw is left of the head forward axis (-Z) and positive pitch is upward.
        double horizontal = Math.Cos(pitchRadians);
        var localDirection = new Vector3(
            (float)(-Math.Sin(yawRadians) * horizontal),
            (float)Math.Sin(pitchRadians),
            (float)(-Math.Cos(yawRadians) * horizontal));
        anchor.GlobalPosition = viewpoint.GlobalPosition + (head.GlobalBasis * localDirection * AnchorDistanceMetres);
    }

    private static T? FindDescendant<T>(Node node)
        where T : Node
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is T typedChild)
            {
                return typedChild;
            }

            T? descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    private static string ReadProjectFile(string path)
        => File.ReadAllText(ProjectSettings.GlobalizePath(path));

    /// <summary>
    /// The controller measures angles and composes its aim in the viewpoint face frame — the head frame with the
    /// marker's local rotation applied — so rigs that author the viewpoint with a 180° yaw flip (the real reference
    /// templates) centre a sustained anchor exactly like identity-marker rigs, rather than reading an anchor ahead
    /// as roughly 180° off and yawing wildly.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Controller_CentresSustainedAnchorOnFlippedMarkerRigsLikeRealTemplates()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 1);
        var root = new Node3D { Name = "OrientingControllerFlippedMarkerFixture" };
        sceneTree.Root.AddChild(root);
        try
        {
            // Mirror the real reference rig: the head bone's forward is +Z and the viewpoint marker carries the
            // authored 180° yaw that maps the face frame onto it.
            var flippedMarkerBasis = Basis.FromEuler(new Vector3(0f, Mathf.Pi, 0f));
            var head = new Node3D
            {
                Name = "Head",
                Basis = flippedMarkerBasis,
            };
            root.AddChild(head);
            var viewpoint = new Marker3D
            {
                Name = "Viewpoint",
                Transform = new Transform3D(flippedMarkerBasis, new Vector3(0f, 0.1f, 0f)),
            };
            head.AddChild(viewpoint);
            var anchor = new Node3D { Name = "Anchor" };
            root.AddChild(anchor);

            TestVision vision = new();
            TestCharacter character = new(hasComponentProjection: true, vision);
            TestMind mind = new(character);
            MutableSceneContext scene = new();
            mind.SetSceneContextLoaderForTesting(() => scene);
            root.AddChild(mind);
            var controller = new OrientingController { Name = "OrientingController", Viewpoint = viewpoint };
            mind.AddChild(controller);

            await WaitForFramesAsync(sceneTree, 1);

            // Sustained in-cone anchor 12° left of the face frame, well within the comfort cone.
            PlaceAnchorAtFaceFrameAngles(anchor, head, viewpoint, flippedMarkerBasis, localYawDegrees: 12f);
            vision.LookTarget = anchor;

            RunFramesWithFollowedHead(controller, head, 20);
            Assert.True(
                AngleFromFaceForwardToAnchorDegrees(controller, anchor, viewpoint, flippedMarkerBasis) is > 9f and < 15f,
                "A brief in-cone glance on a flipped-marker rig should hold the head near the face-frame neutral, but "
                + "pointed "
                + $"{AngleFromFaceForwardToAnchorDegrees(controller, anchor, viewpoint, flippedMarkerBasis):F2}° off.");

            RunFramesWithFollowedHead(controller, head, 90);
            Assert.True(
                AngleFromFaceForwardToAnchorDegrees(controller, anchor, viewpoint, flippedMarkerBasis)
                    < CentredAngleToleranceDegrees,
                "A sustained anchor on a flipped-marker rig should fully centre onto the face forward axis, but pointed "
                + $"{AngleFromFaceForwardToAnchorDegrees(controller, anchor, viewpoint, flippedMarkerBasis):F2}° off.");

            vision.LookTarget = null;
            RunFramesWithFollowedHead(controller, head, 90);
            Assert.Equal(0f, controller.GetTargetIntent().DesiredInfluence);
        }
        finally
        {
            root.QueueFree();
        }
    }

    private static void PlaceAnchorAtFaceFrameAngles(
        Node3D anchor,
        Node3D head,
        Marker3D viewpoint,
        Basis markerLocalBasis,
        float localYawDegrees)
    {
        double yawRadians = DegreesToRadians(localYawDegrees);
        Basis faceBasis = (head.GlobalBasis.Orthonormalized() * markerLocalBasis).Orthonormalized();
        var faceDirection = new Vector3(
            (float)-Math.Sin(yawRadians),
            0f,
            (float)-Math.Cos(yawRadians));
        anchor.GlobalPosition = viewpoint.GlobalPosition + (faceBasis * faceDirection * AnchorDistanceMetres);
    }

    private static float AngleFromFaceForwardToAnchorDegrees(
        OrientingController controller,
        Node3D anchor,
        Marker3D viewpoint,
        Basis markerLocalBasis)
    {
        IKTargetIntent intent = controller.GetTargetIntent();
        Basis faceBasis = (intent.WorldTransform.Basis.Orthonormalized() * markerLocalBasis).Orthonormalized();
        Vector3 faceForward = -faceBasis.Z;
        Vector3 anchorDirection = (anchor.GlobalPosition - viewpoint.GlobalPosition).Normalized();
        return Mathf.RadToDeg(faceForward.AngleTo(anchorDirection));
    }

    private sealed partial class TestMind(ICharacter owner) : MindBase
    {
        protected override ICharacter ResolveOwningCharacter() => owner;

    }

    private sealed class TestCharacter(bool hasComponentProjection, params IComponent[] components)
        : ICharacter, IComponentProjectionNotifier
    {
        private IComponent[] _components = components;
        private Action? _componentsRefreshed;

        public string Id { get; set; } = "owner";

        public IReadOnlyList<IComponent> Components => _components;

        public bool HasComponentProjection { get; private set; } = hasComponentProjection;

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public int ComponentsRefreshedHandlerCount
        {
            get; private set;
        }

        public event Action? ComponentsRefreshed
        {
            add
            {
                _componentsRefreshed += value;
                ComponentsRefreshedHandlerCount++;
            }
            remove
            {
                _componentsRefreshed -= value;
                ComponentsRefreshedHandlerCount--;
            }
        }

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();

        public void RefreshComponents(params IComponent[] components)
        {
            _components = components;
            HasComponentProjection = true;
            _componentsRefreshed?.Invoke();
        }
    }

    private sealed class TestVision : IVision
    {
        public Node3D? LookTarget
        {
            get
            {
                LookTargetReads++;
                return field;
            }
            set;
        }

        public int LookTargetReads
        {
            get;
            private set;
        }

        public int SetLookTargetCalls
        {
            get;
            private set;
        }

        public int ClearLookTargetCalls
        {
            get;
            private set;
        }

        public event Action<IPercept>? Perceived
        {
            add
            {
            }
            remove
            {
            }
        }

        public IReadOnlyList<Type> PerceptTypes { get; } = [];

        public void SetLookTarget(Node3D? target)
        {
            SetLookTargetCalls++;
            LookTarget = target;
        }

        public void ClearLookTarget()
        {
            ClearLookTargetCalls++;
            LookTarget = null;
        }
    }

    private sealed class MutableSceneContext : ISceneContext
    {
        private readonly Dictionary<string, IIdentifiable> _entries = new(StringComparer.Ordinal);

        public ICharacter Player => throw new InvalidOperationException(
            "Scene context contains no player character. Scene authoring guarantees the player is present.");

        public IReadOnlyCollection<ICharacter> Characters => [];

        public ContentContext Content => ContentContext.Default;

        public void Add(IIdentifiable identifiable) => _entries.Add(identifiable.FullId, identifiable);

        public IIdentifiable? Find(string fullId) => _entries.GetValueOrDefault(fullId);

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException($"No test scene entry exists for '{fullId}'.");
    }
}
