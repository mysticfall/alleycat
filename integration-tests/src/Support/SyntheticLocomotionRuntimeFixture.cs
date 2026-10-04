using System.Text.Json;
using AlleyCat.Control.Locomotion;
using Godot;

#pragma warning disable IDE0048, IDE0072

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// Test-owned, content-independent locomotion package used by runtime integration tests. The package models the
/// ANIM-002 embedded-only contract: animations exist only as members of saved AnimationLibrary resources, and the
/// schema version 4 catalogue resolves them by animation identity. No standalone clip resources are published.
/// </summary>
internal sealed class SyntheticLocomotionRuntimeFixture : IDisposable
{
    private const string PackageDirectory = "user://synthetic-locomotion-runtime";
    private const string LocomotionLibraryFilename = "locomotion_library.tres";
    private const string IdleLibraryFilename = "idle_library.tres";
    private const string NavigationLibraryFilename = "navigation_library.tres";
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
        NavigationLibraryPath = JoinResourcePath(directory, NavigationLibraryFilename);
        NavigationCataloguePath = JoinResourcePath(directory, "navigation_catalogue.json");
        AssertSaved(LocomotionLibrary, LocomotionLibraryPath);
        AssertSaved(IdleLibrary, IdleLibraryPath);
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
            RootMotionLocal = true,
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
        var rootMotionReference = new Node3D
        {
            Name = "RootMotionReference",
            Basis = Basis.FromEuler(new Vector3(0f, Mathf.Pi, 0f)),
        };
        Actor.AddChild(rootMotionReference);
        rootMotionReference.Owner = Root;

