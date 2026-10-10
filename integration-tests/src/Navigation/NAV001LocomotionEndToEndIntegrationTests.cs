using System.Text;
using AlleyCat.Control.Locomotion;
using AlleyCat.IK;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Navigation.NavigationRigTestSupport;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>Small female-rig acceptance spine; no exact placeholder clip trajectories or male retarget assumptions.</summary>
public sealed class NAV001LocomotionEndToEndIntegrationTests
{
    // NAV-001 TR28 deliberately sets no numeric movement caps. These loose gross-pop envelopes are engineering
    // guards, not a claim of aesthetic seam quality: 9 m/s actor and 15 m/s world bone motion at normal 60 Hz.
    // Replace them through a reviewed content-independent acceptance decision, never from an observed failing trace.
    private const float MaximumActorStepMetres = 0.15f;
    private const float MaximumBoneStepMetres = 0.25f;
    private const float ArrivalToleranceMetres = 0.01f;
    private const float ArrivalYawToleranceDegrees = 1f;
    private const float PlantedSlipToleranceMetres = 0.005f;
    private const float SettledDriftToleranceMetres = 0.001f;
    private const int RequestTimeoutTicks = 1200;
    private static readonly Vector3 _destination = new(1f, 0f, -3f);
    private static readonly float _yaw = Mathf.Atan2(-_destination.X, -_destination.Z);

    /// <summary>One planned turning walk arrives and settles without gross actor or skeleton pops, planting disabled.</summary>
    [Fact]
    public Task WalkToDestination_ArrivesAndSettles_WithSmoothStitching() => WalkAsync(false);

    /// <summary>Same walk with planting enabled: every observed stance holds its world anchor; NAV owns displacement.</summary>
    [Fact]
    public Task WalkToDestination_PlantedFeetHoldStance_NoFootSliding() => WalkAsync(true);

    private static async Task WalkAsync(bool planting)
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(NPCNavigationRig.Female, useReducedRenderSize: true);
        Assert.Equal(60, Engine.PhysicsTicksPerSecond);
        Assert.Equal(1d, Engine.TimeScale);
        CharacterIK ik = fixture.Actor.GetNode<CharacterIK>("CharacterIK");
        if (!planting)
        {
            ik.LeftFootTargetIntentProvider = null;
            ik.RightFootTargetIntentProvider = null;
        }
        else
        {
            _ = Assert.IsType<FootPlantingTargetProvider>(ik.LeftFootTargetIntentProvider);
            _ = Assert.IsType<FootPlantingTargetProvider>(ik.RightFootTargetIntentProvider);
        }

