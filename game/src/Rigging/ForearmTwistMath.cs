using Godot;

namespace AlleyCat.Rigging;

/// <summary>
/// Pure rest-frame math for distributing a hand's axial forearm motion onto the single twist
/// helper bone.
/// </summary>
/// <remarks>
/// The per-side chain is <c>LowerArm → ForearmTwist → Hand</c>. The writer extracts only the
/// signed principal axial twist of the hand delta about the lower-arm rest longitudinal axis;
/// the swing component is never applied anywhere. The hand's authoritative global pose is
/// re-asserted in the same pass as the helper write.
/// </remarks>
public static class ForearmTwistMath
{
    private const float MinimumLengthSquared = 0.0000001f;
    private const float CanonicalScalarTolerance = 0.00001f;
    private const float ConformalTolerance = 1.0e-4f;

    /// <summary>
    /// Calculates the complete skeleton-local twist-helper pose and the compensating hand pose
    /// for a valid same-pass side without writing the skeleton.
    /// </summary>
    /// <remarks>
    /// Implements the RIG-002 axial transport: with global rests <c>R_L</c> (lower arm),
    /// <c>R_X</c> (twist helper), <c>R_W</c> (hand), current lower-arm global <c>P_L</c>, and
    /// the earlier authoritative achieved hand global <c>C</c>, the hand delta is
    /// <c>D = rot(P_L⁻¹ × C × R_W⁻¹ × R_L)</c> in the lower-arm rest frame. The signed principal
    /// axial twist <c>T</c> about the rest longitudinal axis is weighted by the runtime weight
    /// <paramref name="alpha" /> (skin ownership is not a runtime transform factor) and
    /// transported by <c>F = P_L × Tα × R_L⁻¹</c>. The complete global target is
    /// <c>G_X = F × R_X</c>, written as complete local transforms <c>L_X = P_L⁻¹ × G_X</c> with
    /// the authoritative hand re-asserted as <c>L_W = G_X⁻¹ × C</c>, so the helper write never
    /// changes the hand's achieved global pose.
    /// </remarks>
    public static bool TryComputeAffineTwistHelperLocalPoses(
        Transform3D lowerArmGlobalRest,
        Transform3D lowerArmGlobalPose,
        Transform3D twistHelperGlobalRest,
        Transform3D handGlobalRest,
        Transform3D achievedHandGlobalPose,
        float alpha,
        out Transform3D twistLocalPose,
        out Transform3D handLocalPose)
    {
        twistLocalPose = Transform3D.Identity;
        handLocalPose = Transform3D.Identity;
        if (!TryGetProperOrientation(lowerArmGlobalRest, out _)
            || !TryGetProperOrientation(lowerArmGlobalPose, out _)
            || !TryGetProperOrientation(twistHelperGlobalRest, out _)
            || !TryGetProperOrientation(handGlobalRest, out _)
            || !TryGetProperOrientation(achievedHandGlobalPose, out _)
            || !IsFinite(alpha)
            || alpha < 0.0f || alpha > 1.0f)
        {
            return false;
        }

        // Rest longitudinal axis in the lower-arm rest frame: a = normalise(B_RL⁻¹ (w - origin(R_L))).
        Vector3 wrist = handGlobalRest.Origin;
        Vector3 restAxis = lowerArmGlobalRest.Basis.Inverse() * (wrist - lowerArmGlobalRest.Origin);
        if (!TryNormalise(restAxis, out restAxis))
        {
            return false;
        }

        Transform3D handDelta = lowerArmGlobalPose.AffineInverse() * achievedHandGlobalPose
            * handGlobalRest.AffineInverse() * lowerArmGlobalRest;
        if (!TryGetProperOrientation(handDelta, out Quaternion deltaRotation)
            || !TryExtractSignedPrincipalTwist(deltaRotation, restAxis, out float principalTwistAngle))
        {
            return false;
        }

        // Tα = slerp(I, T, α); the principal twist lives on the canonical double-cover branch,
        // so the weighted twist is the same rotation about the same axis with the scaled angle.
        Quaternion weightedTwist = new(restAxis, principalTwistAngle * alpha);
        Transform3D transportedTwist = lowerArmGlobalPose
            * new Transform3D(new Basis(weightedTwist), Vector3.Zero)
            * lowerArmGlobalRest.AffineInverse();
        Transform3D helperGlobalTarget = transportedTwist * twistHelperGlobalRest;
        Transform3D nextTwist = lowerArmGlobalPose.AffineInverse() * helperGlobalTarget;
        Transform3D nextHand = helperGlobalTarget.AffineInverse() * achievedHandGlobalPose;
        if (!IsFinite(transportedTwist)
            || !IsFinite(helperGlobalTarget)
            || !TryGetProperOrientation(nextTwist, out _)
            || !TryGetProperOrientation(nextHand, out _))
        {
            return false;
        }

        twistLocalPose = nextTwist;
        handLocalPose = nextHand;
        return true;
    }

