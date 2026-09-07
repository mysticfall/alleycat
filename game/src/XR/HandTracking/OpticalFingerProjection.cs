using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Why one projected destination did not receive a fresh rotation, mirroring the modifier's validity ladder
/// (XR-002 TR21-TR22, TR26, TR29, TR34-TR35).
/// </summary>
public enum OpticalFingerProjectionStatus : byte
{
    /// <summary>The destination received a fresh destination-local rotation prediction.</summary>
    Valid = 0,

    /// <summary>
    /// The destination's own source joint or its required source parent failed the provider gate — including the
    /// explicit wrist requirement of the thumb metacarpal and thumb proximal (XR-002 TR28-TR29, TR33, TR35).
    /// </summary>
    FrozenInvalidSource = 1,

    /// <summary>Defensive: the destination unexpectedly lacks a calibration-record index (XR-002 TR34).</summary>
    FrozenMissingCalibration = 2,

    /// <summary>The destination's pinned calibration record is absent or invalid, so it keeps its freeze fallback
    /// instead of an unnormalised direct mapping (XR-002 TR34).</summary>
    FrozenInvalidCalibration = 3,

    /// <summary>A degenerate anatomical input failed only this destination (XR-002 TR21-TR22, TR26).</summary>
    FrozenDegenerateMapping = 4,
}

/// <summary>
/// One destination's projected pose: either a fresh destination-local rotation — the exact <c>D_j</c> the
/// retargeting modifier would write — or the freeze marker explaining why none was produced.
/// </summary>
public readonly record struct OpticalFingerProjectedPose(OpticalFingerProjectionStatus Status, Quaternion Rotation)
{
    /// <summary>Whether <see cref="Rotation" /> carries a fresh valid prediction.</summary>
    public bool IsValid => Status == OpticalFingerProjectionStatus.Valid;

    /// <summary>Creates a frozen marker; the rotation placeholder is never consumed by valid-path callers.</summary>
    public static OpticalFingerProjectedPose Frozen(OpticalFingerProjectionStatus status)
        => new(status, Quaternion.Identity);
}

/// <summary>
/// Shared calibrated anatomical projection seam (XR-002 TR20-TR29): the single source of truth that turns raw
/// per-side optical joint samples into the 15 destination-local finger rotations of that side — the exact same
/// maths the skeleton modifier writes — without touching any skeleton, animation tree, or rendered pose.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it holds.</strong> The binding-derived state captured once per skeleton binding — the effective
/// destination neutrals <c>N_j</c> (rest-derived for the non-thumb destinations, Reset-sampled for the thumbs),
/// the per-bone local hinge axes, the local proximal frames plus the thumb metacarpal's bend/splay frame, and the
/// thumb correspondence frames — and the calibration state resolved once per optical session — the pinned
/// <c>S0_j</c> records with their validity and the per-side metacarpal correspondence records. The modifier stages
/// both exactly as before; nothing here re-derives skeleton state, so a staged binding never reads or writes a
/// <see cref="Skeleton3D" />, <see cref="AnimationTree" />, or rendered pose (XR-002 TR18, TR26, TR43).
/// </para>
/// <para>
/// <strong>What it projects.</strong> <see cref="TryProject" /> applies the modifier's exact per-destination
/// ladder: provider-gated sample acceptance with the two thumb wrist exceptions, the parent-relative source
/// relation <c>S</c> (XR-002 TR20), the neutral delta <c>S0⁻¹ × S</c>, the calibration-record gate with its
/// freeze fallback (XR-002 TR34), and the constrained anatomical dispatch through
/// <see cref="FingerAnatomicalMath" /> — signed hinge flexion for non-thumb intermediate/distal destinations and
/// the thumb proximal/distal about their independent authored axes, one roll-free directional swing for the
/// non-thumb proximals, and the anchored hand-frame correspondence transfer for the thumb metacarpal (XR-002
/// TR21-TR22, TR26-TR28.7). Quaternion write-hemisphere stabilisation stays with the writing modifier; the
/// predicted rotation encodes the identical rotation.
/// </para>
/// <para>
/// <strong>Who consumes it.</strong> The player finger modifier consumes it for its skeleton writes, and the
/// optical grab recogniser consumes it to compare live finger articulation in destination-local space without
/// reading the rendered skeleton (XR-002 TR49, CTRL-002 TR13). The steady-state projection path performs no
/// allocation: it reads the staged buffers and the caller's sample span and writes the caller's destination span.
/// </para>
/// </remarks>
public sealed class OpticalFingerProjectionBinding
{
    /// <summary>Number of canonical finger destination bones per hand side (XR-002 TR12).</summary>
    public const int FingerBonesPerSide = 15;

