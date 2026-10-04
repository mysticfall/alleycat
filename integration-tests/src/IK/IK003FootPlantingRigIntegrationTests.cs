using System.Text;
using AlleyCat.Control.Locomotion;
using AlleyCat.IK;
using AlleyCat.IntegrationTests.Navigation;
using AlleyCat.Navigation;
using AlleyCat.Rigging;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// IK-003 Technical Requirement 21 fixtures on both reference rigs (the installed reference-female NPC and the
/// installed reference-male NPC in the navigation test scene), with equivalent inputs for each rig.
/// </summary>
/// <remarks>
/// <para>The production NPC installation is used deliberately: the claims are about the real rigs' toe bones, the real
/// CharacterIK modifier pipeline, the real AnimationTree, and same-tick ordering, which a synthetic skeleton would not
/// exercise.</para>
/// <para>Scripted-interval fixtures replace only the planted-state source; everything else, including the post-IK ball
/// point read inside <see cref="Skeleton3D.SignalName.SkeletonUpdated"/>, is production. Graph fixtures use the
/// independent <see cref="AnimationGraphPlantedStateRecorder"/> as the oracle for ANIM-002 planted state.</para>
/// </remarks>
public sealed class IK003FootPlantingRigIntegrationTests
{
    private const float PlanarHoldToleranceMetres = 0.001f;
    private const float HeightToleranceMetres = 0.001f;
    private const float ReachToleranceRatio = 1.0005f;
    private const float LiftOffSolveToleranceMetres = 0.0005f;
    // USER-APPROVED 2026-10-03 revised criterion: planar final-post-IK ball realization across the scripted
    // native-heading ±180° seam is accepted within 0.005 m by user authority ("Even 5mm would be good enough"),
    // not a TR26 measured derivation. This replaces the 0.0005 m value the wrap scenario borrowed from the
    // plain-release temporal calibration envelope; the original 0.0005 m red is retained as precommit evidence.
    // Scope: only ScriptedNativeHeadingWrapAsync's planar guard. Height, heading, input, axis, counts, reach,
    // and post-IK-capture predicates keep their original values.
    private const float ScriptedHeadingWrapPlanarToleranceMetres = 0.005f;
    private const float KneeDirectionMinimumOffsetMetres = 0.005f;
    private const float MinimumPoleForwardOffsetMetres = 0.05f;
    private const float MaximumPoleSideToForwardRatio = 0.35f;
    private const float MinimumPoleContinuityDot = 0.8f;
    private const int RequestTimeoutTicks = 1200;

    /// <summary>IK-TAC-007 and IK-TAC-001 order and actuation on the female rig.</summary>
    [Fact]
    public Task Female_BallJointModifierOrderAndSoleActuation() => BallJointModifierOrderAndSoleActuationAsync(Rig.Female);

    /// <summary>IK-TAC-007 and IK-TAC-001 order and actuation on the male rig.</summary>
    [Fact]
    public Task Male_BallJointModifierOrderAndSoleActuation() => BallJointModifierOrderAndSoleActuationAsync(Rig.Male);

    /// <summary>A rig missing a toe bone is a bind error on the female rig: neither foot plants.</summary>
    [Fact]
    public Task Female_MissingToeBoneFailsClosed() => MissingToeBoneFailsClosedAsync();

    /// <summary>IK-TAC-002, IK-TAC-004, and IK-TAC-005 with scripted intervals on the female rig.</summary>
    [Fact]
    public Task Female_ScriptedIntervals_CaptureHoldFloatSinkBlendOutReach() => ScriptedIntervalsAsync(Rig.Female);

    /// <summary>IK-TAC-002, IK-TAC-004, and IK-TAC-005 with scripted intervals on the male rig.</summary>
    [Fact]
    public Task Male_ScriptedIntervals_CaptureHoldFloatSinkBlendOutReach() => ScriptedIntervalsAsync(Rig.Male);

    /// <summary>TR22: independent temporal yaw/ball oracle on the installed female rig.</summary>
    [Fact]
    public Task Female_WorldYawTemporalMatrix_BothFeet() => WorldYawTemporalMatrixAsync(Rig.Female);

    /// <summary>TR22: independent temporal yaw/ball oracle on the installed male rig.</summary>
    [Fact]
    public Task Male_WorldYawTemporalMatrix_BothFeet() => WorldYawTemporalMatrixAsync(Rig.Male);

    /// <summary>Scripted native heading crosses the representation seam on each installed rig.</summary>
    [Fact]
    public Task Female_ScriptedNativeHeading_WrapsBothDirections() => ScriptedNativeHeadingWrapAsync(Rig.Female);

    /// <summary>Scripted native heading crosses the representation seam on each installed rig.</summary>
    [Fact]
    public Task Male_ScriptedNativeHeading_WrapsBothDirections() => ScriptedNativeHeadingWrapAsync(Rig.Male);

    /// <summary>Scoped known-command precision calibration over the female temporal fixture pose envelope.</summary>
    [Fact]
    public Task Female_TemporalKnownTargets_NativePrecision() => TemporalKnownTargetsAsync(Rig.Female);

    /// <summary>Scoped known-command precision calibration over the male temporal fixture pose envelope.</summary>
    [Fact]
    public Task Male_TemporalKnownTargets_NativePrecision() => TemporalKnownTargetsAsync(Rig.Male);

    // These explicit offsets/yaws were fixed from the prior independently derived native-input temporal trace,
    // BEFORE observing this calibration's native output. No foot-planting provider supplies a command or oracle.
    private readonly record struct CalibrationPose(string Name, float ActorYaw, float Pitch, float Roll,
        float ActorX, float LeftYaw, Vector3 LeftOffset, float RightYaw, Vector3 RightOffset,
        float HeightShift = 0f, float FootTilt = 0f, float HeelRoll = 0f, float ReachExtension = 0f);

    private sealed partial class TemporalKnownTargetProvider : IKTargetIntentProvider
    {
        public Transform3D Command { get; set; } = Transform3D.Identity;
        public bool Enabled
        {
            get; set;
        }

        public int Calls
        {
            get; private set;
        }

        public override IKTargetIntent GetTargetIntent() => new(Command, Enabled ? 1f : 0f);

        public override bool TryGetTargetIntent(out IKTargetIntentResolution resolution)
        {
            Calls++;
            resolution = new IKTargetIntentResolution(GetTargetIntent(), ShouldApplyTargetTransform: true);
            return Enabled;
        }
    }