    private static bool TryGetProperOrientation(Transform3D transform, out Quaternion orientation)
    {
        orientation = Quaternion.Identity;
        if (!IsFinite(transform))
        {
            return false;
        }

        Basis basis = transform.Basis;
        float scale = basis.X.Length();
        return IsFinite(scale) && scale >= 1.0e-5f
            && MathF.Abs(basis.Y.Length() - scale) <= scale * ConformalTolerance
            && MathF.Abs(basis.Z.Length() - scale) <= scale * ConformalTolerance
            && MathF.Abs(basis.X.Dot(basis.Y)) <= scale * scale * ConformalTolerance
            && MathF.Abs(basis.Y.Dot(basis.Z)) <= scale * scale * ConformalTolerance
            && MathF.Abs(basis.Z.Dot(basis.X)) <= scale * scale * ConformalTolerance
            && MathF.Abs(basis.Determinant() - (scale * scale * scale)) <= scale * scale * scale * ConformalTolerance
            && TryNormalise(new Basis(basis.X / scale, basis.Y / scale, basis.Z / scale).GetRotationQuaternion(), out orientation);
    }

    /// <summary>
    /// Extracts the deterministic signed principal twist around <paramref name="axis" />.
    /// Quaternion double-cover inputs always produce the same result, including an exact half turn.
    /// </summary>
    public static bool TryExtractSignedPrincipalTwist(Quaternion rotation, Vector3 axis, out float angle)
    {
        angle = 0.0f;
        if (!TryNormalise(rotation, out Quaternion normalisedRotation) || !TryNormalise(axis, out Vector3 normalisedAxis))
        {
            return false;
        }

        normalisedRotation = CanonicaliseDoubleCover(normalisedRotation);

        Vector3 vector = new(normalisedRotation.X, normalisedRotation.Y, normalisedRotation.Z);
        float signedSinHalfAngle = vector.Dot(normalisedAxis);
        Quaternion twist = new(
            normalisedAxis.X * signedSinHalfAngle,
            normalisedAxis.Y * signedSinHalfAngle,
            normalisedAxis.Z * signedSinHalfAngle,
            normalisedRotation.W);
        if (!TryNormalise(twist, out twist))
        {
            return false;
        }

        twist = CanonicaliseDoubleCover(twist);

        float twistVectorLength = new Vector3(twist.X, twist.Y, twist.Z).Length();
        angle = 2.0f * MathF.Atan2(twistVectorLength, twist.W);
        if (signedSinHalfAngle < 0.0f)
        {
            angle = -angle;
        }

        return IsFinite(angle);
    }

    private static bool TryNormalise(Quaternion rotation, out Quaternion normalised)
    {
        normalised = Quaternion.Identity;
        if (!IsFinite(rotation) || rotation.LengthSquared() <= MinimumLengthSquared)
        {
            return false;
        }

        normalised = rotation.Normalized();
        return IsFinite(normalised);
    }

    private static bool TryNormalise(Vector3 vector, out Vector3 normalised)
    {
        normalised = Vector3.Zero;
        if (!IsFinite(vector) || vector.LengthSquared() <= MinimumLengthSquared)
        {
            return false;
        }

        normalised = vector.Normalized();
        return IsFinite(normalised);
    }

    private static Quaternion Negate(Quaternion rotation)
        => new(-rotation.X, -rotation.Y, -rotation.Z, -rotation.W);

    private static Quaternion CanonicaliseDoubleCover(Quaternion rotation)
    {
        if (rotation.W < -CanonicalScalarTolerance)
        {
            return Negate(rotation);
        }

        if (MathF.Abs(rotation.W) > CanonicalScalarTolerance)
        {
            return rotation;
        }

        float x = MathF.Abs(rotation.X);
        float y = MathF.Abs(rotation.Y);
        float z = MathF.Abs(rotation.Z);
        float selected = x >= y && x >= z ? rotation.X : y >= z ? rotation.Y : rotation.Z;
        return selected < 0.0f ? Negate(rotation) : rotation;
    }

    private static bool IsFinite(Quaternion rotation)
        => IsFinite(rotation.X) && IsFinite(rotation.Y) && IsFinite(rotation.Z) && IsFinite(rotation.W);

    private static bool IsFinite(Vector3 vector)
        => IsFinite(vector.X) && IsFinite(vector.Y) && IsFinite(vector.Z);

    private static bool IsFinite(Transform3D transform)
        => IsFinite(transform.Origin) && IsFinite(transform.Basis.X) && IsFinite(transform.Basis.Y) && IsFinite(transform.Basis.Z);

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
