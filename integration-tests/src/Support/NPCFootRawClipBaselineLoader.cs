using AlleyCat.IK;
using AlleyCat.Rigging;
using Godot;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Support;

/// <summary>Loads fixture-declared raw trajectories without an AnimationTree or route replay.</summary>
public static class NPCFootRawClipBaselineLoader
{
    public const float NativeRate = 60f;

    public static NPCFootMotionCalibration Load(
        AnimationPlayer player,
        Skeleton3D skeleton,
        IReadOnlyList<string> clips)
    {
        List<string> rejections = [];
        List<NPCFootRawClipSample> samples = [];
        if (!FootPosePointDefinition.TryCreate(skeleton, LimbSide.Left, out FootPosePointDefinition leftDefinition)
            || !FootPosePointDefinition.TryCreate(skeleton, LimbSide.Right, out FootPosePointDefinition rightDefinition))
        {
            return new(new Dictionary<string, IReadOnlyList<NPCFootRawClipSample>>(),
                new Dictionary<NPCFootBaselineKey, NPCFootMotionBand>(),
                ["Fixture skeleton is missing valid LeftFoot/LeftToes or RightFoot/RightToes point definitions."]);
        }

        int leftFoot = skeleton.FindBone(leftDefinition.FootBone);
        int leftToe = skeleton.FindBone(leftDefinition.ToeBone);
        int rightFoot = skeleton.FindBone(rightDefinition.FootBone);
        int rightToe = skeleton.FindBone(rightDefinition.ToeBone);
        int hips = skeleton.FindBone("Hips");
        int leftLower = skeleton.GetBoneParent(leftFoot);
        int leftUpper = skeleton.GetBoneParent(leftLower);
        int rightLower = skeleton.GetBoneParent(rightFoot);
        int rightUpper = skeleton.GetBoneParent(rightLower);
        int rootBone = skeleton.FindBone("Root");
        if (rootBone < 0)
        {
            return new(new Dictionary<string, IReadOnlyList<NPCFootRawClipSample>>(),
                new Dictionary<NPCFootBaselineKey, NPCFootMotionBand>(),
                ["Fixture skeleton is missing the Root bone required for clip-relative rigid transport removal."]);
        }
        foreach (string clip in clips.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(clip) || !player.HasAnimation(clip))
            {
                rejections.Add($"Missing raw/native clip '{clip}'.");
                continue;
            }
            Animation animation = player.GetAnimation(clip);
            float duration = (float)animation.Length;
            if (!float.IsFinite(duration) || duration <= 0f)
            {
                rejections.Add($"Raw/native clip '{clip}' has invalid duration {duration}.");
                continue;
            }
            int frameCount = (int)Math.Floor(duration * NativeRate) + 1;
            for (int frame = 0; frame < frameCount; frame++)
            {
                float time = frame / NativeRate;
                Transform3D[] globals = InterpolateBoneGlobals(animation, skeleton, time, out int matchedTracks);
                if (matchedTracks == 0)
                {
                    rejections.Add($"Raw/native clip '{clip}' has no tracks matching the runtime imported skeleton.");
                    break;
                }
                Transform3D rootInverse = globals[rootBone].AffineInverse();
                FootPosePointSet left = TransformPoints(
                    rootInverse,
                    leftDefinition.Evaluate(globals[leftFoot], globals[leftToe]));
                FootPosePointSet right = TransformPoints(
                    rootInverse,
                    rightDefinition.Evaluate(globals[rightFoot], globals[rightToe]));
                NPCFootLegChainPose leftChain = Chain(rootInverse, globals, hips, leftUpper, leftLower, leftFoot, leftToe);
                NPCFootLegChainPose rightChain = Chain(rootInverse, globals, hips, rightUpper, rightLower, rightFoot, rightToe);
                samples.Add(new(clip, duration, frame, time, time / duration, left, right, leftChain, rightChain));
            }
        }

        NPCFootMotionCalibration calibration = NPCFootMotionContinuityDetector.CreateCalibration(samples);
        return calibration with
        {
            Rejections = [.. rejections, .. calibration.Rejections]
        };
    }

    private static NPCFootLegChainPose Chain(Transform3D rootInverse, Transform3D[] globals,
        int hips, int upper, int lower, int foot, int toe)
        => new(rootInverse * globals[hips], rootInverse * globals[upper], rootInverse * globals[lower],
            rootInverse * globals[foot], rootInverse * globals[toe]);

    private static FootPosePointSet TransformPoints(Transform3D transform, FootPosePointSet points)
        => new(
            transform * points.Ankle,
            transform * points.HeelSole,
            transform * points.Midsole,
            transform * points.Ball,
            transform * points.ToeTip);

    private static Transform3D[] InterpolateBoneGlobals(
        Animation animation,
        Skeleton3D skeleton,
        double time,
        out int matchedTracks)
    {
        int boneCount = skeleton.GetBoneCount();
        var positions = new Vector3[boneCount];
        var rotations = new Quaternion[boneCount];
        var scales = new Vector3[boneCount];
        for (int bone = 0; bone < boneCount; bone++)
        {
            Transform3D rest = skeleton.GetBoneRest(bone);
            positions[bone] = rest.Origin;
            rotations[bone] = rest.Basis.GetRotationQuaternion();
            scales[bone] = rest.Basis.Scale;
        }

        matchedTracks = 0;
        for (int track = 0; track < animation.GetTrackCount(); track++)
        {
            if (!animation.TrackIsEnabled(track))
            {
                continue;
            }
            NodePath path = animation.TrackGetPath(track);
            if (path.GetSubNameCount() != 1)
            {
                continue;
            }
            int bone = skeleton.FindBone(path.GetSubName(0));
            if (bone < 0)
            {
                continue;
            }
            matchedTracks++;
            switch (animation.TrackGetType(track))
            {
                case Animation.TrackType.Position3D:
                    positions[bone] = animation.PositionTrackInterpolate(track, time) * skeleton.MotionScale;
                    break;
                case Animation.TrackType.Rotation3D:
                    rotations[bone] = animation.RotationTrackInterpolate(track, time);
                    break;
                case Animation.TrackType.Scale3D:
                    scales[bone] = animation.ScaleTrackInterpolate(track, time);
                    break;
                case Animation.TrackType.Value:
                    break;
                case Animation.TrackType.BlendShape:
                    break;
                case Animation.TrackType.Method:
                    break;
                case Animation.TrackType.Bezier:
                    break;
                case Animation.TrackType.Audio:
                    break;
                case Animation.TrackType.Animation:
                    break;
                default:
                    break;
            }
        }

        var globals = new Transform3D[boneCount];
        for (int bone = 0; bone < boneCount; bone++)
        {
            Transform3D local = new(new Basis(rotations[bone]).Scaled(scales[bone]), positions[bone]);
            int parent = skeleton.GetBoneParent(bone);
            globals[bone] = parent >= 0 ? globals[parent] * local : local;
        }
        return globals;
    }
}
