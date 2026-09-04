using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Pure retargeting math for optical finger rotations: anatomical parent-relative source derivation,
/// profile-neutral delta derivation, and quaternion hemisphere stabilisation.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the source relation is a plain parent-relative quotient.</strong> Every destination bone's source
/// relation is derived as <c>parentWorld⁻¹ · childWorld</c> — the tracked orientation of the mapped joint
/// relative to its actual tracked anatomical parent joint, in the shared provider world frame. This mirrors
/// Godot's <c>XRHandModifier3D</c> joint handling: because the rig composes a bone's global pose as
/// <c>parent global · local</c>, the quotient placed in the bone-local slot reproduces the physical joint's
/// orientation relative to its anatomical parent whatever frames the wrist, hand bone, or skeleton root are
/// in. The derived value depends on nothing but the current tracked pair — no session-entry snapshot, no
/// accumulated deltas, no frame maps.
/// </para>
/// <para>
/// <strong>Removed general full-quaternion fit.</strong> The previous normative non-thumb formula
/// <c>D = N × (K⁻¹ × (S0⁻¹ × S) × K)</c> has been removed from the production path per XR-002 TR14/TR17-TR23:
/// non-thumb destinations are mapped through <see cref="FingerAnatomicalMath" />'s constrained anatomical model
/// instead — signed hinge flexion at the intermediate and distal joints and a roll-free directional swing at the
/// proximals — with <c>K</c> reduced to identity-only deprecated compatibility metadata (XR-002 TR44). There is
/// deliberately no runtime basis-correspondence use.
/// </para>
/// <para>
/// <strong>Hardware evidence (WiVRn OPEN/FIST capture).</strong> The controlled capture showed the bilateral
/// tracked hand data is anatomically stable: joint relations parent→child hold through pose changes, while
/// the little-finger metacarpal carries a roughly 29-degree static wrist-relative offset with essentially
/// zero motion across OPEN→FIST. Deriving non-thumb proximal rotations from their metacarpal parent —
/// rather than collapsing the metacarpal into a wrist-relative derivation — keeps that static offset out of
/// the written rotation (it is a spread calibration, not a motion) while preserving every inter-phalangeal
/// motion exactly.
/// </para>
/// <para>
/// All helpers accept and return pure unit rotations; basis inputs are orthonormalised so degenerate or
/// non-orthonormal provider transforms cannot inject scale or shear, and a uniform provider world scale
/// cancels exactly in the quotient.
/// </para>
/// </remarks>
public static class FingerRetargetingMath
{
    /// <summary>
    /// Derives the orientation of a tracked joint relative to its anatomical parent joint from their
    /// world-space bases: <c>parentWorld⁻¹ · childWorld</c> (XR-002 TR20).
    /// </summary>
    /// <remarks>
    /// Both operands are orthonormalised first, so a uniform provider world scale — shared or not — cannot
    /// reach the quotient, and non-orthonormal inputs cannot inject scale or shear. The result is the
    /// bone-local rotation written straight through <see cref="Skeleton3D.SetBonePoseRotation" />: the
    /// quotient is invariant to any common world rotation of the pair, so wrist motion, skeleton world
    /// rotation, and optical-session entry orientation all cancel exactly.
    /// </remarks>
    /// <param name="parentJointWorldBasis">Tracked parent joint's world basis.</param>
    /// <param name="childJointWorldBasis">Tracked joint's world basis in the same frame.</param>
    /// <returns>The joint orientation expressed in its parent joint's frame, as a unit rotation.</returns>
    public static Quaternion DeriveParentRelativeRotation(Basis parentJointWorldBasis, Basis childJointWorldBasis)
    {
        Basis parent = parentJointWorldBasis.Orthonormalized();
        Basis child = childJointWorldBasis.Orthonormalized();

        return (parent.Inverse() * child).GetRotationQuaternion();
    }

    /// <summary>
    /// Derives the neutral child-frame delta <c>S0⁻¹ × S</c> from the tracked parent-relative source relation,
    /// normalised and hemisphere-aligned to identity so the anatomical mapping's half-angles stay unambiguous
    /// (XR-002 TR20).
    /// </summary>
    /// <remarks>
    /// The delta is the only quantity the constrained anatomical mapping consumes: PIP/DIP destinations extract a
    /// signed twist about the shared source hinge from it (XR-002 TR21) and proximal destinations transfer its
    /// tracked longitudinal direction (XR-002 TR22). A delta of identity must reproduce the effective neutral
    /// exactly (XR-002 TR23).
    /// </remarks>
    public static Quaternion DeriveNeutralDelta(Quaternion sourceRelation, Quaternion sourceNeutral)
    {
        Quaternion delta = Normalise(Normalise(sourceNeutral).Inverse() * Normalise(sourceRelation));
        return delta.W < 0.0f
            ? new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W)
            : delta;
    }

    /// <summary>
    /// Aligns a candidate rotation's quaternion hemisphere with a reference rotation (XR-002 TR14).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Quaternion double cover: <c>q</c> and <c>-q</c> encode the same rotation, so a provider that flips a
    /// joint basis's sign between frames yields a visually identical but numerically disjoint write. The
    /// stabilised candidate keeps the hemisphere of the previously written rotation — the modifier passes
    /// its cached last-written rotation as the reference — so consecutive writes stay numerically continuous
    /// for diagnostics and any component-wise comparison. The encoded rotation is never changed.
    /// </para>
    /// <para>
    /// Non-normalised inputs are normalised on the way through; a degenerate (near-zero) input falls back
    /// to the identity rather than producing a non-unit result.
    /// </para>
    /// </remarks>
    /// <param name="candidate">Rotation whose hemisphere is chosen.</param>
    /// <param name="reference">Rotation supplying the preferred hemisphere.</param>
    /// <returns>
    /// <paramref name="candidate" /> normalised, negated when its dot product with
    /// <paramref name="reference" /> is negative.
    /// </returns>
    public static Quaternion StabiliseRotationHemisphere(Quaternion candidate, Quaternion reference)
    {
        Quaternion normalisedCandidate = Normalise(candidate);
        Quaternion normalisedReference = Normalise(reference);

        return normalisedCandidate.Dot(normalisedReference) < 0.0f
            ? new Quaternion(
                -normalisedCandidate.X,
                -normalisedCandidate.Y,
                -normalisedCandidate.Z,
                -normalisedCandidate.W)
            : normalisedCandidate;
    }

    private static Quaternion Normalise(Quaternion rotation)
        => rotation.LengthSquared() <= 0.0000001f ? Quaternion.Identity : rotation.Normalized();
}
