using AlleyCat.XR.HandTracking;
using Godot;

namespace AlleyCat.Control.Hands;

/// <summary>
/// Pure, deterministic evaluation of the animation-derived power-grip aggregate (XR-002 TR48; CTRL-002
/// TR12-TR13): given the derived profile and the live destination-local projected pose of one side, produces
/// per-destination directional progress, per-chain progress, the weighted aggregate score, and the validity
/// verdict.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Per-destination progress.</strong> Each live rotation <c>D_j</c> from the shared calibrated
/// anatomical projection is measured as <c>L_j = normalise(N_j⁻¹ × hemisphere_align(D_j, N_j))</c> —
/// hemisphere-aligned so quaternion double-cover sign flips cannot change any comparison — and its signed
/// twist about the reference axis <c>a_j</c> divided by the reference angle <c>alpha_j</c> yields progress:
/// 0 at the calibrated neutral, 1 at the reference articulation, greater than 1 for over-clench, and signed
/// negative when opening away from the reference direction. Off-reference motion never counts as closing, so
/// directional opening is distinguished from mere pose difference.
/// </para>
/// <para>
/// <strong>Aggregate.</strong> Chain progress is the weighted mean of its valid featured destinations with
/// renormalised intra-chain weights; the score is the weighted mean of the included chains with renormalised
/// chain weights. The score is monotonic in the closing direction, and over-clench (progress above 1) can
/// never make the hand less closed than at the reference because every contribution keeps its sign. One
/// deviating finger cannot alone drop the score below the held release threshold unless its chain weight
/// dominates. Invalid destinations are excluded with renormalised weights; when the included featured weight
/// falls below the settings' fraction, the evaluation reports insufficient validity and drives no recognition
/// (fail closed — no partial guess).
/// </para>
/// <para>
/// The evaluation reads only its inputs and writes only the caller's spans; it allocates nothing.
/// </para>
/// </remarks>
public static class PowerGripRecognition
{
    /// <summary>Finger chains of one hand: thumb, index, middle, ring, little.</summary>
    public const int ChainCount = 5;

    private const float TwistEpsilon = 1e-5f;

    /// <summary>
    /// Evaluates the power-grip aggregate of one side. Callers pass scratch spans of 15 and 5 floats; on
    /// success they receive per-destination progress (canonical order), per-chain progress (thumb first), the
    /// aggregate score, and whether enough featured weight is live-valid to drive recognition.
    /// </summary>
    /// <param name="profile">The derived side profile.</param>
    /// <param name="liveDestinationPoses">
    /// The side's 15 projected poses in canonical destination order — from
    /// <see cref="OpticalFingerProjectionBinding.TryProject" />.
    /// </param>
    /// <param name="destinationProgress">Scratch span of 15 per-destination progress values; overwritten.</param>
    /// <param name="chainProgress">Scratch span of 5 per-chain progress values; overwritten.</param>
    /// <param name="aggregateScore">The weighted aggregate score.</param>
    /// <param name="sufficientValidity">
    /// Whether enough featured chain weight is live-valid to drive recognition; when false, the score must not
    /// be consumed and no edge may be emitted.
    /// </param>
    /// <returns><see langword="false" /> on invalid arguments (fail closed, outputs zeroed).</returns>
    public static bool TryEvaluate(
        in PowerGripProfile profile,
        ReadOnlySpan<OpticalFingerProjectedPose> liveDestinationPoses,
        Span<float> destinationProgress,
        Span<float> chainProgress,
        out float aggregateScore,
        out bool sufficientValidity)
    {
        int destinationCount = XRHandJoints.DestinationJoints.Length;
        aggregateScore = 0.0f;
        sufficientValidity = false;
        if (liveDestinationPoses.Length < destinationCount
            || destinationProgress.Length < destinationCount
            || chainProgress.Length < ChainCount)
        {
            return false;
        }

        destinationProgress.Clear();
        chainProgress.Clear();

        float includedChainWeight = 0.0f;
        for (int chainIndex = 0; chainIndex < ChainCount; chainIndex++)
        {
            if (profile.ChainWeights[chainIndex] <= 0.0f)
            {
                // Unfeatured chain: the reference supplies no directional signal there.
                continue;
            }

            (int start, int length) = GetChainDestinationRange(chainIndex);
            float chainWeightedProgress = 0.0f;
            float chainLiveWeight = 0.0f;
            for (int offset = 0; offset < length; offset++)
            {
                int destinationIndex = start + offset;
                float weight = profile.DestinationWeights[destinationIndex];
                if (weight <= 0.0f)
                {
                    continue;
                }

                if (!TryExtractProgress(
                        profile,
                        destinationIndex,
                        in liveDestinationPoses[destinationIndex],
                        out float progress))
                {
                    // Invalid destination or degenerate twist: exclude it and renormalise the remaining
                    // intra-chain weights (XR-002 TR48).
                    continue;
                }

                destinationProgress[destinationIndex] = progress;
                chainWeightedProgress += weight * progress;
                chainLiveWeight += weight;
            }

            if (chainLiveWeight <= 0.0f)
            {
                // No valid live destination in this chain: exclude the chain and renormalise the chain weights.
                continue;
            }

            chainProgress[chainIndex] = chainWeightedProgress / chainLiveWeight;
            includedChainWeight += profile.ChainWeights[chainIndex];
        }

        // Fail-closed validity: below the featured-weight fraction the aggregate is not a guess about the hand.
        sufficientValidity = profile.FeaturedChainWeightTotal > 0.0f
            && includedChainWeight / profile.FeaturedChainWeightTotal
                >= profile.Settings.MinimumValidChainWeightFraction;
        if (includedChainWeight <= 0.0f)
        {
            aggregateScore = 0.0f;
            return true;
        }

        // Excluded chains hold progress 0 and weight 0 in the sum; included chains contribute weight × progress
        // and the included weight normalises the aggregate.
        float weightedScore = 0.0f;
        for (int chainIndex = 0; chainIndex < ChainCount; chainIndex++)
        {
            weightedScore += profile.ChainWeights[chainIndex] * chainProgress[chainIndex];
        }

        aggregateScore = weightedScore / includedChainWeight;
        return true;
    }

