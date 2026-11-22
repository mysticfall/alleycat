using System.Collections.Concurrent;
using AlleyCat.Control;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    private sealed class MarchSession(bool stopAfterFirst = false) : SomaInferenceWorker.ISession
    {
        internal ConcurrentQueue<Transform3D[][]> Received { get; } = new();

        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            Received.Enqueue(context);
            var rows = new Transform3D[60][];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = (Transform3D[])context[3].Clone();
                rows[i][0].Basis = new Basis(Vector3.Up, .53f);
                if (!stopAfterFirst || Received.Count == 1)
                {
                    rows[i][0].Origin += SomaMotionData.ModelToWorld.Inverse() * (Vector3.Forward * ((stopAfterFirst ? .0046f : .04f) * i));
                }
            }
            return rows;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A successor with zero planar demand must not repay blocked travel during its visual blend.</summary>
    [Fact]
    public async Task StationaryTurnSuccessor_DoesNotRepayBlockedPlanarMotion()
    {
        var session = new MarchSession(stopAfterFirst: true);
        await using ClosureFixture fixture = await ClosureFixture.CreateAsync(configure: motion => motion.SessionFactory = () => session);
        var wall = new StaticBody3D { Position = fixture.Player.GlobalPosition + new Vector3(0, .25f, -.35f) };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(3, 1, .1f) } });
        fixture.Root.AddChild(wall);
        WalkingExperimentDriver driver = fixture.Motion.ContinuousDriver!;
        Skeleton3D skin = fixture.Skeleton;
        int hip = skin.FindBone("Hips");
        Basis finalHip = Basis.Identity;
        float contextYawError = float.PositiveInfinity;
        void ObserveHip()
        {
            finalHip = skin.GlobalBasis * skin.GetBoneGlobalPose(hip).Basis;
        }
        skin.SkeletonUpdated += ObserveHip;
        long successor = 0;
        bool completed = false, atWall = false;
        float blocked = 0, maximum = 0;
        Vector3 anchor = default;
        driver.WindowSubmitted += (ticket, context, _) =>
        {
            if (successor != 0 || driver.RequestReason != "segment-end")
            {
                return;
            }
            successor = ticket;
            Basis submittedHip = SomaMotionData.ModelToWorld * context[3][0].Basis;
            contextYawError = submittedHip.GetRotationQuaternion().AngleTo(finalHip.GetRotationQuaternion());
            anchor = fixture.Player.GlobalPosition;
            atWall = fixture.Player.TestMove(fixture.Player.GlobalTransform, Vector3.Forward * .005f);
            blocked = (SomaMotionData.ModelToWorld * (driver.SuppliedPose[0].Origin - context[3][0].Origin)).Dot(Vector3.Forward);
        };
        driver.PlaybackCompleted += ticket => completed |= successor != 0 && ticket == successor;
        fixture.Right.TriggerActionVector2InputChanged("primary", Vector2.Right);
        // Two 60-sample segments at 30 Hz plus worker startup/consumption.
        int budget = Engine.PhysicsTicksPerSecond * 5;
        for (int i = 0; i < budget && !completed; i++)
        {
            await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
            if (successor != 0)
            {
                Vector3 delta = fixture.Player.GlobalPosition - anchor;
                maximum = Math.Max(maximum, new Vector2(delta.X, delta.Z).Length());
            }
        }
        Assert.True(completed && atWall && blocked >= .025f, $"completed={completed}, contact={atWall}, blocked={blocked} m");
        Assert.True(maximum <= .0001f, $"Zero-demand successor moved {maximum * 1000:F4} mm after {blocked * 1000:F3} mm blocked.");
        skin.SkeletonUpdated -= ObserveHip;
        Assert.InRange(contextYawError, 0, Mathf.DegToRad(.1f));
    }

    /// <summary>Verify the next actual worker context after wall contact, including a known final-target perturbation.</summary>
    [Fact]
    public async Task BlockedPlayback_NextWorkerInputContainsCollidedActorAndFinalHipTurn()
    {
        var session = new MarchSession();
        await using ClosureFixture fixture = await ClosureFixture.CreateAsync(configure: motion => motion.SessionFactory = () => session);
        var wall = new StaticBody3D { Position = fixture.Player.GlobalPosition + new Vector3(0, .25f, -.35f) };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(3, 1, .1f) } });
        fixture.Root.AddChild(wall);
        Skeleton3D target = fixture.Skeleton;
        Skeleton3D source = target.GetParent().GetParent<Skeleton3D>();
        int hip = target.FindBone("Hips");
        SkeletonModifier3D lastStage = target.GetChildren().OfType<SkeletonModifier3D>().Last();
        bool perturb = false;
        Basis beforeTurn = Basis.Identity;
        void Nudge()
        {
            if (!perturb)
            {
                return;
            }
            beforeTurn = target.GlobalBasis * target.GetBoneGlobalPose(hip).Basis;
            target.SetBonePoseRotation(hip, target.GetBonePoseRotation(hip) * new Quaternion(Vector3.Up, Mathf.DegToRad(12)));
        }
        lastStage.ModificationProcessed += Nudge;
        Vector3 actor = default, requested = default, hipOffset = default;
        Basis final = Basis.Identity;
        void Capture()
        {
            actor = fixture.Player.GlobalPosition;
            requested = SomaMotionData.ModelToWorld * fixture.Motion.SuppliedPose[0].Origin;
            Vector3 delta = target.GetBoneGlobalPose(hip).Origin - target.GetBoneGlobalRest(hip).Origin;
            hipOffset = target.GlobalBasis * (delta * (source.MotionScale / target.MotionScale));
            final = target.GlobalBasis * target.GetBoneGlobalPose(hip).Basis;
        }
        target.SkeletonUpdated += Capture;
        try
        {
            fixture.Left.TriggerActionVector2InputChanged("primary", new Vector2(0, 1));
            for (int i = 0; i < 90 && !(fixture.Player.TestMove(fixture.Player.GlobalTransform, Vector3.Forward * .005f)
                && requested.Z < actor.Z - .025f); i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
            }
            Assert.True(fixture.Player.TestMove(fixture.Player.GlobalTransform, Vector3.Forward * .005f));
            Assert.True(requested.Z < actor.Z - .025f);
            Assert.InRange(Math.Abs(fixture.Player.Rotation.Y - .53f), 0, .001f);
            perturb = true;
            await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 8);
            // Release submits the latest real history before replacing the displayed segment.
            Transform3D expected = new(final, actor + hipOffset);
            Basis unperturbed = beforeTurn;
            session.Received.Clear();
            fixture.Left.TriggerActionVector2InputChanged("primary", Vector2.Zero);
            for (int i = 0; i < 60 && session.Received.IsEmpty; i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
            }
            Assert.True(session.Received.TryDequeue(out Transform3D[][]? received));
            Assert.Equal(4, received.Length);
            foreach (Transform3D[] row in received)
            {
                Transform3D root = new Transform3D(SomaMotionData.ModelToWorld, Vector3.Zero) * row[0];
                Vector3 error = root.Origin - expected.Origin;
                Assert.InRange(new Vector2(error.X, error.Z).Length(), 0, .0001f);
                Assert.InRange(root.Basis.GetRotationQuaternion().AngleTo(expected.Basis.GetRotationQuaternion()), 0, Mathf.DegToRad(.1f));
            }
            Assert.InRange(unperturbed.GetRotationQuaternion().AngleTo(expected.Basis.GetRotationQuaternion()), Mathf.DegToRad(11.9f), Mathf.DegToRad(12.1f));
        }
        finally
        {
            lastStage.ModificationProcessed -= Nudge;
            target.SkeletonUpdated -= Capture;
        }
    }
}
