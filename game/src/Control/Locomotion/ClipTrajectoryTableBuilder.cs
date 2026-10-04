using System.Collections.Immutable;
using Godot;

namespace AlleyCat.Control.Locomotion;

/// <summary>
/// Bind-time builder of a <see cref="ClipTrajectoryTable"/> from a package <see cref="AnimationLibrary"/> and the rig's
/// <see cref="Skeleton3D"/>. This is the interim source of the ANIM-002 dense trajectory data until the pipeline
/// publishes it; it runs once per binding, never per frame.
/// </summary>
/// <remarks>
/// <para>
/// Root samples come directly from the root-motion bone's position and rotation keys at the clip's native key rate,
/// in source units (ANIM-002 interim root derivation). Root tracks must be linearly interpolated with every key on
/// the native-rate grid, so the linear table reproduces them exactly; the declared root bounds cover float precision
/// only.
/// </para>
/// <para>
/// Ball samples are rig-specific runtime data (NAV-001 Technical Requirement 7): a forward-kinematics pass over the
/// bound rig's rest pose and the clip's bone tracks, applying <see cref="Skeleton3D.MotionScale"/> once to every
/// position track value, as the animation mixer does. They stay in the rig's runtime metres. Ball paths are not linear
/// in time, so the build measures the linear-interpolation error between keys against the same forward-kinematics
/// pass and rejects a clip whose error exceeds <see cref="InterimBallErrorBound"/>.
/// </para>
/// <para>
/// Both ball-joint bones (<see cref="LeftBallBone"/> and <see cref="RightBallBone"/>) are required rig data; a rig
/// missing either fails the build with an error naming the bone.
/// </para>
/// <para>
/// Keys are interpolated directly (linear for position and scale, spherical-linear for rotation, or nearest for
/// non-root tracks), rather than through <c>Animation.PositionTrackInterpolate</c>, because a looping track's
/// interpolation wraps the clip start onto the final key.
/// </para>
/// <para>
/// Supported rigs: the reference-rig skeleton axes only (avatar forward +Z and right -X in skeleton space, the rig
/// container yawed 180°), and root rotation about skeleton +Y only; a root key with any other rotation component is
/// rejected.
/// </para>
/// </remarks>
public static class ClipTrajectoryTableBuilder
{
    /// <summary>Ball-joint bone of the left foot.</summary>
    public const string LeftBallBone = "LeftToes";

    /// <summary>Ball-joint bone of the right foot.</summary>
    public const string RightBallBone = "RightToes";

    /// <summary>Declared root planar interpolation bound of the interim derivation, in source metres (0.1 mm).</summary>
    public const float InterimSourceRootPlanarErrorBound = 0.0001f;

    /// <summary>Declared yaw interpolation bound of the interim derivation, in radians (0.01°).</summary>
    public static readonly float InterimYawErrorBound = Mathf.DegToRad(0.01f);

    /// <summary>
    /// Provisional declared ball interpolation bound, in runtime metres (5 mm). Measured between keys against engine
    /// evaluation: at most 2.45 mm on the reference female rig and 2.82 mm on the reference male rig.
    /// </summary>
    public const float InterimBallErrorBound = 0.005f;

    /// <summary>Sub-key probes per native interval used to measure the ball interpolation error.</summary>
    private const int BallErrorProbesPerInterval = 4;

    /// <summary>Largest non-yaw root rotation component accepted for a pure-Y root rotation.</summary>
    private const float MaximumRootTiltComponent = 1e-4f;

    // Reference-rig skeleton-local avatar axes: forward is +Z and right is -X (the rig container is yawed 180°).
    private static readonly Vector3 _skeletonAvatarForward = Vector3.Back;
    private static readonly Vector3 _skeletonAvatarRight = Vector3.Left;