        await fixture.StepAsync(WarmUpPhysicsTicks);
        await fixture.SettleAsync();
        using var capture = new PoseCapture(fixture);
        NavigationRequestIdentity request = IssueRequest(fixture.Navigation, new ArrivalRequest(_destination, _yaw), "locomotion-spine", 0);
        ulong firstTick = Engine.GetPhysicsFrames() + 1;
        NavigationTerminalOutcomeRecord? outcome = null;
        for (int tick = 0; tick < RequestTimeoutTicks && outcome is null; tick++)
        {
            await fixture.StepAsync(1);
            outcome = FindOutcome(fixture.Navigation, request);
        }
        await fixture.StepAsync(60);
        await fixture.SettleAsync();
        capture.Stop();
        capture.AssertHealthy();
        PoseSample[] samples = [.. capture.Samples.Where(pair => pair.Key >= firstTick).OrderBy(pair => pair.Key).Select(pair => pair.Value)];
        string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(
            $"res://temp/suite-redesign-v13/spine/{(planting ? "planting" : "smoothness")}"));
        var csv = new StringBuilder("tick,actor_x,actor_y,actor_z,hips_x,hips_y,hips_z,left_x,left_y,left_z,right_x,right_y,right_z,planted_left,planted_right\n");
        foreach (PoseSample sample in samples)
        {
            _ = csv.AppendLine(FormattableString.Invariant($"{sample.Tick},{sample.Actor.X:R},{sample.Actor.Y:R},{sample.Actor.Z:R},{sample.Hips.X:R},{sample.Hips.Y:R},{sample.Hips.Z:R},{sample.Left.X:R},{sample.Left.Y:R},{sample.Left.Z:R},{sample.Right.X:R},{sample.Right.Y:R},{sample.Right.Z:R},{sample.Planted.Left},{sample.Planted.Right}"));
        }
        File.WriteAllText(Path.Combine(directory, "ticks.csv"), csv.ToString());
        File.WriteAllText(Path.Combine(directory, "outcome.txt"), outcome?.ToString() ?? "No terminal outcome.");
        Assert.NotEmpty(samples);
        Assert.Equal(firstTick, samples[0].Tick);
        for (int index = 1; index < samples.Length; index++)
        {
            Assert.Equal(samples[index - 1].Tick + 1, samples[index].Tick);
        }
        Assert.True(outcome.HasValue, "The turning walk timed out without a terminal outcome.");
        NavigationTerminalOutcomeRecord terminal = outcome!.Value;
        Assert.Equal(NavigationTerminalOutcome.Completed, terminal.Outcome);
        Assert.NotEmpty(fixture.Navigation.MovementSamples);
        Assert.True(samples.Any(sample => sample.Actor.Length() > 2f), "The request did not exercise multi-metre locomotion.");
        Assert.InRange(terminal.Error.PlanarDistance, 0f, ArrivalToleranceMetres);
        Assert.InRange(Mathf.Abs(Mathf.RadToDeg(terminal.Error.YawRadians)), 0f, ArrivalYawToleranceDegrees);
        Assert.InRange(new Vector2(fixture.Actor.GlobalPosition.X, fixture.Actor.GlobalPosition.Z).DistanceTo(new Vector2(_destination.X, _destination.Z)), 0f, ArrivalToleranceMetres);
        AnimationNodeStateMachinePlayback playback = fixture.Tree.Get("parameters/States/playback").As<AnimationNodeStateMachinePlayback>();
        Assert.Equal(new StringName("NPCIdle"), playback.GetCurrentNode());
        Assert.True(playback.GetFadingFromNode().IsEmpty, "The completed walk did not settle to unfaded Idle.");
        PoseSample[] settled = [.. samples.Where(sample => sample.Tick >= terminal.TerminalPhysicsTick)];
        Assert.True(settled.Length >= 60, "Missing the full post-completion observation window.");
        Assert.All(settled, sample => Assert.InRange(sample.Actor.DistanceTo(settled[0].Actor), 0f, SettledDriftToleranceMetres));

        if (planting)
        {
            (int leftRuns, float leftSlip) = StanceSlip(samples, true);
            (int rightRuns, float rightSlip) = StanceSlip(samples, false);
            File.WriteAllText(Path.Combine(directory, "stance.txt"), FormattableString.Invariant($"left_runs={leftRuns}\nright_runs={rightRuns}\nleft_slip_m={leftSlip:R}\nright_slip_m={rightSlip:R}\n"));
            Assert.True(leftRuns >= 2 && rightRuns >= 2, "Both feet must exercise multiple multi-tick stances.");
            Assert.InRange(leftSlip, 0f, PlantedSlipToleranceMetres);
            Assert.InRange(rightSlip, 0f, PlantedSlipToleranceMetres);
            // Outcome-level ownership: the independently observed settled actor agrees with NAV's last realised
            // movement and terminal result. No enabled/disabled per-tick parity replay or provider-selected oracle.
            NPCNavigationMovementSample last = fixture.Navigation.MovementSamples[^1];
            Vector2 observed = new(fixture.Actor.GlobalPosition.X, fixture.Actor.GlobalPosition.Z);
            Assert.InRange(observed.DistanceTo(last.RealisedPosition), 0f, SettledDriftToleranceMetres);
            Assert.InRange(observed.DistanceTo(new Vector2(terminal.FinalTransform.X, terminal.FinalTransform.Z)), 0f, SettledDriftToleranceMetres);
        }
        else
        {
            float actorMaximum = 0f;
            float boneMaximum = 0f;
            for (int index = 1; index < samples.Length; index++)
            {
                PoseSample previous = samples[index - 1];
                PoseSample current = samples[index];
                actorMaximum = Math.Max(actorMaximum, current.Actor.DistanceTo(previous.Actor));
                boneMaximum = Math.Max(boneMaximum, Math.Max(current.Hips.DistanceTo(previous.Hips),
                    Math.Max(current.Left.DistanceTo(previous.Left), current.Right.DistanceTo(previous.Right))));
            }
            File.WriteAllText(Path.Combine(directory, "smoothness.txt"), FormattableString.Invariant($"max_actor_step_m={actorMaximum:R}\nmax_bone_step_m={boneMaximum:R}\n"));
            Assert.InRange(actorMaximum, 0f, MaximumActorStepMetres);
            Assert.InRange(boneMaximum, 0f, MaximumBoneStepMetres);
        }
    }

    private static (int Runs, float Maximum) StanceSlip(PoseSample[] samples, bool left)
    {
        Vector3? anchor = null;
        float maximum = 0f;
        int runTicks = 0;
        int runs = 0;
        foreach (PoseSample sample in samples)
        {
            bool planted = left ? sample.Planted.Left : sample.Planted.Right;
            Vector3 ball = left ? sample.Left : sample.Right;
            if (!planted)
            {
                if (runTicks >= 3)
                {
                    runs++;
                }
                anchor = null;
                runTicks = 0;
                continue;
            }
            anchor ??= ball;
            runTicks++;
            maximum = Math.Max(maximum, new Vector2(ball.X, ball.Z).DistanceTo(new Vector2(anchor.Value.X, anchor.Value.Z)));
        }
        return (runs + (runTicks >= 3 ? 1 : 0), maximum);
    }

    private readonly record struct PoseSample(ulong Tick, Transform3D ActorTransform, Vector3 Hips, Vector3 Left, Vector3 Right, PlantedFeetState Planted)
    {
        public Vector3 Actor => ActorTransform.Origin;
    }

    private sealed class PoseCapture : IDisposable
    {
        private readonly NPCNavigationRigFixture _fixture;
        private readonly int _hips;
        private readonly int _left;
        private readonly int _right;
        private readonly Vector3 _leftBall;
        private readonly Vector3 _rightBall;
        private Exception? _failure;
        private bool _stopped;
        public Dictionary<ulong, PoseSample> Samples { get; } = [];

        public PoseCapture(NPCNavigationRigFixture fixture)
        {
            _fixture = fixture;
            _hips = fixture.Skeleton.FindBone("Hips");
            _left = fixture.Skeleton.FindBone("LeftFoot");
            _right = fixture.Skeleton.FindBone("RightFoot");
            int leftToes = fixture.Skeleton.FindBone("LeftToes");
            int rightToes = fixture.Skeleton.FindBone("RightToes");
            Assert.True(_hips >= 0 && _left >= 0 && _right >= 0 && leftToes >= 0 && rightToes >= 0, "Missing anatomical bones.");
            _leftBall = fixture.Skeleton.GetBoneGlobalRest(_left).AffineInverse() * fixture.Skeleton.GetBoneGlobalRest(leftToes).Origin;
            _rightBall = fixture.Skeleton.GetBoneGlobalRest(_right).AffineInverse() * fixture.Skeleton.GetBoneGlobalRest(rightToes).Origin;
            fixture.Skeleton.SkeletonUpdated += Capture;
        }

        private void Capture()
        {
            try
            {
                ulong tick = Engine.GetPhysicsFrames();
                if (_fixture.PlantedSource.EvaluationTick != tick)
                {
                    throw new InvalidOperationException("Pose and contact source do not describe the same physics tick.");
                }
                Skeleton3D skeleton = _fixture.Skeleton;
                Samples[tick] = new PoseSample(tick, _fixture.Actor.GlobalTransform,
                    skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(_hips).Origin,
                    skeleton.GlobalTransform * (skeleton.GetBoneGlobalPose(_left) * _leftBall),
                    skeleton.GlobalTransform * (skeleton.GetBoneGlobalPose(_right) * _rightBall), _fixture.PlantedSource.LastState);
            }
            catch (Exception exception)
            {
                _failure ??= exception;
            }
        }

        public void AssertHealthy()
        {
            if (_failure is not null)
            {
                throw new InvalidOperationException("Native pose capture failed.", _failure);
            }
        }

        public void Stop()
        {
            if (!_stopped)
            {
                _stopped = true;
                _fixture.Skeleton.SkeletonUpdated -= Capture;
            }
        }
        public void Dispose() => Stop();
    }
}
