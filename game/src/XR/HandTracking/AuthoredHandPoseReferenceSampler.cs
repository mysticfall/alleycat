using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Generic binding-time sampler for an immutable authored single-frame hand-pose reference animation
/// (XR-002 TR52, INTR-001 TR14, TR16): loads the configured <see cref="Animation" /> resource directly through
/// <see cref="ResourceLoader" />, resolves and reads the 15 required per-side finger tracks exactly, and exposes
/// the raw one-key quaternions as the destination-local reference pose.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Direct immutable resource contract (generalising XR-002 TR25.1-25.2).</strong> The resource is loaded
/// directly by path and never registered on a live animation node: there is no <c>AnimationPlayer</c>
/// resolution, no <c>AnimationLibrary</c> registration, and no playback dependency. Sampling never calls
/// <c>Play</c>, <c>Seek</c>, or <c>Advance</c>; never mutates tracks, keys, an <see cref="Animation" />,
/// <c>AnimationPlayer</c>, <c>AnimationTree</c>, mixer parameter, skeleton pose, or any other resource; and
/// never reads live pose state.
/// </para>
/// <para>
/// <strong>Track contract.</strong> Exactly one enabled <c>Rotation3D</c> track must resolve for each required
/// canonical finger bone path <c>%GeneralSkeleton:&lt;Side&gt;&lt;Bone&gt;</c> of the requested side — the 15
/// canonical finger destinations in <see cref="XRHandJoints.DestinationJoints" /> order — matched exactly with
/// no fuzzy, suffix, basename, or case-insensitive matching. Every required track must hold exactly one key —
/// key 0 at <c>t=0</c> — read directly through the resource track API with no interpolation. Sampling fails
/// closed, with an actionable error naming the side, bone, resource, and violated contract, on: a missing
/// resource; a non-<see cref="Animation" /> resource; a missing, duplicate, wrong-type, or disabled required
/// track; a wrong key count or key time; or an invalid quaternion — non-finite, near-zero, or failing
/// <c>|length² − 1| ≤ 0.001</c>. There is no silent final-key selection and no <see cref="Animation" /> length
/// semantics for multi-key clips.
/// </para>
/// <para>
/// Everything here runs once per candidate animation — at gesture-profile derivation — and nothing allocates or
/// executes in a per-frame hot path.
/// </para>
/// </remarks>
public static class AuthoredHandPoseReferenceSampler
{
    private const double KeyTimeEpsilon = 1e-6;
    private const float QuaternionUnitTolerance = 0.001f;

    private enum TrackRule
    {
        None,
        Missing,
        Duplicate,
        Disabled,
        WrongType,
    }

    /// <summary>
    /// Loads the reference resource directly and samples the required single-key destination-local pose for one
    /// side's 15 canonical finger destinations (XR-002 TR52, TR25-generalised).
    /// </summary>
    /// <param name="animationPath">Project-relative path of the immutable one-frame reference animation.</param>
    /// <param name="side">Hand side whose finger tracks are required.</param>
    /// <param name="reference">The sampled destination-local reference pose.</param>
    /// <param name="error">Actionable failure identifying the side, bone, resource, and violated rule.</param>
    public static bool TrySample(
        string animationPath,
        LimbSide side,
        out AuthoredHandPoseSideReference reference,
        out string error)
    {
        reference = null!;

        return AuthoredThumbReferenceSampler.TryLoad(
            animationPath,
            "hand-pose reference",
            out Animation? animation,
            out error)
            && TrySample(animation!, side, out reference, out error);
    }