    /// <summary>Builds and validates the trajectory table of every catalogue clip for one rig.</summary>
    /// <param name="catalogue">Runtime package catalogue whose clips the table covers.</param>
    /// <param name="library">The catalogue's package library.</param>
    /// <param name="skeleton">The bound rig skeleton; supplies rest poses and the motion scale.</param>
    /// <param name="rootMotionTrack">
    /// The mixer's root-motion track path (for example <c>%GeneralSkeleton:Root</c>). Its node part prefixes every bone
    /// track path and its sub-name is the root bone.
    /// </param>
    /// <exception cref="InvalidDataException">A clip, track or bone is missing or inconsistent with the catalogue.</exception>
    public static ClipTrajectoryTable Build(
        StandingLocomotionCatalogue catalogue,
        AnimationLibrary library,
        Skeleton3D skeleton,
        NodePath rootMotionTrack)
        => Build(catalogue, library, skeleton, rootMotionTrack, []);

    /// <summary>
    /// Builds and validates the trajectory table of every catalogue clip for one rig, plus the uncatalogued clips
    /// (Idle) the rig's AnimationTree joins to them, so crossfades involving those clips are measurable too.
    /// </summary>
    /// <param name="catalogue">Runtime package catalogue whose clips the table covers.</param>
    /// <param name="library">The catalogue's package library.</param>
    /// <param name="skeleton">The bound rig skeleton; supplies rest poses and the motion scale.</param>
    /// <param name="rootMotionTrack">
    /// The mixer's root-motion track path (for example <c>%GeneralSkeleton:Root</c>). Its node part prefixes every bone
    /// track path and its sub-name is the root bone.
    /// </param>
    /// <param name="uncatalogued">Uncatalogued clips to add, each with its own animation resource.</param>
    /// <exception cref="InvalidDataException">A clip, track or bone is missing or inconsistent with the catalogue.</exception>
    public static ClipTrajectoryTable Build(
        StandingLocomotionCatalogue catalogue,
        AnimationLibrary library,
        Skeleton3D skeleton,
        NodePath rootMotionTrack,
        IEnumerable<UncataloguedClip> uncatalogued)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(rootMotionTrack);
        ArgumentNullException.ThrowIfNull(uncatalogued);
        var rig = RigBinding.Create(skeleton, rootMotionTrack);

        List<ClipTrajectory> trajectories = [];
        foreach (StandingLocomotionClip clip in catalogue.Clips.Values)
        {
            if (!library.HasAnimation(clip.Identity))
            {
                throw new InvalidDataException($"Package library has no animation '{clip.Identity}'.");
            }

            trajectories.Add(BuildClip(clip.Identity, clip.Role, clip.Capability, library.GetAnimation(clip.Identity), rig));
        }

        foreach (UncataloguedClip clip in uncatalogued)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(clip.Identity);
            ArgumentException.ThrowIfNullOrWhiteSpace(clip.Role);
            ArgumentNullException.ThrowIfNull(clip.Animation);
            if (Math.Abs(clip.Animation.Length - clip.DurationSeconds) > 1e-4d)
            {
                throw new InvalidDataException(
                    FormattableString.Invariant(
                        $"Uncatalogued clip '{clip.Identity}' animation length {clip.Animation.Length} differs from its declared duration {clip.DurationSeconds}."));
            }

