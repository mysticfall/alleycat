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
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.Attention;

/// <summary>
/// Runtime proof of the PRODUCTION downward fold integration on the real female fixture skeleton:
/// the normal controller path — no prototype override — keeps the unchanged TR 21 orbit construction
/// for neutral and lateral commands (gate abstention, non-regressed yaw/mixed behaviour), publishes a
/// legal fold-family member instead of the geometrically excluded orbit for deep downward demands, and
/// falls back to the orbit construction with an explicit outside-domain diagnostic when the upstream
/// leaves the neutral domain. The fold-grid oracle here is independent: authored cone caps read
/// straight from the NeckSpineIK modifier, chain rests from the skeleton, FK through the shared pure
/// geometry functions that carry their own unit coverage.
/// </summary>
/// <remarks>
/// Headless-safe: every assertion reads controller/bone state, never renderer output. The fixture's
/// upstream is the constant rest, which is exactly the fold family's supported neutral domain.
/// </remarks>
[Headless]
public sealed class ProductionDownwardFoldOriginIntegrationTests
{
    /// <summary>
    /// A settled neutral hold keeps the orbit path: the frame binds (the rig is inside the fold
    /// authoring domain) but the neutral command abstains, and the requested origin equals the
    /// independent first-principles rest construction.
    /// </summary>
    [Fact]
    public async Task NeutralHold_BindsFrameButKeepsOrbitRestConstruction()
    {
        await using Fixture rig = await Fixture.CreateAsync();
        await rig.SettleOnAsync(0f, 0f, 1.4f);

        rig.AssertFoldFrameBuilt();
        Assert.Equal("orbit_path", rig.Controller.LastFoldStatus);
        rig.AssertRequestedMatchesRestConstruction();
    }

    /// <summary>
    /// A settled lateral look keeps the orbit path: the yaw gate abstains and the requested origin
    /// still equals the independent rest construction (unchanged yaw/mixed behaviour).
    /// </summary>
    [Fact]
    public async Task LateralLook_KeepsOrbitRestConstruction()
    {
        await using Fixture rig = await Fixture.CreateAsync();
        await rig.SettleOnAsync(30f, 0f, 1.2f);

        rig.AssertFoldFrameBuilt();
        Assert.Equal("orbit_path", rig.Controller.LastFoldStatus);
        rig.AssertRequestedMatchesRestConstruction();
        Assert.True(
            Math.Abs(Mathf.RadToDeg((float)rig.Controller.LastPublishedAim.HorizontalRadians)) > 5f,
            "The lateral command must be materially yawed for this gate discriminator.");
    }

    /// <summary>
    /// A settled deep downward look publishes the selected fold-family member: off the geometrically
    /// excluded orbit target by more than 5 mm, exactly on the independently composed grid member at
    /// the controller's selected alpha, within the 0.1 mm FK reconstruction bound, and with the
    /// coupled geometric solve's predicted eye origin identical to the published pose's eye origin at
    /// the equilibrium command.
    /// </summary>
    [Fact]
    public async Task DeepDownwardLook_PublishesFoldMemberOffExcludedOrbit()
    {
        await using Fixture rig = await Fixture.CreateAsync();
        await rig.SettleOnAsync(0f, -60f, 1f);

        rig.AssertFoldFrameBuilt();
        Assert.Equal("fold_governed", rig.Controller.LastFoldStatus);
        DownwardFoldEvaluation evaluation = Assert.IsType<DownwardFoldEvaluation>(
            rig.Controller.LastFoldEvaluation);
        Assert.True(evaluation.Applied);
        Assert.True(
            evaluation.Alpha0ReconstructionMillimetres <= 0.1d,
            $"alpha=0 FK reconstruction {evaluation.Alpha0ReconstructionMillimetres:F6} mm must stay <=0.1 mm.");
        rig.AssertPublishedOnIndependentFoldGrid(evaluation.Alpha);
        rig.AssertPublishedOffExcludedOrbit(5f);
        rig.AssertCoupledPredictionMatchesPublishedPose();
    }

