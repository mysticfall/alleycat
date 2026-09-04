using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Derives complete parent-local neutral rotations from non-thumb destination-bone global rest geometry.
/// </summary>
/// <remarks>
/// Inputs and outputs are value-only maths data; adapting a <see cref="Skeleton3D" /> into the four ordered
/// chains happens at binding time. Chain order is index, middle, ring, little. One global shortest-arc swing
/// aligns each proximal segment to the middle proximal segment and is applied unchanged to every global rest
/// orientation in that chain, preserving its imported curvature, bend plane, and roll.
/// </remarks>
public static class FingerRestNeutralMath
{
    /// <summary>Required number of non-thumb chains per hand.</summary>
    public const int ChainCount = 4;

    /// <summary>Number of proximal/intermediate/distal outputs per chain.</summary>
    public const int BonesPerChain = 3;

    /// <summary>Total derived neutral rotations per hand.</summary>
    public const int NeutralCount = ChainCount * BonesPerChain;

    private const int MiddleChainIndex = 1;
    private const float DirectionLengthSquaredEpsilon = 1e-10f;
    private const float ParallelDotTolerance = 1e-6f;
    private const float BasisTolerance = 1e-4f;

    /// <summary>
    /// Derives the 12 effective destination neutral rotations for one hand.
    /// </summary>
    /// <param name="handBoneIndex">Imported hand bone index.</param>
    /// <param name="handGlobalRest">Hand transform in skeleton-local global rest space.</param>
    /// <param name="chains">Exactly four chains ordered index, middle, ring, little.</param>
    /// <param name="destinationNeutrals">Caller-owned output ordered by chain, then proximal/intermediate/distal.</param>
    /// <param name="error">Failure detail, or an empty string on success.</param>
    /// <returns><see langword="true" /> when topology and rest geometry are valid and all outputs were derived.</returns>
    public static bool TryDerive(
        int handBoneIndex,
        Transform3D handGlobalRest,
        ReadOnlySpan<FingerRestNeutralChain> chains,
        Span<Quaternion> destinationNeutrals,
        out string error)
    {
        Span<Quaternion> desiredGlobals = stackalloc Quaternion[NeutralCount];
        return TryDerive(handBoneIndex, handGlobalRest, chains, destinationNeutrals, desiredGlobals, out error);
    }

