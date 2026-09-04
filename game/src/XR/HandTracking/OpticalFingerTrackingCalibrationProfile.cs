using System.Text;
using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Replaceable, versioned optical finger source-neutral and basis profile for all 30 destinations — the 24
/// non-thumb records plus 6 thumb records — plus the per-side metacarpal correspondence records
/// <c>Q0</c>/<c>K_meta</c> of the anchored hand-frame transfer (XR-002 TR45, TR28.7). Serialised
/// destination-neutral values remain schema-compatible metadata: the effective non-thumb runtime N comes from
/// the bound skeleton's rest geometry and the thumb N from the sampled Reset key (XR-002 TR29).
/// <c>K</c> is identity-only deprecated compatibility metadata for non-thumb and thumb records alike: any
/// non-identity value rejects its record — and therefore the complete profile — rather than being silently
/// ignored (XR-002 TR44). The per-record <c>Gain</c> stays unit-only; the sole non-unit gain is the per-side
/// metacarpal swing gain <c>K_meta</c>, carried as a profile-level record with its <c>Q0</c> anchor and
/// validated fail-closed.
/// </summary>
[Tool]
[GlobalClass]
public partial class OpticalFingerTrackingCalibrationProfile : Resource
{
    /// <summary>Schema understood by the current runtime; the thumb extension bumped it from "1" (XR-002 TR44).</summary>
    public const string CurrentSchemaVersion = "2";

    /// <summary>Expected reference rig identifier.</summary>
    public const string ReferenceFemaleRig = "reference-female";

    /// <summary>Expected headset identifier.</summary>
    public const string Quest3Headset = "Quest 3";

    /// <summary>Expected runtime identifier.</summary>
    public const string WiVRnRuntime = "WiVRn 26.6.2/OpenXR";

    /// <summary>Number of required records per side: 3 thumb plus 12 non-thumb (XR-002 TR44).</summary>
    public const int RecordsPerSide = 15;

    /// <summary>Total number of required bilateral records.</summary>
    public const int RecordCount = RecordsPerSide * 2;

    /// <summary>Authored default h0 threshold.</summary>
    public const float DefaultMetacarpalHingeGateStart = 0.400f;

    /// <summary>Authored default h1 threshold.</summary>
    public const float DefaultMetacarpalHingeGateEnd = 0.625f;

    /// <summary>Authored default b0 threshold.</summary>
    public const float DefaultMetacarpalBendGateStart = 0.050f;

    /// <summary>Authored default b1 threshold.</summary>
    public const float DefaultMetacarpalBendGateEnd = 0.150f;

    private const float NormalisationTolerance = 0.001f;
    private const float IdentityComponentTolerance = 0.0001f;

    /// <summary>Serialised schema identifier.</summary>
    [Export]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Replaceable profile/content version.</summary>
    [Export]
    public string ProfileVersion { get; set; } = string.Empty;

    /// <summary>Rig against which compatibility destination-neutral metadata and basis values were authored.</summary>
    [Export]
    public string ReferenceRig { get; set; } = string.Empty;

    /// <summary>Headset represented by the semantic profile provenance.</summary>
    [Export]
    public string Headset { get; set; } = string.Empty;

    /// <summary>Runtime and OpenXR transport represented by the semantic profile provenance.</summary>
    [Export]
    public string Runtime { get; set; } = string.Empty;

    /// <summary>
    /// Stable semantic identifier for the accepted profile model. The field name is retained for schema compatibility;
    /// its value is not a raw capture identifier.
    /// </summary>
    [Export]
    public string SourceCaptureID { get; set; } = string.Empty;

    /// <summary>Human-readable semantic profile provenance, not raw capture evidence.</summary>
    [Export(PropertyHint.MultilineText)]
    public string Provenance { get; set; } = string.Empty;

    /// <summary>
    /// Per-side metacarpal neutral anchor <c>Q0</c> for the left hand (XR-002 TR45, TR28.7): the rotation, in
    /// the Reset metacarpal local factor, that lands the calibrated neutral transported direction on the
    /// authored longitudinal <c>l</c>. Serialised as the full quaternion because offline calibration derives it
    /// as <c>shortest_arc(l0, l)</c>, whose axis lies in the <c>(b, h)</c> plane rather than along <c>l</c> —
    /// an angle-only record about <c>l</c> cannot reproduce it.
    /// </summary>
    [Export]
    public Quaternion LeftMetacarpalNeutralAnchor { get; set; } = Quaternion.Identity;

