using System.Text;
using AlleyCat.IK;
using AlleyCat.IntegrationTests.Navigation;
using AlleyCat.Navigation;
using AlleyCat.Rigging;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>IK-003 TR26: independent metadata contacts and final, world-space foot-basis headings.</summary>
public sealed class IK003WorldFacingHoldRegressionIntegrationTests
{
    // Output-only redirect: runs may point evidence at a fresh tree via ALLEYCAT_IK003_YAW_EVIDENCE without
    // touching the recorded baseline folder; the default remains the original baseline location.
    private static readonly string _evidenceDirectory = OS.GetEnvironment("ALLEYCAT_IK003_YAW_EVIDENCE") is { Length: > 0 } path
        ? $"res://temp/{path.TrimStart('/')}"
        : "res://temp/nav001-robotic-pivot-ranking-order-fix/run-01/yaw";
    // The fixed engineering bound is independently checked by Female_KnownFootTargets_NativeFinalWorldFacingPrecision:
    // command -> final native foot heading was <= 0.000028 degrees on eight known reachable targets, with a
    // <= 0.001-degree mathematical oracle and target-actuation check. The remaining > 1.2499 degrees is a
    // deliberately conservative allowance for different poses and the new target policy, not an inferred angular
    // allowance from ball-position tolerance. This is not a perceptual naturalness threshold or a universal bound.
    private const float MaximumHoldErrorDegrees = 1.25f;
    private const float MinimumAnatomicalLeverMetres = 0.08f;

