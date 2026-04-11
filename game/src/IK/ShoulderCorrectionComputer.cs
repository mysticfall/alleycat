using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Computes deterministic shoulder correction using a look-at delta approach.
/// The full angular change between rest and current arm directions is captured
/// as a single rotation delta, then dampened by an adjustable weight.
/// </summary>
public static class ShoulderCorrectionComputer
{
    private const float DegenerateThreshold = 1e-6f;

    /// <summary>
    /// Builds a look-at basis from an arm direction and an up reference.
    /// The basis forward axis aligns with the arm direction; the up axis is
    /// the closest orthogonal direction to <paramref name="upReference"/>.
    /// </summary>
    /// <param name="armDirection">Normalised arm direction (shoulder → elbow) in body space.</param>
    /// <param name="upReference">Body-space up vector used to orient the basis.</param>
    /// <returns>
    /// An orthonormal look-at basis, or <see cref="Basis.Identity"/> when the inputs
    /// are degenerate or collinear.
    /// </returns>
    public static Basis BuildLookAtBasis(Vector3 armDirection, Vector3 upReference)
    {
        if (armDirection.LengthSquared() < DegenerateThreshold
            || upReference.LengthSquared() < DegenerateThreshold)
        {
            return Basis.Identity;
        }

        Vector3 forward = armDirection.Normalized();

        // Right = forward × up, then re-derive up to ensure orthogonality.
        Vector3 right = forward.Cross(upReference);

        if (right.LengthSquared() < DegenerateThreshold)
        {
            // Arm direction is collinear with up — fall back to arbitrary perpendicular.
            return Basis.Identity;
        }

        right = right.Normalized();
        Vector3 up = right.Cross(forward).Normalized();

        // Basis columns: X = right, Y = up, Z = -forward (Godot convention: -Z is forward).
        var basis = new Basis
        {
            Column0 = right,
            Column1 = up,
            Column2 = -forward,
        };

        return basis.Orthonormalized();
    }

    /// <summary>
    /// Computes a dampened shoulder correction quaternion from rest and current look-at bases.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The delta rotation between the two bases captures the full angular change of the arm
    /// direction since rest. This delta is then dampened by slerp towards identity.
    /// </para>
    /// <para>
    /// For lowered arms the weight should be smaller (more dampening); for raised arms the
    /// weight should approach 1.0 (less dampening). No boost is ever needed because the delta
    /// already contains the full angular change.
    /// </para>
    /// </remarks>
    /// <param name="restLookBasis">Look-at basis built from the rest arm direction.</param>
    /// <param name="currentLookBasis">Look-at basis built from the current arm direction.</param>
    /// <param name="weight">
    /// Dampening factor in [0, 1]. 0 = full suppression (identity), 1 = full delta applied.
    /// </param>
    /// <returns>Dampened correction quaternion in body space.</returns>
    public static Quaternion ComputeCorrection(
        Basis restLookBasis,
        Basis currentLookBasis,
        float weight)
    {
        Quaternion restQuat = restLookBasis.GetRotationQuaternion().Normalized();
        Quaternion currentQuat = currentLookBasis.GetRotationQuaternion().Normalized();

        Quaternion delta = currentQuat * restQuat.Inverse();
        delta = delta.Normalized();

        float clampedWeight = Mathf.Clamp(weight, 0f, 1f);

        return Quaternion.Identity.Slerp(delta, clampedWeight).Normalized();
    }

    /// <summary>
    /// Computes a pose-adaptive dampening weight from the current arm direction in body space.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the arm is lowered (negative Y component), the weight is reduced so that the
    /// shoulders stay mostly relaxed. As the arm rises, the weight increases towards
    /// <paramref name="baseWeight"/> so the full delta is increasingly applied.
    /// </para>
    /// <para>
    /// The adaptive curve uses two stages: a lower ramp from strongly-lowered to horizontal
    /// (suppression → base), and an upper ramp from horizontal to overhead that adds a small
    /// boost above the base weight. This ensures overhead correction always exceeds
    /// forward-level correction.
    /// </para>
    /// </remarks>
    /// <param name="currentArmDirectionBody">
    /// Current normalised arm direction (shoulder → elbow) in body space.
    /// </param>
    /// <param name="baseWeight">Base dampening weight (exported tuning value).</param>
    /// <returns>Effective weight in [0, 1].</returns>
    public static float ComputeAdaptiveWeight(
        Vector3 currentArmDirectionBody,
        float baseWeight)
    {
        if (currentArmDirectionBody.LengthSquared() < DegenerateThreshold)
        {
            return 0f;
        }

        float elevation = currentArmDirectionBody.Normalized().Y;
        float clampedBase = Mathf.Clamp(baseWeight, 0f, 1f);

        // Stage 1: Suppression ramp — arms below -0.5 are strongly suppressed,
        // ramping up to full base weight at elevation 0.0 (horizontal).
        const float minimumScale = 0.15f;
        float suppressionT = Smoothstep(-0.5f, 0.0f, elevation);
        float suppressionScale = Mathf.Lerp(minimumScale, 1f, suppressionT);

        // Stage 2: Overhead ramp — from horizontal (0.0) to vertical (0.85),
        // add up to 40% extra weight so overhead always exceeds forward-level.
        const float overheadBoostFraction = 0.4f;
        float overheadT = Smoothstep(0.0f, 0.85f, elevation);
        float overheadBoost = overheadT * overheadBoostFraction * clampedBase;

        return Mathf.Clamp((suppressionScale * clampedBase) + overheadBoost, 0f, 1f);
    }

    private static float Smoothstep(float edge0, float edge1, float value)
    {
        float t = Mathf.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - (2f * t));
    }
}
