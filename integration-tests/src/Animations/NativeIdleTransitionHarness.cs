using System.Globalization;
using System.Text;
using AlleyCat.Animations;
using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Recording;
using Godot;
using Xunit;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Animations;

/// <summary>
/// Representative ANIM-003 transition fixture: the installed reference female NPC in the navigation playtest scene,
/// with the real MotionBricks worker. The idle-only fixture pack (<c>slump</c>) and the default-context extension pack
/// (<c>stroll</c>) are added to the installed catalogue before navigation builds it. Records the final visible pose at
/// every <c>skeleton_updated</c>.
/// </summary>
internal sealed partial class NativeIdleTransitionHarness : IAsyncDisposable
{
    internal const string FixtureRoot = "res://tests/animation/catalogue/fixture/";
    internal const string ExtensionPack = "res://tests/animation/catalogue/extension/pack.json";

    /// <summary>SOMA-mapped body bones whose local rotation is traced (no digits, so the trace stays compact).</summary>
    internal static readonly string[] BodyBones =
    [
        "Hips", "Spine", "Chest", "UpperChest", "Neck", "Head", "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand",
        "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand", "LeftUpperLeg", "LeftLowerLeg", "LeftFoot",
        "RightUpperLeg", "RightLowerLeg", "RightFoot"
    ];

    /// <summary>Bones outside the SOMA map that bridges must blend (TR19).</summary>
    internal static readonly string[] UnmappedBones = ["Root", "breast_l", "breast_r", "LeftForearmTwist", "RightForearmTwist"];

    /// <summary>Bones no installed NPC modifier writes after the idle writer, so the final pose shows its output.</summary>
    internal static readonly string[] UnmodifiedBones = ["Hips", "Spine", "Chest", "UpperChest"];

    internal sealed record Frame(long Tick, double Time, string Phase, bool NativeIdle, bool RetargetActive,
        NativeIdleMode Mode, bool Blend, Quaternion[] Body, Transform3D[] Unmapped, Vector3 HipsWorld, Vector3 ActorWorld,
        Vector3[] Landmarks);

    private readonly SceneTree _tree;
    private readonly Node _root;
    private readonly List<Frame> _frames = [];
    private int[] _body = [], _unmapped = [], _landmarks = [];
    private Skeleton3D? _skeleton;
    private bool _recording;

    private NativeIdleTransitionHarness(SceneTree tree, Node root, Node scene)
    {
        _tree = tree;
        _root = root;
        Scene = scene;
        Actor = scene.GetNode<CharacterHub>("NavigationTestNpc");
    }

    public Node Scene
    {
        get;
    }

    public CharacterHub Actor
    {
        get;
    }

    public MotionBricksNavigation Navigation
    {
        get; private set;
    } = null!;

    public Skeleton3D Skeleton => _skeleton!;

    public RetargetModifier3D Retarget
    {
        get; private set;
    } = null!;

    public IReadOnlyList<Frame> Frames => _frames;

    public List<AnimationTransition> Transitions { get; } = [];

    public List<long> Windows { get; } = [];

    public static async Task<NativeIdleTransitionHarness> StartAsync()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new HarnessGame();
        Node scene = GD.Load<PackedScene>(NavigationPlaytestRecorder.PlaytestScene).Instantiate();
        var harness = new NativeIdleTransitionHarness(tree, root, scene);
        MotionBricksNavigation? found = null;
        void Configure(Node node)
        {
            if (node is MotionBricksNavigation nav && nav.GetParent() == harness.Actor)
            {
                found = nav;
                // Captured before worker startup: lets a test hold back the next generation on the worker thread.
                nav.SessionDecorator = session => new DelayingSession(session, harness);
                // Runs before navigation's own deferred initialisation, after installation finished.
                Callable.From(() => AddTestPacks(harness.Actor)).CallDeferred();
            }
        }

