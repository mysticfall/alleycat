using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.IK;
using AlleyCat.Mind.Attention;
using AlleyCat.Sense;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.Attention;

/// <summary>
/// HEAD-3 (a)+(d): release/re-engage semantics and history independence for the orienting controller,
/// asserted at test level on the node-authored bootstrap-rest path (the skeleton-backed pipeline's
/// native roll restoration is covered by OrientingSkeletonLifecycleIntegrationTests). Release eases
/// toward the upstream (bootstrap rest) pose as influence decays; re-engagement starts from explicit
/// current state; no stale reconstructed neutral or revived episode command; identical targets under
/// different histories converge to identical commands.
/// </summary>
[Headless]
public sealed class OrientingReleaseReengageIntegrationTests
{
    private const double FrameDelta = 1d / 60d;

    /// <summary>
    /// Release from saturation eases the command toward the upstream rest pose while influence decays to
    /// exactly zero, leaving the pipeline's native animation in control.
    /// </summary>
    [Fact]
    public async Task ReleaseFromSaturation_EasesTowardUpstreamPose()
    {
        await using NodeRig rig = await NodeRig.CreateAsync();
        rig.AssignAnchor(localYawDegrees: 35f);
        _ = rig.Run(20);
        double commandedBeforeRelease = rig.Controller.LastPublishedAim.HorizontalRadians;
        Assert.True(commandedBeforeRelease > 0.1d, "Premise: saturation is commanding toward the anchor.");

        rig.ClearAnchor();
        _ = rig.Run(40);
        double midRelease = Math.Abs(rig.Controller.LastPublishedAim.HorizontalRadians - rig.RestCommandYaw);
        _ = rig.Run(120);
        double settled = Math.Abs(rig.Controller.LastPublishedAim.HorizontalRadians - rig.RestCommandYaw);
        IKTargetIntent intent = rig.Controller.GetTargetIntent();

        Assert.True(
            settled < midRelease,
            $"Release must ease toward the upstream rest pose, but the distance grew ({midRelease:F3} → {settled:F3} rad).");
        Assert.True(settled < 1e-3d, $"Release must converge to the rest command, {settled:F4} rad remain.");
        Assert.Equal(0f, intent.DesiredInfluence);
        // At zero influence the released command has converged to the rest pose: the pipeline leaves the
        // head to native animation (influence 0), and the published basis is the rest command itself.
        Assert.True(
            BasisAngleRadians(
                intent.WorldTransform.Basis,
                GeometricGazeSaturation.ComposeCommandBasis(rig.RestCommandYaw, 0d)) < 1e-3d,
            "The zero-influence intent must be the rest-pose command.");
    }

    /// <summary>
    /// Clearing mid-transit releases from wherever the command is — no snap, and the same easing target.
    /// </summary>
    [Fact]
    public async Task ReleaseDuringTransit_StartsFromCurrentState()
    {
        await using NodeRig rig = await NodeRig.CreateAsync();
        rig.AssignAnchor(localYawDegrees: 30f);
        _ = rig.Run(9);
        double midTransitCommand = rig.Controller.LastPublishedAim.HorizontalRadians;
        Assert.True(
            midTransitCommand is > 0.55d and < 0.78d,
            $"Premise: cleared mid-transit, partway between the rest command (0.524 rad) and the "
            + $"saturation target; commanded {midTransitCommand:F3} rad.");

        rig.ClearAnchor();
        OrientingAim firstReleaseFrame = rig.Run(1);
        Assert.True(
            Math.Abs(firstReleaseFrame.HorizontalRadians - midTransitCommand) < 0.05d,
            $"Release must start from the current command ({midTransitCommand:F3} rad), not snap; first "
            + $"release frame commanded {firstReleaseFrame.HorizontalRadians:F3} rad.");

        _ = rig.Run(150);
        Assert.True(
            Math.Abs(rig.Controller.LastPublishedAim.HorizontalRadians - rig.RestCommandYaw) < 1e-3d,
            "Mid-transit release must converge to the rest command.");
    }

