using AlleyCat.Core.Logging;
using AlleyCat.Rigging;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Player-only finger retargeting modifier that drives the 30 canonical finger bones from tracked optical hand
/// joints while the committed global hand-pose mode is optical (XR-002 TR11-TR16).
/// </summary>
/// <remarks>
/// <para>
/// One modifier covers both hands. It resolves the 30 canonical <c>SkeletonProfileHumanoid</c> finger bones
/// (15 per <c>Left</c>/<c>Right</c>-prefixed side) on the parent <see cref="Skeleton3D" /> and writes
/// <em>rotation-only</em> local poses through <see cref="Skeleton3D.SetBonePoseRotation" />. It never
/// resolves or writes hand, wrist, arm, palm, or fingertip bones, never writes bone positions, bone scales,
/// or global poses — VRIK exclusively owns wrist/hand solving (XR-002 TR13).
/// </para>
/// <para>
/// Writes are gated on the committed global mode (<c>XRManager.Runtime.HandTrackingMode == Optical</c>), not on
/// <see cref="SkeletonModifier3D.Active" />, which remains the normal Godot enable switch (XR-002 TR30-TR31).
/// Controller-mode runtimes without optical support are tolerated by simply never writing.
/// </para>
/// <para>
/// <strong>Constrained anatomical mapping (XR-002 TR14, TR17-TR29).</strong> Every destination first derives
/// <c>S = sourceParentWorld⁻¹ × sourceJointWorld</c> and the neutral child-frame delta
/// <c>Delta = S0⁻¹ × S</c>, normalised and hemisphere-aligned to identity (XR-002 TR20). Each non-thumb
/// destination transfers only the motion the avatar can represent through <see cref="FingerAnatomicalMath" />:
/// intermediate (PIP) and distal (DIP) destinations receive signed hinge flexion about the shared per-hand hinge
/// <c>H</c> — expressed per bone as <c>h_d,j = inverse(Q'_j) × H</c> and written as
/// <c>D_j = N_j × rotation(h_d,j, theta)</c> — while proximal destinations receive flexion plus deliberate
/// spread as one roll-free directional swing <c>D_p = N_p × R_p</c>. The thumb destinations follow the
/// authored-animation Stage 1 model (XR-002 TR24-TR29): binding samples two immutable Blender-authored
/// single-frame pose references — the neutral <c>Reset</c> and the soft-fist <c>Grab-pipe-10</c>,
/// configurable through the exported reference paths — and derives six independent authored axes plus both
/// deterministic Reset-local metacarpal bend/splay frames and both Reset palm planes. The metacarpal receives
/// the anchored hand-frame correspondence transfer (XR-002 TR28.7): <c>d_w = S_meta × (+Y)</c> from the live
/// wrist→metacarpal relation, <c>d_h = C_side × d_w</c> pairing the source wrist axes with the binding palm
/// plane through the measured side-dependent signs, <c>d_0 = Q0 × (N_meta⁻¹ × d_h)</c> anchored on the
/// calibrated neutral, and <c>D_meta = N_meta × rotation(axis, k_eff · angle)</c> with
/// <c>(axis, angle) = shortest_arc(l, d_0)</c> and the pinned per-side swing gain; the proximal/distal each
/// receive their own signed hinge flexion about their authored axis,
/// <c>D = N × rotation(a_j, theta)</c> — never through a rest-geometry-derived destination frame, a shared
/// hinge, or a synthetic tip. The thumb neutrals are the
/// normalised local Reset keys sampled from the immutable neutral reference animation at binding (XR-002
/// TR29) — never TR43's chain-neutral swings and never the imported rest locals, which remain
/// finite binding-qualification inputs — and no thumb tip is fabricated. Source longitudinal roll and
/// off-hinge swing are deliberately discarded
/// everywhere; at Stage 1 the thumb metacarpal's axial opposition roll is deliberately discarded too (Stage 2,
/// Out Of Scope). A delta of identity reproduces the cached effective neutral <c>N_j</c> exactly (XR-002
/// TR23, TR26-TR27). The per-side mapping is: thumb metacarpal/proximal/distal take
/// wrist→thumb-metacarpal, thumb-metacarpal→thumb-proximal, and thumb-proximal→thumb-distal; each non-thumb
/// proximal/intermediate/distal takes its respective metacarpal→proximal, proximal→intermediate, and
/// intermediate→distal relation. The four non-thumb metacarpals (index/middle/ring/little) are
/// <em>source-only reference parents</em>: they anchor proximal rotations but are never written. Profile value
/// S0 is resolved from the profile into fixed buffers on mode entry — together with the per-side metacarpal
/// correspondence records <c>Q0</c>/<c>K_meta</c> (XR-002 TR45); N is cached during binding — from
/// destination rest geometry for the non-thumb destinations and from the sampled Reset keys for the thumbs
/// (XR-002 TR29) — together with the desired globals Q'_j (the authored imported global rests for thumbs),
/// the per-hand frame and local axes are cached for the non-thumb destinations while the thumb records cache
/// their authored axes, metacarpal frame, and correspondence frame. The serialised profile N remains
/// compatibility/provenance metadata and is never composed with the derived N; serialised K is identity-only
/// deprecated compatibility metadata and never enters the mapping. Missing or invalid records freeze only
/// their destinations; they never fall back to an unnormalised direct write.
/// </para>
/// <para>
/// <strong>Binding (XR-002 TR18-TR19, TR25-TR26, TR28, TR43).</strong> At skeleton binding the modifier
/// transactionally derives, per hand, the 12 effective non-thumb neutrals, the desired neutral global
/// orientations, the shared anatomical frame <c>(L, B, H)</c>, the local proximal frames, and the local hinge
/// axes — all from immutable <c>Skeleton3D.GetBoneGlobalRest()</c> geometry — plus the thumb authored-rest
/// rotations validated through the scale-tolerant <see cref="ThumbRestBasisMath" /> extraction as
/// qualification inputs, the thumb neutrals taken from the sampled Reset keys (XR-002 TR29), the six
/// authored axes sampled from the two directly loaded reference resources, both Reset palm planes, and both
/// metacarpal frames and correspondence frames with every gate and the bilateral mirror contract.
/// Publication happens only after BOTH
/// hands pass every neutral, consensus, thumb-binding, authored-axis, and mirror gate; any failure fails the
/// whole binding closed with no runtime writes and no arbitrary-basis, world-axis, animation-derived, or
/// synthetic-tip fallback. The caches are rebuilt only on skeleton rebind; the steady-state per-frame path
/// performs no rest traversal and no allocation.
/// </para>
/// <para>
/// <strong>Rotation frame contract.</strong> <see cref="IXRHandJointProvider.TryGetJoint" /> returns
/// transforms in the frame <c>originGlobal.Scaled(worldScale) * raw</c>, so parent and child joint bases
/// carry the same uniform world scale, which cancels exactly in the parent-relative quotient. Both operands
/// are orthonormalised before the quotient so non-uniform or degenerate provider transforms cannot inject
/// scale or shear. Joint and parent transforms are always fetched in the same processing pass.
/// </para>
/// <para>
/// Validity and freeze semantics (XR-002 TR33-TR37): a destination bone accepts a sample only when BOTH its
/// source joint AND its required source parent joint pass the provider gate —
/// <see cref="IXRHandJointProvider.TryGetJoint" /> returning <see langword="true" /> already enforces
/// finite and orientation-usable (tracked-or-valid) joint and required source-parent, and the modifier
/// re-checks both fetched samples defensively. Accepted samples apply immediately without smoothing, then
/// are hemisphere-stabilised against the cached previously written rotation so provider-side quaternion
/// sign flips cannot produce numerically disjoint writes. Direct source-dependency freeze applies per chain
/// (XR-002 TR29, TR35): an invalid metacarpal freezes only its proximal destination — there is deliberately NO
/// wrist fallback for non-thumb proximals; an invalid proximal freezes its proximal and intermediate
/// destinations; an invalid intermediate freezes its intermediate and distal destinations; an invalid distal
/// freezes its distal destination; unrelated chains stay live. The thumb follows its own ladder
/// (XR-002 TR28): an invalid wrist joint freezes the thumb metacarpal and proximal destinations (their
/// required source parent) without freezing the non-thumb chains; an invalid thumb metacarpal freezes the thumb
/// metacarpal and proximal; an invalid thumb proximal freezes the proximal and distal; an invalid thumb distal
/// freezes the distal only; thumb invalidity leaves the non-thumb chains live and vice versa. Whole-hand
/// tracking loss freezes that hand through the same per-destination ladder because every joint is then invalid,
/// while the other hand continues (XR-002 TR30, TR36). A degenerate anatomical input (XR-002 TR21-TR22, TR26)
/// fails only its own destination the same way. Invalid joints reapply their cached local rotation
/// independently; the cache holds the last written local rotation. Entering optical mode snapshots the current
/// authored local finger rotations as the initial cache — the ONLY role of the authored pose, which no mapping
/// term consumes; a joint whose samples stay invalid keeps that snapshot frozen (XR-002 TR34). Exiting
/// performs no bone writes and clears the cache so the authored pose immediately regains authority without
/// being cleared; skeleton rebinds reset the same state (XR-002 TR34).
/// </para>
/// <para>
/// This modifier is player-template-only topology (CHAR-001 TR49-TR50) and depends on neither hand-interaction state
/// nor authored AnimationTree state; authored animations and AnimationTree parameters are never mutated.
/// </para>
/// </remarks>
[GlobalClass]
public partial class OpticalFingerTrackingModifier : SkeletonModifier3D
{
    /// <summary>
    /// Number of canonical finger destination bones per hand side.
    /// </summary>
    public const int FingerBonesPerSide = 15;

