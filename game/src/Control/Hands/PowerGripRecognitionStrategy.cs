using AlleyCat.XR.HandTracking;
using Godot;

namespace AlleyCat.Control.Hands;

/// <summary>
/// The animation-derived power-grip strategy (XR-002 TR48; CTRL-002 TR12-TR13, TR22): derives
/// <see cref="PowerGripProfile" /> from a candidate animation's sampled side reference and evaluates live
/// projected poses through the pure <see cref="PowerGripRecognition" /> aggregate. The only registered
/// strategy this increment.
/// </summary>
/// <remarks>Creates the power-grip strategy with its immutable compatible recognition policy.</remarks>
public sealed class PowerGripRecognitionStrategy(PowerGripRecognitionSettings? recognitionSettings = null) : IGripRecognitionStrategy
{
    private const int DestinationCount = 15;

    private readonly float[] _destinationProgress = new float[DestinationCount];
    private readonly float[] _chainProgress = new float[PowerGripRecognition.ChainCount];

    /// <inheritdoc />
    public string Name => GripRecognitionStrategies.PowerGrip;

    /// <inheritdoc />
    public PowerGripRecognitionSettings RecognitionSettings { get; } = recognitionSettings ?? PowerGripRecognitionSettings.Default;

    /// <inheritdoc />
    public bool TryDeriveProfile(
        AuthoredHandPoseSideReference reference,
        ReadOnlySpan<Quaternion> sideEffectiveNeutrals,
        PowerGripRecognitionSettings settings,
        out IGripRecognitionProfile profile,
        out string error)
    {
        if (PowerGripProfile.TryDerive(reference, sideEffectiveNeutrals, settings, out PowerGripProfile? derived, out error))
        {
            profile = derived;
            return true;
        }

        profile = null!;
        return false;
    }

    /// <inheritdoc />
    public bool TryEvaluate(
        IGripRecognitionProfile profile,
        ReadOnlySpan<OpticalFingerProjectedPose> liveDestinationPoses,
        out GripRecognitionEvaluation evaluation)
    {
        if (profile is not PowerGripProfile powerGripProfile)
        {
            evaluation = default;
            return false;
        }

        if (!PowerGripRecognition.TryEvaluate(
                in powerGripProfile,
                liveDestinationPoses,
                _destinationProgress,
                _chainProgress,
                out float score,
                out bool sufficientValidity))
        {
            evaluation = default;
            return false;
        }

        evaluation = new GripRecognitionEvaluation(score, sufficientValidity);
        return true;
    }
}

/// <summary>Recognition edge emitted by the grip state machine (XR-002 TR51; CTRL-002 TR14).</summary>
public enum GripEdge
{
    /// <summary>No edge this evaluation.</summary>
    None = 0,

    /// <summary>The grab threshold held continuously for the stability interval while not grabbed.</summary>
    Grab = 1,

    /// <summary>The release threshold held continuously for the stability interval while grabbed.</summary>
    Release = 2,
}

/// <summary>
/// Hysteresis and stability state machine over grip evaluations (XR-002 TR48; CTRL-002 TR12, TR14): distinct
/// grab and release thresholds — release strictly more open, both relative to the candidate animation's
/// articulation — with a stability interval that requires the threshold condition to hold continuously before
/// an edge is emitted.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Semantics.</strong> While not grabbed, an aggregate at or above the grab threshold accumulates
/// stability and finally emits <see cref="GripEdge.Grab" />; while grabbed, an aggregate at or below the
/// release threshold emits <see cref="GripEdge.Release" />. Between the thresholds nothing happens —
/// over-clench stays grabbed and an intermediate pose cannot chatter. Any interrupt (condition break,
/// candidate-identity change, or an evaluation with insufficient validity) resets the accumulation to zero;
/// insufficient validity never emits a synthetic edge — the machine fail-closes and the caller's lifecycle
/// owns pause semantics (XR-002 TR51; CTRL-002 TR15).
/// </para>
/// <para>
/// The machine emits edges and tracks its grabbed state only; mapping edges onto the grab lifecycle
/// (approach, pending cancel, held release, provenance) belongs to the coordinator that consumes it.
/// </para>
/// </remarks>
public sealed class GripRecognitionStateMachine
{
    private PowerGripRecognitionSettings _settings;

