using AlleyCat.Rigging;
using AlleyCat.XR.HandTracking;
using Godot;

namespace AlleyCat.Control.Hands;

/// <summary>
/// The animation-derived power-grip definition of one side (XR-002 TR48): the per-destination directional
/// progress features — each destination's reference articulation axis and angle measured from the effective
/// neutrals <c>N_j</c> the shared projection uses — plus the derived per-destination and per-chain weights of
/// the weighted aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Derivation.</strong> For each of the side's 15 destinations, the reference offset is
/// <c>O_j = normalise(N_j⁻¹ × hemisphere_align(R_j, N_j))</c> with reference angle
/// <c>alpha_j = 2·atan2(|O_j.xyz|, O_j.w)</c> and reference axis <c>a_j = normalise(O_j.xyz)</c>. Destinations
/// whose reference articulation is below the settings' minimum angle carry no directional signal and are
/// excluded from the features. Intra-chain weights default to each destination's share of its chain's total
/// reference articulation; chain weights default to each chain's share of the total featured articulation, so
/// fingers that articulate more in the reference count more in the aggregate.
/// </para>
/// <para>
/// <strong>Progress semantics.</strong> Live progress for a destination is the signed twist of
/// <c>normalise(N_j⁻¹ × hemisphere_align(D_j, N_j))</c> about <c>a_j</c> divided by <c>alpha_j</c>: 0 at the
/// calibrated neutral, 1 at the reference articulation, greater than 1 for over-clench, and signed negative
/// when opening away from the reference direction. Derivation fails closed when the reference supplies no
/// featured articulation at all.
/// </para>
/// <para>
/// All arrays are immutable after derivation; the evaluation path reads them without allocation.
/// </para>
/// </remarks>
public sealed class PowerGripProfile : IGripRecognitionProfile
{
    private const float AxisEpsilon = 1e-6f;

    private readonly Quaternion[] _effectiveNeutrals;
    private readonly Vector3[] _referenceAxes;
    private readonly float[] _referenceAnglesRadians;
    private readonly float[] _destinationWeights;
    private readonly float[] _chainWeights;

    /// <summary>Creates a profile from validated derivation outputs.</summary>
    public PowerGripProfile(
        LimbSide side,
        PowerGripRecognitionSettings settings,
        Quaternion[] effectiveNeutrals,
        Vector3[] referenceAxes,
        float[] referenceAnglesRadians,
        float[] destinationWeights,
        float[] chainWeights)
    {
        Side = side;
        Settings = settings;
        _effectiveNeutrals = effectiveNeutrals;
        _referenceAxes = referenceAxes;
        _referenceAnglesRadians = referenceAnglesRadians;
        _destinationWeights = destinationWeights;
        _chainWeights = chainWeights;

        float featuredWeight = 0.0f;
        foreach (float chainWeight in chainWeights)
        {
            featuredWeight += chainWeight;
        }

        FeaturedChainWeightTotal = featuredWeight;
    }

    /// <summary>Hand side the profile articulates.</summary>
    public LimbSide Side
    {
        get;
    }

    /// <summary>The strategy identifier this profile derives through.</summary>
    public string StrategyName => GripRecognitionStrategies.PowerGrip;

    /// <summary>The settings snapshot the profile was derived with and must be evaluated with.</summary>
    public PowerGripRecognitionSettings Settings
    {
        get;
    }

    /// <summary>The 15 effective neutrals <c>N_j</c> in canonical destination order.</summary>
    public ReadOnlySpan<Quaternion> EffectiveNeutrals => _effectiveNeutrals;

    /// <summary>The 15 per-destination reference articulation axes in canonical destination order.</summary>
    public ReadOnlySpan<Vector3> ReferenceAxes => _referenceAxes;

    /// <summary>The 15 per-destination reference articulation angles in radians in canonical destination order.</summary>
    public ReadOnlySpan<float> ReferenceAnglesRadians => _referenceAnglesRadians;

    /// <summary>
    /// The 15 intra-chain destination weights — each destination's share of its chain's total reference
    /// articulation; 0 for unfeatured destinations.
    /// </summary>
    public ReadOnlySpan<float> DestinationWeights => _destinationWeights;

    /// <summary>The 5 chain weights (thumb, index, middle, ring, little), normalised over featured chains.</summary>
    public ReadOnlySpan<float> ChainWeights => _chainWeights;

    /// <summary>The total weight of chains carrying at least one featured destination.</summary>
    public float FeaturedChainWeightTotal
    {
        get;
    }

