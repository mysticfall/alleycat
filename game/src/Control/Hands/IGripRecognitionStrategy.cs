using AlleyCat.Rigging;
using AlleyCat.XR.HandTracking;
using Godot;

namespace AlleyCat.Control.Hands;

/// <summary>
/// Generic recognition evaluation consumed by the hysteresis state machine: the aggregate grip score and the
/// validity verdict (XR-002 TR48; CTRL-002 TR12-TR14).
/// </summary>
/// <param name="Score">
/// Aggregate grip progress — 0 at the calibrated neutral, 1 at the reference articulation, above 1 for
/// over-clench, signed negative when opening away.
/// </param>
/// <param name="SufficientValidity">
/// Whether enough featured weight is live-valid; when false the score must not drive any recognition decision
/// (fail closed).
/// </param>
public readonly record struct GripRecognitionEvaluation(float Score, bool SufficientValidity);

/// <summary>Marker for one candidate animation's derived gesture profile.</summary>
public interface IGripRecognitionProfile
{
    /// <summary>Hand side the profile articulates.</summary>
    LimbSide Side
    {
        get;
    }

    /// <summary>Identifier of the strategy that derived — and must evaluate — this profile.</summary>
    string StrategyName
    {
        get;
    }

    /// <summary>Recognition thresholds and validity settings belonging to the strategy-derived profile.</summary>
    PowerGripRecognitionSettings Settings
    {
        get;
    }
}

/// <summary>
/// Strategy seam for candidate-keyed gesture recognition (XR-002 TR50; CTRL-002 TR22; INTR-001 TR17): derives a
/// gesture profile from a candidate animation's sampled side reference and evaluates live projected poses
/// against it. The animation-derived power grip is the only implementation this increment; precision/pinch
/// recognition is deferred and must be addable here without structural change — recognition never branches on
/// concrete grab-point classes.
/// </summary>
public interface IGripRecognitionStrategy
{
    /// <summary>Stable identifier used for candidate-keyed strategy selection.</summary>
    string Name
    {
        get;
    }

    /// <summary>
    /// Immutable, same-instance recognition policy supplied by this strategy for every profile it derives. The current
    /// extensibility seam deliberately uses <see cref="PowerGripRecognitionSettings" /> because the
    /// animation-derived power grip is the only shipped recogniser and owns the validation and derivation
    /// settings. The coordinator treats this as an opaque compatible policy: a future recogniser that needs a
    /// different policy type requires its own approved contract rather than a power-grip fallback.
    /// </summary>
    PowerGripRecognitionSettings RecognitionSettings
    {
        get;
    }

    /// <summary>
    /// Derives one side's gesture profile from its sampled destination-local reference pose and the effective
    /// neutrals the shared projection uses.
    /// </summary>
    /// <param name="reference">The side's sampled reference pose.</param>
    /// <param name="sideEffectiveNeutrals">
    /// The side's 15 effective neutrals in canonical destination order — the same <c>N_j</c> the projection
    /// seam consumes.
    /// </param>
    /// <param name="settings">The strategy-owned recognition settings honoured by derivation and evaluation.</param>
    /// <param name="profile">The derived profile when derivation succeeds.</param>
    /// <param name="error">Actionable failure identifying the side and violated rule.</param>
    bool TryDeriveProfile(
        AuthoredHandPoseSideReference reference,
        ReadOnlySpan<Quaternion> sideEffectiveNeutrals,
        PowerGripRecognitionSettings settings,
        out IGripRecognitionProfile profile,
        out string error);

    /// <summary>
    /// Evaluates one side's live projected pose against a profile this strategy derived.
    /// </summary>
    /// <param name="profile">A profile produced by this strategy.</param>
    /// <param name="liveDestinationPoses">
    /// The side's 15 projected poses in canonical destination order — from
    /// <see cref="OpticalFingerProjectionBinding.TryProject" />.
    /// </param>
    /// <param name="evaluation">The aggregate evaluation.</param>
    bool TryEvaluate(
        IGripRecognitionProfile profile,
        ReadOnlySpan<OpticalFingerProjectedPose> liveDestinationPoses,
        out GripRecognitionEvaluation evaluation);
}

/// <summary>
/// Candidate-keyed strategy resolution (INTR-001 TR17): the animation-derived power grip is the only registered
/// strategy this increment. Resolving anything else fails explicitly — there is no silent fallback to power
/// grip or any default.
/// </summary>
public interface IGripRecognitionStrategyResolver
{
    /// <summary>Resolves the strategy selected by a validated candidate.</summary>
    bool TryResolve(string name, out IGripRecognitionStrategy strategy, out string error);
}

/// <summary>Production strategy registry and default resolver for candidate-keyed recognition.</summary>
public static class GripRecognitionStrategies
{
    /// <summary>Identifier of the animation-derived power-grip strategy.</summary>
    public const string PowerGrip = "power-grip";

    /// <summary>Production resolver containing the shipped animation-derived power grip.</summary>
    public static IGripRecognitionStrategyResolver Default
    {
        get;
    } = new GripRecognitionStrategyResolver([new PowerGripRecognitionStrategy()]);

    /// <summary>Convenience production resolution for legacy non-coordinator callers.</summary>
    public static bool TryResolve(string name, out IGripRecognitionStrategy strategy, out string error)
        => Default.TryResolve(name, out strategy, out error);
}

/// <summary>Dictionary-backed candidate strategy resolver used by production and focused future-strategy tests.</summary>
public sealed class GripRecognitionStrategyResolver : IGripRecognitionStrategyResolver
{
    private readonly IReadOnlyDictionary<string, IGripRecognitionStrategy> _strategies;

    /// <summary>Creates a resolver from an explicit strategy set, enabling future strategy integration tests.</summary>
    public GripRecognitionStrategyResolver(IEnumerable<IGripRecognitionStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        _strategies = strategies.ToDictionary(strategy => strategy.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Resolves a recognition strategy by identifier. Unknown identifiers fail explicitly with an actionable
    /// error naming the requested strategy and the single registered one; precision/pinch remains deferred
    /// (XR-002 TR50).
    /// </summary>
    public bool TryResolve(string name, out IGripRecognitionStrategy strategy, out string error)
    {
        if (_strategies.TryGetValue(name, out strategy!))
        {
            error = string.Empty;
            return true;
        }

        strategy = null!;
        error = $"Unknown grip recognition strategy '{name ?? "<null>"}'; registered strategies are " +
            $"{string.Join(", ", _strategies.Keys)} and no fallback is permitted.";
        return false;
    }

}
