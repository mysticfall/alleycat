using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Read-only snapshot of XR, skeleton, and runtime inputs required for pose-state classification,
/// transitions, animation binding, and hip reconciliation during a single tick.
/// </summary>
/// <remarks>
/// <para>
/// This type is intentionally a record to keep its construction flexible across producers
/// (for example the <see cref="PoseStateMachine"/> driver and tests) while avoiding per-field
/// setters after creation. Callers should prefer <see cref="PoseStateContextBuilder"/> to populate
/// an instance once per tick to reduce churn on the managed heap.
/// </para>
/// <para>
/// In Increment 1 of IK-004, consumer code is permitted to leave fields at their default values
/// when the corresponding runtime signal is not yet wired in (for example
/// <see cref="ViewpointGlobalRest"/> and <see cref="CameraTransform"/> will only be
/// meaningful once the <c>PlayerVRIK</c> driver supplies them in Increment 2).
/// </para>
/// </remarks>
public sealed record PoseStateContext
{
    /// <summary>
    /// Gets the current global transform of the XR camera for this tick.
    /// </summary>
    /// <remarks>
    /// May remain <see cref="Transform3D.Identity"/> until the runtime driver supplies it
    /// (wired in Increment 2). Consumers that rely on this value must guard against that case.
    /// </remarks>
    public Transform3D CameraTransform
    {
        get;
        init;
    } = Transform3D.Identity;

    /// <summary>
    /// Gets the global right-hand controller transform for this tick.
    /// </summary>
    public Transform3D RightControllerTransform
    {
        get;
        init;
    } = Transform3D.Identity;

    /// <summary>
    /// Gets the global left-hand controller transform for this tick.
    /// </summary>
    public Transform3D LeftControllerTransform
    {
        get;
        init;
    } = Transform3D.Identity;

    /// <summary>
    /// Gets the global transform of the avatar viewpoint at rest (calibration reference).
    /// </summary>
    /// <remarks>
    /// May remain <see cref="Transform3D.Identity"/> until the runtime driver supplies it
    /// (wired in Increment 2). Consumers that rely on this value must guard against that case.
    /// </remarks>
    public Transform3D ViewpointGlobalRest
    {
        get;
        init;
    } = Transform3D.Identity;

    /// <summary>
    /// Gets the XR world-scale factor applied by the runtime bridge.
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
    /// (for example headset pitch, controller height, or animation-derived scalars).
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
    /// Attempts to resolve an auxiliary signal value by <paramref name="key"/>.
    /// </summary>
    /// <param name="key">Signal name to look up.</param>
    /// <param name="value">Resolved value when the signal is present.</param>
    /// <returns><c>true</c> when the signal is present; otherwise <c>false</c>.</returns>
    public bool TryGetAuxiliarySignal(StringName key, out float value) =>
        AuxiliarySignals.TryGetValue(key, out value);
}
