using System.Text.Json;
using AlleyCat.Control.Locomotion;
using AlleyCat.IK;
using AlleyCat.Navigation;
using AlleyCat.Rigging;
using Godot;

#pragma warning disable IDE0048, IDE0072

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// Test-owned, content-independent locomotion package used by runtime integration tests. The package models the
/// ANIM-002 embedded-only contract: animations exist only as members of saved AnimationLibrary resources, and the
/// schema version 3 catalogue resolves them by animation identity. No standalone clip resources are published.
/// </summary>
internal sealed class SyntheticLocomotionRuntimeFixture : IDisposable
{
    private const string PackageDirectory = "user://synthetic-locomotion-runtime";
    private const string LocomotionLibraryFilename = "locomotion_library.tres";
    private const string IdleLibraryFilename = "idle_library.tres";
    private const string NavigationLibraryFilename = "navigation_library.tres";
    private static readonly Basis _referenceRigBasis = new(Vector3.Up, Mathf.Pi);
    private readonly string _directory;

    private SyntheticLocomotionRuntimeFixture(string directory, SyntheticLocomotionDeclaration declaration)
    {
        _directory = directory;
        Declaration = declaration;
        Root = new Node3D { Name = "SyntheticLocomotionRuntime" };
        Actor = new CharacterBody3D { Name = "Actor" };
        Skeleton = BuildSkeleton();
        Player = new AnimationPlayer
        {
            Name = "AnimationPlayer",
            RootNode = new NodePath(".."),
        };
        Root.AddChild(Actor);
        Actor.Owner = Root;
        Actor.AddChild(Skeleton);
        Skeleton.Owner = Root;
        Actor.AddChild(Player);
        Player.Owner = Root;

        LocomotionLibrary = new AnimationLibrary();
        IdleLibrary = new AnimationLibrary();
        NavigationLibrary = new AnimationLibrary();
        foreach (SyntheticClipDeclaration clip in declaration.Clips)
        {
            Animation animation = CreateAnimation(clip);
            if (clip.IsIdle)
            {
                EnsureOk(IdleLibrary.AddAnimation(clip.Name, animation), clip.Name);
            }
            else
            {
                EnsureOk(LocomotionLibrary.AddAnimation(clip.Name, animation), clip.Name);
            }
        }

        LocomotionLibraryPath = JoinResourcePath(directory, LocomotionLibraryFilename);
        IdleLibraryPath = JoinResourcePath(directory, IdleLibraryFilename);
        ContactTrackPath = JoinResourcePath(directory, "contacts.json");
        NavigationContactTrackPath = JoinResourcePath(directory, "navigation_contacts.json");
        NavigationLibraryPath = JoinResourcePath(directory, NavigationLibraryFilename);
        NavigationCataloguePath = JoinResourcePath(directory, "navigation_catalogue.json");
        AssertSaved(LocomotionLibrary, LocomotionLibraryPath);
        AssertSaved(IdleLibrary, IdleLibraryPath);
        WriteText(ContactTrackPath, CreateContactTrackJson());
        WriteText(NavigationContactTrackPath, CreateNavigationContactTrackJson());
        EnsureOk(Player.AddAnimationLibrary("locomotion", LocomotionLibrary), "locomotion library");
        EnsureOk(Player.AddAnimationLibrary("idle", IdleLibrary), "idle library");
        CreateNavigationPackage();
    }

    public SyntheticLocomotionDeclaration Declaration
    {
        get;
    }
    public Node3D Root
    {
        get;
    }
    public CharacterBody3D Actor
    {
        get;
    }
    public Skeleton3D Skeleton
    {
        get;
    }
    public AnimationPlayer Player
    {
        get;
    }
    public AnimationLibrary LocomotionLibrary
    {
        get;
    }
    public AnimationLibrary IdleLibrary
    {
        get;
    }
    public AnimationLibrary NavigationLibrary
    {
        get;
    }
    public string LocomotionLibraryPath
    {
        get;
    }
    public string IdleLibraryPath
    {
        get;
    }
    public string ContactTrackPath
    {
        get;
    }
    public string NavigationContactTrackPath
    {
        get;
    }
    public string NavigationLibraryPath
    {
        get;
    }
    public string NavigationCataloguePath
    {
        get;
    }

    public static SyntheticLocomotionRuntimeFixture Create(
        string fixtureName = "default",
        SyntheticNavigationManifest? navigationManifest = null)
    {
        SyntheticLocomotionDeclaration declaration = navigationManifest is null
            ? SyntheticLocomotionDeclaration.Default
            : SyntheticLocomotionDeclaration.Default with
            {
                NavigationManifest = navigationManifest
            };
        string directory = JoinResourcePath(PackageDirectory, fixtureName);
        string nativeDirectory = ProjectSettings.GlobalizePath(directory);
        if (Directory.Exists(nativeDirectory))
        {
            Directory.Delete(nativeDirectory, recursive: true);
        }

        _ = Directory.CreateDirectory(nativeDirectory);
        return new(directory, declaration);
    }

    /// <summary>Creates an explicitly declared navigation profile rather than silently altering the default fixture.</summary>
    public static SyntheticLocomotionRuntimeFixture Create(
        string fixtureName,
        SyntheticNavigationFixtureProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Create(fixtureName, profile.Manifest);
    }

    /// <summary>Converts authored Skeleton-local Root displacement to semantic right/forward catalogue response axes.</summary>
    public static Vector2 ToSemanticCatalogueResponse(Vector3 skeletonLocalRoot)
        => new(-skeletonLocalRoot.X, skeletonLocalRoot.Z);

    public AnimationTree CreatePlaybackTree()
    {
        var machine = new AnimationNodeStateMachine();
        foreach (SyntheticClipDeclaration clip in Declaration.Clips)
        {
            string library = clip.IsIdle ? "idle" : "locomotion";
            machine.AddNode(clip.Name, new AnimationNodeAnimation { Animation = $"{library}/{clip.Name}" });
        }

        foreach (SyntheticClipDeclaration clip in Declaration.Clips.Where(clip => clip.IsFinite))
        {
            machine.AddTransition(clip.Name, "idle", new AnimationNodeStateMachineTransition
            {
                AdvanceMode = AnimationNodeStateMachineTransition.AdvanceModeEnum.Auto,
                SwitchMode = AnimationNodeStateMachineTransition.SwitchModeEnum.AtEnd,
                AdvanceCondition = "synthetic_finite_complete",
                XfadeTime = 0f,
            });
        }

        foreach (SyntheticClipDeclaration loop in Declaration.Clips.Where(clip => clip.Loops))
        {
            foreach (SyntheticClipDeclaration finite in Declaration.Clips.Where(clip => clip.IsFinite))
            {
                machine.AddTransition(loop.Name, finite.Name, new AnimationNodeStateMachineTransition
                {
                    AdvanceMode = AnimationNodeStateMachineTransition.AdvanceModeEnum.Enabled,
                    XfadeTime = 0f,
                });
            }
        }

        var tree = new AnimationTree
        {
            Name = "AnimationTree",
            AnimPlayer = new NodePath("../AnimationPlayer"),
            RootNode = new NodePath(".."),
            TreeRoot = machine,
            RootMotionTrack = new NodePath("SyntheticSkeleton:Root"),
            CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Physics,
        };
        Actor.AddChild(tree);
        return tree;
    }

    /// <summary>
    /// Creates the complete player locomotion composition against this fixture's declared ember package.
    /// The state labels mirror the production controller protocol, while every clip, track, duration, response,
    /// and catalogue expectation remains fixture-owned.
    /// </summary>
    public SyntheticPlayerLocomotionRuntime CreatePlayerLocomotionRuntime()
    {
        var rootMotionReference = new Node3D { Name = "RootMotionReference" };
        Actor.AddChild(rootMotionReference);
        rootMotionReference.Owner = Root;

        AnimationTree tree = CreatePlayerPlaybackTree();
        var locomotion = new CharacterLocomotion
        {
            Name = "Locomotion",
            TargetCharacterBodyNode = Actor,
            AnimationTree = tree,
            RootMotionReference = rootMotionReference,
            CataloguePath = NavigationCataloguePath,
            ResidualSmoothingRate = 100f,
            ResidualEntryRampReference = 0.01f,
        };
        Actor.AddChild(locomotion);
        locomotion.Owner = Root;
        return new(Actor, rootMotionReference, Skeleton, Player, tree, locomotion, Declaration.NavigationManifest);
    }

