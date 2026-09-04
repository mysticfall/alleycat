using AlleyCat.Rigging;
using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>Behavioural validation of XR-002's transactional calibration-profile resolver.</summary>
public sealed class OpticalFingerTrackingCalibrationProfileTests
{
    private static readonly OpticalFingerCalibrationMetadata _validMetadata = new(
        OpticalFingerTrackingCalibrationProfile.CurrentSchemaVersion,
        "unit-test-v1",
        "unit-test-rig",
        "unit-test-headset",
        "unit-test-runtime",
        "unit-test-provenance-id",
        "unit-test-semantic-provenance");

    private static readonly OpticalFingerMetacarpalCalibration _validMetacarpal = new(
        new Quaternion(Vector3.Up, 0.4f),
        2.0f,
        new Quaternion(Vector3.Right, 0.3f),
        2.25f);

    /// <summary>Complete records resolve and quaternion double-cover values are canonicalised.</summary>
    [Fact]
    public void CompleteValidProfile_ResolvesEveryRecordAndCanonicalisesQuaternionHemisphere()
    {
        OpticalFingerCalibrationRecord[] records = CreateValidRecords();
        records[0] = records[0] with
        {
            SourceNeutral = new Quaternion(0.0f, 0.0f, 0.0f, -1.0f)
        };
        var resolved = new ResolvedOpticalFingerCalibration[OpticalFingerTrackingCalibrationProfile.RecordCount];
        bool[] valid = new bool[OpticalFingerTrackingCalibrationProfile.RecordCount];
        var resolvedMetacarpal = new ResolvedThumbMetacarpalCalibration[2];

        bool result = OpticalFingerTrackingCalibrationProfile.TryResolveRecords(
            _validMetadata,
            records,
            _validMetacarpal,
            resolved,
            valid,
            resolvedMetacarpal,
            out string error);

        Assert.True(result, error);
        Assert.Empty(error);
        Assert.All(valid, Assert.True);
        Assert.Equal(Quaternion.Identity, resolved[0].SourceNeutral);

        // The per-side metacarpal correspondence records resolve with their gains and canonicalised anchors.
        Assert.Equal(2.0f, resolvedMetacarpal[0].SwingGain);
        Assert.Equal(2.25f, resolvedMetacarpal[1].SwingGain);
        Assert.True(resolvedMetacarpal[0].NeutralAnchor.Dot(_validMetacarpal.LeftNeutralAnchor) > 0.0f);
        Assert.True(resolvedMetacarpal[1].NeutralAnchor.Dot(_validMetacarpal.RightNeutralAnchor) > 0.0f);
        Assert.Equal(0.400f, resolvedMetacarpal[0].HingeGateStart);
        Assert.Equal(0.625f, resolvedMetacarpal[1].HingeGateEnd);
        Assert.Equal(0.050f, resolvedMetacarpal[0].BendGateStart);
        Assert.Equal(0.150f, resolvedMetacarpal[1].BendGateEnd);
    }

    /// <summary>Repeated identities never become usable, including a third occurrence after invalidation.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void DuplicateOrTriplicateIdentity_FailsTransactionallyWithoutUsableEntries(int occurrenceCount)
    {
        OpticalFingerCalibrationRecord[] records = CreateValidRecords();
        for (int occurrence = 1; occurrence < occurrenceCount; occurrence++)
        {
            records[^occurrence] = records[0];
        }

        AssertFailedResolutionHasNoConsumableRecords(
            _validMetadata,
            records,
            "duplicate record Left/ThumbMetacarpal",
            occurrenceCount - 1);
    }

    /// <summary>A missing record and incorrect record count reject the complete profile.</summary>
    [Fact]
    public void MissingRecordAndWrongCount_FailTransactionally()
        => AssertFailedResolutionHasNoConsumableRecords(
            _validMetadata,
            CreateValidRecords()[..^1],
            "expected exactly 30 entries but found 29");

    /// <summary>A missing thumb record rejects the complete profile: all 30 records are required (XR-002 TR44).</summary>
    [Fact]
    public void MissingThumbRecord_FailsTransactionally()
    {
        OpticalFingerCalibrationRecord[] records = CreateValidRecords();
        int thumbProximalIndex = Array.FindIndex(records, record =>
            record.Joint == XRHandJoint.ThumbProximal && record.Side == LimbSide.Left);
        records[thumbProximalIndex] = records[thumbProximalIndex] with
        {
            Joint = XRHandJoint.ThumbMetacarpal
        };

        AssertFailedResolutionHasNoConsumableRecords(
            _validMetadata,
            records,
            "missing or invalid record Left/ThumbProximal");
    }