    /// <summary>Left-hand metacarpal swing gain <c>K_meta</c> (XR-002 TR45): the only non-unit gain, pinned 2.00.</summary>
    [Export]
    public float LeftMetacarpalSwingGain { get; set; } = 1.0f;

    /// <summary>
    /// Per-side metacarpal neutral anchor <c>Q0</c> for the right hand (XR-002 TR45, TR28.7) — the right-hand
    /// counterpart of <see cref="LeftMetacarpalNeutralAnchor" />.
    /// </summary>
    [Export]
    public Quaternion RightMetacarpalNeutralAnchor { get; set; } = Quaternion.Identity;

    /// <summary>Right-hand metacarpal swing gain <c>K_meta</c> (XR-002 TR45): the only non-unit gain, pinned 2.25.</summary>
    [Export]
    public float RightMetacarpalSwingGain { get; set; } = 1.0f;

    /// <summary>Lower hinge-axis smoothstep threshold shared by both metacarpals (XR-002 R28.7).</summary>
    [Export]
    public float MetacarpalHingeGateStart { get; set; } = DefaultMetacarpalHingeGateStart;

    /// <summary>Upper hinge-axis smoothstep threshold shared by both metacarpals (XR-002 R28.7).</summary>
    [Export]
    public float MetacarpalHingeGateEnd { get; set; } = DefaultMetacarpalHingeGateEnd;

    /// <summary>Lower mirrored-bend smoothstep threshold shared by both metacarpals (XR-002 R28.7).</summary>
    [Export]
    public float MetacarpalBendGateStart { get; set; } = DefaultMetacarpalBendGateStart;

    /// <summary>Upper mirrored-bend smoothstep threshold shared by both metacarpals (XR-002 R28.7).</summary>
    [Export]
    public float MetacarpalBendGateEnd { get; set; } = DefaultMetacarpalBendGateEnd;

    /// <summary>
    /// Provenance of the per-side metacarpal correspondence records (XR-002 TR45).
    /// </summary>
    [Export(PropertyHint.MultilineText)]
    public string MetacarpalCorrespondenceProvenance { get; set; } = string.Empty;

    /// <summary>Explicit bilateral per-destination calibration records.</summary>
    [Export]
    public OpticalFingerTrackingCalibrationEntry[] Entries { get; set; } = [];

    /// <summary>
    /// Resolves authored values into fixed caller-owned buffers. Authored quaternions remain untouched; valid values
    /// are normalised and canonicalised to one hemisphere in the resolved copy. The per-side metacarpal
    /// correspondence records (XR-002 TR45, TR28.7) resolve into their own two-slot buffer under the same
    /// transactional contract.
    /// </summary>
    /// <param name="resolved">Fixed 30-record destination buffer.</param>
    /// <param name="valid">Fixed 30-record validity buffer.</param>
    /// <param name="metacarpal">Fixed two-slot per-side metacarpal correspondence buffer.</param>
    /// <param name="validationError">Concise validation failures, empty when the complete profile is valid.</param>
    /// <returns><see langword="true" /> only for complete valid metadata and exactly 30 valid unique records.</returns>
    public bool TryResolve(
        Span<ResolvedOpticalFingerCalibration> resolved,
        Span<bool> valid,
        Span<ResolvedThumbMetacarpalCalibration> metacarpal,
        out string validationError)
    {
        var records = new OpticalFingerCalibrationRecord[Entries.Length];
        for (int index = 0; index < Entries.Length; index++)
        {
            OpticalFingerTrackingCalibrationEntry? entry = Entries[index];
            records[index] = entry is null
                ? default
                : new OpticalFingerCalibrationRecord(
                    true,
                    entry.Side,
                    entry.Joint,
                    entry.SourceNeutral,
                    entry.DestinationNeutral,
                    entry.BasisCorrespondence,
                    entry.Gain);
        }

        var metadata = new OpticalFingerCalibrationMetadata(
            SchemaVersion,
            ProfileVersion,
            ReferenceRig,
            Headset,
            Runtime,
            SourceCaptureID,
            Provenance);
        return TryResolveRecords(
            metadata,
            records,
            new OpticalFingerMetacarpalCalibration(
                LeftMetacarpalNeutralAnchor,
                 LeftMetacarpalSwingGain,
                 RightMetacarpalNeutralAnchor,
                 RightMetacarpalSwingGain,
                 MetacarpalHingeGateStart,
                 MetacarpalHingeGateEnd,
                 MetacarpalBendGateStart,
                 MetacarpalBendGateEnd),
            resolved,
            valid,
            metacarpal,
            out validationError);
    }

