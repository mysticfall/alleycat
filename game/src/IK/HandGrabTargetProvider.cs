using Godot;

namespace AlleyCat.IK;

/// <summary>
/// IK target provider that can temporarily override a default hand target while retaining smooth provider-driven motion.
/// </summary>
[GlobalClass]
public sealed partial class HandGrabTargetProvider : IKTargetIntentProvider
{
    private const float MinimumDeltaSeconds = 0.000001f;
    private Transform3D _currentTransform = Transform3D.Identity;
    private ulong _lastTicksUsec;
    private bool _hasCurrentTransform;
    private bool _returningToDefault;

    /// <summary>
    /// Provider used when no grab override is active, usually the side's XR controller provider.
    /// </summary>
    [Export]
    public IKTargetIntentProvider? DefaultProvider
    {
        get; set;
    }

    /// <summary>
    /// Transform interpolation responsiveness in reciprocal seconds.
    /// </summary>
    [Export(PropertyHint.Range, "0.1,60,0.1,or_greater")]
    public float Responsiveness { get; set; } = 18.0f;

    /// <summary>
    /// Desired IK influence while a grab override is active.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float GrabInfluence { get; set; } = 1.0f;

    /// <summary>
    /// Target transform currently used for the held grab point.
    /// </summary>
    public Transform3D GrabTarget { get; private set; } = Transform3D.Identity;

    /// <summary>
    /// Whether the provider is currently overriding the default target for a pending grab approach.
    /// </summary>
    public bool IsGrabOverrideActive
    {
        get; private set;
    }

    /// <summary>
    /// Activates or updates the grab target override.
    /// </summary>
    public void SetGrabTarget(Transform3D grabTarget)
    {
        EnsureCurrentTransform();
        GrabTarget = Orthonormalise(grabTarget);
        IsGrabOverrideActive = true;
        _returningToDefault = false;
    }

    /// <summary>
    /// Releases the grab override and smoothly returns to the default provider.
    /// </summary>
    public void ReleaseGrabTarget()
    {
        if (!IsGrabOverrideActive && !_returningToDefault)
        {
            return;
        }

        EnsureCurrentTransform();
        IsGrabOverrideActive = false;
        _returningToDefault = true;
    }

    /// <summary>
    /// Clears the grab override immediately and resumes the default provider without smooth return interpolation.
    /// </summary>
    public void ClearGrabTargetImmediate()
    {
        IKTargetIntent defaultIntent = GetDefaultIntent();
        _currentTransform = Orthonormalise(defaultIntent.WorldTransform);
        _hasCurrentTransform = true;
        _lastTicksUsec = Time.GetTicksUsec();
        IsGrabOverrideActive = false;
        _returningToDefault = false;
    }

    /// <inheritdoc />
    public override IKTargetIntent GetTargetIntent()
    {
        IKTargetIntent defaultIntent = GetDefaultIntent();
        if (!IsGrabOverrideActive && !_returningToDefault)
        {
            _currentTransform = Orthonormalise(defaultIntent.WorldTransform);
            _hasCurrentTransform = true;
            _lastTicksUsec = Time.GetTicksUsec();
            return defaultIntent;
        }

        EnsureCurrentTransform(defaultIntent.WorldTransform);
        float alpha = ComputeInterpolationAlpha();
        Transform3D target = IsGrabOverrideActive ? GrabTarget : Orthonormalise(defaultIntent.WorldTransform);
        _currentTransform = Interpolate(_currentTransform, target, alpha);

        if (_returningToDefault && IsClose(_currentTransform, target))
        {
            _returningToDefault = false;
            _currentTransform = target;
            return defaultIntent;
        }

        float influence = IsGrabOverrideActive ? Mathf.Max(defaultIntent.DesiredInfluence, GrabInfluence) : defaultIntent.DesiredInfluence;
        return new IKTargetIntent(_currentTransform, influence);
    }

    private IKTargetIntent GetDefaultIntent()
        => DefaultProvider is not null && IsInstanceValid(DefaultProvider)
            ? DefaultProvider.GetTargetIntent()
            : new IKTargetIntent(_hasCurrentTransform ? _currentTransform : Transform3D.Identity, 0.0f);

    private void EnsureCurrentTransform()
        => EnsureCurrentTransform(GetDefaultIntent().WorldTransform);

    private void EnsureCurrentTransform(Transform3D defaultTransform)
    {
        if (_hasCurrentTransform)
        {
            return;
        }

        _currentTransform = Orthonormalise(defaultTransform);
        _hasCurrentTransform = true;
        _lastTicksUsec = Time.GetTicksUsec();
    }

    private float ComputeInterpolationAlpha()
    {
        ulong now = Time.GetTicksUsec();
        float deltaSeconds = _lastTicksUsec == 0 ? 0.0f : (now - _lastTicksUsec) / 1_000_000.0f;
        _lastTicksUsec = now;
        return deltaSeconds <= MinimumDeltaSeconds ? 0.0f : Mathf.Clamp(1.0f - Mathf.Exp(-Responsiveness * deltaSeconds), 0.0f, 1.0f);
    }

    private static Transform3D Interpolate(Transform3D from, Transform3D to, float alpha)
        => new(from.Basis.Slerp(to.Basis, alpha).Orthonormalized(), from.Origin.Lerp(to.Origin, alpha));

    private static Transform3D Orthonormalise(Transform3D transform)
        => new(transform.Basis.Orthonormalized(), transform.Origin);

    private static bool IsClose(Transform3D current, Transform3D target)
        => current.Origin.DistanceSquaredTo(target.Origin) <= 0.000001f
           && current.Basis.GetRotationQuaternion().AngleTo(target.Basis.GetRotationQuaternion()) <= 0.01f;
}