    /// <summary>
    /// Total canonical finger destination bones covered by one modifier (both sides).
    /// </summary>
    public const int FingerBoneCount = FingerBonesPerSide * 2;

    private const int TrackedJointCount = 20;

    // Initialisation order matters: the destination table must exist before the derived name table.
    private static readonly DestinationFingerBone[] _destinationBones = BuildDestinationBones();

    private static readonly string[] _canonicalFingerBoneNames = BuildCanonicalFingerBoneNames();

    /// <summary>
    /// Canonical destination bone names in processing order: the 15 <c>Left</c>-prefixed finger bones followed by the
    /// 15 <c>Right</c>-prefixed finger bones, each side in <see cref="XRHandJoints.DestinationJoints" /> order
    /// (XR-002 TR12).
    /// </summary>
    public static IReadOnlyList<string> CanonicalFingerBoneNames => _canonicalFingerBoneNames;

    /// <summary>Replaceable neutral-normalisation profile for all 30 destinations (XR-002 TR44).</summary>
    [Export]
    public OpticalFingerTrackingCalibrationProfile? CalibrationProfile
    {
        get; set;
    }

    /// <summary>
    /// Project-relative path of the immutable Blender-authored single-frame neutral (Reset) reference
    /// animation whose keys feed the authored thumb axes and the Reset forward kinematics (XR-002 TR25).
    /// General and deliberately non-thumb-prefixed so later increments can reuse it for other fingers.
    /// </summary>
    [Export]
    public string AuthoredNeutralReferenceAnimationPath
    {
        get; set;
    }
        = "res://assets/characters/reference/female/animations/Reset.tres";