    /// <summary>
    /// Samples the required single-key destination-local pose for one side's 15 canonical finger destinations
    /// directly from an already-loaded reference <see cref="Animation" /> instance (XR-002 TR52,
    /// TR25-generalised) — the instance-exact path the optical grab pose arbitration consumes at commit, with
    /// no resource re-load and no mutation of the supplied animation.
    /// </summary>
    /// <param name="animation">The immutable one-frame reference animation resource.</param>
    /// <param name="side">Hand side whose finger tracks are required.</param>
    /// <param name="reference">The sampled destination-local reference pose.</param>
    /// <param name="error">Actionable failure identifying the side, bone, resource, and violated rule.</param>
    public static bool TrySample(
        Animation animation,
        LimbSide side,
        out AuthoredHandPoseSideReference reference,
        out string error)
    {
        reference = null!;
        if (animation is null)
        {
            error = $"{side} hand: the authored hand-pose reference animation is null.";
            return false;
        }

        XRHandJoint[] destinationJoints = XRHandJoints.DestinationJoints;
        var poses = new Quaternion[destinationJoints.Length];
        for (int fingerIndex = 0; fingerIndex < destinationJoints.Length; fingerIndex++)
        {
            if (!TryReadRequiredKey(animation, side, destinationJoints[fingerIndex], out poses[fingerIndex], out error))
            {
                return false;
            }
        }

        reference = new AuthoredHandPoseSideReference(side, animation.ResourcePath, poses);
        error = string.Empty;
        return true;
    }

    private static bool TryReadRequiredKey(
        Animation animation,
        LimbSide side,
        XRHandJoint joint,
        out Quaternion value,
        out string error)
    {
        value = Quaternion.Identity;
        string boneName = AuthoredHandPoseSideReference.GetCanonicalBoneName(side, joint);
        string expectedPath = AuthoredThumbReferenceSampler.TrackPathPrefix + boneName;
        TrackRule rule = ResolveRotationTrackRule(animation, expectedPath, out int trackIndex);

        if (rule != TrackRule.None)
        {
            string reason = rule switch
            {
                TrackRule.Duplicate => "carries multiple enabled Rotation3D tracks matching it",
                TrackRule.Disabled => "has only a disabled Rotation3D track matching it",
                TrackRule.WrongType => "carries a non-Rotation3D track matching it",
                TrackRule.Missing or TrackRule.None => "has no track matching it",
                _ => "has no track matching it",
            };
            error = $"{side} hand: the authored hand-pose reference '{animation.ResourcePath}' {reason} for the " +
                $"required bone path '{expectedPath}'.";
            return false;
        }

        return TryReadValidatedKey(animation, trackIndex, boneName, out value, out error);
    }

