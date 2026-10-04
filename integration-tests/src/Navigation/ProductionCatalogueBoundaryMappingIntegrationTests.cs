using AlleyCat.Control.Locomotion;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Xunit;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>
/// Guards the delivered ANIM-002 runtime package against missing or ambiguous entry/exit boundary
/// mappings. The navigation runtime (<see cref="NPCNavigation" />) validates one boundary mapping per
/// navigation phase boundary (entry transition to sustained travel, sustained travel to exit transition,
/// for both travel directions and both anatomical lanes); a package that omits any pair stalls the NPC
/// in the <c>Entry</c> phase with a one-off boundary-mapping failure. This guard parses the real
/// production package — not a synthetic fixture — so publication gaps fail at test time instead of
/// during play.
/// </summary>
[Headless]
public sealed class ProductionCatalogueBoundaryMappingIntegrationTests
{
    private static string CataloguePath => ProductionContentBindings.ResolveReferenceFemaleLocomotionCataloguePath();

    /// <inheritdoc/>
    [Fact]
    public void ProductionPackage_ResolvesEveryNavigationEntryAndExitBoundaryMapping()
    {
        string cataloguePath = CataloguePath;
        string json = Godot.FileAccess.GetFileAsString(cataloguePath);
        Assert.False(string.IsNullOrWhiteSpace(json), $"The delivered ANIM-002 runtime catalogue is unreadable: {cataloguePath}");

        var catalogue = StandingLocomotionCatalogue.Parse(json, cataloguePath);
        catalogue.RequireCapabilities(nameof(NPCNavigation), Enum.GetValues<StandingLocomotionCapability>());


        foreach (bool backward in new[] { false, true })
        {
            foreach (StandingLocomotionAnatomicalSide side in Enum.GetValues<StandingLocomotionAnatomicalSide>())
            {
                StandingLocomotionAnatomicalSide opposite = side == StandingLocomotionAnatomicalSide.Left
                    ? StandingLocomotionAnatomicalSide.Right
                    : StandingLocomotionAnatomicalSide.Left;
                string travel = catalogue[StandingLocomotionCapabilities.Travel(backward)].Role;
                string entry = catalogue[StandingLocomotionCapabilities.Entry(backward, side)].Role;
                string exit = catalogue[StandingLocomotionCapabilities.Exit(backward, side)].Role;

                AssertResolvesEntryMapping(catalogue, entry, travel, side, backward);
                AssertResolvesExitMapping(catalogue, travel, exit, side, backward);

                // Each entry lane must stay side-specific: resolving the opposite lane yields no
                // mapping, which keeps the side resolution above unambiguous.
                _ = Assert.Throws<InvalidOperationException>(
                    () => _ = catalogue.ResolveBoundaryMapping(entry, travel, opposite));
            }
        }
    }

    private static void AssertResolvesEntryMapping(
        StandingLocomotionCatalogue catalogue,
        string entry,
        string travel,
        StandingLocomotionAnatomicalSide side,
        bool backward)
    {
        StandingLocomotionBoundaryMapping mapping = catalogue.ResolveBoundaryMapping(entry, travel, side);
        Assert.Equal(entry, mapping.Stitch.FromRole);
        Assert.Equal(travel, mapping.Stitch.ToRole);
        Assert.Equal(side, mapping.Source.Side);
        Assert.Equal(side, mapping.Target.Side);
        Assert.Equal(StandingLocomotionBoundaryContext.End, mapping.Source.BoundaryContext);
        Assert.Equal(StandingLocomotionBoundaryContext.Cyclic, mapping.Target.BoundaryContext);
        Assert.Equal(mapping.Source.Kind, mapping.Target.Kind);
        Assert.Equal(
            backward ? StandingLocomotionEventKind.Acquire : StandingLocomotionEventKind.Release,
            mapping.Source.Kind);
    }

    private static void AssertResolvesExitMapping(
        StandingLocomotionCatalogue catalogue,
        string travel,
        string exit,
        StandingLocomotionAnatomicalSide side,
        bool backward)
    {
        StandingLocomotionBoundaryMapping mapping = catalogue.ResolveBoundaryMapping(travel, exit, side);
        Assert.Equal(travel, mapping.Stitch.FromRole);
        Assert.Equal(exit, mapping.Stitch.ToRole);
        Assert.Equal(side, mapping.Source.Side);
        Assert.Equal(side, mapping.Target.Side);
        Assert.Equal(StandingLocomotionBoundaryContext.Cyclic, mapping.Source.BoundaryContext);
        Assert.Equal(StandingLocomotionBoundaryContext.Start, mapping.Target.BoundaryContext);
        Assert.Equal(mapping.Source.Kind, mapping.Target.Kind);
        Assert.Equal(
            backward ? StandingLocomotionEventKind.Acquire : StandingLocomotionEventKind.Release,
            mapping.Source.Kind);
    }
}