    /// <summary>
    /// Project-relative path of the immutable Blender-authored single-frame soft-fist flexion reference
    /// animation whose keys feed the authored thumb axes (XR-002 TR25). The configured resource is an
    /// authoritative binding input and may be changed through this path when the authored reference changes.
    /// </summary>
    [Export]
    public string AuthoredFlexionReferenceAnimationPath
    {
        get; set;
    }
        = "res://assets/characters/reference/female/animations/Grab-pipe-10.tres";

    /// <summary>
    /// Whether an optical retargeting session is currently active for this modifier.
    /// </summary>
    public bool IsOpticalSessionActive
    {
        get;
        private set;
    }

    /// <summary>
    /// Canonical destination bone names that failed to resolve on the currently bound skeleton — empty while the
    /// topology is unvalidated or fully resolved.
    /// </summary>
    public IReadOnlyList<string> MissingFingerBoneNames => _missingFingerBones;

    /// <summary>
    /// Whether all <see cref="FingerBoneCount" /> canonical finger destination bones resolved on the currently
    /// bound skeleton. Retargeting stays disabled until the topology validates (XR-002 TR12).
    /// </summary>
    public bool IsFingerTopologyValid => _topologyValidated && _missingFingerBones.Length == 0 && _restNeutralValid;

    private readonly int[] _boneIndices = new int[FingerBoneCount];

    private readonly Quaternion[] _cachedRotations = new Quaternion[FingerBoneCount];

    private readonly XRHandJointSourceSample[] _jointSamples = new XRHandJointSourceSample[TrackedJointCount];

    private readonly Quaternion[] _sourceRelations = new Quaternion[FingerBonesPerSide];

    private readonly bool[] _sourceRelationValid = new bool[FingerBonesPerSide];

    private readonly ResolvedOpticalFingerCalibration[] _resolvedCalibration =
        new ResolvedOpticalFingerCalibration[OpticalFingerTrackingCalibrationProfile.RecordCount];

    private readonly bool[] _calibrationValid = new bool[OpticalFingerTrackingCalibrationProfile.RecordCount];

    private readonly bool[] _calibrationWarningLogged = new bool[OpticalFingerTrackingCalibrationProfile.RecordCount];

    private readonly Quaternion[] _effectiveDestinationNeutrals =
        new Quaternion[OpticalFingerTrackingCalibrationProfile.RecordCount];

    private readonly Quaternion[] _desiredGlobalOrientations =
        new Quaternion[OpticalFingerTrackingCalibrationProfile.RecordCount];

