using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Binding-time sampler for the immutable Blender-authored single-frame thumb reference animations
/// (XR-002 TR25): loads both configured <see cref="Animation" /> resources directly through
/// <see cref="ResourceLoader" />, resolves and reads the required single-key <c>Rotation3D</c> tracks exactly,
/// and runs the Reset forward kinematics that feeds the authored thumb maths.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Direct immutable resource contract (XR-002 TR25.1-25.2).</strong> Resources are loaded directly by
/// path and never registered on a live animation node: there is no <c>AnimationPlayer</c> resolution, no
/// <c>AnimationLibrary</c> registration, and no playback dependency. Sampling never calls <c>Play</c>,
/// <c>Seek</c>, or <c>Advance</c>; never mutates tracks, keys, an <see cref="Animation" />,
/// <c>AnimationPlayer</c>, <c>AnimationTree</c>, mixer parameter, skeleton pose, or any other resource; and
/// never reads live pose state.
/// </para>
/// <para>
/// <strong>Track contract (XR-002 TR25.3-25.6).</strong> Exactly one enabled <c>Rotation3D</c> track must
/// resolve for each required canonical bone path <c>%GeneralSkeleton:&lt;CanonicalBoneName&gt;</c>, matched
/// exactly with no fuzzy, suffix, basename, or case-insensitive matching. Every required track must hold
/// exactly one key — key 0 at <c>t=0</c> — read directly through the resource track API with no
/// interpolation. Binding fails closed, with an actionable error naming the side, bone, resource, and
/// violated contract, on a missing resource; a missing, duplicate, wrong-type, or disabled required track; a
/// wrong key count or key time; or an invalid quaternion — non-finite, near-zero, or failing
/// <c>|length² − 1| ≤ 0.001</c>. There is no silent final-key selection and no <see cref="Animation" />
/// length semantics for hypothetical multi-key clips.
/// </para>
/// <para>
/// <strong>Reset forward kinematics (XR-002 TR28.2).</strong> Reset keys are complete absolute parent-local
/// pose rotations and are never multiplied by imported rest rotations:
/// <c>Q_b^R = Q_parent(b)^R × q_b^R</c> and <c>p_b^R = p_parent(b)^R + Q_parent(b)^R × o_b</c> with
/// <c>o_b = GetBoneRest(b).origin</c>. An animated root-to-hand ancestor contributes its Reset key; a
/// non-animated ancestor contributes its imported rest-local polar rotation through the scale-tolerant
/// <see cref="ThumbRestBasisMath" /> extraction. The current references qualify structurally: the finger
/// and hand origins are not position-animated, the proximal roots are direct children of the hand bone, and
/// the thumb-proximal scale track is downstream of every queried point — a position or scale track on a
/// Reset FK queried bone, or a scale track anywhere on the root-to-hand FK path, fails the binding. A
/// position track strictly above the wrist translates the subtree rigidly and cancels exactly in every
/// hand-relative quantity, so the reference assets' root/hips position tracks are tolerated.
/// </para>
/// <para>
/// Everything here runs only at skeleton binding time; nothing allocates or executes in the per-frame hot
/// path.
/// </para>
/// </remarks>
public static class AuthoredThumbReferenceSampler
{
    /// <summary>Exact track-path prefix of the authored reference animations (XR-002 TR25.3).</summary>
    public const string TrackPathPrefix = "%GeneralSkeleton:";

    private const double KeyTimeEpsilon = 1e-6;
    private const float QuaternionUnitTolerance = 0.001f;

    private enum TrackResolution
    {
        Missing,
        Found,
        Duplicate,
    }