    /// <summary>
    /// Clear then re-engage the SAME target: the re-engagement's brief-glance phase holds the explicit
    /// current (post-release) state — never a stale episode command — and the convergence reaches the
    /// same command as a fresh engagement (history independence for this pair).
    /// </summary>
    [Fact]
    public async Task ClearThenReengageSameTarget_HoldsCurrentStateAndConvergesIdentically()
    {
        await using NodeRig rig = await NodeRig.CreateAsync();
        double fresh = rig.ConvergeCommand(localYawDegrees: 10f, frames: 200);

        rig.AssignAnchor(localYawDegrees: 10f);
        _ = rig.Run(120);
        rig.ClearAnchor();
        _ = rig.Run(150);
        rig.AssignAnchor(localYawDegrees: 10f);
        OrientingAim glancePhase = rig.Run(8);
        Assert.True(
            Math.Abs(glancePhase.HorizontalRadians - rig.RestCommandYaw) < 1e-4d,
            $"The re-engagement glance must hold the post-release current state (≈ rest), not a stale "
            + $"episode command; commanded {glancePhase.HorizontalRadians:F3} rad.");

        double rengaged = rig.ConvergedCommandAfterExisting(frames: 200);
        Assert.True(
            Math.Abs(rengaged - fresh) < 1e-4d,
            $"Same target, different history must converge identically: fresh {fresh:F5} rad vs "
            + $"re-engaged {rengaged:F5} rad.");
        string? evidence = System.Environment.GetEnvironmentVariable("ALLEYCAT_HEAD3_EVIDENCE");
        if (evidence is not null)
        {
            File.WriteAllText(Path.Combine(evidence, "node-reengage.txt"),
                $"fresh rad={fresh:R}; re-engaged rad={rengaged:R}; brief hold rad={glancePhase.HorizontalRadians:R}; upstream rest rad={rig.RestCommandYaw:R}");
        }
    }

    /// <summary>
    /// A glance (in-cone different anchor) followed by clear releases toward the upstream pose — never
    /// toward the glance anchor and never toward the pre-glance held command.
    /// </summary>
    [Fact]
    public async Task GlanceThenRelease_EasesToUpstreamNotGlanceOrHeld()
    {
        await using NodeRig rig = await NodeRig.CreateAsync();
        double held = rig.ConvergeCommand(localYawDegrees: 25f, frames: 200);

        // A glance 5° beyond the held command: in-cone relative to the solved head, so the captured
        // command holds — never chasing the glance anchor.
        rig.AssignAnchor(localYawDegrees: 30f);
        OrientingAim duringGlance = rig.Run(8);
        Assert.True(
            Math.Abs(duringGlance.HorizontalRadians - held) < 1e-4d,
            $"During the glance the command must keep easing toward the pre-glance held command "
            + $"({held:F3} rad), not chase the glance anchor (commanded {duringGlance.HorizontalRadians:F3} rad).");
        rig.ClearAnchor();
        _ = rig.Run(150);
        double settled = rig.Controller.LastPublishedAim.HorizontalRadians;

        Assert.True(
            Math.Abs(settled - rig.RestCommandYaw) < 1e-3d,
            $"Release after a glance must converge to the upstream rest pose, got {settled:F4} rad.");
    }

    /// <summary>
    /// History independence (HEAD-3 d): the same target under four histories — fresh engagement, after a
    /// full release, after a glance at a different target, and after a saturation park (centring delay
    /// elapsed on the same anchor) — converges to identical commands once transient easing completes.
    /// </summary>
    [Fact]
    public async Task SameTarget_DifferentHistories_ConvergeIdentically()
    {
        await using NodeRig rig = await NodeRig.CreateAsync();

        double fresh = rig.ConvergeCommand(localYawDegrees: 18f, frames: 200);

        // History 2: after a full release from a different target.
        rig.AssignAnchor(localYawDegrees: -25f);
        _ = rig.Run(120);
        rig.ClearAnchor();
        _ = rig.Run(150);
        double afterRelease = rig.ConvergeCommand(localYawDegrees: 18f, frames: 220);
        Assert.True(Math.Abs(afterRelease - fresh) < 1e-4d, $"after-release {afterRelease:F5} vs fresh {fresh:F5}");

        // History 3: after a glance at a different target.
        rig.AssignAnchor(localYawDegrees: 5f);
        _ = rig.Run(10);
        double afterGlance = rig.ConvergeCommand(localYawDegrees: 18f, frames: 220);
        Assert.True(Math.Abs(afterGlance - fresh) < 1e-4d, $"after-glance {afterGlance:F5} vs fresh {fresh:F5}");

        // History 4: after a saturation park on the same anchor (brief engagement first, then sustained).
        rig.ClearAnchor();
        _ = rig.Run(150);
        rig.AssignAnchor(localYawDegrees: 18f);
        _ = rig.Run(12);
        double afterPark = rig.ConvergedCommandAfterExisting(frames: 220);
        Assert.True(Math.Abs(afterPark - fresh) < 1e-4d, $"after-park {afterPark:F5} vs fresh {fresh:F5}");

        Assert.True(
            fresh is > 0.75d and < 0.95d,
            $"Sanity: the converged command for an 18°-from-rest anchor should sit near rest+18° "
            + $"({Mathf.RadToDeg((float)((18d * Math.PI / 180d) + (Math.PI / 6d))):F1}°), "
            + $"got {Mathf.RadToDeg((float)fresh):F2}°.");
    }

