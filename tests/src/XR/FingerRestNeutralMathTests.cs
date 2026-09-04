using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>Pure unit coverage for destination-rest-derived non-thumb neutral rotations.</summary>
public sealed class FingerRestNeutralMathTests
{
    private const float Epsilon = 1e-4f;
    private const float RotationEpsilon = 1e-3f;

    /// <summary>
    /// Bilateral asymmetric rest chains align every proximal segment to that hand's middle segment while the
    /// middle chain remains unchanged and all complete parent-local outputs reconstruct the desired globals.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryDerive_AsymmetricHand_AlignsProximalsAndReconstructsDesiredGlobals(bool mirrored)
    {
        Quaternion handRotation = new(new Vector3(0.2f, 0.9f, -0.3f).Normalized(), mirrored ? -0.55f : 0.42f);
        Transform3D hand = Rest(handRotation, new Vector3(mirrored ? 0.25f : -0.25f, 1.2f, 0.1f));
        FingerRestNeutralChain[] chains = CreateAsymmetricChains(mirrored);
        Transform3D[] originalRests = CaptureRests(chains);
        var output = new Quaternion[FingerRestNeutralMath.NeutralCount];

        bool success = FingerRestNeutralMath.TryDerive(10, hand, chains, output, out string error);

        Assert.True(success, error);
        Vector3 target = ProximalDirection(chains[1]);
        for (int chainIndex = 0; chainIndex < FingerRestNeutralMath.ChainCount; chainIndex++)
        {
            int offset = chainIndex * FingerRestNeutralMath.BonesPerChain;
            Quaternion desiredProximal = handRotation * output[offset];
            Quaternion desiredIntermediate = desiredProximal * output[offset + 1];
            Quaternion desiredDistal = desiredIntermediate * output[offset + 2];
            Quaternion restProximal = Rotation(chains[chainIndex].ProximalGlobalRest);
            Quaternion swing = desiredProximal * restProximal.Inverse();

            AssertVectorApproximately(target, Rotate(swing, ProximalDirection(chains[chainIndex])));
            AssertRotationApproximately(swing * restProximal, desiredProximal);
            AssertRotationApproximately(swing * Rotation(chains[chainIndex].IntermediateGlobalRest), desiredIntermediate);
            AssertRotationApproximately(swing * Rotation(chains[chainIndex].DistalGlobalRest), desiredDistal);
        }

        AssertRotationApproximately(
            handRotation.Inverse() * Rotation(chains[1].ProximalGlobalRest),
            output[3]);
        Assert.Equal(originalRests, CaptureRests(chains));
        Assert.All(output, value =>
        {
            Assert.True(IsFinite(value));
            Assert.InRange(value.Length(), 1.0f - Epsilon, 1.0f + Epsilon);
        });
    }

    /// <summary>
    /// A single chain-wide swing preserves segment angle, bend-plane orientation, and every parent-to-child
    /// global-rest rotation; intermediate and distal are never independently direction-aligned.
    /// </summary>
    [Fact]
    public void TryDerive_ChainWideSwing_PreservesInternalGeometryAndRelativeGlobalRotations()
    {
        FingerRestNeutralChain[] chains = CreateAsymmetricChains(mirrored: false);
        var output = new Quaternion[FingerRestNeutralMath.NeutralCount];
        Assert.True(FingerRestNeutralMath.TryDerive(10, Transform3D.Identity, chains, output, out string error), error);

        for (int chainIndex = 0; chainIndex < FingerRestNeutralMath.ChainCount; chainIndex++)
        {
            FingerRestNeutralChain chain = chains[chainIndex];
            int offset = chainIndex * FingerRestNeutralMath.BonesPerChain;
            Quaternion desiredProximal = output[offset];
            Quaternion desiredIntermediate = desiredProximal * output[offset + 1];
            Quaternion desiredDistal = desiredIntermediate * output[offset + 2];
            Quaternion swing = desiredProximal * Rotation(chain.ProximalGlobalRest).Inverse();
            Vector3 firstRestSegment = chain.IntermediateGlobalRest.Origin - chain.ProximalGlobalRest.Origin;
            Vector3 secondRestSegment = chain.DistalGlobalRest.Origin - chain.IntermediateGlobalRest.Origin;
            Vector3 firstDesiredSegment = Rotate(swing, firstRestSegment);
            Vector3 secondDesiredSegment = Rotate(swing, secondRestSegment);

            Assert.InRange(
                firstDesiredSegment.AngleTo(secondDesiredSegment),
                firstRestSegment.AngleTo(secondRestSegment) - Epsilon,
                firstRestSegment.AngleTo(secondRestSegment) + Epsilon);
            AssertVectorApproximately(
                Rotate(swing, firstRestSegment.Cross(secondRestSegment).Normalized()),
                firstDesiredSegment.Cross(secondDesiredSegment).Normalized());
            AssertRotationApproximately(
                Rotation(chain.ProximalGlobalRest).Inverse() * Rotation(chain.IntermediateGlobalRest),
                desiredProximal.Inverse() * desiredIntermediate);
            AssertRotationApproximately(
                Rotation(chain.IntermediateGlobalRest).Inverse() * Rotation(chain.DistalGlobalRest),
                desiredIntermediate.Inverse() * desiredDistal);
        }
    }

