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
/// Runtime proof of the non-recursive upstream aimed-origin construction on the real female fixture
/// skeleton (AI-009 TR 21–22, HEAD-1): while the solved head has turned away from rest, the controller's
/// requested target must equal the first-principles construction from BONE RESTS and authored offsets —
/// the superseded recursive construction rode the solved head and would sit a lever-arm rotation away
/// from that rest construction. Also asserts the snapshot is skeleton-backed with a valid pivot, the
/// solved head really has moved (non-vacuity), the evaluation sequence advances, and the requested
/// target is constant across settled frames.
/// </summary>
/// <remarks>
/// Headless-safe: every assertion reads controller/bone state, not renderer output. The fixture's
/// upstream animation is the constant rest, so the upstream snapshot equals the bone rests throughout —
/// which is exactly what makes the rest construction an independent oracle here.
/// </remarks>
[Headless]
public sealed class OrientingUpstreamOriginIntegrationTests
{
    private const int SettleChunkFrames = 15;
    private const int AssertionWindowFrames = 30;
    private const float RestConstructionToleranceMetres = 1e-3f;
    private const float SettledConstancyMetres = 1e-3f;

    /// <summary>
    /// A converged lateral look keeps the requested aimed origin at the independent rest-geometry
    /// construction while the solved head is away from rest.
    /// </summary>
    [Fact]
    public async Task ConvergedLateralLook_RequestsRestGeometryTargetWhileSolvedHeadTurns()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 1);

        var root = (Node3D)ResourceLoader.Load<GDScript>(
            "res://tests/ai009_head_orientation/isolated_female_tracking.gd").New();
        Skeleton3D skeleton = root.GetNode<Skeleton3D>("Female/GeneralSkeleton");
        Marker3D viewpoint = root.GetNode<Marker3D>("Female/GeneralSkeleton/Head/Viewpoint");
        Node3D neckPivot = root.GetNode<Node3D>("Female/GeneralSkeleton/Neck/NeckPivot");

        DynamicPhysicalRig physical = new()
        {
            Name = "DynamicPhysicalRig",
            TargetSkeleton = skeleton,
            Enabled = false,
        };
        skeleton.AddChild(physical);
        EyesBehaviour eyes = new()
        {
            Name = "Eyes",
            EyeOrigin = viewpoint,
            LeftEye = skeleton.GetNode<Node3D>("Head/Eyes/LeftEye"),
            RightEye = skeleton.GetNode<Node3D>("Head/Eyes/RightEye"),
            SaccadeAmplitude = 0,
            SaccadeInterval = 0,
        };
        root.AddChild(eyes);
        TestMind mind = new(new TestCharacter(eyes))
        {
            Name = "Mind"
        };
        OrientingController controller = new()
        {
            Name = "OrientingController",
            Viewpoint = viewpoint,
            NeckPivot = neckPivot,
        };
        mind.AddChild(controller);
        root.AddChild(mind);
        CharacterIK ik = new()
        {
            Name = "CharacterIK",
            Viewpoint = viewpoint,
            PhysicalRig = physical,
            HeadTargetIntentProvider = controller,
            HeadIKTarget = root.GetNode<CharacterBody3D>("IKTargets/Head"),
            HeadIKSolveTarget = root.GetNode<Marker3D>("IKTargets/HeadSolve"),
            RightHandIKTarget = root.GetNode<AnimatableBody3D>("IKTargets/RightHand"),
            LeftHandIKTarget = root.GetNode<AnimatableBody3D>("IKTargets/LeftHand"),
            HeadModifierGroup =
            [
                skeleton.GetNode<SkeletonModifier3D>("NeckSpineIK"),
                skeleton.GetNode<SkeletonModifier3D>("HeadCopyRotation"),
                skeleton.GetNode<SkeletonModifier3D>("NeckTwistDisperser"),
            ],
        };
        root.AddChild(ik);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 3);
            StaticVisualCue anchor = new()
            {
                Name = "Anchor"
            };
            root.AddChild(anchor);
            Basis neutral = viewpoint.GlobalBasis;
            float yaw = Mathf.DegToRad(30f);
            anchor.GlobalPosition = viewpoint.GlobalPosition
                + (neutral * new Vector3(-Mathf.Sin(yaw), 0f, -Mathf.Cos(yaw)) * 1.2f);
            eyes.SetLookTarget(anchor);

            // Let the policy ramp influence, slew the aim and the IK solve the turn.
            ulong startTicks = Time.GetTicksMsec();
            while (Time.GetTicksMsec() - startTicks < 8000)
            {
                await WaitForFramesAsync(sceneTree, SettleChunkFrames);
                if (controller.GetTargetIntent().DesiredInfluence > 0.99f
                    && controller.LastUpstreamSnapshot is { HasPivot: true })
                {
                    break;
                }
            }

            // The settle loop exits as soon as influence engages; give the aim slew, centring delay and
            // IK solve a fixed window to finish before asserting on the converged state.
            await WaitForFramesAsync(sceneTree, 300);

            List<(Vector3 Requested, Vector3 Expected, ulong Sequence)> window = [];
            for (int frame = 0; frame < AssertionWindowFrames; frame++)
            {
                await WaitForFramesAsync(sceneTree, 1);
                IKTargetIntent intent = controller.GetTargetIntent();
                OrientingUpstreamSnapshot? snapshot = controller.LastUpstreamSnapshot;
                Assert.NotNull(snapshot);
                Assert.True(snapshot.HasPivot, "The fixture authors a valid neck pivot; the snapshot must carry it.");
                Assert.True(
                    intent.DesiredInfluence > 0.99f,
                    $"Influence must stay engaged through the assertion window, found {intent.DesiredInfluence:F3}.");

                window.Add((controller.LastRequestedAimTargetGlobal, ComputeRestConstruction(skeleton, viewpoint, neckPivot, intent), snapshot.EvaluationSequence));
            }

            // Non-vacuity: the solved head has turned away from the upstream rest geometry — its face
            // direction differs from rest and its bone origin has displaced.
            Node3D headAttachment = root.GetNode<Node3D>("Female/GeneralSkeleton/Head");
            Vector3 restHeadOrigin = RestHeadOrigin(skeleton);
            Basis restFace = RestFaceBasis(skeleton, viewpoint);
            float solvedDeparture = (headAttachment.GlobalPosition - restHeadOrigin).Length();
            float solvedFaceTurnDegrees = Mathf.RadToDeg(
                (-headAttachment.GlobalBasis.Z).AngleTo(-restFace.Z));
            Assert.True(
                solvedFaceTurnDegrees > 5f && solvedDeparture > 0.01f,
                $"The solved head must sit away from rest for this discriminator to be meaningful, but only "
                + $"turned {solvedFaceTurnDegrees:F1}° and moved {solvedDeparture * 1000f:F1} mm.");


            foreach ((Vector3 requested, Vector3 expected, _) in window)
            {
                Assert.True(
                    (requested - expected).Length() < RestConstructionToleranceMetres,
                    $"The requested aimed origin must equal the independent rest-geometry construction "
                    + $"{expected}, but found {requested} (delta {(requested - expected).Length() * 1000f:F2} mm).");
            }

            Vector3 first = window[0].Requested;
            foreach ((Vector3 requested, _, ulong sequence) in window)
            {
                Assert.True(
                    (requested - first).Length() < SettledConstancyMetres,
                    $"The requested target must stay constant across settled frames, but moved "
                    + $"{(requested - first).Length() * 1000f:F2} mm.");
                Assert.True(sequence > 0, "The snapshot evaluation sequence must advance.");
            }

            Assert.True(window[^1].Sequence > window[0].Sequence, "The evaluation sequence must advance per frame.");
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// Independent first-principles oracle: <c>p + R*(h − p)</c> with h, p, U derived from bone RESTS
    /// and authored marker offsets, and R from the published command composed through the authored face
    /// offset — derived entirely from authored data plus the published intent, not from the controller's
    /// snapshot or any solved attachment read.
    /// </summary>
    private static Vector3 ComputeRestConstruction(Skeleton3D skeleton, Marker3D viewpoint, Node3D neckPivot, IKTargetIntent intent)
    {
        int headBone = skeleton.FindBone("Head");
        int neckBone = skeleton.FindBone("Neck");
        Transform3D skeletonGlobal = skeleton.GlobalTransform;
        Transform3D restHeadWorld = skeletonGlobal * skeleton.GetBoneGlobalRest(headBone);
        Basis viewpointLocal = viewpoint.Transform.Basis.Orthonormalized();
        Vector3 headOrigin = restHeadWorld.Origin;
        Vector3 pivotPosition = (skeletonGlobal * skeleton.GetBoneGlobalRest(neckBone) * neckPivot.Transform).Origin;
        Basis upstreamFace = (restHeadWorld.Basis * viewpointLocal).Orthonormalized();
        Basis commandFace = (intent.WorldTransform.Basis * viewpointLocal).Orthonormalized();
        Basis rotation = commandFace * upstreamFace.Inverse();
        return pivotPosition + (rotation * (headOrigin - pivotPosition));
    }

    private static Vector3 RestHeadOrigin(Skeleton3D skeleton)
        => (skeleton.GlobalTransform * skeleton.GetBoneGlobalRest(skeleton.FindBone("Head"))).Origin;

    private static Basis RestFaceBasis(Skeleton3D skeleton, Marker3D viewpoint)
        => ((skeleton.GlobalTransform * skeleton.GetBoneGlobalRest(skeleton.FindBone("Head"))).Basis
            * viewpoint.Transform.Basis.Orthonormalized()).Orthonormalized();

    private sealed partial class TestMind(ICharacter owner) : MindBase
    {
        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    private sealed class TestCharacter(EyesBehaviour eyes) : ICharacter, IComponentProjectionNotifier
    {
        public string Id { get; set; } = "orienting-upstream-origin";
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