    /// <summary>
    /// PGI-B2 regression: a near-pitch deep command sweeping yaw across the former |yaw| = 0.5° gate
    /// boundary BOTH signs must keep the published origin continuous — the published request may move
    /// only with the sweep itself, never by a construction branch jump (independent arithmetic gives
    /// 55.48 mm at −39.64° and 89.20 mm at −55° just across the old gate on this rig).
    /// </summary>
    [Fact]
    public async Task DeepYawCrossing_AtDeepPitch_PublishedOriginStaysContinuous()
    {
        const float sweepSeconds = 6f;
        const float sweepYawFrom = -6f;
        const float sweepYawTo = 6f;
        const float maxStepMillimetres = 5f;

        await using Fixture rig = await Fixture.CreateAsync();
        await rig.SettleOnAsync(sweepYawFrom, -60f, 1f);

        StaticVisualCue anchor = new()
        {
            Name = "SweepAnchor"
        };
        rig.Root.AddChild(anchor);
        void Place(float yawDegrees)
        {
            float yaw = Mathf.DegToRad(yawDegrees);
            float pitch = Mathf.DegToRad(-60f);
            Vector3 direction = new(
                -Mathf.Sin(yaw) * Mathf.Cos(pitch),
                Mathf.Sin(pitch),
                -Mathf.Cos(yaw) * Mathf.Cos(pitch));
            anchor.GlobalPosition = rig.NeutralOrigin + (rig.NeutralBasis * direction * 1f);
        }

        Place(sweepYawFrom);
        rig.Eyes.SetLookTarget(anchor);
        await rig.SettleFramesAsync(240);

        double maxStep = 0;
        double crossingStep = 0;
        double finalYaw = double.NaN;
        List<string> _yawCrossDebug = [];
        Vector3 previous = rig.Controller.LastRequestedAimTargetGlobal;
        double sweepStart = Time.GetTicksMsec() / 1000d;
        while ((Time.GetTicksMsec() / 1000d) - sweepStart < sweepSeconds)
        {
            double elapsed = (Time.GetTicksMsec() / 1000d) - sweepStart;
            Place((float)(sweepYawFrom + ((sweepYawTo - sweepYawFrom) * (elapsed / sweepSeconds))));
            await rig.SettleFramesAsync(1);
            Vector3 requested = rig.Controller.LastRequestedAimTargetGlobal;
            double step = (requested - previous).Length() * 1000d;
            double yaw = rig.Controller.LastPublishedAim.HorizontalRadians * 180d / Math.PI;
            bool nearGate = Math.Abs(Math.Abs(yaw) - 0.5d) < 0.2d;
            maxStep = Math.Max(maxStep, step);
            if (nearGate)
            {
                crossingStep = Math.Max(crossingStep, step);
            }

            previous = requested;
            finalYaw = yaw;
            _yawCrossDebug.Add($"t={Time.GetTicksMsec()} yaw={yaw:F4} pitch={rig.Controller.LastPublishedAim.VerticalRadians * 180d / Math.PI:F3} status={rig.Controller.LastFoldStatus} step={step:F3} req={requested}");
        }

        File.WriteAllLines("/tmp/opencode/yawcross_debug.txt", _yawCrossDebug);

        Assert.True(
            Math.Abs(finalYaw) > 3d,
            $"The sweep must carry the command across the gate to the far side (final yaw {finalYaw:F3}°).");
        Assert.True(
            maxStep <= maxStepMillimetres,
            $"Deep yaw crossing: max published-origin step {maxStep:F3} mm exceeds the {maxStepMillimetres} mm " +
            $"continuity bound (crossing-region step {crossingStep:F3} mm; the old gate branch jumps " +
            "55.48/89.20 mm by independent arithmetic).");
    }

    /// <summary>
    /// A settled deep downward demand publishes through the fold before any animation runs — the
    /// animated-upstream fallback itself is covered on the harness fixture that can run the real
    /// native Idle clip (Fold_Domain_NativeIdleUpstream_FallsBackWithDiagnostic).
    /// </summary>
    [Fact]
    public async Task DeepDemandBeforeAnimation_PublishesThroughFold()
    {
        await using Fixture rig = await Fixture.CreateAsync();
        await rig.SettleOnAsync(0f, -40f, 1f);
        Assert.Equal("fold_governed", rig.Controller.LastFoldStatus);
        Assert.NotNull(rig.Controller.LastFoldEvaluation);
    }

    private sealed partial class Fixture : IAsyncDisposable
    {
        private const string SkeletonPath = "Female/GeneralSkeleton";

        private readonly SceneTree _tree = GetSceneTree();
        private readonly Node3D _neckPivot;
        private readonly CharacterIK _ik;
        private readonly int _headBone;

