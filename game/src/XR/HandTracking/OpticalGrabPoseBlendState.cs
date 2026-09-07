using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>
/// One hand's optical-grab pose-arbitration blend phase (XR-002 UR16, TR30-TR31; INTR-003 TR19-TR21;
/// INTR-002 R60-61): the per-hand state the finger modifier moves through around a committed optical grab.
/// </summary>
internal enum OpticalGrabPoseBlendPhase : byte
{
    /// <summary>Normal tracking writes; arbitration reports no held grab on this hand.</summary>
    Tracking = 0,

    /// <summary>
    /// Arbitration entered pending assistance: the modifier writes this hand for the blend window, slerping the
    /// last-written tracked pose toward the sampled authored candidate reference, then continues writing that
    /// reference for the remainder of the approach.
    /// </summary>
    PendingAssistance = 1,

    /// <summary>
    /// Arbitration flipped from pending assistance to held: the modifier keeps writing this hand for the blend window, slerping the
    /// last-written tracked pose toward the sampled authored grab reference (XR-002 UR16).
    /// </summary>
    CommitBlend = 2,

    /// <summary>
    /// The commit blend completed and arbitration is still held: the modifier performs zero writes for this
    /// hand — the AnimationTree owns presentation and already shows the identical authored values
    /// (XR-002 TR31; OG11).
    /// </summary>
    HeldSuppressed = 3,

    /// <summary>
    /// Arbitration cleared while the optical session is active: the modifier writes this hand starting from
    /// its current (authored) pose, slerping toward the live projected tracked pose over the blend window,
    /// then resumes normal tracking (INTR-002 R61).
    /// </summary>
    ReleaseBlend = 4,
}

/// <summary>
/// Pure per-hand pose-arbitration blend bookkeeping (XR-002 UR16, TR30-TR31): elapsed/progress plus the
/// from/to rotation buffers for the commit and release blend windows, with all phase transitions and the
/// whole-hand-loss clock freeze expressed without any Godot node or provider dependency so the state
/// machine is unit-testable in isolation.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Commit blend.</strong> <see cref="BeginCommitBlend" /> captures the last-written tracked pose
/// (the values currently on the bones) as the blend start and the sampled authored grab reference as the
/// fixed target. <see cref="EvaluateCommitBlend" /> hemisphere-aligns the authored target to the start pose
/// before slerping, and returns the aligned target exactly once progress reaches 1 so the modifier's final
/// window write is byte-identical to the authored reference at handoff.
/// </para>
/// <para>
/// <strong>Release blend.</strong> <see cref="BeginReleaseBlend" /> captures the hand's current effective
/// pose (the authored reference once suppressed, or the mid-window commit value if the grab released before
/// the commit window completed). The target is the live projected tracked pose supplied per frame by the
/// modifier; a destination with an invalid projection freezes at its last written value, and whole-hand
/// tracking loss additionally freezes the blend clock through the <c>clockAdvances</c> argument of
/// <see cref="Advance(float,bool)" /> (XR-002 TR33-TR36 freeze semantics mirrored).
/// </para>
/// <para>
/// <strong>Completion.</strong> <see cref="Advance" /> completes a window automatically — commit blends
/// settle into <see cref="OpticalGrabPoseBlendPhase.HeldSuppressed" />, release blends into
/// <see cref="OpticalGrabPoseBlendPhase.Tracking" /> — and returns <see langword="true" /> exactly on the
/// completing call. A non-positive duration completes on the first advance.
/// </para>
/// <para>
/// All buffers are pre-allocated; transitions and evaluation never allocate.
/// </para>
/// </remarks>
internal sealed class OpticalGrabPoseBlendState
{
    /// <summary>Number of finger destinations one hand's blend covers.</summary>
    public const int FingerCount = OpticalFingerProjectionBinding.FingerBonesPerSide;

    private readonly Quaternion[] _commitFrom = new Quaternion[FingerCount];

    private readonly Quaternion[] _authoredReference = new Quaternion[FingerCount];

    private readonly Quaternion[] _releaseFrom = new Quaternion[FingerCount];

    private bool _windowCompletionReported;

    /// <summary>Current arbitration blend phase of this hand.</summary>
    public OpticalGrabPoseBlendPhase Phase { get; private set; } = OpticalGrabPoseBlendPhase.Tracking;