    private static bool TryReadValidatedKey(
        Animation animation,
        int trackIndex,
        string boneName,
        out Quaternion value,
        out string error)
    {
        value = Quaternion.Identity;
        int keyCount = animation.TrackGetKeyCount(trackIndex);
        if (keyCount != 1)
        {
            error = $"{boneName}: the track in the authored hand-pose reference '{animation.ResourcePath}' holds " +
                $"{keyCount} keys; exactly one key is required.";
            return false;
        }

        double keyTime = animation.TrackGetKeyTime(trackIndex, 0);
        if (Math.Abs(keyTime) > KeyTimeEpsilon)
        {
            error = $"{boneName}: key 0 in the authored hand-pose reference '{animation.ResourcePath}' is at " +
                $"t={keyTime:R}s; t=0 is required.";
            return false;
        }

        Variant raw = animation.TrackGetKeyValue(trackIndex, 0);
        if (raw.VariantType != Variant.Type.Quaternion)
        {
            error = $"{boneName}: key 0 in the authored hand-pose reference '{animation.ResourcePath}' is a " +
                $"{raw.VariantType} rather than a Quaternion.";
            return false;
        }

        Quaternion quaternion = raw.AsQuaternion();
        if (!float.IsFinite(quaternion.X) || !float.IsFinite(quaternion.Y)
            || !float.IsFinite(quaternion.Z) || !float.IsFinite(quaternion.W)
            || quaternion.LengthSquared() <= 0.0f
            || Math.Abs(quaternion.LengthSquared() - 1.0f) > QuaternionUnitTolerance)
        {
            error = $"{boneName}: key 0 in the authored hand-pose reference '{animation.ResourcePath}' is an " +
                $"invalid quaternion ({quaternion.X:R}, {quaternion.Y:R}, {quaternion.Z:R}, {quaternion.W:R}); " +
                $"non-finite, near-zero, or failing |length² − 1| ≤ {QuaternionUnitTolerance} all fail closed.";
            return false;
        }

        value = quaternion.Normalized();
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Classifies the exact-path track resolution for one required bone: exactly one enabled
    /// <c>Rotation3D</c> match passes; duplicates, a sole disabled rotation match, or a sole non-rotation match
    /// report their specific rule; otherwise the track is missing.
    /// </summary>
    private static TrackRule ResolveRotationTrackRule(Animation animation, string expectedPath, out int trackIndex)
    {
        trackIndex = -1;
        int enabledRotationMatches = 0;
        bool sawDisabledRotationMatch = false;
        bool sawOtherTypeMatch = false;
        for (int index = 0; index < animation.GetTrackCount(); index++)
        {
            if (animation.TrackGetPath(index).ToString() != expectedPath)
            {
                continue;
            }

            if (animation.TrackGetType(index) != Animation.TrackType.Rotation3D)
            {
                sawOtherTypeMatch = true;
                continue;
            }

            if (!animation.TrackIsEnabled(index))
            {
                sawDisabledRotationMatch = true;
                continue;
            }

            enabledRotationMatches++;
            trackIndex = index;
        }

        return enabledRotationMatches switch
        {
            0 when sawDisabledRotationMatch => TrackRule.Disabled,
            0 when sawOtherTypeMatch => TrackRule.WrongType,
            0 => TrackRule.Missing,
            1 => TrackRule.None,
            _ => TrackRule.Duplicate,
        };
    }
}

/// <summary>
/// One side's immutable destination-local reference pose sampled from an authored one-frame hand-pose
/// animation (XR-002 TR52): the 15 raw single-key quaternions in canonical
/// <see cref="XRHandJoints.DestinationJoints" /> order.
/// </summary>
public sealed class AuthoredHandPoseSideReference
{
    private readonly Quaternion[] _destinationLocalPoses;

    /// <summary>
    /// Creates a reference pose from the 15 canonical destination-local quaternions. Defensively rejects
    /// non-finite or non-normalised values so a hand-built reference can never smuggle invalid data into
    /// gesture-profile derivation.
    /// </summary>
    public AuthoredHandPoseSideReference(LimbSide side, string resourcePath, Quaternion[] destinationLocalPoses)
    {
        ArgumentNullException.ThrowIfNull(destinationLocalPoses);
        if (destinationLocalPoses.Length != XRHandJoints.DestinationJoints.Length)
        {
            throw new ArgumentException(
                $"A hand-pose side reference requires exactly {XRHandJoints.DestinationJoints.Length} " +
                $"destination-local poses for {side}.");
        }

        foreach (Quaternion pose in destinationLocalPoses)
        {
            if (!float.IsFinite(pose.X) || !float.IsFinite(pose.Y)
                || !float.IsFinite(pose.Z) || !float.IsFinite(pose.W)
                || pose.LengthSquared() <= 0.0f)
            {
                throw new ArgumentException(
                    $"A hand-pose side reference for {side} carries a non-finite or near-zero quaternion.");
            }
        }

        Side = side;
        ResourcePath = resourcePath;
        _destinationLocalPoses = (Quaternion[])destinationLocalPoses.Clone();
    }

    /// <summary>Hand side the reference poses belong to.</summary>
    public LimbSide Side
    {
        get;
    }

    /// <summary>Project-relative path of the sampled reference resource, for diagnostics.</summary>
    public string ResourcePath
    {
        get;
    }

    /// <summary>The 15 destination-local reference quaternions in canonical destination order.</summary>
    public ReadOnlySpan<Quaternion> Poses => _destinationLocalPoses;

    /// <summary>Gets the canonical skeleton bone name of one destination (<c>&lt;Side&gt;&lt;Joint&gt;</c>).</summary>
    public static string GetCanonicalBoneName(LimbSide side, XRHandJoint joint)
        => (side == LimbSide.Left ? "Left" : "Right") + joint;
}
