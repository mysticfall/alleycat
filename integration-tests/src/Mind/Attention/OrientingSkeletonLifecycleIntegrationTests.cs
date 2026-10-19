using System.Reflection;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.IK;
using AlleyCat.Mind.Attention;
using AlleyCat.Rigging.Physics;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using Environment = System.Environment;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.Attention;

/// <summary>
/// HEAD-3 feedback-sensitive evidence uses the real female skeleton, animation and CharacterIK pipeline.
/// Unlike deliberately skeleton-less adapter tests, these rigs must never take the bootstrap-rest path.
/// All observations are engine state, not screenshots; this class is headless-compatible.
/// </summary>
[Headless]
public sealed class OrientingSkeletonLifecycleIntegrationTests
{
    private const double Delta = 1d / 60d;

    /// <summary>Co-moving geometry must yield the same commands despite root transport between evaluations.</summary>
    [Fact]
    public async Task RootTransport_PreservesCommandsAndRespondsToWorldChanges()
    {
        await using Rig control = await Rig.CreateAsync();
        await using Rig moved = await Rig.CreateAsync();
        control.Assign(35f);
        moved.Assign(35f);
        double maximumAimDelta = 0d;
        float maximumOriginDelta = 0f;
        HashSet<string> classifications = [];
        for (int frame = 0; frame < 240; frame++)
        {
            // Small motion, large motion, pure rotation, then transport during release.
            if (frame is 18 or 28 or 90 or 190)
            {
                float yaw = frame switch
                {
                    18 => 5f,
                    28 => 120f,
                    90 => -80f,
                    _ => 25f
                };
                Vector3 origin = frame == 90 ? moved.Root.Position : new Vector3(frame / 20f, 0.3f, -2f);
                moved.Root.Transform = new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(yaw)), origin);
            }

            if (frame == 170)
            {
                control.Eyes.ClearLookTarget();
                moved.Eyes.ClearLookTarget();
            }

