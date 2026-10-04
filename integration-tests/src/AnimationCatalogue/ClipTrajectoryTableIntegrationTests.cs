using System.Globalization;
using System.Text;
using AlleyCat.Control.Locomotion;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.AnimationCatalogue;

/// <summary>
/// The bind-time clip trajectory table on the bare reference female and male models, which share the female
/// locomotion package but bind different <see cref="Skeleton3D.MotionScale"/> values. Oracles are native Godot
/// evaluation only: the table's whole-clip and sub-interval slices are compared with the root motion an
/// <see cref="AnimationTree"/> produces over a native-speed playback, its dense samples with
/// <c>AnimationPlayer.Seek</c>-evaluated Root and ball-bone poses at 120 Hz, and its source-unit endpoints with the
/// catalogue. Every interpolation check uses the clip's declared <see cref="ClipTrajectory.ErrorBounds"/>: the root
/// planar bound × <see cref="Skeleton3D.MotionScale"/>, the yaw bound unscaled, and the ball bound in runtime metres.
/// </summary>
[Headless]
public sealed class ClipTrajectoryTableIntegrationTests
{
    private const string FemaleModelPath = "res://assets/characters/reference/female/reference_female.blend";
    private const string MaleModelPath = "res://assets/characters/reference/male/reference_male.blend";
    private const string EvidenceDirectory = "res://temp/nav001-p4-u1/trajectory";
    private const string LibraryName = "locomotion";
    private const string PackageLibraryName = "package";
    private const double PlaybackStepSeconds = 1d / 60d;
    private const double DenseSampleRateHz = 120d;

    /// <summary>Sub-interval slice start and end as fractions of the clip, rounded to whole playback steps.</summary>
    private const double SliceStartFraction = 0.25d;

    private const double SliceEndFraction = 0.75d;

    private static readonly NodePath _rootMotionTrack = new("%GeneralSkeleton:Root");

    /// <inheritdoc/>
    [Fact]
    public async Task FemaleRig_TableMatchesRuntimeRootMotionPoseAndCatalogueAtMotionScale()
        => await VerifyRigAsync("female", FemaleModelPath);

    /// <inheritdoc/>
    [Fact]
    public async Task MaleRig_TableMatchesRuntimeRootMotionPoseAndCatalogueAtMotionScale()
        => await VerifyRigAsync("male", MaleModelPath);

