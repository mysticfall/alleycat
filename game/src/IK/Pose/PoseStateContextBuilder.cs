using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Mutable builder that assembles <see cref="PoseStateContext"/> instances for a tick.
/// </summary>
/// <remarks>
/// Producers (for example the state-machine driver) may reuse a single builder instance across
/// frames and overwrite fields to avoid churning the managed heap. A fresh
/// <see cref="PoseStateContext"/> snapshot is still returned from <see cref="Build"/> because
/// the context is a record class and therefore immutable once constructed.
/// </remarks>
public sealed class PoseStateContextBuilder
{
    private Dictionary<StringName, float>? _auxiliarySignals;

    /// <summary>
    /// Gets or sets the global left-hand controller transform for the tick.
    /// </summary>
    public Transform3D LeftControllerTransform
    {
        get;
        set;
    } = Transform3D.Identity;

    /// <summary>
    /// Gets or sets the global right-hand controller transform for the tick.
    /// </summary>
    public Transform3D RightControllerTransform
    {
        get;
        set;
    } = Transform3D.Identity;

    /// <summary>
    /// Gets or sets the viewpoint rest transform for calibration.
    /// </summary>
    public Transform3D ViewpointGlobalRest
    {
        get;
        set;
    } = Transform3D.Identity;

    /// <summary>
    /// Gets or sets the camera transform for the tick.
    /// </summary>
    public Transform3D CameraTransform
    {
        get;
        set;
    } = Transform3D.Identity;

    /// <summary>
    /// Gets or sets the active XR world-scale factor.
    /// </summary>
    public float WorldScale
    {
        get;
        set;
    } = 1.0f;

    /// <summary>
    /// Gets or sets the solved skeleton for the tick.
    /// </summary>
    public Skeleton3D? Skeleton
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the cached hip bone index.
    /// </summary>
    public int HipBoneIndex
    {
        get;
        set;
    } = -1;

    /// <summary>
    /// Gets or sets the cached head bone index.
    /// </summary>
    public int HeadBoneIndex
    {
        get;
        set;
    } = -1;

    /// <summary>
    /// Gets or sets the frame delta seconds for the tick.
    /// </summary>
    public double Delta
    {
        get;
        set;
    }

    /// <summary>
    /// Sets (or overwrites) an auxiliary signal entry.
    /// </summary>
    /// <param name="key">Signal name.</param>
    /// <param name="value">Signal value.</param>
    public void SetAuxiliarySignal(StringName key, float value)
    {
        _auxiliarySignals ??= [];
        _auxiliarySignals[key] = value;
    }

    /// <summary>
    /// Clears all auxiliary signals collected so far.
    /// </summary>
    public void ClearAuxiliarySignals() =>
        _auxiliarySignals?.Clear();

    /// <summary>
    /// Builds an immutable <see cref="PoseStateContext"/> snapshot from the current builder state.
    /// </summary>
    /// <returns>The constructed context.</returns>
    public PoseStateContext Build() => new()
    {
        CameraTransform = CameraTransform,
        LeftControllerTransform = LeftControllerTransform,
        RightControllerTransform = RightControllerTransform,
        ViewpointGlobalRest = ViewpointGlobalRest,
        WorldScale = WorldScale,
        Skeleton = Skeleton,
        HipBoneIndex = HipBoneIndex,
        HeadBoneIndex = HeadBoneIndex,
        Delta = Delta,
        AuxiliarySignals = _auxiliarySignals is null
            ? []
            : new Dictionary<StringName, float>(_auxiliarySignals),
    };
}
