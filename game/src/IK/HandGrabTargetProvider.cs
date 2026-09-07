using Godot;

namespace AlleyCat.IK;

/// <summary>
/// IK target provider that can temporarily override a default hand target while retaining smooth provider-driven motion.
/// Interpolation advances once per authoritative simulation tick through <see cref="AdvanceSimulation" />; the
/// wall clock is never read, so pauses and process-frame jitter cannot advance or jump the interpolation
/// (IK-005 TR20). A Movable commit ends the override through the <see cref="BeginHeldCarry" /> anchor, which
/// holds the authored grip across the commit boundary before the residual return resumes default tracking.
/// The return carries the live source's bounded motion while decaying the source-relative residual to zero
/// and then completes into true default passthrough, so held tracking carries no steady-state lag
/// (INTR-002 R2/R4; IK-005 TR19-TR21).
/// </summary>
[GlobalClass]
public sealed partial class HandGrabTargetProvider : IKTargetIntentProvider, IIKSimulationAdvancable
{
    private const float MinimumDeltaSeconds = 0.000001f;

    private const float ReturnSnapDistanceSquaredMetres = 0.000001f;

    private const float ReturnSnapAngleRadians = 0.01f;

    private Transform3D _currentTransform = Transform3D.Identity;
    private Transform3D _previousSourceTransform = Transform3D.Identity;
    private bool _hasCurrentTransform;
    private bool _hasPreviousSourceTransform;
    private bool _returningToDefault;
    private bool _heldCarryPending;
    private bool _heldCarryActive;
    private double _heldCarryElapsedSeconds;

    /// <summary>
    /// Provider used when no grab override is active, usually the side's XR controller provider.
    /// </summary>
    [Export]
    public IKTargetIntentProvider? DefaultProvider
    {
        get;
        set;
    }

    /// <summary>
    /// Approach transform interpolation responsiveness in reciprocal seconds. This bounds the provider's
    /// per-tick translation toward an assisted approach command to a fraction of the assisted approach
    /// distance, keeping adjacent-frame hand and held-item motion within the INTR-002 R2/R4 continuity
    /// budget from the first override tick (a typical 9 cm assisted offset steps under 9 mm per tick at
    /// 60 Hz); the exponential remains composition-exact for catch-up deltas (IK-005 TR20). Held tracking
    /// does not use this rate — see <see cref="ReturnResponsiveness" />.
    /// </summary>
    [Export(PropertyHint.Range, "0.1,60,0.1,or_greater")]
    public float Responsiveness { get; set; } = 8.0f;

    /// <summary>
    /// Held-carry return responsiveness in reciprocal seconds: the decay rate of the source-relative
    /// residual once the held-carry anchor has elapsed (or a release ends the override). Faster than the
    /// approach <see cref="Responsiveness" /> because the held item follows the hand while the residual
    /// decays — the per-tick decay stays additionally capped by
    /// <see cref="HeldCarryMaximumSpeedMetresPerSecond" /> — and because the return must converge to true
    /// default passthrough so sustained held motion carries no steady-state lag (INTR-002 R2/R4).
    /// </summary>
    [Export(PropertyHint.Range, "0.1,60,0.1,or_greater")]
    public float ReturnResponsiveness { get; set; } = 18.0f;

    /// <summary>
    /// Maximum source motion the residual return applies to the output per second, in metres. Live source
    /// translation beyond this rate is not carried in one tick; the un-carried displacement becomes decaying
    /// return residual instead of an output jump, so a stepped or reset source never teleports the held
    /// hand. The same bound caps the return residual's translation decay, keeping provider-attributable
    /// adjacent-frame output motion within the INTR-002 R2/R4 continuity budget (0.6 m/s steps 10 mm per
    /// tick at 60 Hz).
    /// </summary>
    [Export(PropertyHint.Range, "0.01,10,0.01,or_greater")]
    public float HeldCarryMaximumSpeedMetresPerSecond { get; set; } = 0.6f;

    /// <summary>
    /// Maximum source rotation the residual return applies to the output per second, in degrees. Live source
    /// rotation beyond this rate is not carried in one tick; the un-carried rotation becomes decaying return
    /// residual instead of an output jump. The same bound caps the return residual's rotation decay
    /// (360 °/s steps 6° per tick at 60 Hz).
    /// </summary>
    [Export(PropertyHint.Range, "10,2160,10,or_greater")]
    public float HeldCarryMaximumAngularSpeedDegreesPerSecond { get; set; } = 360.0f;