    /// <summary>
    /// Adds a bare NPC navigation composition owned entirely by this fixture. It includes only a navigation agent,
    /// state machine, declared root tracks, and a declared catalogue; it has no production scene or library input.
    /// </summary>
    public SyntheticNavigationRuntime CreateNavigationRuntime()
    {
        var reference = new Node3D
        {
            Name = "NavigationRig",
            Transform = new Transform3D(_referenceRigBasis, Vector3.Zero),
        };
        Skeleton3D skeleton = BuildNavigationSkeleton();
        AnimationTree tree = CreateNavigationTree();
        var navigation = new SyntheticNPCNavigation
        {
            Name = "Navigation",
            Actor = Actor,
            AnimationTree = tree,
            RootMotionReference = reference,
            CataloguePath = NavigationCataloguePath,
            AwaitInstallerActivation = true,
        };
        Actor.AddChild(reference);
        reference.Owner = Root;
        reference.AddChild(skeleton);
        skeleton.Owner = Root;
        Actor.AddChild(tree);
        tree.Owner = Root;
        Actor.AddChild(navigation);
        navigation.Owner = Root;
        var physicsDriver = new SyntheticAnimationPhysicsDriver(tree)
        {
            Name = "AnimationPhysicsDriver",
            ProcessPhysicsPriority = -99,
        };
        Actor.AddChild(physicsDriver);
        physicsDriver.Owner = Root;
        return new(Root, Actor, reference, skeleton, Player, tree, navigation, physicsDriver, Declaration.NavigationManifest);
    }

    public Animation AnimationFor(string clipName)
        => Player.GetAnimation($"{(Declaration[clipName].IsIdle ? "idle" : "locomotion")}/{clipName}");

    public AuthoredPerFootContactTrackV1 LoadContactTrack()
        => AuthoredPerFootContactTrackV1.Load(File.ReadAllText(ProjectSettings.GlobalizePath(ContactTrackPath)));

    public ContactLockLiveState Live(string clipName, float phase, ulong tick, string? identity = null)
    {
        SyntheticClipDeclaration clip = Declaration[clipName];
        return new(
            true,
            clip.Name,
            clip.Identity,
            phase,
            Declaration.Variant,
            identity ?? clip.Identity,
            Declaration.Generation,
            tick,
            1f / 60f);
    }

    public void Dispose()
    {
        if (GodotObject.IsInstanceValid(Root))
        {
            // Roots outside the scene tree are freed immediately so reclamation never depends on a later frame
            // flush. Roots still inside the tree keep the deferred free, because teardown can run while tree
            // callbacks are active.
            if (Root.IsInsideTree())
            {
                Root.QueueFree();
            }
            else
            {
                Root.Free();
            }
        }
        string nativeDirectory = ProjectSettings.GlobalizePath(_directory);
        if (Directory.Exists(nativeDirectory))
        {
            Directory.Delete(nativeDirectory, recursive: true);
        }
    }

    private static Skeleton3D BuildSkeleton()
    {
        var skeleton = new Skeleton3D { Name = "SyntheticSkeleton" };
        EnsureBoneIndex(skeleton.AddBone("Root"), 0);
        skeleton.SetBoneRest(0, Transform3D.Identity);
        EnsureBoneIndex(skeleton.AddBone("Hips"), 1);
        skeleton.SetBoneParent(1, 0);
        skeleton.SetBoneRest(1, new Transform3D(Basis.Identity, new Vector3(0f, 1f, 0f)));
        EnsureBoneIndex(skeleton.AddBone("LeftUpperLeg"), 2);
        skeleton.SetBoneParent(2, 1);
        skeleton.SetBoneRest(2, new Transform3D(Basis.Identity, new Vector3(-0.2f, -0.45f, 0f)));
        EnsureBoneIndex(skeleton.AddBone("LeftLowerLeg"), 3);
        skeleton.SetBoneParent(3, 2);
        skeleton.SetBoneRest(3, new Transform3D(Basis.Identity, new Vector3(0f, -0.45f, 0f)));
        EnsureBoneIndex(skeleton.AddBone("LeftFoot"), 4);
        skeleton.SetBoneParent(4, 3);
        skeleton.SetBoneRest(4, new Transform3D(Basis.Identity, new Vector3(0f, -0.1f, 0f)));
        EnsureBoneIndex(skeleton.AddBone("LeftToes"), 5);
        skeleton.SetBoneParent(5, 4);
        skeleton.SetBoneRest(5, new Transform3D(Basis.Identity, new Vector3(0f, 0f, -0.2f)));
        EnsureBoneIndex(skeleton.AddBone("RightUpperLeg"), 6);
        skeleton.SetBoneParent(6, 1);
        skeleton.SetBoneRest(6, new Transform3D(Basis.Identity, new Vector3(0.2f, -0.45f, 0f)));
        EnsureBoneIndex(skeleton.AddBone("RightLowerLeg"), 7);
        skeleton.SetBoneParent(7, 6);
        skeleton.SetBoneRest(7, new Transform3D(Basis.Identity, new Vector3(0f, -0.45f, 0f)));
        EnsureBoneIndex(skeleton.AddBone("RightFoot"), 8);
        skeleton.SetBoneParent(8, 7);
        skeleton.SetBoneRest(8, new Transform3D(Basis.Identity, new Vector3(0f, -0.1f, 0f)));
        EnsureBoneIndex(skeleton.AddBone("RightToes"), 9);
        skeleton.SetBoneParent(9, 8);
        skeleton.SetBoneRest(9, new Transform3D(Basis.Identity, new Vector3(0f, 0f, -0.2f)));
        return skeleton;
    }

    private static Skeleton3D BuildNavigationSkeleton()
    {
        Skeleton3D skeleton = BuildSkeleton();
        skeleton.Name = "GeneralSkeleton";
        skeleton.UniqueNameInOwner = true;
        skeleton.MotionScale = 1f;
        return skeleton;
    }

    private void CreateNavigationPackage()
    {
        foreach (SyntheticNavigationClipDeclaration clip in Declaration.NavigationManifest.Clips)
        {
            Animation animation = CreateNavigationAnimation(clip);
            EnsureOk(LocomotionLibrary.AddAnimation(clip.Identity, animation), clip.Identity);
            EnsureOk(NavigationLibrary.AddAnimation(clip.Identity, animation), $"navigation {clip.Identity}");
        }

        Animation idle = CreateNavigationAnimation(new("idle", "ember_idle", 1f, true, Vector3.Zero, 0f, "confined_manoeuvre"));
        EnsureOk(IdleLibrary.AddAnimation("ember_idle", idle), "navigation idle");
        AssertSaved(LocomotionLibrary, LocomotionLibraryPath);
        AssertSaved(IdleLibrary, IdleLibraryPath);
        AssertSaved(NavigationLibrary, NavigationLibraryPath);
        WriteText(
            NavigationCataloguePath,
            Declaration.NavigationManifest.ToJson(NavigationLibraryFilename, NavigationContactTrackPath));
    }

    private static Animation CreateNavigationAnimation(SyntheticNavigationClipDeclaration clip)
    {
        var animation = new Animation
        {
            Length = clip.DurationSeconds,
            LoopMode = clip.Loops ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None,
        };
        int rootPosition = animation.AddTrack(Animation.TrackType.Position3D);
        animation.TrackSetPath(rootPosition, new NodePath("%GeneralSkeleton:Root"));
        EnsureKey(animation.PositionTrackInsertKey(rootPosition, 0f, Vector3.Zero));
        EnsureKey(animation.PositionTrackInsertKey(rootPosition, clip.DurationSeconds, clip.SkeletonLocalRootResponse));
        int rootRotation = animation.AddTrack(Animation.TrackType.Rotation3D);
        animation.TrackSetPath(rootRotation, new NodePath("%GeneralSkeleton:Root"));
        EnsureKey(animation.RotationTrackInsertKey(rootRotation, 0f, Quaternion.Identity));
        EnsureKey(animation.RotationTrackInsertKey(rootRotation, clip.DurationSeconds, Quaternion.FromEuler(new Vector3(0f, clip.YawRadians, 0f))));
        return animation;
    }

