namespace AlleyCat.Mind.Observation;

/// <summary>
/// Policy-owned retention decision for one candidate observation.
/// </summary>
/// <param name="Suppress">Whether the candidate is equivalent to retained evidence and must not be accepted.</param>
/// <param name="SupersededSequenceIDs">Active retained entries this accepted candidate replaces.</param>
public readonly record struct ObservationLifetimePolicyDecision(
    bool Suppress,
    IReadOnlyList<long> SupersededSequenceIDs)
{
    /// <summary>Accepts the candidate without removing retained evidence.</summary>
    public static ObservationLifetimePolicyDecision Accept { get; } = new(false, []);

    /// <summary>Suppresses the candidate before any acceptance effect occurs.</summary>
    public static ObservationLifetimePolicyDecision Suppressed { get; } = new(true, []);
}

/// <summary>
/// Defines lifetime semantics for exactly one declared concrete observation type.
/// </summary>
/// <remarks>
/// Policies own their matching, equivalence, supersession, expiry, and event-eligibility semantics. Mind only applies
/// the resulting decision atomically; it does not infer subject scopes or type-specific equality.
/// </remarks>
public interface IObservationLifetimePolicy
{
    /// <summary>Concrete observation type this policy declares.</summary>
    Type DeclaredConcreteType
    {
        get;
    }

    /// <summary>Determines acceptance and retained-entry replacement for one candidate.</summary>
    ObservationLifetimePolicyDecision Evaluate(
        Observation candidate,
        IReadOnlyList<AcceptedObservationEntry> activeEntries,
        double observedAtSeconds);

    /// <summary>Determines whether an accepted observation belongs in the persistent event timeline.</summary>
    bool IsEventEligible(Observation observation);

    /// <summary>Gets whether active entries governed by this policy can expire without supersession.</summary>
    bool HasFiniteLifetime
    {
        get;
    }

    /// <summary>Determines whether one active retained entry has expired at game time <paramref name="nowSeconds" />.</summary>
    bool IsExpired(AcceptedObservationEntry entry, double nowSeconds);
}

/// <summary>Never-expiring policy for an explicitly declared concrete observation type.</summary>
public sealed class NeverExpireObservationLifetimePolicy<TObservation>(bool eventEligible) : IObservationLifetimePolicy
    where TObservation : Observation
{
    /// <inheritdoc />
    public Type DeclaredConcreteType => typeof(TObservation);

    /// <inheritdoc />
    public ObservationLifetimePolicyDecision Evaluate(
        Observation candidate,
        IReadOnlyList<AcceptedObservationEntry> activeEntries,
        double observedAtSeconds)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(activeEntries);
        return ObservationLifetimePolicyDecision.Accept;
    }

    /// <inheritdoc />
    public bool IsEventEligible(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return eventEligible;
    }

    /// <inheritdoc />
    public bool HasFiniteLifetime => false;

    /// <inheritdoc />
    public bool IsExpired(AcceptedObservationEntry entry, double nowSeconds)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return false;
    }
}

/// <summary>Policy for focused visual descriptions: equal descriptions suppress and changed descriptions supersede.</summary>
public sealed class VisualDescriptionObservationLifetimePolicy : IObservationLifetimePolicy
{
    /// <inheritdoc />
    public Type DeclaredConcreteType => typeof(ObservedVisualDescription);

    /// <inheritdoc />
    public ObservationLifetimePolicyDecision Evaluate(
        Observation candidate,
        IReadOnlyList<AcceptedObservationEntry> activeEntries,
        double observedAtSeconds)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(activeEntries);
        ObservedVisualDescription description = candidate as ObservedVisualDescription
            ?? throw new ArgumentException("Visual-description policy received an incompatible observation.", nameof(candidate));

        List<long> superseded = [];
        foreach (AcceptedObservationEntry entry in activeEntries)
        {
            if (entry.Payload is not ObservedVisualDescription retained
                || !string.Equals(retained.SubjectId, description.SubjectId, StringComparison.Ordinal))
            {
                continue;
            }

            if (description.IsSemanticallyEquivalentTo(retained))
            {
                return ObservationLifetimePolicyDecision.Suppressed;
            }

            superseded.Add(entry.SequenceID);
        }

        return superseded.Count == 0
            ? ObservationLifetimePolicyDecision.Accept
            : new ObservationLifetimePolicyDecision(false, superseded);
    }

    /// <inheritdoc />
    public bool IsEventEligible(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return false;
    }

    /// <inheritdoc />
    public bool HasFiniteLifetime => false;

    /// <inheritdoc />
    public bool IsExpired(AcceptedObservationEntry entry, double nowSeconds)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return false;
    }
}

/// <summary>
/// Policy for focused relative-position evidence: equal samples suppress, changed samples supersede, and active
/// evidence expires after a finite game-time window.
/// </summary>
public sealed class RelativePositionObservationLifetimePolicy : IObservationLifetimePolicy
{
    /// <summary>Initial finite evidence lifetime; tuning remains a policy concern.</summary>
    public const double DefaultEvidenceLifetimeSeconds = 2d;

