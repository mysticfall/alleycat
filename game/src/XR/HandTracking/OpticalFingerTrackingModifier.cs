using AlleyCat.Core.Logging;
using AlleyCat.Interaction;
using AlleyCat.Interaction.Hands;
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
/// <strong>Constrained anatomical mapping (XR-002 TR14, TR17-TR29) — through the shared projection seam.</strong>
/// The modifier delegates its entire per-destination mapping to <see cref="OpticalFingerProjectionBinding" />,
/// the single source of truth that turns raw joint samples into the 15 destination-local finger rotations of a
/// side — the same seam the candidate-aware optical grab recogniser consumes for recognition, so the skeleton
/// writes and the recognition predictions can never diverge (XR-002 TR49, CTRL-002 TR13). The binding derives,
/// per accepted destination, <c>S = sourceParentWorld⁻¹ × sourceJointWorld</c> and the neutral child-frame delta
/// <c>Delta = S0⁻¹ × S</c>, normalised and hemisphere-aligned to identity (XR-002 TR20), then transfers only the
/// motion the avatar can represent through <see cref="FingerAnatomicalMath" />: intermediate (PIP) and distal
/// (DIP) destinations receive signed hinge flexion about the shared per-hand hinge <c>H</c> — expressed per bone
/// as <c>h_d,j = inverse(Q'_j) × H</c> and written as <c>D_j = N_j × rotation(h_d,j, theta)</c> — while proximal
/// destinations receive flexion plus deliberate spread as one roll-free directional swing
/// <c>D_p = N_p × R_p</c>. The thumb destinations follow the authored-animation Stage 1 model (XR-002
/// TR24-TR29): binding samples two immutable Blender-authored single-frame pose references — the neutral
/// <c>Reset</c> and the soft-fist <c>Grab-pipe-10</c>, configurable through the exported reference paths — and
/// derives six independent authored axes plus both deterministic Reset-local metacarpal bend/splay frames and
/// both Reset palm planes. The metacarpal receives the anchored hand-frame correspondence transfer (XR-002
/// TR28.7): <c>d_w = S_meta × (+Y)</c> from the live wrist→metacarpal relation, <c>d_h = C_side × d_w</c>
/// pairing the source wrist axes with the binding palm plane through the measured side-dependent signs,
/// <c>d_0 = Q0 × (N_meta⁻¹ × d_h)</c> anchored on the calibrated neutral, and
/// <c>D_meta = N_meta × rotation(axis, k_eff · angle)</c> with <c>(axis, angle) = shortest_arc(l, d_0)</c> and
/// the pinned per-side swing gain; the proximal/distal each receive their own signed hinge flexion about their
/// authored axis, <c>D = N × rotation(a_j, theta)</c> — never through a rest-geometry-derived destination
/// frame, a shared hinge, or a synthetic tip. The thumb neutrals are the
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
/// S0 is resolved from the profile into the binding's calibration state on mode entry — together with the
/// per-side metacarpal correspondence records <c>Q0</c>/<c>K_meta</c> (XR-002 TR45); the effective N is staged
/// into the binding during skeleton binding — from destination rest geometry for the non-thumb destinations and
/// from the sampled Reset keys for the thumbs (XR-002 TR29) — together with the per-bone local hinge axes,
/// local proximal frames, thumb metacarpal frame, and correspondence frames. The serialised profile N remains
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
/// synthetic-tip fallback. Successful publication stages the neutrals, local axes, and correspondence frames
/// into the shared <see cref="OpticalFingerProjectionBinding" />; the caches are rebuilt only on skeleton
/// rebind, and the steady-state per-frame path performs no rest traversal and no allocation.
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
/// <strong>Per-hand grab-pose arbitration (XR-002 UR16, TR30-TR31; INTR-003 TR19-TR21; INTR-002 R60-61).</strong>
/// Before writing each hand the modifier queries <c>XRManager.OpticalGrabArbiter</c> — the single authoritative
/// arbitration seam <c>HandPoseBehaviour</c> publishes after optical pending selection, at commit, and clears on
/// every exit path. While a grab is pending, the modifier blends that hand toward the candidate reference over
/// <see cref="GrabPoseBlendDurationSeconds" /> and continues writing the settled reference. When arbitration flips
/// to held, the modifier keeps writing that hand for
/// <see cref="GrabPoseBlendDurationSeconds" /> (default 0.2 s, coordinated with
/// <c>HandPoseController.TransitionDuration</c> so the AnimationTree's <c>blend_amount</c> reaches 1 by window
/// end), slerping per finger from the last-written tracked pose — cached per side, the values currently on the
/// bones — to the sampled authored grab reference. The window's final write equals the authored reference
/// exactly, so the handoff to the AnimationTree is seamless; afterwards the modifier performs zero writes for
/// that hand while the grab stays held (XR-002 TR31; OG11) and raw optical sampling stays hidden — the
/// release recogniser fetches its own provider samples. When arbitration clears while the session stays
/// active, writing resumes from the hand's current (authored) pose, slerping to the live projected tracked
/// pose over the same window, then normal tracking continues. Tracking loss mid-blend-out freezes at the
/// current blend value and pauses the blend clock until samples recover; blend state resets on session exit
/// and on arbitration clear, and the opposite hand keeps its normal tracking writes throughout. These
/// transition-window writes are the sanctioned implementation of XR-002 UR16's seamless-transition
/// requirement; outside the window there are no writes for a committed hand.
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
    public const int FingerBonesPerSide = OpticalFingerProjectionBinding.FingerBonesPerSide;

    /// <summary>
    /// Total canonical finger destination bones covered by one modifier (both sides).
    /// </summary>
    public const int FingerBoneCount = OpticalFingerProjectionBinding.FingerBoneCount;

    private const int TrackedJointCount = 20;

    private static readonly OpticalGrabHandPresentation _defaultUnheldPresentation =
        new(OpticalGrabPresentationState.Tracking, GrabReference: null);

    /// <summary>
    /// Canonical destination bone names in processing order: the 15 <c>Left</c>-prefixed finger bones followed by the
    /// 15 <c>Right</c>-prefixed finger bones, each side in <see cref="XRHandJoints.DestinationJoints" /> order
    /// (XR-002 TR12).
    /// </summary>
    public static IReadOnlyList<string> CanonicalFingerBoneNames
        => OpticalFingerProjectionBinding.CanonicalFingerBoneNames;

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
        get;
        set;
    }
        = "res://assets/characters/reference/female/animations/Grab-pipe-10.tres";

    /// <summary>
    /// Duration in seconds of the per-hand optical-grab pose blend window (XR-002 UR16; INTR-002 R60-61):
    /// how long the modifier keeps writing a hand after arbitration flips to held (last-written tracked pose
    /// → sampled authored reference) and after arbitration clears (authored pose → live tracked projection)
    /// before zero-write suppression or normal tracking takes over. The default 0.2 s is coordinated with
    /// <c>HandPoseController.TransitionDuration</c> (also 0.2 s) so the AnimationTree's <c>blend_amount</c>
    /// reaches its settled value when the window completes and the modifier's final write already equals the
    /// authored reference — a seamless handoff in both directions.
    /// </summary>
    [ExportGroup("Optical Grab Arbitration")]
    [Export]
    public float GrabPoseBlendDurationSeconds
    {
        get;
        set;
    } = 0.2f;

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

    /// <summary>
    /// Projects one side's raw joint samples through this modifier's shared calibrated anatomical projection
    /// binding (XR-002 TR20-TR29) — the same single source of truth this modifier's skeleton writes flow
    /// through, exposed read-only so the mode-aware optical grab recogniser's predictions can never diverge
    /// from the written poses (XR-002 TR49; CTRL-002 TR13). Binding and calibration state stay owned by this
    /// modifier's skeleton-binding and optical-session lifecycle; consumers cannot stage or clear them.
    /// </summary>
    /// <param name="side">Hand side to project.</param>
    /// <param name="jointSamples">Raw per-side joint samples indexed by <see cref="XRHandJoint" /> (20 entries).</param>
    /// <param name="destinationPoses">Output span of 15 poses in canonical destination order.</param>
    /// <returns>
    /// <see langword="true" /> when the projection ran; <see langword="false" /> — leaving the span untouched —
    /// when the binding or calibration is not staged (fail closed).
    /// </returns>
    public bool TryProjectOpticalFingers(
        LimbSide side,
        ReadOnlySpan<XRHandJointSourceSample> jointSamples,
        Span<OpticalFingerProjectedPose> destinationPoses)
        => _projection.TryProject(side, jointSamples, destinationPoses);

    /// <summary>
    /// Copies one side's 15 effective neutrals from the shared projection binding — the profile-derivation
    /// inputs of the animation-derived grip references, in canonical destination order (XR-002 TR48).
    /// </summary>
    public bool TryCopyOpticalFingerEffectiveNeutrals(LimbSide side, Span<Quaternion> destination)
        => _projection.TryCopySideEffectiveNeutrals(side, destination);

    /// <summary>Identity of this modifier-owned projection binding for recognition cache keys.</summary>
    public ulong OpticalFingerProjectionBindingIdentity => GetInstanceId();

    /// <summary>Monotonic projection binding generation for recognition cache invalidation.</summary>
    public long OpticalFingerProjectionBindingGeneration => _projection.Generation;

    /// <summary>
    /// Current per-hand optical-grab pose-arbitration blend phase of this modifier (XR-002 UR16, TR30-TR31) —
    /// diagnostic and test observation of the commit-blend/held-suppressed/release-blend/tracking lifecycle.
    /// </summary>
    /// <param name="side">Hand side to query.</param>
    internal OpticalGrabPoseBlendPhase GetGrabPoseBlendPhase(LimbSide side)
        => _grabPoseBlends[(int)side].Phase;

    private readonly int[] _boneIndices = new int[FingerBoneCount];

    private readonly Quaternion[] _cachedRotations = new Quaternion[FingerBoneCount];

    private readonly XRHandJointSourceSample[] _jointSamples = new XRHandJointSourceSample[TrackedJointCount];

    private readonly OpticalFingerProjectedPose[] _projectedPoses = new OpticalFingerProjectedPose[FingerBonesPerSide];

    private readonly bool[] _calibrationWarningLogged = new bool[OpticalFingerTrackingCalibrationProfile.RecordCount];

    // Shared calibrated anatomical projection seam (XR-002 TR20-TR29): the single source of truth for the
    // per-destination mapping this modifier writes and the optical grab recogniser predicts with.
    private readonly OpticalFingerProjectionBinding _projection = new();

    // Per-hand optical-grab pose-arbitration blend state (XR-002 UR16, TR30-TR31): pending assistance, commit
    // handoff, zero-write suppression while held, and return-to-tracking blend-out.
    private readonly OpticalGrabPoseBlendState[] _grabPoseBlends = [new(), new()];

    private readonly OpticalGrabPresentationState[] _arbitrationStates = new OpticalGrabPresentationState[2];

    private readonly Animation?[] _arbitrationAnimations = new Animation?[2];

    private readonly OpticalGrabPresentationOwner?[] _arbitrationOwners = new OpticalGrabPresentationOwner?[2];

    private readonly bool[] _releaseBlendClockFrozen = new bool[2];

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
        float blendDeltaSeconds = (float)Math.Max(0.0, delta);
        ProcessHand(skeleton, jointProvider, LimbSide.Left, blendDeltaSeconds);
        ProcessHand(skeleton, jointProvider, LimbSide.Right, blendDeltaSeconds);
    }

    private void ValidateTopology(Skeleton3D skeleton)
    {
        _topologyValidated = true;
        _validatedSkeleton = skeleton;

        List<string>? missingBones = null;
        IReadOnlyList<string> canonicalBoneNames = OpticalFingerProjectionBinding.CanonicalFingerBoneNames;
        for (int index = 0; index < FingerBoneCount; index++)
        {
            string boneName = canonicalBoneNames[index];
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
        LimbSide side,
        float deltaSeconds)
    {
        int sideIndex = (int)side;
        OpticalGrabPoseBlendState poseBlend = _grabPoseBlends[sideIndex];

        ApplyArbitrationTransitions(side, poseBlend);

        if (poseBlend.IsWriteSuppressed)
        {
            // XR-002 TR31; OG11: while the grab stays committed the authored animation owns this hand's finger
            // presentation and the modifier writes nothing. Raw optical sampling stays hidden — the release
            // recogniser fetches its own provider samples — so there is nothing to do for this hand.
            return;
        }

        FetchTrackedJoints(jointProvider, side);

        // The shared calibrated anatomical projection produces the exact rotations this modifier writes —
        // through the same seam the optical grab recogniser predicts with, so the write path and the
        // recognition path can never diverge (XR-002 TR20-TR29, TR49). An unstaged binding or calibration is
        // unreachable here because topology validation and session entry gate this call; fail closed anyway.
        if (!_projection.TryProject(side, _jointSamples, _projectedPoses))
        {
            return;
        }

        int sideOffset = sideIndex * FingerBonesPerSide;

        if (poseBlend.Phase == OpticalGrabPoseBlendPhase.CommitBlend)
        {
            // The timer is only a minimum: retain authored reference writes until the legitimate AnimationTree owner
            // confirms this publisher generation/reference has evaluated at its intended effective weight.
            _ = poseBlend.Advance(deltaSeconds, clockAdvances: true);
            WriteCommitBlendWindow(skeleton, poseBlend, sideOffset);
            if (poseBlend.IsCommitBlendMinimumSatisfied && TrySuppressCommitBlendWhenOwnerReady(side, poseBlend))
            {
                LogArbitrationTransition(
                    "Optical grab commit pose handoff is ready for {Side}; modifier writes suspended while the grab stays held.",
                    side);
            }

            return;
        }

        if (poseBlend.Phase == OpticalGrabPoseBlendPhase.PendingAssistance)
        {
            bool assistanceCompleted = poseBlend.Advance(deltaSeconds, clockAdvances: true);
            WritePendingAssistanceWindow(skeleton, poseBlend, sideOffset);
            if (assistanceCompleted)
            {
                LogArbitrationTransition(
                    "Optical pending grab assistance settled for {Side}; the candidate reference remains modifier-owned until commit or cancellation.",
                    side);
            }

            return;
        }

        if (poseBlend.Phase == OpticalGrabPoseBlendPhase.ReleaseBlend)
        {
            WriteReleaseBlendWindow(skeleton, poseBlend, side, sideOffset, deltaSeconds);
            return;
        }

        for (int fingerIndex = 0; fingerIndex < FingerBonesPerSide; fingerIndex++)
        {
            int cacheIndex = sideOffset + fingerIndex;
            OpticalFingerProjectedPose pose = _projectedPoses[fingerIndex];

            Quaternion rotation;
            if (pose.IsValid)
            {
                // Quaternion double cover: keep the hemisphere of the previously written rotation so
                // provider-side sign flips cannot produce numerically disjoint writes.
                rotation = FingerRetargetingMath.StabiliseRotationHemisphere(pose.Rotation, _cachedRotations[cacheIndex]);
                _cachedRotations[cacheIndex] = rotation;
            }
            else
            {
                // Invalid joint or invalid required source parent, an invalid calibration record, or a
                // degenerate anatomical input: reapply this joint's cached local rotation — the last written
                // result — without freezing valid siblings (XR-002 TR21-TR22, TR26, TR28-TR29, TR34-TR35).
                WarnFrozenDestinationOnce(side, fingerIndex, pose.Status);
                rotation = _cachedRotations[cacheIndex];
            }

            skeleton.SetBonePoseRotation(_boneIndices[cacheIndex], rotation);
        }
    }

    /// <summary>
    /// Detects per-hand presentation edges against <c>XRManager.OpticalGrabArbiter</c>. Pending assistance begins
    /// the tracked-to-reference blend, held begins the seamless handoff window, and tracking begins the return
    /// blend, so the modifier reacts to the exact frame <c>HandPoseBehaviour</c> publishes each transition.
    /// </summary>
    private void ApplyArbitrationTransitions(LimbSide side, OpticalGrabPoseBlendState poseBlend)
    {
        int sideIndex = (int)side;
        OpticalGrabHandPresentation presentation =
            _resolvedXrManager?.OpticalGrabArbiter.GetPresentation(side) ?? _defaultUnheldPresentation;
        if (presentation.State == _arbitrationStates[sideIndex]
            && ReferenceEquals(presentation.GrabReference?.Animation, _arbitrationAnimations[sideIndex])
            && presentation.Owner == _arbitrationOwners[sideIndex])
        {
            return;
        }

        _arbitrationStates[sideIndex] = presentation.State;
        _arbitrationAnimations[sideIndex] = presentation.GrabReference?.Animation;
        _arbitrationOwners[sideIndex] = presentation.Owner;
        switch (presentation.State)
        {
            case OpticalGrabPresentationState.PendingAssistance:
                BeginPendingAssistance(side, poseBlend, presentation);
                break;

            case OpticalGrabPresentationState.Held:
                BeginCommitBlend(side, poseBlend, presentation);
                break;

            case OpticalGrabPresentationState.Tracking:
            default:
                BeginReleaseBlend(side, poseBlend);
                break;
        }
    }

    private void BeginPendingAssistance(
        LimbSide side,
        OpticalGrabPoseBlendState poseBlend,
        OpticalGrabHandPresentation presentation)
    {
        if (!TrySampleAuthoredReference(side, presentation, out AuthoredHandPoseSideReference reference, out string sampleError))
        {
            // A pending approach has no AnimationTree grab pose to fall back to. Retaining live optical tracking is
            // safer than incorrectly suppressing its finger writes.
            poseBlend.ResetToTracking();
            LogAuthoredReferenceSamplingFailure(side, sampleError, pending: true);
            return;
        }

        int sideOffset = (int)side * FingerBonesPerSide;
        poseBlend.BeginPendingAssistance(
            _cachedRotations.AsSpan(sideOffset, FingerBonesPerSide),
            reference.Poses,
            GrabPoseBlendDurationSeconds);
        LogArbitrationTransition(
            "Optical pending grab assistance started for {Side}: blending tracked fingers toward the candidate reference over {BlendDurationSeconds:F3}s.",
            side,
            GrabPoseBlendDurationSeconds);
    }

    /// <summary>
    /// Begins the commit blend-in: the blend starts from the last pose this modifier actually wrote — the
    /// values currently on the bones — and targets the held grab's sampled authored reference (XR-002 UR16,
    /// TR31). A grab whose reference cannot be sampled retains live tracking rather than relinquishing writes
    /// without an exact controller readiness publication.
    /// </summary>
    private void BeginCommitBlend(
        LimbSide side,
        OpticalGrabPoseBlendState poseBlend,
        OpticalGrabHandPresentation presentation)
    {
        if (!TrySampleAuthoredReference(side, presentation, out AuthoredHandPoseSideReference reference, out string sampleError))
        {
            poseBlend.ResetToTracking();
            LogAuthoredReferenceSamplingFailure(side, sampleError, pending: false);
            return;
        }

        int sideOffset = (int)side * FingerBonesPerSide;
        Span<Quaternion> fromLastWritten = stackalloc Quaternion[FingerBonesPerSide];
        _cachedRotations.AsSpan(sideOffset, FingerBonesPerSide).CopyTo(fromLastWritten);
        poseBlend.BeginCommitBlend(fromLastWritten, reference.Poses, GrabPoseBlendDurationSeconds);

        if (GameLoggerResolver.TryResolve(out ILogger<OpticalFingerTrackingModifier>? transitionLogger)
            && transitionLogger is not null)
        {
            transitionLogger.LogInformation(
                "Optical grab commit pose blend started for {Side}: blending tracked fingers toward the authored " +
                    "reference '{AnimationName}' over {BlendDurationSeconds:F3}s before writes suspend.",
                side,
                presentation.GrabReference!.Animation.ResourceName,
                GrabPoseBlendDurationSeconds);
        }
    }

    private static bool TrySampleAuthoredReference(
        LimbSide side,
        OpticalGrabHandPresentation presentation,
        out AuthoredHandPoseSideReference reference,
        out string sampleError)
    {
        reference = null!;
        GrabPoseReference? grabReference = presentation.GrabReference;
        sampleError = presentation.State == OpticalGrabPresentationState.PendingAssistance
            ? "the pending grab published no authored candidate pose animation."
            : "the held grab published no authored grab pose animation.";
        if (grabReference is null)
        {
            return false;
        }

        if (grabReference.Side != side)
        {
            sampleError = $"the published candidate reference belongs to {grabReference.Side}, not {side}.";
            return false;
        }

        reference = grabReference.SampledReference;
        return true;
    }

    private static void LogAuthoredReferenceSamplingFailure(LimbSide side, string sampleError, bool pending)
    {
        if (GameLoggerResolver.TryResolve(out ILogger<OpticalFingerTrackingModifier>? logger) && logger is not null)
        {
            logger.LogError(
                "Optical {PresentationState} grab arbitration on {Side} could not sample its authored reference ({SampleError}); {Fallback}.",
                pending ? "pending" : "held",
                side,
                sampleError,
                "retaining live tracking");
        }
    }

    private bool TrySuppressCommitBlendWhenOwnerReady(LimbSide side, OpticalGrabPoseBlendState poseBlend)
    {
        OpticalGrabHandPresentation presentation =
            _resolvedXrManager?.OpticalGrabArbiter.GetPresentation(side) ?? _defaultUnheldPresentation;
        return presentation.State == OpticalGrabPresentationState.Held
            && presentation.Owner is OpticalGrabPresentationOwner publisher
            && presentation.GrabReference is GrabPoseReference reference
            && HandPoseController.TryGetOpticalGrabHandoffReadiness(publisher, out HandPoseHandoffReadiness readiness)
            && readiness.PublisherGeneration == publisher.Generation
            && ReferenceEquals(readiness.Reference, reference.Animation)
            && float.IsFinite(readiness.IntendedEffectiveWeight)
            && readiness.IsEvaluatedReady
            && poseBlend.TrySuppressCommitBlend();
    }

    /// <summary>
    /// Begins the release blend-out from the hand's current effective pose — the authored reference once
    /// suppressed, or the mid-window commit value when the grab released before the commit blend completed —
    /// toward the live projected tracked pose (XR-002 UR16; INTR-002 R61). A clear on an already-tracking
    /// hand just resets the blend bookkeeping.
    /// </summary>
    private void BeginReleaseBlend(LimbSide side, OpticalGrabPoseBlendState poseBlend)
    {
        Span<Quaternion> fromCurrentPose = stackalloc Quaternion[FingerBonesPerSide];
        if (poseBlend.Phase == OpticalGrabPoseBlendPhase.HeldSuppressed && poseBlend.HasAuthoredReference)
        {
            poseBlend.AuthoredReferencePoses.CopyTo(fromCurrentPose);
        }
        else if (poseBlend.Phase == OpticalGrabPoseBlendPhase.CommitBlend)
        {
            for (int fingerIndex = 0; fingerIndex < FingerBonesPerSide; fingerIndex++)
            {
                fromCurrentPose[fingerIndex] = poseBlend.EvaluateCommitBlend(fingerIndex);
            }
        }
        else if (poseBlend.Phase == OpticalGrabPoseBlendPhase.PendingAssistance)
        {
            for (int fingerIndex = 0; fingerIndex < FingerBonesPerSide; fingerIndex++)
            {
                fromCurrentPose[fingerIndex] = poseBlend.EvaluatePendingAssistance(fingerIndex);
            }
        }
        else
        {
            // Already tracking or releasing, or suppressed without a sampled reference: normal tracking
            // writes resume directly and the AnimationTree's own blend-down remains the visible transition.
            poseBlend.ResetToTracking();
            return;
        }

        _releaseBlendClockFrozen[(int)side] = false;
        poseBlend.BeginReleaseBlend(fromCurrentPose, GrabPoseBlendDurationSeconds);

        if (GameLoggerResolver.TryResolve(out ILogger<OpticalFingerTrackingModifier>? logger) && logger is not null)
        {
            logger.LogInformation(
                "Optical grab release pose blend started for {Side}: blending the authored pose back toward " +
                    "live tracked fingers over {BlendDurationSeconds:F3}s.",
                side,
                GrabPoseBlendDurationSeconds);
        }
    }

    /// <summary>
    /// Writes one commit-blend frame: per finger, the slerp from the last-written tracked pose toward the
    /// hemisphere-aligned authored reference at the current progress (XR-002 UR16). The window's final write
    /// equals the authored reference exactly, handing off seamlessly to the AnimationTree.
    /// </summary>
    private void WriteCommitBlendWindow(Skeleton3D skeleton, OpticalGrabPoseBlendState poseBlend, int sideOffset)
    {
        for (int fingerIndex = 0; fingerIndex < FingerBonesPerSide; fingerIndex++)
        {
            int cacheIndex = sideOffset + fingerIndex;
            Quaternion rotation = poseBlend.EvaluateCommitBlend(fingerIndex);
            _cachedRotations[cacheIndex] = rotation;
            skeleton.SetBonePoseRotation(_boneIndices[cacheIndex], rotation);
        }
    }

    private void WritePendingAssistanceWindow(Skeleton3D skeleton, OpticalGrabPoseBlendState poseBlend, int sideOffset)
    {
        for (int fingerIndex = 0; fingerIndex < FingerBonesPerSide; fingerIndex++)
        {
            int cacheIndex = sideOffset + fingerIndex;
            Quaternion rotation = poseBlend.EvaluatePendingAssistance(fingerIndex);
            _cachedRotations[cacheIndex] = rotation;
            skeleton.SetBonePoseRotation(_boneIndices[cacheIndex], rotation);
        }
    }

    /// <summary>
    /// Writes one release-blend frame: per finger, the slerp from the current-pose capture toward the live
    /// projected tracked rotation. The clock advances before the write so the completing frame writes the
    /// exact live target, handing off seamlessly to normal tracking. Invalid destinations freeze at their
    /// last written blend value; whole-hand tracking loss additionally freezes the blend clock until samples
    /// recover (XR-002 TR36 mirrored).
    /// </summary>
    private void WriteReleaseBlendWindow(
        Skeleton3D skeleton,
        OpticalGrabPoseBlendState poseBlend,
        LimbSide side,
        int sideOffset,
        float deltaSeconds)
    {
        bool anyValidSample = false;
        for (int fingerIndex = 0; fingerIndex < FingerBonesPerSide; fingerIndex++)
        {
            anyValidSample |= _projectedPoses[fingerIndex].IsValid;
        }

        bool releaseCompleted = poseBlend.Advance(deltaSeconds, anyValidSample);

        for (int fingerIndex = 0; fingerIndex < FingerBonesPerSide; fingerIndex++)
        {
            int cacheIndex = sideOffset + fingerIndex;
            OpticalFingerProjectedPose pose = _projectedPoses[fingerIndex];

            Quaternion rotation;
            if (pose.IsValid)
            {
                rotation = poseBlend.EvaluateReleaseBlend(fingerIndex, pose.Rotation);
                _cachedRotations[cacheIndex] = rotation;
            }
            else
            {
                WarnFrozenDestinationOnce(side, fingerIndex, pose.Status);
                rotation = _cachedRotations[cacheIndex];
            }

            skeleton.SetBonePoseRotation(_boneIndices[cacheIndex], rotation);
        }

        UpdateReleaseBlendClockLogging(side, anyValidSample, releaseCompleted);
    }

    /// <summary>
    /// Logs the freeze and resume of the release-blend clock once per episode and the window's completion
    /// (XR-002 UR16; TR36 freeze semantics mirrored for the blend-out).
    /// </summary>
    private void UpdateReleaseBlendClockLogging(
        LimbSide side,
        bool anyValidSample,
        bool releaseCompleted)
    {
        int sideIndex = (int)side;
        if (!anyValidSample)
        {
            if (!_releaseBlendClockFrozen[sideIndex])
            {
                _releaseBlendClockFrozen[sideIndex] = true;
                LogArbitrationTransition(
                    "Optical grab release pose blend frozen mid-window for {Side}; tracking loss holds the " +
                        "current blend value until samples recover.",
                    side);
            }

            return;
        }

        if (_releaseBlendClockFrozen[sideIndex])
        {
            _releaseBlendClockFrozen[sideIndex] = false;
            LogArbitrationTransition(
                "Optical grab release pose blend resumed for {Side}; blending continues toward live tracked fingers.",
                side);
        }

        if (releaseCompleted)
        {
            LogArbitrationTransition(
                "Optical grab release pose blend completed for {Side}; normal tracking writes resumed.",
                side);
        }
    }

    private static void LogArbitrationTransition(string message, LimbSide side)
    {
        if (GameLoggerResolver.TryResolve(out ILogger<OpticalFingerTrackingModifier>? logger) && logger is not null)
        {
            logger.LogInformation(message, side);
        }
    }

    private static void LogArbitrationTransition(string message, LimbSide side, float durationSeconds)
    {
        if (GameLoggerResolver.TryResolve(out ILogger<OpticalFingerTrackingModifier>? logger) && logger is not null)
        {
            logger.LogInformation(message, side, durationSeconds);
        }
    }

    private void FetchTrackedJoints(IXRHandJointProvider jointProvider, LimbSide side)
    {
        for (int jointIndex = 0; jointIndex < TrackedJointCount; jointIndex++)
        {
            bool accepted = jointProvider.TryGetJoint(side, (XRHandJoint)jointIndex, out _jointSamples[jointIndex]);
            System.Diagnostics.Debug.Assert(accepted == _jointSamples[jointIndex].ProductionAccepted);
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

        Array.Clear(_calibrationWarningLogged);
        const int recordCount = OpticalFingerTrackingCalibrationProfile.RecordCount;
        var resolvedCalibration = new ResolvedOpticalFingerCalibration[recordCount];
        bool[] calibrationValid = new bool[recordCount];
        var metacarpalCalibration = new ResolvedThumbMetacarpalCalibration[2];
        string validationError = "profile absent";
        bool profileValid = CalibrationProfile?.TryResolve(
            resolvedCalibration,
            calibrationValid,
            metacarpalCalibration,
            out validationError) == true;
        if (!profileValid)
        {
            // Fail closed even if a future profile resolver accidentally returns per-record output with an overall
            // failure. No invalid session may consume a partial result or values pinned by an earlier session;
            // the all-invalid staged calibration keeps every destination on its freeze fallback (XR-002 TR34).
            Array.Clear(resolvedCalibration);
            Array.Clear(calibrationValid);
            Array.Clear(metacarpalCalibration);
        }

        _projection.StageCalibration(resolvedCalibration, calibrationValid, metacarpalCalibration);
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
        Array.Clear(_calibrationWarningLogged);
        ResetArbitrationBlendState();
        _projection.ClearCalibration();

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
        Array.Clear(_calibrationWarningLogged);
        ResetArbitrationBlendState();
        _projection.ClearBinding();
        _restNeutralValid = false;
        _restNeutralError = string.Empty;
        IsOpticalSessionActive = false;
    }

    /// <summary>
    /// Clears both hands' arbitration blend bookkeeping — blend state resets on session exit, skeleton rebind,
    /// and whenever an already-tracking hand's arbitration clears (XR-002 TR30-TR31, TR37).
    /// </summary>
    private void ResetArbitrationBlendState()
    {
        foreach (OpticalGrabPoseBlendState poseBlend in _grabPoseBlends)
        {
            poseBlend.ResetToTracking();
        }

        Array.Clear(_arbitrationStates);
        Array.Clear(_arbitrationAnimations);
        Array.Clear(_arbitrationOwners);
        Array.Clear(_releaseBlendClockFrozen);
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
        _projection.ClearBinding();

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

        // Publication stages the projection inputs into the shared binding — the single source of truth the
        // modifier's writes and the grab recogniser's predictions both consume.
        if (!_projection.TryStageBinding(
                derivedNeutrals,
                localHingeAxes,
                localProximalFrames,
                correspondenceFrames,
                out error))
        {
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void WarnFrozenDestinationOnce(LimbSide side, int fingerIndex, OpticalFingerProjectionStatus status)
    {
        if (status != OpticalFingerProjectionStatus.FrozenInvalidCalibration)
        {
            return;
        }

        XRHandJoint joint = XRHandJoints.DestinationJoints[fingerIndex];
        if (!OpticalFingerTrackingCalibrationProfile.TryGetRecordIndex(side, joint, out int calibrationIndex))
        {
            return;
        }

        if (_calibrationWarningLogged[calibrationIndex])
        {
            return;
        }

        _calibrationWarningLogged[calibrationIndex] = true;
        if (GameLoggerResolver.TryResolve(out ILogger<OpticalFingerTrackingModifier>? logger) && logger is not null)
        {
            logger.LogWarning(
                "Optical finger destination {Side}/{Joint} has no valid pinned calibration record; retaining its session freeze cache instead of applying direct mapping.",
                side,
                joint);
        }
    }
}
