using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Read-only snapshot of IK target, skeleton, and runtime inputs required for pose-state classification,
/// transitions, state-owned animation control, and hip reconciliation during a single tick.
/// </summary>
/// <remarks>
/// <para>
/// This type is intentionally a record to keep its construction flexible across producers
/// (for example the <see cref="PoseStateMachine"/> driver and tests) while avoiding per-field
/// setters after creation. Callers should prefer <see cref="PoseStateContextBuilder"/> to populate
/// an instance once per tick to reduce churn on the managed heap.
/// </para>
/// <para>
/// Consumer code may leave fields at their default values when the corresponding runtime signal
/// is not wired in yet.
/// </para>
/// </remarks>
public sealed record PoseStateContext
{
    /// <summary>
    /// Gets the intrinsic rest-pose head-height body measure in skeleton-local space.
    /// </summary>
    /// <remarks>
    /// This value is derived from the calibrated rest head target after converting to skeleton
    /// local space (<c>abs(restHeadLocal.Y)</c>). It is invariant to world origin/elevation and
    /// is used as the normalisation baseline for ratio-based kneeling thresholds.
    /// </remarks>
    public float RestHeadHeight
    {
        get;
        init;
    } = 1.0f;

    /// <summary>
    /// Gets the precomputed head offset from rest-local to current-local, expressed in
    /// skeleton-local space and normalised by rest local head height.
    /// </summary>
    /// <remarks>
    /// The baseline is <c>restHeadLocal.Y == 1.0</c>, so each component represents offset as a
    /// fraction of rest height rather than metres. Positive Y means the head moved upward;
    /// negative Y means descent.
    /// </remarks>
    public Vector3 NormalizedHeadLocalOffset
    {
        get;
        init;
    } = Vector3.Zero;

    /// <summary>
    /// Gets the current global head IK target transform for this tick.
    /// </summary>
    /// <remarks>
    /// May remain <see cref="Transform3D.Identity"/> until the runtime driver supplies it.
    /// Consumers that rely on this value must guard against that case.
    /// </remarks>
    public Transform3D HeadTargetTransform
    {
        get;
        init;
    } = Transform3D.Identity;

    /// <summary>
    /// Gets the global right-hand IK target transform for this tick.
    /// </summary>
    public Transform3D RightHandTargetTransform
    {
        get;
        init;
    } = Transform3D.Identity;

    /// <summary>
    /// Gets the global left-hand IK target transform for this tick.
    /// </summary>
    public Transform3D LeftHandTargetTransform
    {
        get;
        init;
    } = Transform3D.Identity;

    /// <summary>
    /// Gets the global left-foot IK target transform for this tick.
    /// </summary>
    public Transform3D LeftFootTargetTransform
    {
        get;
        init;
    } = Transform3D.Identity;

    /// <summary>
    /// Gets the global right-foot IK target transform for this tick.
    /// </summary>
    public Transform3D RightFootTargetTransform
    {
        get;
        init;
    } = Transform3D.Identity;

    /// <summary>
    /// Gets the global rest/reference transform for the head target.
    /// </summary>
    /// <remarks>
    /// May remain <see cref="Transform3D.Identity"/> until the runtime driver supplies it.
    /// Consumers that rely on this value must guard against that case.
    /// </remarks>
    public Transform3D HeadTargetRestTransform
    {
        get;
        init;
    } = Transform3D.Identity;

    /// <summary>
    /// Gets the world-scale factor applied by the runtime bridge.
    /// </summary>
    public float WorldScale
    {
        get;
        init;
    } = 1.0f;

    /// <summary>
    /// Gets the solved skeleton for this tick, or <c>null</c> if not yet resolved.
    /// </summary>
    public Skeleton3D? Skeleton
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the current <see cref="AnimationTree"/> instance for this tick, or <c>null</c> when
    /// no runtime animation tree is bound.
    /// </summary>
    public AnimationTree? AnimationTree
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the cached hip bone index for <see cref="Skeleton"/>, or <c>-1</c> if unresolved.
    /// </summary>
    public int HipBoneIndex
    {
        get;
        init;
    } = -1;

    /// <summary>
    /// Gets the cached head bone index for <see cref="Skeleton"/>, or <c>-1</c> if unresolved.
    /// </summary>
    public int HeadBoneIndex
    {
        get;
        init;
    } = -1;

    /// <summary>
    /// Gets the frame delta seconds for this tick.
    /// </summary>
    public double Delta
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the auxiliary signals dictionary keyed by <see cref="StringName"/>
    /// (for example head pitch, hand height, or animation-derived scalars).
    /// </summary>
    /// <remarks>
    /// Treat the dictionary as read-only from a consumer perspective. The producer populates
    /// signals before passing the context to <see cref="PoseStateMachine.Tick"/>.
    /// </remarks>
    public IReadOnlyDictionary<StringName, float> AuxiliarySignals
    {
        get;
        init;
    } = _emptySignals;

    private static readonly IReadOnlyDictionary<StringName, float> _emptySignals =
        new Dictionary<StringName, float>();

    /// <summary>
    /// Gets the state that was active when the current transition fired, or null when no
    /// transition is in progress. Receiving states can type-test this against transition-
    /// source interfaces to query effective source values for smooth blending.
    /// </summary>
    public IPoseState? TransitionSourceState
    {
        get;
        init;
    }

    /// <summary>
    /// Attempts to resolve an auxiliary signal value by <paramref name="key"/>.
    /// </summary>
    /// <param name="key">Signal name to look up.</param>
    /// <param name="value">Resolved value when the signal is present.</param>
    /// <returns><c>true</c> when the signal is present; otherwise <c>false</c>.</returns>
    public bool TryGetAuxiliarySignal(StringName key, out float value) =>
        AuxiliarySignals.TryGetValue(key, out value);
}