        private Fixture()
        {
            Root = (Node3D)ResourceLoader.Load<GDScript>(
                "res://tests/ai009_head_orientation/isolated_female_tracking.gd").New();
            Skeleton = Root.GetNode<Skeleton3D>(SkeletonPath);
            Viewpoint = Skeleton.GetNode<Marker3D>("Head/Viewpoint");
            _neckPivot = Skeleton.GetNode<Node3D>("Neck/NeckPivot");
            _headBone = Skeleton.FindBone("Head");

            DynamicPhysicalRig physical = new()
            {
                Name = "DynamicPhysicalRig",
                TargetSkeleton = Skeleton,
                Enabled = false,
            };
            Skeleton.AddChild(physical);
            EyesBehaviour eyes = new()
            {
                Name = "Eyes",
                EyeOrigin = Viewpoint,
                LeftEye = Skeleton.GetNode<Node3D>("Head/Eyes/LeftEye"),
                RightEye = Skeleton.GetNode<Node3D>("Head/Eyes/RightEye"),
                SaccadeAmplitude = 0,
                SaccadeInterval = 0,
            };
            Eyes = eyes;
            Root.AddChild(eyes);
            TestMind mind = new(new TestCharacter(eyes))
            {
                Name = "Mind"
            };
            Controller = new OrientingController
            {
                Name = "OrientingController",
                Viewpoint = Viewpoint,
                NeckPivot = _neckPivot
            };
            mind.AddChild(Controller);
            Root.AddChild(mind);
            _ik = new CharacterIK
            {
                Name = "CharacterIK",
                Viewpoint = Viewpoint,
                PhysicalRig = physical,
                HeadTargetIntentProvider = Controller,
                HeadIKTarget = Root.GetNode<CharacterBody3D>("IKTargets/Head"),
                HeadIKSolveTarget = Root.GetNode<Marker3D>("IKTargets/HeadSolve"),
                RightHandIKTarget = Root.GetNode<AnimatableBody3D>("IKTargets/RightHand"),
                LeftHandIKTarget = Root.GetNode<AnimatableBody3D>("IKTargets/LeftHand"),
                HeadModifierGroup =
                [
                    Skeleton.GetNode<SkeletonModifier3D>("NeckSpineIK"),
                    Skeleton.GetNode<SkeletonModifier3D>("HeadCopyRotation"),
                    Skeleton.GetNode<SkeletonModifier3D>("NeckTwistDisperser"),
                ],
            };
            Root.AddChild(_ik);
            _tree.Root.AddChild(Root);
        }

        public OrientingController Controller
        {
            get;
        }

        public Skeleton3D Skeleton
        {
            get;
        }

        /// <summary>The fixture root node (anchor placement for sweep scenarios).</summary>
        public Node3D Root
        {
            get;
        }

        /// <summary>The viewpoint marker (neutral-frame anchor placement for sweep scenarios).</summary>
        public Marker3D Viewpoint
        {
            get;
        }

        /// <summary>The eyes behaviour (anchor assignment in sweep scenarios).</summary>
        public EyesBehaviour Eyes
        {
            get;
        }

        /// <summary>Advances the given number of physics frames (bounded frame stepping).</summary>
        public async Task SettleFramesAsync(int frames) => await WaitForPhysicsFramesAsync(_tree, frames);

        public static async Task<Fixture> CreateAsync()
        {
            Fixture rig = new();
            await WaitForPhysicsFramesAsync(rig._tree, 3);
            // The authored neutral face frame captured before ANY target assignment: anchor placement
            // must reference this stable frame, never the solved head (which follows the pose).
            rig.NeutralBasis = rig.Viewpoint.GlobalBasis.Orthonormalized();
            rig.NeutralOrigin = rig.Viewpoint.GlobalPosition;
            return rig;
        }

        /// <summary>The authored neutral face basis captured before any assignment (anchor placement).</summary>
        public Basis NeutralBasis
        {
            get;
            private set;
        } = Basis.Identity;

        /// <summary>The authored neutral face origin captured before any assignment (anchor placement).</summary>
        public Vector3 NeutralOrigin
        {
            get;
            private set;
        }

        /// <summary>
        /// Assigns an anchor at the given yaw/pitch/distance from the current neutral face pose and
        /// waits for full engagement and aim settling (bounded loop, physics-frame polling).
        /// </summary>
        public async Task SettleOnAsync(float yaw, float pitch, float distance)
        {
            float y = Mathf.DegToRad(yaw);
            float p = Mathf.DegToRad(pitch);
            Basis neutral = Viewpoint.GlobalBasis.Orthonormalized();
            Vector3 direction = new(
                -Mathf.Sin(y) * Mathf.Cos(p),
                Mathf.Sin(p),
                -Mathf.Cos(y) * Mathf.Cos(p));
            StaticVisualCue anchor = new()
            {
                Name = "Anchor"
            };
            Root.AddChild(anchor);
            anchor.GlobalPosition = Viewpoint.GlobalPosition + (neutral * direction * distance);
            Eyes.SetLookTarget(anchor);

            ulong start = Time.GetTicksMsec();
            while (Time.GetTicksMsec() - start < 8000)
            {
                await WaitForPhysicsFramesAsync(_tree, 10);
                if (Controller.GetTargetIntent().DesiredInfluence > 0.99f)
                {
                    break;
                }
            }

            Assert.True(
                Controller.GetTargetIntent().DesiredInfluence > 0.99f,
                "The anchor must engage the controller fully within the bounded settle window.");

            // Fixed settle window for the aim slew, centring delay and IK solve to finish.
            await WaitForPhysicsFramesAsync(_tree, 300);
        }