    /// <summary>Wrist and other unsupported source-only identities reject the complete profile.</summary>
    [Fact]
    public void UnsupportedIdentity_FailsTransactionally()
    {
        OpticalFingerCalibrationRecord[] records = CreateValidRecords();
        records[0] = records[0] with
        {
            Joint = XRHandJoint.IndexMetacarpal
        };

        AssertFailedResolutionHasNoConsumableRecords(
            _validMetadata,
            records,
            "unsupported side/joint record Left/IndexMetacarpal");
    }

    /// <summary>Every serialised quaternion field rejects non-finite and non-normalised values.</summary>
    [Theory]
    [InlineData("S0", true)]
    [InlineData("DestinationNeutral", true)]
    [InlineData("K", true)]
    [InlineData("S0", false)]
    [InlineData("DestinationNeutral", false)]
    [InlineData("K", false)]
    public void InvalidQuaternion_FailsTransactionally(string field, bool nonFinite)
    {
        OpticalFingerCalibrationRecord[] records = CreateValidRecords();
        Quaternion invalid = nonFinite
            ? new Quaternion(float.NaN, 0.0f, 0.0f, 1.0f)
            : new Quaternion(0.0f, 0.0f, 0.0f, 2.0f);
        records[0] = field switch
        {
            "S0" => records[0] with { SourceNeutral = invalid },
            "DestinationNeutral" => records[0] with { DestinationNeutral = invalid },
            "K" => records[0] with { BasisCorrespondence = invalid },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown quaternion field."),
        };

        AssertFailedResolutionHasNoConsumableRecords(
            _validMetadata,
            records,
            "non-finite or non-normalised S0, N, or K quaternion");
    }

    /// <summary>Schema, profile-version, and required provenance metadata are validated.</summary>
    [Theory]
    [InlineData("SchemaVersion")]
    [InlineData("ProfileVersion")]
    [InlineData("ReferenceRig")]
    [InlineData("Headset")]
    [InlineData("Runtime")]
    [InlineData("SourceCaptureID")]
    [InlineData("Provenance")]
    public void InvalidSchemaVersionOrRequiredMetadata_FailsTransactionally(string field)
    {
        OpticalFingerCalibrationMetadata metadata = field switch
        {
            "SchemaVersion" => _validMetadata with { SchemaVersion = "unsupported" },
            "ProfileVersion" => _validMetadata with { ProfileVersion = " " },
            "ReferenceRig" => _validMetadata with { ReferenceRig = string.Empty },
            "Headset" => _validMetadata with { Headset = string.Empty },
            "Runtime" => _validMetadata with { Runtime = string.Empty },
            "SourceCaptureID" => _validMetadata with { SourceCaptureID = string.Empty },
            "Provenance" => _validMetadata with { Provenance = string.Empty },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown metadata field."),
        };

        AssertFailedResolutionHasNoConsumableRecords(
            metadata,
            CreateValidRecords(),
            field == "SchemaVersion"
                ? "unsupported schema version"
                : field == "ProfileVersion" ? "profile version is empty" : "provenance metadata is incomplete");
    }

    /// <summary>Only the currently supported unit gain is accepted.</summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(0.5f)]
    public void UnsupportedGain_FailsTransactionally(float gain)
    {
        OpticalFingerCalibrationRecord[] records = CreateValidRecords();
        records[0] = records[0] with
        {
            Gain = gain
        };

        AssertFailedResolutionHasNoConsumableRecords(_validMetadata, records, "unsupported gain");
    }

    /// <summary>
    /// A non-identity non-thumb K rejects the complete profile rather than being silently ignored: K is
    /// identity-only deprecated compatibility metadata (XR-002 TR44, AC25).
    /// </summary>
    [Fact]
    public void NonIdentityBasisCorrespondence_FailsTransactionally()
    {
        OpticalFingerCalibrationRecord[] records = CreateValidRecords();
        records[7] = records[7] with
        {
            BasisCorrespondence = new Quaternion(Vector3.Up, 0.4f)
        };

        AssertFailedResolutionHasNoConsumableRecords(_validMetadata, records, "non-identity K");
    }