    private readonly Vector3[] _localHingeAxes =
        new Vector3[OpticalFingerTrackingCalibrationProfile.RecordCount];

    private readonly FingerAnatomicalFrame[] _localProximalFrames =
        new FingerAnatomicalFrame[OpticalFingerTrackingCalibrationProfile.RecordCount];

    private readonly FingerAnatomicalFrame[] _handFrames = new FingerAnatomicalFrame[2];

    // Per-side metacarpal correspondence frames (XR-002 TR28.7): the validated binding palm planes' (u, t,
    // n_palm,H) axes published by the bilateral binding for the runtime anchored hand-frame transfer.
    private readonly AuthoredThumbCorrespondenceFrame[] _thumbCorrespondenceFrames = new AuthoredThumbCorrespondenceFrame[2];

    // Per-side resolved metacarpal correspondence calibration (XR-002 TR45): Q0 anchor + K_meta gain.
    private readonly ResolvedThumbMetacarpalCalibration[] _resolvedMetacarpalCalibration =
        new ResolvedThumbMetacarpalCalibration[2];

    private XRManager? _resolvedXrManager;

    private Skeleton3D? _validatedSkeleton;

    private bool _topologyValidated;

    private bool _restNeutralValid;

    private string _restNeutralError = string.Empty;

    private string[] _missingFingerBones = [];

    /// <summary>
    /// Clears resolved bone indices and the optical-session state when the modifier rebinds to another skeleton.
    /// </summary>
    /// <param name="oldSkeleton">Previously bound skeleton.</param>
    /// <param name="newSkeleton">Newly bound skeleton.</param>
    public override void _SkeletonChanged(Skeleton3D oldSkeleton, Skeleton3D newSkeleton)
    {
        _ = oldSkeleton;
        _ = newSkeleton;

        ResetBindingState();

        if (GameLoggerResolver.TryResolve(out ILogger<OpticalFingerTrackingModifier>? logger) && logger is not null)
        {
            logger.LogDebug("Optical finger tracking modifier rebound to a new skeleton; binding state reset.");
        }
    }

    /// <inheritdoc />
    public override void _ProcessModificationWithDelta(double delta)
    {
        _ = delta;

        Skeleton3D? skeleton = GetSkeleton();
        if (skeleton is null)
        {
            return;
        }

        if (!ReferenceEquals(skeleton, _validatedSkeleton))
        {
            ResetBindingState();
        }

        if (!_topologyValidated)
        {
            ValidateTopology(skeleton);
        }

        if (!IsFingerTopologyValid)
        {
            return;
        }

        IXRRuntime? runtime = ResolveRuntime();
        if (runtime is null || runtime.HandTrackingMode != XRHandTrackingMode.Optical)
        {
            if (IsOpticalSessionActive)
            {
                ExitOpticalSession();
            }

            return;
        }

        if (!IsOpticalSessionActive)
        {
            EnterOpticalSession(skeleton);
        }

        IXRHandJointProvider jointProvider = runtime.OpticalHandJoints;
        ProcessHand(skeleton, jointProvider, LimbSide.Left);
        ProcessHand(skeleton, jointProvider, LimbSide.Right);
    }

    private void ValidateTopology(Skeleton3D skeleton)
    {
        _topologyValidated = true;
        _validatedSkeleton = skeleton;

        List<string>? missingBones = null;
        for (int index = 0; index < FingerBoneCount; index++)
        {
            string boneName = _canonicalFingerBoneNames[index];
            _boneIndices[index] = skeleton.FindBone(boneName);
            if (_boneIndices[index] < 0)
            {
                (missingBones ??= []).Add(boneName);
            }
        }

        _missingFingerBones = missingBones is null ? [] : [.. missingBones];

        if (missingBones is null && TryDeriveRestNeutrals(skeleton, out _restNeutralError))
        {
            _restNeutralValid = true;
            return;
        }

        if (GameLoggerResolver.TryResolve(out ILogger<OpticalFingerTrackingModifier>? logger) && logger is not null)
        {
            if (missingBones is not null)
            {
                logger.LogError(
                    "Optical finger tracking modifier '{ModifierPath}' failed to resolve {MissingBoneCount} canonical " +
                    "finger bones on '{SkeletonPath}': {MissingBones}. Finger retargeting stays disabled for this " +
                    "skeleton.",
                    GetPath(),
                    skeleton.GetPath(),
                    _missingFingerBones.Length,
                    string.Join(", ", _missingFingerBones));
            }
            else
            {
                logger.LogError(
                    "Optical finger tracking modifier '{ModifierPath}' rejected destination rest geometry on " +
                    "'{SkeletonPath}': {RestNeutralError}. Finger retargeting stays disabled for this skeleton.",
                    GetPath(),
                    skeleton.GetPath(),
                    _restNeutralError);
            }
        }
    }