    /// <summary>Total canonical finger destination bones covered by one binding (both sides).</summary>
    public const int FingerBoneCount = FingerBonesPerSide * 2;

    private const int TrackedJointCount = 20;

    // Initialisation order matters: the destination table must exist before the derived name table.
    private static readonly DestinationFingerBone[] _destinationBones = BuildDestinationBones();

    private static readonly string[] _canonicalFingerBoneNames = BuildCanonicalFingerBoneNames();

    private readonly Quaternion[] _effectiveDestinationNeutrals =
        new Quaternion[OpticalFingerTrackingCalibrationProfile.RecordCount];

    private readonly Vector3[] _localHingeAxes =
        new Vector3[OpticalFingerTrackingCalibrationProfile.RecordCount];

    private readonly FingerAnatomicalFrame[] _localProximalFrames =
        new FingerAnatomicalFrame[OpticalFingerTrackingCalibrationProfile.RecordCount];

    private readonly AuthoredThumbCorrespondenceFrame[] _thumbCorrespondenceFrames = new AuthoredThumbCorrespondenceFrame[2];

    private readonly ResolvedOpticalFingerCalibration[] _resolvedCalibration =
        new ResolvedOpticalFingerCalibration[OpticalFingerTrackingCalibrationProfile.RecordCount];

    private readonly bool[] _calibrationValid =
        new bool[OpticalFingerTrackingCalibrationProfile.RecordCount];

    private readonly ResolvedThumbMetacarpalCalibration[] _metacarpalCalibration = new ResolvedThumbMetacarpalCalibration[2];

    /// <summary>
    /// Canonical destination bone names in processing order: the 15 <c>Left</c>-prefixed finger bones followed by the
    /// 15 <c>Right</c>-prefixed finger bones, each side in <see cref="XRHandJoints.DestinationJoints" /> order
    /// (XR-002 TR12).
    /// </summary>
    public static IReadOnlyList<string> CanonicalFingerBoneNames => _canonicalFingerBoneNames;

    /// <summary>Whether binding-derived state is staged and projections may run.</summary>
    public bool IsBindingStaged
    {
        get;
        private set;
    }

    /// <summary>Whether calibration state is staged and projections may run.</summary>
    public bool IsCalibrationStaged
    {
        get;
        private set;
    }

    /// <summary>
    /// Monotonic version of the staged binding/calibration inputs. Consumers may cache derived data only while this
    /// value and the owning modifier identity remain unchanged.
    /// </summary>
    public long Generation
    {
        get;
        private set;
    }

