using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Computes deterministic shoulder correction using an anatomical axis decomposition.
/// </summary>
/// <remarks>
/// <para>
/// The correction is derived as the composition of two independent rotations around
/// explicit anatomical axes in body space:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <b>Elevation</b> — a rotation about the body forward–back axis that lifts the
///       shoulder as the arm rises along body <c>+Y</c>. An additional overhead boost
///       term smoothly ramps in above shoulder level so arms-up poses visibly lift more
///       than T-pose.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Protraction</b> — a rotation about the body up axis that moves the shoulder
///       forward as the arm advances along body <c>+Z</c>.
///     </description>
///   </item>
/// </list>
/// <para>
/// This avoids the ambiguity of a shortest-arc rotation axis, which for arms-forward
/// poses degenerates into a near-outward twist of the shoulder rather than a genuine
/// protraction, producing a visible "drop". The composed rotation is finally damped by
/// slerping towards identity via the overall correction weight.
/// </para>
/// </remarks>
public static class ShoulderCorrectionComputer
{
    private const float DegenerateThreshold = 1e-6f;

    /// <summary>
    /// Returns a normalised arm direction representing anatomical neutral in body space:
    /// the arm hanging down with a small outward lateral bias.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Because the character's rest pose is typically a T-pose, the rest arm direction
    /// cannot be used as the "zero correction" reference (it already represents fully
    /// elevated arms). Instead, the correction is measured relative to a more natural
    /// neutral: an arm hanging down with a slight outward component along the lateral
    /// axis (−X for the left arm, +X for the right arm in body space).
    /// </para>
    /// <para>
    /// The returned vector satisfies <c>x² + y² = 1</c>, with <c>y ≤ 0</c> and the sign
    /// of <c>x</c> chosen according to <paramref name="side"/>.
    /// </para>
    /// </remarks>
    /// <param name="side">Which arm the neutral is being computed for.</param>
    /// <param name="lateralBias">
    /// Lateral component magnitude in [0, 0.95]. 0 = straight down; 0.95 approaches horizontal.
    /// Values outside the range are clamped.
    /// </param>
    /// <returns>Normalised anatomical-neutral arm direction in body space.</returns>
    public static Vector3 ComputeAnatomicalNeutralDirection(ArmSide side, float lateralBias)
    {
        float clampedBias = Mathf.Clamp(lateralBias, 0f, 0.95f);
        float downComponent = Mathf.Sqrt(1f - (clampedBias * clampedBias));
        float lateralSign = side == ArmSide.Left ? -1f : 1f;

        return new Vector3(lateralSign * clampedBias, -downComponent, 0f);
    }