            await StepAsync(control, moved);
            OrientingAim a = control.Controller.LastPublishedAim;
            OrientingAim b = moved.Controller.LastPublishedAim;
            maximumAimDelta = Math.Max(maximumAimDelta, Math.Max(
                Math.Abs(a.HorizontalRadians - b.HorizontalRadians), Math.Abs(a.VerticalRadians - b.VerticalRadians)));
            maximumOriginDelta = Math.Max(maximumOriginDelta,
                control.LocalRequestedOrigin.DistanceTo(moved.LocalRequestedOrigin));
            Assert.True(maximumAimDelta < 1e-4d, $"Root-only transport changed the requested aim by {maximumAimDelta:R} rad at frame {frame}.");
            Assert.True(maximumOriginDelta < 1e-3f, $"Root-only transport changed the requested local origin by {maximumOriginDelta:R} m at frame {frame}.");
            Assert.Equal(a.Influence, b.Influence, 5);
            string classification = ReadClassification(control.Controller);
            Assert.Equal(classification, ReadClassification(moved.Controller));
            _ = classifications.Add(classification);
        }

        // Genuine world change: keep the anchor in world space while rotating the root, then compare to
        // an unrotated rig given exactly that changed character-relative geometry.
        control.Root.Transform = Transform3D.Identity;
        moved.Root.Transform = Transform3D.Identity;
        control.Assign(10f);
        moved.Assign(10f);
        Vector3 fixedWorld = moved.Anchor.GlobalPosition;
        moved.Root.Basis = new Basis(Vector3.Up, Mathf.DegToRad(35f));
        moved.Anchor.GlobalPosition = fixedWorld;
        control.Anchor.Position = moved.Anchor.Position;
        for (int frame = 0; frame < 240; frame++)
        {
            await StepAsync(control, moved);
        }

        Assert.True(Math.Abs(control.Controller.LastPublishedAim.HorizontalRadians
            - moved.Controller.LastPublishedAim.HorizontalRadians) < 1e-4d,
            "A fixed-world anchor must converge to the independently equivalent character-relative geometry.");
        Assert.True(Math.Abs(moved.Controller.LastPublishedAim.HorizontalRadians - Mathf.DegToRad(10f)) > 0.2d,
            "Non-vacuity: genuine world geometry must change the command.");
        Assert.Contains("Saturation", classifications);
        Assert.Contains("Centring", classifications);
        Assert.Contains("None", classifications);
        WriteEvidence("root", $"maximum aim delta rad={maximumAimDelta:R}; maximum local origin delta m={maximumOriginDelta:R}; observed equal classifications={string.Join(',', classifications)}; world-change aims={control.Controller.LastPublishedAim}/{moved.Controller.LastPublishedAim}");
    }

    /// <summary>Real animation regains its nonzero authored roll when head influence reaches zero.</summary>
    [Fact]
    public async Task Release_RestoresNativeRolledAnimationAndReengagesFromCurrentState()
    {
        await using Rig rig = await Rig.CreateAsync(rollDegrees: 8f);
        Basis native = rig.Viewpoint.GlobalBasis;
        Assert.True(Mathf.Abs(RollDegrees(native)) > 7f, "The native upstream animation must actually contain roll.");
        rig.Assign(35f);
        await RunAsync(rig, 24); // Release during the saturation episode, before centring eligibility.
        Assert.True(rig.Controller.GetTargetIntent().DesiredInfluence > 0.5f);
        double initial = Math.Abs(rig.Controller.LastPublishedAim.HorizontalRadians);
        rig.Eyes.ClearLookTarget();
        await RunAsync(rig, 15);
        double middle = Math.Abs(rig.Controller.LastPublishedAim.HorizontalRadians);
        Assert.True(middle < initial, "Release must ease towards the upstream roll-free direction.");
        await RunAsync(rig, 180);
        Assert.Equal(0f, rig.Controller.GetTargetIntent().DesiredInfluence);
        double restoredAngle = BasisAngle(native, rig.Viewpoint.GlobalBasis);
        Assert.True(restoredAngle < 1e-3d, $"Native animation including roll was not restored: {restoredAngle:R} rad.");
        Assert.True(Mathf.Abs(RollDegrees(rig.Viewpoint.GlobalBasis)) > 7f);
        float restoredRoll = RollDegrees(rig.Viewpoint.GlobalBasis);
        rig.Assign(10f);
        await RunAsync(rig, 6);
        Assert.True(Math.Abs(rig.Controller.LastPublishedAim.HorizontalRadians) < 1e-3d,
            "Brief in-cone re-engagement must hold explicit released state, not the old saturation command.");
        WriteEvidence("roll-release", $"initial yaw rad={initial:R}; middle={middle:R}; restored basis angle rad={restoredAngle:R}; native/restored roll deg={RollDegrees(native):R}/{restoredRoll:R}");
    }

    /// <summary>Identical fixed anchor geometry converges identically after release, glance and saturation.</summary>
    [Fact]
    public async Task Histories_ConvergeToIdenticalRequestedAims()
    {
        await using Rig rig = await Rig.CreateAsync();
        rig.Assign(25f);
        Vector3 target = rig.Anchor.Position;
        await RunAsync(rig, 240);
        OrientingAim fresh = rig.Controller.LastPublishedAim;
        List<OrientingAim> results = [fresh];
        for (int history = 0; history < 3; history++)
        {
            if (history == 0)
            {
                rig.Eyes.ClearLookTarget();
                await RunAsync(rig, 180);
            }
            else if (history == 1)
            {
                rig.Assign(30f);
                await RunAsync(rig, 6); // In-cone different anchor, below the unchanged 0.6 s delay.
            }
            else
            {
                rig.Eyes.ClearLookTarget();
                await RunAsync(rig, 180);
                rig.Assign(18f);
                await RunAsync(rig, 32); // Last pre-eligibility ticks of the unchanged 0.6 s delay.
                double parkedYaw = rig.Controller.LastPublishedAim.HorizontalRadians;
                await RunAsync(rig, 3);
                Vector3 faceLocal = rig.Viewpoint.GlobalBasis.Inverse()
                    * (rig.Anchor.GlobalPosition - rig.Viewpoint.GlobalPosition).Normalized();
                double residualDegrees = Mathf.RadToDeg(Mathf.Atan2(-faceLocal.X, -faceLocal.Z));
                Assert.True(residualDegrees is > 14.5d and < 15.5d,
                    $"The saturation history must genuinely reach the authored 15° residual, got {residualDegrees:R}°.");
                Assert.True(Math.Abs(rig.Controller.LastPublishedAim.HorizontalRadians - parkedYaw) < Mathf.DegToRad(0.05f),
                    $"The saturation command must genuinely park before the target is replaced; delta={Math.Abs(rig.Controller.LastPublishedAim.HorizontalRadians - parkedYaw):R} rad.");
                WriteEvidence("park-history", $"park residual deg={residualDegrees:R}; three-frame command delta rad={Math.Abs(rig.Controller.LastPublishedAim.HorizontalRadians - parkedYaw):R}; elapsed seconds={35d * Delta:R}");
            }

            rig.Assign(25f);
            rig.Anchor.Position = target;
            await RunAsync(rig, 240);
            OrientingAim settled = rig.Controller.LastPublishedAim;
            await RunAsync(rig, 30);
            OrientingAim extra = rig.Controller.LastPublishedAim;
            Assert.True(Math.Abs(extra.HorizontalRadians - settled.HorizontalRadians) < 1e-5d,
                "Transient easing must have completed before the history comparison.");
            Assert.True(Math.Abs(extra.HorizontalRadians - fresh.HorizontalRadians) < 1e-4d
                && Math.Abs(extra.VerticalRadians - fresh.VerticalRadians) < 1e-4d,
                $"History {history} changed the converged aim: fresh={fresh}; current={extra}.");
            results.Add(extra);
        }

        Assert.True(fresh.HorizontalRadians > 0.3d, "Non-vacuity: the settled command must have turned.");
        WriteEvidence("histories", string.Join(Environment.NewLine, results));
    }

    /// <summary>TR14–15: render callbacks cannot invent evaluations between installed Begin stages.</summary>
    [Fact]
    public async Task BoundProvider_RenderCallbacksDoNotAdvanceSkeletonEvaluation()
    {
        await using Rig rig = await Rig.CreateAsync();
        rig.Assign(40f);
        await WaitForPhysicsFramesAsync(GetSceneTree(), 2);
        OrientingUpstreamSnapshot before = Assert.IsType<OrientingUpstreamSnapshot>(rig.Controller.LastUpstreamSnapshot);
        for (int render = 0; render < 12; render++)
        {
            rig.Controller._Process(Delta);
            _ = rig.Controller.GetTargetIntent();
        }

        Assert.Equal(before.EvaluationSequence, rig.Controller.LastUpstreamSnapshot!.EvaluationSequence);
        Assert.Equal("begin", rig.Controller.LastUpstreamSnapshot.EvaluationMode);
    }

    /// <summary>TR14: final bones, not attachment callbacks, own the preceding evaluation packet.</summary>
    [Fact]
    public async Task BeginFeedback_UsesFinalBonesAndCurrentRoot_AndInvalidatesOnRebind()
    {
        await using Rig rig = await Rig.CreateAsync();
        rig.Skeleton.ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Manual;
        rig.Root.GetNode<AnimationPlayer>("UpstreamNeutral").Stop();
        await WaitForFramesAsync(GetSceneTree(), 2);
        rig.Assign(40f);
        Node3D attachment = rig.Viewpoint.GetParent<Node3D>();
        void CorruptAttachment()
        {
            attachment.Position = new Vector3(5f, 6f, 7f);
        }

        rig.Skeleton.SkeletonUpdated += CorruptAttachment;
        // A changed pivot offset forces a new binding; its completion subscriber is now after ours.
        Node3D pivot = rig.Controller.NeckPivot!;
        pivot.Position += new Vector3(0f, 0.00001f, 0f);
        try
        {
            rig.Skeleton.Advance(Delta);
            await WaitForFramesAsync(GetSceneTree(), 2);
            Assert.True(rig.Controller.LastFeedbackIsBootstrap);
            OrientingUpstreamSnapshot first = rig.Controller.LastUpstreamSnapshot!;

            // Observe the next final pose independently, with the same corrupted attachment ordering.
            Transform3D finalHead = Transform3D.Identity;
            Vector3 finalEye = Vector3.Zero;
            void ObserveFinalBones()
            {
                finalHead = rig.Skeleton.GlobalTransform * rig.Skeleton.GetBoneGlobalPose(rig.Skeleton.FindBone("Head"));
                finalEye = (finalHead * rig.Viewpoint.Transform).Origin;
            }

            rig.Skeleton.SkeletonUpdated += ObserveFinalBones;
            try
            {
                rig.Skeleton.Advance(Delta);
                await WaitForFramesAsync(GetSceneTree(), 2);
            }
            finally
            {
                rig.Skeleton.SkeletonUpdated -= ObserveFinalBones;
            }

            Assert.Equal(first.EvaluationSequence, rig.Controller.LastFeedbackSequence);
            ulong completedSequence = rig.Controller.LastUpstreamSnapshot!.EvaluationSequence;
            Transform3D captureRoot = rig.Root.GlobalTransform;
            Transform3D relativeHead = captureRoot.AffineInverse() * finalHead;
            Vector3 relativeEye = captureRoot.AffineInverse() * finalEye;
            Assert.True((rig.Viewpoint.GlobalPosition - finalEye).Length() > 1f,
                "Attachment sabotage must distinguish direct bone feedback from live attachment reads.");

            rig.Root.Transform = new Transform3D(new Basis(Vector3.Up, 0.6f), new Vector3(2f, 0.1f, -3f));
            OrientingAim aimBefore = rig.Controller.LastPublishedAim;
            double elapsedBefore = ReadPolicyElapsed(rig.Controller);
            rig.Skeleton.Advance(0d);
            await WaitForFramesAsync(GetSceneTree(), 2);
            Assert.False(rig.Controller.LastFeedbackIsBootstrap);
            Assert.Equal(completedSequence, rig.Controller.LastFeedbackSequence);
            Transform3D expectedHead = rig.Root.GlobalTransform * relativeHead;
            Assert.True((expectedHead.Origin - rig.Controller.LastFeedbackHeadFrame.Origin).Length() < 0.00001f);
            Assert.True(BasisAngle(expectedHead.Basis, rig.Controller.LastFeedbackHeadFrame.Basis) < 0.001d);
            Assert.True(((rig.Root.GlobalTransform * relativeEye) - rig.Controller.LastFeedbackEyeOrigin).Length() < 0.00001f);
            Assert.Equal(aimBefore, rig.Controller.LastPublishedAim);
            Assert.Equal(elapsedBefore, ReadPolicyElapsed(rig.Controller));

            // Unpaired pose-only notifications cannot replace the retained preceding completion.
            _ = rig.Skeleton.EmitSignal(Skeleton3D.SignalName.SkeletonUpdated);
            _ = rig.Skeleton.EmitSignal(Skeleton3D.SignalName.SkeletonUpdated);
            ulong before = rig.Controller.LastUpstreamSnapshot!.EvaluationSequence;
            rig.Skeleton.Advance(Delta);
            await WaitForFramesAsync(GetSceneTree(), 2);
            Assert.Equal(before + 1, rig.Controller.LastUpstreamSnapshot!.EvaluationSequence);
            Assert.Equal(before, rig.Controller.LastFeedbackSequence);

            // A new marker on the same bone with exactly the same authored transform is still a rebind.
            Node3D originalPivot = rig.Controller.NeckPivot!;
            var replacementPivot = new Marker3D { Transform = originalPivot.Transform };
            originalPivot.GetParent().AddChild(replacementPivot);
            ulong generationBeforeReplacement = rig.Controller.LastUpstreamSnapshot.Generation;
            Assert.NotSame(originalPivot, replacementPivot);
            Assert.Equal(originalPivot.Transform, replacementPivot.Transform);
            Assert.Same(originalPivot.GetParent(), replacementPivot.GetParent());
            rig.Controller.NeckPivot = replacementPivot;
            rig.Skeleton.Advance(Delta);
            await WaitForFramesAsync(GetSceneTree(), 2);
            Assert.True(rig.Controller.LastUpstreamSnapshot!.Generation > generationBeforeReplacement,
                "Replacing NeckPivot identity must invalidate even identical bone/offset geometry.");
            Assert.True(rig.Controller.LastFeedbackIsBootstrap);
            Assert.Null(rig.Controller.LastFeedbackSequence);

            Marker3D original = rig.Viewpoint;
            rig.Controller.Viewpoint = null;
            rig.Skeleton.Advance(Delta);
            await WaitForFramesAsync(GetSceneTree(), 2);
            Assert.Null(rig.Controller.LastUpstreamSnapshot);
            Assert.Null(rig.Controller.LastFeedbackSequence);
            Assert.Equal(0f, rig.Controller.GetTargetIntent().DesiredInfluence);
            rig.Controller.Viewpoint = original;
            rig.Skeleton.Advance(Delta);
            await WaitForFramesAsync(GetSceneTree(), 2);
            Assert.True(rig.Controller.LastFeedbackIsBootstrap);
            Assert.Null(rig.Controller.LastFeedbackSequence);
            Assert.True(rig.Controller.LastUpstreamSnapshot!.Generation > first.Generation);
        }
        finally
        {
            rig.Skeleton.SkeletonUpdated -= CorruptAttachment;
        }
    }

    /// <summary>The Begin capability must select exactly the provider subsequently consumed by the head pipeline.</summary>
    [Fact]
    public async Task BeginSelection_MatchesConsumedValidPrimaryOrFallback()
    {
        await using Rig rig = await Rig.CreateAsync();
        rig.Skeleton.ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Manual;
        rig.Root.GetNode<AnimationPlayer>("UpstreamNeutral").Stop();
        await WaitForFramesAsync(GetSceneTree(), 2);
        CharacterIK ik = rig.Root.GetChildren().OfType<CharacterIK>().Single();
        var primary = new BeginCountingProvider();
        var fallback = new BeginCountingProvider();
        rig.Root.AddChild(primary);
        rig.Root.AddChild(fallback);
        ik.HeadFallbackIntentProvider = fallback;

        ik.HeadTargetIntentProvider = null;
        await CheckStepAsync(fallback, primary);
        ik.HeadTargetIntentProvider = primary;
        await CheckStepAsync(primary, fallback);

        // Keep the non-null managed reference after freeing the native node, matching the reported branch.
        primary.Free();
        Assert.False(GodotObject.IsInstanceValid(primary));
        Assert.NotNull(ik.HeadTargetIntentProvider);
        await CheckStepAsync(fallback, primary);

        async Task CheckStepAsync(BeginCountingProvider selected, BeginCountingProvider unselected)
        {
            selected.Events.Clear();
            unselected.Events.Clear();
            rig.Skeleton.Advance(Delta);
            await WaitForFramesAsync(GetSceneTree(), 2);
            Assert.Equal(new[] { "begin", "read" }, selected.Events);
            Assert.Empty(unselected.Events);
        }
    }

    private sealed partial class BeginCountingProvider : IKTargetIntentProvider, IIKBeginEvaluable
    {
        public List<string> Events { get; } = [];

        public void EvaluateAtBegin(Skeleton3D skeleton, double delta, ulong sequence) => Events.Add("begin");

        public override IKTargetIntent GetTargetIntent()
        {
            Events.Add("read");
            return new IKTargetIntent(Transform3D.Identity, 0f);
        }
    }

    private static double ReadPolicyElapsed(OrientingController controller)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object policy = typeof(OrientingController).GetField("_policy", flags)!.GetValue(controller)!;
        return (double)typeof(OrientingPolicy).GetField("_anchorElapsedSeconds", flags)!.GetValue(policy)!;
    }

    private static async Task RunAsync(Rig rig, int frames)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            await StepAsync(rig);
        }
    }

    private static async Task StepAsync(params Rig[] rigs)
    {
        foreach (Rig rig in rigs)
        {
            rig.Controller._Process(Delta);
            Assert.NotNull(rig.Controller.LastUpstreamSnapshot);
            Assert.Equal(rig.Skeleton.GetInstanceId(), rig.Controller.LastUpstreamSnapshot!.SkeletonIdentity);
        }

        // Real animation/modifier ticks, never followed-head assignments or solved-bone overwrites.
        await WaitForPhysicsFramesAsync(GetSceneTree(), 1);
        await WaitForFramesAsync(GetSceneTree(), 1);
    }

    private static double BasisAngle(Basis a, Basis b)
        => a.GetRotationQuaternion().AngleTo(b.GetRotationQuaternion());

    // Read-only test observation: compare the actual classification across equivalent geometries,
    // without calling or duplicating the policy's classification algorithm as an expected-value oracle.
    private static string ReadClassification(OrientingController controller)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object policy = typeof(OrientingController).GetField("_policy", flags)!.GetValue(controller)!;
        return typeof(OrientingPolicy).GetField("_mode", flags)!.GetValue(policy)!.ToString()!;
    }

    private static float RollDegrees(Basis basis)
    {
        Vector3 up = basis.Transposed() * Vector3.Up;
        return Mathf.RadToDeg(Mathf.Atan2(up.X, up.Y));
    }

    private static void WriteEvidence(string name, string value)
    {
        string? directory = Environment.GetEnvironmentVariable("ALLEYCAT_HEAD3_EVIDENCE");
        if (directory is not null)
        {
            File.WriteAllText(Path.Combine(directory, name + ".txt"), value);
        }
    }

    private sealed class Rig(Node3D root, Skeleton3D skeleton, Marker3D viewpoint,
        EyesBehaviour eyes, OrientingController controller, StaticVisualCue anchor) : IAsyncDisposable
    {
        public Node3D Root => root;
        public Skeleton3D Skeleton => skeleton;
        public Marker3D Viewpoint => viewpoint;
        public EyesBehaviour Eyes => eyes;
        public OrientingController Controller => controller;
        public StaticVisualCue Anchor { get; private set; } = anchor;
        public Vector3 LocalRequestedOrigin => root.GlobalTransform.AffineInverse() * controller.LastRequestedAimTargetGlobal;
        private Vector3 _nativeEyeOrigin;
        private Basis _nativeFace;
        private readonly Dictionary<float, StaticVisualCue> _anchors = [];

        public static async Task<Rig> CreateAsync(float rollDegrees = 0f)
        {
            var root = (Node3D)ResourceLoader.Load<GDScript>("res://tests/ai009_head_orientation/isolated_female_tracking.gd").New();
            Skeleton3D skeleton = root.GetNode<Skeleton3D>("Female/GeneralSkeleton");
            Marker3D viewpoint = skeleton.GetNode<Marker3D>("Head/Viewpoint");
            Node3D pivot = skeleton.GetNode<Node3D>("Neck/NeckPivot");
            if (rollDegrees != 0f)
            {
                Animation animation = root.GetNode<AnimationPlayer>("UpstreamNeutral").GetAnimation("Neutral");
                int track = animation.FindTrack(new NodePath("Female/GeneralSkeleton:Head"), Animation.TrackType.Rotation3D);
                Quaternion rest = skeleton.GetBoneRest(skeleton.FindBone("Head")).Basis.GetRotationQuaternion();
                animation.TrackSetKeyValue(track, 0, rest * new Quaternion(Vector3.Back, Mathf.DegToRad(rollDegrees)));
            }

            DynamicPhysicalRig physical = new()
            {
                TargetSkeleton = skeleton,
                Enabled = false
            };
            skeleton.AddChild(physical);
            EyesBehaviour eyes = new()
            {
                EyeOrigin = viewpoint,
                LeftEye = skeleton.GetNode<Node3D>("Head/Eyes/LeftEye"),
                RightEye = skeleton.GetNode<Node3D>("Head/Eyes/RightEye"),
                SaccadeAmplitude = 0,
                SaccadeInterval = 0,
            };
            root.AddChild(eyes);
            TestMind mind = new(new NodeCharacter(root, eyes));
            OrientingController controller = new()
            {
                Viewpoint = viewpoint,
                NeckPivot = pivot
            };
            mind.AddChild(controller);
            root.AddChild(mind);
            CharacterIK ik = new()
            {
                Viewpoint = viewpoint,
                PhysicalRig = physical,
                HeadTargetIntentProvider = controller,
                HeadIKTarget = root.GetNode<CharacterBody3D>("IKTargets/Head"),
                HeadIKSolveTarget = root.GetNode<Marker3D>("IKTargets/HeadSolve"),
                RightHandIKTarget = root.GetNode<AnimatableBody3D>("IKTargets/RightHand"),
                LeftHandIKTarget = root.GetNode<AnimatableBody3D>("IKTargets/LeftHand"),
                HeadModifierGroup = [skeleton.GetNode<SkeletonModifier3D>("NeckSpineIK"),
                    skeleton.GetNode<SkeletonModifier3D>("HeadCopyRotation"), skeleton.GetNode<SkeletonModifier3D>("NeckTwistDisperser")],
            };
            root.AddChild(ik);
            StaticVisualCue anchor = new();
            root.AddChild(anchor);
            GetSceneTree().Root.AddChild(root);
            controller.SetProcess(false);
            await WaitForPhysicsFramesAsync(GetSceneTree(), 3);
            await WaitForFramesAsync(GetSceneTree(), 1);
            controller._Process(Delta);
            return new Rig(root, skeleton, viewpoint, eyes, controller, anchor)
            {
                _nativeEyeOrigin = root.GlobalTransform.AffineInverse() * viewpoint.GlobalPosition,
                _nativeFace = root.GlobalBasis.Inverse() * viewpoint.GlobalBasis,
            };
        }

        public void Assign(float yawDegrees)
        {
            if (!_anchors.TryGetValue(yawDegrees, out StaticVisualCue? cue))
            {
                cue = new StaticVisualCue();
                root.AddChild(cue);
                _anchors.Add(yawDegrees, cue);
            }

            Anchor = cue;
            float yaw = Mathf.DegToRad(yawDegrees);
            Anchor.Position = _nativeEyeOrigin + (_nativeFace * new Vector3(-Mathf.Sin(yaw), 0f, -Mathf.Cos(yaw)) * 1.5f);
            eyes.SetLookTarget(Anchor);
        }

        public async ValueTask DisposeAsync()
        {
            root.QueueFree();
            await WaitForFramesAsync(GetSceneTree(), 2);
        }
    }

    private sealed partial class TestMind(ICharacter character) : MindBase
    {
        protected override ICharacter ResolveOwningCharacter() => character;
    }

    private sealed class NodeCharacter(Node3D root, EyesBehaviour eyes) : ICharacter, IComponentProjectionNotifier
    {
        public string Id { get; set; } = "head3-skeleton";
        public IReadOnlyList<IComponent> Components { get; } = [eyes];
        public bool HasComponentProjection => true;
        public IReadOnlyList<VisualCue> VisualCues { get; } = [];
        public Transform3D GlobalTransform => root.GlobalTransform;
        public event Action? ComponentsRefreshed { add { } remove { } }
    }
}
