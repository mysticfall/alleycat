using System.Text;
using AlleyCat.Core.Installer;
using AlleyCat.IK;
using AlleyCat.Rigging.Installation;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>Independent final world-space foot-basis heading and native target actuation checks.</summary>
public sealed class IK003WorldFacingHoldRegressionIntegrationTests
{
    private const float MaximumHoldErrorDegrees = 1.25f;
    private const float MinimumAnatomicalLeverMetres = 0.08f;

    /// <summary>
    /// Native-free endpoint for the authored NPC navigation worker; the world-facing hold facts verify the
    /// foot/leg modifier chain, never locomotion. Bound before Ready so the eager MotionBricksNavigation
    /// worker cannot construct a real CUDA session during these runs.
    /// </summary>
    private sealed class AmbientIkSession : AlleyCat.Motion.SomaInferenceWorker.ISession
    {
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
            => [.. Enumerable.Range(0, 4).Select(_ => (Transform3D[])context[3].Clone())];

        public void Dispose()
        {
        }
    }

    private static void BindAmbientIkSession(Node node)
    {
        if (node is Navigation.MotionBricksNavigation navigation)
        {
            navigation.SessionFactory = () => new AmbientIkSession();
        }
    }

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
        // Output-only: every run owns a fresh evidence directory so the recorded candidate calibration under
        // nav001-yaw-scale-candidate is never rewritten. An explicit override still selects a curated tree.
        string evidenceDirectory = OS.GetEnvironment("ALLEYCAT_IK003_CALIBRATION_EVIDENCE") is { Length: > 0 } calibrationPath
            ? $"res://temp/{calibrationPath.TrimStart('/')}"
            : $"res://temp/tests/ik003-calibration/{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        SceneTree sceneTree = GetSceneTree();
        int previousLimit = Engine.MaxPhysicsStepsPerFrame;
        Engine.MaxPhysicsStepsPerFrame = 1;
        Node3D root = Assert.IsType<Node3D>(LoadPackedScene("res://assets/characters/reference/ally_npc.tscn").Instantiate(), exactMatch: false);
        sceneTree.NodeAdded += BindAmbientIkSession;
        KnownTargetProvider? leftProvider = null;
        KnownTargetProvider? rightProvider = null;
        try
        {
            Node3D actor = root;
            RigRoleTemplateSceneInstaller installer = actor.GetNode<RigRoleTemplateSceneInstaller>("NPCCharacterInstaller");
            installer.AutoInstallOnReady = false;
            SceneInstallationResult installation = installer.Install(new SceneInstallationContext(actor));
            Assert.True(installation.Succeeded, string.Join('\n', installation.Errors));
            CharacterIK characterIK = actor.GetNode<CharacterIK>("CharacterIK");
            characterIK.HeadTargetIntentProvider = null;
            characterIK.HeadFallbackIntentProvider = null;
            Node mind = actor.GetNode("Mind");
            actor.RemoveChild(mind);
            mind.Free();
            actor.GetNode<AnimationTree>("AnimationTree").Active = false;
            sceneTree.Root.AddChild(root);
            await WaitForPhysicsFramesAsync(sceneTree, 30);

            Skeleton3D skeleton = actor.GetNode<Skeleton3D>("Female/SOMA/Native/GeneralSkeleton");
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
            File.WriteAllText(Path.Combine(directory, "derivation.txt"), FormattableString.Invariant($"reference female; {samples} final post-IK foot observations; peak native target-to-output world-heading error={peak:R} deg; command-to-applied tolerance=0.001 deg (full basis within 0.00001 units); mathematical known-transform oracle tolerance=0.001 deg; native actuation bound={MaximumHoldErrorDegrees:R} deg. Each provider was called once, command reached the stage on the same tick, both final skeleton poses were finite and reachable. Samples use neutral/turned/wrap world headings, tilt and heel roll with 60 mm radial inset from near-extension. This is native IK actuation evidence, not locomotion or planting evidence.\n"));
            Assert.Equal(8, samples);
            Assert.True(peak <= MaximumHoldErrorDegrees, $"Native final orientation precision {peak:R}° exceeds fixed {MaximumHoldErrorDegrees:R}°; evidence {evidenceDirectory}.");
        }
        finally
        {
            Engine.MaxPhysicsStepsPerFrame = previousLimit;
            sceneTree.NodeAdded -= BindAmbientIkSession;
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

    private static float Heading(Vector3 direction)
    {
        direction.Y = 0f;
        return direction.LengthSquared() < 1e-10f ? float.NaN : Mathf.Atan2(-direction.X, -direction.Z);
    }

}