    /// <summary>
    /// Duration of the held-carry anchor window after a Movable commit, in simulation seconds. During this
    /// window the provider keeps commanding the last approach pose — settling the commit-gate residual while
    /// the authored grip presentation owns the hand and the held attachment keeps its commit-gate placement —
    /// before the residual return resumes live default-source tracking (INTR-002 R2/R4; the hand must not be
    /// yanked back toward a distant assisted source while the presentation takes over).
    /// </summary>
    [Export(PropertyHint.Range, "0,2,0.01,or_greater")]
    public float HeldCarryAnchorSeconds { get; set; } = 0.35f;

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
        get;
        private set;
    }

    /// <summary>
    /// Independent source intent sampled at the canonical epoch — the most recent authoritative simulation tick
    /// — before any grab override, assistance, or physical actuation is applied. Grab eligibility, retained
    /// contact, and recognition queries must consume this snapshot rather than arbitrary-phase target reads;
    /// provider output is never fed back into it (IK-005 TR19-TR20).
    /// </summary>
    public IKTargetIntent LastSourceIntent { get; private set; } = new(Transform3D.Identity, 0.0f);

    /// <summary>
    /// Whether <see cref="AdvanceSimulation" /> has captured at least one canonical source sample.
    /// </summary>
    public bool HasSourceIntent
    {
        get;
        private set;
    }

    /// <summary>
    /// Most recent intent returned to the IK target pipeline. This observation does not evaluate or advance the
    /// provider and is intended for temporal diagnostics.
    /// </summary>
    public IKTargetIntent LastOutputIntent { get; private set; } = new(Transform3D.Identity, 0.0f);

    /// <summary>
    /// Gets the canonical-epoch source intent when a usable sample exists. A sample is usable when the default
    /// source reported positive influence; absent or invalid source state returns <see langword="false" /> so
    /// consumers can fall back to their own query transform.
    /// </summary>
    /// <param name="sourceIntent">The captured source intent; unchanged when unavailable.</param>
    /// <returns><see langword="true" /> when a usable canonical source sample exists.</returns>
    public bool TryGetSourceIntent(out IKTargetIntent sourceIntent)
    {
        sourceIntent = LastSourceIntent;
        return HasSourceIntent && LastSourceIntent.DesiredInfluence > 0.0f;
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
        _heldCarryPending = false;
        _heldCarryActive = false;
        _heldCarryElapsedSeconds = 0.0;
    }

    /// <summary>
    /// Releases the grab override and smoothly returns to the default provider.
    /// </summary>
    public void ReleaseGrabTarget()
    {
        if (!IsGrabOverrideActive && !_returningToDefault && !_heldCarryPending && !_heldCarryActive)
        {
            return;
        }

        EnsureCurrentTransform();
        IsGrabOverrideActive = false;
        _returningToDefault = true;
        _heldCarryPending = false;
        _heldCarryActive = false;
    }

    /// <summary>
    /// Ends the grab override at a Movable commit and schedules the held-carry anchor. Until the next
    /// authoritative simulation tick the provider answers with plain default passthrough — the immediate
    /// post-commit observation contract — while a driven rig's first <see cref="AdvanceSimulation" /> engages
    /// the anchor: the provider keeps interpolating toward the last approach command, settling the
    /// commit-gate residual while the authored grip presentation takes over, for
    /// <see cref="HeldCarryAnchorSeconds" /> of simulation time. The override flag clears immediately so
    /// held-state observers see the commit, while the hand never teleports from the authored grip to a
    /// distant assisted source (INTR-002 R2/R4).
    /// </summary>
    public void BeginHeldCarry()
    {
        EnsureCurrentTransform();
        IsGrabOverrideActive = false;
        _returningToDefault = false;
        _heldCarryPending = true;
        _heldCarryActive = false;
        _heldCarryElapsedSeconds = 0.0;
    }

    /// <summary>
    /// Advances interpolation by one authoritative simulation tick and captures the canonical source sample.
    /// Interpolation never advances outside this call, so wall-clock pauses and repeated observations cannot
    /// move the provider (IK-005 TR20).
    /// </summary>
    /// <param name="deltaSeconds">Simulation delta for this tick in seconds.</param>
    public void AdvanceSimulation(double deltaSeconds)
    {
        IKTargetIntent defaultIntent = GetDefaultIntent();
        if (defaultIntent.DesiredInfluence > 0.0f)
        {
            // Retain the latest usable canonical source sample. An unavailable or zero-influence source never
            // clobbers it, so transient loss cannot substitute assisted output for the independent source
            // intent consumers query (IK-005 TR19-TR21).
            LastSourceIntent = defaultIntent;
            HasSourceIntent = true;
        }

        Transform3D source = Orthonormalise(defaultIntent.WorldTransform);

        if (!_hasCurrentTransform)
        {
            // The first authoritative tick establishes the interpolation baseline without stepping.
            _currentTransform = source;
            _hasCurrentTransform = true;
            _previousSourceTransform = source;
            _hasPreviousSourceTransform = true;
            return;
        }

        // The residual return carries the live source's motion — never pursuing the source through a low-pass
        // that would leave a permanent velocity-dependent lag — with the carried motion bounded per tick so a
        // stepped or reset source cannot teleport the held hand (INTR-002 R2/R4; IK-005 TR20). The
        // previous-source sample is established even when the interpolation baseline was seeded by an
        // override rather than by an advance, so the carry engages from the first return tick after any grab.
        Transform3D sourceMotion = ComputeSourceMotion(source, deltaSeconds);
        _previousSourceTransform = source;
        _hasPreviousSourceTransform = true;

        if (_heldCarryPending)
        {
            // The first authoritative tick after the commit engages the anchor from the retained approach
            // interpolation state; undriven consumers never observe an intermediate carry pose.
            _heldCarryPending = false;
            _heldCarryActive = true;
            _heldCarryElapsedSeconds = 0.0;
        }

        if (_heldCarryActive)
        {
            // The held-carry anchor keeps interpolating toward the last approach command — settling the
            // commit-gate residual while the authored grip presentation owns the hand and the held
            // attachment keeps its commit-gate placement across the boundary (INTR-002 R2/R4: the hand must
            // not be yanked back toward a distant assisted source while the presentation takes over). The
            // window advances on the simulation clock only; when it elapses, the residual return carries the
            // live source's bounded motion while decaying the source-relative offset to zero, completing
            // into exact default-source tracking with no steady-state held lag (IK-005 TR20).
            _heldCarryElapsedSeconds += deltaSeconds;
            float settleAlpha = ComputeInterpolationAlpha(deltaSeconds);
            _currentTransform = Interpolate(_currentTransform, GrabTarget, settleAlpha);
            if (_heldCarryElapsedSeconds >= Mathf.Max(0.0f, HeldCarryAnchorSeconds))
            {
                _heldCarryActive = false;
                _returningToDefault = true;
            }

            return;
        }

        if (IsGrabOverrideActive)
        {
            // The assisted approach pursues the authored command; its per-tick step stays bounded by the
            // approach responsiveness, which is what the approach continuity budget certifies.
            float approachAlpha = ComputeInterpolationAlpha(deltaSeconds);
            _currentTransform = Interpolate(_currentTransform, GrabTarget, approachAlpha);
            return;
        }

        if (_returningToDefault)
        {
            AdvanceReturn(source, sourceMotion, deltaSeconds);
            return;
        }

        _currentTransform = source;
    }

    /// <inheritdoc />
    public override IKTargetIntent GetTargetIntent()
    {
        IKTargetIntent defaultIntent = GetDefaultIntent();
        if (!IsGrabOverrideActive && !_returningToDefault && !_heldCarryActive)
        {
            return CaptureOutput(defaultIntent);
        }

        EnsureCurrentTransform(defaultIntent.WorldTransform);
        float influence = IsGrabOverrideActive || _heldCarryActive
            ? Mathf.Max(defaultIntent.DesiredInfluence, GrabInfluence)
            : defaultIntent.DesiredInfluence;
        return CaptureOutput(new IKTargetIntent(_currentTransform, influence));
    }

    /// <summary>
    /// Advances one residual-return tick. The output is composed from the live source and a decaying
    /// source-relative residual: exact source motion is carried (bounded per tick), the residual's translation
    /// and rotation decay toward identity at <see cref="ReturnResponsiveness" /> under the held-carry caps, and
    /// any un-carried source displacement — a stepped or reset source — is absorbed into the residual so it
    /// decays instead of jumping. Once the residual is within the snap tolerance the return completes into
    /// true default passthrough, which is what removes steady-state held lag (INTR-002 R2/R4; IK-005 TR20).
    /// </summary>
    private void AdvanceReturn(Transform3D source, Transform3D sourceMotion, double deltaSeconds)
    {
        Transform3D carried = sourceMotion * _currentTransform;
        Transform3D residual = source.AffineInverse() * carried;

        float alpha = ComputeReturnAlpha(deltaSeconds);
        float maxTranslationStep = Mathf.Max(0.0f, HeldCarryMaximumSpeedMetresPerSecond) * (float)deltaSeconds;
        Vector3 decayedOrigin = DecayResidualTranslation(residual.Origin, alpha, maxTranslationStep);

        Quaternion rotation = residual.Basis.GetRotationQuaternion();
        float maxAngleStep = Mathf.DegToRad(Mathf.Max(0.0f, HeldCarryMaximumAngularSpeedDegreesPerSecond))
            * (float)deltaSeconds;
        Quaternion decayedRotation = DecayResidualRotation(rotation, alpha, maxAngleStep);

        _currentTransform = source * new Transform3D(new Basis(decayedRotation), decayedOrigin);
        if (decayedOrigin.LengthSquared() <= ReturnSnapDistanceSquaredMetres
            && decayedRotation.AngleTo(Quaternion.Identity) <= ReturnSnapAngleRadians)
        {
            _returningToDefault = false;
            _currentTransform = source;
        }
    }

    private static Vector3 DecayResidualTranslation(Vector3 residualOrigin, float alpha, float maximumStep)
    {
        float distance = residualOrigin.Length();
        if (distance <= Mathf.Epsilon)
        {
            return Vector3.Zero;
        }

        float step = Mathf.Min(alpha * distance, maximumStep);
        return residualOrigin * (1.0f - (step / distance));
    }

    private static Quaternion DecayResidualRotation(Quaternion residualRotation, float alpha, float maximumAngleStep)
    {
        float angle = residualRotation.AngleTo(Quaternion.Identity);
        if (angle <= Mathf.Epsilon)
        {
            return Quaternion.Identity;
        }

        float angleStep = Mathf.Min(alpha * angle, maximumAngleStep);
        return residualRotation.Slerp(Quaternion.Identity, angleStep / angle);
    }

    /// <summary>
    /// Computes the bounded rigid motion that carries the previous source sample to the current one, in
    /// world space: the source's rotation about its previous origin combined with its origin translation,
    /// each clamped to the held-carry per-tick bound, so the output never steps further than the carried
    /// bound plus the decaying residual. The motion must be the world-frame transform
    /// (<c>current · previous⁻¹</c>) composed left of the carried pose; applying the previous-sample-local
    /// motion (<c>previous⁻¹ · current</c>) as a world carry instead would rotate and skew the carried pose
    /// by the source basis — inverting held output motion for bases far from identity.
    /// </summary>
    private Transform3D ComputeSourceMotion(Transform3D source, double deltaSeconds)
    {
        if (!_hasPreviousSourceTransform)
        {
            return Transform3D.Identity;
        }

        Basis rotationBasis = source.Basis * _previousSourceTransform.Basis.Inverse();
        Vector3 translation = source.Origin - _previousSourceTransform.Origin;

        float maxTranslation = Mathf.Max(0.0f, HeldCarryMaximumSpeedMetresPerSecond) * (float)deltaSeconds;
        translation = ClampDirection(translation, maxTranslation);
        float maxAngle = Mathf.DegToRad(Mathf.Max(0.0f, HeldCarryMaximumAngularSpeedDegreesPerSecond))
            * (float)deltaSeconds;
        Quaternion rotation = ClampRotation(rotationBasis.GetRotationQuaternion(), maxAngle);

        // The clamped world rotation pivots on the previous source origin, so an in-place rotating source
        // carries the held pose about its own position rather than about the world origin.
        Basis carriedRotation = new(rotation);
        Vector3 pivot = _previousSourceTransform.Origin;
        return new Transform3D(carriedRotation, pivot - (carriedRotation * pivot) + translation);
    }

    private static Vector3 ClampDirection(Vector3 direction, float maximumLength)
    {
        float length = direction.Length();
        return length <= maximumLength || length <= Mathf.Epsilon
            ? direction
            : direction * (maximumLength / length);
    }

    private static Quaternion ClampRotation(Quaternion rotation, float maximumAngle)
    {
        float angle = rotation.AngleTo(Quaternion.Identity);
        return angle <= maximumAngle || angle <= Mathf.Epsilon
            ? rotation
            : rotation.Slerp(Quaternion.Identity, 1.0f - (maximumAngle / angle));
    }

    private IKTargetIntent CaptureOutput(IKTargetIntent intent)
    {
        LastOutputIntent = intent;
        return intent;
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
    }

    private float ComputeInterpolationAlpha(double deltaSeconds)
        => deltaSeconds <= MinimumDeltaSeconds
            ? 0.0f
            : Mathf.Clamp(1.0f - Mathf.Exp(-Responsiveness * (float)deltaSeconds), 0.0f, 1.0f);

    private float ComputeReturnAlpha(double deltaSeconds)
        => deltaSeconds <= MinimumDeltaSeconds
            ? 0.0f
            : Mathf.Clamp(1.0f - Mathf.Exp(-ReturnResponsiveness * (float)deltaSeconds), 0.0f, 1.0f);

    private static Transform3D Interpolate(Transform3D from, Transform3D to, float alpha)
        => new(from.Basis.Slerp(to.Basis, alpha).Orthonormalized(), from.Origin.Lerp(to.Origin, alpha));

    private static Transform3D Orthonormalise(Transform3D transform)
        => new(transform.Basis.Orthonormalized(), transform.Origin);
}