    /// <summary>
    /// At zero influence the controller releases head ownership to native animation — the pipeline
    /// condition under which native animation (including authored roll) owns the head. The upstream
    /// release command itself is roll-free by construction; the authored rolled rest basis is never
    /// absorbed into any command (this rig has no solver to restore, so the bone-level restoration is
    /// covered by OrientingSkeletonLifecycleIntegrationTests).
    /// </summary>
    [Fact]
    public async Task ZeroInfluence_IdleIntentAndNoCommandAbsorption()
    {
        await using NodeRig rig = await NodeRig.CreateAsync(rolledDegrees: 8f);
        rig.AssignAnchor(localYawDegrees: 20f);
        _ = rig.Run(200);
        rig.ClearAnchor();
        _ = rig.Run(160);

        IKTargetIntent intent = rig.Controller.GetTargetIntent();
        Assert.Equal(0f, intent.DesiredInfluence);
        Assert.True(
            BasisAngleRadians(
                intent.WorldTransform.Basis,
                GeometricGazeSaturation.ComposeCommandBasis(rig.RestCommandYaw, 0d)) < 1e-3d,
            "At zero influence the published basis must be the released rest command.");

        Basis releaseCommand = intent.WorldTransform.Basis;
        float roll = MeasureRoll(releaseCommand);
        Assert.True(
            Mathf.Abs(Mathf.RadToDeg(roll)) < 0.1f,
            $"The release command must stay roll-free ({Mathf.RadToDeg(roll):F2}° measured); authored rest roll must never be absorbed.");
    }

    private static double BasisAngleRadians(Basis a, Basis b)
    {
        Basis relative = a.Orthonormalized().Inverse() * b.Orthonormalized();
        float trace = relative.X.X + relative.Y.Y + relative.Z.Z;
        return Math.Acos(Math.Clamp((trace - 1d) / 2d, -1d, 1d));
    }

    private static float MeasureRoll(Basis basis)
    {
        Vector3 localUp = basis.Transposed() * Vector3.Up;
        return Mathf.Atan2(localUp.X, localUp.Y);
    }

    /// <summary>
    /// Node-authored rig (no skeleton by construction — the documented split criterion for the bootstrap
    /// path): a head node with an authored yaw (and optional roll), a viewpoint marker, a movable
    /// character backing node, and a followed-head solve model.
    /// </summary>
    private sealed class NodeRig : IAsyncDisposable
    {
        private readonly Node3D _root;
        private readonly Node3D _head;
        private readonly Marker3D _viewpoint;
        private readonly TestVision _vision;
        private readonly Dictionary<float, StaticVisualCue> _anchors = [];
        public OrientingController Controller
        {
            get;
        }

        public double RestCommandYaw
        {
            get;
        }

        private NodeRig(
            Node3D root,
            Node3D head,
            Marker3D viewpoint,
            TestVision vision,
            OrientingController controller,
            double restCommandYaw)
        {
            _root = root;
            _head = head;
            _viewpoint = viewpoint;
            _vision = vision;
            Controller = controller;
            RestCommandYaw = restCommandYaw;
        }

        public static async Task<NodeRig> CreateAsync(float rolledDegrees = 0f)
        {
            SceneTree sceneTree = GetSceneTree();
            await WaitForFramesAsync(sceneTree, 1);
            Node3D root = new()
            {
                Name = "ReleaseRig"
            };
            sceneTree.Root.AddChild(root);
            Node3D characterNode = new()
            {
                Name = "Character"
            };
            root.AddChild(characterNode);
            Node3D head = new()
            {
                Name = "Head",
                Basis = Basis.FromEuler(new Vector3(0f, (float)(Math.PI / 6d), Mathf.DegToRad(rolledDegrees))),
            };
            characterNode.AddChild(head);
            Marker3D viewpoint = new()
            {
                Name = "Viewpoint",
                Transform = new Transform3D(Basis.Identity, new Vector3(0f, 0.1f, 0f)),
            };
            head.AddChild(viewpoint);
            StaticVisualCue anchor = new()
            {
                Name = "Anchor"
            };
            characterNode.AddChild(anchor);
            TestVision vision = new();
            TestMind mind = new(new NodeBackedCharacter(characterNode, vision));
            characterNode.AddChild(mind);
            OrientingController controller = new()
            {
                Name = "OrientingController",
                Viewpoint = viewpoint,
                CentringDelaySeconds = 0.2f,
                ReactionDelaySeconds = 0.05f,
            };
            mind.AddChild(controller);
            await WaitForFramesAsync(sceneTree, 1);
            controller._Process(FrameDelta);
            Vector3 restForward = -head.GlobalBasis.Z;
            double restYaw = Math.Atan2(-restForward.X, -restForward.Z);
            return new NodeRig(root, head, viewpoint, vision, controller, restYaw)
            {
                Anchor = anchor,
            };
        }