        tree.NodeAdded += Configure;
        root.AddChild(scene);
        tree.Root.AddChild(root);
        try
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 20);
        }
        finally
        {
            tree.NodeAdded -= Configure;
        }

        Assert.NotNull(found);
        harness.Navigation = found;
        Assert.Equal("", found.Failure);
        Assert.True(found.NativeIdleOwnsBody);
        harness.Bind();
        return harness;
    }

    private static void AddTestPacks(CharacterHub actor)
    {
        var catalogue = AnimationCatalogue.RequireFor(actor);
        Assert.False(catalogue.IsBuilt);
        catalogue.AddChild(new AnimationPack
        {
            Name = "Fixture",
            Manifest = FixtureRoot + "pack.json",
            Priority = 20,
            ContextOverride = new ContentContext("fixture", FixtureRoot)
        });
        catalogue.AddChild(new AnimationPack { Name = "Extension", Manifest = ExtensionPack, Priority = 10 });
    }

    private void Bind()
    {
        _skeleton = Actor.RequireMotionTarget();
        Retarget = Assert.IsType<RetargetModifier3D>(_skeleton.GetParent());
        _body = [.. BodyBones.Select(Bone)];
        _unmapped = [.. UnmappedBones.Select(Bone)];
        _landmarks = [.. new[] { "Hips", "Head", "LeftHand", "RightHand", "LeftFoot", "RightFoot" }.Select(Bone)];
        _skeleton.SkeletonUpdated += Record;
        Navigation.TransitionStarted += Transitions.Add;
        Navigation.WindowSubmitted += OnWindow;
        Navigation.NativeHandoffObserved = observation =>
        {
            if (observation.Accepted)
            {
                Installed.Add(observation.Request);
            }
        };
    }

    private void OnWindow(long ticket, Transform3D[][] context, Transform3D[][] target)
    {
        Windows.Add(ticket);
        Targets[ticket] = target;
    }

    /// <summary>Submitted target windows by request ticket.</summary>
    public Dictionary<long, Transform3D[][]> Targets { get; } = [];

    /// <summary>Request tickets whose results were installed as playing segments.</summary>
    public List<long> Installed { get; } = [];

    private long _delayMilliseconds;

    /// <summary>Holds back the next generation on the worker thread by a wall-clock delay, once.</summary>
    public void DelayNextGeneration(TimeSpan delay) => Interlocked.Exchange(ref _delayMilliseconds, (long)delay.TotalMilliseconds);

    private void TakeDelay()
    {
        long delay = Interlocked.Exchange(ref _delayMilliseconds, 0);
        if (delay > 0)
        {
            Thread.Sleep((int)delay);
        }
    }

    private sealed class DelayingSession(SomaInferenceWorker.ISession inner, NativeIdleTransitionHarness harness) : SomaInferenceWorker.ISession
    {
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            harness.TakeDelay();
            return inner.Generate(context, target);
        }

        public Transform3D[][] GeneratePresented(Transform3D[][] context, Transform3D[][] target, Transform3D[] start)
        {
            harness.TakeDelay();
            return inner.GeneratePresented(context, target, start);
        }

        public void Dispose() => inner.Dispose();
    }

    /// <summary>Whether trace and measurement files are written (set ALLEYCAT_ANIM003_TRACES=1 to keep evidence).</summary>
    public static bool WritesTraces => System.Environment.GetEnvironmentVariable("ALLEYCAT_ANIM003_TRACES") == "1";

    public int Bone(string name)
    {
        int bone = Skeleton.FindBone(name);
        Assert.True(bone >= 0, $"Missing bone {name}.");
        return bone;
    }

    public void StartRecording()
    {
        _frames.Clear();
        _recording = true;
    }

    public void StopRecording() => _recording = false;

    private void Record()
    {
        if (!_recording)
        {
            return;
        }

        Skeleton3D s = Skeleton;
        var body = new Quaternion[_body.Length];
        for (int i = 0; i < _body.Length; i++)
        {
            body[i] = s.GetBonePoseRotation(_body[i]);
        }

        // The root body bone is measured in world space: game-owned actor yaw changes its local frame, not its look.
        body[0] = (s.GlobalBasis.Orthonormalized() * s.GetBoneGlobalPose(_body[0]).Basis).GetRotationQuaternion();

        var unmapped = new Transform3D[_unmapped.Length];
        for (int i = 0; i < _unmapped.Length; i++)
        {
            unmapped[i] = s.GetBonePose(_unmapped[i]);
        }

        var landmarks = new Vector3[_landmarks.Length];
        for (int i = 0; i < _landmarks.Length; i++)
        {
            landmarks[i] = s.GlobalTransform * s.GetBoneGlobalPose(_landmarks[i]).Origin;
        }

        _frames.Add(new Frame((long)Engine.GetPhysicsFrames(), Navigation.ObservationTime, Navigation.Phase, Navigation.NativeIdleOwnsBody,
            Retarget.Active, Navigation.IdleModifier!.Mode, Navigation.VelocityBlendActive, body, unmapped, landmarks[0],
            Actor.GlobalPosition, landmarks));
    }

    public Task PhysicsFramesAsync(int count) => TestUtils.WaitForPhysicsFramesAsync(_tree, count);

    /// <summary>Waits until a condition holds, failing after a bounded number of physics frames.</summary>
    public async Task WaitUntilAsync(Func<bool> condition, int maxFrames, string what)
    {
        for (int i = 0; i < maxFrames; i++)
        {
            if (condition())
            {
                return;
            }

            await TestUtils.WaitForNextPhysicsFrameAsync(_tree);
        }

        Assert.Fail($"Timed out after {maxFrames} physics frames waiting for {what}; phase {Navigation.Phase}, failure '{Navigation.Failure}'.");
    }

    /// <summary>
    /// Settles the cold model load with one idle round trip, so measured transitions use a warm session.
    /// </summary>
    public async Task WarmUpAsync()
    {
        Assert.True(Navigation.SetIdlePreference("slump"));
        await WaitUntilAsync(() => Navigation.NativeIdleOwnsBody && Navigation.CurrentIdle?.ID == "slump", 1200, "warm-up slump");
        Assert.True(Navigation.SetIdlePreference(null));
        await WaitUntilAsync(() => Navigation.NativeIdleOwnsBody && Navigation.CurrentIdle?.ID == "sad", 600, "warm-up sad");
        Transitions.Clear();
        Assert.Equal("", Navigation.Failure);
    }

    /// <summary>Writes the recorded trace as CSV under <c>res://temp/anim003-transitions/traces/</c>.</summary>
    public void Dump(string name)
    {
        if (!WritesTraces)
        {
            return;
        }

        string directory = ProjectSettings.GlobalizePath("res://temp/anim003-transitions/traces/");
        _ = Directory.CreateDirectory(directory);
        var csv = new StringBuilder("tick,time,phase,native_idle,retarget,mode,blend,rot_delta_30hz_deg,rot_bone,acc_30hz_deg,unmapped_rot_delta_deg,unmapped_pos_delta_cm,hips_x,hips_y,hips_z,actor_x,actor_z,hips_offset_cm\n");
        CultureInfo c = CultureInfo.InvariantCulture;
        for (int i = 0; i < _frames.Count; i++)
        {
            Frame f = _frames[i];
            (double delta, int bone) = i >= 2 ? MaxRotationDelta(_frames[i - 2], f) : (0, 0);
            double acc = i >= 4 ? MaxAcceleration(_frames[i - 4], _frames[i - 2], f) : 0;
            (double ur, double up) = i >= 2 ? UnmappedDelta(_frames[i - 2], f) : (0, 0);
            Vector3 offset = f.HipsWorld - f.ActorWorld;
            _ = csv.Append(c, $"{f.Tick},{f.Time:F4},{f.Phase},{f.NativeIdle},{f.RetargetActive},{f.Mode},{f.Blend},{delta:F4},{BodyBones[bone]},{acc:F4},{ur:F4},{up * 100:F4},{f.HipsWorld.X:F5},{f.HipsWorld.Y:F5},{f.HipsWorld.Z:F5},{f.ActorWorld.X:F5},{f.ActorWorld.Z:F5},{new Vector2(offset.X, offset.Z).Length() * 100:F3}\n");
        }

        File.WriteAllText(Path.Combine(directory, name + ".csv"), csv.ToString());
    }

    /// <summary>Largest body-bone local rotation change between two frames, in degrees, and its bone.</summary>
    public static (double Degrees, int Bone) MaxRotationDelta(Frame a, Frame b)
    {
        double worst = 0;
        int bone = 0;
        for (int i = 0; i < a.Body.Length; i++)
        {
            double angle = Mathf.RadToDeg(a.Body[i].AngleTo(b.Body[i]));
            if (angle > worst)
            {
                worst = angle;
                bone = i;
            }
        }

        return (worst, bone);
    }

    /// <summary>Largest change of per-span body-bone angular velocity across three equally spaced frames, in degrees.</summary>
    public static double MaxAcceleration(Frame a, Frame b, Frame c)
    {
        double worst = 0;
        for (int i = 0; i < a.Body.Length; i++)
        {
            Quaternion first = b.Body[i] * a.Body[i].Inverse();
            Quaternion second = c.Body[i] * b.Body[i].Inverse();
            worst = Math.Max(worst, Mathf.RadToDeg(first.AngleTo(second)));
        }

        return worst;
    }

    /// <summary>Largest unmapped-bone local rotation (degrees) and position (metres) change between two frames.</summary>
    public static (double Degrees, double Metres) UnmappedDelta(Frame a, Frame b)
    {
        double rotation = 0, position = 0;
        for (int i = 0; i < a.Unmapped.Length; i++)
        {
            rotation = Math.Max(rotation, Mathf.RadToDeg(a.Unmapped[i].Basis.GetRotationQuaternion().AngleTo(b.Unmapped[i].Basis.GetRotationQuaternion())));
            position = Math.Max(position, a.Unmapped[i].Origin.DistanceTo(b.Unmapped[i].Origin));
        }

        return (rotation, position);
    }

    public async ValueTask DisposeAsync()
    {
        if (_skeleton is not null && GodotObject.IsInstanceValid(_skeleton))
        {
            _skeleton.SkeletonUpdated -= Record;
        }

        if (Navigation is not null && GodotObject.IsInstanceValid(Navigation))
        {
            Navigation.TransitionStarted -= Transitions.Add;
            Navigation.WindowSubmitted -= OnWindow;
            Navigation.SetPhysicsProcess(false);
        }

        if (_root is HarnessGame game)
        {
            await game.DrainMotionAndDiagnosticsAsync();
        }

        _root.QueueFree();
        await TestUtils.WaitForPhysicsFramesAsync(_tree, 2);
    }

    private sealed partial class HarnessGame : Game
    {
        public override void _Ready()
        {
        }
    }
}