    private AnimationTree CreateNavigationTree()
    {
        var states = new AnimationNodeStateMachine();
        states.AddNode("NPCIdle", new AnimationNodeAnimation { Animation = "idle/ember_idle" });
        states.AddTransition("Start", "NPCIdle", new AnimationNodeStateMachineTransition
        {
            AdvanceMode = AnimationNodeStateMachineTransition.AdvanceModeEnum.Enabled,
            XfadeTime = 0f,
        });
        AddFiniteState("NPCI2FL", "i2f_l");
        AddFiniteState("NPCI2FR", "i2f_r");
        AddFiniteState("NPCI2BL", "i2b_l");
        AddFiniteState("NPCI2BR", "i2b_r");
        AddFiniteState("NPCF2IL", "f2i_l");
        AddFiniteState("NPCF2IR", "f2i_r");
        AddFiniteState("NPCB2IL", "b2i_l");
        AddFiniteState("NPCB2IR", "b2i_r");
        AddFiniteState("NPCPivotL", "pl45");
        AddFiniteState("NPCPivotR", "pr45");
        AddFiniteState("NPCSideStepL", "sl");
        AddFiniteState("NPCSideStepR", "sr");
        AddFiniteState("NPCShortFL", "i2f_l");
        AddFiniteState("NPCShortFLExit", "f2i_r");
        AddFiniteState("NPCShortFR", "i2f_r");
        AddFiniteState("NPCShortFRExit", "f2i_l");
        AddFiniteState("NPCShortBL", "i2b_l");
        AddFiniteState("NPCShortBLExit", "b2i_r");
        AddFiniteState("NPCShortBR", "i2b_r");
        AddFiniteState("NPCShortBRExit", "b2i_l");
        states.AddNode("NPCTravel", CreateTravelTree());
        foreach (StringName from in new[]
                 {
                     new StringName("NPCIdle"), new StringName("NPCI2FL"), new StringName("NPCI2FR"), new StringName("NPCI2BL"), new StringName("NPCI2BR"),
                     new StringName("NPCTravel"), new StringName("NPCF2IL"), new StringName("NPCF2IR"), new StringName("NPCB2IL"), new StringName("NPCB2IR"),
                     new StringName("NPCPivotL"), new StringName("NPCPivotR"), new StringName("NPCSideStepL"), new StringName("NPCSideStepR"),
                     new StringName("NPCShortFL"), new StringName("NPCShortFLExit"), new StringName("NPCShortFR"), new StringName("NPCShortFRExit"),
                     new StringName("NPCShortBL"), new StringName("NPCShortBLExit"), new StringName("NPCShortBR"), new StringName("NPCShortBRExit"),
                 })
        {
            foreach (StringName to in new[] { new StringName("NPCIdle"), new StringName("NPCTravel"), new StringName("NPCF2IL"), new StringName("NPCF2IR"), new StringName("NPCB2IL"), new StringName("NPCB2IR") })
            {
                if (from != to)
                {
                    states.AddTransition(from, to, new AnimationNodeStateMachineTransition { AdvanceMode = AnimationNodeStateMachineTransition.AdvanceModeEnum.Enabled, XfadeTime = 0.1f });
                }
            }
        }

        var root = new AnimationNodeBlendTree();
        root.AddNode("States", states);
        root.ConnectNode("output", 0, "States");
        return new AnimationTree
        {
            Name = "AnimationTree",
            AnimPlayer = new NodePath("../AnimationPlayer"),
            RootNode = new NodePath(".."),
            RootMotionTrack = new NodePath("%GeneralSkeleton:Root"),
            TreeRoot = root,
            CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Physics,
        };

        void AddFiniteState(string state, string role)
        {
            states.AddNode(state, new AnimationNodeAnimation { Animation = $"locomotion/{Declaration.NavigationManifest[role].Identity}" });
        }
    }