    /// <summary>
    /// Both toe bones are required rig data: on the female rig with its right toe bone renamed, the build fails with an
    /// error naming the missing bone.
    /// </summary>
    [Fact]
    public async Task FemaleRig_WithoutRightToeBone_FailsTheBuild()
    {
        SceneTree sceneTree = GetSceneTree();
        StandingLocomotionCatalogue catalogue = ProductionContentBindings.ParseReferenceFemaleLocomotionCatalogue();
        AnimationLibrary library = ResourceLoader.Load<AnimationLibrary>(catalogue.AnimationLibraryPath)
            ?? throw new InvalidOperationException($"SETUP: package library '{catalogue.AnimationLibraryPath}' did not load.");
        Node3D model = Assert.IsType<Node3D>(LoadPackedScene(FemaleModelPath).Instantiate(), exactMatch: false);
        try
        {
            sceneTree.Root.AddChild(model);
            Skeleton3D skeleton = Assert.IsType<Skeleton3D>(model.FindChild("GeneralSkeleton", true, false));
            await WaitForFramesAsync(sceneTree, 1);
            skeleton.SetBoneName(skeleton.FindBone(ClipTrajectoryTableBuilder.RightBallBone), "RightToesRemovedForFixture");

            InvalidDataException error = Assert.Throws<InvalidDataException>(
                () => ClipTrajectoryTableBuilder.Build(catalogue, library, skeleton, _rootMotionTrack));
            Assert.Contains($"'{ClipTrajectoryTableBuilder.RightBallBone}'", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            model.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    private static async Task VerifyRigAsync(string rig, string modelPath)
    {
        SceneTree sceneTree = GetSceneTree();
        StandingLocomotionCatalogue catalogue = ProductionContentBindings.ParseReferenceFemaleLocomotionCatalogue();
        AnimationLibrary library = ResourceLoader.Load<AnimationLibrary>(catalogue.AnimationLibraryPath)
            ?? throw new InvalidOperationException($"SETUP: package library '{catalogue.AnimationLibraryPath}' did not load.");
        Node3D model = Assert.IsType<Node3D>(LoadPackedScene(modelPath).Instantiate(), exactMatch: false);
        try
        {
            sceneTree.Root.AddChild(model);
            Skeleton3D skeleton = Assert.IsType<Skeleton3D>(model.FindChild("GeneralSkeleton", true, false));
            // The package tracks address the skeleton by unique name from the player root, as in the NPC templates.
            skeleton.Owner = model;
            skeleton.UniqueNameInOwner = true;
            AnimationPlayer player = CreateOraclePlayer(model, library);
            AnimationTree tree = CreateRootMotionTree(model, player);
            await WaitForFramesAsync(sceneTree, 1);

            ClipTrajectoryTable table = ClipTrajectoryTableBuilder.Build(catalogue, library, skeleton, _rootMotionTrack);
            float motionScale = skeleton.MotionScale;
            Assert.Equal(motionScale, table.MotionScale);
            Assert.True(
                Math.Abs(motionScale - 1f) > 0.05f,
                $"SETUP: the {rig} rig motion scale {motionScale} does not discriminate scaled from unscaled distances.");

            var report = new StringBuilder(FormattableString.Invariant($"rig={rig} motion_scale={motionScale}\n"));
            _ = report.AppendLine("clip,root_bound_mm,yaw_bound_deg,ball_bound_mm,endpoint_err_mm,endpoint_yaw_err_deg,slice_s,slice_err_mm,slice_yaw_err_deg,catalogue_err_mm,dense_planar_err_mm,dense_yaw_err_deg,ball_at_keys_err_mm,ball_between_keys_err_mm,runtime_right_m,runtime_forward_m,unscaled_forward_m");
            List<string> failures = [];
            int rootBone = skeleton.FindBone(_rootMotionTrack.GetSubName(0));
            int leftBall = skeleton.FindBone(ClipTrajectoryTableBuilder.LeftBallBone);
            int rightBall = skeleton.FindBone(ClipTrajectoryTableBuilder.RightBallBone);
            foreach (StandingLocomotionClip clip in catalogue.Clips.Values.OrderBy(value => value.Role, StringComparer.Ordinal))
            {
                ClipTrajectory trajectory = table[clip.Role];
                string animation = $"{LibraryName}/{clip.Identity}";
                string packageAnimation = $"{PackageLibraryName}/{clip.Identity}";
                float rootBound = trajectory.RootPlanarErrorBound;
                float yawBound = trajectory.ErrorBounds.Yaw;
                float ballBound = trajectory.ErrorBounds.Ball;
                if (trajectory.BallMotionScale != motionScale || Math.Abs(rootBound - (trajectory.ErrorBounds.SourceRootPlanar * motionScale)) > 1e-9f)
                {
                    failures.Add($"{clip.Role}: ball data or root bound is not bound to the rig's motion scale");
                }

                (Vector2 runtimePlanar, float runtimeYaw) = AccumulateRootMotion(
                    tree,
                    packageAnimation,
                    clip.DurationSeconds,
                    0d,
                    clip.DurationSeconds);
                ClipTrajectorySlice endpoint = trajectory.Endpoint;
                float endpointError = (endpoint.Planar - runtimePlanar).Length();
                float endpointYawError = Mathf.Abs(endpoint.Yaw - runtimeYaw);

                double sliceStart = WholeSteps(clip.DurationSeconds * SliceStartFraction);
                double sliceEnd = WholeSteps(clip.DurationSeconds * SliceEndFraction);
                (Vector2 slicePlanar, float sliceYaw) = AccumulateRootMotion(
                    tree,
                    packageAnimation,
                    clip.DurationSeconds,
                    sliceStart,
                    sliceEnd);
                ClipTrajectorySlice slice = trajectory.Slice(sliceStart, sliceEnd);
                float sliceError = (slice.Planar - slicePlanar).Length();
                float sliceYawError = Mathf.Abs(slice.Yaw - sliceYaw);

                var catalogueEndpoint = new Vector2(clip.EndpointRootDisplacement.X, -clip.EndpointRootDisplacement.Z);
                float catalogueError = (endpoint.Planar - (catalogueEndpoint * motionScale)).Length();

                DenseComparison dense = CompareDense(
                    player,
                    skeleton,
                    animation,
                    trajectory,
                    rootBone,
                    leftBall,
                    rightBall);

                _ = report.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{clip.Role},{rootBound * 1000f:0.####},{Mathf.RadToDeg(yawBound):0.####},{ballBound * 1000f:0.###},{endpointError * 1000f:0.####},{Mathf.RadToDeg(endpointYawError):0.#####},{sliceStart:0.###}-{sliceEnd:0.###},{sliceError * 1000f:0.####},{Mathf.RadToDeg(sliceYawError):0.#####},{catalogueError * 1000f:0.####},{dense.Planar * 1000f:0.####},{Mathf.RadToDeg(dense.Yaw):0.#####},{dense.BallAtKeys * 1000f:0.####},{dense.BallBetweenKeys * 1000f:0.####},{runtimePlanar.X:0.#####},{runtimePlanar.Y:0.#####},{catalogueEndpoint.Y:0.#####}"));
                if (endpointError > rootBound || endpointYawError > yawBound)
                {
                    failures.Add($"{clip.Role}: endpoint differs from accumulated runtime root motion beyond the declared bound");
                }

                if (sliceError > rootBound || sliceYawError > yawBound)
                {
                    failures.Add($"{clip.Role}: slice {sliceStart:0.###}-{sliceEnd:0.###} s differs from runtime root motion beyond the declared bound");
                }

                if (catalogueError > ClipTrajectoryTable.CatalogueEndpointTolerance * motionScale)
                {
                    failures.Add($"{clip.Role}: endpoint is not catalogue × motion scale");
                }

                if (dense.Planar > rootBound || dense.Yaw > yawBound)
                {
                    failures.Add($"{clip.Role}: root interpolation exceeds the declared bound against engine evaluation");
                }

                if (Math.Max(dense.BallAtKeys, dense.BallBetweenKeys) > ballBound)
                {
                    failures.Add($"{clip.Role}: ball samples exceed the declared bound against the evaluated post-FK pose");
                }
            }

            string text = report.ToString();
            string directory = ProjectSettings.GlobalizePath(EvidenceDirectory);
            _ = Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{rig}.csv"), text);
            Assert.True(failures.Count == 0, string.Join('\n', failures) + "\n" + text);
        }
        finally
        {
            model.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    private static double WholeSteps(double seconds) => Math.Round(seconds / PlaybackStepSeconds) * PlaybackStepSeconds;

    /// <summary>
    /// Plays the package clip from its start at native speed through an <see cref="AnimationTree"/>, as the NPC runtime
    /// does, and accumulates the root motion of the steps inside <c>(start, end]</c> as the NPC motor consumes it with
    /// <see cref="AnimationMixer.RootMotionLocal"/>: each delta is expressed in the root frame at the end of its step,
    /// so it is applied after that step's rotation. Returns the displacement in actor-semantic <c>[right, forward]</c>
    /// in the root frame at <c>start</c>, and the yaw change.
    /// </summary>
    private static (Vector2 Planar, float Yaw) AccumulateRootMotion(
        AnimationTree tree,
        string animation,
        double duration,
        double start,
        double end)
    {
        tree.Active = false;
        tree.TreeRoot = new AnimationNodeAnimation { Animation = animation };
        tree.Active = true;
        Basis orientation = Basis.Identity;
        Vector3 position = Vector3.Zero;
        int steps = (int)Math.Round(duration / PlaybackStepSeconds);
        Assert.True(
            Math.Abs((steps * PlaybackStepSeconds) - duration) < 1e-9,
            $"SETUP: '{animation}' length {duration} s is not a whole number of playback steps.");
        // The first advance after activation is the entry tick: it evaluates the clip at position 0 and produces no
        // root motion, so a whole playback is the entry tick plus one advance per step.
        int firstStep = (int)Math.Round(start / PlaybackStepSeconds) + 1;
        int lastStep = (int)Math.Round(end / PlaybackStepSeconds);
        for (int step = 0; step <= Math.Min(steps, lastStep); step++)
        {
            tree.Advance(PlaybackStepSeconds);
            if (step < firstStep)
            {
                continue;
            }

            orientation *= new Basis(tree.GetRootMotionRotation());
            position += orientation * tree.GetRootMotionPosition();
        }

        tree.Active = false;
        return (SkeletonToSemantic(position), YawOf(orientation.GetRotationQuaternion()));
    }

    /// <summary>
    /// Largest planar, yaw and ball differences between the table and seek-evaluated poses at 120 Hz over the clip.
    /// </summary>
    private static DenseComparison CompareDense(
        AnimationPlayer player,
        Skeleton3D skeleton,
        string animation,
        ClipTrajectory trajectory,
        int rootBone,
        int leftBall,
        int rightBall)
    {
        player.Stop();
        player.Play(animation);
        int count = (int)Math.Round(trajectory.DurationSeconds * DenseSampleRateHz);
        Vector2 startPlanar = Vector2.Zero;
        float startYaw = 0f;
        float previousYaw = 0f;
        float planarError = 0f;
        float yawError = 0f;
        float ballAtKeys = 0f;
        float ballBetweenKeys = 0f;
        for (int index = 0; index <= count; index++)
        {
            double time = Math.Min(index / DenseSampleRateHz, trajectory.DurationSeconds);
            player.Seek(time, true);
            Transform3D root = skeleton.GetBoneGlobalPose(rootBone);
            float wrapped = YawOf(root.Basis.GetRotationQuaternion());
            float yaw = index == 0 ? wrapped : previousYaw + Mathf.Wrap(wrapped - previousYaw, -Mathf.Pi, Mathf.Pi);
            previousYaw = yaw;
            Vector2 planar = SkeletonToSemantic(root.Origin);
            if (index == 0)
            {
                startPlanar = planar;
                startYaw = yaw;
            }

            planarError = Math.Max(planarError, (trajectory.RootPlanarAt(time) - (planar - startPlanar)).Length());
            yawError = Math.Max(yawError, Mathf.Abs(trajectory.YawAt(time) - (yaw - startYaw)));

            Transform3D inverseRoot = root.AffineInverse();
            Vector3 left = SkeletonToActorLocal(inverseRoot * skeleton.GetBoneGlobalPose(leftBall).Origin);
            Vector3 right = SkeletonToActorLocal(inverseRoot * skeleton.GetBoneGlobalPose(rightBall).Origin);
            float ballError = Math.Max(
                (trajectory.Ball(StandingLocomotionAnatomicalSide.Left, time) - left).Length(),
                (trajectory.Ball(StandingLocomotionAnatomicalSide.Right, time) - right).Length());
            double keyPosition = time * trajectory.KeyRateHz;
            if (Math.Abs(keyPosition - Math.Round(keyPosition)) < 1e-6 || time >= trajectory.DurationSeconds)
            {
                ballAtKeys = Math.Max(ballAtKeys, ballError);
            }
            else
            {
                ballBetweenKeys = Math.Max(ballBetweenKeys, ballError);
            }
        }

        return new DenseComparison(planarError, yawError, ballAtKeys, ballBetweenKeys);
    }

    /// <summary>
    /// Oracle player on the bare model with non-looping copies of the package clips, so a seek or playback to the
    /// clip end evaluates the final key rather than wrapping to the start.
    /// </summary>
    private static AnimationPlayer CreateOraclePlayer(Node3D model, AnimationLibrary source)
    {
        var copy = new AnimationLibrary();
        foreach (StringName name in source.GetAnimationList())
        {
            var animation = (Animation)source.GetAnimation(name).Duplicate(true);
            animation.LoopMode = Animation.LoopModeEnum.None;
            _ = copy.AddAnimation(name, animation);
        }

        var player = new AnimationPlayer { Name = "OraclePlayer" };
        model.AddChild(player);
        player.RootNode = player.GetPathTo(model);
        player.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
        _ = player.AddAnimationLibrary(LibraryName, copy);
        _ = player.AddAnimationLibrary(PackageLibraryName, source);
        return player;
    }

    /// <summary>Manually advanced root-motion tree over the unmodified package clips, configured as the NPC tree.</summary>
    private static AnimationTree CreateRootMotionTree(Node3D model, AnimationPlayer player)
    {
        var tree = new AnimationTree { Name = "OracleTree", Active = false };
        model.AddChild(tree);
        tree.RootNode = tree.GetPathTo(model);
        tree.AnimPlayer = tree.GetPathTo(player);
        tree.RootMotionTrack = _rootMotionTrack;
        tree.RootMotionLocal = true;
        tree.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
        return tree;
    }

    private readonly record struct DenseComparison(float Planar, float Yaw, float BallAtKeys, float BallBetweenKeys);

    // Reference-rig skeleton space: avatar forward is +Z and avatar right is -X.
    private static Vector2 SkeletonToSemantic(Vector3 value) => new(-value.X, value.Z);

    private static Vector3 SkeletonToActorLocal(Vector3 value) => new(-value.X, value.Y, -value.Z);

    private static float YawOf(Quaternion rotation) => 2f * MathF.Atan2(rotation.Y, rotation.W);
}