    /// <summary>Seconds elapsed in the active blend window.</summary>
    public float ElapsedSeconds
    {
        get;
        private set;
    }

    /// <summary>Configured duration of the active blend window in seconds.</summary>
    public float DurationSeconds
    {
        get;
        private set;
    }

    /// <summary>Clamped linear blend progress of the active window; 1 when no window or a zero-duration window applies.</summary>
    public float Progress => DurationSeconds <= 0f
        ? 1f
        : Mathf.Clamp(ElapsedSeconds / DurationSeconds, 0f, 1f);

    /// <summary>Whether the modifier must perform zero writes for this hand (XR-002 TR31; OG11).</summary>
    public bool IsWriteSuppressed => Phase == OpticalGrabPoseBlendPhase.HeldSuppressed;

    /// <summary>
    /// Whether the commit-blend duration has elapsed. This is a minimum handoff delay only; the modifier must also
    /// receive readiness from the legitimate AnimationTree owner before entering suppression.
    /// </summary>
    public bool IsCommitBlendMinimumSatisfied => Phase == OpticalGrabPoseBlendPhase.CommitBlend && _windowCompletionReported;

    /// <summary>Whether a valid authored reference has been staged for this hand's held grab.</summary>
    public bool HasAuthoredReference
    {
        get;
        private set;
    }

    /// <summary>The sampled authored grab reference of the held grab, in canonical destination order.</summary>
    public ReadOnlySpan<Quaternion> AuthoredReferencePoses => _authoredReference;

    /// <summary>
    /// Begins the commit blend from the last pose the modifier actually wrote toward the sampled authored
    /// grab reference.
    /// </summary>
    /// <param name="fromLastWritten">Blend start pose — the values currently on the bones.</param>
    /// <param name="authoredReference">Sampled authored grab reference pose.</param>
    /// <param name="durationSeconds">Blend window duration in seconds.</param>
    public void BeginCommitBlend(
        ReadOnlySpan<Quaternion> fromLastWritten,
        ReadOnlySpan<Quaternion> authoredReference,
        float durationSeconds)
    {
        ValidateLength(fromLastWritten, nameof(fromLastWritten));
        ValidateLength(authoredReference, nameof(authoredReference));

        fromLastWritten.CopyTo(_commitFrom);
        authoredReference.CopyTo(_authoredReference);
        HasAuthoredReference = true;
        DurationSeconds = Math.Max(0f, durationSeconds);
        ElapsedSeconds = 0f;
        _windowCompletionReported = false;
        Phase = OpticalGrabPoseBlendPhase.CommitBlend;
    }

    /// <summary>
    /// Begins the visible pending-contact assistance blend from the last modifier output toward the candidate's
    /// immutable authored reference. Unlike a committed blend, completion remains in pending assistance so that
    /// the reference continues to be written while AnimationTree has no pending grab pose.
    /// </summary>
    public void BeginPendingAssistance(
        ReadOnlySpan<Quaternion> fromLastWritten,
        ReadOnlySpan<Quaternion> authoredReference,
        float durationSeconds)
    {
        ValidateLength(fromLastWritten, nameof(fromLastWritten));
        ValidateLength(authoredReference, nameof(authoredReference));

        fromLastWritten.CopyTo(_commitFrom);
        authoredReference.CopyTo(_authoredReference);
        HasAuthoredReference = true;
        DurationSeconds = Math.Max(0f, durationSeconds);
        ElapsedSeconds = 0f;
        _windowCompletionReported = false;
        Phase = OpticalGrabPoseBlendPhase.PendingAssistance;
    }

    /// <summary>
    /// Begins the release blend from the hand's current effective pose toward the live projected tracked
    /// pose.
    /// </summary>
    /// <param name="fromCurrentPose">Blend start pose — the hand's current effective (authored) pose.</param>
    /// <param name="durationSeconds">Blend window duration in seconds.</param>
    public void BeginReleaseBlend(ReadOnlySpan<Quaternion> fromCurrentPose, float durationSeconds)
    {
        ValidateLength(fromCurrentPose, nameof(fromCurrentPose));

        fromCurrentPose.CopyTo(_releaseFrom);
        DurationSeconds = Math.Max(0f, durationSeconds);
        ElapsedSeconds = 0f;
        _windowCompletionReported = false;
        Phase = OpticalGrabPoseBlendPhase.ReleaseBlend;
    }