    /// <summary>
    /// Stages the binding-derived state of one skeleton binding transactionally: either every buffer copies and
    /// the binding becomes projectable, or nothing stages (XR-002 TR18, TR26, TR43).
    /// </summary>
    /// <param name="effectiveDestinationNeutrals">Effective neutrals <c>N_j</c>, bilateral record order.</param>
    /// <param name="localHingeAxes">Per-bone local hinge axes, bilateral record order.</param>
    /// <param name="localProximalFrames">Local proximal frames — and the thumb metacarpal frame — bilateral record order.</param>
    /// <param name="thumbCorrespondenceFrames">Per-side metacarpal correspondence frames (XR-002 TR28.7).</param>
    /// <param name="error">Failure reason when staging is rejected.</param>
    public bool TryStageBinding(
        ReadOnlySpan<Quaternion> effectiveDestinationNeutrals,
        ReadOnlySpan<Vector3> localHingeAxes,
        ReadOnlySpan<FingerAnatomicalFrame> localProximalFrames,
        ReadOnlySpan<AuthoredThumbCorrespondenceFrame> thumbCorrespondenceFrames,
        out string error)
    {
        int recordCount = OpticalFingerTrackingCalibrationProfile.RecordCount;
        if (effectiveDestinationNeutrals.Length < recordCount
            || localHingeAxes.Length < recordCount
            || localProximalFrames.Length < recordCount
            || thumbCorrespondenceFrames.Length < 2)
        {
            ClearBinding();
            error = $"Projection binding requires {recordCount} neutrals, hinge axes, and proximal frames plus " +
                "two correspondence frames.";
            return false;
        }

        ClearBinding();
        effectiveDestinationNeutrals[..recordCount].CopyTo(_effectiveDestinationNeutrals);
        localHingeAxes[..recordCount].CopyTo(_localHingeAxes);
        localProximalFrames[..recordCount].CopyTo(_localProximalFrames);
        thumbCorrespondenceFrames[..2].CopyTo(_thumbCorrespondenceFrames);
        IsBindingStaged = true;
        Generation++;
        error = string.Empty;
        return true;
    }

    /// <summary>Clears the binding-derived state; calibration state is voided with it because a rebind ends the session.</summary>
    public void ClearBinding()
    {
        _effectiveDestinationNeutrals.AsSpan().Clear();
        _localHingeAxes.AsSpan().Clear();
        _localProximalFrames.AsSpan().Clear();
        _thumbCorrespondenceFrames.AsSpan().Clear();
        IsBindingStaged = false;
        Generation++;
        ClearCalibration();
    }

    /// <summary>
    /// Stages the calibration state resolved for one optical session. An invalid or absent profile is staged as an
    /// all-invalid calibration so every destination uses its freeze fallback — never an unnormalised direct mapping
    /// (XR-002 TR34).
    /// </summary>
    /// <param name="resolvedCalibration">Resolved pinned records, bilateral record order.</param>
    /// <param name="calibrationValid">Per-record validity, bilateral record order.</param>
    /// <param name="metacarpalCalibration">Per-side metacarpal correspondence records (XR-002 TR45).</param>
    /// <exception cref="ArgumentException">Thrown when a buffer has the wrong length — a programming error.</exception>
    public void StageCalibration(
        ReadOnlySpan<ResolvedOpticalFingerCalibration> resolvedCalibration,
        ReadOnlySpan<bool> calibrationValid,
        ReadOnlySpan<ResolvedThumbMetacarpalCalibration> metacarpalCalibration)
    {
        int recordCount = OpticalFingerTrackingCalibrationProfile.RecordCount;
        if (resolvedCalibration.Length < recordCount
            || calibrationValid.Length < recordCount
            || metacarpalCalibration.Length < 2)
        {
            throw new ArgumentException(
                $"Calibration staging requires {recordCount} records and validity flags plus two metacarpal records.");
        }

        resolvedCalibration[..recordCount].CopyTo(_resolvedCalibration);
        calibrationValid[..recordCount].CopyTo(_calibrationValid);
        metacarpalCalibration[..2].CopyTo(_metacarpalCalibration);
        IsCalibrationStaged = true;
        Generation++;
    }

    /// <summary>Clears the calibration state; destinations stay unprojectable until the next session stages again.</summary>
    public void ClearCalibration()
    {
        _resolvedCalibration.AsSpan().Clear();
        _calibrationValid.AsSpan().Clear();
        _metacarpalCalibration.AsSpan().Clear();
        IsCalibrationStaged = false;
        Generation++;
    }

    /// <summary>
    /// Copies one side's 15 effective neutrals — the profile-derivation inputs of the animation-derived grip
    /// references — in canonical <see cref="XRHandJoints.DestinationJoints" /> order.
    /// </summary>
    public bool TryCopySideEffectiveNeutrals(LimbSide side, Span<Quaternion> destination)
    {
        if (!IsBindingStaged || destination.Length < FingerBonesPerSide)
        {
            return false;
        }

        int sideOffset = (int)side * FingerBonesPerSide;
        for (int fingerIndex = 0; fingerIndex < FingerBonesPerSide; fingerIndex++)
        {
            destination[fingerIndex] = _effectiveDestinationNeutrals[sideOffset + fingerIndex];
        }

        return true;
    }

