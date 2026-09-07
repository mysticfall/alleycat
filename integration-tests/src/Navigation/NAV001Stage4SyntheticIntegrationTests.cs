using AlleyCat.IntegrationTests.Support;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>Stage-4 automation boundary: the authored harness remains manual-only.</summary>
public sealed class NAV001Stage4SyntheticIntegrationTests
{
    private const string SyntheticScenePath = "res://tests/navigation/synthetic_navigation_runtime.tscn";

    /// <inheritdoc/>
    [Fact]
    public void BareSyntheticScene_LoadsFixtureOwnedNavigationComposition()
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>(SyntheticScenePath);
        Node3D host = Assert.IsType<Node3D>(scene.Instantiate(), exactMatch: false);
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(nameof(BareSyntheticScene_LoadsFixtureOwnedNavigationComposition));
        SyntheticNavigationRuntime runtime = fixture.CreateNavigationRuntime();
        host.AddChild(runtime.Root);

        try
        {
            Assert.Equal("SyntheticNavigationRuntime", host.Name);
            Assert.Same(runtime.Actor, runtime.Navigation.Actor);
            Assert.Same(runtime.AnimationTree, runtime.Navigation.AnimationTree);
            Assert.Same(runtime.RootMotionReference, runtime.Navigation.RootMotionReference);
            Assert.Equal("ember_token_0b", runtime.Manifest["wf"].Identity);
        }
        finally
        {
            // The host is never attached to the scene tree, so free it immediately instead of deferring reclamation
            // to a later frame flush. Freeing the host also frees the attached fixture root; the fixture's Dispose
            // is idempotent about that root already being collected.
            host.Free();
        }
    }
}
