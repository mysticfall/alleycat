using AlleyCat.Navigation;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Navigation;

/// <summary>
/// Deterministic coverage for monotonic forward progress projected onto sampled navigation paths.
/// </summary>
public sealed class NavigationTravelProgressTests
{
    private static readonly Vector3[] _straightPath = [Vector3.Zero, new(10.0f, 0.0f, 0.0f)];

    /// <inheritdoc/>
    [Fact]
    public void Sample_PerpendicularAndBackwardMovement_AddsNoProgress()
    {
        NavigationTravelProgress progress = new();
        progress.Start(Vector3.Zero, _straightPath, 1);

        float perpendicular = progress.Sample(new Vector3(0.0f, 0.0f, 2.0f), _straightPath, 1);
        float forward = progress.Sample(new Vector3(2.0f, 0.0f, 0.0f), _straightPath, 1);
        float backward = progress.Sample(new Vector3(1.0f, 0.0f, 0.0f), _straightPath, 1);
        float returnedToHighWater = progress.Sample(new Vector3(2.0f, 0.0f, 0.0f), _straightPath, 1);

        Assert.Equal(0.0f, perpendicular);
        Assert.Equal(2.0f, forward, 5);
        Assert.Equal(forward, backward);
        Assert.Equal(backward, returnedToHighWater);
    }

    /// <inheritdoc/>
    [Fact]
    public void Sample_StraightForwardMovement_AddsProjectedPathDistance()
    {
        NavigationTravelProgress progress = new();
        progress.Start(new Vector3(1.0f, 0.0f, 3.0f), _straightPath, 1);

        float travelled = progress.Sample(new Vector3(4.5f, 0.0f, -2.0f), _straightPath, 1);

        Assert.Equal(3.5f, travelled, 5);
    }

    /// <inheritdoc/>
    [Fact]
    public void Sample_CornerAndPathIndexTransition_AddsArcDistanceAcrossBothSegments()
    {
        Vector3[] cornerPath = [
            Vector3.Zero,
            new Vector3(2.0f, 0.0f, 0.0f),
            new Vector3(2.0f, 0.0f, 3.0f),
        ];
        NavigationTravelProgress progress = new();
        progress.Start(new Vector3(1.0f, 0.0f, 0.0f), cornerPath, 1);

        float travelled = progress.Sample(new Vector3(2.0f, 0.0f, 1.5f), cornerPath, 2);

        Assert.Equal(2.5f, travelled, 5);
    }

    /// <inheritdoc/>
    [Fact]
    public void Sample_NoMotionPublicationAndLongerOrShorterReplans_DoNotChangeCommittedProgress()
    {
        NavigationTravelProgress progress = new();
        progress.Start(Vector3.Zero, _straightPath, 1);
        float forward = progress.Sample(new Vector3(2.0f, 0.0f, 0.0f), _straightPath, 1);
        Vector3[] published = [Vector3.Zero, new Vector3(5.0f, 0.0f, 0.0f), _straightPath[^1]];
        float publication = progress.Sample(new Vector3(2.0f, 0.0f, 0.0f), published, 1);
        Vector3[] longer = [
            Vector3.Zero,
            new Vector3(2.0f, 0.0f, 0.0f),
            new Vector3(2.0f, 0.0f, 4.0f),
            _straightPath[^1],
        ];
        float longerReplan = progress.Sample(new Vector3(2.0f, 0.0f, 0.0f), longer, 2);
        Vector3[] shorter = [new Vector3(2.0f, 0.0f, 0.0f), _straightPath[^1]];
        float shorterReplan = progress.Sample(new Vector3(2.0f, 0.0f, 0.0f), shorter, 1);

        Assert.Equal(2.0f, forward, 5);
        Assert.Equal(forward, publication);
        Assert.Equal(publication, longerReplan);
        Assert.Equal(longerReplan, shorterReplan);
    }

    /// <inheritdoc/>
    [Fact]
    public void Sample_DegenerateAndNonFiniteSamples_RemainFiniteWithoutDeferredProgress()
    {
        NavigationTravelProgress progress = new();
        progress.Start(Vector3.Zero, _straightPath, 1);
        float valid = progress.Sample(new Vector3(1.0f, 0.0f, 0.0f), _straightPath, 1);
        Vector3[] degenerate = [Vector3.Zero, Vector3.Zero, new(float.NaN, 0.0f, 0.0f)];
        float invalidPath = progress.Sample(new Vector3(2.0f, 0.0f, 0.0f), degenerate, 1);
        float invalidActor = progress.Sample(new Vector3(float.NaN, 0.0f, 0.0f), _straightPath, 1);
        float reanchored = progress.Sample(new Vector3(3.0f, 0.0f, 0.0f), _straightPath, 1);
        float resumed = progress.Sample(new Vector3(4.0f, 0.0f, 0.0f), _straightPath, 1);

        Assert.Equal(1.0f, valid, 5);
        Assert.Equal(valid, invalidPath);
        Assert.Equal(invalidPath, invalidActor);
        Assert.Equal(invalidActor, reanchored);
        Assert.Equal(reanchored + 1.0f, resumed, 5);
        Assert.True(float.IsFinite(resumed));
    }
}