    /// <summary>
    /// Projects one side's 15 destination-local finger rotations from raw joint samples through the exact
    /// modifier mapping (XR-002 TR20-TR29).
    /// </summary>
    /// <param name="side">Hand side to project.</param>
    /// <param name="jointSamples">Raw per-side joint samples indexed by <see cref="XRHandJoint" /> (20 entries).</param>
    /// <param name="destinationPoses">Output span of 15 poses in <see cref="XRHandJoints.DestinationJoints" /> order.</param>
    /// <returns>
    /// <see langword="true" /> when the projection ran and filled <paramref name="destinationPoses" />;
    /// <see langword="false" /> — leaving the span untouched — when the binding or calibration is not staged or a
    /// span has the wrong length (fail closed).
    /// </returns>
    public bool TryProject(
        LimbSide side,
        ReadOnlySpan<XRHandJointSourceSample> jointSamples,
        Span<OpticalFingerProjectedPose> destinationPoses)
    {
        if (!IsBindingStaged
            || !IsCalibrationStaged
            || jointSamples.Length < TrackedJointCount
            || destinationPoses.Length < FingerBonesPerSide)
        {
            return false;
        }

        int sideOffset = (int)side * FingerBonesPerSide;
        for (int fingerIndex = 0; fingerIndex < FingerBonesPerSide; fingerIndex++)
        {
            DestinationFingerBone destination = _destinationBones[sideOffset + fingerIndex];
            destinationPoses[fingerIndex] = ProjectDestination(side, jointSamples, in destination);
        }

        return true;
    }

    /// <summary>
    /// A destination accepts its own provider-gated source sample; the immediate source parent's acceptance state
    /// is never consumed recursively here — an invalid grandparent already propagates through the provider's
    /// direct raw-parent classification. The thumb metacarpal and thumb proximal are the sole explicit exceptions:
    /// each additionally requires an accepted wrist sample, so an invalid wrist freezes exactly those two thumb
    /// destinations while a valid thumb distal and every non-thumb chain continue (XR-002 TR28-TR29, TR33, TR36).
    /// </summary>
    private static bool IsSampleAccepted(ReadOnlySpan<XRHandJointSourceSample> jointSamples, in DestinationFingerBone destination)
        => jointSamples[(int)destination.Joint].ProductionAccepted
            && (destination.Joint is not (XRHandJoint.ThumbMetacarpal or XRHandJoint.ThumbProximal)
                || jointSamples[(int)XRHandJoint.Wrist].ProductionAccepted);