        public StaticVisualCue Anchor
        {
            get;
            set;
        } = null!;

        public void AssignAnchor(float localYawDegrees)
        {
            if (!_anchors.TryGetValue(localYawDegrees, out StaticVisualCue? anchor))
            {
                anchor = new StaticVisualCue();
                _head.GetParent().AddChild(anchor);
                _anchors.Add(localYawDegrees, anchor);
            }

            Anchor = anchor;
            // Rest-relative placement: the anchor direction is composed through the bootstrap rest pose,
            // so the angle parameter is the anchor's face-local offset from rest — identical
            // character-relative geometry across histories regardless of the head's current pose.
            double radians = localYawDegrees * Math.PI / 180d;
            Basis rest = GeometricGazeSaturation.ComposeCommandBasis(RestCommandYaw, 0d);
            Anchor.GlobalPosition = _viewpoint.GlobalPosition + (rest * new Vector3(
                -(float)Math.Sin(radians),
                0f,
                -(float)Math.Cos(radians)) * 1.5f);
            _vision.LookTarget = Anchor;
        }

        public void ClearAnchor() => _vision.LookTarget = null;

        /// <summary>Runs frames, following the commanded basis onto the head while influence is engaged.</summary>
        public OrientingAim Run(int frames)
        {
            OrientingAim aim = default;
            for (int frame = 0; frame < frames; frame++)
            {
                Controller._Process(FrameDelta);
                aim = Controller.LastPublishedAim;
                if (Controller.GetTargetIntent().DesiredInfluence > 0f)
                {
                    _head.GlobalBasis = Controller.GetTargetIntent().WorldTransform.Basis;
                }
            }

            return aim;
        }

        /// <summary>Assigns the anchor fresh and runs until the command settles.</summary>
        public double ConvergeCommand(float localYawDegrees, int frames)
        {
            AssignAnchor(localYawDegrees);
            return ConvergedCommandAfterExisting(frames);
        }

        /// <summary>Runs the already-assigned anchor until the command settles and returns its yaw.</summary>
        public double ConvergedCommandAfterExisting(int frames)
        {
            _ = Run(frames);
            double yaw = Controller.LastPublishedAim.HorizontalRadians;
            OrientingAim extra = Run(30);
            Assert.True(
                Math.Abs(extra.HorizontalRadians - yaw) < 1e-5d,
                "The command must be settled (transient easing complete) before history comparison.");
            return extra.HorizontalRadians;
        }

        public async ValueTask DisposeAsync()
        {
            _root.QueueFree();
            await WaitForFramesAsync(GetSceneTree(), 2);
        }
    }

    private sealed class TestVision : IVision
    {
        public VisualCue? LookTarget
        {
            get;
            set;
        }

        public IReadOnlyList<Type> PerceptTypes { get; } = [];

        public event Action<IPercept>? Perceived
        {
            add
            {
            }
            remove
            {
            }
        }

        public void SetLookTarget(VisualCue? target) => LookTarget = target;

        public void ClearLookTarget() => LookTarget = null;
    }

    private sealed partial class TestMind(ICharacter owner) : MindBase
    {
        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    /// <summary>
    /// Character whose global transform is a real scene node, so tests can move/rotate the character root
    /// between evaluations (the envelope-frame source).
    /// </summary>
    private sealed class NodeBackedCharacter(Node3D node, IVision vision) : ICharacter, IComponentProjectionNotifier
    {
        public string Id { get; set; } = "release-rig";

        public IReadOnlyList<IComponent> Components { get; } = [vision];

        public bool HasComponentProjection => true;

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform => node.GlobalTransform;

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