    private static async Task TemporalKnownTargetsAsync(Rig rig)
    {
        await using RigFixture fixture = await RigFixture.CreateAsync(rig);
        fixture.Navigation.DeactivateMovementAuthority();
        Skeleton3D skeleton = fixture.Skeleton;
        SkeletonModifier3D stage = skeleton.GetNode<SkeletonModifier3D>("CharacterIKFootProviderStage");
        Node3D leftTarget = Assert.IsType<Node3D>(fixture.CharacterIK.LeftFootIKTarget, exactMatch: false);
        Node3D rightTarget = Assert.IsType<Node3D>(fixture.CharacterIK.RightFootIKTarget, exactMatch: false);
        var leftProvider = new TemporalKnownTargetProvider { Name = "TemporalKnownLeft" };
        var rightProvider = new TemporalKnownTargetProvider { Name = "TemporalKnownRight" };
        fixture.CharacterIK.AddChild(leftProvider);
        fixture.CharacterIK.AddChild(rightProvider);
        fixture.CharacterIK.LeftFootTargetIntentProvider = leftProvider;
        fixture.CharacterIK.RightFootTargetIntentProvider = rightProvider;
        Vector3 actorStart = fixture.Actor.GlobalPosition;
        float actorYaw = fixture.Actor.Rotation.Y;
        // Explicit per-rig planar displacements reproduce the envelope's near-extension, with NO 60 mm inset.
        CalibrationPose[] poses = rig == Rig.Female ?
        [
            new("capture-native", 0, 0, 0, 0, 0, Vector3.Zero, 0, Vector3.Zero),
            new("capture", 0, 0, 0, 0, -.25f, new(.001f, 0, 0), -.25f, new(.001f, 0, 0), -.0008f, -1f, -1f),
            new("tilt-plus", 0, 0, 0, 0, 0, Vector3.Zero, 0, Vector3.Zero, FootTilt: 1.5f),
            new("hold-6", 12, 1.2f, .6f, .003f, -11.5799f, new(.018370f, 0, -.024241f), -11.8761f, new(.021406f, 0, .023219f)),
            new("hold-12", 24, 2.4f, 1.2f, .006f, -23.111f, new(.030713f, 0, -.051729f), -23.7105f, new(.046941f, 0, .041330f), ReachExtension: .0005f),
            new("release-1", 24, 2.4f, 1.2f, .006f, -22.3184f, new(.029660f, 0, -.049955f), -22.8974f, new(.045331f, 0, .039913f)),
            new("release-5", 24, 2.4f, 1.2f, .006f, -9.6375f, new(.012808f, 0, -.021572f), -9.8875f, new(.019575f, 0, .017235f)),
            new("release-9", 24, 2.4f, 1.2f, .006f, 0, Vector3.Zero, 0, Vector3.Zero, .0008f, 1f, 2f),
            new("release-native", 24, 2.4f, 1.2f, .006f, 0, Vector3.Zero, 0, Vector3.Zero, HeelRoll: -.1f),
            new("pre-hold", 32, 0, 0, .006f, -8.7453f, new(.007274f, 0, -.020080f), -8.2145f, new(.020980f, 0, .009036f)),
            new("release-3", 32, 0, 0, .006f, -6.478f, new(.005388f, 0, -.014874f), -6.0848f, new(.015541f, 0, .006693f)),
            new("reacquisition", 32, 0, 0, .006f, -6.478f, new(.005388f, 0, -.012874f), -6.0848f, new(.015541f, 0, .006693f)),
            new("post-hold", 32, 0, 0, .006f, -6.3279f, new(.005267f, 0, -.015005f), -6.1445f, new(.015549f, 0, .006751f))
        ] :
        [
            new("capture-native", 0, 0, 0, 0, 0, Vector3.Zero, 0, Vector3.Zero),
            new("capture", 0, 0, 0, 0, -.25f, new(.001f, 0, 0), -.25f, new(.001f, 0, 0), -.0008f, -1f, -1f),
            new("tilt-plus", 0, 0, 0, 0, 0, Vector3.Zero, 0, Vector3.Zero, FootTilt: 1.5f),
            new("hold-6", 12, 1.2f, .6f, .003f, -11.6238f, new(.022816f, 0, -.029722f), -11.8532f, new(.026547f, 0, .028870f)),
            new("hold-12", 24, 2.4f, 1.2f, .006f, -23.1969f, new(.038318f, 0, -.063396f), -23.6663f, new(.058270f, 0, .051470f), ReachExtension: .0005f),
            new("release-1", 24, 2.4f, 1.2f, .006f, -22.4014f, new(.037004f, 0, -.061222f), -22.8547f, new(.056272f, 0, .049705f)),
            new("release-5", 24, 2.4f, 1.2f, .006f, -9.6733f, new(.015979f, 0, -.026437f), -9.8691f, new(.024299f, 0, .021464f)),
            new("release-9", 24, 2.4f, 1.2f, .006f, 0, Vector3.Zero, 0, Vector3.Zero, .0008f, 1f, 2f),
            new("release-native", 24, 2.4f, 1.2f, .006f, 0, Vector3.Zero, 0, Vector3.Zero, HeelRoll: -.1f),
            new("pre-hold", 32, 0, 0, .006f, -8.6599f, new(.008689f, 0, -.024797f), -8.2583f, new(.025573f, 0, .011078f)),
            new("release-3", 32, 0, 0, .006f, -6.4147f, new(.006436f, 0, -.018368f), -6.1173f, new(.018943f, 0, .008206f)),
            new("reacquisition", 32, 0, 0, .006f, -6.4147f, new(.006436f, 0, -.016368f), -6.1173f, new(.018943f, 0, .008206f)),
            new("post-hold", 32, 0, 0, .006f, -6.2634f, new(.006306f, 0, -.018538f), -6.1764f, new(.018945f, 0, .008232f))
        ];
        var stageSamples = new Dictionary<ulong, (Transform3D Left, Transform3D Right, Vector3 LeftHip, Vector3 RightHip,
            Transform3D NativeLeft, Transform3D NativeRight)>();
        var finalSamples = new Dictionary<ulong, (Transform3D Left, Transform3D Right)>();
        var stageCounts = new Dictionary<ulong, int>();
        var finalCounts = new Dictionary<ulong, int>();
        int finalBeforeStage = 0;
        void OnStage()
        {
            ulong tick = Engine.GetPhysicsFrames();
            stageCounts[tick] = stageCounts.GetValueOrDefault(tick) + 1;
            Transform3D world = skeleton.GlobalTransform;
            stageSamples[tick] = (leftTarget.GlobalTransform, rightTarget.GlobalTransform,
                world * skeleton.GetBoneGlobalPose(skeleton.FindBone("LeftUpperLeg")).Origin,
                world * skeleton.GetBoneGlobalPose(skeleton.FindBone("RightUpperLeg")).Origin,
                world * skeleton.GetBoneGlobalPose(skeleton.FindBone("LeftFoot")),
                world * skeleton.GetBoneGlobalPose(skeleton.FindBone("RightFoot")));
        }

        void OnFinal()
        {
            ulong tick = Engine.GetPhysicsFrames();
            finalCounts[tick] = finalCounts.GetValueOrDefault(tick) + 1;
            if (!stageSamples.ContainsKey(tick))
            {
                finalBeforeStage++;
            }

            Transform3D world = skeleton.GlobalTransform;
            finalSamples[tick] = (world * skeleton.GetBoneGlobalPose(skeleton.FindBone("LeftFoot")),
                world * skeleton.GetBoneGlobalPose(skeleton.FindBone("RightFoot")));
        }

        stage.ModificationProcessed += OnStage;
        skeleton.SkeletonUpdated += OnFinal;
        var rows = new StringBuilder("pose\ttick\tside\tactor_yaw_deg\tactor_pitch_deg\tactor_roll_deg\tactor_x\tpreceding_native_origin\tnative_origin\tnative_basis\tcommand_origin\tcommand_basis\tapplied_origin\tapplied_basis\tfinal_origin\tfinal_basis\tlever\tcommand_ball\tfinal_ball\tcommand_yaw\tfinal_yaw\thip\tnative_reach\ttarget_reach\tchain\tstage_count\tfinal_count\tprovider_calls\tcommand_applied_origin_error\tcommand_applied_basis_error\tplanar_ball_error\tball_y_error\theel_component_error\ttilt_component_error\tfull_basis_error\tfoot_origin_error\theading_error_deg\n");
        var failures = new List<string>();
        int judged = 0;
        try
        {
            leftProvider.Enabled = false;
            rightProvider.Enabled = false;
            foreach (CalibrationPose pose in poses)
            {
                fixture.Actor.Rotation = new Vector3(Mathf.DegToRad(pose.Pitch), actorYaw + Mathf.DegToRad(pose.ActorYaw), Mathf.DegToRad(pose.Roll));
                fixture.Actor.GlobalPosition = actorStart + new Vector3(pose.ActorX, 0, 0);
                _ = await fixture.StepAsync(1); // Unactuated native input, sampled within SkeletonUpdated.
                ulong baselineTick = Engine.GetPhysicsFrames();
                Assert.Equal(1, stageCounts.GetValueOrDefault(baselineTick));
                Assert.Equal(1, finalCounts.GetValueOrDefault(baselineTick));
                (Transform3D baselineLeft, Transform3D baselineRight) = finalSamples[baselineTick];
                // A disabled calibration policy must leave the baseline genuinely native, not reuse a stale
                // target from the previous case. Both stages were captured inside their own callbacks.
                foreach ((Transform3D native, Transform3D final) in new[]
                {
                    (stageSamples[baselineTick].NativeLeft, baselineLeft),
                    (stageSamples[baselineTick].NativeRight, baselineRight)
                })
                {
                    Assert.True(native.Origin.DistanceTo(final.Origin) < .00001f &&
                        BasisError(native.Basis, final.Basis) < .00001f,
                        $"SETUP: {rig} {pose.Name} disabled target still modifies the calibration baseline.");
                }

                leftProvider.Command = Command(baselineLeft, fixture.BallLevers["Left"], pose.LeftYaw, pose.LeftOffset,
                    stageSamples[baselineTick].LeftHip, pose);
                rightProvider.Command = Command(baselineRight, fixture.BallLevers["Right"], pose.RightYaw, pose.RightOffset,
                    stageSamples[baselineTick].RightHip, pose);
                if (pose.Name == "release-5")
                {
                    Transform3D target = rightProvider.Command;
                    Vector3 hip = stageSamples[baselineTick].RightHip;
                    rightProvider.Command = new Transform3D(target.Basis,
                        target.Origin - ((target.Origin - hip).Normalized() * .0013f));
                }
                leftProvider.Enabled = true;
                rightProvider.Enabled = true;
                int leftCalls = leftProvider.Calls, rightCalls = rightProvider.Calls;
                _ = await fixture.StepAsync(1);
                ulong tick = Engine.GetPhysicsFrames();
                Assert.Equal(baselineTick + 1, tick);
                Assert.Equal(1, stageCounts.GetValueOrDefault(tick));
                Assert.Equal(1, finalCounts.GetValueOrDefault(tick));
                (Transform3D appliedLeft, Transform3D appliedRight, Vector3 hipLeft, Vector3 hipRight,
                    Transform3D nativeLeft, Transform3D nativeRight) = stageSamples[tick];
                (Transform3D finalLeft, Transform3D finalRight) = finalSamples[tick];
                Record("Left", baselineLeft, nativeLeft, leftProvider.Command, appliedLeft, finalLeft, hipLeft, leftProvider.Calls - leftCalls);
                Record("Right", baselineRight, nativeRight, rightProvider.Command, appliedRight, finalRight, hipRight, rightProvider.Calls - rightCalls);
                leftProvider.Enabled = false;
                rightProvider.Enabled = false;

                void Record(string side, Transform3D precedingNative, Transform3D native, Transform3D command, Transform3D applied,
                    Transform3D output, Vector3 hip, int calls)
                {
                    Vector3 lever = fixture.BallLevers[side];
                    Vector3 commandedBall = command * lever;
                    Vector3 finalBall = output * lever;
                    float planar = new Vector2(finalBall.X - commandedBall.X, finalBall.Z - commandedBall.Z).Length();
                    float height = Math.Abs(finalBall.Y - commandedBall.Y);
                    float heel = Math.Abs(output.Basis.X.Y - command.Basis.X.Y);
                    float tilt = Math.Abs(output.Basis.Z.Y - command.Basis.Z.Y);
                    float appliedBasis = BasisError(command.Basis, applied.Basis);
                    float fullBasis = BasisError(command.Basis, output.Basis);
                    float appliedOrigin = command.Origin.DistanceTo(applied.Origin);
                    float chain = ChainLength(side);
                    float nativeReach = native.Origin.DistanceTo(hip);
                    float targetReach = command.Origin.DistanceTo(hip);
                    float heading = Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(
                        WorldFacing(command.Basis * lever), WorldFacing(output.Basis * lever))));
                    _ = rows.AppendLine(FormattableString.Invariant($"{pose.Name}\t{tick}\t{side}\t{pose.ActorYaw:R}\t{pose.Pitch:R}\t{pose.Roll:R}\t{pose.ActorX:R}\t{V(precedingNative.Origin)}\t{V(native.Origin)}\t{B(native.Basis)}\t{V(command.Origin)}\t{B(command.Basis)}\t{V(applied.Origin)}\t{B(applied.Basis)}\t{V(output.Origin)}\t{B(output.Basis)}\t{V(lever)}\t{V(commandedBall)}\t{V(finalBall)}\t{WorldFacing(command.Basis * lever):R}\t{WorldFacing(output.Basis * lever):R}\t{V(hip)}\t{nativeReach:R}\t{targetReach:R}\t{chain:R}\t{stageCounts[tick]}\t{finalCounts[tick]}\t{calls}\t{appliedOrigin:R}\t{appliedBasis:R}\t{planar:R}\t{height:R}\t{heel:R}\t{tilt:R}\t{fullBasis:R}\t{command.Origin.DistanceTo(output.Origin):R}\t{heading:R}"));
                    judged++;
                    if (calls != 1 || targetReach >= chain - 0.0001f ||
                        appliedOrigin > 0.00001f || appliedBasis > 0.00001f ||
                        planar > 0.0005f || height > 0.0005f || heel > 0.0001f || tilt > 0.0001f)
                    {
                        failures.Add(FormattableString.Invariant($"{rig} {pose.Name} {side} {tick}: calls={calls} nativeReach={nativeReach:R} targetReach={targetReach:R} chain={chain:R} applied={appliedOrigin:R}/{appliedBasis:R} ball={planar:R}/{height:R} basis={heel:R}/{tilt:R}"));
                    }
                }
            }
        }
        finally
        {
            stage.ModificationProcessed -= OnStage;
            skeleton.SkeletonUpdated -= OnFinal;
            string directory = ProjectSettings.GlobalizePath("res://temp/nav001-yaw-temporal-calibration/run-08");
            _ = Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{rig}-known-targets.tsv"), rows.ToString());
            File.WriteAllText(Path.Combine(directory, $"{rig}-calibration-result.txt"),
                $"rig={rig} known_commands={judged} stage_order_errors={finalBeforeStage} failures={failures.Count}\n" + string.Join('\n', failures));
        }

        Assert.Equal(26, judged);
        Assert.Equal(0, finalBeforeStage);
        Assert.Empty(failures);

        float ChainLength(string side)
        {
            Vector3 upper = skeleton.GetBoneGlobalRest(skeleton.FindBone($"{side}UpperLeg")).Origin;
            Vector3 lower = skeleton.GetBoneGlobalRest(skeleton.FindBone($"{side}LowerLeg")).Origin;
            Vector3 foot = skeleton.GetBoneGlobalRest(skeleton.FindBone($"{side}Foot")).Origin;
            float scale = skeleton.GlobalTransform.Basis.X.Length();
            return (upper.DistanceTo(lower) + lower.DistanceTo(foot)) * scale;
        }

        static Transform3D Command(Transform3D native, Vector3 lever, float yaw, Vector3 offset,
            Vector3 hip, CalibrationPose pose)
        {
            Basis tilted = native.Basis.Rotated(native.Basis.X.Normalized(), Mathf.DegToRad(pose.FootTilt));
            Basis rolled = tilted.Rotated((tilted * lever).Normalized(), Mathf.DegToRad(pose.HeelRoll));
            Basis basis = new Basis(Vector3.Up, Mathf.DegToRad(yaw)) * rolled;
            Vector3 ball = (native * lever) + offset + new Vector3(0, pose.HeightShift, 0);
            Vector3 origin = ball - (basis * lever);
            if (pose.ReachExtension != 0f)
            {
                origin += (origin - hip).Normalized() * pose.ReachExtension;
            }

            return new Transform3D(basis, origin);
        }

        static float BasisError(Basis expected, Basis actual)
        {
            return Math.Max(expected.X.DistanceTo(actual.X),
                Math.Max(expected.Y.DistanceTo(actual.Y), expected.Z.DistanceTo(actual.Z)));
        }

        static string V(Vector3 value)
        {
            return FormattableString.Invariant($"{value.X:R};{value.Y:R};{value.Z:R}");
        }

        static string B(Basis value)
        {
            return $"{V(value.X)}|{V(value.Y)}|{V(value.Z)}";
        }
    }

    // Scripted metadata isolates contact geometry; native Idle, the installed rig and IK pipeline remain active.
    private static async Task ScriptedNativeHeadingWrapAsync(Rig rig)
    {
        await using RigFixture fixture = await RigFixture.CreateAsync(rig);
        fixture.Navigation.DeactivateMovementAuthority();
        ScriptedPlantedStateSource source = fixture.UseScriptedSource();
        Skeleton3D skeleton = fixture.Skeleton;
        SkeletonModifier3D beginStage = skeleton.GetNode<SkeletonModifier3D>("CharacterIKBeginStage");
        SkeletonModifier3D providerStage = skeleton.GetNode<SkeletonModifier3D>("CharacterIKFootProviderStage");
        var native = new Dictionary<ulong, (Transform3D Left, Transform3D Right)>();
        var stageCount = new Dictionary<ulong, int>();
        var finalCount = new Dictionary<ulong, int>();
        var failures = new List<string>();
        var rows = new StringBuilder("direction,stage,tick,side,requested_deg,native_deg,expected_deg,final_deg,native_ball_x,native_ball_y,native_ball_z,expected_ball_x,expected_ball_z,final_ball_x,final_ball_y,final_ball_z,phase,reach_release,stage_count,final_count\n");
        void OnStage()
        {
            ulong tick = Engine.GetPhysicsFrames();
            stageCount[tick] = stageCount.GetValueOrDefault(tick) + 1;
            Transform3D world = skeleton.GlobalTransform;
            native[tick] = (world * skeleton.GetBoneGlobalPose(skeleton.FindBone("LeftFoot")),
                world * skeleton.GetBoneGlobalPose(skeleton.FindBone("RightFoot")));
        }

        void OnFinal()
        {
            ulong tick = Engine.GetPhysicsFrames();
            finalCount[tick] = finalCount.GetValueOrDefault(tick) + 1;
        }

        providerStage.ModificationProcessed += OnStage;
        skeleton.SkeletonUpdated += OnFinal;
        // Begin-stage observation writes test-only native ANIM pose before the real provider commits.
        var pose = new ScriptedNativeFootHeading(skeleton, fixture.BallLevers);
        beginStage.ModificationProcessed += pose.Apply;
        fixture.Actor.Rotation = new Vector3(0f, Mathf.Pi, 0f);
        source.State = new PlantedFeetState(false, false);
        _ = await fixture.StepAsync(10);
        int releaseTicks = (int)Math.Ceiling(fixture.Left.BlendOutSeconds * Engine.PhysicsTicksPerSecond);
        int crossingCount = 0;
        int judged = 0;
        try
        {
            foreach (int direction in new[] { 1, -1 })
            {
                float[] commands = [179.5f, 179.8f, -179.8f, -179.5f];
                if (direction < 0)
                {
                    commands = [-179.5f, -179.8f, 179.8f, 179.5f];
                }
                pose.RequestedHeading = Mathf.DegToRad(commands[0]);
                pose.ApplyHeading = true;
                _ = await fixture.StepAsync(3);
                var anchors = new Dictionary<string, (float Heading, Vector3 Ball)>();
                var release = new Dictionary<string, (float Yaw, Vector3 Offset)>();
                float previousNative = float.NaN;

                void Observe(string stage, FootPlantingTickDiagnostic diagnostic, string side, float expectedHeading, Vector3 expectedBall)
                {
                    ulong tick = diagnostic.Tick;
                    Assert.Equal(tick, Engine.GetPhysicsFrames());
                    Assert.True(native.TryGetValue(tick, out (Transform3D Left, Transform3D Right) feet), $"SETUP: missing native stage tick {tick}.");
                    Transform3D animated = side == "Left" ? feet.Left : feet.Right;
                    Transform3D final = fixture.PostIKFeet[side][tick];
                    Vector3 lever = fixture.BallLevers[side];
                    Vector3 animatedBall = animated * lever;
                    Vector3 finalBall = final * lever;
                    float nativeHeading = WorldFacing(animated.Basis * lever);
                    float finalHeading = WorldFacing(final.Basis * lever);
                    float requested = pose.RequestedHeading;
                    float inputError = Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(requested, nativeHeading)));
                    float headingError = Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(expectedHeading, finalHeading)));
                    float ballError = new Vector2(finalBall.X - expectedBall.X, finalBall.Z - expectedBall.Z).Length();
                    float axisError = Math.Max(Mathf.Abs(animated.Basis.X.Y - final.Basis.X.Y),
                        Mathf.Abs(animated.Basis.Z.Y - final.Basis.Z.Y));
                    _ = rows.AppendLine(FormattableString.Invariant($"{direction},{stage},{tick},{side},{Mathf.RadToDeg(requested):R},{Mathf.RadToDeg(nativeHeading):R},{Mathf.RadToDeg(expectedHeading):R},{Mathf.RadToDeg(finalHeading):R},{animatedBall.X:R},{animatedBall.Y:R},{animatedBall.Z:R},{expectedBall.X:R},{expectedBall.Z:R},{finalBall.X:R},{finalBall.Y:R},{finalBall.Z:R},{diagnostic.Phase},{diagnostic.ReachBlendOutStarted},{stageCount.GetValueOrDefault(tick)},{finalCount.GetValueOrDefault(tick)}"));
                    judged++;
                    if (inputError > 0.02f || headingError > 1.25f || ballError > ScriptedHeadingWrapPlanarToleranceMetres ||
                        Mathf.Abs(finalBall.Y - animatedBall.Y) > 0.0005f || axisError > 0.0001f ||
                        stageCount.GetValueOrDefault(tick) != 1 || finalCount.GetValueOrDefault(tick) != 1 ||
                        diagnostic.ReachBlendOutStarted || !diagnostic.PostIKCaptured)
                    {
                        failures.Add(FormattableString.Invariant($"{rig} direction={direction} {stage} {side} tick={tick}: input={inputError:R}deg heading={headingError:R}deg ball={ballError:R}m height={finalBall.Y - animatedBall.Y:R}m axis={axisError:R} reach_release={diagnostic.ReachBlendOutStarted}"));
                    }
                }

                source.State = new PlantedFeetState(true, true);
                (FootPlantingTickDiagnostic Left, FootPlantingTickDiagnostic Right) pair = await fixture.StepAsync(1);
                foreach ((string side, FootPlantingTickDiagnostic diagnostic) in new[] { ("Left", pair.Left), ("Right", pair.Right) })
                {
                    Transform3D foot = side == "Left" ? native[diagnostic.Tick].Left : native[diagnostic.Tick].Right;
                    anchors[side] = (WorldFacing(foot.Basis * fixture.BallLevers[side]), foot * fixture.BallLevers[side]);
                    Observe("capture", diagnostic, side, anchors[side].Heading, anchors[side].Ball);
                }

                for (int index = 1; index < commands.Length; index++)
                {
                    pose.RequestedHeading = Mathf.DegToRad(commands[index]);
                    pair = await fixture.StepAsync(1);
                    float heading = WorldFacing(native[pair.Left.Tick].Left.Basis * fixture.BallLevers["Left"]);
                    if (index == 2)
                    {
                        Assert.True(Mathf.Sign(heading) != Mathf.Sign(previousNative),
                            $"SETUP: {rig} native heading never crossed signed pi: previous={previousNative:R}, current={heading:R}, requested={pose.RequestedHeading:R}, modifier_calls={pose.Calls}, modifier_heading={pose.LastHeading:R}, readback={pose.LastReadback:R}, rows:\n{rows}");
                        crossingCount++;
                    }

                    previousNative = heading;
                    Observe("hold", pair.Left, "Left", anchors["Left"].Heading, anchors["Left"].Ball);
                    Observe("hold", pair.Right, "Right", anchors["Right"].Heading, anchors["Right"].Ball);
                }

                source.State = new PlantedFeetState(false, false);
                foreach (string side in new[] { "Left", "Right" })
                {
                    Transform3D foot = side == "Left" ? native[pair.Left.Tick].Left : native[pair.Right.Tick].Right;
                    Vector3 lever = fixture.BallLevers[side];
                    release[side] = (Mathf.AngleDifference(WorldFacing(foot.Basis * lever), anchors[side].Heading),
                        anchors[side].Ball - (foot * lever));
                    Assert.InRange(Mathf.Abs(Mathf.RadToDeg(release[side].Yaw)), 0.5f, 2f);
                }

                for (int tick = 1; tick <= releaseTicks + 3; tick++)
                {
                    pose.RequestedHeading = Mathf.DegToRad(commands[tick % commands.Length]);
                    pair = await fixture.StepAsync(1);
                    foreach ((string side, FootPlantingTickDiagnostic diagnostic) in new[] { ("Left", pair.Left), ("Right", pair.Right) })
                    {
                        Transform3D foot = side == "Left" ? native[diagnostic.Tick].Left : native[diagnostic.Tick].Right;
                        Vector3 lever = fixture.BallLevers[side];
                        double progress = Math.Clamp((double)tick / Engine.PhysicsTicksPerSecond /
                            (side == "Left" ? fixture.Left.BlendOutSeconds : fixture.Right.BlendOutSeconds), 0d, 1d);
                        float weight = (float)(1d - (progress * progress * (3d - (2d * progress))));
                        Observe("release", diagnostic, side,
                            WorldFacing(foot.Basis * lever) + (release[side].Yaw * weight),
                            (foot * lever) + (release[side].Offset * weight));
                    }
                }
            }
        }
        finally
        {
            beginStage.ModificationProcessed -= pose.Apply;
            providerStage.ModificationProcessed -= OnStage;
            skeleton.SkeletonUpdated -= OnFinal;
        }

        string directory = ProjectSettings.GlobalizePath("res://temp/nav001-yaw-native-edges/run-01");
        _ = Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{rig}-wrap.csv"), rows.ToString());
        File.WriteAllText(Path.Combine(directory, $"{rig}-wrap-result.txt"),
            $"rig={rig} crossings={crossingCount} foot_samples={judged} failures={failures.Count}\n" + string.Join('\n', failures));
        Assert.Equal(2, crossingCount);
        Assert.True(failures.Count == 0, string.Join('\n', failures.Take(20)));
    }

    private static async Task WorldYawTemporalMatrixAsync(Rig rig)
    {
        await using RigFixture fixture = await RigFixture.CreateAsync(rig);
        fixture.Navigation.DeactivateMovementAuthority();
        ScriptedPlantedStateSource source = fixture.UseScriptedSource();
        Skeleton3D skeleton = fixture.Skeleton;
        SkeletonModifier3D providerStage = skeleton.GetNode<SkeletonModifier3D>("CharacterIKFootProviderStage");
        var nativeFeet = new Dictionary<ulong, (Transform3D Left, Transform3D Right)>();
        var stageLegs = new Dictionary<ulong, (Vector3 LeftHip, Vector3 RightHip, Transform3D LeftApplied, Transform3D RightApplied)>();
        Node3D leftTarget = Assert.IsType<Node3D>(fixture.CharacterIK.LeftFootIKTarget, exactMatch: false);
        Node3D rightTarget = Assert.IsType<Node3D>(fixture.CharacterIK.RightFootIKTarget, exactMatch: false);
        var stageCounts = new Dictionary<ulong, int>();
        var finalCounts = new Dictionary<ulong, int>();
        int outOfOrder = 0;
        void OnStage()
        {
            ulong tick = Engine.GetPhysicsFrames();
            stageCounts[tick] = stageCounts.GetValueOrDefault(tick) + 1;
            Transform3D world = skeleton.GlobalTransform;
            nativeFeet[tick] = (world * skeleton.GetBoneGlobalPose(skeleton.FindBone("LeftFoot")),
                world * skeleton.GetBoneGlobalPose(skeleton.FindBone("RightFoot")));
            stageLegs[tick] = (world * skeleton.GetBoneGlobalPose(skeleton.FindBone("LeftUpperLeg")).Origin,
                world * skeleton.GetBoneGlobalPose(skeleton.FindBone("RightUpperLeg")).Origin,
                leftTarget.GlobalTransform, rightTarget.GlobalTransform);
        }

        void OnFinal()
        {
            ulong tick = Engine.GetPhysicsFrames();
            finalCounts[tick] = finalCounts.GetValueOrDefault(tick) + 1;
            if (!nativeFeet.ContainsKey(tick))
            {
                outOfOrder++;
            }
        }

        providerStage.ModificationProcessed += OnStage;
        skeleton.SkeletonUpdated += OnFinal;
        source.State = new PlantedFeetState(false, false);
        _ = await fixture.StepAsync(10);
        var rows = new StringBuilder("tick,stage,side,metadata,phase,native_yaw_rad,native_ball_x,native_ball_y,native_ball_z,retained_yaw_rad,retained_offset_x,retained_offset_z,expected_yaw_rad,observed_yaw_rad,expected_ball_x,expected_ball_z,observed_ball_x,observed_ball_z,observed_ball_y,native_heel_axis_y,post_heel_axis_y,native_tilt_axis_y,post_tilt_axis_y,reach_release,stage_count,final_count\n");
        var transforms = new StringBuilder("tick\tstage\tside\tnative_origin\tnative_basis\tball_lever\tretained_yaw\tretained_offset\texpected_heading\texpected_ball\tfinal_origin\tfinal_basis\tobserved_heading\tobserved_ball\thip\tnative_reach\texpected_reach\tapplied_reach\tchain\n");
        var failures = new List<string>();
        var anchors = new Dictionary<string, (float Heading, Vector3 Ball)>();
        var retained = new Dictionary<string, (float Yaw, Vector3 Offset)>();
        var release = new Dictionary<string, (float Yaw, Vector3 Offset)>();
        Vector3 start = fixture.Actor.GlobalPosition;
        float originalYaw = fixture.Actor.Rotation.Y;
        int releaseTicks = (int)Math.Ceiling(fixture.Left.BlendOutSeconds * Engine.PhysicsTicksPerSecond);
        const float yawTolerance = 1.25f; // Degrees: independent final native bone fidelity.
        // Fixture-only precision guards: bounded known-command measurements on both native rigs span this
        // temporal pose envelope; see nav001-yaw-temporal-calibration. They are not universal IK error bounds.
        const float ballTolerance = 0.0005f;
        const float axisTolerance = 0.0001f;
        // Independent discontinuous-native-motion control (not a native fixture measurement): a 3-degree
        // release-stage jump must expose an oracle that wrongly re-bases on the first release input.
        float controlWeight = (float)(1d - (Math.Pow(1d / 9d, 2d) * (3d - (2d / 9d))));
        Assert.True(3f * controlWeight > yawTolerance);
        int judged = 0;
        var nativeRanges = new Dictionary<string, (float MinHeel, float MaxHeel, float MinTilt, float MaxTilt)>
        {
            ["Left"] = (float.PositiveInfinity, float.NegativeInfinity, float.PositiveInfinity, float.NegativeInfinity),
            ["Right"] = (float.PositiveInfinity, float.NegativeInfinity, float.PositiveInfinity, float.NegativeInfinity)
        };

        (float Heading, Vector3 Ball, Transform3D Foot) Native(string side, ulong tick)
        {
            Assert.True(nativeFeet.TryGetValue(tick, out (Transform3D Left, Transform3D Right) pair), $"SETUP: missing native provider stage {tick}.");
            Transform3D foot = side == "Left" ? pair.Left : pair.Right;
            Vector3 lever = fixture.BallLevers[side];
            return (WorldFacing(foot.Basis * lever), foot * lever, foot);
        }

        void Observe(string stage, FootPlantingTickDiagnostic diagnostic, string side, float expectedYaw, Vector3 expectedBall)
        {
            Assert.Equal(1, stageCounts.GetValueOrDefault(diagnostic.Tick));
            Assert.Equal(1, finalCounts.GetValueOrDefault(diagnostic.Tick));
            Assert.Equal(diagnostic.Tick, Engine.GetPhysicsFrames());
            Transform3D final = fixture.PostIKFeet[side][diagnostic.Tick];
            Vector3 lever = fixture.BallLevers[side];
            (float animatedYaw, Vector3 animatedBall, Transform3D native) = Native(side, diagnostic.Tick);
            Assert.True(native.Origin.DistanceTo(diagnostic.AnimatedTarget.Origin) < 0.00001f &&
                native.Basis.X.DistanceTo(diagnostic.AnimatedTarget.Basis.X) < 0.00001f &&
                native.Basis.Y.DistanceTo(diagnostic.AnimatedTarget.Basis.Y) < 0.00001f &&
                native.Basis.Z.DistanceTo(diagnostic.AnimatedTarget.Basis.Z) < 0.00001f,
                $"SETUP: {side} tick {diagnostic.Tick} provider native pose does not match observer stage.");
            Vector3 actualBall = final * lever;
            float actualYaw = WorldFacing(final.Basis * lever);
            float yawError = Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(expectedYaw, actualYaw)));
            float planarError = new Vector2(actualBall.X - expectedBall.X, actualBall.Z - expectedBall.Z).Length();
            float nativeHeel = native.Basis.X.Y;
            float nativeTilt = native.Basis.Z.Y;
            if (stage == "hold")
            {
                (float minHeel, float maxHeel, float minTilt, float maxTilt) = nativeRanges[side];
                nativeRanges[side] = (Math.Min(minHeel, nativeHeel), Math.Max(maxHeel, nativeHeel),
                    Math.Min(minTilt, nativeTilt), Math.Max(maxTilt, nativeTilt));
            }
            // A retained offset is planar; the expected full ball uses this tick's native height.
            expectedBall.Y = animatedBall.Y;
            (Vector3 leftHip, Vector3 rightHip, Transform3D leftApplied, Transform3D rightApplied) = stageLegs[diagnostic.Tick];
            Vector3 hip = side == "Left" ? leftHip : rightHip;
            Transform3D applied = side == "Left" ? leftApplied : rightApplied;
            Basis expectedBasis = new Basis(Vector3.Up, Mathf.AngleDifference(animatedYaw, expectedYaw)) * native.Basis;
            Vector3 expectedOrigin = expectedBall - (expectedBasis * lever);
            Vector3 upperRest = skeleton.GetBoneGlobalRest(skeleton.FindBone($"{side}UpperLeg")).Origin;
            Vector3 lowerRest = skeleton.GetBoneGlobalRest(skeleton.FindBone($"{side}LowerLeg")).Origin;
            Vector3 footRest = skeleton.GetBoneGlobalRest(skeleton.FindBone($"{side}Foot")).Origin;
            float chain = (upperRest.DistanceTo(lowerRest) + lowerRest.DistanceTo(footRest)) * skeleton.GlobalTransform.Basis.X.Length();
            (float offsetYaw, Vector3 offsetBall) = retained[side];
            _ = rows.AppendLine(FormattableString.Invariant($"{diagnostic.Tick},{stage},{side},{diagnostic.MetadataPlanted},{diagnostic.Phase},{animatedYaw:R},{animatedBall.X:R},{animatedBall.Y:R},{animatedBall.Z:R},{offsetYaw:R},{offsetBall.X:R},{offsetBall.Z:R},{expectedYaw:R},{actualYaw:R},{expectedBall.X:R},{expectedBall.Z:R},{actualBall.X:R},{actualBall.Z:R},{actualBall.Y:R},{nativeHeel:R},{final.Basis.X.Y:R},{nativeTilt:R},{final.Basis.Z.Y:R},{diagnostic.ReachBlendOutStarted},{stageCounts[diagnostic.Tick]},{finalCounts[diagnostic.Tick]}"));
            _ = transforms.AppendLine(FormattableString.Invariant($"{diagnostic.Tick}\t{stage}\t{side}\t{Vector(native.Origin)}\t{Axes(native.Basis)}\t{Vector(lever)}\t{offsetYaw:R}\t{Vector(offsetBall)}\t{expectedYaw:R}\t{Vector(expectedBall)}\t{Vector(final.Origin)}\t{Axes(final.Basis)}\t{actualYaw:R}\t{Vector(actualBall)}\t{Vector(hip)}\t{native.Origin.DistanceTo(hip):R}\t{expectedOrigin.DistanceTo(hip):R}\t{applied.Origin.DistanceTo(hip):R}\t{chain:R}"));
            judged++;
            if (yawError > yawTolerance || planarError > ballTolerance ||
                Math.Abs(actualBall.Y - animatedBall.Y) > ballTolerance ||
                Math.Abs(nativeHeel - final.Basis.X.Y) > axisTolerance ||
                Math.Abs(nativeTilt - final.Basis.Z.Y) > axisTolerance || !diagnostic.PostIKCaptured)
            {
                failures.Add(FormattableString.Invariant($"{rig} {stage} {side} tick {diagnostic.Tick}: heading error={yawError:R}deg expected={expectedYaw:R} actual={actualYaw:R}; ball error={planarError:R}m; height error={actualBall.Y - animatedBall.Y:R}m; heel error={final.Basis.X.Y - nativeHeel:R} tilt error={final.Basis.Z.Y - nativeTilt:R}; postIK={diagnostic.PostIKCaptured}"));
            }
            if (diagnostic.ReachBlendOutStarted)
            {
                failures.Add($"{rig} {stage} {side} tick {diagnostic.Tick}: unexpected reach release.");
            }
        }

        (FootPlantingTickDiagnostic Left, FootPlantingTickDiagnostic Right) pair;
        source.State = new PlantedFeetState(true, true);
        pair = await fixture.StepAsync(1);
        foreach ((string side, FootPlantingTickDiagnostic value) in new[] { ("Left", pair.Left), ("Right", pair.Right) })
        {
            (float heading, Vector3 ball, _) = Native(side, value.Tick);
            anchors[side] = (heading, ball);
            retained[side] = (0f, Vector3.Zero);
            Observe("capture", value, side, anchors[side].Heading, anchors[side].Ball);
        }

        // Small actor rotation exposes both rigs' reversed container orientation while staying inside leg reach.
        for (int tick = 1; tick <= 12; tick++)
        {
            fixture.Actor.Rotation = new Vector3(Mathf.DegToRad(tick * 0.2f), originalYaw + Mathf.DegToRad(tick * 2f), Mathf.DegToRad(tick * 0.1f));
            fixture.Actor.GlobalPosition = start + new Vector3(0.0005f * tick, 0f, 0f);
            pair = await fixture.StepAsync(1);
            foreach ((string side, FootPlantingTickDiagnostic value) in new[] { ("Left", pair.Left), ("Right", pair.Right) })
            {
                (float heading, Vector3 ball, _) = Native(side, value.Tick);
                retained[side] = (Mathf.AngleDifference(heading, anchors[side].Heading),
                    anchors[side].Ball - ball);
            }
            Observe("hold", pair.Left, "Left", anchors["Left"].Heading, anchors["Left"].Ball);
            Observe("hold", pair.Right, "Right", anchors["Right"].Heading, anchors["Right"].Ball);
        }

        // Native animation continues during release. Smoothstep is the public release timing contract; expected
        // world heading is the independently captured heading blended toward the CURRENT native animated heading.
        source.State = new PlantedFeetState(false, false);
        foreach (string side in new[] { "Left", "Right" })
        {
            release[side] = retained[side]; // The preceding held tick, not release tick one's native pose.
        }
        for (int tick = 1; tick <= releaseTicks + 3; tick++)
        {
            pair = await fixture.StepAsync(1);
            foreach ((string side, FootPlantingTickDiagnostic value) in new[] { ("Left", pair.Left), ("Right", pair.Right) })
            {
                (float nativeYaw, Vector3 nativeBall, _) = Native(side, value.Tick);

                double progress = Math.Clamp((double)tick / Engine.PhysicsTicksPerSecond /
                    (side == "Left" ? fixture.Left.BlendOutSeconds : fixture.Right.BlendOutSeconds), 0d, 1d);
                float weight = (float)(1d - (progress * progress * (3d - (2d * progress))));
                retained[side] = (release[side].Yaw * weight, release[side].Offset * weight);
                float expectedYaw = nativeYaw + retained[side].Yaw;
                Vector3 expectedBall = nativeBall + retained[side].Offset;
                Observe("release", value, side, expectedYaw, expectedBall);
            }
        }

        // Replant, create a nonzero yaw error, then interrupt an unfinished metadata release.
        source.State = new PlantedFeetState(true, true);
        pair = await fixture.StepAsync(1);
        foreach ((string side, FootPlantingTickDiagnostic value) in new[] { ("Left", pair.Left), ("Right", pair.Right) })
        {
            (float heading, Vector3 ball, _) = Native(side, value.Tick);
            anchors[side] = (heading + retained[side].Yaw, ball + retained[side].Offset);
            Observe("replant", value, side, anchors[side].Heading, anchors[side].Ball);
        }

        for (int tick = 1; tick <= 4; tick++)
        {
            fixture.Actor.Rotation = new Vector3(0f, originalYaw + Mathf.DegToRad(24f + (tick * 2f)), 0f);
            pair = await fixture.StepAsync(1);
            foreach ((string side, FootPlantingTickDiagnostic value) in new[] { ("Left", pair.Left), ("Right", pair.Right) })
            {
                (float heading, Vector3 ball, _) = Native(side, value.Tick);
                retained[side] = (Mathf.AngleDifference(heading, anchors[side].Heading), anchors[side].Ball - ball);
            }
            Observe("pre-interruption-hold", pair.Left, "Left", anchors["Left"].Heading, anchors["Left"].Ball);
            Observe("pre-interruption-hold", pair.Right, "Right", anchors["Right"].Heading, anchors["Right"].Ball);
        }

        source.State = new PlantedFeetState(false, false);
        foreach (string side in new[] { "Left", "Right" })
        {
            release[side] = retained[side];
        }
        for (int tick = 1; tick <= 3; tick++)
        {
            pair = await fixture.StepAsync(1);
            foreach ((string side, FootPlantingTickDiagnostic value) in new[] { ("Left", pair.Left), ("Right", pair.Right) })
            {
                (float nativeYaw, Vector3 nativeBall, _) = Native(side, value.Tick);

                double progress = (double)tick / Engine.PhysicsTicksPerSecond /
                    (side == "Left" ? fixture.Left.BlendOutSeconds : fixture.Right.BlendOutSeconds);
                float weight = (float)(1d - (progress * progress * (3d - (2d * progress))));
                retained[side] = (release[side].Yaw * weight, release[side].Offset * weight);
                float expectedYaw = nativeYaw + retained[side].Yaw;
                Vector3 expectedBall = nativeBall + retained[side].Offset;
                Observe("unfinished-release", value, side, expectedYaw, expectedBall);
            }
        }

        source.State = new PlantedFeetState(true, true);
        pair = await fixture.StepAsync(1);
        foreach ((string side, FootPlantingTickDiagnostic value) in new[] { ("Left", pair.Left), ("Right", pair.Right) })
        {
            (float heading, Vector3 ball, _) = Native(side, value.Tick);
            anchors[side] = (heading + retained[side].Yaw, ball + retained[side].Offset);
            Observe("interrupted-reacquisition", value, side, anchors[side].Heading, anchors[side].Ball);
        }

        for (int tick = 1; tick <= 5; tick++)
        {
            pair = await fixture.StepAsync(1);
            foreach ((string side, FootPlantingTickDiagnostic value) in new[] { ("Left", pair.Left), ("Right", pair.Right) })
            {
                (float heading, Vector3 ball, _) = Native(side, value.Tick);
                retained[side] = (Mathf.AngleDifference(heading, anchors[side].Heading), anchors[side].Ball - ball);
            }
            Observe("post-interruption-hold", pair.Left, "Left", anchors["Left"].Heading, anchors["Left"].Ball);
            Observe("post-interruption-hold", pair.Right, "Right", anchors["Right"].Heading, anchors["Right"].Ball);
        }

        providerStage.ModificationProcessed -= OnStage;
        skeleton.SkeletonUpdated -= OnFinal;
        Assert.Equal(0, outOfOrder);
        Assert.Equal(78, judged);
        // Analytic negative control: within EACH foot's 12-tick hold, a frozen orientation must fail at an extremum.
        foreach (string side in new[] { "Left", "Right" })
        {
            (float minHeel, float maxHeel, float minTilt, float maxTilt) = nativeRanges[side];
            Assert.True(maxHeel - minHeel > 2f * axisTolerance && maxTilt - minTilt > 2f * axisTolerance,
                $"SETUP: {side} frozen tilt/heel roll is not distinguishable: heel={maxHeel - minHeel:R} tilt={maxTilt - minTilt:R} guard={axisTolerance:R}.");
        }

        string directory = ProjectSettings.GlobalizePath("res://temp/nav001-yaw-temporal-calibration/run-08");
        _ = Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{rig}-temporal-yaw.csv"), rows.ToString());
        File.WriteAllText(Path.Combine(directory, $"{rig}-temporal-transforms.tsv"), transforms.ToString());
        File.WriteAllText(Path.Combine(directory, $"{rig}-temporal-yaw-result.txt"),
            $"rig={rig} judged={judged} release_ticks={releaseTicks} stage_final_order_errors={outOfOrder} " +
            $"left_heel_span={nativeRanges["Left"].MaxHeel - nativeRanges["Left"].MinHeel:R} " +
            $"left_tilt_span={nativeRanges["Left"].MaxTilt - nativeRanges["Left"].MinTilt:R} " +
            $"right_heel_span={nativeRanges["Right"].MaxHeel - nativeRanges["Right"].MinHeel:R} " +
            $"right_tilt_span={nativeRanges["Right"].MaxTilt - nativeRanges["Right"].MinTilt:R} " +
            $"ball_guard={ballTolerance:R} axis_guard={axisTolerance:R} failures={failures.Count}\n" + string.Join('\n', failures));
        Assert.True(failures.Count == 0, string.Join('\n', failures.Take(12)));

        static string Vector(Vector3 value)
        {
            return FormattableString.Invariant($"{value.X:R};{value.Y:R};{value.Z:R}");
        }

        static string Axes(Basis value)
        {
            return $"{Vector(value.X)}|{Vector(value.Y)}|{Vector(value.Z)}";
        }
    }

    private static float WorldFacing(Vector3 axis)
    {
        Vector2 planar = new(axis.X, axis.Z);
        Assert.True(planar.LengthSquared() > 1e-10f, "SETUP: anatomical heading has no horizontal projection.");
        return Mathf.Atan2(-axis.X, -axis.Z);
    }

    /// <summary>YAW-R1 baseline: provider reach must be expressed in the scaled skeleton's world frame.</summary>
    [Fact]
    public async Task Female_ScaledContainer_ReachUsesWorldRestLength()
    {
        const float reachTolerance = 0.001f;
        const float boundarySeparation = 0.002f;
        const string evidence = "res://temp/nav001-yaw-scale-candidate/run-01";
        var failures = new List<string>();
        foreach (float scale in new[] { 0.5f, 1f, 2f })
        {
            await using RigFixture fixture = await RigFixture.CreateAsync(Rig.Female);
            ScriptedPlantedStateSource source = fixture.UseScriptedSource();
            source.State = new PlantedFeetState(false, false);
            Node3D container = fixture.Actor.GetNode<Node3D>("Female");
            container.Scale = Vector3.One * scale;
            fixture.Right.ReachMarginMetres = 0f;
            fixture.Right.ResetPin();
            _ = await fixture.StepAsync(8);

            Skeleton3D skeleton = fixture.Skeleton;
            Transform3D world = skeleton.GlobalTransform;
            int upper = skeleton.FindBone("RightUpperLeg");
            int lower = skeleton.FindBone("RightLowerLeg");
            int foot = skeleton.FindBone("RightFoot");
            int toe = skeleton.FindBone("RightToes");
            Transform3D footRest = skeleton.GetBoneGlobalRest(foot);
            Vector3 lever = (footRest.AffineInverse() * skeleton.GetBoneGlobalRest(toe)).Origin;
            float worldChain = (world * skeleton.GetBoneGlobalRest(upper).Origin)
                .DistanceTo(world * skeleton.GetBoneGlobalRest(lower).Origin)
                + (world * skeleton.GetBoneGlobalRest(lower).Origin)
                .DistanceTo(world * footRest.Origin);
            source.State = new PlantedFeetState(false, true);
            FootPlantingTickDiagnostic acquisition = await fixture.NextDiagnosticAsync(fixture.Right);
            Vector3 anchor = acquisition.AnimatedBall;
            Vector3 initialFacing = acquisition.AnimatedTarget.Basis * lever;
            float capturedYaw = Facing(initialFacing);
            Vector3 initialUpper = world * skeleton.GetBoneGlobalPose(upper).Origin;
            Vector3 away = acquisition.AnimatedTarget.Origin - initialUpper;
            away.Y = 0f;
            Vector3 direction = -away.Normalized();
            Vector3 start = fixture.Actor.GlobalPosition;
            var rows = new StringBuilder("step,tick,metadata,phase,reach_release,continuity_reset,world_chain_m,animated_reach_m,demand_reach_m,actual_target_reach_m,postik_reach_m,pin_x,pin_z,post_ball_x,post_ball_z,target_heading_rad,post_heading_rad,actor_x,actor_z\n");
            int releaseCount = 0;
            bool sawReachableHold = false;
            bool sawUnreachableDemand = false;
            bool returnedToAnimation = false;
            for (int step = 0; step <= 25 + (int)Math.Ceiling(fixture.Right.BlendOutSeconds * Engine.PhysicsTicksPerSecond) + 6; step++)
            {
                bool planted = step <= 25;
                if (step is > 0 and <= 25)
                {
                    fixture.Actor.GlobalPosition = start + (direction * (0.004f * step));
                }

                if (!planted)
                {
                    source.State = new PlantedFeetState(false, false);
                }

                FootPlantingTickDiagnostic d = step == 0 ? acquisition : await fixture.NextDiagnosticAsync(fixture.Right);
                Transform3D currentWorld = skeleton.GlobalTransform;
                Vector3 nativeUpper = currentWorld * skeleton.GetBoneGlobalPose(upper).Origin;
                float animatedReach = d.AnimatedTarget.Origin.DistanceTo(nativeUpper);
                Vector3 facing = d.AnimatedTarget.Basis * lever;
                float currentYaw = Facing(facing);
                Basis held = new Basis(Vector3.Up, Mathf.AngleDifference(currentYaw, capturedYaw)) * d.AnimatedTarget.Basis;
                Vector3 desiredBall = new(anchor.X, d.AnimatedBall.Y, anchor.Z);
                Vector3 demandAnkle = desiredBall - (held * lever);
                float demandReach = demandAnkle.DistanceTo(nativeUpper);
                float actualReach = d.Target.Origin.DistanceTo(nativeUpper);
                Node3D targetNode = Assert.IsType<Node3D>(fixture.CharacterIK.RightFootIKTarget, exactMatch: false);
                float targetActuationError = targetNode.GlobalPosition.DistanceTo(d.Target.Origin);
                Vector3 finalAnkle = currentWorld * skeleton.GetBoneGlobalPose(foot).Origin;
                float finalReach = finalAnkle.DistanceTo(nativeUpper);
                Vector3 finalFacing = currentWorld.Basis * skeleton.GetBoneGlobalPose(foot).Basis * lever;
                float postYaw = Facing(finalFacing);
                releaseCount += d.ReachBlendOutStarted ? 1 : 0;
                _ = rows.AppendLine(FormattableString.Invariant($"{step},{d.Tick},{planted},{d.Phase},{d.ReachBlendOutStarted},{d.ContinuityReset},{worldChain:R},{animatedReach:R},{demandReach:R},{actualReach:R},{finalReach:R},{d.Pin.X:R},{d.Pin.Z:R},{d.PostIKBall.X:R},{d.PostIKBall.Z:R},{d.TargetHeading:R},{postYaw:R},{fixture.Actor.GlobalPosition.X:R},{fixture.Actor.GlobalPosition.Z:R}"));
                if (d.ContinuityReset || !d.PostIKCaptured)
                {
                    failures.Add($"scale {scale} step {step}: missing post-IK capture or unexpected pin reset.");
                }

                if (targetActuationError > 0.0001f)
                {
                    failures.Add(FormattableString.Invariant($"scale {scale} step {step}: SETUP provider target was not applied ({targetActuationError:R} m)."));
                }

                if (animatedReach > worldChain + reachTolerance)
                {
                    failures.Add(FormattableString.Invariant($"scale {scale} step {step}: SETUP native animation itself exceeds world chain ({animatedReach:R}>{worldChain:R})."));
                }

                if (planted && demandReach < worldChain - boundarySeparation && !sawUnreachableDemand)
                {
                    sawReachableHold = true;
                    if (d.Phase != FootPlantingPhase.Pinned || d.ReachBlendOutStarted)
                    {
                        failures.Add(FormattableString.Invariant($"scale {scale} step {step}: released a reachable pin; demand {demandReach:R} < world chain {worldChain:R}, phase {d.Phase}."));
                    }
                }

                if (planted && demandReach > worldChain + boundarySeparation)
                {
                    sawUnreachableDemand = true;
                    if (actualReach > worldChain + reachTolerance || d.Phase == FootPlantingPhase.Pinned)
                    {
                        failures.Add(FormattableString.Invariant($"scale {scale} step {step}: unreachable target retained; demand {demandReach:R}, target {actualReach:R}, world chain {worldChain:R}, phase {d.Phase}."));
                    }
                    if (d.Phase == FootPlantingPhase.Pinned && d.PostIKBall.DistanceTo(d.TargetBall) > reachTolerance)
                    {
                        failures.Add(FormattableString.Invariant($"scale {scale} step {step}: pinned final ball misses demanded ball by {d.PostIKBall.DistanceTo(d.TargetBall):R} m."));
                    }
                }

                if (d.Phase == FootPlantingPhase.Free && !planted)
                {
                    returnedToAnimation = d.Target.Origin.DistanceTo(d.AnimatedTarget.Origin) < reachTolerance;
                }
            }

            string directory = ProjectSettings.GlobalizePath(evidence);
            _ = Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"female-scale-{scale:0.0}.csv"), rows.ToString());
            if (!sawReachableHold || !returnedToAnimation || (scale == 0.5f && !sawUnreachableDemand))
            {
                failures.Add($"scale {scale}: SETUP incomplete: reachable={sawReachableHold}, unreachable={sawUnreachableDemand}, returned={returnedToAnimation}.");
            }

            if (releaseCount != (scale == 0.5f ? 1 : 0))
            {
                failures.Add($"scale {scale}: wrong-frame release count={releaseCount}; expected {(scale == 0.5f ? 1 : 0)} for this complete contact.");
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));

        static float Facing(Vector3 axis)
        {
            return Mathf.Atan2(-axis.X, -axis.Z);
        }
    }

    /// <summary>IK-TAC-002 and IK-TAC-003 on the real graph of the female rig.</summary>
    [Fact]
    public Task Female_Walk_SameTickIntervalsCrossfadesAndStopToIdle() => WalkAsync(Rig.Female, slicedSideStep: false);

    /// <summary>IK-TAC-002 sliced clip on the female rig: a side-step played from a start offset.</summary>
    [Fact]
    public Task Female_SlicedSideStep_FollowsSliceSourceTime() => WalkAsync(Rig.Female, slicedSideStep: true);

    /// <summary>IK-TAC-002 sliced clip on the male rig: a side-step played from a start offset.</summary>
    [Fact]
    public Task Male_SlicedSideStep_FollowsSliceSourceTime() => WalkAsync(Rig.Male, slicedSideStep: true);

    /// <summary>Leg-Feet IK Contract TR10: degenerate and near-extension pole fallback on the female rig.</summary>
    [Fact]
    public Task Female_PoleFallback_DegenerateAndNearFullExtension() => PoleFallbackAsync(Rig.Female);

    /// <summary>Leg-Feet IK Contract TR10: degenerate and near-extension pole fallback on the male rig.</summary>
    [Fact]
    public Task Male_PoleFallback_DegenerateAndNearFullExtension() => PoleFallbackAsync(Rig.Male);

    /// <summary>IK-003 TR8/TR9 continuity bounds on the female rig: long outages and teleports reset the pins.</summary>
    [Fact]
    public Task Female_LongGapOrTeleport_ResetsPins_ShortSkipContinues() => PinContinuityBoundsAsync(Rig.Female);

    /// <summary>IK-003 TR8/TR9 continuity bounds on the male rig: long outages and teleports reset the pins.</summary>
    [Fact]
    public Task Male_LongGapOrTeleport_ResetsPins_ShortSkipContinues() => PinContinuityBoundsAsync(Rig.Male);

    /// <summary>
    /// IK-003 TR8 on the female rig: while one character is inside its foot-provider stage, another character's
    /// provider is outside its own and does not advance; it advances only inside its own stage.
    /// </summary>
    [Fact]
    public async Task Female_OtherCharactersStage_DoesNotAdvanceThisProvider()
    {
        await using RigFixture fixture = await RigFixture.CreateAsync(Rig.Female);
        ScriptedPlantedStateSource source = fixture.UseScriptedSource();
        source.State = new PlantedFeetState(true, true);
        _ = await fixture.StepAsync(3);

        // A second character: its own skeleton with the same bones, its own provider, no CharacterIK of its own.
        Skeleton3D otherSkeleton = CopyBones(fixture.Skeleton);
        otherSkeleton.Name = "OtherCharacterSkeleton";
        fixture.Actor.GetParent().AddChild(otherSkeleton);
        otherSkeleton.GlobalPosition = fixture.Actor.GlobalPosition + new Vector3(3f, 0f, 0f);
        var other = new FootPlantingTargetProvider
        {
            Name = "OtherCharacterLeftFootPlanting",
            Side = LimbSide.Left,
            Skeleton = otherSkeleton,
            PlantedStateSource = source,
        };
        otherSkeleton.AddChild(other);
        _ = await fixture.StepAsync(1);

        ulong tick = Engine.GetPhysicsFrames();
        Assert.True(fixture.Left.LastDiagnostic.Tick == tick, "SETUP: this character's own stage did not run on this tick.");
        Assert.True(other.IsPlantingAvailable || other.TryGetTargetIntent(out _), "SETUP: the other character's provider is not available.");
        using (FootTargetResolutionStage.Scope stage = FootTargetResolutionStage.Enter(fixture.Skeleton))
        {
            Assert.True(other.TryGetTargetIntent(out IKTargetIntentResolution preview) && preview.ShouldApplyTargetTransform);
            Assert.True(other.LastDiagnostic.Tick == 0, $"The other character's provider advanced inside this character's stage (tick {other.LastDiagnostic.Tick}).");

            // A copied scope disposed twice leaves the enclosing span intact.
            using (FootTargetResolutionStage.Scope nested = FootTargetResolutionStage.Enter(otherSkeleton))
            {
                FootTargetResolutionStage.Scope copy = nested;
                copy.Dispose();
                copy.Dispose();
            }

            Assert.True(FootTargetResolutionStage.IsActiveFor(fixture.Skeleton), "A double-disposed nested scope closed the enclosing stage.");
            Assert.True(other.TryGetTargetIntent(out _));
            Assert.True(other.LastDiagnostic.Tick == 0, "The other character's provider advanced after its double-disposed scope.");
        }

        Assert.False(FootTargetResolutionStage.IsActiveFor(fixture.Skeleton), "The stage stayed open after its scope.");
        using (FootTargetResolutionStage.Enter(otherSkeleton))
        {
            Assert.True(other.TryGetTargetIntent(out _));
        }

        Assert.True(other.LastDiagnostic.Tick == tick, "The other character's provider did not advance inside its own stage.");
        Assert.Equal(FootPlantingPhase.Pinned, other.LastDiagnostic.Phase);

        // This character's pipeline keeps resolving on later ticks.
        FootPlantingTickDiagnostic next = await fixture.NextDiagnosticAsync(fixture.Left);
        Assert.Equal(Engine.GetPhysicsFrames(), next.Tick);
        Assert.True(next.PostIKCaptured);
        otherSkeleton.QueueFree();
    }

    /// <summary>
    /// The interrupted-crossfade guard judges each evaluation by the delta NPC navigation actually advanced the tree by.
    /// Under <see cref="Engine.TimeScale" /> 3 (0.05 s per tick), an Idle → side-step crossfade of 0.2 s read at 0.15 s
    /// completes within the next advance, so travelling back to Idle then chains legitimately and is not flagged,
    /// although one unscaled tick could not have finished it. Control: travelling from an Idle → walk-entry crossfade to
    /// travel early in the fade genuinely interrupts it and is flagged. The independent recorder agrees on both.
    /// </summary>
    [Fact]
    public async Task Female_TimeScaledChainedCrossfade_IsNotFlagged_WhileRealInterruptionIs()
    {
        const double timeScale = 3d;
        double previousTimeScale = Engine.TimeScale;
        try
        {
            await using RigFixture fixture = await RigFixture.CreateAsync(Rig.Female);
            AnimationGraphPlantedStateSource source = fixture.CharacterIK.GetNode<AnimationGraphPlantedStateSource>("PlantedStateSource");
            AnimationTree tree = fixture.Actor.GetNode<AnimationTree>("AnimationTree");
            AnimationNodeStateMachinePlayback playback = tree.Get("parameters/States/playback").As<AnimationNodeStateMachinePlayback>();
            AnimationGraphPlantedStateRecorder recorder = fixture.BindRecorder();
            Engine.TimeScale = timeScale;
            recorder.Attach();
            _ = await fixture.StepAsync(3);
            double advance = timeScale / Engine.PhysicsTicksPerSecond;
            double fixedTick = 1d / Engine.PhysicsTicksPerSecond;
            Assert.Equal(advance, source.LastAdvanceDeltaSeconds, 9);

            ChainResult completed = await ChainDuringCrossfadeAsync(fixture, playback, source, recorder, "NPCSideStepR", "NPCIdle", forceNext: false, remaining => remaining > fixedTick + 1e-6 && remaining <= advance + 1e-6);
            Assert.True(
                AnimationGraphWeightModel.InterruptsCrossfade(true, completed.ReadPosition, completed.Length, fixedTick, true, true, true),
                $"SETUP: a fixed 1/60 s tick would not have flagged the chain at {completed.ReadPosition}/{completed.Length}.");
            Assert.False(completed.RecorderInterrupted, $"SETUP: the recorder saw an interruption in the completing chain {completed.Chain}.");
            Assert.False(completed.SourceFlagged, $"The source flagged a crossfade that completed within the {advance} s advance and chained: {completed.Chain}.");

            ChainResult interrupted = await ChainDuringCrossfadeAsync(fixture, playback, source, recorder, "NPCI2FR", "NPCWalkF", forceNext: true, remaining => remaining > advance + 0.02d);
            Assert.True(interrupted.RecorderInterrupted, $"SETUP: the recorder saw no interruption at {interrupted.ReadPosition}/{interrupted.Length}: {interrupted.Chain}.");
            Assert.True(interrupted.SourceFlagged, $"The source missed a genuine interruption: {interrupted.Chain}.");
            recorder.Detach();
            WriteEvidence(
                "Female-time-scaled-crossfade.txt",
                FormattableString.Invariant(
                    $"time_scale={timeScale} advance={advance:0.#####}\ncompleting: read={completed.ReadPosition:0.#####}/{completed.Length:0.#####} flagged={completed.SourceFlagged} recorder={completed.RecorderInterrupted} {completed.Chain}\ninterrupting: read={interrupted.ReadPosition:0.#####}/{interrupted.Length:0.#####} flagged={interrupted.SourceFlagged} recorder={interrupted.RecorderInterrupted} {interrupted.Chain}"));
        }
        finally
        {
            Engine.TimeScale = previousTimeScale;
        }
    }

    /// <summary>
    /// Travels Idle → <paramref name="first" />, then to <paramref name="second" /> on the first read whose remaining
    /// crossfade satisfies <paramref name="trigger" />; returns whether the source and the recorder flagged the chaining
    /// evaluation.
    /// </summary>
    private static async Task<ChainResult> ChainDuringCrossfadeAsync(
        RigFixture fixture,
        AnimationNodeStateMachinePlayback playback,
        AnimationGraphPlantedStateSource source,
        AnimationGraphPlantedStateRecorder recorder,
        string first,
        string second,
        bool forceNext,
        Func<double, bool> trigger)
    {
        const string idle = "NPCIdle";
        for (int tick = 0; tick < 120 && !(playback.GetCurrentNode() == idle && playback.GetFadingFromNode().IsEmpty); tick++)
        {
            _ = await fixture.StepAsync(1);
        }

        Assert.True(playback.GetCurrentNode() == idle && playback.GetFadingFromNode().IsEmpty, "SETUP: the NPC did not settle in Idle.");
        playback.Travel(first);
        for (int tick = 0; tick < 60; tick++)
        {
            _ = await fixture.StepAsync(1);
            double position = playback.GetFadingPosition();
            double length = playback.GetFadingLength();
            if (playback.GetCurrentNode() != first || playback.GetFadingFromNode() != idle || !trigger(length - position))
            {
                continue;
            }

            int flaggedBefore = source.InterruptedCrossfadeTicks;
            playback.Travel(second);
            if (forceNext)
            {
                playback.Next();
            }

            _ = await fixture.StepAsync(1);
            ulong evaluated = source.EvaluationTick;
            PlantedStateSample? sample = recorder.Samples.LastOrDefault(candidate => candidate.Tick == evaluated);
            Assert.True(sample is not null, $"SETUP: the recorder has no sample for tick {evaluated}.");
            Assert.True(
                sample.Graph.Current == second && sample.Graph.Fading == first,
                $"SETUP: the travel to {second} did not chain from {first}: {sample.Graph.Describe()}.");
            return new ChainResult(source.InterruptedCrossfadeTicks != flaggedBefore, sample.Graph.Interruption is not null, position, length, sample.Graph.Describe());
        }

        throw new InvalidOperationException($"SETUP: the Idle → {first} crossfade never reached the trigger window.");
    }

    private static async Task PinContinuityBoundsAsync(Rig rig)
    {
        await using RigFixture fixture = await RigFixture.CreateAsync(rig);
        ScriptedPlantedStateSource source = fixture.UseScriptedSource();
        source.State = new PlantedFeetState(false, false);
        _ = await fixture.StepAsync(5);
        source.State = new PlantedFeetState(true, true);
        (FootPlantingTickDiagnostic left, FootPlantingTickDiagnostic right) = await fixture.StepAsync(3);
        int gapTicks = (int)Math.Ceiling(fixture.Left.MaxContinuationGapSeconds * Engine.PhysicsTicksPerSecond);
        Assert.True(gapTicks > 4, $"{rig}: SETUP: the continuation gap {fixture.Left.MaxContinuationGapSeconds} s is too short for a short skip.");

        // Short skip: the stage misses three ticks while the actor drifts 3 cm; both pins are kept.
        (Vector3 leftPin, Vector3 rightPin) = (left.Pin, right.Pin);
        int resetsBefore = fixture.Left.ContinuityResetCount + fixture.Right.ContinuityResetCount;
        fixture.CharacterIK.Active = false;
        Vector3 origin = fixture.Actor.GlobalPosition;
        for (int tick = 1; tick <= 3; tick++)
        {
            fixture.Actor.GlobalPosition = origin + new Vector3(0.01f * tick, 0f, 0f);
            _ = await fixture.StepAsync(1);
        }

        fixture.CharacterIK.Active = true;
        (left, right) = await fixture.StepAsync(1);
        foreach ((FootPlantingTickDiagnostic resumed, Vector3 before) in new[] { (left, leftPin), (right, rightPin) })
        {
            Assert.False(resumed.ContinuityReset, $"{rig}: {resumed.Side} pin was reset by a short skip.");
            Assert.Equal(FootPlantingPhase.Pinned, resumed.Phase);
            Assert.True(resumed.Pin.IsEqualApprox(before), $"{rig}: {resumed.Side} pin changed across a short skip ({before} → {resumed.Pin}).");
            Assert.True(
                Planar(resumed.PostIKBall).DistanceTo(Planar(before)) <= PlanarHoldToleranceMetres,
                $"{rig}: {resumed.Side} ball {resumed.PostIKBall} is off its kept pin {before} after a short skip.");
        }

        Assert.Equal(resetsBefore, fixture.Left.ContinuityResetCount + fixture.Right.ContinuityResetCount);

        // Long outage: CharacterIK is off for longer than the continuation gap while the actor creeps 30 cm (inside
        // reach). On resume both feet pin at their current animated positions instead of popping from stale pins.
        fixture.CharacterIK.Active = false;
        origin = fixture.Actor.GlobalPosition;
        for (int tick = 1; tick <= gapTicks + 5; tick++)
        {
            fixture.Actor.GlobalPosition = origin + new Vector3(0f, 0f, -0.3f * Math.Min(1f, tick / 10f));
            _ = await fixture.StepAsync(1);
        }

        fixture.CharacterIK.Active = true;
        (left, right) = await fixture.StepAsync(1);
        AssertPinnedAtAnimated(rig, "long outage", left, right);

        // Teleport while IK was off: a two-tick outage, but the skeleton moved 2 m, beyond leg reach.
        fixture.CharacterIK.Active = false;
        _ = await fixture.StepAsync(1);
        fixture.Actor.GlobalPosition += new Vector3(2f, 0f, 0f);
        _ = await fixture.StepAsync(1);
        fixture.CharacterIK.Active = true;
        (left, right) = await fixture.StepAsync(1);
        AssertPinnedAtAnimated(rig, "teleport while IK was off", left, right);

        // Teleport with IK running: one tick, 2 m.
        fixture.Actor.GlobalPosition += new Vector3(-2f, 0f, 0f);
        (left, right) = await fixture.StepAsync(1);
        AssertPinnedAtAnimated(rig, "teleport with IK running", left, right);

        // Normal ticks afterwards continue the new pins.
        (Vector3 newLeft, Vector3 newRight) = (left.Pin, right.Pin);
        (left, right) = await fixture.StepAsync(1);
        Assert.False(left.ContinuityReset || right.ContinuityReset, $"{rig}: a normal tick reset a pin.");
        Assert.True(left.Pin.IsEqualApprox(newLeft) && right.Pin.IsEqualApprox(newRight), $"{rig}: the recaptured pins did not hold.");
    }

    private static void AssertPinnedAtAnimated(Rig rig, string scenario, FootPlantingTickDiagnostic left, FootPlantingTickDiagnostic right)
    {
        foreach (FootPlantingTickDiagnostic resumed in new[] { left, right })
        {
            Assert.True(resumed.ContinuityReset, $"{rig}: {scenario}: {resumed.Side} pin was not reset.");
            Assert.False(resumed.ReachBlendOutStarted, $"{rig}: {scenario}: {resumed.Side} was clipped to reach instead of reset.");
            Assert.Equal(FootPlantingPhase.Pinned, resumed.Phase);
            Assert.True(
                Planar(resumed.Pin).DistanceTo(Planar(resumed.AnimatedBall)) <= 1e-5f,
                $"{rig}: {scenario}: {resumed.Side} pin {resumed.Pin} is not at the animated ball {resumed.AnimatedBall}.");
            Assert.True(
                Planar(resumed.PostIKBall).DistanceTo(Planar(resumed.AnimatedBall)) <= PlanarHoldToleranceMetres,
                $"{rig}: {scenario}: {resumed.Side} post-IK ball {resumed.PostIKBall} popped away from the animated {resumed.AnimatedBall}.");
        }
    }

    private static Skeleton3D CopyBones(Skeleton3D source)
    {
        var copy = new Skeleton3D();
        for (int bone = 0; bone < source.GetBoneCount(); bone++)
        {
            _ = copy.AddBone(source.GetBoneName(bone));
        }

        for (int bone = 0; bone < source.GetBoneCount(); bone++)
        {
            copy.SetBoneParent(bone, source.GetBoneParent(bone));
            copy.SetBoneRest(bone, source.GetBoneRest(bone));
        }

        copy.ResetBonePoses();
        return copy;
    }

    private static async Task BallJointModifierOrderAndSoleActuationAsync(Rig rig)
    {
        await using RigFixture fixture = await RigFixture.CreateAsync(rig);
        foreach ((FootPlantingTargetProvider provider, string prefix) in fixture.Providers)
        {
            Skeleton3D skeleton = fixture.Skeleton;
            int foot = skeleton.FindBone($"{prefix}Foot");
            int toe = skeleton.FindBone($"{prefix}Toes");
            Assert.True(toe >= 0 && skeleton.GetBoneParent(toe) == foot, $"{rig}: {prefix}Toes must be a child of {prefix}Foot.");
            Assert.True(provider.IsPlantingAvailable, $"{rig}: {prefix} planting must be available.");
            Assert.True(
                provider.BallInFoot.IsEqualApprox(skeleton.GetBoneRest(toe).Origin),
                $"{rig}: {prefix} ball {provider.BallInFoot} is not the toe rest origin {skeleton.GetBoneRest(toe).Origin} in foot space.");
        }

        Skeleton3D rigSkeleton = fixture.Skeleton;
        int sync = rigSkeleton.GetNode<Node>("FootTargetSyncController").GetIndex();
        int begin = rigSkeleton.GetNode<Node>("CharacterIKBeginStage").GetIndex();
        int providerStage = rigSkeleton.GetNode<Node>("CharacterIKFootProviderStage").GetIndex();
        foreach (string consumer in new[] { "LeftLegIKController", "LeftLegTwoBoneIKController", "CopyLeftFootRotation", "RightLegIKController", "RightLegTwoBoneIKController", "CopyRightFootRotation" })
        {
            Assert.True(
                sync < begin && begin < providerStage && providerStage < rigSkeleton.GetNode<Node>(consumer).GetIndex(),
                $"{rig}: modifier order sync({sync}) → begin({begin}) → provider({providerStage}) → {consumer}({rigSkeleton.GetNode<Node>(consumer).GetIndex()}) is violated.");
        }

        // Value-only: querying the provider outside the pipeline changes no target, actor, or skeleton state, and does
        // not advance or cache the provider's own per-tick state (the next in-stage step is still this tick's).
        Node3D leftTarget = fixture.CharacterIK.LeftFootIKTarget!;
        Transform3D targetBefore = leftTarget.GlobalTransform;
        Transform3D actorBefore = fixture.Actor.GlobalTransform;
        Transform3D poseBefore = rigSkeleton.GetBonePose(rigSkeleton.FindBone("LeftFoot"));
        FootPlantingTickDiagnostic diagnosticBefore = fixture.Left.LastDiagnostic;
        Assert.True(fixture.Left.TryGetTargetIntent(out IKTargetIntentResolution resolution) && resolution.ShouldApplyTargetTransform);
        Assert.True(fixture.Left.LastDiagnostic == diagnosticBefore, $"{rig}: an out-of-stage query advanced the provider.");
        Assert.True(NAV001ArrivalOutcomeIntegrationTests.ExactlyEqual(targetBefore, leftTarget.GlobalTransform), $"{rig}: the provider wrote its target.");
        Assert.True(NAV001ArrivalOutcomeIntegrationTests.ExactlyEqual(actorBefore, fixture.Actor.GlobalTransform), $"{rig}: the provider moved the actor.");
        Assert.True(NAV001ArrivalOutcomeIntegrationTests.ExactlyEqual(poseBefore, rigSkeleton.GetBonePose(rigSkeleton.FindBone("LeftFoot"))), $"{rig}: the provider wrote the skeleton.");

        // Sole actuation: after a pipeline tick the target CharacterIK applied is exactly the provider's value,
        // resolved on that tick inside the modifier stage.
        FootPlantingTickDiagnostic recorded = await fixture.NextDiagnosticAsync(fixture.Left);
        Assert.Equal(Engine.GetPhysicsFrames(), recorded.Tick);
        Transform3D applied = leftTarget.GlobalTransform;
        Assert.True(
            applied.Origin.DistanceTo(recorded.Target.Origin) < 1e-5f,
            $"{rig}: applied target {applied.Origin} differs from the provider target {recorded.Target.Origin}.");
        Assert.True(recorded.PostIKCaptured, $"{rig}: post-IK ball was not captured in SkeletonUpdated.");
    }

    private static async Task MissingToeBoneFailsClosedAsync()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = Assert.IsType<Node3D>(
            LoadPackedScene(NAV001ArrivalOutcomeIntegrationTests.NavigationTestScenePath).Instantiate(),
            exactMatch: false);
        try
        {
            Node3D actor = root.GetNode<Node3D>(NAV001ArrivalOutcomeIntegrationTests.ActorNodeName);
            NAV001ArrivalOutcomeIntegrationTests.PrepareActorBeforeTreeEntry(actor);
            Skeleton3D skeleton = actor.GetNode<Skeleton3D>("Female/GeneralSkeleton");
            skeleton.SetBoneName(skeleton.FindBone("RightToes"), "RightToesRemovedForFixture");
            sceneTree.Root.AddChild(root);
            await WaitForPhysicsFramesAsync(sceneTree, 30);

            CharacterIK characterIK = actor.GetNode<CharacterIK>("CharacterIK");
            FootPlantingTargetProvider left = Assert.IsType<FootPlantingTargetProvider>(characterIK.LeftFootTargetIntentProvider);
            FootPlantingTargetProvider right = Assert.IsType<FootPlantingTargetProvider>(characterIK.RightFootTargetIntentProvider);
            foreach (FootPlantingTargetProvider provider in new[] { left, right })
            {
                Assert.False(provider.IsPlantingAvailable, $"{provider.Side} planting bound to a rig without RightToes.");
                Assert.False(provider.TryGetTargetIntent(out _), $"The {provider.Side} provider did not refuse.");
            }

            // No per-foot degraded mode: with no fallback on the NPC, both leg solves stay off and follow animation.
            foreach (SkeletonModifier3D modifier in characterIK.LeftFootModifierGroup.Concat(characterIK.RightFootModifierGroup))
            {
                Assert.True(!modifier.Active || modifier.Influence == 0f, $"Modifier '{modifier.Name}' stayed active on the unbound rig.");
            }

            // The trajectory table requires both toe bones too, so navigation latches its binding off.
            Assert.False(
                actor.GetNode<NPCNavigation>("Navigation").IsRuntimeBindingActive,
                "Navigation bound a rig without RightToes.");
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    private static async Task ScriptedIntervalsAsync(Rig rig)
    {
        await using RigFixture fixture = await RigFixture.CreateAsync(rig);
        ScriptedPlantedStateSource source = fixture.UseScriptedSource();
        Skeleton3D skeleton = fixture.Skeleton;
        float rightChain = ChainLength(skeleton, "Right");

        // Swing, then interval start: the pin is captured where the ball is, without a jump.
        source.State = new PlantedFeetState(false, false);
        _ = await fixture.StepAsync(10);
        source.State = new PlantedFeetState(true, true);
        (FootPlantingTickDiagnostic left, FootPlantingTickDiagnostic right) = await fixture.StepAsync(1);
        foreach (FootPlantingTickDiagnostic captured in new[] { left, right })
        {
            Assert.Equal(FootPlantingPhase.Pinned, captured.Phase);
            Assert.True(captured.TargetBall.DistanceTo(captured.AnimatedBall) < 1e-5f, $"{rig}: {captured.Side} pin capture jumped.");
            Assert.True(Planar(captured.PostIKBall).DistanceTo(Planar(captured.Pin)) <= PlanarHoldToleranceMetres, $"{rig}: {captured.Side} post-IK ball is off its new pin.");
        }

        // Double support while the actor drifts 4 cm and the source feet float up 1 cm and sink 2 cm: planar hold,
        // animated height.
        Vector3 origin = fixture.Actor.GlobalPosition;
        for (int tick = 1; tick <= 30; tick++)
        {
            float height = tick <= 10 ? 0.001f * tick : 0.01f - (0.0015f * (tick - 10));
            fixture.Actor.GlobalPosition = origin + new Vector3(0.004f * Math.Min(tick, 10), height, 0f);
            (left, right) = await fixture.StepAsync(1);
            foreach (FootPlantingTickDiagnostic held in new[] { left, right })
            {
                Assert.Equal(FootPlantingPhase.Pinned, held.Phase);
                Assert.True(
                    Planar(held.PostIKBall).DistanceTo(Planar(held.Pin)) <= PlanarHoldToleranceMetres,
                    $"{rig}: {held.Side} tick {tick}: post-IK ball {held.PostIKBall} slipped from pin {held.Pin}.");
                Assert.True(
                    Math.Abs(held.PostIKBall.Y - held.AnimatedBall.Y) <= HeightToleranceMetres,
                    $"{rig}: {held.Side} tick {tick}: post-IK ball height {held.PostIKBall.Y} does not follow the animated {held.AnimatedBall.Y}.");
            }
        }

        // Skipped ticks: the provider stage does not run for three ticks; when it resumes, both pins are kept and the
        // balls are back on them without a jump to the animation.
        (Vector3 leftPinBeforeSkip, Vector3 rightPinBeforeSkip) = (left.Pin, right.Pin);
        fixture.CharacterIK.Active = false;
        _ = await fixture.StepAsync(3);
        fixture.CharacterIK.Active = true;
        (left, right) = await fixture.StepAsync(1);
        foreach ((FootPlantingTickDiagnostic resumed, Vector3 before) in new[] { (left, leftPinBeforeSkip), (right, rightPinBeforeSkip) })
        {
            Assert.Equal(FootPlantingPhase.Pinned, resumed.Phase);
            Assert.True(resumed.Pin.IsEqualApprox(before), $"{rig}: {resumed.Side} pin changed across skipped ticks ({before} → {resumed.Pin}).");
            Assert.True(
                Planar(resumed.PostIKBall).DistanceTo(Planar(before)) <= PlanarHoldToleranceMetres,
                $"{rig}: {resumed.Side} ball {resumed.PostIKBall} is off its kept pin {before} after skipped ticks.");
        }

        // Left lifts off while the right stays planted: the left blends back to its animation, the right is untouched.
        Vector3 rightPin = right.Pin;
        float initialOffset = Planar(left.PostIKBall).DistanceTo(Planar(left.AnimatedBall));
        Assert.True(initialOffset > 0.03f, $"{rig}: SETUP: the left offset {initialOffset} is too small to observe a blend-out.");
        // No single-tick jump (IK-UAC-003): a correct blend-out moves the ball's offset from its animated position by
        // offset(t) = O₀·(1 − smoothstep(t/T)); smoothstep's steepest slope is 1.5, so no tick may change the offset
        // by more than 1.5·|O₀|·Δt/T. The 0.5 mm allowance covers the two-bone solve's residual on the post-IK ball.
        source.State = new PlantedFeetState(false, true);
        Vector3 previousOffset = Planar(left.PostIKBall) - Planar(left.AnimatedBall);
        float maximumStep = (1.5f * initialOffset / Engine.PhysicsTicksPerSecond / fixture.Left.BlendOutSeconds) + LiftOffSolveToleranceMetres;
        int blendTicks = (int)Math.Ceiling(fixture.Left.BlendOutSeconds * Engine.PhysicsTicksPerSecond);
        for (int tick = 1; tick <= blendTicks + 2; tick++)
        {
            (left, right) = await fixture.StepAsync(1);
            Vector3 offset = Planar(left.PostIKBall) - Planar(left.AnimatedBall);
            float step = offset.DistanceTo(previousOffset);
            Assert.True(
                step <= maximumStep,
                $"{rig}: left lift-off tick {tick} changed the ball offset by {step * 1000:0.###} mm, above the smoothstep bound {maximumStep * 1000:0.###} mm.");
            previousOffset = offset;
            Assert.Equal(FootPlantingPhase.Pinned, right.Phase);
            Assert.True(right.Pin.IsEqualApprox(rightPin), $"{rig}: the right pin changed while the left foot released.");
        }

        Assert.Equal(FootPlantingPhase.Free, left.Phase);
        Assert.True(
            left.PostIKBall.DistanceTo(left.AnimatedBall) <= PlanarHoldToleranceMetres,
            $"{rig}: the left blend-out did not end on the animated target ({left.PostIKBall} vs {left.AnimatedBall}).");

        // The actor walks 40 cm away from the planted right foot: the pin leaves reach and blends out without
        // hyperextension or actor movement, and is not recaptured within the same interval.
        int releasesBefore = fixture.Right.ReachBlendOutCount;
        Vector3 start = fixture.Actor.GlobalPosition;
        for (int tick = 1; tick <= 40; tick++)
        {
            Vector3 scripted = start + new Vector3(0f, 0f, -0.01f * tick);
            fixture.Actor.GlobalPosition = scripted;
            (_, right) = await fixture.StepAsync(1);
            Assert.True(fixture.Actor.GlobalPosition == scripted, $"{rig}: tick {tick}: the actor moved away from its scripted position.");
            Assert.True(
                fixture.LastRightLegExtension <= rightChain * ReachToleranceRatio,
                $"{rig}: tick {tick}: right leg extension {fixture.LastRightLegExtension} exceeds its chain {rightChain}.");
        }

        Assert.Equal(releasesBefore + 1, fixture.Right.ReachBlendOutCount);
        for (int tick = 0; tick < 20; tick++)
        {
            (_, right) = await fixture.StepAsync(1);
            Assert.NotEqual(FootPlantingPhase.Pinned, right.Phase);
        }

        source.State = new PlantedFeetState(false, false);
        _ = await fixture.StepAsync(3);
        source.State = new PlantedFeetState(false, true);
        (_, right) = await fixture.StepAsync(1);
        Assert.Equal(FootPlantingPhase.Pinned, right.Phase);
    }

    private static async Task WalkAsync(Rig rig, bool slicedSideStep)
    {
        await using RigFixture fixture = await RigFixture.CreateAsync(rig);
        NPCAnimationTreeDriver driver = fixture.Actor.GetNode<NPCNavigation>("Navigation").AnimationDriver!;
        bool sliced = false;
        AnimationGraphPlantedStateRecorder recorder = fixture.BindRecorder();
        recorder.Attach();
        _ = await fixture.StepAsync(30);

        ulong issue = Engine.GetPhysicsFrames() + 1;
        NavigationRequestIdentity identity = default;
        bool returnRequested = false;
        int idleSettleTicks = 0;
        Transform3D stationaryActor = fixture.Actor.GlobalTransform;
        if (slicedSideStep)
        {
            // No competing NAV plan: its normal physics callback still advances the real native tree exactly once,
            // but its movement stage has zero displacement. Request the seek BEFORE that callback consumes it.
            fixture.Navigation.DeactivateMovementAuthority();
            double? entryFade = driver.AuthoredCrossfade("NPCIdle", SlicedState);
            Assert.True(entryFade is > 0d, $"{rig}: SETUP: the authored Idle → SideStepR crossfade is missing.");
            sliced = driver.TryRequestTransition(SlicedState, SliceStartOffset, entryFade.Value);
            Assert.True(sliced, $"{rig}: SETUP: the native .3 s slice request was refused.");
        }
        else
        {
            Transform3D destination = new(Basis.Identity, new Vector3(0f, 0f, -1.5f));
            Assert.Equal(NavigationDestinationResult.Accepted, fixture.Navigation.SetDestination(destination));
            identity = fixture.Navigation.CurrentStatus.ActiveRequest!.Value;
        }

        ulong terminal = 0;
        long evaluationsBefore = driver.EvaluationCount;
        long preEvaluationsBefore = driver.PreEvaluationCount;
        for (int tick = 0; tick < RequestTimeoutTicks && terminal == 0; tick++)
        {
            _ = await fixture.StepAsync(1);
            if (slicedSideStep)
            {
                Assert.Equal(evaluationsBefore + tick + 1, driver.EvaluationCount);
                PlantedStateSample latest = recorder.Samples[^1];
                if (!returnRequested && latest.Graph.Current == SlicedState && latest.Graph.Fading is null
                    && latest.Contributions.Any(contribution => contribution.State == SlicedState
                        && contribution.SourceTimeSeconds >= driver.ClipLength(SlicedState) - 0.25d))
                {
                    double? exitFade = driver.AuthoredCrossfade(SlicedState, "NPCIdle");
                    Assert.True(exitFade is > 0d, $"{rig}: SETUP: the authored SideStepR → Idle crossfade is missing.");
                    Assert.True(driver.TryRequestTransition("NPCIdle", 4.35d, exitFade.Value),
                        $"{rig}: SETUP: native return to Idle was refused.");
                    returnRequested = true;
                }

                idleSettleTicks = returnRequested && latest.Graph.Current == "NPCIdle" && latest.Graph.Fading is null
                    ? idleSettleTicks + 1 : 0;
                if (idleSettleTicks >= 8)
                {
                    terminal = Engine.GetPhysicsFrames();
                }
            }
            else if (NAV001ArrivalOutcomeIntegrationTests.FindOutcome(fixture.Navigation, identity) is not null)
            {
                terminal = Engine.GetPhysicsFrames();
            }
        }

        recorder.Detach();
        Assert.True(terminal != 0, $"{rig}: SETUP: the request never ended.");
        var samples = recorder.Samples.ToDictionary(sample => sample.Tick);
        if (slicedSideStep)
        {
            WriteSliceTrace(rig, driver, fixture, recorder, issue, terminal, stationaryActor);
            NPCClipTransitionRecord entered = Assert.Single(driver.IssuedTransitions, transition =>
                transition.PhysicsTick >= issue && transition.To == SlicedState);
            Assert.Equal(SliceStartOffset, entered.EntryPositionSeconds, 8);
            Assert.True(returnRequested, $"{rig}: SETUP: the native SideStepR did not return to Idle.");
            PlantedStateSample first = samples[issue];
            double firstTime = Assert.Single(first.Contributions, contribution => contribution.State == SlicedState).SourceTimeSeconds;
            // The entry tick's native seek overrides elapsed time; only the NEXT final evaluation adds one delta.
            double followingTime = Assert.Single(samples[issue + 1].Contributions,
                contribution => contribution.State == SlicedState).SourceTimeSeconds;
            Assert.InRange(Math.Abs(firstTime - SliceStartOffset), 0d, 0.0002d);
            Assert.InRange(Math.Abs(followingTime - firstTime - (1d / Engine.PhysicsTicksPerSecond)), 0d, 0.0002d);
            Assert.Equal(preEvaluationsBefore + 2, driver.PreEvaluationCount);
        }

        float peakReach = 0f;
        int kneeTicks = 0;
        int nearExtensionKneeTicks = 0;
        int crossfadeTicks = 0;
        int stopToIdlePinnedTicks = 0;
        int sliceTicks = 0;
        int releasesAboveThreshold = 0;
        var failures = new List<string>();
        foreach ((FootPlantingTargetProvider provider, string prefix) in fixture.Providers)
        {
            StandingLocomotionAnatomicalSide side = provider.Side == LimbSide.Left
                ? StandingLocomotionAnatomicalSide.Left
                : StandingLocomotionAnatomicalSide.Right;
            Dictionary<ulong, FootPlantingTickDiagnostic> diagnostics = provider == fixture.Left ? fixture.LeftHistory : fixture.RightHistory;
            Vector3? pin = null;
            bool wasPlanted = false;
            Dictionary<ulong, KneeSample> knees = provider == fixture.Left ? fixture.LeftKnees : fixture.RightKnees;

            // The crude-stop snap happens on the terminal tick; the stop → Idle crossfade completes before it.
            for (ulong tick = issue; tick < terminal; tick++)
            {
                if (!samples.TryGetValue(tick, out PlantedStateSample? sample) || !diagnostics.TryGetValue(tick, out FootPlantingTickDiagnostic diagnostic))
                {
                    failures.Add($"{prefix} tick {tick}: missing recorder sample or diagnostic.");
                    continue;
                }

                bool planted = sample.Planted.IsPlanted(side);
                peakReach = Math.Max(peakReach, diagnostic.ReachUsage);
                if (diagnostic.ReachBlendOutStarted)
                {
                    failures.Add(FormattableString.Invariant($"{prefix} tick {tick}: reach release mid-plant (usage {diagnostic.ReachUsage:0.####})."));
                }

                // Stable knee: the post-IK knee bends in front of the leg, and its bend direction never reverses
                // between ticks. A flip reverses the bend plane (dot ≈ −1); no real knee turns 90° in one 60 Hz tick,
                // so dot ≤ 0 is a flip. Below a 5 mm knee offset the bend direction is undefined, so such ticks are
                // not judged.
                if (knees.TryGetValue(tick, out KneeSample knee) && knee.Offset >= KneeDirectionMinimumOffsetMetres)
                {
                    kneeTicks++;
                    nearExtensionKneeTicks += diagnostic.ReachUsage >= 0.99f ? 1 : 0;
                    if (knee.Direction.Dot(knee.AvatarForward) <= 0f)
                    {
                        failures.Add(FormattableString.Invariant($"{prefix} tick {tick}: knee bends behind the leg (forward dot {knee.Direction.Dot(knee.AvatarForward):0.###}, reach {diagnostic.ReachUsage:0.####})."));
                    }

                    if (knees.TryGetValue(tick - 1, out KneeSample previousKnee)
                        && previousKnee.Offset >= KneeDirectionMinimumOffsetMetres
                        && previousKnee.Direction.Dot(knee.Direction) <= 0f)
                    {
                        failures.Add(FormattableString.Invariant($"{prefix} tick {tick}: knee direction flipped (dot {previousKnee.Direction.Dot(knee.Direction):0.###}, reach {diagnostic.ReachUsage:0.####})."));
                    }
                }

                if (planted != diagnostic.MetadataPlanted)
                {
                    failures.Add($"{prefix} tick {tick}: runtime planted={diagnostic.MetadataPlanted}, oracle={planted} [{string.Join("; ", sample.Contributions)}].");
                }

                crossfadeTicks += sample.Graph.Fading is not null ? 1 : 0;
                // Stop → Idle: the Idle state is entered from an exit, by crossfade or (in a graph that authors no
                // crossfade) by a direct switch, while this foot stays pinned on both ticks.
                bool enteringIdleFromExit = sample.Graph.Current == "NPCIdle"
                    && (IsExit(sample.Graph.Fading)
                        || (samples.TryGetValue(tick - 1, out PlantedStateSample? before) && IsExit(before.Graph.Current)));
                stopToIdlePinnedTicks += enteringIdleFromExit
                    && diagnostic.Phase == FootPlantingPhase.Pinned
                    && diagnostics.TryGetValue(tick - 1, out FootPlantingTickDiagnostic beforeDiagnostic)
                    && beforeDiagnostic.Phase == FootPlantingPhase.Pinned
                    && beforeDiagnostic.Pin.IsEqualApprox(diagnostic.Pin)
                        ? 1
                        : 0;
                sliceTicks += sample.Contributions.Any(contribution => contribution.State == "NPCSideStepR" && contribution.SourceTimeSeconds >= SliceStartOffset - 1e-6) ? 1 : 0;
                if (planted && wasPlanted && diagnostic.Phase == FootPlantingPhase.Pinned && pin is { } held)
                {
                    if (!diagnostic.Pin.IsEqualApprox(held))
                    {
                        failures.Add($"{prefix} tick {tick}: pin recaptured within a planted interval ({held} → {diagnostic.Pin}) [{sample.Graph.Describe()}].");
                    }

                    if (Planar(diagnostic.PostIKBall).DistanceTo(Planar(held)) > PlanarHoldToleranceMetres)
                    {
                        failures.Add(FormattableString.Invariant($"{prefix} tick {tick}: pinned ball {diagnostic.PostIKBall} is {Planar(diagnostic.PostIKBall).DistanceTo(Planar(held)) * 1000:0.###} mm off its pin [{sample.Graph.Describe()}]."));
                    }
                }

                if (!planted && wasPlanted)
                {
                    // A release is decided by some contribution above the threshold that marks this foot swinging.
                    double total = sample.Contributions.Sum(contribution => contribution.RawWeight);
                    bool decided = sample.Contributions.Any(contribution =>
                        contribution.RawWeight / total > PlantedCombinationRule.WeightThreshold
                        && !contribution.Intervals.IsPlanted(side, contribution.SourceTimeSeconds));
                    releasesAboveThreshold += decided ? 1 : 0;
                    if (!decided || diagnostic.Phase == FootPlantingPhase.Pinned)
                    {
                        failures.Add($"{prefix} tick {tick}: release not decided above the threshold or not applied (phase {diagnostic.Phase}).");
                    }
                }

                pin = diagnostic.Phase == FootPlantingPhase.Pinned ? diagnostic.Pin : null;
                wasPlanted = planted;
            }
        }

        string graphPath = string.Join(" → ", samples.Values
            .Where(sample => sample.Tick >= issue && sample.Tick < terminal)
            .Select(sample => sample.Graph.Fading is null ? sample.Graph.Current : $"{sample.Graph.Fading}⇒{sample.Graph.Current}")
            .Aggregate(new List<string>(), (states, state) =>
            {
                if (states.Count == 0 || states[^1] != state)
                {
                    states.Add(state);
                }

                return states;
            }));
        string summary = FormattableString.Invariant(
            $"{rig} {(slicedSideStep ? "sliced-side-step" : "walk")}: ticks {issue}..{terminal - 1} peak_reach_usage={peakReach:0.####} knee_ticks_judged={kneeTicks} near_extension_knee_ticks={nearExtensionKneeTicks} failures={failures.Count}");
        if (slicedSideStep)
        {
            WriteSliceFile($"{rig}-sliced-side-step.txt", summary + "\n" + graphPath + "\n" + string.Join('\n', failures));
        }
        else
        {
            WriteEvidence($"{rig}-walk.txt", summary + "\n" + graphPath + "\n" + string.Join('\n', failures));
        }
        Assert.True(failures.Count == 0, $"{summary}\n{string.Join('\n', failures.Take(40))}\n{graphPath}");
        Assert.True(kneeTicks > 0, $"{summary}: SETUP: no knee direction was judged.");
        Assert.True(crossfadeTicks > 0, $"{rig}: SETUP: the walk exercised no crossfade: {graphPath}.");
        Assert.True(releasesAboveThreshold > 0, $"{rig}: SETUP: the walk released no foot: {graphPath}.");
        if (slicedSideStep)
        {
            Assert.True(sliced && sliceTicks > 0, $"{rig}: SETUP: the sliced side-step never played.");
        }
        else
        {
            Assert.True(stopToIdlePinnedTicks > 0, $"{rig}: SETUP: no foot stayed pinned into Idle from the exit: {graphPath}.");
        }
    }

    private static void WriteSliceTrace(Rig rig, NPCAnimationTreeDriver driver, RigFixture fixture,
        AnimationGraphPlantedStateRecorder recorder, ulong issue, ulong terminal, Transform3D stationaryActor)
    {
        var rows = new StringBuilder("tick,clip,fading,contributions,metadata_left,metadata_right,provider_left,provider_right,phase_left,phase_right,release_left,release_right,pin_left_x,pin_left_z,pin_right_x,pin_right_z,post_ball_left_x,post_ball_left_z,post_ball_right_x,post_ball_right_z,actor_stationary\n");
        int records = 0;
        foreach (PlantedStateSample sample in recorder.Samples.Where(sample => sample.Tick >= issue - 1 && sample.Tick <= terminal))
        {
            Assert.True(fixture.LeftHistory.TryGetValue(sample.Tick, out FootPlantingTickDiagnostic left),
                $"{rig}: SETUP: missing left provider diagnostic on tick {sample.Tick}.");
            Assert.True(fixture.RightHistory.TryGetValue(sample.Tick, out FootPlantingTickDiagnostic right),
                $"{rig}: SETUP: missing right provider diagnostic on tick {sample.Tick}.");
            string contributions = string.Join('|', sample.Contributions.Select(contribution =>
                FormattableString.Invariant($"{contribution.State}@{contribution.SourceTimeSeconds:R}:w{contribution.RawWeight:R}")));
            bool stationary = NAV001ArrivalOutcomeIntegrationTests.ExactlyEqual(stationaryActor, sample.Actor);
            _ = rows.AppendLine(FormattableString.Invariant($"{sample.Tick},{sample.Graph.Current},{sample.Graph.Fading},{contributions},{sample.Planted.Left},{sample.Planted.Right},{left.MetadataPlanted},{right.MetadataPlanted},{left.Phase},{right.Phase},{left.ReachBlendOutStarted},{right.ReachBlendOutStarted},{left.Pin.X:R},{left.Pin.Z:R},{right.Pin.X:R},{right.Pin.Z:R},{left.PostIKBall.X:R},{left.PostIKBall.Z:R},{right.PostIKBall.X:R},{right.PostIKBall.Z:R},{stationary}"));
            records++;
        }

        WriteSliceFile($"{rig}-sliced-side-step-ticks.csv", rows.ToString());
        Assert.Equal((long)(terminal - issue + 2), records);
        Assert.Equal(0, recorder.UnpairedTicks);
        Assert.All(recorder.Samples.Where(sample => sample.Tick >= issue - 1 && sample.Tick <= terminal),
            sample => Assert.True(NAV001ArrivalOutcomeIntegrationTests.ExactlyEqual(stationaryActor, sample.Actor),
                $"{rig}: no-displacement movement stage changed the actor at tick {sample.Tick}."));
        string transitions = string.Join('\n', driver.IssuedTransitions
            .Where(transition => transition.PhysicsTick >= issue && transition.PhysicsTick <= terminal)
            .Select(transition => FormattableString.Invariant($"{transition.PhysicsTick}: {transition.From} → {transition.To}, requested_entry_s={transition.EntryPositionSeconds:R}, authored_fade_s={transition.CrossfadeSeconds:R}")));
        WriteSliceFile($"{rig}-sliced-side-step-transitions.txt", transitions + "\n" +
            FormattableString.Invariant($"first_tick={issue} terminal_tick={terminal} last_recorded_tick={terminal} requested_slice_start_s={SliceStartOffset:R} first_expected_source_s={SliceStartOffset:R} observed_first_source_s={Assert.Single(recorder.Samples.Single(sample => sample.Tick == issue).Contributions, contribution => contribution.State == SlicedState).SourceTimeSeconds:R} observed_second_source_s={Assert.Single(recorder.Samples.Single(sample => sample.Tick == issue + 1).Contributions, contribution => contribution.State == SlicedState).SourceTimeSeconds:R} next_expected_delta_s={1d / Engine.PhysicsTicksPerSecond:R} observations={records} final_evaluations={terminal - issue + 1} authored_pre_evaluations=2 actor_stationary=True\n"));
    }

    private static void WriteSliceFile(string file, string text)
    {
        string directory = ProjectSettings.GlobalizePath("res://temp/nav001-yaw-slice-fixture-candidate/run-03");
        _ = Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, file), text);
    }

    private static async Task PoleFallbackAsync(Rig rig)
    {
        await using RigFixture fixture = await RigFixture.CreateAsync(rig);
        Skeleton3D skeleton = fixture.Skeleton;
        Basis skeletonBasis = skeleton.GlobalTransform.Basis.Orthonormalized();
        Vector3 up = skeletonBasis.Column1.Normalized();
        Vector3 avatarForward = skeletonBasis * Vector3.Back;
        Vector3 forward = new Vector3(avatarForward.X, 0f, avatarForward.Z).Normalized();
        Vector3 lateral = up.Cross(forward).Normalized();
        Transform3D toSkeleton = skeleton.GlobalTransform.AffineInverse();
        var failures = new List<string>();
        int cases = 0;
        foreach (string prefix in new[] { "Left", "Right" })
        {
            LegIKController controller = skeleton.GetNode<LegIKController>($"{prefix}LegIKController");
            Node3D footTarget = controller.FootTarget!;
            Node3D poleTarget = controller.PoleTarget!;
            int upperLeg = skeleton.FindBone($"{prefix}UpperLeg");
            int lowerLeg = skeleton.FindBone($"{prefix}LowerLeg");
            int foot = skeleton.FindBone($"{prefix}Foot");
            float chain = ChainLength(skeleton, prefix);
            Vector3 hip = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(upperLeg).Origin;

            // Near-full extension (95%–100% of the rest chain) straight down and reaching forward; the knee is placed on
            // the hip-foot line (exactly degenerate), then a few millimetres to centimetres behind it or to its side.
            foreach (float extension in new[] { 0.95f, 0.99f, 0.998f, 1f })
            {
                foreach (float lean in new[] { 0f, 0.35f, 0.9f })
                {
                    Vector3 legDirection = ((-up) + (forward * lean)).Normalized();
                    Vector3 footWorld = hip + (legDirection * chain * extension);
                    Vector3 midpoint = (hip + footWorld) * 0.5f;
                    Vector3 behind = Perpendicular(-forward, legDirection);
                    Vector3 side = Perpendicular(prefix == "Left" ? -lateral : lateral, legDirection);
                    Vector3 previous = Vector3.Zero;
                    foreach ((Vector3 direction, float magnitude, string label) in new[]
                    {
                        (behind, 0f, "degenerate"),
                        (behind, 0.003f, "3mm-behind"),
                        (behind, 0.03f, "30mm-behind"),
                        (side, 0.003f, "3mm-side"),
                        (side, 0.03f, "30mm-side"),
                    })
                    {
                        footTarget.GlobalTransform = new Transform3D(new Basis(lateral, up, forward), footWorld);
                        // The knee first, then the foot: posing the parent afterwards would move the child.
                        skeleton.SetBoneGlobalPose(
                            lowerLeg,
                            new Transform3D(skeleton.GetBoneGlobalRest(lowerLeg).Basis, toSkeleton * (midpoint + (direction * magnitude))));
                        skeleton.SetBoneGlobalPose(foot, new Transform3D(skeleton.GetBoneGlobalRest(foot).Basis, toSkeleton * footWorld));

                        // A stale pole behind the leg must not survive: the stage's output depends only on this pose.
                        poleTarget.GlobalPosition = midpoint - (forward * 0.5f);
                        controller._ProcessModificationWithDelta(1d / 60d);
                        cases++;

                        Vector3 toPole = poleTarget.GlobalPosition - midpoint;
                        float forwardOffset = toPole.Dot(forward);
                        float sideOffset = Math.Abs(toPole.Dot(lateral));
                        string where = FormattableString.Invariant(
                            $"{prefix} extension={extension:0.###} lean={lean:0.##} knee={label}: forward={forwardOffset:0.####} side={sideOffset:0.####}");
                        if (!toPole.IsFinite() || forwardOffset < MinimumPoleForwardOffsetMetres)
                        {
                            failures.Add($"{where}: pole is not in front of the leg.");
                        }
                        else if (sideOffset > forwardOffset * MaximumPoleSideToForwardRatio)
                        {
                            failures.Add($"{where}: pole spikes sideways.");
                        }

                        Vector3 poleDirection = toPole.Normalized();
                        if (previous != Vector3.Zero && previous.Dot(poleDirection) < MinimumPoleContinuityDot)
                        {
                            failures.Add(FormattableString.Invariant($"{where}: pole direction jumped (dot {previous.Dot(poleDirection):0.###})."));
                        }

                        previous = poleDirection;
                    }
                }
            }
        }

        WriteEvidence($"{rig}-pole-fallback.txt", $"{rig}: {cases} cases, {failures.Count} failures\n{string.Join('\n', failures)}");
        Assert.True(failures.Count == 0, $"{rig}: {failures.Count} of {cases} pole cases failed:\n{string.Join('\n', failures.Take(40))}");
    }

    private static Vector3 Perpendicular(Vector3 vector, Vector3 axis)
    {
        Vector3 projected = vector - (vector.Dot(axis) * axis);
        return projected.LengthSquared() > 1e-8f ? projected.Normalized() : vector.Normalized();
    }

    private static void WriteEvidence(string file, string text)
    {
        string directory = ProjectSettings.GlobalizePath("res://temp/ik003-foot-planting/rig");
        _ = Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, file), text);
    }

    private const double SliceStartOffset = 0.3d;
    private const string SlicedState = "NPCSideStepR";

    private static bool IsExit(string? state)
        => state is not null
           && (state.StartsWith("NPCF2I", StringComparison.Ordinal) || state.StartsWith("NPCB2I", StringComparison.Ordinal));

    private static float ChainLength(Skeleton3D skeleton, string prefix)
    {
        Vector3 upper = skeleton.GetBoneGlobalRest(skeleton.FindBone($"{prefix}UpperLeg")).Origin;
        Vector3 lower = skeleton.GetBoneGlobalRest(skeleton.FindBone($"{prefix}LowerLeg")).Origin;
        Vector3 foot = skeleton.GetBoneGlobalRest(skeleton.FindBone($"{prefix}Foot")).Origin;
        return upper.DistanceTo(lower) + lower.DistanceTo(foot);
    }

    private static Vector3 Planar(Vector3 value) => new(value.X, 0f, value.Z);

    private readonly record struct ChainResult(bool SourceFlagged, bool RecorderInterrupted, double ReadPosition, double Length, string Chain);

    private readonly record struct KneeSample(Vector3 Direction, float Offset, Vector3 AvatarForward);

    private enum Rig
    {
        Female,
        Male,
    }

    private sealed class ScriptedNativeFootHeading(Skeleton3D skeleton, Dictionary<string, Vector3> levers)
    {
        public float RequestedHeading
        {
            get; set;
        }

        public bool ApplyHeading
        {
            get; set;
        }

        public int Calls
        {
            get; private set;
        }

        public float LastHeading
        {
            get; private set;
        }

        public float LastReadback
        {
            get; private set;
        }

        public void Apply()
        {
            if (!ApplyHeading)
            {
                return;
            }

            Calls++;

            foreach (string side in new[] { "Left", "Right" })
            {
                int index = skeleton.FindBone($"{side}Foot");
                Transform3D local = skeleton.GetBoneGlobalPose(index);
                Basis world = skeleton.GlobalTransform.Basis * local.Basis;
                Vector3 axis = world * levers[side];
                float heading = Mathf.Atan2(-axis.X, -axis.Z);
                LastHeading = heading;
                Basis rotation = new(Vector3.Up, Mathf.AngleDifference(heading, RequestedHeading));
                Transform3D desired = new(rotation * world, skeleton.GlobalTransform * local.Origin);
                skeleton.SetBoneGlobalPose(index, skeleton.GlobalTransform.AffineInverse() * desired);
                Vector3 readback = skeleton.GlobalTransform.Basis * skeleton.GetBoneGlobalPose(index).Basis * levers[side];
                LastReadback = Mathf.Atan2(-readback.X, -readback.Z);
            }
        }
    }

    private sealed partial class ScriptedPlantedStateSource : PlantedStateSource
    {
        public PlantedFeetState State
        {
            get; set;
        }

        public override bool TryGetPlantedState(out PlantedFeetState state)
        {
            state = State;
            return true;
        }
    }

    private sealed class RigFixture : IAsyncDisposable
    {
        private readonly SceneTree _sceneTree;
        private readonly Node3D _root;
        private readonly int _previousStepLimit;
        private ScriptedPlantedStateSource? _scripted;
        private AnimationGraphPlantedStateRecorder? _recorder;

        private RigFixture(SceneTree sceneTree, Node3D root, Node3D actor, string skeletonPath, int previousStepLimit)
        {
            _sceneTree = sceneTree;
            _root = root;
            _previousStepLimit = previousStepLimit;
            Actor = actor;
            Skeleton = actor.GetNode<Skeleton3D>(skeletonPath);
            CharacterIK = actor.GetNode<CharacterIK>("CharacterIK");
            Left = Assert.IsType<FootPlantingTargetProvider>(CharacterIK.LeftFootTargetIntentProvider);
            Right = Assert.IsType<FootPlantingTargetProvider>(CharacterIK.RightFootTargetIntentProvider);
            Navigation = actor.GetNode<INavigation>("Navigation");
            foreach (string side in new[] { "Left", "Right" })
            {
                int foot = Skeleton.FindBone($"{side}Foot");
                int toe = Skeleton.FindBone($"{side}Toes");
                BallLevers[side] = (Skeleton.GetBoneGlobalRest(foot).AffineInverse() * Skeleton.GetBoneGlobalRest(toe)).Origin;
            }
            Left.TickRecorded += OnLeft;
            Right.TickRecorded += OnRight;
        }

        public Node3D Actor
        {
            get;
        }

        public Skeleton3D Skeleton
        {
            get;
        }

        public CharacterIK CharacterIK
        {
            get;
        }

        public FootPlantingTargetProvider Left
        {
            get;
        }

        public FootPlantingTargetProvider Right
        {
            get;
        }

        public INavigation Navigation
        {
            get;
        }

        public Dictionary<ulong, FootPlantingTickDiagnostic> LeftHistory { get; } = [];

        public Dictionary<ulong, FootPlantingTickDiagnostic> RightHistory { get; } = [];

        public Dictionary<ulong, KneeSample> LeftKnees { get; } = [];

        public Dictionary<ulong, KneeSample> RightKnees { get; } = [];

        public Dictionary<string, Dictionary<ulong, Transform3D>> PostIKFeet
        {
            get;
        } = new()
        {
            ["Left"] = [],
            ["Right"] = []
        };

        public Dictionary<string, Vector3> BallLevers { get; } = [];

        public float LastRightLegExtension
        {
            get; private set;
        }

        public (FootPlantingTargetProvider Provider, string Prefix)[] Providers => [(Left, "Left"), (Right, "Right")];

        public static async Task<RigFixture> CreateAsync(Rig rig)
        {
            SceneTree sceneTree = GetSceneTree();
            int previousStepLimit = Engine.MaxPhysicsStepsPerFrame;
            Engine.MaxPhysicsStepsPerFrame = 1;
            Node3D root = Assert.IsType<Node3D>(
                LoadPackedScene(NAV001ArrivalOutcomeIntegrationTests.NavigationTestScenePath).Instantiate(),
                exactMatch: false);
            if (rig == Rig.Male)
            {
                var controller = (DirectTransformNavigationPlaytestController)root;
                controller.CharacterVariant = NavigationPlaytestCharacterVariant.ReferenceMale;
                controller.ApplyCharacterVariantSelection();
            }

            Node3D actor = root.GetNode<Node3D>(NAV001ArrivalOutcomeIntegrationTests.ActorNodeName);
            string skeletonPath = rig == Rig.Male ? "Male/GeneralSkeleton" : "Female/GeneralSkeleton";
            NAV001ArrivalOutcomeIntegrationTests.PrepareActorBeforeTreeEntry(actor);
            sceneTree.Root.AddChild(root);
            NavigationRegion3D region = root.GetNode<NavigationRegion3D>("NavigationRegion3D");
            for (int tick = 0; tick < 600; tick++)
            {
                await WaitForNextPhysicsFrameAsync(sceneTree);
                Rid map = region.GetNavigationMap();
                if (map.IsValid && NavigationServer3D.MapGetIterationId(map) > 0 && actor.GetNodeOrNull("Navigation") is NPCNavigation { IsRuntimeBindingActive: true })
                {
                    var fixture = new RigFixture(sceneTree, root, actor, skeletonPath, previousStepLimit);
                    _ = await fixture.StepAsync(5);
                    return fixture;
                }
            }

            throw new InvalidOperationException($"SETUP: the {rig} NPC navigation was not ready in time.");
        }

        public ScriptedPlantedStateSource UseScriptedSource()
        {
            _scripted = new ScriptedPlantedStateSource { Name = "ScriptedPlantedStateSource" };
            Left.PlantedStateSource = _scripted;
            Right.PlantedStateSource = _scripted;
            return _scripted;
        }

        public AnimationGraphPlantedStateRecorder BindRecorder()
        {
            _recorder = AnimationGraphPlantedStateRecorder.Bind(
                Actor,
                Actor.GetNode<AnimationTree>("AnimationTree"),
                Actor.GetNode<AnimationPlayer>("AnimationPlayer"),
                Skeleton);
            return _recorder;
        }

        public async Task<(FootPlantingTickDiagnostic Left, FootPlantingTickDiagnostic Right)> StepAsync(int ticks)
        {
            for (int tick = 0; tick < ticks; tick++)
            {
                await WaitForNextPhysicsFrameAsync(_sceneTree);
            }

            // The post-IK record of the last tick lands in its deferred skeleton update.
            await WaitForNextFrameAsync(_sceneTree);
            return (Left.LastDiagnostic, Right.LastDiagnostic);
        }

        public async Task<FootPlantingTickDiagnostic> NextDiagnosticAsync(FootPlantingTargetProvider provider)
        {
            (FootPlantingTickDiagnostic left, FootPlantingTickDiagnostic right) = await StepAsync(1);
            return provider == Left ? left : right;
        }

        public async ValueTask DisposeAsync()
        {
            _recorder?.Detach();
            Left.TickRecorded -= OnLeft;
            Right.TickRecorded -= OnRight;
            Engine.MaxPhysicsStepsPerFrame = _previousStepLimit;
            _root.QueueFree();
            _scripted?.Free();
            await WaitForFramesAsync(_sceneTree, 1);
        }

        private void OnLeft(FootPlantingTickDiagnostic diagnostic)
        {
            LeftHistory[diagnostic.Tick] = diagnostic;
            PostIKFeet["Left"][diagnostic.Tick] = Skeleton.GlobalTransform * Skeleton.GetBoneGlobalPose(Skeleton.FindBone("LeftFoot"));
            LeftKnees[diagnostic.Tick] = SampleKnee("Left");
        }

        /// <summary>Post-IK knee bend: the knee's offset from the hip-ankle line, read inside SkeletonUpdated.</summary>
        private KneeSample SampleKnee(string prefix)
        {
            Transform3D world = Skeleton.GlobalTransform;
            Vector3 hip = world * Skeleton.GetBoneGlobalPose(Skeleton.FindBone($"{prefix}UpperLeg")).Origin;
            Vector3 knee = world * Skeleton.GetBoneGlobalPose(Skeleton.FindBone($"{prefix}LowerLeg")).Origin;
            Vector3 ankle = world * Skeleton.GetBoneGlobalPose(Skeleton.FindBone($"{prefix}Foot")).Origin;
            Vector3 axis = (ankle - hip).Normalized();
            Vector3 bend = knee - hip - ((knee - hip).Dot(axis) * axis);
            Vector3 forward = world.Basis.Orthonormalized() * Vector3.Back;
            return new KneeSample(bend.LengthSquared() > 0f ? bend.Normalized() : Vector3.Zero, bend.Length(), new Vector3(forward.X, 0f, forward.Z).Normalized());
        }

        private void OnRight(FootPlantingTickDiagnostic diagnostic)
        {
            RightHistory[diagnostic.Tick] = diagnostic;
            PostIKFeet["Right"][diagnostic.Tick] = Skeleton.GlobalTransform * Skeleton.GetBoneGlobalPose(Skeleton.FindBone("RightFoot"));
            RightKnees[diagnostic.Tick] = SampleKnee("Right");
            Transform3D world = Skeleton.GlobalTransform;
            Vector3 upper = world * Skeleton.GetBoneGlobalPose(Skeleton.FindBone("RightUpperLeg")).Origin;
            Vector3 foot = world * Skeleton.GetBoneGlobalPose(Skeleton.FindBone("RightFoot")).Origin;
            LastRightLegExtension = upper.DistanceTo(foot);
        }
    }
}