    /// <summary>Creates the policy with a finite, positive game-time evidence window.</summary>
    public RelativePositionObservationLifetimePolicy(double evidenceLifetimeSeconds = DefaultEvidenceLifetimeSeconds)
    {
        if (!double.IsFinite(evidenceLifetimeSeconds) || evidenceLifetimeSeconds <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(evidenceLifetimeSeconds),
                evidenceLifetimeSeconds,
                "Relative-position evidence lifetime must be finite and greater than zero.");
        }

        EvidenceLifetimeSeconds = evidenceLifetimeSeconds;
    }

    /// <summary>Finite game-time duration for active relative-position evidence.</summary>
    public double EvidenceLifetimeSeconds
    {
        get;
    }

    /// <inheritdoc />
    public Type DeclaredConcreteType => typeof(ObservedRelativePosition);

    /// <inheritdoc />
    public ObservationLifetimePolicyDecision Evaluate(
        Observation candidate,
        IReadOnlyList<AcceptedObservationEntry> activeEntries,
        double observedAtSeconds)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(activeEntries);
        ObservedRelativePosition position = candidate as ObservedRelativePosition
            ?? throw new ArgumentException("Relative-position policy received an incompatible observation.", nameof(candidate));

        List<long> superseded = [];
        foreach (AcceptedObservationEntry entry in activeEntries)
        {
            if (entry.Payload is not ObservedRelativePosition retained
                || !string.Equals(retained.SubjectId, position.SubjectId, StringComparison.Ordinal))
            {
                continue;
            }

            if (position.IsSemanticallyEquivalentTo(retained))
            {
                return ObservationLifetimePolicyDecision.Suppressed;
            }

            superseded.Add(entry.SequenceID);
        }

        return superseded.Count == 0
            ? ObservationLifetimePolicyDecision.Accept
            : new ObservationLifetimePolicyDecision(false, superseded);
    }

    /// <inheritdoc />
    public bool IsEventEligible(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return false;
    }

    /// <inheritdoc />
    public bool HasFiniteLifetime => true;

    /// <inheritdoc />
    public bool IsExpired(AcceptedObservationEntry entry, double nowSeconds)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return nowSeconds >= entry.ObservedAt + EvidenceLifetimeSeconds;
    }
}

/// <summary>
/// Creates the initial concrete policy set without teaching Mind's source-neutral ingestion about any observation
/// modality. New concrete observation types must be registered here when their initial semantics differ from the
/// extensible fallback.
/// </summary>
internal static class InitialObservationLifetimePolicies
{
    /// <summary>Creates one policy for every initial concrete observation contract.</summary>
    internal static IEnumerable<IObservationLifetimePolicy> Create()
        =>
        [
            new NeverExpireObservationLifetimePolicy<ObservedSpeech>(eventEligible: true),
            new VisualDescriptionObservationLifetimePolicy(),
            new RelativePositionObservationLifetimePolicy(),
        ];
}

/// <summary>Concrete-type registry used by Mind to resolve observation lifetime policies.</summary>
public sealed class ObservationLifetimePolicyRegistry
{
    private static readonly IObservationLifetimePolicy _defaultPolicy =
        new NeverExpireObservationLifetimePolicy<Observation>(eventEligible: true);

    private readonly IReadOnlyDictionary<Type, IObservationLifetimePolicy> _policies;

    /// <summary>Validates and registers exactly one policy for every configured declared concrete type.</summary>
    public ObservationLifetimePolicyRegistry(IEnumerable<IObservationLifetimePolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        Dictionary<Type, IObservationLifetimePolicy> configured = [];
        foreach (IObservationLifetimePolicy policy in policies)
        {
            ArgumentNullException.ThrowIfNull(policy);
            Type declaredType = policy.DeclaredConcreteType
                ?? throw new InvalidOperationException("Observation lifetime policy declared a null observation type.");
            if (declaredType.IsAbstract
                || declaredType.IsInterface
                || !typeof(Observation).IsAssignableFrom(declaredType))
            {
                throw new InvalidOperationException(
                    $"Observation lifetime policy must declare one concrete {nameof(Observation)} type, not '{declaredType.FullName}'.");
            }

            if (!configured.TryAdd(declaredType, policy))
            {
                throw new InvalidOperationException(
                    $"Mind cannot activate duplicate lifetime policies for declared observation type '{declaredType.FullName}'.");
            }
        }

        _policies = configured;
    }

    /// <summary>Resolves exact runtime type policy, with a never-expiring event-eligible fallback for undeclared types.</summary>
    public IObservationLifetimePolicy Resolve(Type runtimeType)
    {
        ArgumentNullException.ThrowIfNull(runtimeType);
        return _policies.TryGetValue(runtimeType, out IObservationLifetimePolicy? policy) ? policy : _defaultPolicy;
    }
}