    /// <summary>
    /// Pure behavioural resolver used by the resource adapter and unit tests. Resolution is transactional: failure
    /// clears every caller-owned output rather than exposing a partially valid profile.
    /// </summary>
    public static bool TryResolveRecords(
        OpticalFingerCalibrationMetadata metadata,
        ReadOnlySpan<OpticalFingerCalibrationRecord> records,
        in OpticalFingerMetacarpalCalibration metacarpal,
        Span<ResolvedOpticalFingerCalibration> resolved,
        Span<bool> valid,
        Span<ResolvedThumbMetacarpalCalibration> resolvedMetacarpal,
        out string validationError)
    {
        if (resolved.Length < RecordCount || valid.Length < RecordCount || resolvedMetacarpal.Length < 2)
        {
            throw new ArgumentException(
                $"Calibration resolution requires buffers of at least {RecordCount} records and two metacarpal slots.");
        }

        resolved[..RecordCount].Clear();
        valid[..RecordCount].Clear();
        resolvedMetacarpal[..2].Clear();
        Span<bool> seen = stackalloc bool[RecordCount];
        var errors = new StringBuilder();

        ValidateMetadata(metadata, errors);
        if (errors.Length > 0)
        {
            validationError = errors.ToString();
            return false;
        }

        if (records.Length != RecordCount)
        {
            AppendError(errors, $"expected exactly {RecordCount} entries but found {records.Length}");
        }

        foreach (OpticalFingerCalibrationRecord record in records)
        {
            if (!record.IsPresent)
            {
                AppendError(errors, "contains a null entry");
                continue;
            }

            if (!TryGetRecordIndex(record.Side, record.Joint, out int recordIndex))
            {
                AppendError(errors, $"unsupported side/joint record {record.Side}/{record.Joint}");
                continue;
            }

            if (seen[recordIndex])
            {
                valid[recordIndex] = false;
                AppendError(errors, $"duplicate record {record.Side}/{record.Joint}");
                continue;
            }

            seen[recordIndex] = true;

            if (!IsFiniteNormalised(record.SourceNeutral)
                || !IsFiniteNormalised(record.DestinationNeutral)
                || !IsFiniteNormalised(record.BasisCorrespondence))
            {
                AppendError(errors, $"record {record.Side}/{record.Joint} has a non-finite or non-normalised S0, N, or K quaternion");
                continue;
            }

            if (!float.IsFinite(record.Gain) || record.Gain != 1.0f)
            {
                AppendError(errors, $"record {record.Side}/{record.Joint} has unsupported gain {record.Gain}; current version requires exactly 1");
                continue;
            }

            // K is identity-only deprecated compatibility metadata for non-thumb and thumb records alike
            // (XR-002 TR44): the resolver rejects a non-identity value as an invalid record — driving its
            // destination to Requirement 34's freeze fallback — rather than silently ignoring it.
            if (!IsIdentity(record.BasisCorrespondence))
            {
                AppendError(errors, $"record {record.Side}/{record.Joint} has a non-identity K; basis correspondence is identity-only deprecated compatibility metadata");
                continue;
            }

            resolved[recordIndex] = new ResolvedOpticalFingerCalibration(
                CanonicalNormalise(record.SourceNeutral),
                CanonicalNormalise(record.DestinationNeutral),
                CanonicalNormalise(record.BasisCorrespondence));
            valid[recordIndex] = true;
        }

        // Per-side metacarpal correspondence records (XR-002 TR45, TR28.7): the neutral anchor Q0 must be a
        // finite unit quaternion and the metacarpal swing gain K_meta a finite positive scalar. An invalid
        // record rejects the complete profile — the only gain in the system that is not exactly 1, never a
        // fitted or clamped runtime value.
        bool metacarpalValid = IsFiniteNormalised(metacarpal.LeftNeutralAnchor)
            && IsFiniteNormalised(metacarpal.RightNeutralAnchor)
            && float.IsFinite(metacarpal.LeftSwingGain)
            && metacarpal.LeftSwingGain > 0.0f
             && float.IsFinite(metacarpal.RightSwingGain)
             && metacarpal.RightSwingGain > 0.0f
             && IsValidThresholdPair(metacarpal.HingeGateStart, metacarpal.HingeGateEnd)
             && IsValidThresholdPair(metacarpal.BendGateStart, metacarpal.BendGateEnd);
        if (!metacarpalValid)
        {
            AppendError(
                errors,
                 "metacarpal correspondence records are invalid: each Q0 must be a finite unit quaternion, each " +
                 "K_meta a finite positive gain, and finite thresholds must satisfy 0 <= h0 < h1 <= 1 and " +
                 "0 <= b0 < b1 <= 1");
        }

        for (int index = 0; index < RecordCount; index++)
        {
            if (!valid[index])
            {
                (LimbSide side, XRHandJoint joint) = GetRecordIdentity(index);
                AppendError(errors, $"missing or invalid record {side}/{joint}");
            }
        }

        validationError = errors.ToString();
        if (errors.Length == 0)
        {
            resolvedMetacarpal[0] = new ResolvedThumbMetacarpalCalibration(
                 CanonicalNormalise(metacarpal.LeftNeutralAnchor),
                 metacarpal.LeftSwingGain,
                 metacarpal.HingeGateStart,
                 metacarpal.HingeGateEnd,
                 metacarpal.BendGateStart,
                 metacarpal.BendGateEnd);
            resolvedMetacarpal[1] = new ResolvedThumbMetacarpalCalibration(
                 CanonicalNormalise(metacarpal.RightNeutralAnchor),
                 metacarpal.RightSwingGain,
                 metacarpal.HingeGateStart,
                 metacarpal.HingeGateEnd,
                 metacarpal.BendGateStart,
                 metacarpal.BendGateEnd);
            return true;
        }

        // Resolution is transactional: an invalid profile never exposes a usable subset to callers, including
        // duplicate identities whose first occurrence happened to be valid.
        resolved[..RecordCount].Clear();
        valid[..RecordCount].Clear();
        resolvedMetacarpal[..2].Clear();
        return false;
    }