        /// <summary>The fold authoring domain must bind on this rig; a null frame invalidates every fold assertion.</summary>
        public void AssertFoldFrameBuilt()
        {
            Assert.NotNull(Controller.LastUpstreamSnapshot);
            Assert.True(Controller.LastUpstreamSnapshot.HasPivot, "The fixture authors a valid neck pivot.");
            Assert.Equal("frame_built-or-applied", LastFrameStatusForAssert());
        }

        private string LastFrameStatusForAssert() =>
            // The status field reflects the publication decision after evaluation; both a built frame
            // that abstained and one that applied prove the bind (the null-frame cases carry other texts).
            Controller.LastFoldFrame is null ? "not-built" : "frame_built-or-applied";

        /// <summary>
        /// Independent first-principles oracle: <c>p + R*(h − p)</c> with h, p, U derived from bone RESTS
        /// and authored marker offsets, and R from the published intent — entirely authored data, never
        /// the controller's snapshot or solved attachment reads.
        /// </summary>
        public void AssertRequestedMatchesRestConstruction()
        {
            IKTargetIntent intent = Controller.GetTargetIntent();
            Transform3D skeletonGlobal = Skeleton.GlobalTransform;
            Transform3D restHeadWorld = skeletonGlobal * Skeleton.GetBoneGlobalRest(_headBone);
            Basis viewpointLocal = Viewpoint.Transform.Basis.Orthonormalized();
            int neckBone = Skeleton.FindBone("Neck");
            Vector3 pivotPosition = (skeletonGlobal * Skeleton.GetBoneGlobalRest(neckBone) * _neckPivot.Transform).Origin;
            Basis upstreamFace = (restHeadWorld.Basis * viewpointLocal).Orthonormalized();
            Basis commandFace = (intent.WorldTransform.Basis * viewpointLocal).Orthonormalized();
            Basis rotation = commandFace * upstreamFace.Inverse();
            Vector3 expected = pivotPosition + (rotation * (restHeadWorld.Origin - pivotPosition));
            float delta = (Controller.LastRequestedAimTargetGlobal - expected).Length();
            Assert.True(
                delta < 1e-3f,
                $"The requested origin must equal the independent rest-geometry construction; delta {delta * 1000f:F3} mm.");
        }

        /// <summary>
        /// The published origin is exactly the family FK at the selected CONTINUOUS alpha over the
        /// CURRENT upstream poses: caps read straight from the NeckSpineIK modifier, poses from the
        /// native bones (the animation-owned upstream), FK through the shared pure geometry functions,
        /// fold sign resolved in the world frame. Wiring/transport oracle (N1-qualified: the FK helpers
        /// are shared, the authoring/pose reads are independent).
        /// </summary>
        public void AssertPublishedOnIndependentFoldGrid(double alpha)
        {
            SkeletonModifier3D modifier = Skeleton.GetNode<SkeletonModifier3D>("NeckSpineIK");
            string rootBoneName = modifier.Get("settings/0/root_bone_name").AsString();
            int jointCount = modifier.Get("settings/0/joint_count").AsInt32();

            _ = Skeleton.FindBone(rootBoneName);

            int[] bones = new int[jointCount];
            var rests = new Transform3D[jointCount];
            List<int> headToRoot = [_headBone];
            int walker = _headBone;
            while (Skeleton.GetBoneParent(walker) >= 0)
            {
                walker = Skeleton.GetBoneParent(walker);
                headToRoot.Add(walker);
            }

            for (int i = 0; i < jointCount; i++)
            {
                bones[i] = headToRoot[jointCount - 1 - i];
                rests[i] = Skeleton.GetBoneRest(bones[i]);
            }

            float[] caps = new float[jointCount - 1];
            for (int joint = 0; joint < jointCount - 1; joint++)
            {
                var limitation = modifier.Get($"settings/0/joints/{joint}/limitation").AsGodotObject() as Resource;
                Assert.NotNull(limitation);
                caps[joint] = limitation.Get("angle").AsSingle() * 0.5f;
            }

            Transform3D chainParentWorld = Skeleton.GlobalTransform
                * Skeleton.GetBoneGlobalPose(Skeleton.GetBoneParent(bones[0]));
            var poses = new Transform3D[jointCount];
            for (int i = 0; i < jointCount; i++)
            {
                poses[i] = Skeleton.GetBonePose(bones[i]);
            }

            float foldSign = DownwardFoldGeometry.ResolveForwardFoldSign(chainParentWorld, poses, caps, out _);
            float[] folds = new float[caps.Length];
            for (int joint = 0; joint < caps.Length; joint++)
            {
                folds[joint] = foldSign * (float)alpha * caps[joint];
            }

            Vector3 familyMember = DownwardFoldGeometry.ComposePosedFoldedChain(
                chainParentWorld, poses, folds, new Transform3D[jointCount]).Origin;
            float delta = (Controller.LastRequestedAimTargetGlobal - familyMember).Length();
            Assert.True(
                delta < 5e-4f,
                $"The published origin must equal the independently composed continuous family FK at alpha " +
                $"{alpha:F4}; delta {delta * 1000f:F3} mm.");
        }