    private void ProcessHand(
        Skeleton3D skeleton,
        IXRHandJointProvider jointProvider,
        LimbSide side)
    {
        FetchTrackedJoints(jointProvider, side);
        RetainSourceRelations(side);

        int sideOffset = (int)side * FingerBonesPerSide;
        for (int fingerIndex = 0; fingerIndex < FingerBonesPerSide; fingerIndex++)
        {
            DestinationFingerBone destination = _destinationBones[sideOffset + fingerIndex];
            int cacheIndex = sideOffset + fingerIndex;

            Quaternion rotation;
            if (_sourceRelationValid[fingerIndex])
            {
                // The parent-relative tracked relation supplies S (XR-002 TR20). Every destination — thumb and
                // non-thumb alike — transfers only the motion the avatar can represent through the constrained
                // anatomical model: signed hinge flexion about the shared per-hand hinge for non-thumb
                // intermediate/distal destinations, one roll-free directional swing for non-thumb proximals,
                // and the authored-animation thumb model — the anchored hand-frame metacarpal correspondence
                // with the pinned Q0 anchor and K_meta swing gain plus independent proximal/distal hinges
                // about the authored axes — for the thumb (XR-002 TR21-TR22, TR26-TR28.7). The provider gate
                // already enforces joint plus required-parent validity; this explicit check is defensive
                // against providers that under-enforce. Non-thumb proximals use their metacarpal parent here,
                // never the wrist (there is no wrist fallback: an invalid metacarpal freezes the destination
                // below).
                Quaternion sourceRelation = _sourceRelations[fingerIndex];

                if (!OpticalFingerTrackingCalibrationProfile.TryGetRecordIndex(
                        side,
                        destination.Joint,
                        out int calibrationIndex))
                {
                    // Unreachable for the canonical destination table; freeze defensively rather than mapping
                    // without a record (XR-002 TR34 — no direct unnormalised write may exist).
                    rotation = _cachedRotations[cacheIndex];
                    skeleton.SetBonePoseRotation(_boneIndices[cacheIndex], rotation);
                    continue;
                }

                if (!_calibrationValid[calibrationIndex])
                {
                    WarnInvalidCalibrationOnce(calibrationIndex, destination);
                    rotation = _cachedRotations[cacheIndex];
                    skeleton.SetBonePoseRotation(_boneIndices[cacheIndex], rotation);
                    continue;
                }

                Quaternion delta = FingerRetargetingMath.DeriveNeutralDelta(
                    sourceRelation,
                    _resolvedCalibration[calibrationIndex].SourceNeutral);

                // The thumb is authored-animation (XR-002 TR26-TR28.7): the metacarpal takes the anchored
                // hand-frame correspondence transfer — the live wrist→metacarpal relation through the binding
                // palm plane's (u, t, n_palm,H) axes, pinned Q0, and profile-scoped directional response —
                // reading S directly so the mapping is S0-drift-invariant by construction, and the thumb
                // proximal/distal take signed hinge flexion about their independent authored axes — cached in
                // the thumb local hinge-axis slots. No rest-geometry-derived destination frame, shared hinge,
                // or synthetic tip exists anywhere in the thumb path.
                bool mapped = destination.Joint == XRHandJoint.ThumbMetacarpal
                    ? FingerAnatomicalMath.TryMapThumbMetacarpalSwing(
                        sourceRelation,
                        _thumbCorrespondenceFrames[(int)side],
                        side,
                        _localProximalFrames[calibrationIndex],
                        _effectiveDestinationNeutrals[calibrationIndex],
                        _resolvedMetacarpalCalibration[(int)side],
                        out rotation)
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

                if (!mapped)
                {
                    // Degenerate anatomical input (XR-002 TR21-TR22, TR26): freeze only this destination at its
                    // cached rotation rather than inventing an axis or falling back to a wrist parent.
                    rotation = _cachedRotations[cacheIndex];
                    skeleton.SetBonePoseRotation(_boneIndices[cacheIndex], rotation);
                    continue;
                }

                // Quaternion double cover: keep the hemisphere of the previously written rotation so
                // provider-side sign flips cannot produce numerically disjoint writes.
                rotation = FingerRetargetingMath.StabiliseRotationHemisphere(rotation, _cachedRotations[cacheIndex]);
                _cachedRotations[cacheIndex] = rotation;
            }
            else
            {
                // Invalid joint or invalid required source parent: reapply this joint's cached local rotation —
                // the last written result — without freezing valid siblings (XR-002 TR28-TR29, TR35).
                rotation = _cachedRotations[cacheIndex];
            }

            skeleton.SetBonePoseRotation(_boneIndices[cacheIndex], rotation);
        }
    }