    /// <summary>Maps one supported side/joint identity to the resolved bilateral buffer.</summary>
    public static bool TryGetRecordIndex(LimbSide side, XRHandJoint joint, out int index)
    {
        if ((side != LimbSide.Left && side != LimbSide.Right)
            || !XRHandJoints.TryGetDestinationIndex(joint, out int jointIndex))
        {
            index = -1;
            return false;
        }

        index = ((int)side * RecordsPerSide) + jointIndex;
        return true;
    }

    /// <summary>Returns the identity represented by a resolved bilateral-buffer index.</summary>
    public static (LimbSide Side, XRHandJoint Joint) GetRecordIdentity(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, RecordCount);
        LimbSide side = index < RecordsPerSide ? LimbSide.Left : LimbSide.Right;
        return (side, XRHandJoints.DestinationJoints[index % RecordsPerSide]);
    }

    private static void ValidateMetadata(OpticalFingerCalibrationMetadata metadata, StringBuilder errors)
    {
        if (!string.Equals(metadata.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            AppendError(errors, $"unsupported schema version '{metadata.SchemaVersion}'");
        }

        if (string.IsNullOrWhiteSpace(metadata.ProfileVersion))
        {
            AppendError(errors, "profile version is empty");
        }

        if (string.IsNullOrWhiteSpace(metadata.ReferenceRig)
            || string.IsNullOrWhiteSpace(metadata.Headset)
            || string.IsNullOrWhiteSpace(metadata.Runtime)
            || string.IsNullOrWhiteSpace(metadata.SourceCaptureID)
            || string.IsNullOrWhiteSpace(metadata.Provenance))
        {
            AppendError(errors, "provenance metadata is incomplete");
        }
    }

    private static bool IsFiniteNormalised(Quaternion value)
    {
        if (!float.IsFinite(value.X)
            || !float.IsFinite(value.Y)
            || !float.IsFinite(value.Z)
            || !float.IsFinite(value.W))
        {
            return false;
        }

        float lengthSquared = value.LengthSquared();
        return lengthSquared > 0.0f && Mathf.Abs(lengthSquared - 1.0f) <= NormalisationTolerance;
    }

    private static bool IsIdentity(Quaternion value)
        => Mathf.Abs(value.X) <= IdentityComponentTolerance
            && Mathf.Abs(value.Y) <= IdentityComponentTolerance
            && Mathf.Abs(value.Z) <= IdentityComponentTolerance
            && (Mathf.Abs(value.W - 1.0f) <= IdentityComponentTolerance
                || Mathf.Abs(value.W + 1.0f) <= IdentityComponentTolerance);

    private static bool IsValidThresholdPair(float start, float end)
        => float.IsFinite(start)
            && float.IsFinite(end)
            && start >= 0.0f
            && start < end
            && end <= 1.0f;

    private static Quaternion CanonicalNormalise(Quaternion value)
    {
        Quaternion normalised = value.Normalized();
        return normalised.W < 0.0f
            ? new Quaternion(-normalised.X, -normalised.Y, -normalised.Z, -normalised.W)
            : normalised;
    }

    private static void AppendError(StringBuilder errors, string error)
    {
        if (errors.Length > 0)
        {
            _ = errors.Append("; ");
        }

        _ = errors.Append(error);
    }
}