        /// <summary>
        /// The published fold origin must separate from the geometrically excluded orbit target at this
        /// demand by more than the given margin (the radial bound excluded the orbit for this chain).
        /// </summary>
        public void AssertPublishedOffExcludedOrbit(float marginMillimetres)
        {
            DownwardFoldEvaluation evaluation = Assert.IsType<DownwardFoldEvaluation>(Controller.LastFoldEvaluation);
            float separation = (evaluation.PublishedOriginWorld - evaluation.OrbitTargetWorld).Length() * 1000f;
            Assert.True(
                separation > marginMillimetres,
                $"The published fold origin must sit off the excluded orbit target; separation {separation:F3} mm " +
                $"<= {marginMillimetres} mm.");
        }

        /// <summary>
        /// Coupled prediction/publication identity at the equilibrium command: the geometric solve's
        /// predicted eye origin equals the published fold head origin plus the commanded basis applied
        /// to the authored viewpoint offset (the character envelope is identity in this fixture, so the
        /// published world origin is its envelope coordinate).
        /// </summary>
        public void AssertCoupledPredictionMatchesPublishedPose()
        {
            GazeSaturationResult? solve = Controller.LastPolicySolve;
            Assert.NotNull(solve);
            OrientingAim aim = Controller.LastPublishedAim;
            Assert.True(
                Math.Abs(solve.YawRadians - aim.HorizontalRadians) <= DegreesToRadians(0.01d)
                    && Math.Abs(solve.PitchRadians - aim.VerticalRadians) <= DegreesToRadians(0.01d),
                $"The settled solve candidate ({solve.YawRadians * 180d / Math.PI:F4}/{solve.PitchRadians * 180d / Math.PI:F4}°) " +
                $"must equal the published command ({aim.HorizontalRadians * 180d / Math.PI:F4}/" +
                $"{aim.VerticalRadians * 180d / Math.PI:F4}°) for the equilibrium identity check.");

            Basis command = GeometricGazeSaturation.ComposeCommandBasis(aim.HorizontalRadians, aim.VerticalRadians);
            Vector3 expectedEye = Controller.LastRequestedAimTargetGlobal
                + (command * Controller.LastViewpointOffsetEnvelope);
            float delta = (solve.PredictedEyeOrigin - expectedEye).Length();
            Assert.True(
                delta < 1e-3f,
                $"Predicted eye origin must equal the published-pose eye origin; delta {delta * 1000f:F3} mm.");
        }

        private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

        public async ValueTask DisposeAsync()
        {
            Root.QueueFree();
            await WaitForPhysicsFramesAsync(_tree, 2);
        }

        private sealed partial class TestMind(ICharacter owner) : MindBase
        {
            protected override ICharacter ResolveOwningCharacter() => owner;
        }

        private sealed class TestCharacter(EyesBehaviour eyes) : ICharacter, IComponentProjectionNotifier
        {
            public string Id { get; set; } = "production-downward-fold";
            public IReadOnlyList<IComponent> Components { get; } = [eyes];
            public bool HasComponentProjection => true;
            public IReadOnlyList<VisualCue> VisualCues { get; } = [];
            public Transform3D GlobalTransform => Transform3D.Identity;
            public event Action? ComponentsRefreshed
            {
                add
                {
                }
                remove
                {
                }
            }
        }
    }
}