    /// <summary>
    /// Derives the 12 effective destination neutral rotations for one hand, additionally publishing the swung
    /// desired neutral global bone orientations <c>Q'_j</c> consumed by the per-hand anatomical frame derivation
    /// (XR-002 TR18, TR43).
    /// </summary>
    /// <param name="handBoneIndex">Imported hand bone index.</param>
    /// <param name="handGlobalRest">Hand transform in skeleton-local global rest space.</param>
    /// <param name="chains">Exactly four chains ordered index, middle, ring, little.</param>
    /// <param name="destinationNeutrals">Caller-owned output ordered by chain, then proximal/intermediate/distal.</param>
    /// <param name="desiredGlobalRotations">
    /// Caller-owned output of the swung desired global orientations, in the same chain-major order.</param>
    /// <param name="error">Failure detail, or an empty string on success.</param>
    /// <returns><see langword="true" /> when topology and rest geometry are valid and all outputs were derived.</returns>
    public static bool TryDerive(
        int handBoneIndex,
        Transform3D handGlobalRest,
        ReadOnlySpan<FingerRestNeutralChain> chains,
        Span<Quaternion> destinationNeutrals,
        Span<Quaternion> desiredGlobalRotations,
        out string error)
    {
        if (chains.Length != ChainCount
            || destinationNeutrals.Length < NeutralCount
            || desiredGlobalRotations.Length < NeutralCount)
        {
            error = $"Expected exactly {ChainCount} chains and output buffers of at least {NeutralCount} rotations.";
            return false;
        }

        destinationNeutrals[..NeutralCount].Clear();
        desiredGlobalRotations[..NeutralCount].Clear();
        if (handBoneIndex < 0)
        {
            error = "Hand bone is missing.";
            return false;
        }

        if (!TryExtractRotation(handGlobalRest, "hand", out Quaternion handRotation, out error))
        {
            return false;
        }

        Span<Quaternion> globalRestRotations = stackalloc Quaternion[NeutralCount];
        Span<Vector3> proximalDirections = stackalloc Vector3[ChainCount];
        Span<int> boneIndices = stackalloc int[NeutralCount];

        for (int chainIndex = 0; chainIndex < ChainCount; chainIndex++)
        {
            FingerRestNeutralChain chain = chains[chainIndex];
            int outputOffset = chainIndex * BonesPerChain;
            if (!ValidateTopology(chain, handBoneIndex, chainIndex, boneIndices, outputOffset, out error)
                || !TryExtractRotation(chain.ProximalGlobalRest, $"chain {chainIndex} proximal", out globalRestRotations[outputOffset], out error)
                || !TryExtractRotation(chain.IntermediateGlobalRest, $"chain {chainIndex} intermediate", out globalRestRotations[outputOffset + 1], out error)
                || !TryExtractRotation(chain.DistalGlobalRest, $"chain {chainIndex} distal", out globalRestRotations[outputOffset + 2], out error))
            {
                return false;
            }

            Vector3 segment = chain.IntermediateGlobalRest.Origin - chain.ProximalGlobalRest.Origin;
            if (!IsFinite(segment) || segment.LengthSquared() <= DirectionLengthSquaredEpsilon)
            {
                error = $"Chain {chainIndex} proximal segment is zero-length or non-finite.";
                return false;
            }

            proximalDirections[chainIndex] = segment.Normalized();
        }

        if (HasDuplicateBoneIndices(boneIndices, handBoneIndex, out int duplicateIndex))
        {
            error = $"Destination topology contains duplicate bone index {duplicateIndex}.";
            return false;
        }

        Vector3 targetDirection = proximalDirections[MiddleChainIndex];
        for (int chainIndex = 0; chainIndex < ChainCount; chainIndex++)
        {
            int outputOffset = chainIndex * BonesPerChain;
            Quaternion swing = ShortestArc(
                proximalDirections[chainIndex],
                targetDirection,
                globalRestRotations[outputOffset]);
            Quaternion desiredProximal = CanonicalNormalise(swing * globalRestRotations[outputOffset]);
            Quaternion desiredIntermediate = CanonicalNormalise(swing * globalRestRotations[outputOffset + 1]);
            Quaternion desiredDistal = CanonicalNormalise(swing * globalRestRotations[outputOffset + 2]);

            desiredGlobalRotations[outputOffset] = desiredProximal;
            desiredGlobalRotations[outputOffset + 1] = desiredIntermediate;
            desiredGlobalRotations[outputOffset + 2] = desiredDistal;
            destinationNeutrals[outputOffset] = CanonicalNormalise(handRotation.Inverse() * desiredProximal);
            destinationNeutrals[outputOffset + 1] = CanonicalNormalise(desiredProximal.Inverse() * desiredIntermediate);
            destinationNeutrals[outputOffset + 2] = CanonicalNormalise(desiredIntermediate.Inverse() * desiredDistal);
        }

        error = string.Empty;
        return true;
    }

    private static bool ValidateTopology(
        FingerRestNeutralChain chain,
        int handBoneIndex,
        int chainIndex,
        Span<int> boneIndices,
        int outputOffset,
        out string error)
    {
        if (chain.ProximalBoneIndex < 0 || chain.IntermediateBoneIndex < 0 || chain.DistalBoneIndex < 0)
        {
            error = $"Chain {chainIndex} has a missing destination bone.";
            return false;
        }

        if (chain.ProximalParentBoneIndex != handBoneIndex
            || chain.IntermediateParentBoneIndex != chain.ProximalBoneIndex
            || chain.DistalParentBoneIndex != chain.IntermediateBoneIndex)
        {
            error = $"Chain {chainIndex} has an unsupported parent topology.";
            return false;
        }

        boneIndices[outputOffset] = chain.ProximalBoneIndex;
        boneIndices[outputOffset + 1] = chain.IntermediateBoneIndex;
        boneIndices[outputOffset + 2] = chain.DistalBoneIndex;
        error = string.Empty;
        return true;
    }

    private static bool HasDuplicateBoneIndices(ReadOnlySpan<int> indices, int handBoneIndex, out int duplicateIndex)
    {
        for (int index = 0; index < indices.Length; index++)
        {
            if (indices[index] == handBoneIndex)
            {
                duplicateIndex = indices[index];
                return true;
            }

            for (int candidate = index + 1; candidate < indices.Length; candidate++)
            {
                if (indices[index] == indices[candidate])
                {
                    duplicateIndex = indices[index];
                    return true;
                }
            }
        }

        duplicateIndex = -1;
        return false;
    }