    /// <summary>
    /// A non-identity thumb K rejects the complete profile exactly like a non-thumb record: the K rejection
    /// covers all 30 records (XR-002 TR44, AC25).
    /// </summary>
    [Fact]
    public void NonIdentityThumbBasisCorrespondence_FailsTransactionally()
    {
        OpticalFingerCalibrationRecord[] records = CreateValidRecords();
        int thumbDistalIndex = Array.FindIndex(
            records,
            record => record.Joint == XRHandJoint.ThumbDistal && record.Side == LimbSide.Right);
        records[thumbDistalIndex] = records[thumbDistalIndex] with
        {
            BasisCorrespondence = new Quaternion(Vector3.Right, 0.2f)
        };

        AssertFailedResolutionHasNoConsumableRecords(_validMetadata, records, "non-identity K");
    }

    /// <summary>
    /// The negative quaternion hemisphere of identity still encodes the identity rotation and therefore remains
    /// valid identity-only K metadata (XR-002 TR44).
    /// </summary>
    [Fact]
    public void NegativeIdentityBasisCorrespondence_RemainsValid()
    {
        OpticalFingerCalibrationRecord[] records = CreateValidRecords();
        records[11] = records[11] with
        {
            BasisCorrespondence = new Quaternion(0.0f, 0.0f, 0.0f, -1.0f)
        };
        var resolved = new ResolvedOpticalFingerCalibration[OpticalFingerTrackingCalibrationProfile.RecordCount];
        bool[] valid = new bool[OpticalFingerTrackingCalibrationProfile.RecordCount];
        var resolvedMetacarpal = new ResolvedThumbMetacarpalCalibration[2];

        bool result = OpticalFingerTrackingCalibrationProfile.TryResolveRecords(
            _validMetadata,
            records,
            _validMetacarpal,
            resolved,
            valid,
            resolvedMetacarpal,
            out string error);

        Assert.True(result, error);
        Assert.All(valid, Assert.True);
    }

    /// <summary>A missing/null resource record rejects the complete profile.</summary>
    [Fact]
    public void AbsentRecord_FailsTransactionally()
    {
        OpticalFingerCalibrationRecord[] records = CreateValidRecords();
        records[0] = default;

        AssertFailedResolutionHasNoConsumableRecords(_validMetadata, records, "contains a null entry");
    }

    /// <summary>Caller-owned resolution buffers must provide the complete bilateral capacity.</summary>
    [Fact]
    public void UndersizedCallerBuffers_AreRejected()
    {
        var resolved = new ResolvedOpticalFingerCalibration[OpticalFingerTrackingCalibrationProfile.RecordCount - 1];
        bool[] valid = new bool[OpticalFingerTrackingCalibrationProfile.RecordCount];

        _ = Assert.Throws<ArgumentException>(() => OpticalFingerTrackingCalibrationProfile.TryResolveRecords(
            _validMetadata,
            CreateValidRecords(),
            _validMetacarpal,
            resolved,
            valid,
            new ResolvedThumbMetacarpalCalibration[2],
            out _));
    }

    private static void AssertFailedResolutionHasNoConsumableRecords(
        OpticalFingerCalibrationMetadata metadata,
        OpticalFingerCalibrationRecord[] records,
        string expectedError,
        int expectedErrorOccurrences = 1,
        OpticalFingerMetacarpalCalibration? metacarpal = null)
    {
        metacarpal ??= _validMetacarpal;
        ResolvedOpticalFingerCalibration[] resolved =
        [
            .. Enumerable.Repeat(
                new ResolvedOpticalFingerCalibration(Quaternion.Identity, Quaternion.Identity, Quaternion.Identity),
                OpticalFingerTrackingCalibrationProfile.RecordCount),
        ];
        bool[] valid = [.. Enumerable.Repeat(true, OpticalFingerTrackingCalibrationProfile.RecordCount)];
        var resolvedMetacarpal = new ResolvedThumbMetacarpalCalibration[2];

        bool result = OpticalFingerTrackingCalibrationProfile.TryResolveRecords(
            metadata,
            records,
            metacarpal.Value,
            resolved,
            valid,
            resolvedMetacarpal,
            out string error);

        Assert.False(result);
        Assert.Contains(expectedError, error, StringComparison.Ordinal);
        Assert.Equal(expectedErrorOccurrences, error.Split(expectedError, StringSplitOptions.None).Length - 1);
        Assert.All(valid, Assert.False);
        Assert.All(resolved, value => Assert.Equal(default, value));
        Assert.All(resolvedMetacarpal, value => Assert.Equal(default, value));
    }