    /// <inheritdoc/>
    [Fact]
    public void WorldFacingOracle_WrapTiltAndRotatedContainer()
    {
        var axis = new Vector3(0f, 0f, 1f);
        Basis container = new(Vector3.Up, Mathf.Pi);
        Basis foot = new(Vector3.Right, Mathf.DegToRad(25f));
        float heading = Heading(container * foot * axis);
        Assert.InRange(Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(0f, heading))), 0f, 0.001f);

        Basis turned = new(Vector3.Up, Mathf.DegToRad(42f));
        float moved = Heading(turned * container * foot * axis);
        Assert.InRange(Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(heading, moved))) - 42f, -0.001f, 0.001f);

        float before = Mathf.DegToRad(179.8f);
        float after = Mathf.DegToRad(-179.8f);
        Assert.InRange(Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(before, after))), 0.399f, 0.401f);
        // Foot rotation about its anatomical forward axis changes heel roll, not horizontal facing.
        Basis rolled = container * foot * new Basis(axis, Mathf.DegToRad(30f));
        Assert.InRange(Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(heading, Heading(rolled * axis)))), 0f, 0.001f);
    }

    /// <summary>Calibrates the final native leg/foot modifier chain, not the missing planting-yaw policy.</summary>
    [Fact]
    public async Task Female_KnownFootTargets_NativeFinalWorldFacingPrecision()
    {
        // Output-only redirect: a run may point calibration evidence at a fresh tree; the recorded candidate
        // calibration folder stays untouched.
        string evidenceDirectory = OS.GetEnvironment("ALLEYCAT_IK003_CALIBRATION_EVIDENCE") is { Length: > 0 } calibrationPath
            ? $"res://temp/{calibrationPath.TrimStart('/')}"
            : "res://temp/nav001-yaw-scale-candidate/run-01/calibration";
        SceneTree sceneTree = GetSceneTree();
        int previousLimit = Engine.MaxPhysicsStepsPerFrame;
        Engine.MaxPhysicsStepsPerFrame = 1;
        Node3D root = Assert.IsType<Node3D>(LoadPackedScene(NAV001ArrivalOutcomeIntegrationTests.NavigationTestScenePath).Instantiate(), exactMatch: false);
        KnownTargetProvider? leftProvider = null;
        KnownTargetProvider? rightProvider = null;
        try
        {
            Node3D actor = root.GetNode<Node3D>(NAV001ArrivalOutcomeIntegrationTests.ActorNodeName);
            NAV001ArrivalOutcomeIntegrationTests.PrepareActorBeforeTreeEntry(actor);
            sceneTree.Root.AddChild(root);
            _ = await NAV001ArrivalOutcomeIntegrationTests.WaitForReadyNavigationAsync(sceneTree, root, actor);
            await WaitForPhysicsFramesAsync(sceneTree, NAV001ArrivalOutcomeIntegrationTests.WarmUpPhysicsTicks);

            Skeleton3D skeleton = actor.GetNode<Skeleton3D>(NAV001ArrivalOutcomeIntegrationTests.SkeletonPath);
            CharacterIK characterIK = actor.GetNode<CharacterIK>("CharacterIK");
            Node3D leftTarget = Assert.IsType<Node3D>(characterIK.LeftFootIKTarget, exactMatch: false);
            Node3D rightTarget = Assert.IsType<Node3D>(characterIK.RightFootIKTarget, exactMatch: false);
            int leftFoot = skeleton.FindBone("LeftFoot");
            int rightFoot = skeleton.FindBone("RightFoot");
            int leftToes = skeleton.FindBone("LeftToes");
            int rightToes = skeleton.FindBone("RightToes");
            Assert.True(leftFoot >= 0 && rightFoot >= 0 && leftToes >= 0 && rightToes >= 0, "SETUP: both foot/toe bones required.");
            Vector3 leftAxis = skeleton.GetBoneGlobalRest(leftFoot).AffineInverse() * skeleton.GetBoneGlobalRest(leftToes).Origin;
            Vector3 rightAxis = skeleton.GetBoneGlobalRest(rightFoot).AffineInverse() * skeleton.GetBoneGlobalRest(rightToes).Origin;
            Assert.InRange(leftAxis.Length(), MinimumAnatomicalLeverMetres, 1f);
            Assert.InRange(rightAxis.Length(), MinimumAnatomicalLeverMetres, 1f);

            // Only replace the target *policy*. CharacterIK's provider stage, the authored two-bone solve and
            // CopyFootRotation modifier remain in their actual scene order and at production influence.
            leftProvider = new KnownTargetProvider { Name = "CalibrationLeftIntent" };
            rightProvider = new KnownTargetProvider { Name = "CalibrationRightIntent" };
            characterIK.AddChild(leftProvider);
            characterIK.AddChild(rightProvider);
            characterIK.LeftFootTargetIntentProvider = leftProvider;
            characterIK.RightFootTargetIntentProvider = rightProvider;
            SkeletonModifier3D providerStage = skeleton.GetNode<SkeletonModifier3D>("CharacterIKFootProviderStage");
            StringBuilder rows = new("case,foot,tick,actor_yaw_deg,command_world_deg,applied_world_deg,output_world_deg,angular_error_deg,command_x,command_y,command_z,applied_x,applied_y,applied_z,output_x,output_y,output_z,upper_to_target_m,leg_rest_length_m,provider_calls,applied_basis_max_column_error,output_basis_max_column_error\n");
            float peak = 0f;
            int samples = 0;

            // Identity and rotated actor; crossing +179.8 -> -179.8 world headings; both feet in each case.
            (string Name, float ActorYaw, float DesiredYaw, float Tilt, float Roll)[] cases =
            [
                ("neutral", 0f, 0f, 0f, 0f),
                ("turned-tilt-roll", 55f, 26f, 12f, 17f),
                ("wrap-positive", 179f, 179.8f, 8f, -13f),
                ("wrap-negative", 179f, -179.8f, -9f, 14f),
            ];

            foreach ((string name, float actorYaw, float desiredYaw, float tilt, float roll) in cases)
            {
                actor.GlobalBasis = new Basis(Vector3.Up, Mathf.DegToRad(actorYaw));
                leftProvider.Enabled = false;
                rightProvider.Enabled = false;
                await WaitForNextPhysicsFrameAsync(sceneTree);
                await WaitForNextFrameAsync(sceneTree);

                Transform3D baselineLeft = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(leftFoot);
                Transform3D baselineRight = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(rightFoot);
                // A known WORLD heading, irrespective of the 180-degree rig container and current actor yaw.
                Transform3D leftCommand = Command(baselineLeft, leftAxis, desiredYaw, tilt, roll, ReachCentre("Left"));
                Transform3D rightCommand = Command(baselineRight, rightAxis, desiredYaw, tilt, roll, ReachCentre("Right"));
                leftProvider.Command = leftCommand;
                rightProvider.Command = rightCommand;
                leftProvider.Enabled = true;
                rightProvider.Enabled = true;

                ulong expectedTick = Engine.GetPhysicsFrames() + 1;
                int previousLeftCalls = leftProvider.Calls;
                int previousRightCalls = rightProvider.Calls;
                int stageCount = 0;
                int finalCount = 0;
                Transform3D appliedLeft = Transform3D.Identity;
                Transform3D appliedRight = Transform3D.Identity;
                Transform3D outputLeft = Transform3D.Identity;
                Transform3D outputRight = Transform3D.Identity;
                ulong stageTick = 0;
                ulong finalTick = 0;
                void OnStage()
                {
                    stageCount++;
                    stageTick = Engine.GetPhysicsFrames();
                    appliedLeft = leftTarget.GlobalTransform;
                    appliedRight = rightTarget.GlobalTransform;
                }

                void OnFinal()
                {
                    finalCount++;
                    finalTick = Engine.GetPhysicsFrames();
                    outputLeft = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(leftFoot);
                    outputRight = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(rightFoot);
                }

                providerStage.ModificationProcessed += OnStage;
                skeleton.SkeletonUpdated += OnFinal;
                try
                {
                    await WaitForNextPhysicsFrameAsync(sceneTree);
                    await WaitForNextFrameAsync(sceneTree);
                }
                finally
                {
                    providerStage.ModificationProcessed -= OnStage;
                    skeleton.SkeletonUpdated -= OnFinal;
                }

                Assert.Equal(1, stageCount);
                Assert.Equal(1, finalCount);
                Assert.Equal(expectedTick, stageTick);
                Assert.Equal(expectedTick, finalTick);
                Assert.True(leftProvider.Calls > previousLeftCalls && rightProvider.Calls > previousRightCalls,
                    $"SETUP: provider not consulted by the native pipeline on {name}.");
                Record("left", leftFoot, leftAxis, leftCommand, appliedLeft, outputLeft, leftProvider.Calls - previousLeftCalls);
                Record("right", rightFoot, rightAxis, rightCommand, appliedRight, outputRight, rightProvider.Calls - previousRightCalls);

                void Record(string foot, int footIndex, Vector3 axis, Transform3D command, Transform3D applied, Transform3D output, int calls)
                {
                    float commandYaw = Heading(command.Basis * axis);
                    float appliedYaw = Heading(applied.Basis * axis);
                    float outputYaw = Heading(output.Basis * axis);
                    Assert.True(float.IsFinite(commandYaw) && float.IsFinite(appliedYaw) && float.IsFinite(outputYaw), $"SETUP: {name}/{foot} non-finite heading.");
                    Assert.InRange(Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(Mathf.DegToRad(desiredYaw), commandYaw))), 0f, 0.001f);
                    float appliedError = Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(commandYaw, appliedYaw)));
                    Assert.InRange(appliedError, 0f, 0.001f);
                    Assert.InRange(command.Origin.DistanceTo(applied.Origin), 0f, 0.00001f);
                    float appliedBasisError = BasisColumnError(command.Basis, applied.Basis);
                    Assert.InRange(appliedBasisError, 0f, 0.00001f);
                    float error = Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(commandYaw, outputYaw)));
                    float outputBasisError = BasisColumnError(command.Basis, output.Basis);
                    Vector3 upper = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(skeleton.FindBone(foot == "left" ? "LeftUpperLeg" : "RightUpperLeg")).Origin;
                    int lowerIndex = skeleton.FindBone(foot == "left" ? "LeftLowerLeg" : "RightLowerLeg");
                    float legLength = skeleton.GetBoneGlobalRest(skeleton.FindBone(foot == "left" ? "LeftUpperLeg" : "RightUpperLeg")).Origin.DistanceTo(skeleton.GetBoneGlobalRest(lowerIndex).Origin)
                        + skeleton.GetBoneGlobalRest(lowerIndex).Origin.DistanceTo(skeleton.GetBoneGlobalRest(footIndex).Origin);
                    float distance = upper.DistanceTo(command.Origin);
                    Assert.True(distance < legLength - 0.005f, $"SETUP: {name}/{foot} target out of reach: {distance} vs {legLength}.");
                    _ = rows.AppendLine(FormattableString.Invariant($"{name},{foot},{expectedTick},{actorYaw:R},{Mathf.RadToDeg(commandYaw):R},{Mathf.RadToDeg(appliedYaw):R},{Mathf.RadToDeg(outputYaw):R},{error:R},{command.Origin.X:R},{command.Origin.Y:R},{command.Origin.Z:R},{applied.Origin.X:R},{applied.Origin.Y:R},{applied.Origin.Z:R},{output.Origin.X:R},{output.Origin.Y:R},{output.Origin.Z:R},{distance:R},{legLength:R},{calls},{appliedBasisError:R},{outputBasisError:R}"));
                    peak = Mathf.Max(peak, error);
                    samples++;
                }

                Vector3 ReachCentre(string prefix)
                {
                    int upperIndex = skeleton.FindBone($"{prefix}UpperLeg");
                    return skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(upperIndex).Origin;
                }
            }

            string directory = ProjectSettings.GlobalizePath(evidenceDirectory);
            _ = Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "native-foot-orientation.csv"), rows.ToString());
            File.WriteAllText(Path.Combine(directory, "derivation.txt"), FormattableString.Invariant($"reference female; {samples} final post-IK foot observations; peak native target-to-output world-heading error={peak:R} deg; command-to-applied tolerance=0.001 deg (full basis within 0.00001 units); mathematical known-transform oracle tolerance=0.001 deg; unchanged TR26 regression bound={MaximumHoldErrorDegrees:R} deg; conservative remaining allowance={MaximumHoldErrorDegrees - peak:R} deg for uncalibrated poses and future target policy. Each provider was called once, command reached the stage on the same tick, both final skeleton poses were finite and reachable. Samples use neutral/turned/wrap world headings, tilt and heel roll with 60 mm radial inset from idle near-extension. This empirical margin supports the bound for the sampled conditions, not every pose or the new policy. The 1 mm positional target tolerance is not a rotational precision measurement; A6 still requires user judgement.\n"));
            Assert.Equal(8, samples);
            Assert.True(peak <= MaximumHoldErrorDegrees, $"Native final orientation precision {peak:R}° exceeds fixed {MaximumHoldErrorDegrees:R}°; evidence {evidenceDirectory}.");
        }
        finally
        {
            Engine.MaxPhysicsStepsPerFrame = previousLimit;
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }

        static Transform3D Command(Transform3D baseline, Vector3 axis, float worldYawDegrees, float tiltDegrees, float rollDegrees, Vector3 hip)
        {
            Vector3 ball = baseline * axis;
            Basis tilted = baseline.Basis.Rotated(baseline.Basis.Column0.Normalized(), Mathf.DegToRad(tiltDegrees));
            Basis rolled = tilted.Rotated((tilted * axis).Normalized(), Mathf.DegToRad(rollDegrees));
            float currentYaw = Heading(rolled * axis);
            Basis commanded = new Basis(Vector3.Up, Mathf.AngleDifference(currentYaw, Mathf.DegToRad(worldYawDegrees))) * rolled;
            Vector3 origin = ball - (commanded * axis);
            // A 60 mm radial inset avoids the idle pose's near-full extension and leaves room for the
            // yaw-induced ankle displacement. The independent reach assertion below checks every command.
            Vector3 toOrigin = origin - hip;
            origin -= toOrigin.Normalized() * 0.06f;
            return new Transform3D(commanded, origin);
        }

        static float BasisColumnError(Basis expected, Basis actual)
        {
            return Mathf.Max(expected.Column0.DistanceTo(actual.Column0),
                        Mathf.Max(expected.Column1.DistanceTo(actual.Column1), expected.Column2.DistanceTo(actual.Column2)));
        }
    }

    private sealed partial class KnownTargetProvider : IKTargetIntentProvider
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

    /// <inheritdoc/>
    [Fact]
    public Task S1_Oblique_WorldFacingHeld() => RunAsync("s1-oblique-2m-30deg", new Vector3(-1f, 0f, -Mathf.Sqrt(3f)), Mathf.DegToRad(30f));

    /// <inheritdoc/>
    [Fact]
    public Task S3_Turn90_WorldFacingHeld() => RunAsync("s3-turn-90deg-left", Vector3.Zero, Mathf.Pi / 2f);

    /// <inheritdoc/>
    [Fact]
    public Task S4_Turn180_WorldFacingHeld() => RunAsync("s4-turn-180deg", Vector3.Zero, Mathf.Pi);

    /// <inheritdoc/>
    [Fact]
    public Task S6_UserShuffle_WorldFacingHeld() => RunAsync("s6-user-shuffle", new Vector3(-0.45992267f, 0f, -1.4639847f), 0.30439475f);

    private static async Task RunAsync(string scenario, Vector3 destination, float yaw)
    {
        SceneTree sceneTree = GetSceneTree();
        int previousLimit = Engine.MaxPhysicsStepsPerFrame;
        Engine.MaxPhysicsStepsPerFrame = 1;
        Node3D root = Assert.IsType<Node3D>(LoadPackedScene(NAV001ArrivalOutcomeIntegrationTests.NavigationTestScenePath).Instantiate(), exactMatch: false);
        AnimationGraphPlantedStateRecorder? recorder = null;
        FacingCapture? capture = null;
        try
        {
            Node3D actor = root.GetNode<Node3D>(NAV001ArrivalOutcomeIntegrationTests.ActorNodeName);
            NAV001ArrivalOutcomeIntegrationTests.PrepareActorBeforeTreeEntry(actor);
            sceneTree.Root.AddChild(root);
            INavigation navigation = await NAV001ArrivalOutcomeIntegrationTests.WaitForReadyNavigationAsync(sceneTree, root, actor);
            recorder = NAV001ArrivalOutcomeIntegrationTests.BindRecorder(actor);
            recorder.Attach();
            Skeleton3D skeleton = actor.GetNode<Skeleton3D>(NAV001ArrivalOutcomeIntegrationTests.SkeletonPath);
            capture = new FacingCapture(actor, skeleton);
            await WaitForPhysicsFramesAsync(sceneTree, NAV001ArrivalOutcomeIntegrationTests.WarmUpPhysicsTicks);
            NAV001ArrivalOutcomeIntegrationTests.AssertIdleWarmUp(recorder, actor);

            NavigationRequestIdentity identity = NAV001ArrivalOutcomeIntegrationTests.IssueRequest(
                navigation, new NAV001ArrivalOutcomeIntegrationTests.ArrivalRequest(destination, yaw), scenario, 0);
            ulong issueTick = Engine.GetPhysicsFrames();
            NavigationTerminalOutcomeRecord? outcome = null;
            ulong completionTick = 0;
            // A process-frame barrier follows each physics signal, never an Advance(0) or a pose read from the
            // physics-await continuation. The SkeletonUpdated callback is the sole source of final headings.
            await WaitForNextFrameAsync(sceneTree);
            for (int tick = 0; tick < 1200 && outcome is null; tick++)
            {
                outcome = NAV001ArrivalOutcomeIntegrationTests.FindOutcome(navigation, identity);
                if (outcome is not null)
                {
                    completionTick = Engine.GetPhysicsFrames();
                    break;
                }

                await WaitForNextPhysicsFrameAsync(sceneTree);
                await WaitForNextFrameAsync(sceneTree);
            }

            Assert.True(outcome is not null, $"SETUP: {scenario} never completed its navigation request.");
            for (int tick = 0; tick < Engine.PhysicsTicksPerSecond; tick++)
            {
                await WaitForNextPhysicsFrameAsync(sceneTree);
                await WaitForNextFrameAsync(sceneTree);
            }

            ulong lastTick = Engine.GetPhysicsFrames();
            recorder.Detach();
            capture.Detach();
            Assert.Equal(0, recorder.UnpairedTicks);
            Assert.Equal(0, recorder.RepeatedSkeletonUpdates);
            Assert.Equal(0, capture.DuplicateTicks);
            Assert.True(capture.Levers.Left >= MinimumAnatomicalLeverMetres && capture.Levers.Right >= MinimumAnatomicalLeverMetres,
                $"SETUP: anatomical lever below {MinimumAnatomicalLeverMetres} m: {capture.Levers}.");

            List<PlantedStateSample> samples = [.. recorder.Samples.Where(s => s.Tick >= issueTick && s.Tick <= lastTick)];
            Assert.Equal((long)(lastTick - issueTick + 1), samples.Count);
            StringBuilder csv = new("tick,clip,fading,planted_l,planted_r,actor_yaw_deg,world_heading_l_deg,world_heading_r_deg,ball_l_x,ball_l_z,ball_r_x,ball_r_z,reach_release_l,reach_release_r\n");
            var runs = new List<string>();
            float maximum = 0f;
            int plantedRuns = 0;
            foreach (bool left in new[] { true, false })
            {
                PlantedStateSample? previous = recorder.Samples.LastOrDefault(s => s.Tick < issueTick);
                float anchor = 0f;
                float maxYaw = 0f;
                float maxBall = 0f;
                ulong start = 0;
                string startClip = string.Empty;
                string endClip = string.Empty;
                bool active = false;
                foreach (PlantedStateSample sample in samples)
                {
                    Assert.True(capture.Headings.TryGetValue(sample.Tick, out HeadingPair headings), $"SETUP: no post-IK facing for tick {sample.Tick}.");
                    float current = left ? headings.Left : headings.Right;
                    Assert.True(float.IsFinite(current), $"SETUP: degenerate anatomical axis on tick {sample.Tick}.");
                    bool planted = left ? sample.Planted.Left : sample.Planted.Right;
                    if (planted && !active)
                    {
                        // A contact already active at issue is anchored by the immediately preceding final pose.
                        bool inherited = previous is not null && (left ? previous.Planted.Left : previous.Planted.Right);
                        Assert.True(!inherited || capture.Headings.TryGetValue(previous!.Tick, out _), "SETUP: missing preceding contact pose.");
                        anchor = inherited ? (left ? capture.Headings[previous!.Tick].Left : capture.Headings[previous!.Tick].Right) : current;
                        start = sample.Tick;
                        startClip = sample.Graph.Current;
                        maxYaw = 0f;
                        maxBall = 0f;
                        active = true;
                    }

                    if (planted)
                    {
                        maxYaw = Mathf.Max(maxYaw, Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(anchor, current))));
                        Vector3 ball = left ? sample.LeftBall : sample.RightBall;
                        Vector3 initial = left ? samples.First(s => s.Tick == start).LeftBall : samples.First(s => s.Tick == start).RightBall;
                        maxBall = Mathf.Max(maxBall, new Vector2(ball.X - initial.X, ball.Z - initial.Z).Length());
                        endClip = sample.Graph.Current;
                    }

                    if (active && (!planted || sample.Tick == lastTick))
                    {
                        ulong end = planted ? sample.Tick : sample.Tick - 1;
                        runs.Add(FormattableString.Invariant($"{scenario},{(left ? "left" : "right")},{start},{end},{Mathf.RadToDeg(anchor):R},{maxYaw:R},{maxBall * 1000:R},{startClip},{endClip}"));
                        maximum = Mathf.Max(maximum, maxYaw);
                        plantedRuns++;
                        active = false;
                    }

                    previous = sample;
                }
            }

            foreach (PlantedStateSample sample in samples)
            {
                HeadingPair heading = capture.Headings[sample.Tick];
                string clip = sample.Graph.Current;
                _ = csv.AppendLine(FormattableString.Invariant($"{sample.Tick},{clip},{sample.Graph.Fading},{(sample.Planted.Left ? 1 : 0)},{(sample.Planted.Right ? 1 : 0)},{Mathf.RadToDeg(NAV001ArrivalOutcomeIntegrationTests.YawOf(sample.Actor)):R},{Mathf.RadToDeg(heading.Left):R},{Mathf.RadToDeg(heading.Right):R},{sample.LeftBall.X:R},{sample.LeftBall.Z:R},{sample.RightBall.X:R},{sample.RightBall.Z:R},{(capture.Releases.Left.Contains(sample.Tick) ? 1 : 0)},{(capture.Releases.Right.Contains(sample.Tick) ? 1 : 0)}"));
            }

            string directory = ProjectSettings.GlobalizePath(_evidenceDirectory);
            _ = Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{scenario}-ticks.csv"), csv.ToString());
            File.WriteAllText(Path.Combine(directory, $"{scenario}-runs.csv"), "scenario,foot,start_tick,end_tick,anchor_world_deg,max_world_excursion_deg,max_planar_ball_mm,start_clip,end_clip\n" + string.Join('\n', runs) + "\n");
            File.WriteAllText(Path.Combine(directory, $"{scenario}-summary.txt"), FormattableString.Invariant($"issue={issueTick} completion={completionTick} last={lastTick} ticks_per_second={Engine.PhysicsTicksPerSecond} runs={plantedRuns} max_world_yaw_deg={maximum:R} bound_deg={MaximumHoldErrorDegrees:R} lever_l_m={capture.Levers.Left:R} lever_r_m={capture.Levers.Right:R} duplicate={capture.DuplicateTicks} unpaired={recorder.UnpairedTicks}\n"));
            Assert.True(plantedRuns > 0, $"SETUP: {scenario} produced no metadata-planted run.");
            Assert.True(maximum <= MaximumHoldErrorDegrees,
                $"IK-003 WORLD-FACING RED: {scenario} {maximum:R}° > {MaximumHoldErrorDegrees:R}°; {plantedRuns} runs, issue {issueTick} completion {completionTick} last {lastTick}; evidence {_evidenceDirectory}.");
        }
        finally
        {
            capture?.Detach();
            recorder?.Detach();
            Engine.MaxPhysicsStepsPerFrame = previousLimit;
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    private static float Heading(Vector3 direction)
    {
        direction.Y = 0f;
        return direction.LengthSquared() < 1e-10f ? float.NaN : Mathf.Atan2(-direction.X, -direction.Z);
    }

    private readonly record struct HeadingPair(float Left, float Right);

    private sealed class FacingCapture
    {
        private readonly Skeleton3D _skeleton;
        private readonly int _left;
        private readonly int _right;
        private readonly Vector3 _leftAxis;
        private readonly Vector3 _rightAxis;
        private readonly FootPlantingTargetProvider _leftProvider;
        private readonly FootPlantingTargetProvider _rightProvider;

        public FacingCapture(Node3D actor, Skeleton3D skeleton)
        {
            _skeleton = skeleton;
            _left = skeleton.FindBone("LeftFoot");
            _right = skeleton.FindBone("RightFoot");
            int leftToes = skeleton.FindBone("LeftToes");
            int rightToes = skeleton.FindBone("RightToes");
            Assert.True(_left >= 0 && _right >= 0 && leftToes >= 0 && rightToes >= 0, "SETUP: both anatomical foot/toe pairs required.");
            // Bone-rest foot -> ball/toes vector, expressed in the foot bone's own frame; never assume local -Z.
            _leftAxis = skeleton.GetBoneGlobalRest(_left).AffineInverse() * skeleton.GetBoneGlobalRest(leftToes).Origin;
            _rightAxis = skeleton.GetBoneGlobalRest(_right).AffineInverse() * skeleton.GetBoneGlobalRest(rightToes).Origin;
            Levers = (_leftAxis.Length(), _rightAxis.Length());
            _leftProvider = actor.GetNode<FootPlantingTargetProvider>("CharacterIK/LeftFootPlanting");
            _rightProvider = actor.GetNode<FootPlantingTargetProvider>("CharacterIK/RightFootPlanting");
            _leftProvider.TickRecorded += OnDiagnostic;
            _rightProvider.TickRecorded += OnDiagnostic;
            skeleton.SkeletonUpdated += OnUpdated;
        }

        public Dictionary<ulong, HeadingPair> Headings { get; } = [];
        public (float Left, float Right) Levers
        {
            get;
        }
        public (HashSet<ulong> Left, HashSet<ulong> Right) Releases { get; } = ([], []);
        public int DuplicateTicks
        {
            get; private set;
        }

        public void Detach()
        {
            _skeleton.SkeletonUpdated -= OnUpdated;
            _leftProvider.TickRecorded -= OnDiagnostic;
            _rightProvider.TickRecorded -= OnDiagnostic;
        }

        private void OnDiagnostic(FootPlantingTickDiagnostic diagnostic)
        {
            if (diagnostic.ReachBlendOutStarted)
            {
                _ = (diagnostic.Side == LimbSide.Left ? Releases.Left : Releases.Right).Add(diagnostic.Tick);
            }
        }

        private void OnUpdated()
        {
            ulong tick = Engine.GetPhysicsFrames();
            Transform3D frame = _skeleton.GlobalTransform;
            HeadingPair heading = new(
                Heading((frame * _skeleton.GetBoneGlobalPose(_left)).Basis * _leftAxis),
                Heading((frame * _skeleton.GetBoneGlobalPose(_right)).Basis * _rightAxis));
            if (!Headings.TryAdd(tick, heading))
            {
                DuplicateTicks++;
            }
        }
    }
}
