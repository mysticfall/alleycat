using System.Collections.Immutable;
using AlleyCat.Control.Locomotion;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.TestFramework;
using Xunit;

namespace AlleyCat.IntegrationTests.AnimationCatalogue;

/// <summary>
/// Real selected-content checks for ANIM-002 per-foot planted intervals: the delivered locomotion package file and
/// the delivered idle catalogue field load through the runtime reader, bind to their package's character, clips, and
/// runtime library, cover exactly their package clips with both feet, and declare the role-level structure the
/// derivation policy requires. Production timings are not used as oracles.
/// </summary>
[Headless]
public sealed class PerFootPlantedIntervalsPackageIntegrationTests
{
    /// <inheritdoc/>
    [Fact]
    public void DeliveredLocomotionPackage_CoversEveryClipWithBothFeet()
    {
        StandingLocomotionCatalogue catalogue = ProductionContentBindings.ParseReferenceFemaleLocomotionCatalogue();
        string cataloguePath = ProductionContentBindings.ResolveReferenceFemaleLocomotionCataloguePath();
        string intervalsPath = $"{cataloguePath[..(cataloguePath.LastIndexOf('/') + 1)]}{PerFootPlantedIntervals.FileName}";

        var intervals = PerFootPlantedIntervals.Load(intervalsPath);

        intervals.ValidateAgainst(catalogue);
        foreach (StandingLocomotionClip clip in catalogue.Clips.Values)
        {
            PlantedClipIntervals clipIntervals = intervals[clip.Identity];
            foreach (StandingLocomotionAnatomicalSide foot in Enum.GetValues<StandingLocomotionAnatomicalSide>())
            {
                ImmutableArray<PlantedIntervalSegment> segments = clipIntervals.SegmentsFor(foot);
                Assert.True(segments.Length > 0, $"{clip.Identity} {foot} declares no segments.");
                Assert.Equal(0d, segments[0].StartSeconds);
                Assert.Equal(clip.DurationSeconds, segments[^1].EndSeconds, 6);
                Assert.True(
                    segments.Any(segment => segment.IsPlanted),
                    $"{clip.Identity} never plants the {foot} foot.");
                Assert.True(
                    segments.Any(segment => !segment.IsPlanted),
                    $"{clip.Identity} never swings the {foot} foot.");
            }

            if (clip.BoundaryKind == StandingLocomotionBoundaryKind.CyclicTravel)
            {
                AssertAlternatingCycle(clipIntervals);
            }
        }
    }

    /// <inheritdoc/>
    [Fact]
    public void DeliveredIdleCatalogue_PlantsBothFeetThroughout()
    {
        var intervals = PerFootPlantedIntervals.LoadValidatedIdleCatalogue(
            ProductionContentBindings.ResolveReferenceFemaleIdleCataloguePath());

        Assert.Equal("idle", intervals.Package);
        Assert.NotEmpty(intervals.Clips);
        foreach (PlantedClipIntervals clip in intervals.Clips.Values)
        {
            foreach (StandingLocomotionAnatomicalSide foot in Enum.GetValues<StandingLocomotionAnatomicalSide>())
            {
                PlantedIntervalSegment segment = Assert.Single(clip.SegmentsFor(foot));
                Assert.True(segment.IsPlanted, $"{clip.Identity} does not plant the {foot} foot throughout.");
            }
        }
    }

    /// <inheritdoc/>
    [Fact]
    public void DeliveredIntervals_FailClosedAgainstAnotherPackageLibrary()
    {
        StandingLocomotionCatalogue catalogue = ProductionContentBindings.ParseReferenceFemaleLocomotionCatalogue();
        string cataloguePath = ProductionContentBindings.ResolveReferenceFemaleLocomotionCataloguePath();
        var intervals = PerFootPlantedIntervals.Load(
            $"{cataloguePath[..(cataloguePath.LastIndexOf('/') + 1)]}{PerFootPlantedIntervals.FileName}");
        string idleCatalogue = ProductionContentBindings.ResolveReferenceFemaleIdleCataloguePath();
        byte[] idleLibrary = Godot.FileAccess.GetFileAsBytes($"{idleCatalogue[..^"_catalogue.json".Length]}.tres");

        intervals.ValidateLibraryBinding(Godot.FileAccess.GetFileAsBytes(catalogue.AnimationLibraryPath));
        _ = Assert.Throws<InvalidDataException>(() => intervals.ValidateLibraryBinding(idleLibrary));
    }

    // Each foot is planted once and swings once per cycle (the loop may split either interval at the clip boundary),
    // and the two feet's planted intervals are centred roughly half a cycle apart.
    private static void AssertAlternatingCycle(PlantedClipIntervals clip)
    {
        double left = PlantedCentre(clip, StandingLocomotionAnatomicalSide.Left);
        double right = PlantedCentre(clip, StandingLocomotionAnatomicalSide.Right);
        double offset = Math.Abs(left - right) / clip.DurationSeconds;
        Assert.InRange(Math.Min(offset, 1d - offset), 0.3d, 0.5d);
    }

    private static double PlantedCentre(PlantedClipIntervals clip, StandingLocomotionAnatomicalSide foot)
    {
        ImmutableArray<PlantedIntervalSegment> segments = clip.SegmentsFor(foot);
        bool joined = segments.Length > 1 && segments[0].IsPlanted == segments[^1].IsPlanted;
        bool wraps = joined && segments[0].IsPlanted;
        int planted = segments.Count(segment => segment.IsPlanted) - (wraps ? 1 : 0);
        int swings = segments.Count(segment => !segment.IsPlanted) - (joined && !wraps ? 1 : 0);
        Assert.True(
            planted == 1 && swings == 1,
            $"{clip.Identity} must plant and swing the {foot} foot once per cycle.");
        if (!wraps)
        {
            PlantedIntervalSegment single = segments.Single(segment => segment.IsPlanted);
            return (single.StartSeconds + single.EndSeconds) * 0.5d;
        }

        double length = segments[0].EndSeconds + (clip.DurationSeconds - segments[^1].StartSeconds);
        double centre = segments[^1].StartSeconds + (length * 0.5d);
        return centre >= clip.DurationSeconds ? centre - clip.DurationSeconds : centre;
    }
}