/// <summary>
/// Immutable, normalised profile values pinned for one optical session. <see cref="DestinationNeutral" /> is retained
/// as compatibility metadata and is not the effective N consumed by production retargeting.
/// </summary>
public readonly record struct ResolvedOpticalFingerCalibration(
    Quaternion SourceNeutral,
    Quaternion DestinationNeutral,
    Quaternion BasisCorrespondence);

/// <summary>
/// The resolved per-side metacarpal correspondence record (XR-002 TR45, TR28.7): the neutral anchor
/// <c>Q0</c> in the Reset metacarpal local factor and the metacarpal swing gain <c>K_meta</c> — the only
/// gain in the system that is not exactly 1. Never fitted at runtime.
/// </summary>
public readonly record struct ResolvedThumbMetacarpalCalibration(
    Quaternion NeutralAnchor,
    float SwingGain,
    float HingeGateStart,
    float HingeGateEnd,
    float BendGateStart,
    float BendGateEnd);

/// <summary>Plain metadata input for transactional calibration resolution.</summary>
public readonly record struct OpticalFingerCalibrationMetadata(
    string SchemaVersion,
    string ProfileVersion,
    string ReferenceRig,
    string Headset,
    string Runtime,
    string SourceCaptureID,
    string Provenance);

/// <summary>
/// Plain per-side metacarpal correspondence input for transactional calibration resolution (XR-002 TR45):
/// the neutral anchors and swing gains of both hands.
/// </summary>
public readonly record struct OpticalFingerMetacarpalCalibration(
    Quaternion LeftNeutralAnchor,
    float LeftSwingGain,
    Quaternion RightNeutralAnchor,
    float RightSwingGain,
    float HingeGateStart,
    float HingeGateEnd,
    float BendGateStart,
    float BendGateEnd)
{
    /// <summary>Creates a record using the authored response-threshold defaults.</summary>
    public OpticalFingerMetacarpalCalibration(
        Quaternion leftNeutralAnchor,
        float leftSwingGain,
        Quaternion rightNeutralAnchor,
        float rightSwingGain)
        : this(
            leftNeutralAnchor,
            leftSwingGain,
            rightNeutralAnchor,
            rightSwingGain,
            OpticalFingerTrackingCalibrationProfile.DefaultMetacarpalHingeGateStart,
            OpticalFingerTrackingCalibrationProfile.DefaultMetacarpalHingeGateEnd,
            OpticalFingerTrackingCalibrationProfile.DefaultMetacarpalBendGateStart,
            OpticalFingerTrackingCalibrationProfile.DefaultMetacarpalBendGateEnd)
    {
    }
}

/// <summary>Plain authored-record input for transactional calibration resolution.</summary>
public readonly record struct OpticalFingerCalibrationRecord(
    bool IsPresent,
    LimbSide Side,
    XRHandJoint Joint,
    Quaternion SourceNeutral,
    Quaternion DestinationNeutral,
    Quaternion BasisCorrespondence,
    float Gain);