    private static Quaternion ShortestArc(Vector3 from, Vector3 to, Quaternion restRotation)
    {
        float dot = Mathf.Clamp(from.Dot(to), -1.0f, 1.0f);
        if (dot >= 1.0f - ParallelDotTolerance)
        {
            return Quaternion.Identity;
        }

        if (dot <= -1.0f + ParallelDotTolerance)
        {
            Basis restBasis = new(restRotation);
            Vector3 axis = MostPerpendicularRestAxis(from, restBasis);
            return CanonicalNormalise(new Quaternion(axis, Mathf.Pi));
        }

        Vector3 cross = from.Cross(to);
        return CanonicalNormalise(new Quaternion(cross.X, cross.Y, cross.Z, 1.0f + dot));
    }

    private static Vector3 MostPerpendicularRestAxis(Vector3 direction, Basis restBasis)
    {
        Vector3 best = Vector3.Zero;
        float bestLengthSquared = -1.0f;
        Span<Vector3> candidates = [restBasis.X, restBasis.Y, restBasis.Z];
        foreach (Vector3 candidate in candidates)
        {
            Vector3 projected = candidate - (direction * candidate.Dot(direction));
            float lengthSquared = projected.LengthSquared();
            if (lengthSquared > bestLengthSquared)
            {
                best = projected;
                bestLengthSquared = lengthSquared;
            }
        }

        return best.Normalized();
    }

    internal static bool TryExtractRotation(
        Transform3D transform,
        string label,
        out Quaternion rotation,
        out string error)
    {
        Basis basis = transform.Basis;
        if (!IsFinite(transform.Origin) || !IsFinite(basis.X) || !IsFinite(basis.Y) || !IsFinite(basis.Z))
        {
            rotation = Quaternion.Identity;
            error = $"The {label} global rest transform is non-finite.";
            return false;
        }

        float xLength = basis.X.Length();
        float yLength = basis.Y.Length();
        float zLength = basis.Z.Length();
        if (xLength <= BasisTolerance || yLength <= BasisTolerance || zLength <= BasisTolerance)
        {
            rotation = Quaternion.Identity;
            error = $"The {label} global rest basis is degenerate.";
            return false;
        }

        float maximumScale = Mathf.Max(xLength, Mathf.Max(yLength, zLength));
        if (Mathf.Abs(xLength - yLength) > BasisTolerance * maximumScale
            || Mathf.Abs(xLength - zLength) > BasisTolerance * maximumScale)
        {
            rotation = Quaternion.Identity;
            error = $"The {label} global rest basis has unsupported non-uniform scale.";
            return false;
        }

        Vector3 x = basis.X / xLength;
        Vector3 y = basis.Y / yLength;
        Vector3 z = basis.Z / zLength;
        if (Mathf.Abs(x.Dot(y)) > BasisTolerance
            || Mathf.Abs(x.Dot(z)) > BasisTolerance
            || Mathf.Abs(y.Dot(z)) > BasisTolerance)
        {
            rotation = Quaternion.Identity;
            error = $"The {label} global rest basis contains shear.";
            return false;
        }

        Basis rotationBasis = new(x, y, z);
        if (rotationBasis.Determinant() <= 0.0f)
        {
            rotation = Quaternion.Identity;
            error = $"The {label} global rest basis has a non-positive determinant.";
            return false;
        }

        rotation = CanonicalNormalise(rotationBasis.GetRotationQuaternion());
        error = string.Empty;
        return true;
    }

    private static Quaternion CanonicalNormalise(Quaternion value)
    {
        Quaternion normalised = value.Normalized();
        return normalised.W < 0.0f
            ? new Quaternion(-normalised.X, -normalised.Y, -normalised.Z, -normalised.W)
            : normalised;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>Value-only topology and skeleton-local global-rest geometry for one non-thumb finger chain.</summary>
public readonly record struct FingerRestNeutralChain(
    int ProximalBoneIndex,
    int ProximalParentBoneIndex,
    Transform3D ProximalGlobalRest,
    int IntermediateBoneIndex,
    int IntermediateParentBoneIndex,
    Transform3D IntermediateGlobalRest,
    int DistalBoneIndex,
    int DistalParentBoneIndex,
    Transform3D DistalGlobalRest);