    /// <summary>Near-parallel directions take the exact identity swing branch.</summary>
    [Fact]
    public void TryDerive_NearParallelDirection_LeavesChainGlobalRestOrientationsUnchanged()
    {
        FingerRestNeutralChain[] chains = CreateAsymmetricChains(mirrored: false);
        Vector3 target = ProximalDirection(chains[1]);
        chains[0] = WithProximalDirection(chains[0], (target + new Vector3(0.0f, 0.0f, 1e-7f)).Normalized());
        var output = new Quaternion[FingerRestNeutralMath.NeutralCount];

        Assert.True(FingerRestNeutralMath.TryDerive(10, Transform3D.Identity, chains, output, out string error), error);

        AssertRotationApproximately(Rotation(chains[0].ProximalGlobalRest), output[0]);
        AssertRotationApproximately(
            Rotation(chains[0].ProximalGlobalRest).Inverse() * Rotation(chains[0].IntermediateGlobalRest),
            output[1]);
        AssertRotationApproximately(
            Rotation(chains[0].IntermediateGlobalRest).Inverse() * Rotation(chains[0].DistalGlobalRest),
            output[2]);
    }

    /// <summary>Antiparallel handling uses the same rest-basis-derived fallback axis on every invocation.</summary>
    [Fact]
    public void TryDerive_AntiparallelDirection_IsDeterministicAndAlignsToMiddle()
    {
        FingerRestNeutralChain[] chains = CreateAsymmetricChains(mirrored: false);
        Vector3 target = ProximalDirection(chains[1]);
        chains[0] = WithProximalDirection(chains[0], -target);
        var first = new Quaternion[FingerRestNeutralMath.NeutralCount];
        var second = new Quaternion[FingerRestNeutralMath.NeutralCount];

        Assert.True(FingerRestNeutralMath.TryDerive(10, Transform3D.Identity, chains, first, out string firstError), firstError);
        Assert.True(FingerRestNeutralMath.TryDerive(10, Transform3D.Identity, chains, second, out string secondError), secondError);

        Assert.Equal(first, second);
        Quaternion swing = first[0] * Rotation(chains[0].ProximalGlobalRest).Inverse();
        AssertVectorApproximately(target, Rotate(swing, -target));
    }

    /// <summary>Missing, duplicate, and wrong-parent destination topology fails without partial success.</summary>
    [Fact]
    public void TryDerive_InvalidTopology_ReturnsFailure()
    {
        FingerRestNeutralChain[] baseline = CreateAsymmetricChains(mirrored: false);

        FingerRestNeutralChain[] missing = [.. baseline];
        missing[0] = missing[0] with
        {
            ProximalBoneIndex = -1
        };
        AssertFailure(missing, "missing");

        FingerRestNeutralChain[] duplicate = [.. baseline];
        duplicate[3] = duplicate[3] with
        {
            DistalBoneIndex = baseline[0].DistalBoneIndex
        };
        AssertFailure(duplicate, "duplicate");

        FingerRestNeutralChain[] wrongParent = [.. baseline];
        wrongParent[2] = wrongParent[2] with
        {
            IntermediateParentBoneIndex = 10
        };
        AssertFailure(wrongParent, "parent topology");
    }