    /// <summary>
    /// Advances the active blend clock and records completion when it reaches the configured duration.
    /// </summary>
    /// <param name="deltaSeconds">Frame delta in seconds.</param>
    /// <param name="clockAdvances">
    /// Whether samples are usable — whole-hand tracking loss passes <see langword="false" /> to freeze the
    /// blend at its current value until samples recover (XR-002 TR36 mirrored for the release blend).</param>
    /// <returns><see langword="true" /> exactly when a blend window completed on this call.</returns>
    public bool Advance(float deltaSeconds, bool clockAdvances)
    {
        if (Phase is not (
                OpticalGrabPoseBlendPhase.PendingAssistance
                or OpticalGrabPoseBlendPhase.CommitBlend
                or OpticalGrabPoseBlendPhase.ReleaseBlend)
            || !clockAdvances
            || deltaSeconds <= 0f)
        {
            return false;
        }

        if (_windowCompletionReported)
        {
            return false;
        }

        ElapsedSeconds = Math.Min(DurationSeconds, ElapsedSeconds + deltaSeconds);
        if (ElapsedSeconds < DurationSeconds)
        {
            return false;
        }

        _windowCompletionReported = true;

        if (Phase == OpticalGrabPoseBlendPhase.ReleaseBlend)
        {
            Phase = OpticalGrabPoseBlendPhase.Tracking;
        }

        return true;
    }

    /// <summary>Enters zero-write suppression after both the commit minimum and owner readiness are satisfied.</summary>
    public bool TrySuppressCommitBlend()
    {
        if (!IsCommitBlendMinimumSatisfied)
        {
            return false;
        }

        Phase = OpticalGrabPoseBlendPhase.HeldSuppressed;
        return true;
    }

    /// <summary>
    /// Evaluates one finger's commit-blend rotation at the current progress: the hemisphere-aligned authored
    /// target exactly once progress reaches 1, otherwise the slerp from the last-written pose toward it.
    /// </summary>
    /// <param name="fingerIndex">Canonical destination index within the hand.</param>
    public Quaternion EvaluateCommitBlend(int fingerIndex)
        => Evaluate(_commitFrom[fingerIndex], _authoredReference[fingerIndex], Progress);

    /// <summary>Evaluates one finger's pending-assistance rotation at the current progress.</summary>
    public Quaternion EvaluatePendingAssistance(int fingerIndex)
        => Evaluate(_commitFrom[fingerIndex], _authoredReference[fingerIndex], Progress);

    /// <summary>
    /// Evaluates one finger's release-blend rotation at the current progress: the hemisphere-aligned live
    /// target exactly once progress reaches 1, otherwise the slerp from the current-pose capture toward it.
    /// </summary>
    /// <param name="fingerIndex">Canonical destination index within the hand.</param>
    /// <param name="liveTarget">This frame's projected tracked rotation for the destination.</param>
    public Quaternion EvaluateReleaseBlend(int fingerIndex, Quaternion liveTarget)
        => Evaluate(_releaseFrom[fingerIndex], liveTarget, Progress);

    /// <summary>Resets to plain tracking with all blend buffers cleared.</summary>
    public void ResetToTracking()
    {
        Phase = OpticalGrabPoseBlendPhase.Tracking;
        ElapsedSeconds = 0f;
        DurationSeconds = 0f;
        HasAuthoredReference = false;
        _windowCompletionReported = false;
        Array.Clear(_commitFrom);
        Array.Clear(_authoredReference);
        Array.Clear(_releaseFrom);
    }

    private static Quaternion Evaluate(Quaternion from, Quaternion target, float progress)
    {
        // The target is hemisphere-aligned to the start pose so the slerp takes the short arc, and the
        // final window write is the aligned target exactly — the AnimationTree then shows the identical
        // rotation at handoff.
        Quaternion alignedTarget = FingerRetargetingMath.StabiliseRotationHemisphere(target, from);
        return progress >= 1f ? alignedTarget : from.Slerp(alignedTarget, progress);
    }

    private static void ValidateLength(ReadOnlySpan<Quaternion> poses, string paramName)
    {
        if (poses.Length != FingerCount)
        {
            throw new ArgumentException(
                $"A pose-arbitration blend buffer requires exactly {FingerCount} rotations.",
                paramName);
        }
    }
}