    /// <summary>
    /// A destination accepts its own provider-gated source sample; the immediate source parent's acceptance
    /// state is never consumed recursively here — an invalid grandparent already propagates through the
    /// provider's direct raw-parent classification, so a child with valid own data and a valid direct parent
    /// continues. The thumb metacarpal and thumb proximal are the sole explicit exceptions: each additionally
    /// requires an accepted wrist sample, so an invalid wrist freezes exactly those two thumb destinations
    /// while a valid thumb distal and every non-thumb chain continue (XR-002 TR28-TR29, TR33, TR36).
    /// </summary>
    private bool IsSampleAccepted(in DestinationFingerBone destination)
        => _jointSamples[(int)destination.Joint].ProductionAccepted
            && (destination.Joint is not (XRHandJoint.ThumbMetacarpal or XRHandJoint.ThumbProximal)
                || _jointSamples[(int)XRHandJoint.Wrist].ProductionAccepted);

    private static bool IsNonThumbProximal(XRHandJoint joint)
        => joint is XRHandJoint.IndexProximal
            or XRHandJoint.MiddleProximal
            or XRHandJoint.RingProximal
            or XRHandJoint.LittleProximal;

    /// <summary>
    /// Swing destinations receive one roll-free directional swing: the non-thumb proximals (XR-002 TR22). The
    /// thumb metacarpal dispatches separately to the anchored hand-frame correspondence transfer with its
    /// pinned anchor and swing gain (XR-002 TR28.7).
    /// </summary>
    private static bool IsSwingDestination(XRHandJoint joint)
        => IsNonThumbProximal(joint);

    private void FetchTrackedJoints(IXRHandJointProvider jointProvider, LimbSide side)
    {
        for (int jointIndex = 0; jointIndex < TrackedJointCount; jointIndex++)
        {
            bool accepted = jointProvider.TryGetJoint(side, (XRHandJoint)jointIndex, out _jointSamples[jointIndex]);
            System.Diagnostics.Debug.Assert(accepted == _jointSamples[jointIndex].ProductionAccepted);
        }
    }

    private void RetainSourceRelations(LimbSide side)
    {
        int sideOffset = (int)side * FingerBonesPerSide;
        for (int fingerIndex = 0; fingerIndex < FingerBonesPerSide; fingerIndex++)
        {
            DestinationFingerBone destination = _destinationBones[sideOffset + fingerIndex];
            bool accepted = IsSampleAccepted(destination);
            _sourceRelationValid[fingerIndex] = accepted;
            _sourceRelations[fingerIndex] = accepted
                ? FingerRetargetingMath.DeriveParentRelativeRotation(
                    _jointSamples[(int)destination.ParentJoint].ProductionWorldTransform.Basis,
                    _jointSamples[(int)destination.Joint].ProductionWorldTransform.Basis)
                : Quaternion.Identity;
        }
    }

    private void EnterOpticalSession(Skeleton3D skeleton)
    {
        // The authored local snapshot is ONLY the initial freeze-cache value: mapping never consumes it, so a
        // joint whose samples stay invalid holds this pose while every valid joint writes tracked data
        // immediately (XR-002 TR34).
        for (int index = 0; index < FingerBoneCount; index++)
        {
            _cachedRotations[index] = skeleton.GetBonePoseRotation(_boneIndices[index]);
        }

        Array.Clear(_resolvedCalibration);
        Array.Clear(_calibrationValid);
        Array.Clear(_calibrationWarningLogged);
        Array.Clear(_resolvedMetacarpalCalibration);
        string validationError = "profile absent";
        bool profileValid = CalibrationProfile?.TryResolve(
            _resolvedCalibration,
            _calibrationValid,
            _resolvedMetacarpalCalibration,
            out validationError) == true;
        if (!profileValid)
        {
            // Fail closed even if a future profile resolver accidentally returns per-record output with an overall
            // failure. No invalid session may consume a partial result or values pinned by an earlier session.
            Array.Clear(_resolvedCalibration);
            Array.Clear(_calibrationValid);
            Array.Clear(_resolvedMetacarpalCalibration);
        }

        IsOpticalSessionActive = true;

        if (GameLoggerResolver.TryResolve(out ILogger<OpticalFingerTrackingModifier>? logger) && logger is not null)
        {
            logger.LogInformation(
                "Optical finger retargeting session entered on '{SkeletonPath}'; authored finger rotations " +
                "snapshotted as the initial freeze cache and calibration profile '{ProfilePath}' resolved with status {ProfileStatus}.",
                skeleton.GetPath(),
                CalibrationProfile?.ResourcePath ?? "<absent>",
                profileValid ? "valid" : validationError);
        }
    }

