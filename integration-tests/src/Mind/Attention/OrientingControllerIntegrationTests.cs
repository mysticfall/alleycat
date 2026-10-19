using AlleyCat.Character;
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
    /// <summary>AC11: defaults and both installed NPC configurations retain secondary-glance timing headroom.</summary>
    [Headless]
    [Fact]
    public void DefaultCentringDelay_ExceedsConfiguredSecondaryDwell()
    {
        using AttentionGazeTargetSelector selector = new();
        Assert.True(OrientingSettings.Default.CentringDelaySeconds > selector.SecondaryDwellSeconds);
        foreach (string path in new[] { FemaleNpcTemplatePath, MaleNpcTemplatePath })
        {
            Node npc = LoadPackedScene(path).Instantiate();
            try
            {
                OrientingController controller = npc.GetNode<OrientingController>("Mind/OrientingController");
                AttentionGazeTargetSelector configured = npc.GetNode<AttentionGazeTargetSelector>("Mind/AttentionGazeTargetSelector");
                Assert.True(controller.CentringDelaySeconds > configured.SecondaryDwellSeconds);
            }
            finally
            {
                npc.Free();
            }
        }
    }

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
    /// installed character's own solved head viewpoint and neck pivot, without making it a character component.
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
            Assert.Same(root.GetNode("Female/GeneralSkeleton/Neck/NeckPivot"), controller.NeckPivot);
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
            var anchor = new StaticVisualCue { Name = "Anchor" };
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
            // HEAD-2 rationale: release eases toward the upstream (authored rest) pose, so the pose held
            // through the re-assignment glance is ≈ the rest orientation rather than world-forward; the
            // anchor sits 8° local from it, so the held measurement reads ≈8° rather than the scalar-era
            // ≈22°. The invariant under test — the timer restarts and the new anchor is not centred before
            // the engagement timing elapses — is the well-clear-of-centring bound.
            Assert.True(
                AngleToAnchorDegrees(controller, anchor, viewpoint) > FullCentringMinimumDegrees,
                "Re-assignment after a clear should restart the centring timer and hold the released pose "
                + $"instead of centring immediately (found {AngleToAnchorDegrees(controller, anchor, viewpoint):F2}° off).");
            RunFramesWithFollowedHead(controller, head, 90);
            AssertCentredOn(controller, anchor, viewpoint);

            // A different anchor straight after a sustained one is a glance reset, then recentres on both axes.
            var secondAnchor = new StaticVisualCue { Name = "SecondAnchor" };
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
            var anchor = new StaticVisualCue
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
            $"NeckPivot = NodePath(\"../../{bodyName}/GeneralSkeleton/Neck/NeckPivot\")",
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
            Assert.Same(template.GetNode($"{bodyName}/GeneralSkeleton/Neck/NeckPivot"), controller.NeckPivot);
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
            var anchor = new StaticVisualCue { Name = "Anchor" };
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

    /// <summary>
    /// The published intent basis stays roll-free across frames measured against the character-stable up — not
    /// world up — even when the solved head is externally rolled between frames: transient solved-head roll (idle
    /// sway, twist redistribution, CCDIK roll) must decay instead of being absorbed into the neutral reference and
    /// republished forever (AI-009 TR 19–20, acceptance criterion 15; diagnostic baseline: −11.97° sustained roll
    /// over a 3.2 s lateral hold).
    /// </summary>
    [Headless]
    [Fact]
    public async Task IntentBasis_StaysRollFreeWhenSolvedHeadIsExternallyRolled()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 1);
        var root = new Node3D { Name = "OrientingControllerRollFixture" };
        sceneTree.Root.AddChild(root);
        try
        {
            // The character frame is deliberately yawed and rolled so the roll-free construction is verified
            // against the character-stable up: an implementation that measured roll against world up would read
            // the character's 10° roll in every published frame.
            TestVision vision = new();
            TestCharacter character = new(hasComponentProjection: true, vision)
            {
                GlobalTransform = new Transform3D(
                    Basis.FromEuler(
                        new Vector3(0f, (float)DegreesToRadians(20d), (float)DegreesToRadians(10d))),
                    Vector3.Zero),
            };
            Vector3 characterUp = character.GlobalTransform.Basis.Y;
            TestMind mind = new(character);
            MutableSceneContext scene = new();
            mind.SetSceneContextLoaderForTesting(() => scene);
            root.AddChild(mind);

            var head = new Node3D { Name = "Head" };
            root.AddChild(head);
            var viewpoint = new Marker3D
            {
                Name = "Viewpoint",
                Transform = new Transform3D(Basis.Identity, new Vector3(0f, 0.1f, 0f)),
            };
            head.AddChild(viewpoint);
            var anchor = new StaticVisualCue { Name = "Anchor" };
            root.AddChild(anchor);

            var controller = new OrientingController { Name = "OrientingController", Viewpoint = viewpoint };
            mind.AddChild(controller);
            await WaitForFramesAsync(sceneTree, 1);

            // Sustained lateral anchor — the diagnostic defect scenario.
            PlaceAnchorAtLocalAngles(anchor, head, viewpoint, localYawDegrees: 30f, localPitchDegrees: 0f);
            vision.LookTarget = anchor;

            const int firstKickFrame = 150;
            const int secondKickFrame = 260;
            const float firstKickDegrees = 8f;
            const float secondKickDegrees = -10f;
            float maxAbsRollDegrees = 0f;
            bool observedRollKick = false;
            for (int frame = 0; frame < 360; frame++)
            {
                controller._Process(FrameDelta);
                IKTargetIntent intent = controller.GetTargetIntent();
                if (intent.DesiredInfluence > 0.5f)
                {
                    maxAbsRollDegrees = Mathf.Max(
                        maxAbsRollDegrees,
                        Mathf.Abs(MeasureRollDegrees(intent.WorldTransform.Basis, characterUp)));
                }

                // The solved head confirms the commanded orientation each frame; the two kicks inject external roll
                // about the head forward axis between frames, modelling the roll idle sway, the twist disperser,
                // and CCDIK leave on the solved head. Both kick magnitudes stay inside the neutral tracking
                // tolerance (~11.46°) so the re-anchor genuinely runs on the rolled head.
                Transform3D intentTransform = intent.WorldTransform;
                if (frame is firstKickFrame or secondKickFrame)
                {
                    float kickDegrees = frame == firstKickFrame ? firstKickDegrees : secondKickDegrees;
                    var kickBasis = Basis.FromEuler(new Vector3(0f, 0f, (float)DegreesToRadians(kickDegrees)));
                    head.GlobalTransform = new Transform3D(intentTransform.Basis * kickBasis, intentTransform.Origin);
                    observedRollKick = observedRollKick
                        || Mathf.Abs(MeasureRollDegrees(head.GlobalBasis, characterUp)) > 5f;
                }
                else
                {
                    head.GlobalTransform = intentTransform;
                }
            }

            Assert.True(observedRollKick, "The external roll kick never reached the solved head; the fixture is vacuous.");
            Assert.True(
                maxAbsRollDegrees < 1f,
                "The published intent basis should stay roll-free against the character-stable up while the solved "
                + $"head is externally rolled, but reached {maxAbsRollDegrees:F2}°.");
            Assert.True(
                controller.GetTargetIntent().DesiredInfluence > 0.99f,
                "The roll observation window should cover an engaged, tracking head.");

            // Transient roll decays rather than persisting: after both kicks the head recentres the anchor.
            AssertCentredOn(controller, anchor, viewpoint);
        }
        finally
        {
            root.QueueFree();
        }
    }

    /// <summary>
    /// While a valid neck pivot is assigned, the published intent origin swings about the pivot by exactly the
    /// commanded aim rotation — a downward aim swings the origin down about the pivot, the swing angle matches the
    /// commanded aim, and the pivot radius is preserved — giving the Neck-Spine CCDIK chain positional error to
    /// distribute across the neck and upper spine (AI-009 TR 21, acceptance criterion 16).
    /// </summary>
    [Headless]
    [Fact]
    public async Task IntentOrigin_SwingsAboutNeckPivotByCommandedAim()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 1);
        var root = new Node3D { Name = "OrientingControllerPivotFixture" };
        sceneTree.Root.AddChild(root);
        try
        {
            TestVision vision = new();
            TestCharacter character = new(hasComponentProjection: true, vision);
            TestMind mind = new(character);
            MutableSceneContext scene = new();
            mind.SetSceneContextLoaderForTesting(() => scene);
            root.AddChild(mind);

            var head = new Node3D { Name = "Head" };
            root.AddChild(head);
            var viewpoint = new Marker3D
            {
                Name = "Viewpoint",
                Transform = new Transform3D(Basis.Identity, new Vector3(0f, 0.1f, 0f)),
            };
            head.AddChild(viewpoint);
            var pivot = new Marker3D
            {
                Name = "NeckPivot",
                Transform = new Transform3D(Basis.Identity, new Vector3(0f, -0.15f, 0f)),
            };
            head.AddChild(pivot);
            var anchor = new StaticVisualCue { Name = "Anchor" };
            root.AddChild(anchor);

            var controller = new OrientingController
            {
                Name = "OrientingController",
                Viewpoint = viewpoint,
                NeckPivot = pivot,
            };
            mind.AddChild(controller);
            await WaitForFramesAsync(sceneTree, 1);

            // Sustained anchor exactly 25° below the face forward axis — the stoop scenario. The head fixture
            // follows the commanded orientation but keeps its bone origin fixed, modelling a skeleton that has not
            // yet solved the published positional drive.
            double pitchRadians = DegreesToRadians(25d);
            anchor.GlobalPosition = viewpoint.GlobalPosition + (new Vector3(
                0f,
                (float)-Math.Sin(pitchRadians),
                (float)-Math.Cos(pitchRadians)) * AnchorDistanceMetres);
            vision.LookTarget = anchor;

            RunFramesWithFollowedHeadBasis(controller, head, 200);

            IKTargetIntent intent = controller.GetTargetIntent();
            Assert.True(intent.DesiredInfluence > 0.99f, "The pivot fixture never reached engaged influence.");
            AssertCentredOn(controller, anchor, viewpoint);

            // The commanded aim is measured geometrically from the authored initial forward (straight ahead) to the
            // centred intent forward, independently of the controller's internal aim state.
            Vector3 initialForward = new(0f, 0f, -1f);
            Vector3 intentForward = -intent.WorldTransform.Basis.Z;
            float commandedAimDegrees = Mathf.RadToDeg(initialForward.AngleTo(intentForward));
            Assert.True(
                commandedAimDegrees is > 24f and < 26f,
                $"The converged aim should pitch the face 25° downward, but measured {commandedAimDegrees:F2}°.");

            Vector3 headOrigin = head.GlobalPosition;
            Vector3 pivotPosition = pivot.GlobalPosition;
            Vector3 publishedOrigin = intent.WorldTransform.Origin;
            float headOffsetMetres = (headOrigin - pivotPosition).Length();
            float publishedOffsetMetres = (publishedOrigin - pivotPosition).Length();
            Assert.True(
                Mathf.Abs(publishedOffsetMetres - headOffsetMetres) < 1e-3f,
                $"Pivoting must preserve the pivot radius ({headOffsetMetres:F4} m), but the published origin sits "
                + $"{publishedOffsetMetres:F4} m from the pivot.");

            float swingDegrees = Mathf.RadToDeg((headOrigin - pivotPosition).AngleTo(publishedOrigin - pivotPosition));
            Assert.True(
                Mathf.Abs(swingDegrees - commandedAimDegrees) < 1f,
                $"The origin should swing about the pivot by the commanded aim ({commandedAimDegrees:F2}°), but "
                + $"swung {swingDegrees:F2}°.");

            Assert.True(
                publishedOrigin.Y < headOrigin.Y - 0.005f,
                "A downward aim should swing the origin down about the pivot, but it moved "
                + $"{(publishedOrigin.Y - headOrigin.Y) * 1000f:F1} mm vertically.");
            Assert.True(
                (publishedOrigin - headOrigin).Length() > 0.01f,
                "The published origin should sit away from the solved head origin to drive the CCDIK chain.");
        }
        finally
        {
            root.QueueFree();
        }
    }

    /// <summary>
    /// Without a valid neck pivot — never assigned, or assigned but since freed — the published intent origin
    /// stays at the current solved head origin (AI-009 TR 22, acceptance criterion 16), while assigning a valid
    /// pivot moves it away from the head origin.
    /// </summary>
    [Headless]
    [Fact]
    public async Task IntentOrigin_FallsBackToHeadOriginWhenNeckPivotUnsetOrInvalid()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 1);
        var root = new Node3D { Name = "OrientingControllerPivotFallbackFixture" };
        sceneTree.Root.AddChild(root);
        try
        {
            TestVision vision = new();
            TestCharacter character = new(hasComponentProjection: true, vision);
            TestMind mind = new(character);
            MutableSceneContext scene = new();
            mind.SetSceneContextLoaderForTesting(() => scene);
            root.AddChild(mind);

            var head = new Node3D { Name = "Head" };
            root.AddChild(head);
            var viewpoint = new Marker3D
            {
                Name = "Viewpoint",
                Transform = new Transform3D(Basis.Identity, new Vector3(0f, 0.1f, 0f)),
            };
            head.AddChild(viewpoint);
            var anchor = new StaticVisualCue { Name = "Anchor" };
            root.AddChild(anchor);

            var controller = new OrientingController { Name = "OrientingController", Viewpoint = viewpoint };
            mind.AddChild(controller);
            await WaitForFramesAsync(sceneTree, 1);

            // Sustained anchor 25° below the face forward axis keeps a downward aim commanded throughout.
            double pitchRadians = DegreesToRadians(25d);
            anchor.GlobalPosition = viewpoint.GlobalPosition + (new Vector3(
                0f,
                (float)-Math.Sin(pitchRadians),
                (float)-Math.Cos(pitchRadians)) * AnchorDistanceMetres);
            vision.LookTarget = anchor;

            RunFramesWithFollowedHeadBasis(controller, head, 120);
            AssertOriginAtHead(controller, head, "an unset pivot");

            var pivot = new Marker3D
            {
                Name = "NeckPivot",
                Transform = new Transform3D(Basis.Identity, new Vector3(0f, -0.15f, 0f)),
            };
            head.AddChild(pivot);
            controller.NeckPivot = pivot;
            RunFramesWithFollowedHeadBasis(controller, head, 40);

            IKTargetIntent aimedIntent = controller.GetTargetIntent();
            Assert.True(aimedIntent.DesiredInfluence > 0.99f, "Assigning a pivot should not disengage tracking.");
            Assert.True(
                (aimedIntent.WorldTransform.Origin - head.GlobalPosition).Length() > 0.01f,
                "Assigning a valid pivot should aim the origin away from the solved head origin.");

            pivot.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
            RunFramesWithFollowedHeadBasis(controller, head, 10);
            AssertOriginAtHead(controller, head, "an invalid pivot");
        }
        finally
        {
            root.QueueFree();
        }
    }

    private static void RunFramesWithFollowedHeadBasis(OrientingController controller, Node3D head, int frameCount)
    {
        for (int frame = 0; frame < frameCount; frame++)
        {
            controller._Process(FrameDelta);
            IKTargetIntent intent = controller.GetTargetIntent();
            if (intent.DesiredInfluence > 0f)
            {
                head.GlobalBasis = intent.WorldTransform.Basis;
            }
        }
    }

    private static void AssertOriginAtHead(OrientingController controller, Node3D head, string pivotDescription)
    {
        IKTargetIntent intent = controller.GetTargetIntent();
        Assert.True(intent.DesiredInfluence > 0.5f, $"With {pivotDescription}, tracking should stay engaged.");
        Assert.True(
            -intent.WorldTransform.Basis.Z.Y < -0.1f,
            $"With {pivotDescription}, the downward aim should stay active so the fallback check is not vacuous.");
        float originOffsetMetres = (intent.WorldTransform.Origin - head.GlobalPosition).Length();
        Assert.True(
            originOffsetMetres < 1e-4f,
            $"With {pivotDescription}, the intent origin should sit at the solved head origin, but strayed "
            + $"{originOffsetMetres * 1000f:F2} mm.");
    }

    /// <summary>
    /// Measures how far a basis is rolled about its own forward axis relative to the given reference up: zero for a
    /// basis whose right axis is perpendicular to the reference up (the roll-free construction), and the injected
    /// roll angle for a basis composed with a local Z-axis roll.
    /// </summary>
    private static float MeasureRollDegrees(Basis basis, Vector3 referenceUp)
    {
        Vector3 localUp = basis.Transposed() * referenceUp.Normalized();
        return Mathf.RadToDeg(Mathf.Atan2(localUp.X, localUp.Y));
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

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;

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

        public void RefreshComponents(params IComponent[] components)
        {
            _components = components;
            HasComponentProjection = true;
            _componentsRefreshed?.Invoke();
        }
    }

    private sealed class TestVision : IVision
    {
        public VisualCue? LookTarget
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

        public void SetLookTarget(VisualCue? target)
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
