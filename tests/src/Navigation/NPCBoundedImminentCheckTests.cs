using AlleyCat.Navigation;
using Xunit;

namespace AlleyCat.Tests.Navigation;

/// <summary>Focused NAV-TAC-002 coverage for fixed-size imminent evidence and retry gating.</summary>
public sealed class NPCBoundedImminentCheckTests
{
    private static readonly NPCFarRouteEstimateKey _key = new(new(7, 2), 3, 5, 11);

    /// <inheritdoc/>
    [Fact]
    public void RejectedPairRetriesOnlyAfterOccurrenceOrGenerationChanges()
    {
        var gate = new NPCImminentCheckRetryGate();
        NPCImminentCheckEvidence evidence = CreateEvidence(4, 4);

        Assert.True(gate.ShouldCheck(evidence));
        gate.RecordRejected(evidence);

        Assert.False(gate.ShouldCheck(evidence));
        Assert.True(gate.ShouldCheck(CreateEvidence(5, 4)));
        Assert.True(gate.ShouldCheck(evidence with
        {
            EstimateKey = _key with
            {
                SustainedClockGeneration = 6
            },
        }));
    }

    /// <inheritdoc/>
    [Fact]
    public void EvidenceContainsOnlyNextSameAndOppositeOccurrences()
    {
        NPCImminentCheckEvidence evidence = CreateEvidence(21, 22);

        Assert.Equal(21, evidence.SameLaneOccurrence.CycleOrdinal);
        Assert.Equal(NPCAnimationPhaseLane.Left, evidence.SameLaneOccurrence.Lane);
        Assert.Equal("release_l", evidence.SameLaneOccurrence.ReleaseEvent);
        Assert.Equal(22, evidence.OppositeLaneOccurrence.CycleOrdinal);
        Assert.Equal(NPCAnimationPhaseLane.Right, evidence.OppositeLaneOccurrence.Lane);
        Assert.Equal("release_r", evidence.OppositeLaneOccurrence.ReleaseEvent);
        Assert.Equal(2, typeof(NPCImminentCheckEvidence)
            .GetProperties()
            .Count(property => property.PropertyType == typeof(NPCReleaseOccurrence)));
    }

    /// <inheritdoc/>
    [Fact]
    public void ProductionNavigationOwnsEstimatorAndNoRecurrenceBuffers()
    {
        System.Reflection.FieldInfo[] fields = typeof(NPCNavigation).GetFields(
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic);

        Assert.Contains(fields, field => field.FieldType == typeof(NPCFarRouteStoppingEstimator));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(NPCReleaseOccurrence[]));
        Assert.DoesNotContain(fields, field => field.Name.Contains("Recurrence", StringComparison.Ordinal));
    }

    private static NPCImminentCheckEvidence CreateEvidence(long sameOrdinal, long oppositeOrdinal)
        => new(
            _key,
            new(5, sameOrdinal, 0.25f, NPCAnimationPhaseLane.Left, "release_l"),
            new(5, oppositeOrdinal, 0.75f, NPCAnimationPhaseLane.Right, "release_r"));
}