    private AnimationTree CreatePlayerPlaybackTree()
    {
        var states = new AnimationNodeStateMachine();
        states.AddNode(SyntheticPlayerLocomotionRuntime.IdleState, new AnimationNodeAnimation { Animation = "idle/ember_idle" });
        AddFiniteState(SyntheticPlayerLocomotionRuntime.EntryForwardLeftState, "i2f_l");
        AddFiniteState(SyntheticPlayerLocomotionRuntime.EntryForwardRightState, "i2f_r");
        AddFiniteState(SyntheticPlayerLocomotionRuntime.EntryBackwardLeftState, "i2b_l");
        AddFiniteState(SyntheticPlayerLocomotionRuntime.EntryBackwardRightState, "i2b_r");
        AddFiniteState(SyntheticPlayerLocomotionRuntime.ExitForwardLeftState, "f2i_l");
        AddFiniteState(SyntheticPlayerLocomotionRuntime.ExitForwardRightState, "f2i_r");
        AddFiniteState(SyntheticPlayerLocomotionRuntime.ExitBackwardLeftState, "b2i_l");
        AddFiniteState(SyntheticPlayerLocomotionRuntime.ExitBackwardRightState, "b2i_r");
        AddFiniteState(SyntheticPlayerLocomotionRuntime.PivotLeftState, "pl45");
        AddFiniteState(SyntheticPlayerLocomotionRuntime.PivotRightState, "pr45");
        AddFiniteState(SyntheticPlayerLocomotionRuntime.SideStepLeftState, "sl");
        AddFiniteState(SyntheticPlayerLocomotionRuntime.SideStepRightState, "sr");
        states.AddNode(SyntheticPlayerLocomotionRuntime.TravelState, CreatePlayerTravelTree());

        AddTransitions("Start", [SyntheticPlayerLocomotionRuntime.IdleState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.IdleState, [
            SyntheticPlayerLocomotionRuntime.EntryForwardLeftState, SyntheticPlayerLocomotionRuntime.EntryForwardRightState,
            SyntheticPlayerLocomotionRuntime.EntryBackwardLeftState, SyntheticPlayerLocomotionRuntime.EntryBackwardRightState,
            SyntheticPlayerLocomotionRuntime.PivotLeftState, SyntheticPlayerLocomotionRuntime.PivotRightState,
            SyntheticPlayerLocomotionRuntime.SideStepLeftState, SyntheticPlayerLocomotionRuntime.SideStepRightState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.EntryForwardLeftState, [SyntheticPlayerLocomotionRuntime.TravelState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.EntryForwardRightState, [SyntheticPlayerLocomotionRuntime.TravelState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.EntryBackwardLeftState, [SyntheticPlayerLocomotionRuntime.TravelState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.EntryBackwardRightState, [SyntheticPlayerLocomotionRuntime.TravelState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.TravelState, [
            SyntheticPlayerLocomotionRuntime.ExitForwardLeftState, SyntheticPlayerLocomotionRuntime.ExitForwardRightState,
            SyntheticPlayerLocomotionRuntime.ExitBackwardLeftState, SyntheticPlayerLocomotionRuntime.ExitBackwardRightState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.ExitForwardLeftState, [SyntheticPlayerLocomotionRuntime.IdleState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.ExitForwardRightState, [SyntheticPlayerLocomotionRuntime.IdleState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.ExitBackwardLeftState, [SyntheticPlayerLocomotionRuntime.IdleState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.ExitBackwardRightState, [SyntheticPlayerLocomotionRuntime.IdleState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.PivotLeftState, [SyntheticPlayerLocomotionRuntime.IdleState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.PivotRightState, [SyntheticPlayerLocomotionRuntime.IdleState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.SideStepLeftState, [SyntheticPlayerLocomotionRuntime.IdleState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.SideStepRightState, [SyntheticPlayerLocomotionRuntime.IdleState]);

        var root = new AnimationNodeBlendTree();
        root.AddNode("States", states);
        root.ConnectNode("output", 0, "States");
        return new AnimationTree
        {
            Name = "AnimationTree",
            AnimPlayer = new NodePath("../AnimationPlayer"),
            RootNode = new NodePath(".."),
            RootMotionTrack = new NodePath("SyntheticSkeleton:Root"),
            TreeRoot = root,
            CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Physics,
            Active = true,
        };

        void AddFiniteState(string state, string role)
        {
            states.AddNode(state, new AnimationNodeAnimation { Animation = $"locomotion/{Declaration.NavigationManifest[role].Identity}" });
        }

        void AddTransitions(string from, IEnumerable<string> targets)
        {
            foreach (string to in targets)
            {
                states.AddTransition(from, to, new AnimationNodeStateMachineTransition
                {
                    AdvanceMode = AnimationNodeStateMachineTransition.AdvanceModeEnum.Enabled,
                    XfadeTime = 0.05f,
                });
            }
        }
    }

    private AnimationNodeBlendTree CreatePlayerTravelTree()
    {
        AnimationNodeBlendSpace2D forwardLeft = CreateTravelSpace("wf", "xl_l", "xr_l");
        AnimationNodeBlendSpace2D forwardRight = CreateTravelSpace("wf", "xl_r", "xr_r");
        AnimationNodeBlendSpace2D directionalLeft = CreateTravelSpace("wf", "wb", "xl_l");
        AnimationNodeBlendSpace2D directionalRight = CreateTravelSpace("wf", "wb", "xr_r");
        var tree = new AnimationNodeBlendTree();
        tree.AddNode("ForwardArcL", forwardLeft);
        tree.AddNode("ForwardArcR", forwardRight);
        tree.AddNode("DirectionalL", directionalLeft);
        tree.AddNode("DirectionalR", directionalRight);
        tree.AddNode("PhaseForward", new AnimationNodeBlend2());
        tree.AddNode("PhaseDirectional", new AnimationNodeBlend2());
        tree.AddNode("Domain", new AnimationNodeBlend2());
        tree.AddNode("Rate", new AnimationNodeTimeScale());
        tree.ConnectNode("PhaseForward", 0, "ForwardArcL");
        tree.ConnectNode("PhaseForward", 1, "ForwardArcR");
        tree.ConnectNode("PhaseDirectional", 0, "DirectionalL");
        tree.ConnectNode("PhaseDirectional", 1, "DirectionalR");
        tree.ConnectNode("Domain", 0, "PhaseForward");
        tree.ConnectNode("Domain", 1, "PhaseDirectional");
        tree.ConnectNode("Rate", 0, "Domain");
        tree.ConnectNode("output", 0, "Rate");
        return tree;
    }

    private AnimationNodeBlendSpace2D CreateTravelSpace(string centreRole, string leftRole, string rightRole)
    {
        var space = new AnimationNodeBlendSpace2D
        {
            MinSpace = new Vector2(-1f, -1f),
            MaxSpace = new Vector2(1f, 1f),
            AutoTriangles = true,
            SyncMode = AnimationNodeBlendSpace2D.SyncModeEnum.CyclicConstant,
            CyclicLength = Declaration.NavigationManifest[centreRole].DurationSeconds,
        };
        space.AddBlendPoint(CreateAnimationNode(centreRole), new Vector2(0f, 1f), -1, centreRole);
        space.AddBlendPoint(CreateAnimationNode(leftRole), new Vector2(-1f, 0f), -1, leftRole);
        space.AddBlendPoint(CreateAnimationNode(rightRole), new Vector2(1f, 0f), -1, rightRole);
        return space;
    }

    private AnimationNodeAnimation CreateAnimationNode(string role)
        => new()
        {
            Animation = $"locomotion/{Declaration.NavigationManifest[role].Identity}"
        };

    private AnimationNodeBlendTree CreateTravelTree()
    {
        var space = new AnimationNodeBlendSpace2D { MinSpace = new Vector2(-1f, -1f), MaxSpace = new Vector2(1f, 1f), AutoTriangles = true, CyclicLength = 0.8f };
        foreach ((string name, Vector2 position, string role) in new[]
                 {
                      ("wf", new Vector2(0f, 1f), "wf"),
                      ("xl", new Vector2(-1f, 0f), "xl_l"),
                      ("xr", new Vector2(1f, 0f), "xr_l"),
                 })
        {
            space.AddBlendPoint(new AnimationNodeAnimation { Animation = $"locomotion/{Declaration.NavigationManifest[role].Identity}" }, position, -1, name);
        }
        var tree = new AnimationNodeBlendTree();
        tree.AddNode("Sustained", space);
        tree.AddNode("Rate", new AnimationNodeTimeScale());
        tree.ConnectNode("Rate", 0, "Sustained");
        tree.ConnectNode("output", 0, "Rate");
        return tree;
    }

    private static Animation CreateAnimation(SyntheticClipDeclaration clip)
    {
        var animation = new Animation { Length = clip.DurationSeconds, LoopMode = clip.Loops ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None };
        int rootPosition = animation.AddTrack(Animation.TrackType.Position3D);
        animation.TrackSetPath(rootPosition, new NodePath("SyntheticSkeleton:Root"));
        EnsureKey(animation.PositionTrackInsertKey(rootPosition, 0f, Vector3.Zero));
        EnsureKey(animation.PositionTrackInsertKey(rootPosition, clip.DurationSeconds, clip.RootResponse));
        int rootRotation = animation.AddTrack(Animation.TrackType.Rotation3D);
        animation.TrackSetPath(rootRotation, new NodePath("SyntheticSkeleton:Root"));
        EnsureKey(animation.RotationTrackInsertKey(rootRotation, 0f, Quaternion.Identity));
        EnsureKey(animation.RotationTrackInsertKey(rootRotation, clip.DurationSeconds, Quaternion.FromEuler(new Vector3(0f, clip.YawRadians, 0f))));
        AddFootTrack(animation, "LeftFoot", clip.LeftFootStart, clip.LeftFootEnd, clip.DurationSeconds);
        AddFootTrack(animation, "RightFoot", clip.RightFootStart, clip.RightFootEnd, clip.DurationSeconds);
        return animation;
    }

    private static void AddFootTrack(Animation animation, string bone, Vector3 start, Vector3 end, float duration)
    {
        int track = animation.AddTrack(Animation.TrackType.Position3D);
        animation.TrackSetPath(track, new NodePath($"SyntheticSkeleton:{bone}"));
        EnsureKey(animation.PositionTrackInsertKey(track, 0f, start));
        EnsureKey(animation.PositionTrackInsertKey(track, duration, end));
    }

    private string CreateContactTrackJson()
        => JsonSerializer.Serialize(new
        {
            schema_version = 1,
            package_identity = Declaration.Package,
            source_derived = new
            {
                schema_version = 3,
                method = "phase_guided_relative_motion_segmentation_v1",
                generation = Declaration.Generation,
                source_package = new
                {
                    package = Declaration.Package,
                    variant = Declaration.Variant
                },
                paths_relative_to_processed_package = new
                {
                    processed_index = "processed-index.json",
                    retargeted_blend = "declared.blend"
                },
                input_digests = new
                {
                    processed_index_sha256 = "synthetic",
                    retargeted_blend_sha256 = "synthetic"
                },
                phase_policy = new
                {
                    identity = "synthetic-phase-policy",
                    digest = "synthetic"
                },
                bone_mapping = new
                {
                    identity = "synthetic-bone-mapping",
                    digest = "synthetic",
                    feet = new
                    {
                        left = "declared",
                        right = "declared"
                    }
                },
                sampling = "native_fps",
                configuration = new
                {
                    declared = true
                },
                configuration_digest = "synthetic",
                confidence_weights = new
                {
                    semantic_event_anchor_agreement = 0.22f,
                    within_state_cohesion = 0.18f,
                    between_state_separation = 0.22f,
                    transition_persistence_sharpness = 0.16f,
                    contextual_opposite_consistency = 0.12f,
                    ambiguity_margin = 0.10f
                },
                publication_eligible = true,
                required_refusals = Array.Empty<string>(),
                clips = Declaration.Clips.Select(clip => new { identity = clip.Identity, paths = Array.Empty<object>() }),
                role_contact_matrix = Array.Empty<object>(),
            },
            samples = Declaration.Clips.SelectMany(clip => clip.ContactWindows.Select(window => new
            {
                schema_version = 1,
                role = clip.Name,
                foot = window.Foot == LimbSide.Left ? "left" : "right",
                lifecycle = window.Lifecycle,
                timing = window.Timing,
                validity = "valid",
                confidence = 1f,
                provenance = Declaration.Provenance,
                clip = clip.Identity,
                variant = Declaration.Variant,
                reference = Declaration.Reference,
                identity = clip.Identity,
                generation = Declaration.Generation,
            })),
        });

    private string CreateNavigationContactTrackJson()
    {
        SyntheticNavigationManifest manifest = Declaration.NavigationManifest;
        var samples = manifest.Clips.SelectMany(clip => clip.ContactWindows.Select(window => new
        {
            schema_version = 1,
            role = clip.Role,
            foot = window.Foot == LimbSide.Left ? "left" : "right",
            lifecycle = window.Lifecycle,
            timing = window.Timing,
            validity = "valid",
            confidence = 1f,
            provenance = Declaration.Provenance,
            clip = clip.Identity,
            variant = manifest.Variant,
            reference = Declaration.Reference,
            identity = clip.Identity,
            generation = manifest.Generation,
        }).ToArray()).ToArray();
        var matrix = manifest.Clips.SelectMany(clip => new[] { LimbSide.Left, LimbSide.Right }.Select(foot => new
        {
            role = clip.Role,
            foot = foot == LimbSide.Left ? "left" : "right",
            identity = clip.Identity,
            outcome = clip.ContactWindows.Any(window => window.Foot == foot) ? "emitted" : "fallback",
        })).ToArray();
        return JsonSerializer.Serialize(new
        {
            schema_version = 1,
            package_identity = manifest.ContactPackageIdentity,
            samples,
            source_derived = new
            {
                schema_version = 5,
                method = "phase_seeded_relative_motion_segmentation_v2",
                generation = manifest.Generation,
                publication_eligible = true,
                required_refusals = Array.Empty<string>(),
                source_package = new
                {
                    package = manifest.Package,
                    variant = manifest.Variant,
                    action_count = manifest.Clips.Count
                },
                paths_relative_to_processed_package = new
                {
                    processed_index = "processed-index.json",
                    retargeted_blend = "declared.blend"
                },
                input_digests = new
                {
                    processed_index_sha256 = "synthetic",
                    retargeted_blend_sha256 = "synthetic"
                },
                phase_policy = new
                {
                    identity = "synthetic_policy",
                    digest = "synthetic"
                },
                bone_mapping = new
                {
                    identity = "synthetic_mapping",
                    digest = "synthetic"
                },
                input_contracts = new[] { "synthetic_contract" },
                phase_seed_policy = new
                {
                    sample_domain = "unique_runtime_samples"
                },
                sampling = "native_fps",
                configuration = new
                {
                    declared = true
                },
                configuration_digest = "synthetic",
                confidence_weights = new
                {
                    declared = 1f
                },
                clips = manifest.Clips.Select(clip => new { identity = clip.Identity, role = clip.Role }).ToArray(),
                role_contact_matrix = matrix,
            },
        });
    }

    private static void AssertSaved(Resource resource, string path)
    {
        Error result = ResourceSaver.Save(resource, path);
        if (result != Error.Ok)
        {
            throw new InvalidOperationException($"Could not save synthetic fixture resource '{path}': {result}.");
        }
    }

    private static void EnsureOk(Error result, string resource)
    {
        if (result != Error.Ok)
        {
            throw new InvalidOperationException($"Could not add synthetic {resource}: {result}.");
        }
    }

    private static void EnsureBoneIndex(int actual, int expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"Synthetic skeleton bone index {actual} did not match {expected}.");
        }
    }

    private static void EnsureKey(int index)
    {
        if (index < 0)
        {
            throw new InvalidOperationException("Could not add a synthetic animation key.");
        }
    }

    private static void WriteText(string path, string text)
    {
        using Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write)
            ?? throw new InvalidOperationException($"Could not create synthetic fixture declaration '{path}'.");
        _ = file.StoreString(text);
    }

    private static string JoinResourcePath(string directory, string name) => $"{directory.TrimEnd('/')}/{name}";
}

internal sealed record SyntheticLocomotionDeclaration(
    string Package,
    string Variant,
    string Generation,
    string Provenance,
    string Reference,
    IReadOnlyList<SyntheticClipDeclaration> Clips)
{
    public static SyntheticLocomotionDeclaration Default
    {
        get;
    } = new(
        "synthetic-locomotion",
        "fixture",
        "fixture-generation-1",
        "test declaration",
        "test reference",
        [
            new("stride_loop", "ember_stride_loop", 0.8f, true, new Vector3(0f, 0f, -0.8f), 0f),
            new("reverse_loop", "ember_reverse_loop", 0.8f, true, new Vector3(0f, 0f, 0.6f), 0f),
            new("pivot_clockwise", "ember_pivot_clockwise", 0.5f, true, Vector3.Zero, -Mathf.Pi / 2f),
            new("pivot_counterclockwise", "ember_pivot_counterclockwise", 0.5f, true, Vector3.Zero, Mathf.Pi / 2f),
            new("exit_alpha", "ember_exit_alpha", 0.6f, false, new Vector3(0f, 0f, -0.2f), 0f,
                DeclaredContactWindows: [
                    new(LimbSide.Right, "acquire", [0d]), new(LimbSide.Right, "support", [0d, 0.45d]), new(LimbSide.Right, "release", [0.45d]),
                    new(LimbSide.Left, "acquire", [0.60d]), new(LimbSide.Left, "support", [0.60d, 1d]), new(LimbSide.Left, "release", [1d])]),
            new("exit_beta", "ember_exit_beta", 0.6f, false, new Vector3(0.1f, 0f, -0.2f), 0f,
                DeclaredContactWindows: [
                    new(LimbSide.Left, "acquire", [0d]), new(LimbSide.Left, "support", [0d, 0.45d]), new(LimbSide.Left, "release", [0.45d]),
                    new(LimbSide.Right, "acquire", [0.60d]), new(LimbSide.Right, "support", [0.60d, 1d]), new(LimbSide.Right, "release", [1d])]),
            new("idle", "ember_idle", 1f, true, Vector3.Zero, 0f, IsIdle: true),
        ]);

    public SyntheticClipDeclaration this[string name] => Clips.Single(clip => clip.Name == name);

    public SyntheticNavigationManifest NavigationManifest { get; init; } = SyntheticNavigationManifest.Default;
}

internal sealed record SyntheticClipDeclaration(
    string Name,
    string Identity,
    float DurationSeconds,
    bool Loops,
    Vector3 RootResponse,
    float YawRadians,
    Vector3? DeclaredLeftFootStart = null,
    Vector3? DeclaredLeftFootEnd = null,
    Vector3? DeclaredRightFootStart = null,
    Vector3? DeclaredRightFootEnd = null,
    IReadOnlyList<SyntheticContactWindow>? DeclaredContactWindows = null,
    bool IsIdle = false)
{
    public bool IsFinite => !Loops && !IsIdle;
    public Vector3 LeftFootStart { get; } = DeclaredLeftFootStart ?? new Vector3(-0.2f, -1f, 0f);
    public Vector3 LeftFootEnd { get; } = DeclaredLeftFootEnd ?? new Vector3(-0.2f, -1f, -0.25f);
    public Vector3 RightFootStart { get; } = DeclaredRightFootStart ?? new Vector3(0.2f, -1f, 0f);
    public Vector3 RightFootEnd { get; } = DeclaredRightFootEnd ?? new Vector3(0.2f, -1f, -0.25f);
    public IReadOnlyList<SyntheticContactWindow> ContactWindows { get; } = DeclaredContactWindows ?? [];
}

internal sealed record SyntheticContactWindow(LimbSide Foot, string Lifecycle, IReadOnlyList<double> Timing);

/// <summary>Fixture-owned navigation roles and package contract. Values are deliberately arbitrary and generic.</summary>
internal sealed record SyntheticNavigationManifest(
    IReadOnlyList<SyntheticNavigationClipDeclaration> Clips,
    IReadOnlyList<SyntheticNavigationMapping>? DeclaredMappings = null,
    IReadOnlyList<StandingLocomotionShortBoutPair>? DeclaredShortBoutPairs = null,
    string Package = "locomotion",
    string Variant = "ember",
    string ContactPackageIdentity = "synthetic_contact_package",
    string Generation = "synthetic_contact_generation")
{
    private static readonly StandingLocomotionCapability[] _capabilities =
        [.. Enum.GetValues<StandingLocomotionCapability>().Reverse()];

    public static SyntheticNavigationManifest Default { get; } = new([.. _capabilities.Select(CreateClip)]);

    /// <summary>
    /// A serviceable ordinary-forward to finite-forward-exit contract. The forward gait is 0.285714 m / 0.80 s =
    /// 0.357143 m/s and both f2i roots are 0.20 m / 0.56 s = 0.357143 m/s. With the fixture's 180° reference root,
    /// positive skeleton-local Z resolves to world -Z; its declared target is therefore forward world -Z.
    /// The finite exit's 0.20 m authored response leaves the production convergence window to correct only its
    /// bounded residual, rather than accepting a discontinuous 0.20 m / 0.56 s exit.
    /// </summary>
    public static SyntheticNavigationManifest HealthyFiniteExit { get; } = CreateHealthyFiniteExit();

    public SyntheticNavigationClipDeclaration this[string semanticAlias]
        => Clips.Single(clip => clip.Capability == CapabilityForAlias(semanticAlias));

    public SyntheticNavigationClipDeclaration this[StandingLocomotionCapability capability]
        => Clips.Single(clip => clip.Capability == capability);

    /// <summary>Serialises the fixture's embedded-only runtime catalogue (schema version 3).</summary>
    public string ToJson(string animationLibraryFilename, string contactTrackPath)
    {
        IReadOnlyList<SyntheticNavigationMapping> mappings = DeclaredMappings ?? CreateMappings();
        var root = new Dictionary<string, object?>
        {
            ["schema_version"] = 3,
            ["package"] = Package,
            ["package_variant"] = Variant,
            ["animation_library"] = animationLibraryFilename,
            ["clip_count"] = Clips.Count,
            ["clips"] = Clips.Select(clip => CreateClipJson(clip, Clips, mappings)).ToArray(),
            ["short_bout_splices"] = new Dictionary<string, object>
            {
                ["pairs"] = (DeclaredShortBoutPairs ?? CreateShortBoutPairs()).Select(pair => new
                {
                    entry_role = pair.EntryRole,
                    entry_release_event = pair.EntryReleaseEvent,
                    exit_role = pair.ExitRole,
                    exit_release_event = pair.ExitReleaseEvent,
                }).ToArray(),
            },
            ["contact_evidence"] = new Dictionary<string, object>
            {
                ["schema_version"] = 1,
                ["path"] = contactTrackPath,
                ["package_identity"] = ContactPackageIdentity,
                ["generation"] = Generation,
                ["identities"] = Clips.Where(clip => clip.ContactWindows.Count > 0).Select(clip => clip.Identity).ToArray(),
                ["diagnostic_provenance_non_runtime_authoritative"] = new Dictionary<string, string>
                {
                    ["processed_index"] = "user://synthetic-locomotion-runtime/processed-index.json",
                    ["input"] = "user://synthetic-locomotion-runtime/authored_per_foot_contacts_v1.json",
                },
            },
        };
        return JsonSerializer.Serialize(root, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
    }

    private static Dictionary<string, object?> CreateClipJson(
        SyntheticNavigationClipDeclaration clip,
        IReadOnlyList<SyntheticNavigationClipDeclaration> clips,
        IReadOnlyList<SyntheticNavigationMapping> mappings)
    {
        var result = new Dictionary<string, object?>
        {
            ["identity"] = clip.Identity,
            ["role"] = clip.Role,
            ["capability"] = CapabilityID(clip.Capability),
            ["boundary_kind"] = clip.BoundaryKind,
            ["loop_intent"] = clip.Loops,
            ["duration_seconds"] = clip.DurationSeconds,
            ["phase_events"] = CreatePhaseEvents(clip),
            ["stitch_mappings"] = CreateStitchMappings(clip, clips, mappings),
            ["endpoint_root_displacement_godot"] = new[]
            {
                clip.SkeletonLocalRootResponse.X,
                clip.SkeletonLocalRootResponse.Y,
                clip.SkeletonLocalRootResponse.Z,
            },
            ["endpoint_local_y_yaw"] = clip.YawRadians,
        };
        if (clip.IsCyclic)
        {
            result.Add("response", CreateResponse(clip));
        }
        return result;
    }

    private static object CreatePhaseEvents(SyntheticNavigationClipDeclaration clip) => clip.IsConfined
        ? new Dictionary<string, object> { ["applicability"] = "inapplicable" }
        : new Dictionary<string, object>
        {
            ["applicability"] = "applicable",
            ["contract"] = "anim002_post_repair_directional_phase_events_v2",
            ["evaluation_stage"] = "final_repaired_evaluated_hierarchy",
            ["sample_semantics"] = new
            {
                conceptual_sample_count = (int)Math.Round(clip.DurationSeconds * 100f) + 1,
                duplicate_endpoint = clip.Loops,
                is_loop = clip.Loops,
                unique_runtime_sample_count = (int)Math.Round(clip.DurationSeconds * 100f) + (clip.Loops ? 0 : 1),
            },
            ["timeline"] = new { fps = 100, frame_start = 0, frame_end = (int)Math.Round(clip.DurationSeconds * 100f) },
            ["duration_seconds"] = clip.DurationSeconds,
            ["events"] = (clip.DeclaredPhaseEvents ?? EventsFor(clip.Capability, clip.DurationSeconds)).Select(phase => new Dictionary<string, object?>
            {
                ["accepted"] = true,
                ["id"] = phase.ID,
                ["time_seconds"] = phase.Time,
                ["anatomical_side"] = phase.Side,
                ["boundary_context"] = phase.BoundaryContext,
                ["canonicalised_from_duplicate_endpoint"] = null,
                ["confidence"] = 1d,
                ["derivation_event_id"] = phase.ID,
                ["derived_output_frame"] = (int)Math.Round(phase.Time * 100f),
                ["event_kind"] = phase.Kind,
                ["measurement"] = new Dictionary<string, object>(),
                ["output_frame"] = (int)Math.Round(phase.Time * 100f),
                ["reason"] = "accepted synthetic directional phase transition",
                ["repair_frame_delta"] = 0,
                ["seed_displacement_provenance"] = new Dictionary<string, object>(),
            }).ToArray(),
        };

    private static object CreateStitchMappings(
        SyntheticNavigationClipDeclaration clip,
        IReadOnlyList<SyntheticNavigationClipDeclaration> clips,
        IReadOnlyList<SyntheticNavigationMapping> mappings) => clip.IsConfined
        ? new Dictionary<string, object> { ["applicability"] = "inapplicable" }
        : new Dictionary<string, object>
        {
            ["applicability"] = "applicable",
            ["mappings"] = mappings.Where(mapping => IsMappingDeclaredBy(clip.Role, clips, mapping))
                .Select(mapping => new
                {
                    from = new
                    {
                        role = mapping.FromRole,
                        @event = mapping.EventID
                    },
                    to = new
                    {
                        role = mapping.ToRole,
                        @event = mapping.ToEventID
                    },
                }).ToArray(),
        };

    private static object CreateResponse(SyntheticNavigationClipDeclaration clip) => new
    {
        sample_count = 2,
        samples = new[]
        {
            new { time_seconds = 0f, cumulative_local_planar = new[] { 0f, 0f }, yaw = 0f, planar_velocity = new[] { clip.SemanticCatalogueRootResponse.X / clip.DurationSeconds, clip.SemanticCatalogueRootResponse.Y / clip.DurationSeconds }, yaw_rate = clip.YawRadians / clip.DurationSeconds },
            new { time_seconds = clip.DurationSeconds, cumulative_local_planar = new[] { clip.SemanticCatalogueRootResponse.X, clip.SemanticCatalogueRootResponse.Y }, yaw = clip.YawRadians, planar_velocity = new[] { clip.SemanticCatalogueRootResponse.X / clip.DurationSeconds, clip.SemanticCatalogueRootResponse.Y / clip.DurationSeconds }, yaw_rate = clip.YawRadians / clip.DurationSeconds },
        },
    };

    private static bool IsMappingDeclaredBy(
        string role,
        IReadOnlyList<SyntheticNavigationClipDeclaration> clips,
        SyntheticNavigationMapping mapping)
    {
        bool transition = clips.Single(clip => clip.Role == mapping.FromRole).BoundaryKind == "transition"
            || clips.Single(clip => clip.Role == mapping.ToRole).BoundaryKind == "transition";
        return mapping.FromRole == role || (transition && mapping.ToRole == role);
    }

    private static StandingLocomotionCapability CapabilityForAlias(string alias) => alias switch
    {
        "wf" => StandingLocomotionCapability.CyclicForwardPrimary,
        "wb" => StandingLocomotionCapability.CyclicBackwardPrimary,
        "xl_l" => StandingLocomotionCapability.CyclicLateralLeftLeftLane,
        "xl_r" => StandingLocomotionCapability.CyclicLateralLeftRightLane,
        "xr_l" => StandingLocomotionCapability.CyclicLateralRightLeftLane,
        "xr_r" => StandingLocomotionCapability.CyclicLateralRightRightLane,
        "al_l" => StandingLocomotionCapability.CyclicArcLeftLeftLane,
        "al_r" => StandingLocomotionCapability.CyclicArcLeftRightLane,
        "ar_l" => StandingLocomotionCapability.CyclicArcRightLeftLane,
        "ar_r" => StandingLocomotionCapability.CyclicArcRightRightLane,
        "sl" => StandingLocomotionCapability.ConfinedSideStepLeft,
        "sr" => StandingLocomotionCapability.ConfinedSideStepRight,
        "pl45" => StandingLocomotionCapability.ConfinedPivotLeft,
        "pr45" => StandingLocomotionCapability.ConfinedPivotRight,
        "i2f_l" => StandingLocomotionCapability.EntryForwardLeft,
        "i2f_r" => StandingLocomotionCapability.EntryForwardRight,
        "f2i_l" => StandingLocomotionCapability.ExitForwardLeft,
        "f2i_r" => StandingLocomotionCapability.ExitForwardRight,
        "i2b_l" => StandingLocomotionCapability.EntryBackwardLeft,
        "i2b_r" => StandingLocomotionCapability.EntryBackwardRight,
        "b2i_l" => StandingLocomotionCapability.ExitBackwardLeft,
        "b2i_r" => StandingLocomotionCapability.ExitBackwardRight,
        _ => throw new InvalidOperationException($"Unknown fixture semantic alias '{alias}'."),
    };

    private static string CapabilityID(StandingLocomotionCapability capability) => capability switch
    {
        StandingLocomotionCapability.CyclicForwardPrimary => "cyclic_forward_primary",
        StandingLocomotionCapability.CyclicBackwardPrimary => "cyclic_backward_primary",
        StandingLocomotionCapability.CyclicLateralLeftLeftLane => "cyclic_lateral_left_left_lane",
        StandingLocomotionCapability.CyclicLateralLeftRightLane => "cyclic_lateral_left_right_lane",
        StandingLocomotionCapability.CyclicLateralRightLeftLane => "cyclic_lateral_right_left_lane",
        StandingLocomotionCapability.CyclicLateralRightRightLane => "cyclic_lateral_right_right_lane",
        StandingLocomotionCapability.CyclicArcLeftLeftLane => "cyclic_arc_left_left_lane",
        StandingLocomotionCapability.CyclicArcLeftRightLane => "cyclic_arc_left_right_lane",
        StandingLocomotionCapability.CyclicArcRightLeftLane => "cyclic_arc_right_left_lane",
        StandingLocomotionCapability.CyclicArcRightRightLane => "cyclic_arc_right_right_lane",
        StandingLocomotionCapability.ConfinedSideStepLeft => "confined_side_step_left",
        StandingLocomotionCapability.ConfinedSideStepRight => "confined_side_step_right",
        StandingLocomotionCapability.ConfinedPivotLeft => "confined_pivot_left",
        StandingLocomotionCapability.ConfinedPivotRight => "confined_pivot_right",
        StandingLocomotionCapability.EntryForwardLeft => "entry_forward_left",
        StandingLocomotionCapability.EntryForwardRight => "entry_forward_right",
        StandingLocomotionCapability.ExitForwardLeft => "exit_forward_left",
        StandingLocomotionCapability.ExitForwardRight => "exit_forward_right",
        StandingLocomotionCapability.EntryBackwardLeft => "entry_backward_left",
        StandingLocomotionCapability.EntryBackwardRight => "entry_backward_right",
        StandingLocomotionCapability.ExitBackwardLeft => "exit_backward_left",
        StandingLocomotionCapability.ExitBackwardRight => "exit_backward_right",
        _ => throw new InvalidOperationException($"Unknown fixture capability '{capability}'."),
    };

    private static SyntheticNavigationClipDeclaration CreateClip(StandingLocomotionCapability capability)
    {
        bool confined = capability is StandingLocomotionCapability.ConfinedSideStepLeft
            or StandingLocomotionCapability.ConfinedSideStepRight
            or StandingLocomotionCapability.ConfinedPivotLeft
            or StandingLocomotionCapability.ConfinedPivotRight;
        bool transition = capability is StandingLocomotionCapability.EntryForwardLeft
            or StandingLocomotionCapability.EntryForwardRight
            or StandingLocomotionCapability.ExitForwardLeft
            or StandingLocomotionCapability.ExitForwardRight
            or StandingLocomotionCapability.EntryBackwardLeft
            or StandingLocomotionCapability.EntryBackwardRight
            or StandingLocomotionCapability.ExitBackwardLeft
            or StandingLocomotionCapability.ExitBackwardRight;
        bool cyclic = !confined && !transition;
        Vector3 root = capability switch
        {
            StandingLocomotionCapability.CyclicForwardPrimary => new(0f, 0f, 0.72f),
            StandingLocomotionCapability.CyclicBackwardPrimary => new(0f, 0f, -0.54f),
            StandingLocomotionCapability.EntryForwardLeft or StandingLocomotionCapability.EntryForwardRight
                or StandingLocomotionCapability.ExitForwardLeft or StandingLocomotionCapability.ExitForwardRight => new(0f, 0f, 0.20f),
            StandingLocomotionCapability.EntryBackwardLeft or StandingLocomotionCapability.EntryBackwardRight
                or StandingLocomotionCapability.ExitBackwardLeft or StandingLocomotionCapability.ExitBackwardRight => new(0f, 0f, -0.20f),
            StandingLocomotionCapability.ConfinedSideStepLeft => new(0.32f, 0f, 0f),
            StandingLocomotionCapability.ConfinedSideStepRight => new(-0.32f, 0f, 0f),
            StandingLocomotionCapability.ConfinedPivotLeft or StandingLocomotionCapability.ConfinedPivotRight => Vector3.Zero,
            StandingLocomotionCapability.CyclicLateralLeftLeftLane or StandingLocomotionCapability.CyclicLateralLeftRightLane => new(0.28f, 0f, 0.46f),
            StandingLocomotionCapability.CyclicLateralRightLeftLane or StandingLocomotionCapability.CyclicLateralRightRightLane => new(-0.28f, 0f, 0.46f),
            StandingLocomotionCapability.CyclicArcLeftLeftLane or StandingLocomotionCapability.CyclicArcLeftRightLane => new(0.22f, 0f, 0.40f),
            StandingLocomotionCapability.CyclicArcRightLeftLane or StandingLocomotionCapability.CyclicArcRightRightLane => new(-0.22f, 0f, 0.40f),
            _ => Vector3.Zero,
        };
        float yaw = capability switch
        {
            StandingLocomotionCapability.ConfinedPivotLeft => Mathf.Pi / 4f,
            StandingLocomotionCapability.ConfinedPivotRight => -Mathf.Pi / 4f,
            _ => 0f
        };
        int token = ((int)capability * 17 + 11) % _capabilities.Length;
        return new(
            $"slot_{token:x2}",
            $"ember_token_{token:x2}",
            confined ? 0.42f : cyclic ? 0.8f : 0.56f,
            !transition,
            root,
            yaw,
            confined ? "confined_manoeuvre" : cyclic ? "cyclic_travel" : "transition",
            DeclaredPhaseEvents: EventsFor(capability, confined ? 0.42f : cyclic ? 0.8f : 0.56f),
            Capability: capability);
    }

    private static SyntheticNavigationManifest CreateHealthyFiniteExit()
    {
        SyntheticNavigationClipDeclaration[] clips = [.. Default.Clips];
        int forwardIndex = Array.FindIndex(
            clips,
            clip => clip.Capability == StandingLocomotionCapability.CyclicForwardPrimary);
        clips[forwardIndex] = clips[forwardIndex] with
        {
            SkeletonLocalRootResponse = new Vector3(0f, 0f, 0.2857143f),
            DeclaredContactWindows =
            [
                new(LimbSide.Left, "acquire", [0.45d]),
                new(LimbSide.Left, "support", [0.45d, 1d]),
                new(LimbSide.Left, "release", [1d]),
            ],
        };
        foreach (StandingLocomotionCapability capability in new[]
                 {
                     StandingLocomotionCapability.ExitForwardLeft,
                     StandingLocomotionCapability.ExitForwardRight,
                 })
        {
            int index = Array.FindIndex(clips, clip => clip.Capability == capability);
            clips[index] = clips[index] with
            {
                DurationSeconds = 0.56f,
                SkeletonLocalRootResponse = new Vector3(0f, 0f, 0.20f),
                DeclaredContactWindows =
                [
                    new(capability == StandingLocomotionCapability.ExitForwardRight ? LimbSide.Left : LimbSide.Right,
                        "acquire", [0d]),
                    new(capability == StandingLocomotionCapability.ExitForwardRight ? LimbSide.Left : LimbSide.Right,
                        "support", [0d, 1d]),
                    new(capability == StandingLocomotionCapability.ExitForwardRight ? LimbSide.Left : LimbSide.Right,
                        "release", [1d]),
                ],
            };
        }

        var manifest = new SyntheticNavigationManifest(
            clips,
            Package: "healthy_finite_exit",
            Variant: "healthy_ember");
        return manifest with
        {
            DeclaredMappings = manifest.CreateMappings(),
        };
    }

    private SyntheticNavigationMapping[] CreateMappings()
    {
        var mappings = new List<SyntheticNavigationMapping>();
        foreach (StandingLocomotionAnatomicalSide side in Enum.GetValues<StandingLocomotionAnatomicalSide>())
        {
            string release = side == StandingLocomotionAnatomicalSide.Left ? "release_l" : "release_r";
            string forward = this[StandingLocomotionCapability.CyclicForwardPrimary].Role;
            string backward = this[StandingLocomotionCapability.CyclicBackwardPrimary].Role;
            mappings.Add(new(this[StandingLocomotionCapabilities.Entry(false, side)].Role, release, forward));
            mappings.Add(new(forward, release, this[StandingLocomotionCapabilities.Exit(false, side)].Role));
            mappings.Add(new(this[StandingLocomotionCapabilities.Entry(true, side)].Role, release, backward));
            mappings.Add(new(backward, release, this[StandingLocomotionCapabilities.Exit(true, side)].Role));
            StandingLocomotionCapability[] families = side == StandingLocomotionAnatomicalSide.Left
                ?
                [
                    StandingLocomotionCapability.CyclicLateralLeftLeftLane,
                    StandingLocomotionCapability.CyclicLateralRightLeftLane,
                    StandingLocomotionCapability.CyclicArcLeftLeftLane,
                    StandingLocomotionCapability.CyclicArcRightLeftLane,
                ]
                :
                [
                    StandingLocomotionCapability.CyclicLateralLeftRightLane,
                    StandingLocomotionCapability.CyclicLateralRightRightLane,
                    StandingLocomotionCapability.CyclicArcLeftRightLane,
                    StandingLocomotionCapability.CyclicArcRightRightLane,
                ];
            foreach (StandingLocomotionCapability family in families)
            {
                string familyRole = this[family].Role;
                mappings.Add(new(forward, release, familyRole));
                mappings.Add(new(familyRole, release, forward));
            }
            mappings.Add(new(this[families[0]].Role, release, this[families[1]].Role));
            mappings.Add(new(this[families[1]].Role, release, this[families[0]].Role));
            mappings.Add(new(this[families[2]].Role, release, this[families[3]].Role));
            mappings.Add(new(this[families[3]].Role, release, this[families[2]].Role));
        }
        return [.. mappings];
    }

    private StandingLocomotionShortBoutPair[] CreateShortBoutPairs() =>
    [
        new(this[StandingLocomotionCapability.EntryForwardRight].Role, "release_r", this[StandingLocomotionCapability.ExitForwardLeft].Role, "release_l"),
        new(this[StandingLocomotionCapability.EntryForwardLeft].Role, "release_l", this[StandingLocomotionCapability.ExitForwardRight].Role, "release_r"),
        new(this[StandingLocomotionCapability.EntryBackwardRight].Role, "release_r", this[StandingLocomotionCapability.ExitBackwardLeft].Role, "release_l"),
        new(this[StandingLocomotionCapability.EntryBackwardLeft].Role, "release_l", this[StandingLocomotionCapability.ExitBackwardRight].Role, "release_r"),
    ];

    private static SyntheticPhaseEvent[] EventsFor(StandingLocomotionCapability capability, float durationSeconds) => capability switch
    {
        StandingLocomotionCapability.CyclicForwardPrimary or StandingLocomotionCapability.CyclicBackwardPrimary
            => [new("release_l", 0.20f), new("release_r", 0.60f)],
        StandingLocomotionCapability.EntryForwardLeft or StandingLocomotionCapability.EntryBackwardLeft
            => [new("release_l", durationSeconds, "end")],
        StandingLocomotionCapability.EntryForwardRight or StandingLocomotionCapability.EntryBackwardRight
            => [new("release_r", durationSeconds, "end")],
        StandingLocomotionCapability.ExitForwardLeft or StandingLocomotionCapability.ExitBackwardLeft
            => [new("release_l", 0f, "start")],
        StandingLocomotionCapability.ExitForwardRight or StandingLocomotionCapability.ExitBackwardRight
            => [new("release_r", 0f, "start")],
        _ => [new(capability.Side() == StandingLocomotionAnatomicalSide.Left ? "release_l" : "release_r", 0.28f)],
    };
}

internal readonly record struct SyntheticNavigationMapping(
    string FromRole,
    string EventID,
    string ToRole,
    string? DeclaredToEventID = null)
{
    public string ToEventID => DeclaredToEventID ?? EventID;
}

/// <summary>Named synthetic runtime profile values used by terminal-navigation acceptance traces.</summary>
internal sealed record SyntheticNavigationFixtureProfile(
    string Name,
    SyntheticNavigationManifest Manifest,
    Transform3D Destination,
    string ExpectedExitRole,
    int OrdinaryForwardCyclesBeforeExit)
{
    public static SyntheticNavigationFixtureProfile HealthyFiniteExit { get; } = CreateHealthyFiniteExit();

    public SyntheticNavigationClipDeclaration ExpectedExitDeclaration
        => Manifest.Clips.Single(clip => clip.Role == ExpectedExitRole);

    public string ExpectedReleaseEvent => ExpectedExitMapping.ToEventID;

    public NPCAnimationPhaseLane ExpectedLane => ExpectedExitEvent.Side switch
    {
        "Left" => NPCAnimationPhaseLane.Left,
        "Right" => NPCAnimationPhaseLane.Right,
        _ => throw new InvalidOperationException(
            $"Profile '{Name}' selected exit role '{ExpectedExitRole}' has no declared left/right release mapping."),
    };

    public Vector2 ExpectedExitEndpointResponse => ExpectedExitDeclaration.SemanticCatalogueRootResponse;

    public float ExpectedUncorrectedTerminalRemainderMetres => Destination.Origin.Length()
        - ((OrdinaryForwardCyclesBeforeExit * Manifest["wf"].SemanticCatalogueRootResponse.Length())
            + ExpectedExitEndpointResponse.Length());

    public static SyntheticNavigationFixtureProfile Create(
        string name,
        SyntheticNavigationManifest manifest,
        Transform3D destination,
        string expectedExitRole,
        int ordinaryForwardCyclesBeforeExit)
        => new(name, manifest, destination, expectedExitRole, ordinaryForwardCyclesBeforeExit);

    private static SyntheticNavigationFixtureProfile CreateHealthyFiniteExit()
    {
        SyntheticNavigationManifest manifest = SyntheticNavigationManifest.HealthyFiniteExit;
        return Create(
            "healthy-finite-exit",
            manifest,
            new Transform3D(Basis.Identity, new Vector3(0f, 0f, -2f)),
            manifest["f2i_r"].Role,
            6);
    }

    private SyntheticNavigationMapping ExpectedExitMapping => (Manifest.DeclaredMappings
        ?? throw new InvalidOperationException($"Profile '{Name}' requires declared navigation mappings."))
        .Single(mapping => mapping.FromRole == Manifest["wf"].Role && mapping.ToRole == ExpectedExitRole);

    private SyntheticPhaseEvent ExpectedExitEvent => (ExpectedExitDeclaration.DeclaredPhaseEvents ?? [])
        .Single(@event => @event.ID == ExpectedReleaseEvent && @event.BoundaryContext == "start");
}

internal readonly record struct SyntheticPhaseEvent(string ID, float Time, string? BoundaryContext = null)
{
    public string Kind => ID.StartsWith("acquire_", StringComparison.Ordinal) ? "Acquire" : "Release";
    public string Side => ID.EndsWith("_l", StringComparison.Ordinal) ? "Left" : "Right";
}

internal sealed record SyntheticNavigationClipDeclaration(
    string Role,
    string Identity,
    float DurationSeconds,
    bool Loops,
    Vector3 SkeletonLocalRootResponse,
    float YawRadians,
    string BoundaryKind,
    IReadOnlyList<SyntheticContactWindow>? DeclaredContactWindows = null,
    IReadOnlyList<SyntheticPhaseEvent>? DeclaredPhaseEvents = null,
    StandingLocomotionCapability Capability = StandingLocomotionCapability.CyclicForwardPrimary)
{
    public bool IsConfined => BoundaryKind == "confined_manoeuvre";
    public bool IsCyclic => BoundaryKind == "cyclic_travel";
    public Vector2 SemanticCatalogueRootResponse => SyntheticLocomotionRuntimeFixture.ToSemanticCatalogueResponse(SkeletonLocalRootResponse);
    public IReadOnlyList<SyntheticContactWindow> ContactWindows => DeclaredContactWindows ?? [];
}

internal sealed record SyntheticNavigationRuntime(
    Node3D Root,
    CharacterBody3D Actor,
    Node3D RootMotionReference,
    Skeleton3D Skeleton,
    AnimationPlayer Player,
    AnimationTree AnimationTree,
    NPCNavigation Navigation,
    Node AnimationPhysicsDriver,
    SyntheticNavigationManifest Manifest);

/// <summary>Fixture-owned player composition and its declared controller state protocol.</summary>
internal sealed record SyntheticPlayerLocomotionRuntime(
    CharacterBody3D Actor,
    Node3D RootMotionReference,
    Skeleton3D Skeleton,
    AnimationPlayer Player,
    AnimationTree AnimationTree,
    CharacterLocomotion Locomotion,
    SyntheticNavigationManifest Manifest)
{
    public const string IdleState = "StandingCrouching";
    public const string TravelState = "PlayerTravel";
    public const string EntryForwardLeftState = "PlayerI2FL";
    public const string EntryForwardRightState = "PlayerI2FR";
    public const string EntryBackwardLeftState = "PlayerI2BL";
    public const string EntryBackwardRightState = "PlayerI2BR";
    public const string ExitForwardLeftState = "PlayerF2IL";
    public const string ExitForwardRightState = "PlayerF2IR";
    public const string ExitBackwardLeftState = "PlayerB2IL";
    public const string ExitBackwardRightState = "PlayerB2IR";
    public const string PivotLeftState = "PlayerPivotL";
    public const string PivotRightState = "PlayerPivotR";
    public const string SideStepLeftState = "PlayerSideStepL";
    public const string SideStepRightState = "PlayerSideStepR";

    public static IReadOnlyList<string> FiniteExitStates
    {
        get;
    } =
    [
        ExitForwardLeftState, ExitForwardRightState, ExitBackwardLeftState, ExitBackwardRightState,
    ];
}

/// <summary>Fixture-only binding adapter; it retains the production activation pipeline while avoiding scene installers.</summary>
internal sealed partial class SyntheticNPCNavigation : NPCNavigation
{
    protected override bool AreBindingsInTree() => true;

    protected override bool IsAnimationTreeRootResolved() => true;
}

/// <summary>Fixture-owned deterministic producer scheduled immediately after the NPC navigation consumer.</summary>
internal sealed partial class SyntheticAnimationPhysicsDriver(AnimationTree animationTree) : Node
{
    public override void _Ready() => SetPhysicsProcess(true);

    public override void _PhysicsProcess(double delta) => animationTree.Advance(delta);
}
