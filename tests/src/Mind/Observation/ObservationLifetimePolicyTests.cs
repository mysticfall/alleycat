using AlleyCat.Mind.Observation;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Tests.Mind.Observation;

/// <summary>Unit coverage for concrete-type lifetime policy registration and policy-owned retention semantics.</summary>
public sealed class ObservationLifetimePolicyTests
{
    /// <summary>Duplicate concrete registrations fail deterministically before Mind activation.</summary>
    [Fact]
    public void Registry_DuplicateDeclaredConcreteType_ThrowsActivationFailure()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => new ObservationLifetimePolicyRegistry(
            [
                new NeverExpireObservationLifetimePolicy<TestObservation>(eventEligible: true),
                new NeverExpireObservationLifetimePolicy<TestObservation>(eventEligible: false),
            ]));

        Assert.Contains("duplicate lifetime policies", exception.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(TestObservation).FullName!, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Undeclared observation types retain the extensible never-expire, event-eligible default.</summary>
    [Fact]
    public void Registry_UndeclaredRuntimeType_UsesNeverExpireEventEligibleFallback()
    {
        var registry = new ObservationLifetimePolicyRegistry([]);
        var observation = new TestObservation("future.event");
        var entry = new AcceptedObservationEntry(
            1,
            1d,
            observation,
            new ObservationSchedulingMetadata(0f, false),
            IsRetained: true);

        IObservationLifetimePolicy policy = registry.Resolve(observation.GetType());

        Assert.True(policy.IsEventEligible(observation));
        Assert.False(policy.IsExpired(entry, double.MaxValue));
        Assert.False(policy.Evaluate(observation, [entry], 2d).Suppress);
    }

    /// <summary>Initial speech registers its explicit persistent policy rather than relying on the extensible fallback.</summary>
    [Fact]
    public void InitialPolicies_DeclareSpeechAsPersistentEvent()
    {
        var registry = new ObservationLifetimePolicyRegistry(InitialObservationLifetimePolicies.Create());
        var speech = new ObservedSpeech("char:other", "hello");

        Assert.Equal(typeof(ObservedSpeech), registry.Resolve(typeof(ObservedSpeech)).DeclaredConcreteType);
        Assert.True(registry.Resolve(typeof(ObservedSpeech)).IsEventEligible(speech));
    }

    /// <summary>Visual-description equivalence and supersession remain policy-owned.</summary>
    [Fact]
    public void VisualDescriptionPolicy_OwnsEqualSuppressionAndChangedSupersession()
    {
        var policy = new VisualDescriptionObservationLifetimePolicy();
        var retained = new AcceptedObservationEntry(
            41,
            10d,
            new ObservedVisualDescription("char:subject", "red coat"),
            new ObservationSchedulingMetadata(0.1f, false),
            IsRetained: true);

        ObservationLifetimePolicyDecision equal = policy.Evaluate(
            new ObservedVisualDescription("char:subject", "red coat"),
            [retained],
            11d);
        ObservationLifetimePolicyDecision changed = policy.Evaluate(
            new ObservedVisualDescription("char:subject", "blue coat"),
            [retained],
            11d);

        Assert.True(equal.Suppress);
        Assert.Empty(equal.SupersededSequenceIDs);
        Assert.False(changed.Suppress);
        Assert.Equal([41L], changed.SupersededSequenceIDs);
        Assert.False(policy.IsEventEligible(retained.Payload));
    }

    /// <summary>Relative-position evidence expiry uses a deterministic inclusive game-time boundary.</summary>
    [Fact]
    public void RelativePositionPolicy_ExpiresAtFiniteGameTimeBoundary()
    {
        var policy = new RelativePositionObservationLifetimePolicy(2d);
        var entry = new AcceptedObservationEntry(
            1,
            10d,
            new ObservedRelativePosition("char:subject", 1f, RelativeDirection.Front, RelativeDirection.Front),
            new ObservationSchedulingMetadata(0.1f, false),
            IsRetained: true);

        Assert.False(policy.IsExpired(entry, 11.999d));
        Assert.True(policy.IsExpired(entry, 12d));
        Assert.False(policy.IsEventEligible(entry.Payload));
    }

    private sealed record TestObservation(string Key) : AgentObservation
    {
        public override string TypeKey => Key;

        public override float CalculateImportance(ObservationContext context) => 0f;
    }
}