    private void ExitOpticalSession()
    {
        IsOpticalSessionActive = false;
        Array.Clear(_cachedRotations);
        Array.Clear(_resolvedCalibration);
        Array.Clear(_calibrationValid);
        Array.Clear(_calibrationWarningLogged);
        Array.Clear(_resolvedMetacarpalCalibration);

        if (GameLoggerResolver.TryResolve(out ILogger<OpticalFingerTrackingModifier>? logger) && logger is not null)
        {
            logger.LogInformation(
                "Optical finger retargeting session exited; session cache cleared without bone writes so authored poses regain authority.");
        }
    }

    private void ResetBindingState()
    {
        _validatedSkeleton = null;
        _topologyValidated = false;
        _missingFingerBones = [];
        Array.Fill(_boneIndices, -1);
        Array.Clear(_cachedRotations);
        Array.Clear(_resolvedCalibration);
        Array.Clear(_calibrationValid);
        Array.Clear(_calibrationWarningLogged);
        Array.Clear(_effectiveDestinationNeutrals);
        Array.Clear(_desiredGlobalOrientations);
        Array.Clear(_localHingeAxes);
        Array.Clear(_localProximalFrames);
        Array.Clear(_handFrames);
        Array.Clear(_thumbCorrespondenceFrames);
        Array.Clear(_resolvedMetacarpalCalibration);
        _restNeutralValid = false;
        _restNeutralError = string.Empty;
        IsOpticalSessionActive = false;
    }

    private IXRRuntime? ResolveRuntime()
    {
        if (_resolvedXrManager is null || !IsInstanceValid(_resolvedXrManager))
        {
            _resolvedXrManager = TryResolveXrManager();
        }

        IXRRuntime? runtime = _resolvedXrManager?.Runtime;

        return runtime is not null && IsRuntimeValid(runtime) ? runtime : null;
    }