    /// <summary>
    /// Loads both reference resources directly and samples every required key plus the Reset forward
    /// kinematics for both sides, producing the pure-maths inputs (XR-002 TR25, TR28.2).
    /// </summary>
    public static bool TrySample(
        string neutralPath,
        string flexionPath,
        Skeleton3D skeleton,
        out AuthoredThumbSideReferences leftReferences,
        out AuthoredThumbSideReferences rightReferences,
        out string error)
    {
        leftReferences = default;
        rightReferences = default;
        if (!TryLoad(neutralPath, "neutral", out Animation? neutral, out error)
            || !TryLoad(flexionPath, "flexion", out Animation? flexion, out error))
        {
            return false;
        }

        List<string> ancestors = new(16);

        if (!TrySampleSide(neutral!, flexion!, skeleton, LimbSide.Left, out leftReferences, ancestors, out error)
            || !TrySampleSide(neutral!, flexion!, skeleton, LimbSide.Right, out rightReferences, ancestors, out error))
        {
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Loads one reference resource directly from a non-empty project-relative path, failing closed on an
    /// empty path, an unresolvable resource, or a non-<see cref="Animation" /> resource (XR-002 TR25.1).
    /// </summary>
    public static bool TryLoad(
        string path,
        string role,
        out Animation? animation,
        out string error)
    {
        animation = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = $"The authored {role} reference animation path is empty; a non-empty project-relative " +
                "path is required.";
            return false;
        }

        Resource? resource = ResourceLoader.Load(path);
        if (resource is null)
        {
            error = $"The authored {role} reference animation '{path}' could not be loaded; binding fails closed.";
            return false;
        }

        if (resource is not Animation loaded)
        {
            error = $"The authored {role} reference '{path}' resolved to {resource.GetType().Name} rather than " +
                "an Animation resource; binding fails closed.";
            return false;
        }

        animation = loaded;
        error = string.Empty;
        return true;
    }

    private static bool TrySampleSide(
        Animation neutral,
        Animation flexion,
        Skeleton3D skeleton,
        LimbSide side,
        out AuthoredThumbSideReferences references,
        List<string> ancestors,
        out string error)
    {
        references = default;
        string prefix = side == LimbSide.Left ? "Left" : "Right";

        int handBoneIndex = skeleton.FindBone(prefix + "Hand");
        int wristBoneIndex = handBoneIndex >= 0 ? skeleton.GetBoneParent(handBoneIndex) : -1;
        if (handBoneIndex < 0 || wristBoneIndex < 0)
        {
            error = $"{prefix} hand: the authored Reset forward kinematics requires a {prefix}Hand bone with a " +
                "parent wrist bone on the bound skeleton.";
            return false;
        }

        string wristBoneName = skeleton.GetBoneName(wristBoneIndex).ToString();
        string handBoneName = skeleton.GetBoneName(handBoneIndex).ToString();
        string[] thumbBones =
        [
            prefix + "ThumbMetacarpal",
            prefix + "ThumbProximal",
            prefix + "ThumbDistal",
        ];
        string[] proximalRootBones =
        [
            prefix + "IndexProximal",
            prefix + "MiddleProximal",
            prefix + "RingProximal",
            prefix + "LittleProximal",
        ];

        // Required Reset rotation tracks: wrist, hand, the four proximal roots, and the three thumb bones
        // (XR-002 TR25.3, TR28.2). The flexion reference requires the six thumb bones only.
        if (!TryReadRequiredKey(neutral, wristBoneName, out _, out error)
            || !TryReadRequiredKey(neutral, handBoneName, out Quaternion handKey, out error))
        {
            error = $"{prefix} hand: {error}";
            return false;
        }

        var rootKeys = new Quaternion[proximalRootBones.Length];
        int[] rootBoneIndices = new int[proximalRootBones.Length];
        for (int rootIndex = 0; rootIndex < proximalRootBones.Length; rootIndex++)
        {
            string boneName = proximalRootBones[rootIndex];
            int boneIndex = skeleton.FindBone(boneName);
            if (boneIndex < 0 || skeleton.GetBoneParent(boneIndex) != handBoneIndex)
            {
                error = $"{prefix} hand: the Reset FK requires {boneName} to be a direct child of {prefix}Hand " +
                    "on the bound skeleton.";
                return false;
            }

            if (!TryReadRequiredKey(neutral, boneName, out rootKeys[rootIndex], out error))
            {
                error = $"{prefix} hand: {error}";
                return false;
            }

            rootBoneIndices[rootIndex] = boneIndex;
        }

        var resetKeys = new Quaternion[thumbBones.Length];
        var flexionKeys = new Quaternion[thumbBones.Length];
        int[] thumbBoneIndices = new int[thumbBones.Length];
        for (int thumbIndex = 0; thumbIndex < thumbBones.Length; thumbIndex++)
        {
            string boneName = thumbBones[thumbIndex];
            int boneIndex = skeleton.FindBone(boneName);
            if (!TryReadRequiredKey(neutral, boneName, out resetKeys[thumbIndex], out error)
                || !TryReadRequiredKey(flexion, boneName, out flexionKeys[thumbIndex], out error))
            {
                error = $"{prefix} hand: {error}";
                return false;
            }

            thumbBoneIndices[thumbIndex] = boneIndex;
        }

        // Ancestor Reset rotations from the skeleton root down to the wrist: an animated ancestor contributes
        // its key; a non-animated ancestor contributes its imported rest-local polar rotation (XR-002 TR28.2).
        List<(Quaternion LocalRotation, Vector3 RestOrigin)> chain = [];
        for (int boneIndex = wristBoneIndex; boneIndex >= 0; boneIndex = skeleton.GetBoneParent(boneIndex))
        {
            string boneName = skeleton.GetBoneName(boneIndex).ToString();
            TrackResolution resolution = ResolveRotationTrack(neutral, boneName, out int trackIndex);
            if (resolution == TrackResolution.Duplicate)
            {
                error = $"{prefix} ancestor {boneName}: the authored neutral reference '{neutral.ResourcePath}' " +
                    $"carries multiple enabled Rotation3D tracks matching '{TrackPathPrefix}{boneName}'; " +
                    "exactly one is required.";
                return false;
            }

            if (resolution == TrackResolution.Found)
            {
                if (!TryReadKey(neutral, trackIndex, out Quaternion key, out error))
                {
                    error = $"{prefix} ancestor {boneName}: {error}";
                    return false;
                }

                chain.Add((key, skeleton.GetBoneRest(boneIndex).Origin));
                ancestors.Add($"{prefix}:{boneName}=reset-key");
            }
            else
            {
                if (!ThumbRestBasisMath.TryExtractRotation(
                        skeleton.GetBoneRest(boneIndex).Basis,
                        $"{prefix} ancestor {boneName} rest local",
                        out Quaternion restRotation,
                        out error))
                {
                    error = $"{prefix} hand: {error}";
                    return false;
                }

                chain.Add((restRotation, skeleton.GetBoneRest(boneIndex).Origin));
                ancestors.Add($"{prefix}:{boneName}=imported-rest-polar");
            }
        }

        // Position/scale structural qualification (XR-002 TR28.2): the current references qualify because the
        // queried origins are not position-animated and no scale channel reaches the FK path.
        if (!ValidateNeutralTransformTracks(neutral, skeleton, wristBoneIndex, handBoneIndex, proximalRootBones, prefix, out error))
        {
            return false;
        }

        // Reset forward kinematics (XR-002 TR28.2): absolute rotations compose parent-left, positions
        // accumulate through the parent's absolute Reset rotation times the imported rest origin. The chain
        // is wrist-first, so the walk starts at the root (last entry) and descends to the wrist.
        Quaternion wristGlobal = Quaternion.Identity;
        Vector3 wristPosition = Vector3.Zero;
        for (int index = chain.Count - 1; index >= 0; index--)
        {
            (Quaternion localRotation, Vector3 restOrigin) = chain[index];
            if (index == chain.Count - 1)
            {
                wristGlobal = localRotation.Normalized();
                wristPosition = restOrigin;
                continue;
            }

            wristPosition += new Basis(wristGlobal) * restOrigin;
            wristGlobal = (wristGlobal * localRotation).Normalized();
        }

        Quaternion handGlobal = (wristGlobal * handKey).Normalized();
        Vector3 handPosition = wristPosition + (new Basis(wristGlobal) * skeleton.GetBoneRest(handBoneIndex).Origin);
        Quaternion metacarpalGlobal = (handGlobal * resetKeys[0]).Normalized();

        Vector3 indexPosition = handPosition + (new Basis(handGlobal) * skeleton.GetBoneRest(rootBoneIndices[0]).Origin);
        Vector3 middlePosition = handPosition + (new Basis(handGlobal) * skeleton.GetBoneRest(rootBoneIndices[1]).Origin);
        Vector3 ringPosition = handPosition + (new Basis(handGlobal) * skeleton.GetBoneRest(rootBoneIndices[2]).Origin);
        Vector3 littlePosition = handPosition + (new Basis(handGlobal) * skeleton.GetBoneRest(rootBoneIndices[3]).Origin);

        var geometry = new AuthoredThumbResetGeometry(
            handGlobal,
            resetKeys[0],
            metacarpalGlobal,
            skeleton.GetBoneRest(thumbBoneIndices[1]).Origin,
            wristPosition,
            handPosition,
            indexPosition,
            middlePosition,
            ringPosition,
            littlePosition);

        references = new AuthoredThumbSideReferences(
            new AuthoredThumbJointKeys(resetKeys[0], flexionKeys[0]),
            new AuthoredThumbJointKeys(resetKeys[1], flexionKeys[1]),
            new AuthoredThumbJointKeys(resetKeys[2], flexionKeys[2]),
            geometry);
        error = string.Empty;
        return true;
    }

    private static bool ValidateNeutralTransformTracks(
        Animation neutral,
        Skeleton3D skeleton,
        int wristBoneIndex,
        int handBoneIndex,
        string[] proximalRootBones,
        string prefix,
        out string error)
    {
        var queriedBones = new HashSet<string>
        {
            skeleton.GetBoneName(wristBoneIndex).ToString(),
            skeleton.GetBoneName(handBoneIndex).ToString(),
        };
        foreach (string rootBone in proximalRootBones)
        {
            _ = queriedBones.Add(rootBone);
        }

        var ancestorBones = new HashSet<string>();
        for (int boneIndex = skeleton.GetBoneParent(wristBoneIndex); boneIndex >= 0; boneIndex = skeleton.GetBoneParent(boneIndex))
        {
            _ = ancestorBones.Add(skeleton.GetBoneName(boneIndex).ToString());
        }

        for (int trackIndex = 0; trackIndex < neutral.GetTrackCount(); trackIndex++)
        {
            if (!neutral.TrackIsEnabled(trackIndex))
            {
                continue;
            }

            Animation.TrackType type = neutral.TrackGetType(trackIndex);
            if (type is not (Animation.TrackType.Position3D or Animation.TrackType.Scale3D))
            {
                continue;
            }

            string bone = TrackBoneName(neutral.TrackGetPath(trackIndex).ToString());
            if (queriedBones.Contains(bone))
            {
                error = $"{prefix} hand: the authored neutral reference '{neutral.ResourcePath}' carries an " +
                    $"enabled {type} track on '{bone}', whose Reset FK queried origin it would override; the " +
                    "references must keep finger, hand, and wrist origins free of position/scale animation.";
                return false;
            }

            if (type == Animation.TrackType.Scale3D && ancestorBones.Contains(bone))
            {
                error = $"{prefix} hand: the authored neutral reference '{neutral.ResourcePath}' carries an " +
                    $"enabled scale track on FK-path ancestor '{bone}', which would distort the Reset " +
                    "forward kinematics.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryReadRequiredKey(
        Animation animation,
        string boneName,
        out Quaternion value,
        out string error)
    {
        value = Quaternion.Identity;
        TrackResolution resolution = ResolveRotationTrack(animation, boneName, out int trackIndex);
        if (resolution != TrackResolution.Found)
        {
            string reason = resolution == TrackResolution.Duplicate
                ? $"carries multiple enabled Rotation3D tracks matching it"
                : "has no enabled Rotation3D track matching it";
            error = $"The authored reference '{animation.ResourcePath}' {reason} for the required bone path " +
                $"'{TrackPathPrefix}{boneName}'.";
            return false;
        }

        if (!TryReadValidatedKey(animation, trackIndex, out value, out _, out _, out error))
        {
            error = $"{boneName}: {error}";
            return false;
        }

        return true;
    }

    private static bool TryReadKey(Animation animation, int trackIndex, out Quaternion value, out string error)
        => TryReadValidatedKey(animation, trackIndex, out value, out _, out _, out error);

    private static bool TryReadValidatedKey(
        Animation animation,
        int trackIndex,
        out Quaternion value,
        out double keyTime,
        out int keyCount,
        out string error)
    {
        value = Quaternion.Identity;
        keyTime = 0.0;
        keyCount = animation.TrackGetKeyCount(trackIndex);
        if (keyCount != 1)
        {
            error = $"the track in the authored reference '{animation.ResourcePath}' holds {keyCount} keys; " +
                "exactly one key is required.";
            return false;
        }

        keyTime = animation.TrackGetKeyTime(trackIndex, 0);
        if (Math.Abs(keyTime) > KeyTimeEpsilon)
        {
            error = $"key 0 in the authored reference '{animation.ResourcePath}' is at t={keyTime:R}s; t=0 is " +
                "required.";
            return false;
        }

        Variant raw = animation.TrackGetKeyValue(trackIndex, 0);
        if (raw.VariantType != Variant.Type.Quaternion)
        {
            error = $"key 0 in the authored reference '{animation.ResourcePath}' is a {raw.VariantType} rather " +
                "than a Quaternion.";
            return false;
        }

        Quaternion quaternion = raw.AsQuaternion();
        if (!float.IsFinite(quaternion.X) || !float.IsFinite(quaternion.Y)
            || !float.IsFinite(quaternion.Z) || !float.IsFinite(quaternion.W)
            || quaternion.LengthSquared() <= 0.0f
            || Math.Abs(quaternion.LengthSquared() - 1.0f) > QuaternionUnitTolerance)
        {
            error = $"key 0 in the authored reference '{animation.ResourcePath}' is an invalid quaternion " +
                $"({quaternion.X:R}, {quaternion.Y:R}, {quaternion.Z:R}, {quaternion.W:R}); non-finite, " +
                $"near-zero, or failing |length² − 1| ≤ {QuaternionUnitTolerance} all fail closed.";
            return false;
        }

        value = quaternion.Normalized();
        error = string.Empty;
        return true;
    }

    private static TrackResolution ResolveRotationTrack(Animation animation, string boneName, out int trackIndex)
    {
        trackIndex = -1;
        int matches = 0;
        string expectedPath = TrackPathPrefix + boneName;
        for (int index = 0; index < animation.GetTrackCount(); index++)
        {
            if (animation.TrackGetType(index) != Animation.TrackType.Rotation3D
                || !animation.TrackIsEnabled(index)
                || animation.TrackGetPath(index).ToString() != expectedPath)
            {
                continue;
            }

            matches++;
            trackIndex = index;
        }

        return matches switch
        {
            0 => TrackResolution.Missing,
            1 => TrackResolution.Found,
            _ => TrackResolution.Duplicate,
        };
    }

    private static string TrackBoneName(string trackPath)
    {
        int separator = trackPath.LastIndexOf(':');
        return separator >= 0 ? trackPath[(separator + 1)..] : trackPath;
    }
}

/// <summary>One thumb joint's sampled absolute parent-local key pair (XR-002 TR26).</summary>
public readonly record struct AuthoredThumbJointKeys(Quaternion Reset, Quaternion Flexion);

/// <summary>
/// One side's sampled authored-reference inputs for the pure thumb maths: the three thumb joint key pairs
/// and the Reset forward-kinematics geometry (XR-002 TR25-TR26, TR28.2).
/// </summary>
public readonly record struct AuthoredThumbSideReferences(
    AuthoredThumbJointKeys Metacarpal,
    AuthoredThumbJointKeys Proximal,
    AuthoredThumbJointKeys Distal,
    AuthoredThumbResetGeometry ResetGeometry);