        AnimationTree tree = CreatePlayerPlaybackTree();
        // The player playback tree must be inside the scene graph: the AnimationTree only binds its AnimationPlayer
        // and processes advances after entering the tree, so a detached tree silently never evaluates its graph.
        Actor.AddChild(tree);
        tree.Owner = Root;
        var locomotion = new CharacterLocomotion
        {
            Name = "Locomotion",
            TargetCharacterBodyNode = Actor,
            AnimationTree = tree,
            RootMotionReference = rootMotionReference,
            CataloguePath = NavigationCataloguePath,
        };
        Actor.AddChild(locomotion);
        locomotion.Owner = Root;
        return new(
            Actor,
            rootMotionReference,
            Skeleton,
            Player,
            tree,
            locomotion,
            Declaration.NavigationManifest);
    }

    public Animation AnimationFor(string clipName)
        => Player.GetAnimation($"{(Declaration[clipName].IsIdle ? "idle" : "locomotion")}/{clipName}");

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

    internal static Skeleton3D BuildSkeleton()
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
            Declaration.NavigationManifest.ToJson(NavigationLibraryFilename));
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
        // The same fixture-owned library serves the minimal player rig. Mirror the declared Root onto that rig's
        // path so player behavioural tests evaluate real authored root rather than accidentally proving a discarded
        // or missing track. Production-content paths remain outside the fixture oracle.
        int playerRootPosition = animation.AddTrack(Animation.TrackType.Position3D);
        animation.TrackSetPath(playerRootPosition, new NodePath("SyntheticSkeleton:Root"));
        EnsureKey(animation.PositionTrackInsertKey(playerRootPosition, 0f, Vector3.Zero));
        EnsureKey(animation.PositionTrackInsertKey(playerRootPosition, clip.DurationSeconds, clip.SkeletonLocalRootResponse));
        int playerRootRotation = animation.AddTrack(Animation.TrackType.Rotation3D);
        animation.TrackSetPath(playerRootRotation, new NodePath("SyntheticSkeleton:Root"));
        EnsureKey(animation.RotationTrackInsertKey(playerRootRotation, 0f, Quaternion.Identity));
        EnsureKey(animation.RotationTrackInsertKey(playerRootRotation, clip.DurationSeconds, Quaternion.FromEuler(new Vector3(0f, clip.YawRadians, 0f))));
        return animation;
    }

    internal AnimationTree CreatePlayerPlaybackTree()
    {
        var states = new AnimationNodeStateMachine();
        states.AddNode(SyntheticPlayerLocomotionRuntime.IdleState, new AnimationNodeAnimation { Animation = "idle/ember_idle" });
        AddFiniteState(SyntheticPlayerLocomotionRuntime.EntryForwardLeftState, "i2f_l");
        AddFiniteState(SyntheticPlayerLocomotionRuntime.EntryForwardRightState, "i2f_r");
        AddFiniteState(SyntheticPlayerLocomotionRuntime.EntryBackwardLeftState, "i2b_l");
        AddFiniteState(SyntheticPlayerLocomotionRuntime.EntryBackwardRightState, "i2b_r");
        AddStopState(SyntheticPlayerLocomotionRuntime.ExitForwardLeftState, "f2i_l");
        AddStopState(SyntheticPlayerLocomotionRuntime.ExitForwardRightState, "f2i_r");
        AddStopState(SyntheticPlayerLocomotionRuntime.ExitBackwardLeftState, "b2i_l");
        AddStopState(SyntheticPlayerLocomotionRuntime.ExitBackwardRightState, "b2i_r");
        // Stationary turning (CTRL-001): the authored confined pivots present turn-in-place exactly like the
        // production tree's new turn states (both pivot animations loop, so the presentation sustains while
        // the stick is held; the yaw reaches the actor through the authored root rotation alone).
        AddFiniteState(SyntheticPlayerLocomotionRuntime.TurnInPlaceLeftState, "pl45");
        AddFiniteState(SyntheticPlayerLocomotionRuntime.TurnInPlaceRightState, "pr45");
        states.AddNode(SyntheticPlayerLocomotionRuntime.TravelState, CreatePlayerTravelTree());

        AddTransitions("Start", [SyntheticPlayerLocomotionRuntime.IdleState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.IdleState, [
            SyntheticPlayerLocomotionRuntime.EntryForwardLeftState, SyntheticPlayerLocomotionRuntime.EntryForwardRightState,
            SyntheticPlayerLocomotionRuntime.EntryBackwardLeftState, SyntheticPlayerLocomotionRuntime.EntryBackwardRightState,
            SyntheticPlayerLocomotionRuntime.TurnInPlaceLeftState, SyntheticPlayerLocomotionRuntime.TurnInPlaceRightState]);
        // The turn states return to idle on release and hand straight off to a movement entry when movement
        // begins during the turn (no idle interlude), mirroring the exit-state interruptions.
        foreach (string turnState in new[]
                 {
                     SyntheticPlayerLocomotionRuntime.TurnInPlaceLeftState, SyntheticPlayerLocomotionRuntime.TurnInPlaceRightState,
                 })
        {
            AddTransitions(turnState, [
                SyntheticPlayerLocomotionRuntime.IdleState,
                SyntheticPlayerLocomotionRuntime.EntryForwardLeftState, SyntheticPlayerLocomotionRuntime.EntryForwardRightState,
                SyntheticPlayerLocomotionRuntime.EntryBackwardLeftState, SyntheticPlayerLocomotionRuntime.EntryBackwardRightState]);
        }
        // Entries can be interrupted straight into their compatible exit (release during an authored start),
        // into sustained travel (completion or retarget), or into idle (a lateral-dominant release during
        // the entry window takes the neutral stop policy with no idle interlude through a stop clip).
        AddTransitions(SyntheticPlayerLocomotionRuntime.EntryForwardLeftState,
            [SyntheticPlayerLocomotionRuntime.TravelState, SyntheticPlayerLocomotionRuntime.ExitForwardLeftState,
             SyntheticPlayerLocomotionRuntime.IdleState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.EntryForwardRightState,
            [SyntheticPlayerLocomotionRuntime.TravelState, SyntheticPlayerLocomotionRuntime.ExitForwardRightState,
             SyntheticPlayerLocomotionRuntime.IdleState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.EntryBackwardLeftState,
            [SyntheticPlayerLocomotionRuntime.TravelState, SyntheticPlayerLocomotionRuntime.ExitBackwardLeftState,
             SyntheticPlayerLocomotionRuntime.IdleState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.EntryBackwardRightState,
            [SyntheticPlayerLocomotionRuntime.TravelState, SyntheticPlayerLocomotionRuntime.ExitBackwardRightState,
             SyntheticPlayerLocomotionRuntime.IdleState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.TravelState, [
            SyntheticPlayerLocomotionRuntime.ExitForwardLeftState, SyntheticPlayerLocomotionRuntime.ExitForwardRightState,
            SyntheticPlayerLocomotionRuntime.ExitBackwardLeftState, SyntheticPlayerLocomotionRuntime.ExitBackwardRightState,
            // Direct fallback stop when authored exit content is unavailable (presentation degradation only).
            SyntheticPlayerLocomotionRuntime.IdleState]);
        // Exits accept interruption straight back into travel: fresh intent replaces a pending stop without an idle
        // interlude.
        AddTransitions(SyntheticPlayerLocomotionRuntime.ExitForwardLeftState,
            [SyntheticPlayerLocomotionRuntime.IdleState, SyntheticPlayerLocomotionRuntime.TravelState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.ExitForwardRightState,
            [SyntheticPlayerLocomotionRuntime.IdleState, SyntheticPlayerLocomotionRuntime.TravelState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.ExitBackwardLeftState,
            [SyntheticPlayerLocomotionRuntime.IdleState, SyntheticPlayerLocomotionRuntime.TravelState]);
        AddTransitions(SyntheticPlayerLocomotionRuntime.ExitBackwardRightState,
            [SyntheticPlayerLocomotionRuntime.IdleState, SyntheticPlayerLocomotionRuntime.TravelState]);

        var root = new AnimationNodeBlendTree();
        root.AddNode("States", states);
        root.ConnectNode("output", 0, "States");
        return new AnimationTree
        {
            Name = "AnimationTree",
            AnimPlayer = new NodePath("../AnimationPlayer"),
            RootNode = new NodePath(".."),
            RootMotionTrack = new NodePath("SyntheticSkeleton:Root"),
            RootMotionLocal = true,
            TreeRoot = root,
            CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Physics,
            Active = true,
        };

        void AddFiniteState(string state, string role)
        {
            states.AddNode(state, new AnimationNodeAnimation { Animation = $"locomotion/{Declaration.NavigationManifest[role].Identity}" });
        }

        // Stop states mirror the production topology after stop-rate governance: the stop clip sits inside a
        // blend tree with a Rate timescale, so `parameters/States/<state>/Rate/scale` governs stop playback.
        void AddStopState(string state, string role)
        {
            AnimationNodeBlendTree wrap = new();
            wrap.AddNode("Anim", new AnimationNodeAnimation { Animation = $"locomotion/{Declaration.NavigationManifest[role].Identity}" });
            wrap.AddNode("Rate", new AnimationNodeTimeScale());
            wrap.ConnectNode("Rate", 0, "Anim");
            wrap.ConnectNode("output", 0, "Rate");
            states.AddNode(state, wrap);
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
        AnimationNodeBlendSpace2D forwardLeft = CreateTravelSpace("wf", "al_l", "ar_l");
        AnimationNodeBlendSpace2D forwardRight = CreateTravelSpace("wf", "al_r", "ar_r");
        AnimationNodeBlendSpace2D directionalLeft = CreateDirectionalTravelSpace("xl_l", "xr_l");
        AnimationNodeBlendSpace2D directionalRight = CreateDirectionalTravelSpace("xl_r", "xr_r");
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

    private AnimationNodeBlendSpace2D CreateDirectionalTravelSpace(string leftRole, string rightRole)
    {
        AnimationNodeBlendSpace2D space = CreateTravelSpace("wf", leftRole, rightRole);
        space.AddBlendPoint(CreateAnimationNode("wb"), new Vector2(0f, -1f), -1, "wb");
        return space;
    }

    private AnimationNodeAnimation CreateAnimationNode(string role)
        => new()
        {
            Animation = $"locomotion/{Declaration.NavigationManifest[role].Identity}"
        };

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
    IReadOnlyList<SyntheticClipDeclaration> Clips)
{
    public static SyntheticLocomotionDeclaration Default
    {
        get;
    } = new(
        "synthetic-locomotion",
        "fixture",
        [
            new("stride_loop", "ember_stride_loop", 0.8f, true, new Vector3(0f, 0f, -0.8f), 0f),
            new("reverse_loop", "ember_reverse_loop", 0.8f, true, new Vector3(0f, 0f, 0.6f), 0f),
            new("pivot_clockwise", "ember_pivot_clockwise", 0.5f, true, Vector3.Zero, -Mathf.Pi / 2f),
            new("pivot_counterclockwise", "ember_pivot_counterclockwise", 0.5f, true, Vector3.Zero, Mathf.Pi / 2f),
            new("exit_alpha", "ember_exit_alpha", 0.6f, false, new Vector3(0f, 0f, -0.2f), 0f),
            new("exit_beta", "ember_exit_beta", 0.6f, false, new Vector3(0.1f, 0f, -0.2f), 0f),
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
    bool IsIdle = false)
{
    public bool IsFinite => !Loops && !IsIdle;
    public Vector3 LeftFootStart { get; } = DeclaredLeftFootStart ?? new Vector3(-0.2f, -1f, 0f);
    public Vector3 LeftFootEnd { get; } = DeclaredLeftFootEnd ?? new Vector3(-0.2f, -1f, -0.25f);
    public Vector3 RightFootStart { get; } = DeclaredRightFootStart ?? new Vector3(0.2f, -1f, 0f);
    public Vector3 RightFootEnd { get; } = DeclaredRightFootEnd ?? new Vector3(0.2f, -1f, -0.25f);
}

/// <summary>Fixture-owned navigation roles and package contract. Values are deliberately arbitrary and generic.</summary>
internal sealed record SyntheticNavigationManifest(
    IReadOnlyList<SyntheticNavigationClipDeclaration> Clips,
    IReadOnlyList<SyntheticNavigationMapping>? DeclaredMappings = null,
    string Package = "locomotion",
    string Variant = "ember")
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

    /// <summary>Finite player exits whose authored 0.12 m root remains inside the measured CTRL stop envelope.</summary>
    public static SyntheticNavigationManifest ShortPlayerStop { get; } = CreateShortPlayerStop();

    public SyntheticNavigationClipDeclaration this[string semanticAlias]
        => Clips.Single(clip => clip.Capability == CapabilityForAlias(semanticAlias));

    public SyntheticNavigationClipDeclaration this[StandingLocomotionCapability capability]
        => Clips.Single(clip => clip.Capability == capability);

    /// <summary>Serialises the fixture's embedded-only runtime catalogue (schema version 4).</summary>
    public string ToJson(string animationLibraryFilename)
    {
        IReadOnlyList<SyntheticNavigationMapping> mappings = DeclaredMappings ?? CreateMappings();
        var root = new Dictionary<string, object?>
        {
            ["schema_version"] = 4,
            ["package"] = Package,
            ["package_variant"] = Variant,
            ["animation_library"] = animationLibraryFilename,
            ["clip_count"] = Clips.Count,
            ["clips"] = Clips.Select(clip => CreateClipJson(clip, Clips, mappings)).ToArray(),
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
            // Lateral cycles are pure-lateral by fixture contract: their root response travels only along X
            // with a zero forward component, so lateral travel stays measurable without a forward-drift
            // oracle. The per-side sign convention (root −X = actor right) and the 0.8 s cyclic timing are
            // test-owned declarations consumed by the V4 catalogue and seam-table tests.
            StandingLocomotionCapability.CyclicLateralLeftLeftLane or StandingLocomotionCapability.CyclicLateralLeftRightLane => new(0.744f, 0f, 0f),
            StandingLocomotionCapability.CyclicLateralRightLeftLane or StandingLocomotionCapability.CyclicLateralRightRightLane => new(-0.744f, 0f, 0f),
            StandingLocomotionCapability.CyclicArcLeftLeftLane or StandingLocomotionCapability.CyclicArcLeftRightLane => new(0.22f, 0f, 0.40f),
            StandingLocomotionCapability.CyclicArcRightLeftLane or StandingLocomotionCapability.CyclicArcRightRightLane => new(-0.22f, 0f, 0.40f),
            _ => Vector3.Zero,
        };
        float yaw = capability switch
        {
            // Synthetic arc yaw: ±π/6 rad across the 0.8 s cycle (about 0.65 rad/s) is the fixture's own
            // turn-rate scale for signed yaw-track resolution, not a direct actor rotation.
            StandingLocomotionCapability.CyclicArcLeftLeftLane or StandingLocomotionCapability.CyclicArcLeftRightLane
                => Mathf.Pi / 6f,
            StandingLocomotionCapability.CyclicArcRightLeftLane or StandingLocomotionCapability.CyclicArcRightRightLane
                => -Mathf.Pi / 6f,
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

    private static SyntheticNavigationManifest CreateShortPlayerStop()
    {
        SyntheticNavigationClipDeclaration[] clips = [.. Default.Clips];
        foreach (StandingLocomotionCapability capability in new[]
                 {
                     StandingLocomotionCapability.ExitForwardLeft,
                     StandingLocomotionCapability.ExitForwardRight,
                     StandingLocomotionCapability.ExitBackwardLeft,
                     StandingLocomotionCapability.ExitBackwardRight,
                 })
        {
            int index = Array.FindIndex(clips, clip => clip.Capability == capability);
            Vector3 root = capability is StandingLocomotionCapability.ExitForwardLeft
                or StandingLocomotionCapability.ExitForwardRight
                ? new(0f, 0f, 0.12f)
                : new(0f, 0f, -0.12f);
            clips[index] = clips[index] with
            {
                SkeletonLocalRootResponse = root
            };
        }
        var manifest = new SyntheticNavigationManifest(clips, Package: "short_player_stop", Variant: "short_ember");
        return manifest with
        {
            DeclaredMappings = manifest.CreateMappings()
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
    IReadOnlyList<SyntheticPhaseEvent>? DeclaredPhaseEvents = null,
    StandingLocomotionCapability Capability = StandingLocomotionCapability.CyclicForwardPrimary)
{
    public bool IsConfined => BoundaryKind == "confined_manoeuvre";
    public bool IsCyclic => BoundaryKind == "cyclic_travel";
    public Vector2 SemanticCatalogueRootResponse => SyntheticLocomotionRuntimeFixture.ToSemanticCatalogueResponse(SkeletonLocalRootResponse);
}

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
    public const string TurnInPlaceLeftState = "PlayerTurnInPlaceL";
    public const string TurnInPlaceRightState = "PlayerTurnInPlaceR";

    public static IReadOnlyList<string> FiniteExitStates
    {
        get;
    } =
    [
        ExitForwardLeftState, ExitForwardRightState, ExitBackwardLeftState, ExitBackwardRightState,
    ];
}

/// <summary>Fixture-owned deterministic producer scheduled immediately after the NPC navigation consumer.</summary>
internal sealed partial class SyntheticAnimationPhysicsDriver(AnimationTree animationTree) : Node
{
    public override void _Ready() => SetPhysicsProcess(true);

    public override void _PhysicsProcess(double delta) => animationTree.Advance(delta);
}