    /// <summary>Zero-length and non-finite proximal segment geometry is rejected.</summary>
    [Fact]
    public void TryDerive_InvalidSegmentGeometry_ReturnsFailure()
    {
        FingerRestNeutralChain[] zeroLength = CreateAsymmetricChains(mirrored: false);
        zeroLength[0] = zeroLength[0] with
        {
            IntermediateGlobalRest = zeroLength[0].IntermediateGlobalRest with
            {
                Origin = zeroLength[0].ProximalGlobalRest.Origin,
            },
        };
        AssertFailure(zeroLength, "zero-length");

        FingerRestNeutralChain[] nonFinite = CreateAsymmetricChains(mirrored: false);
        nonFinite[0] = nonFinite[0] with
        {
            IntermediateGlobalRest = nonFinite[0].IntermediateGlobalRest with
            {
                Origin = new Vector3(float.NaN, 0.0f, 0.0f),
            },
        };
        AssertFailure(nonFinite, "non-finite");
    }

    /// <summary>Scaled/sheared/reflective invalid rest bases are rejected before quaternion extraction.</summary>
    [Theory]
    [InlineData("non-uniform")]
    [InlineData("shear")]
    [InlineData("determinant")]
    [InlineData("non-finite")]
    public void TryDerive_InvalidRestBasis_ReturnsFailure(string kind)
    {
        FingerRestNeutralChain[] chains = CreateAsymmetricChains(mirrored: false);
        Basis invalid = kind switch
        {
            "non-uniform" => new Basis(Vector3.Right * 2.0f, Vector3.Up, Vector3.Back),
            "shear" => new Basis(Vector3.Right, new Vector3(0.2f, Mathf.Sqrt(0.96f), 0.0f), Vector3.Back),
            "determinant" => new Basis(Vector3.Left, Vector3.Up, Vector3.Back),
            "non-finite" => new Basis(new Vector3(float.PositiveInfinity, 0.0f, 0.0f), Vector3.Up, Vector3.Back),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        chains[0] = chains[0] with
        {
            DistalGlobalRest = new Transform3D(invalid, chains[0].DistalGlobalRest.Origin),
        };

        AssertFailure(chains, kind);
    }

    /// <summary>Positive uniform rest scale is stripped without changing the derived rotations.</summary>
    [Fact]
    public void TryDerive_PositiveUniformScale_StripsScaleAndReturnsFiniteNormalisedRotations()
    {
        FingerRestNeutralChain[] chains = CreateAsymmetricChains(mirrored: false);
        Transform3D hand = ScaleRest(Transform3D.Identity, 1.5f);
        for (int index = 0; index < chains.Length; index++)
        {
            chains[index] = chains[index] with
            {
                ProximalGlobalRest = ScaleRest(chains[index].ProximalGlobalRest, 1.5f),
                IntermediateGlobalRest = ScaleRest(chains[index].IntermediateGlobalRest, 1.5f),
                DistalGlobalRest = ScaleRest(chains[index].DistalGlobalRest, 1.5f),
            };
        }

        var output = new Quaternion[FingerRestNeutralMath.NeutralCount];
        Assert.True(FingerRestNeutralMath.TryDerive(10, hand, chains, output, out string error), error);
        Assert.All(output, value => Assert.InRange(value.Length(), 1.0f - Epsilon, 1.0f + Epsilon));
    }

    private static FingerRestNeutralChain[] CreateAsymmetricChains(bool mirrored)
    {
        float side = mirrored ? -1.0f : 1.0f;
        Vector3[] directions =
        [
            new Vector3(0.45f * side, 0.12f, -0.88f).Normalized(),
            new Vector3(0.03f * side, 0.08f, -1.0f).Normalized(),
            new Vector3(-0.28f * side, -0.04f, -0.96f).Normalized(),
            new Vector3(-0.58f * side, -0.1f, -0.8f).Normalized(),
        ];
        var result = new FingerRestNeutralChain[FingerRestNeutralMath.ChainCount];
        for (int index = 0; index < result.Length; index++)
        {
            int proximalIndex = 20 + (index * 3);
            Vector3 proximalOrigin = new(side * (-0.09f + (index * 0.055f)), 1.0f, -0.05f);
            Vector3 intermediateOrigin = proximalOrigin + (directions[index] * (0.055f + (index * 0.004f)));
            Vector3 bendDirection = Rotate(new Quaternion(Vector3.Up, side * (0.15f + (index * 0.04f))), directions[index]);
            Vector3 distalOrigin = intermediateOrigin + (bendDirection * (0.035f + (index * 0.003f)));
            Quaternion proximalRotation = new(new Vector3(0.3f, 0.7f, side * 0.2f).Normalized(), 0.15f + (index * 0.13f));
            Quaternion intermediateRotation = proximalRotation * new Quaternion(Vector3.Right, 0.2f + (index * 0.09f));
            Quaternion distalRotation = intermediateRotation * new Quaternion(Vector3.Right, 0.12f + (index * 0.05f));
            result[index] = new FingerRestNeutralChain(
                proximalIndex,
                10,
                Rest(proximalRotation, proximalOrigin),
                proximalIndex + 1,
                proximalIndex,
                Rest(intermediateRotation, intermediateOrigin),
                proximalIndex + 2,
                proximalIndex + 1,
                Rest(distalRotation, distalOrigin));
        }

        return result;
    }

    private static FingerRestNeutralChain WithProximalDirection(FingerRestNeutralChain chain, Vector3 direction)
    {
        float length = chain.IntermediateGlobalRest.Origin.DistanceTo(chain.ProximalGlobalRest.Origin);
        return chain with
        {
            IntermediateGlobalRest = chain.IntermediateGlobalRest with
            {
                Origin = chain.ProximalGlobalRest.Origin + (direction.Normalized() * length),
            },
        };
    }

    private static Transform3D[] CaptureRests(IEnumerable<FingerRestNeutralChain> chains)
        => [.. chains.SelectMany(chain => new[]
        {
            chain.ProximalGlobalRest,
            chain.IntermediateGlobalRest,
            chain.DistalGlobalRest,
        })];

    private static Transform3D Rest(Quaternion rotation, Vector3 origin) => new(new Basis(rotation), origin);

    private static Transform3D ScaleRest(Transform3D transform, float scale)
        => new(new Basis(transform.Basis.X * scale, transform.Basis.Y * scale, transform.Basis.Z * scale), transform.Origin);

    private static Quaternion Rotation(Transform3D transform) => transform.Basis.GetRotationQuaternion().Normalized();

    private static Vector3 ProximalDirection(FingerRestNeutralChain chain)
        => (chain.IntermediateGlobalRest.Origin - chain.ProximalGlobalRest.Origin).Normalized();

    private static Vector3 Rotate(Quaternion rotation, Vector3 vector) => new Basis(rotation) * vector;

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static void AssertFailure(FingerRestNeutralChain[] chains, string expectedError)
    {
        var output = new Quaternion[FingerRestNeutralMath.NeutralCount];
        bool success = FingerRestNeutralMath.TryDerive(10, Transform3D.Identity, chains, output, out string error);
        Assert.False(success);
        Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertRotationApproximately(Quaternion expected, Quaternion actual)
    {
        float dot = Mathf.Clamp(Mathf.Abs(expected.Normalized().Dot(actual.Normalized())), 0.0f, 1.0f);
        float angle = 2.0f * Mathf.Acos(dot);
        Assert.True(angle <= RotationEpsilon, $"Expected rotations within {RotationEpsilon} radians, got {angle}.");
    }

    private static void AssertVectorApproximately(Vector3 expected, Vector3 actual)
        => Assert.True(
            expected.Normalized().DistanceTo(actual.Normalized()) <= Epsilon,
            $"Expected {expected}, got {actual}.");
}
