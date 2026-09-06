using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.Observation;
using AlleyCat.Vision;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Mind.Observation;

/// <summary>
/// Unit coverage for the durable relative-position observation contract.
/// </summary>
public sealed class ObservedRelativePositionTests
{
    /// <summary>Relative-position subject identity must be a canonical <c>Type:Id</c> FullId.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("subject")]
    [InlineData("char:")]
    [InlineData(":subject")]
    [InlineData("char:subject:extra")]
    [InlineData("CHAR:subject")]
    [InlineData(" ")]
    public void Constructor_RejectsNonCanonicalSubjectId(string? subjectId)
        => Assert.ThrowsAny<ArgumentException>(
            () => new ObservedRelativePosition(
                subjectId!,
                2.5f,
                RelativeDirection.Front,
                RelativeDirection.Back));

    /// <summary>Distances must be finite, non-negative values.</summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-0.5f)]
    public void Constructor_RejectsNonFiniteOrNegativeDistance(float distance)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new ObservedRelativePosition(
                "char:subject",
                distance,
                RelativeDirection.Front,
                RelativeDirection.Back));

    /// <summary>Valid payloads, including the zero-distance boundary, round-trip through every property.</summary>
    [Fact]
    public void Constructor_AcceptsBoundaryDistanceAndStoresPayload()
    {
        var observation = new ObservedRelativePosition(
            "char:subject",
            0f,
            RelativeDirection.Left,
            RelativeDirection.Right);

        Assert.Equal("char:subject", observation.SubjectId);
        Assert.Equal(0f, observation.Distance);
        Assert.Equal(RelativeDirection.Left, observation.SubjectDirection);
        Assert.Equal(RelativeDirection.Right, observation.ObserverDirection);
    }

    /// <summary>Relative positions use one stable exact semantic key.</summary>
    [Fact]
    public void TypeKey_UsesStableExactValue()
    {
        ObservedRelativePosition observation = CreateObservation();

        Assert.Equal("vision.relative_position", observation.TypeKey);
        Assert.Equal(ObservedRelativePosition.TypeKeyValue, observation.TypeKey);
    }

    /// <summary>Relative-position policy owns equivalent suppression and subject-scoped supersession.</summary>
    [Fact]
    public void LifetimePolicy_SuppressesEquivalentsAndScopesSupersessionToSubject()
    {
        var policy = new RelativePositionObservationLifetimePolicy();
        ObservedRelativePosition observation = CreateObservation();
        var otherSubject = new ObservedRelativePosition(
            "char:other",
            2.5f,
            RelativeDirection.Front,
            RelativeDirection.Back);
        var changed = new ObservedRelativePosition(
            "char:subject",
            3f,
            RelativeDirection.Front,
            RelativeDirection.Back);
        var retained = new AcceptedObservationEntry(
            17,
            10d,
            observation,
            new ObservationSchedulingMetadata(0.1f, false),
            IsRetained: true);

        Assert.True(policy.Evaluate(observation, [retained], 11d).Suppress);
        Assert.False(policy.Evaluate(otherSubject, [retained], 11d).Suppress);
        Assert.Equal([17L], policy.Evaluate(changed, [retained], 11d).SupersededSequenceIDs);
    }

    /// <summary>
    /// Semantic equivalence requires the subject identity and both direction classifications to match exactly while
    /// the distance matches within the float-noise tolerance, ignoring ingestion metadata.
    /// </summary>
    [Fact]
    public void IsSemanticallyEquivalentTo_MatchesFieldsWithinDistanceNoiseTolerance()
    {
        ObservedRelativePosition observation = CreateObservation(observedAt: 1d);
        ObservedRelativePosition identical = CreateObservation(observedAt: 2d);
        // A sub-tolerance delta (5e-5 below the 1e-4 guard) is float arithmetic noise, not a changed position.
        var subToleranceDistance = new ObservedRelativePosition(
            "char:subject",
            2.5f + 5e-5f,
            RelativeDirection.Front,
            RelativeDirection.Back);
        var otherSubject = new ObservedRelativePosition(
            "char:other",
            2.5f,
            RelativeDirection.Front,
            RelativeDirection.Back);
        var otherDistance = new ObservedRelativePosition(
            "char:subject",
            2.6f,
            RelativeDirection.Front,
            RelativeDirection.Back);
        var otherSubjectDirection = new ObservedRelativePosition(
            "char:subject",
            2.5f,
            RelativeDirection.Left,
            RelativeDirection.Back);
        var otherObserverDirection = new ObservedRelativePosition(
            "char:subject",
            2.5f,
            RelativeDirection.Front,
            RelativeDirection.Front);

        Assert.True(observation.IsSemanticallyEquivalentTo(identical));
        Assert.True(observation.IsSemanticallyEquivalentTo(subToleranceDistance));
        Assert.False(observation.IsSemanticallyEquivalentTo(otherSubject));
        Assert.False(observation.IsSemanticallyEquivalentTo(otherDistance));
        Assert.False(observation.IsSemanticallyEquivalentTo(otherSubjectDirection));
        Assert.False(observation.IsSemanticallyEquivalentTo(otherObserverDirection));
        Assert.False(observation.IsSemanticallyEquivalentTo(new ObservedVisualPresence("char:subject")));
    }

    /// <summary>Relative-position importance is one fixed provisional tuning value.</summary>
    [Fact]
    public void CalculateImportance_ReturnsProvisionalTuningValue()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };

        Assert.Equal(0.1f, CreateObservation().CalculateImportance(new ObservationContext(owner)));
    }

    /// <summary>Relative positions never force a fresh reasoning turn.</summary>
    [Fact]
    public void RequiresFreshTurn_NeverForcesFreshTurn()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        ObservationContext context = new(owner);

        Assert.False(CreateObservation().RequiresFreshTurn(context));
    }

    private static ObservedRelativePosition CreateObservation(double? observedAt = null)
    {
        var observation = new ObservedRelativePosition(
            "char:subject",
            2.5f,
            RelativeDirection.Front,
            RelativeDirection.Back);

        return observedAt is null ? observation : observation with
        {
            ObservedAt = observedAt
        };
    }

    private sealed class FakeCharacter : ICharacter
    {
        public string Id { get; set; } = string.Empty;

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }
}