    private static XRManager? TryResolveXrManager()
    {
        try
        {
            return Game.Instance.GetService<XRManager>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsRuntimeValid(IXRRuntime runtime)
        => runtime is not GodotObject godotObject || IsInstanceValid(godotObject);

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

    private bool TryDeriveRestNeutrals(Skeleton3D skeleton, out string error)
    {
        Span<Quaternion> derivedNeutrals = stackalloc Quaternion[OpticalFingerTrackingCalibrationProfile.RecordCount];
        Span<Quaternion> desiredGlobals = stackalloc Quaternion[OpticalFingerTrackingCalibrationProfile.RecordCount];
        Span<Vector3> localHingeAxes = stackalloc Vector3[OpticalFingerTrackingCalibrationProfile.RecordCount];
        Span<FingerAnatomicalFrame> localProximalFrames =
            stackalloc FingerAnatomicalFrame[OpticalFingerTrackingCalibrationProfile.RecordCount];
        Span<FingerAnatomicalFrame> handFrames = stackalloc FingerAnatomicalFrame[2];
        Span<AuthoredThumbCorrespondenceFrame> correspondenceFrames = stackalloc AuthoredThumbCorrespondenceFrame[2];
        Array.Clear(_effectiveDestinationNeutrals);
        Array.Clear(_desiredGlobalOrientations);
        Array.Clear(_localHingeAxes);
        Array.Clear(_localProximalFrames);
        Array.Clear(_handFrames);
        Array.Clear(_thumbCorrespondenceFrames);

        var handGeometry = new FingerHandRestGeometry[2];
        var thumbGeometry = new ThumbRestGeometry[2];
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            var side = (LimbSide)sideIndex;
            string prefix = GetSidePrefix(side);
            int handBoneIndex = skeleton.FindBone(prefix + "Hand");
            if (handBoneIndex < 0)
            {
                error = $"{prefix}Hand bone is missing.";
                return false;
            }

            var chains = new FingerRestNeutralChain[FingerRestNeutralMath.ChainCount];
            int sideDestinationOffset = sideIndex * FingerBonesPerSide;
            for (int chainIndex = 0; chainIndex < FingerRestNeutralMath.ChainCount; chainIndex++)
            {
                int destinationOffset = sideDestinationOffset + 3 + (chainIndex * FingerRestNeutralMath.BonesPerChain);
                int proximalIndex = _boneIndices[destinationOffset];
                int intermediateIndex = _boneIndices[destinationOffset + 1];
                int distalIndex = _boneIndices[destinationOffset + 2];
                chains[chainIndex] = new FingerRestNeutralChain(
                    proximalIndex,
                    skeleton.GetBoneParent(proximalIndex),
                    skeleton.GetBoneGlobalRest(proximalIndex),
                    intermediateIndex,
                    skeleton.GetBoneParent(intermediateIndex),
                    skeleton.GetBoneGlobalRest(intermediateIndex),
                    distalIndex,
                    skeleton.GetBoneParent(distalIndex),
                    skeleton.GetBoneGlobalRest(distalIndex));
            }

            handGeometry[sideIndex] = new FingerHandRestGeometry(
                handBoneIndex,
                skeleton.GetBoneGlobalRest(handBoneIndex),
                chains);

            // The thumb chain occupies the first three per-side destination slots (XR-002 TR25): topology and
            // the scale-tolerant rest-basis extraction are validated alongside the authored-reference-derived
            // axes and metacarpal frame — never segment-centre geometry.
            int thumbMetacarpalIndex = _boneIndices[sideDestinationOffset];
            int thumbProximalIndex = _boneIndices[sideDestinationOffset + 1];
            int thumbDistalIndex = _boneIndices[sideDestinationOffset + 2];
            thumbGeometry[sideIndex] = new ThumbRestGeometry(
                thumbMetacarpalIndex,
                skeleton.GetBoneParent(thumbMetacarpalIndex),
                skeleton.GetBoneGlobalRest(thumbMetacarpalIndex),
                skeleton.GetBoneRest(thumbMetacarpalIndex).Basis,
                thumbProximalIndex,
                skeleton.GetBoneParent(thumbProximalIndex),
                skeleton.GetBoneGlobalRest(thumbProximalIndex),
                skeleton.GetBoneRest(thumbProximalIndex).Basis,
                thumbDistalIndex,
                skeleton.GetBoneParent(thumbDistalIndex),
                skeleton.GetBoneGlobalRest(thumbDistalIndex),
                skeleton.GetBoneRest(thumbDistalIndex).Basis);
        }

        // Authored-reference sampling (XR-002 TR25, TR28.2): both immutable single-frame pose resources load
        // directly and every required key plus the Reset forward kinematics is read without registering on a
        // live animation node, mutating a resource, or reading live pose state.
        if (!AuthoredThumbReferenceSampler.TrySample(
                AuthoredNeutralReferenceAnimationPath,
                AuthoredFlexionReferenceAnimationPath,
                skeleton,
                out AuthoredThumbSideReferences leftReferences,
                out AuthoredThumbSideReferences rightReferences,
                out error))
        {
            return false;
        }

        // Transactional bilateral publication (XR-002 TR18, TR25-TR26, TR28): the effective neutrals, desired
        // globals, per-hand frames, local axes, six authored thumb axes, both metacarpal frames, and both
        // metacarpal correspondence frames become usable only after BOTH hands pass every neutral, consensus,
        // thumb-binding, authored-axis, and bilateral-mirror gate.
        if (!FingerAnatomicalMath.TryDeriveBilateralBinding(
                handGeometry[0],
                handGeometry[1],
                thumbGeometry[0],
                thumbGeometry[1],
                leftReferences,
                rightReferences,
                derivedNeutrals,
                desiredGlobals,
                localHingeAxes,
                localProximalFrames,
                handFrames,
                correspondenceFrames,
                out error))
        {
            return false;
        }

        derivedNeutrals.CopyTo(_effectiveDestinationNeutrals);
        desiredGlobals.CopyTo(_desiredGlobalOrientations);
        localHingeAxes.CopyTo(_localHingeAxes);
        localProximalFrames.CopyTo(_localProximalFrames);
        handFrames.CopyTo(_handFrames);
        correspondenceFrames.CopyTo(_thumbCorrespondenceFrames);
        error = string.Empty;
        return true;
    }

    private readonly record struct DestinationFingerBone(LimbSide Side, XRHandJoint Joint, XRHandJoint ParentJoint);

    private void WarnInvalidCalibrationOnce(int calibrationIndex, DestinationFingerBone destination)
    {
        if (_calibrationWarningLogged[calibrationIndex])
        {
            return;
        }

        _calibrationWarningLogged[calibrationIndex] = true;
        if (GameLoggerResolver.TryResolve(out ILogger<OpticalFingerTrackingModifier>? logger) && logger is not null)
        {
            logger.LogWarning(
                "Optical finger destination {Side}/{Joint} has no valid pinned calibration record; retaining its session freeze cache instead of applying direct mapping.",
                destination.Side,
                destination.Joint);
        }
    }
}