    private IGripRecognitionStrategy? _strategy;

    private object? _candidateIdentity;

    /// <summary>Whether a grab edge has been emitted and no release edge has followed.</summary>
    public bool IsGrabRecognised
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the continuous duration accumulated for the currently pending recognition edge. This is a read-only
    /// diagnostic for runtime measurement; it does not affect edge evaluation.
    /// </summary>
    public float StabilitySeconds
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the threshold edge currently accumulating stability, or <see cref="GripEdge.None" /> when no
    /// threshold condition is active. This is a read-only diagnostic for runtime measurement.
    /// </summary>
    public GripEdge PendingEdge
    {
        get;
        private set;
    }

    /// <summary>
    /// Forces the grabbed latch to <paramref name="isGrabRecognised" /> and clears any pending edge
    /// accumulation — the coordinator's reconciliation hook for externally driven lifecycle changes (forced
    /// cancellation or release, explicit mode switches, pause boundaries) and for suppressing post-pause edge
    /// bursts (CTRL-002 TR6, TR8, TR18). A fresh stability interval is always required after a reset.
    /// </summary>
    /// <param name="isGrabRecognised">Whether a grab intent is currently active on the consuming hand.</param>
    public void Reset(bool isGrabRecognised)
    {
        IsGrabRecognised = isGrabRecognised;
        ResetAccumulation();
    }

    /// <summary>Creates a machine bound to one settings record; thresholds come from the candidate's profile settings.</summary>
    public GripRecognitionStateMachine(PowerGripRecognitionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>
    /// Applies the resolved profile's strategy and settings. A change cannot inherit stability accumulated under
    /// another strategy/configuration, so it resets the recognition boundary without broad per-tick invalidation.
    /// </summary>
    public void Configure(IGripRecognitionStrategy strategy, PowerGripRecognitionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(settings);
        if (ReferenceEquals(_strategy, strategy) && ReferenceEquals(_settings, settings))
        {
            return;
        }

        _strategy = strategy;
        _settings = settings;
        Reset(IsGrabRecognised);
    }

    /// <summary>
    /// Advances the state machine by one evaluation. <paramref name="deltaSeconds" /> accumulates the elapsed
    /// time since the previous evaluation; negative deltas are clamped to zero.
    /// </summary>
    /// <param name="evaluation">The current aggregate evaluation.</param>
    /// <param name="deltaSeconds">Elapsed seconds since the previous evaluation.</param>
    /// <param name="candidateIdentity">
    /// Identity of the observed candidate — when it changes, stability accumulation resets without locking a
    /// speculative candidate (XR-002 TR47; CTRL-002 TR11).
    /// </param>
    /// <returns>The emitted edge, if any.</returns>
    public GripEdge Evaluate(in GripRecognitionEvaluation evaluation, float deltaSeconds, object? candidateIdentity)
    {
        if (!evaluation.SufficientValidity)
        {
            // Fail closed: no recognition on insufficient validity, and no synthetic release either — the
            // coordinator owns pause semantics (XR-002 TR48, TR51).
            ResetAccumulation();
            return GripEdge.None;
        }

        if (!Equals(candidateIdentity, _candidateIdentity))
        {
            _candidateIdentity = candidateIdentity;
            ResetAccumulation();
        }

        GripEdge? desired = IsGrabRecognised
            ? evaluation.Score <= _settings.ReleaseThreshold ? GripEdge.Release : null
            : evaluation.Score >= _settings.GrabThreshold ? GripEdge.Grab : null;
        if (desired is null)
        {
            // No threshold condition holds — between the thresholds nothing happens, preserving hysteresis.
            ResetAccumulation();
            return GripEdge.None;
        }

        if (PendingEdge != desired.Value)
        {
            PendingEdge = desired.Value;
            StabilitySeconds = 0.0f;
        }

        StabilitySeconds += Math.Max(deltaSeconds, 0.0f);
        if (StabilitySeconds < _settings.StabilitySeconds)
        {
            return GripEdge.None;
        }

        IsGrabRecognised = desired.Value == GripEdge.Grab;
        ResetAccumulation();
        return desired.Value;
    }

    private void ResetAccumulation()
    {
        PendingEdge = GripEdge.None;
        StabilitySeconds = 0.0f;
    }
}