            trajectories.Add(BuildClip(clip.Identity, clip.Role, null, clip.Animation, rig));
        }

        var table = new ClipTrajectoryTable(rig.MotionScale, trajectories);
        table.ValidateAgainst(catalogue);
        return table;
    }

    private static ClipTrajectory BuildClip(
        string identity,
        string role,
        StandingLocomotionCapability? capability,
        Animation animation,
        RigBinding rig)
    {
        NodePath rootPath = new($"{rig.SkeletonPath}:{rig.RootBoneName}");
        int rootPositionTrack = animation.FindTrack(rootPath, Animation.TrackType.Position3D);
        if (rootPositionTrack < 0)
        {
            throw new InvalidDataException($"Animation '{identity}' has no root position track '{rootPath}'.");
        }

        var rootPosition = TrackSampler.Create(animation, rootPositionTrack);
        var rootRotation = TrackSampler.TryCreate(animation, rootPath, Animation.TrackType.Rotation3D);
        rootPosition.RequireLinear(identity);
        rootRotation?.RequireLinear(identity);
        rootRotation?.RequirePureYaw(identity);
        BoneSamplers[] left = CreateChainSamplers(animation, rig.Skeleton, rig.SkeletonPath, rig.LeftChain);
        BoneSamplers[] right = CreateChainSamplers(animation, rig.Skeleton, rig.SkeletonPath, rig.RightChain);
        double keyRate = TrackSampler.NativeKeyRate(identity, rootPosition, rootRotation, left, right);
        double duration = animation.Length;
        float motionScale = rig.MotionScale;

        int count = ClipTrajectory.ExpectedSampleCount(duration, keyRate);
        ImmutableArray<Vector2>.Builder planar = ImmutableArray.CreateBuilder<Vector2>(count);
        ImmutableArray<float>.Builder yaw = ImmutableArray.CreateBuilder<float>(count);
        ImmutableArray<Vector3>.Builder leftBall = ImmutableArray.CreateBuilder<Vector3>(count);
        ImmutableArray<Vector3>.Builder rightBall = ImmutableArray.CreateBuilder<Vector3>(count);
        float previousYaw = 0f;
        for (int index = 0; index < count; index++)
        {
            double time = ClipTrajectory.SampleTimeOf(index, duration, keyRate);
            Vector3 position = rootPosition.SampleVector(time);
            planar.Add(new Vector2(position.Dot(_skeletonAvatarRight), position.Dot(_skeletonAvatarForward)));

            Quaternion rotation = rootRotation?.SampleRotation(time) ?? Quaternion.Identity;
            float wrapped = 2f * MathF.Atan2(rotation.Y, rotation.W);
            float unwrapped = index == 0 ? wrapped : previousYaw + Mathf.Wrap(wrapped - previousYaw, -Mathf.Pi, Mathf.Pi);
            yaw.Add(unwrapped);
            previousYaw = unwrapped;

            leftBall.Add(EvaluateBall(left, time, motionScale));
            rightBall.Add(EvaluateBall(right, time, motionScale));
        }

        ImmutableArray<Vector3> leftSamples = leftBall.MoveToImmutable();
        ImmutableArray<Vector3> rightSamples = rightBall.MoveToImmutable();
        float ballError = Math.Max(
            MeasureBallInterpolationError(left, leftSamples, duration, keyRate, motionScale),
            MeasureBallInterpolationError(right, rightSamples, duration, keyRate, motionScale));
        RequireBallBound(identity, ballError);
        return new ClipTrajectory(
            identity,
            role,
            capability,
            duration,
            keyRate,
            planar.MoveToImmutable(),
            yaw.MoveToImmutable(),
            leftSamples,
            rightSamples,
            motionScale,
            new ClipTrajectoryErrorBounds(InterimSourceRootPlanarErrorBound, InterimYawErrorBound, InterimBallErrorBound));
    }

    private static void RequireBallBound(string clip, float error)
    {
        if (error > InterimBallErrorBound)
        {
            throw new InvalidDataException(
                FormattableString.Invariant(
                    $"Ball interpolation error of '{clip}' is {error * 1000f:0.###} mm, above the declared {InterimBallErrorBound * 1000f:0.###} mm bound."));
        }
    }

    /// <summary>Runtime-unit ball position in the root frame, in actor-local axes.</summary>
    private static Vector3 EvaluateBall(BoneSamplers[] chain, double time, float motionScale)
        => ToActorLocal(EvaluateChain(chain, time, motionScale));

    /// <summary>Largest distance between linear interpolation of the samples and the evaluated ball inside each key interval.</summary>
    private static float MeasureBallInterpolationError(
        BoneSamplers[] chain,
        ImmutableArray<Vector3> samples,
        double duration,
        double keyRate,
        float motionScale)
    {
        float error = 0f;
        for (int index = 0; index < samples.Length - 1; index++)
        {
            double start = ClipTrajectory.SampleTimeOf(index, duration, keyRate);
            double end = ClipTrajectory.SampleTimeOf(index + 1, duration, keyRate);
            for (int probe = 1; probe < BallErrorProbesPerInterval; probe++)
            {
                float weight = probe / (float)BallErrorProbesPerInterval;
                double time = start + ((end - start) * weight);
                Vector3 interpolated = samples[index].Lerp(samples[index + 1], weight);
                error = Math.Max(error, interpolated.DistanceTo(EvaluateBall(chain, time, motionScale)));
            }
        }

        return error;
    }

    /// <summary>Ball origin in the root bone's frame: the composed local poses of the chain below the root.</summary>
    private static Vector3 EvaluateChain(BoneSamplers[] chain, double time, float motionScale)
    {
        Transform3D pose = Transform3D.Identity;
        foreach (BoneSamplers bone in chain)
        {
            pose *= bone.LocalPose(time, motionScale);
        }

        return pose.Origin;
    }

    private static Vector3 ToActorLocal(Vector3 skeletonLocal)
        => new(skeletonLocal.Dot(_skeletonAvatarRight), skeletonLocal.Y, -skeletonLocal.Dot(_skeletonAvatarForward));

    private static BoneSamplers[] CreateChainSamplers(
        Animation animation,
        Skeleton3D skeleton,
        string skeletonPath,
        int[] chain)
    {
        var samplers = new BoneSamplers[chain.Length];
        for (int index = 0; index < chain.Length; index++)
        {
            int bone = chain[index];
            var path = new NodePath($"{skeletonPath}:{skeleton.GetBoneName(bone)}");
            samplers[index] = new BoneSamplers(
                skeleton.GetBoneRest(bone),
                TrackSampler.TryCreate(animation, path, Animation.TrackType.Position3D),
                TrackSampler.TryCreate(animation, path, Animation.TrackType.Rotation3D),
                TrackSampler.TryCreate(animation, path, Animation.TrackType.Scale3D));
        }

        return samplers;
    }

    /// <summary>The chain down to a required ball-joint bone.</summary>
    /// <exception cref="InvalidDataException">The rig has no such bone.</exception>
    private static int[] BallChain(Skeleton3D skeleton, int root, string ballBone)
        => ChainBelow(skeleton, root, RequireBone(skeleton, ballBone));

    private static int RequireBone(Skeleton3D skeleton, string name)
    {
        int bone = skeleton.FindBone(name);
        return bone >= 0 ? bone : throw new InvalidDataException($"Skeleton '{skeleton.Name}' has no bone '{name}'.");
    }

    /// <summary>Bones from the root bone's child down to <paramref name="leaf"/>, root side first.</summary>
    private static int[] ChainBelow(Skeleton3D skeleton, int root, int leaf)
    {
        List<int> chain = [];
        for (int bone = leaf; bone != root; bone = skeleton.GetBoneParent(bone))
        {
            if (bone < 0)
            {
                throw new InvalidDataException(
                    $"Bone '{skeleton.GetBoneName(leaf)}' does not descend from root bone '{skeleton.GetBoneName(root)}'.");
            }

            chain.Add(bone);
        }

        chain.Reverse();
        return [.. chain];
    }

    /// <summary>The rig-side inputs every clip of one build shares.</summary>
    private sealed record RigBinding(
        Skeleton3D Skeleton,
        string SkeletonPath,
        string RootBoneName,
        int[] LeftChain,
        int[] RightChain,
        float MotionScale)
    {
        public static RigBinding Create(Skeleton3D skeleton, NodePath rootMotionTrack)
        {
            if (rootMotionTrack.GetSubNameCount() != 1)
            {
                throw new InvalidDataException($"Root-motion track '{rootMotionTrack}' must name exactly one skeleton bone.");
            }

            string rootBoneName = rootMotionTrack.GetSubName(0);
            int rootBone = RequireBone(skeleton, rootBoneName);
            return new RigBinding(
                skeleton,
                rootMotionTrack.GetConcatenatedNames(),
                rootBoneName,
                BallChain(skeleton, rootBone, LeftBallBone),
                BallChain(skeleton, rootBone, RightBallBone),
                skeleton.MotionScale);
        }
    }

    private sealed class BoneSamplers(
        Transform3D rest,
        TrackSampler? position,
        TrackSampler? rotation,
        TrackSampler? scale)
    {
        private readonly Quaternion _restRotation = rest.Basis.GetRotationQuaternion();
        private readonly Vector3 _restScale = rest.Basis.Scale;

        /// <summary>Smallest key interval over the bone's tracks, or infinity when no track has two keys.</summary>
        public double SmallestKeyInterval()
            => Math.Min(
                position?.SmallestKeyInterval() ?? double.PositiveInfinity,
                Math.Min(rotation?.SmallestKeyInterval() ?? double.PositiveInfinity, scale?.SmallestKeyInterval() ?? double.PositiveInfinity));

        /// <summary>Bone pose as Skeleton3D composes it: position, then rotation and local scale.</summary>
        public Transform3D LocalPose(double time, float motionScale)
        {
            Vector3 origin = position is null ? rest.Origin : position.SampleVector(time) * motionScale;
            Quaternion orientation = rotation?.SampleRotation(time) ?? _restRotation;
            Vector3 size = scale?.SampleVector(time) ?? _restScale;
            return new Transform3D(new Basis(orientation) * Basis.FromScale(size), origin);
        }
    }

    /// <summary>Direct key interpolation of one transform track.</summary>
    private sealed class TrackSampler
    {
        private readonly double[] _times;
        private readonly Vector3[] _vectors;
        private readonly Quaternion[] _rotations;
        private readonly Animation.InterpolationType _interpolation;

        private TrackSampler(
            double[] times,
            Vector3[] vectors,
            Quaternion[] rotations,
            Animation.InterpolationType interpolation)
        {
            _times = times;
            _vectors = vectors;
            _rotations = rotations;
            _interpolation = interpolation;
        }

        public static TrackSampler? TryCreate(Animation animation, NodePath path, Animation.TrackType type)
        {
            int track = animation.FindTrack(path, type);
            return track < 0 || !animation.TrackIsEnabled(track) ? null : Create(animation, track);
        }

        public static TrackSampler Create(Animation animation, int track)
        {
            int count = animation.TrackGetKeyCount(track);
            if (count == 0)
            {
                throw new InvalidDataException($"Track '{animation.TrackGetPath(track)}' of '{animation.ResourceName}' has no keys.");
            }

            Animation.InterpolationType interpolation = animation.TrackGetInterpolationType(track);
            if (interpolation is not (Animation.InterpolationType.Nearest or Animation.InterpolationType.Linear or Animation.InterpolationType.LinearAngle))
            {
                throw new InvalidDataException(
                    $"Track '{animation.TrackGetPath(track)}' of '{animation.ResourceName}' uses unsupported {interpolation} interpolation.");
            }

            bool rotation = animation.TrackGetType(track) == Animation.TrackType.Rotation3D;
            double[] times = new double[count];
            Vector3[] vectors = rotation ? [] : new Vector3[count];
            Quaternion[] rotations = rotation ? new Quaternion[count] : [];
            for (int key = 0; key < count; key++)
            {
                times[key] = animation.TrackGetKeyTime(track, key);
                Variant value = animation.TrackGetKeyValue(track, key);
                if (rotation)
                {
                    rotations[key] = value.AsQuaternion();
                }
                else
                {
                    vectors[key] = value.AsVector3();
                }
            }

            return new TrackSampler(times, vectors, rotations, interpolation);
        }

        /// <summary>
        /// Rejects root tracks whose interpolation a linear native-rate table cannot reproduce: a Nearest track is
        /// accepted only when it is constant, since its steps would otherwise be smeared across a key interval.
        /// </summary>
        public void RequireLinear(string clip)
        {
            if (_interpolation == Animation.InterpolationType.Nearest && !IsConstant())
            {
                throw new InvalidDataException($"Root track of '{clip}' uses Nearest interpolation; root tracks must be linear.");
            }
        }

        private bool IsConstant()
        {
            for (int key = 1; key < _times.Length; key++)
            {
                bool same = _rotations.Length > 0
                    ? _rotations[key].IsEqualApprox(_rotations[0])
                    : _vectors[key].IsEqualApprox(_vectors[0]);
                if (!same)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Rejects root rotation keys with any rotation component other than skeleton +Y.</summary>
        public void RequirePureYaw(string clip)
        {
            foreach (Quaternion rotation in _rotations)
            {
                if (Math.Abs(rotation.X) > MaximumRootTiltComponent || Math.Abs(rotation.Z) > MaximumRootTiltComponent)
                {
                    throw new InvalidDataException($"Root rotation of '{clip}' is not a pure yaw about skeleton +Y.");
                }
            }
        }

        /// <summary>
        /// Key rate implied by the smallest key interval across the root tracks; every root key must lie on that
        /// rate's grid, so linear samples at that rate reproduce the tracks exactly. A clip whose root tracks are
        /// constant single keys (Idle) takes the rate of its ball chains' tracks instead.
        /// </summary>
        public static double NativeKeyRate(
            string clip,
            TrackSampler position,
            TrackSampler? rotation,
            BoneSamplers[] leftChain,
            BoneSamplers[] rightChain)
        {
            double interval = Math.Min(position.SmallestKeyInterval(), rotation?.SmallestKeyInterval() ?? double.PositiveInfinity);
            if (!double.IsFinite(interval))
            {
                foreach (BoneSamplers bone in leftChain.Concat(rightChain))
                {
                    interval = Math.Min(interval, bone.SmallestKeyInterval());
                }
            }

            if (!double.IsFinite(interval) || interval <= 0d)
            {
                throw new InvalidDataException($"Root and ball-chain tracks of '{clip}' need at least two distinct keys.");
            }

            double rate = Math.Round(1d / interval);
            position.RequireKeysOnGrid(clip, rate);
            rotation?.RequireKeysOnGrid(clip, rate);
            return rate;
        }

        public double SmallestKeyInterval()
        {
            double interval = double.PositiveInfinity;
            for (int key = 1; key < _times.Length; key++)
            {
                interval = Math.Min(interval, _times[key] - _times[key - 1]);
            }

            return interval;
        }

        private void RequireKeysOnGrid(string clip, double rate)
        {
            foreach (double time in _times)
            {
                if (Math.Abs((time * rate) - Math.Round(time * rate)) > 1e-3d)
                {
                    throw new InvalidDataException($"Root track key {time} s of '{clip}' is off its {rate} Hz key grid.");
                }
            }
        }

        public Vector3 SampleVector(double time)
        {
            Locate(time, out int index, out float weight);
            Vector3 from = _vectors[index];
            return weight <= 0f ? from : from.Lerp(_vectors[index + 1], weight);
        }

        public Quaternion SampleRotation(double time)
        {
            Locate(time, out int index, out float weight);
            Quaternion from = _rotations[index];
            return weight <= 0f ? from : from.Slerp(_rotations[index + 1], weight);
        }

        private void Locate(double time, out int index, out float weight)
        {
            weight = 0f;
            if (time <= _times[0] || _times.Length == 1)
            {
                index = 0;
                return;
            }

            if (time >= _times[^1])
            {
                index = _times.Length - 1;
                return;
            }

            int found = Array.BinarySearch(_times, time);
            index = found >= 0 ? found : ~found - 1;
            if (found >= 0 || _interpolation == Animation.InterpolationType.Nearest)
            {
                return;
            }

            weight = (float)((time - _times[index]) / (_times[index + 1] - _times[index]));
        }
    }
}

/// <summary>
/// A clip outside the standing-locomotion catalogue (Idle) that the rig's AnimationTree joins to catalogue clips.
/// </summary>
/// <param name="Identity">Animation identity within its library.</param>
/// <param name="Role">Package role; must not repeat a catalogue role.</param>
/// <param name="DurationSeconds">Declared clip duration, checked against the animation length.</param>
/// <param name="Animation">The bound animation resource.</param>
public readonly record struct UncataloguedClip(string Identity, string Role, double DurationSeconds, Animation Animation);