    /// <summary>
    /// Derives the weighted-aggregate power-grip definition of one side from its sampled reference pose and the
    /// effective neutrals the shared projection uses (XR-002 TR48; INTR-001 TR14).
    /// </summary>
    /// <param name="reference">The side's sampled destination-local reference pose.</param>
    /// <param name="sideEffectiveNeutrals">
    /// The side's 15 effective neutrals in canonical destination order — the same <c>N_j</c> the projection
    /// seam consumes.
    /// </param>
    /// <param name="settings">Recognition settings the derivation and evaluation honour.</param>
    /// <param name="profile">The derived profile.</param>
    /// <param name="error">Actionable failure identifying the side and violated rule.</param>
    public static bool TryDerive(
        AuthoredHandPoseSideReference reference,
        ReadOnlySpan<Quaternion> sideEffectiveNeutrals,
        PowerGripRecognitionSettings settings,
        out PowerGripProfile profile,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(settings);

        int destinationCount = XRHandJoints.DestinationJoints.Length;
        profile = null!;
        if (sideEffectiveNeutrals.Length < destinationCount || reference.Poses.Length < destinationCount)
        {
            error = $"{reference.Side} hand: the power-grip derivation requires {destinationCount} reference " +
                $"poses and effective neutrals; got {reference.Poses.Length} poses and " +
                $"{sideEffectiveNeutrals.Length} neutrals.";
            return false;
        }

        var neutrals = new Quaternion[destinationCount];
        var axes = new Vector3[destinationCount];
        float[] angles = new float[destinationCount];
        bool[] featured = new bool[destinationCount];
        for (int destinationIndex = 0; destinationIndex < destinationCount; destinationIndex++)
        {
            Quaternion neutral = Normalise(sideEffectiveNeutrals[destinationIndex]);
            Quaternion referencePose = Normalise(reference.Poses[destinationIndex]);
            if (!IsFinite(neutral) || !IsFinite(referencePose))
            {
                error = $"{reference.Side} hand: destination {XRHandJoints.DestinationJoints[destinationIndex]} " +
                    "has a non-finite effective neutral or reference pose; derivation fails closed.";
                return false;
            }

            // O_j = normalise(N_j⁻¹ × hemisphere_align(R_j, N_j)): the reference articulation measured from the
            // calibrated neutral, in destination-local terms (XR-002 TR48).
            Quaternion aligned = referencePose.Dot(neutral) < 0.0f
                ? new Quaternion(-referencePose.X, -referencePose.Y, -referencePose.Z, -referencePose.W)
                : referencePose;
            Quaternion offset = Normalise(neutral.Inverse() * aligned);
            Vector3 axisPart = new(offset.X, offset.Y, offset.Z);
            float axisLength = axisPart.Length();
            float angle = 2.0f * Mathf.Atan2(axisLength, offset.W);
            if (!IsFinite(offset) || axisLength <= AxisEpsilon || angle < settings.MinimumReferenceAngleRadians)
            {
                // Below the minimum articulation the destination carries no directional signal; it stays
                // excluded from the features rather than amplifying noise.
                continue;
            }

            neutrals[destinationIndex] = neutral;
            axes[destinationIndex] = axisPart / axisLength;
            angles[destinationIndex] = angle;
            featured[destinationIndex] = true;
        }

        // Intra-chain weights: each destination's share of its chain's total reference articulation, and each
        // chain's share of the total featured articulation.
        float[] destinationWeights = new float[destinationCount];
        float[] chainWeights = new float[PowerGripRecognition.ChainCount];
        for (int chainIndex = 0; chainIndex < PowerGripRecognition.ChainCount; chainIndex++)
        {
            (int start, int length) = PowerGripRecognition.GetChainDestinationRange(chainIndex);
            float chainArticulation = 0.0f;
            for (int offset = 0; offset < length; offset++)
            {
                int destinationIndex = start + offset;
                if (featured[destinationIndex])
                {
                    chainArticulation += angles[destinationIndex];
                }
            }

            if (chainArticulation <= 0.0f)
            {
                continue;
            }

            for (int offset = 0; offset < length; offset++)
            {
                int destinationIndex = start + offset;
                destinationWeights[destinationIndex] = featured[destinationIndex]
                    ? angles[destinationIndex] / chainArticulation
                    : 0.0f;
            }

            chainWeights[chainIndex] = chainArticulation;
        }

        float featuredTotal = 0.0f;
        foreach (float chainWeight in chainWeights)
        {
            featuredTotal += chainWeight;
        }

        if (featuredTotal <= 0.0f)
        {
            error = $"{reference.Side} hand: the reference '{reference.ResourcePath}' supplies no featured " +
                "articulation — every destination sits at or below the minimum reference angle — so no " +
                "power-grip definition can derive; content validation fails closed.";
            return false;
        }

        for (int chainIndex = 0; chainIndex < chainWeights.Length; chainIndex++)
        {
            chainWeights[chainIndex] /= featuredTotal;
        }

        profile = new PowerGripProfile(
            reference.Side,
            settings,
            neutrals,
            axes,
            angles,
            destinationWeights,
            chainWeights);
        error = string.Empty;
        return true;
    }

    private static Quaternion Normalise(Quaternion value)
        => value.LengthSquared() <= 0.0000001f ? Quaternion.Identity : value.Normalized();

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