    /// <summary>
    /// The canonical destination-index range of one chain: thumb 0-2, index 3-5, middle 6-8, ring 9-11,
    /// little 12-14.
    /// </summary>
    public static (int Start, int Length) GetChainDestinationRange(int chainIndex)
        => chainIndex switch
        {
            0 => (0, 3),
            1 => (3, 3),
            2 => (6, 3),
            3 => (9, 3),
            4 => (12, 3),
            _ => throw new ArgumentOutOfRangeException(nameof(chainIndex), chainIndex, "Unknown finger chain."),
        };

    /// <summary>
    /// Extracts one destination's directional progress: the signed twist of the live offset about the reference
    /// axis, divided by the reference angle. Quaternion sign flips cannot change the result because both the
    /// live rotation and the offset are hemisphere-aligned first.
    /// </summary>
    private static bool TryExtractProgress(
        in PowerGripProfile profile,
        int destinationIndex,
        in OpticalFingerProjectedPose livePose,
        out float progress)
    {
        progress = 0.0f;
        if (!livePose.IsValid)
        {
            return false;
        }

        Quaternion neutral = profile.EffectiveNeutrals[destinationIndex];
        Quaternion live = Normalise(livePose.Rotation);
        if (!IsFinite(live))
        {
            return false;
        }

        Quaternion aligned = live.Dot(neutral) < 0.0f
            ? new Quaternion(-live.X, -live.Y, -live.Z, -live.W)
            : live;
        Quaternion offset = Normalise(neutral.Inverse() * aligned);
        offset = offset.W < 0.0f
            ? new Quaternion(-offset.X, -offset.Y, -offset.Z, -offset.W)
            : offset;
        if (!IsFinite(offset))
        {
            return false;
        }

        Vector3 axisPart = new(offset.X, offset.Y, offset.Z);
        float p = axisPart.Dot(profile.ReferenceAxes[destinationIndex]);
        float m = Mathf.Sqrt((offset.W * offset.W) + (p * p));
        if (m <= TwistEpsilon)
        {
            // Degenerate twist extraction (a π rotation about an axis perpendicular to the reference axis):
            // the destination contributes no directional signal this frame.
            return false;
        }

        float theta = 2.0f * Mathf.Atan2(p / m, offset.W / m);
        progress = theta / profile.ReferenceAnglesRadians[destinationIndex];
        return true;
    }

    private static Quaternion Normalise(Quaternion value)
        => value.LengthSquared() <= 0.0000001f ? Quaternion.Identity : value.Normalized();

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