    /// <summary>
    /// Computes a damped shoulder correction from anatomically-decomposed rotations about body-space axes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The correction is the composition of two independent rotations:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       <b>Elevation</b> — rotation around the body forward–back axis that lifts the shoulder as the arm rises.
    ///       Magnitude scales from 0 at the anatomical neutral Y (arm hanging down) to <paramref name="maxElevationAngle"/>
    ///       at arm-Y = 1 (arm pointing straight up along body +Y). An additional overhead boost controlled by
    ///       <paramref name="maxOverheadElevationBoost"/> ramps smoothly from 0 at horizontal-or-below to its full value
    ///       at arm-Y = 1, allowing overhead poses to visibly lift the shoulder more than a purely linear base term
    ///       permits. The combined elevation is scaled by <c>1 − forwardElevationDamping · max(0, arm-Z)</c> so a
    ///       forward-reaching arm lifts the shoulder less than a purely lateral or overhead arm at the same arm-Y.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <b>Protraction</b> — rotation around the body up axis that moves the shoulder forward as the arm
    ///       advances. Magnitude scales from 0 at arm-Z = 0 to <paramref name="maxProtractionAngle"/> at arm-Z = 1
    ///       (arm pointing straight forward along body +Z). Protraction is not affected by the forward-elevation damping.
    ///     </description>
    ///   </item>
    /// </list>
    /// <para>The combined rotation is finally slerped towards identity by <c>1 − weight</c>.</para>
    /// </remarks>
    /// <param name="currentArmDirectionBody">Current shoulder-to-elbow direction in body space (X right, Y up, Z forward).</param>
    /// <param name="side">The arm being driven (controls rotation signs).</param>
    /// <param name="anatomicalNeutralY">
    /// The Y component of the anatomical-neutral arm direction (equal to
    /// <c>ComputeAnatomicalNeutralDirection(side, lateralBias).Y</c>).
    /// Used as the baseline for elevation so that the arm-at-neutral produces zero elevation.
    /// </param>
    /// <param name="maxElevationAngle">Maximum shoulder elevation (radians) when the arm points straight up.</param>
    /// <param name="maxOverheadElevationBoost">
    /// Additional shoulder elevation (radians) added on top of the base elevation term when the arm points straight up.
    /// The boost ramps smoothly from 0 at horizontal-or-below (arm-Y ≤ 0) to the full value at arm-Y = 1, and is subject
    /// to the same forward-elevation damping and overall weight as the base elevation term.
    /// </param>
    /// <param name="maxProtractionAngle">Maximum shoulder protraction (radians) when the arm points straight forward.</param>
    /// <param name="forwardElevationDamping">
    /// Fraction of elevation suppressed when the arm points straight forward (arm-Z = 1). Clamped to [0, 1].
    /// 0 leaves the elevation unaffected by the forward arm component; 1 fully suppresses elevation at arms-forward.
    /// Damping scales linearly with the positive forward component of the arm, and does not affect protraction.
    /// </param>
    /// <param name="weight">Overall dampening weight in [0, 1]. 0 = identity, 1 = full composed rotation applied.</param>
    /// <returns>Correction quaternion in body space.</returns>
    public static Quaternion ComputeCorrection(
        Vector3 currentArmDirectionBody,
        ArmSide side,
        float anatomicalNeutralY,
        float maxElevationAngle,
        float maxOverheadElevationBoost,
        float maxProtractionAngle,
        float forwardElevationDamping,
        float weight)
    {
        if (currentArmDirectionBody.LengthSquared() < DegenerateThreshold)
        {
            return Quaternion.Identity;
        }

        Vector3 armDir = currentArmDirectionBody.Normalized();
        float clampedWeight = Mathf.Clamp(weight, 0f, 1f);

        float span = Mathf.Max(1f - anatomicalNeutralY, 1e-4f);
        float elevationT = Mathf.Clamp((armDir.Y - anatomicalNeutralY) / span, 0f, 1f);
        float protractionT = Mathf.Clamp(armDir.Z, 0f, 1f);

        float forwardness = Mathf.Clamp(armDir.Z, 0f, 1f);
        float forwardDampingFactor = 1f - (Mathf.Clamp(forwardElevationDamping, 0f, 1f) * forwardness);

        // Overhead boost ramps from 0 at horizontal-or-below (arm-Y ≤ 0) to 1 at arm-Y = 1 (straight up).
        float overheadT = Smoothstep(0f, 1f, armDir.Y);

        Vector3 elevationAxis = side == ArmSide.Left
            ? new Vector3(0f, 0f, -1f)
            : new Vector3(0f, 0f, 1f);

        Vector3 protractionAxis = side == ArmSide.Left
            ? new Vector3(0f, 1f, 0f)
            : new Vector3(0f, -1f, 0f);

        float elevationAngle =
            ((elevationT * maxElevationAngle) + (overheadT * maxOverheadElevationBoost))
            * forwardDampingFactor
            * clampedWeight;
        float protractionAngle = protractionT * maxProtractionAngle * clampedWeight;

        Quaternion elevationRot = new(elevationAxis, elevationAngle);
        Quaternion protractionRot = new(protractionAxis, protractionAngle);

        return (protractionRot * elevationRot).Normalized();
    }

    private static float Smoothstep(float edge0, float edge1, float value)
    {
        float t = Mathf.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - (2f * t));
    }
}