    /// <summary>
    /// The per-side metacarpal correspondence records validate fail-closed (XR-002 TR45, TR28.7): a
    /// non-finite or non-unit Q0 anchor and a non-finite or non-positive K_meta gain each reject the
    /// complete profile transactionally — the only non-unit gain in the system is never silently coerced.
    /// </summary>
    [Theory]
    [InlineData("Q0LeftNonFinite")]
    [InlineData("Q0RightNonUnit")]
    [InlineData("GainLeftNaN")]
    [InlineData("GainLeftZero")]
    [InlineData("GainRightNegative")]
    public void InvalidMetacarpalCorrespondenceRecords_FailTransactionally(string kind)
    {
        OpticalFingerMetacarpalCalibration metacarpal = kind switch
        {
            "Q0LeftNonFinite" => _validMetacarpal with
            {
                LeftNeutralAnchor = new Quaternion(float.NaN, 0.0f, 0.0f, 1.0f),
            },
            "Q0RightNonUnit" => _validMetacarpal with
            {
                RightNeutralAnchor = new Quaternion(0.0f, 0.0f, 0.0f, 2.0f),
            },
            "GainLeftNaN" => _validMetacarpal with { LeftSwingGain = float.NaN },
            "GainLeftZero" => _validMetacarpal with { LeftSwingGain = 0.0f },
            "GainRightNegative" => _validMetacarpal with { RightSwingGain = -2.25f },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown metacarpal field."),
        };

        AssertFailedResolutionHasNoConsumableRecords(
            _validMetadata,
            CreateValidRecords(),
            "metacarpal correspondence records are invalid",
            metacarpal: metacarpal);
    }

    /// <summary>Every non-finite, unordered, equal, or out-of-range response threshold rejects all outputs.</summary>
    [Theory]
    [InlineData("H0NaN")]
    [InlineData("H1Infinity")]
    [InlineData("HEqual")]
    [InlineData("HUnordered")]
    [InlineData("HBelowZero")]
    [InlineData("HAboveOne")]
    [InlineData("B0NaN")]
    [InlineData("B1Infinity")]
    [InlineData("BEqual")]
    [InlineData("BUnordered")]
    [InlineData("BBelowZero")]
    [InlineData("BAboveOne")]
    public void InvalidMetacarpalResponseThresholds_FailTransactionally(string kind)
    {
        OpticalFingerMetacarpalCalibration metacarpal = kind switch
        {
            "H0NaN" => _validMetacarpal with { HingeGateStart = float.NaN },
            "H1Infinity" => _validMetacarpal with { HingeGateEnd = float.PositiveInfinity },
            "HEqual" => _validMetacarpal with { HingeGateEnd = _validMetacarpal.HingeGateStart },
            "HUnordered" => _validMetacarpal with { HingeGateStart = 0.8f, HingeGateEnd = 0.2f },
            "HBelowZero" => _validMetacarpal with { HingeGateStart = -0.01f },
            "HAboveOne" => _validMetacarpal with { HingeGateEnd = 1.01f },
            "B0NaN" => _validMetacarpal with { BendGateStart = float.NaN },
            "B1Infinity" => _validMetacarpal with { BendGateEnd = float.NegativeInfinity },
            "BEqual" => _validMetacarpal with { BendGateEnd = _validMetacarpal.BendGateStart },
            "BUnordered" => _validMetacarpal with { BendGateStart = 0.9f, BendGateEnd = 0.1f },
            "BBelowZero" => _validMetacarpal with { BendGateStart = -0.01f },
            "BAboveOne" => _validMetacarpal with { BendGateEnd = 1.01f },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown threshold failure."),
        };

        AssertFailedResolutionHasNoConsumableRecords(
            _validMetadata,
            CreateValidRecords(),
            "finite thresholds must satisfy",
            metacarpal: metacarpal);
    }

    private static OpticalFingerCalibrationRecord[] CreateValidRecords()
    {
        List<OpticalFingerCalibrationRecord> records = [];
        foreach (LimbSide side in new[] { LimbSide.Left, LimbSide.Right })
        {
            foreach (XRHandJoint joint in XRHandJoints.DestinationJoints)
            {
                records.Add(new OpticalFingerCalibrationRecord(
                    true,
                    side,
                    joint,
                    Quaternion.Identity,
                    Quaternion.Identity,
                    Quaternion.Identity,
                    1.0f));
            }
        }

        return [.. records];
    }
}