    private OpticalFingerProjectedPose ProjectDestination(
        LimbSide side,
        ReadOnlySpan<XRHandJointSourceSample> jointSamples,
        in DestinationFingerBone destination)
    {
        if (!IsSampleAccepted(jointSamples, in destination))
        {
            return OpticalFingerProjectedPose.Frozen(OpticalFingerProjectionStatus.FrozenInvalidSource);
        }

        // The parent-relative tracked relation supplies S (XR-002 TR20). Non-thumb proximals use their
        // metacarpal parent here, never the wrist (there is no wrist fallback: an invalid metacarpal freezes
        // the destination below).
        Quaternion sourceRelation = FingerRetargetingMath.DeriveParentRelativeRotation(
            jointSamples[(int)destination.ParentJoint].ProductionWorldTransform.Basis,
            jointSamples[(int)destination.Joint].ProductionWorldTransform.Basis);

        if (!OpticalFingerTrackingCalibrationProfile.TryGetRecordIndex(
                side,
                destination.Joint,
                out int calibrationIndex))
        {
            // Unreachable for the canonical destination table; freeze defensively rather than mapping without a
            // record (XR-002 TR34 — no direct unnormalised write may exist).
            return OpticalFingerProjectedPose.Frozen(OpticalFingerProjectionStatus.FrozenMissingCalibration);
        }

        if (!_calibrationValid[calibrationIndex])
        {
            return OpticalFingerProjectedPose.Frozen(OpticalFingerProjectionStatus.FrozenInvalidCalibration);
        }

        Quaternion delta = FingerRetargetingMath.DeriveNeutralDelta(
            sourceRelation,
            _resolvedCalibration[calibrationIndex].SourceNeutral);

        // The thumb is authored-animation (XR-002 TR26-TR28.7): the metacarpal takes the anchored hand-frame
        // correspondence transfer — the live wrist→metacarpal relation through the binding palm plane's
        // (u, t, n_palm,H) axes, pinned Q0, and profile-scoped directional response — reading S directly so the
        // mapping is S0-drift-invariant by construction, and the thumb proximal/distal take signed hinge flexion
        // about their independent authored axes — cached in the thumb local hinge-axis slots. No
        // rest-geometry-derived destination frame, shared hinge, or synthetic tip exists anywhere in the thumb
        // path. Non-thumb proximals receive one roll-free directional swing (XR-002 TR22) and every other
        // destination signed hinge flexion about its cached local hinge axis (XR-002 TR21).
        bool mapped = destination.Joint == XRHandJoint.ThumbMetacarpal
            ? FingerAnatomicalMath.TryMapThumbMetacarpalSwing(
                sourceRelation,
                _thumbCorrespondenceFrames[(int)side],
                side,
                _localProximalFrames[calibrationIndex],
                _effectiveDestinationNeutrals[calibrationIndex],
                _metacarpalCalibration[(int)side],
                out Quaternion rotation)
            : IsSwingDestination(destination.Joint)
                ? FingerAnatomicalMath.TryMapProximalDestination(
                    delta,
                    _localProximalFrames[calibrationIndex],
                    _effectiveDestinationNeutrals[calibrationIndex],
                    out rotation)
                : FingerAnatomicalMath.TryMapHingeDestination(
                    delta,
                    _localHingeAxes[calibrationIndex],
                    _effectiveDestinationNeutrals[calibrationIndex],
                    out rotation);

        return mapped
            ? new OpticalFingerProjectedPose(OpticalFingerProjectionStatus.Valid, rotation)
            : OpticalFingerProjectedPose.Frozen(OpticalFingerProjectionStatus.FrozenDegenerateMapping);
    }

    /// <summary>
    /// Swing destinations receive one roll-free directional swing: the non-thumb proximals (XR-002 TR22). The
    /// thumb metacarpal dispatches separately to the anchored hand-frame correspondence transfer with its pinned
    /// anchor and swing gain (XR-002 TR28.7).
    /// </summary>
    private static bool IsSwingDestination(XRHandJoint joint)
        => joint is XRHandJoint.IndexProximal
            or XRHandJoint.MiddleProximal
            or XRHandJoint.RingProximal
            or XRHandJoint.LittleProximal;

    private static string[] BuildCanonicalFingerBoneNames()
    {
        string[] names = new string[FingerBoneCount];
        for (int index = 0; index < _destinationBones.Length; index++)
        {
            names[index] = GetSidePrefix(_destinationBones[index].Side) + _destinationBones[index].Joint;
        }

        return names;
    }

    private static DestinationFingerBone[] BuildDestinationBones()
    {
        var bones = new DestinationFingerBone[FingerBoneCount];
        int writeIndex = 0;
        foreach (LimbSide side in new[] { LimbSide.Left, LimbSide.Right })
        {
            foreach (XRHandJoint joint in XRHandJoints.DestinationJoints)
            {
                XRHandJoint parentJoint = XRHandJoints.GetRequiredSourceParent(joint)
                    ?? throw new InvalidOperationException($"Finger joint {joint} unexpectedly has no source parent.");

                bones[writeIndex++] = new DestinationFingerBone(side, joint, parentJoint);
            }
        }

        return bones;
    }

    private static string GetSidePrefix(LimbSide side)
        => side == LimbSide.Left ? "Left" : "Right";

    private readonly record struct DestinationFingerBone(LimbSide Side, XRHandJoint Joint, XRHandJoint ParentJoint);
}
