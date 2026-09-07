using System.Text.Json.Nodes;
using AlleyCat.Control.Locomotion;
using AlleyCat.IK;
using AlleyCat.IntegrationTests.AnimationCatalogue;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Rigging;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>Production-scene coverage is deliberately limited to composition wiring.</summary>
public sealed class PlayerContactLockProductionIntegrationTests
{
    private const string PlayerScenePath = "res://assets/characters/reference/ally_player.tscn";

    /// <inheritdoc/>
    [Fact]
    public void ProductionPlayer_ContainsAnimationHostAndContactLockCompositionIsANode()
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>(PlayerScenePath)
            ?? throw new Xunit.Sdk.XunitException("The production player scene could not be loaded.");
        Node root = scene.Instantiate();
        try
        {
            Assert.NotNull(root.GetNodeOrNull<AnimationPlayer>("AnimationPlayer"));
            Assert.True(typeof(Node).IsAssignableFrom(typeof(PlayerContactLockComposition)));
        }
        finally
        {
            root.Free();
        }
    }

    /// <inheritdoc/>
    [Fact]
    public void BareCatalogueComposition_SelectsGenericCyclicFinitePartialAndFallbackPaths()
    {
        SyntheticNavigationManifest manifest = LocomotionRuntimeCatalogueV3IntegrationTests.CreateGenericManifest();
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(nameof(BareCatalogueComposition_SelectsGenericCyclicFinitePartialAndFallbackPaths), manifest);
        StandingLocomotionCatalogue catalogue = LoadCatalogue(fixture);
        AuthoredPerFootContactTrackV1 track = LoadBoundTrack(fixture, catalogue);

        AssertLifecycle(track, catalogue["stride_alpha"], LimbSide.Left, 0.9f, ContactLockLifecycle.Support);
        AssertLifecycle(track, catalogue["stride_alpha"], LimbSide.Left, 0.1f, ContactLockLifecycle.Support);
        AssertLifecycle(track, catalogue["stride_alpha"], LimbSide.Left, 0.2f, ContactLockLifecycle.Release);
        AssertLifecycle(track, catalogue["stride_alpha"], LimbSide.Right, 0.3f, ContactLockLifecycle.Acquire);
        AssertLifecycle(track, catalogue["settle"], LimbSide.Left, 0.8f, ContactLockLifecycle.Release);
        AssertLifecycle(track, catalogue["settle"], LimbSide.Right, 0.2f, ContactLockLifecycle.Acquire);
        Assert.False(track.TryGetLifecycle("turn", LimbSide.Left, catalogue["turn"].Identity, 0.5f, out _));
        Assert.False(track.TryGetLifecycle("turn", LimbSide.Right, catalogue["turn"].Identity, 0.5f, out _));
    }

    /// <inheritdoc/>
    [Fact]
    public void BareCatalogueComposition_FailsClosedForPackageMismatchAmbiguityAndReplacement()
    {
        using var first = SyntheticLocomotionRuntimeFixture.Create(
            $"{nameof(BareCatalogueComposition_FailsClosedForPackageMismatchAmbiguityAndReplacement)}-first",
            LocomotionRuntimeCatalogueV3IntegrationTests.CreateGenericManifest());
        StandingLocomotionCatalogue firstCatalogue = LoadCatalogue(first);
        ContactLockCoordinator firstCoordinator = ContactLockProductionComposition.LoadCoordinator(
            firstCatalogue,
            first.NavigationCataloguePath);

        string trackJson = File.ReadAllText(ProjectSettings.GlobalizePath(first.NavigationContactTrackPath));
        _ = Assert.Throws<InvalidDataException>(() => AuthoredPerFootContactTrackV1.Load(
            trackJson.Replace("generation_one", "stale_generation", StringComparison.Ordinal),
            firstCatalogue));
        _ = Assert.Throws<InvalidDataException>(() => AuthoredPerFootContactTrackV1.Load(
            trackJson.Replace("quartz_token_a", "quartz_unknown_token", StringComparison.Ordinal),
            firstCatalogue));

        JsonObject duplicateDocument = JsonNode.Parse(trackJson)!.AsObject();
        JsonArray duplicateSamples = duplicateDocument["samples"]!.AsArray();
        duplicateSamples.Add(duplicateSamples[0]!.DeepClone());
        _ = Assert.Throws<InvalidDataException>(() => AuthoredPerFootContactTrackV1.Load(duplicateDocument.ToJsonString(), firstCatalogue));

        using var replacement = SyntheticLocomotionRuntimeFixture.Create(
            $"{nameof(BareCatalogueComposition_FailsClosedForPackageMismatchAmbiguityAndReplacement)}-replacement",
            LocomotionRuntimeCatalogueV3IntegrationTests.CreateGenericManifest("generation_two"));
        ContactLockCoordinator replacementCoordinator = ContactLockProductionComposition.LoadCoordinator(
            LoadCatalogue(replacement),
            replacement.NavigationCataloguePath);
        firstCoordinator.Teardown();

        Assert.NotEqual(
            firstCatalogue.ContactEvidence!.PackageIdentity,
            LoadCatalogue(replacement).ContactEvidence!.PackageIdentity);
        Assert.NotNull(replacementCoordinator);
        Assert.False(firstCoordinator.StateSnapshot.Left.HasAnchor);
        Assert.False(firstCoordinator.StateSnapshot.Right.HasAnchor);
    }

    /// <inheritdoc/>
    [Fact]
    public void PlayerPublisher_ResolvesCatalogueIdentityWithoutRoleNameOrOrderInference()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(
            nameof(PlayerPublisher_ResolvesCatalogueIdentityWithoutRoleNameOrOrderInference),
            LocomotionRuntimeCatalogueV3IntegrationTests.CreateGenericManifest());
        StandingLocomotionCatalogue catalogue = LoadCatalogue(fixture);
        var states = new AnimationNodeStateMachine();
        states.AddNode("OpaqueState", new AnimationNodeAnimation { Animation = "locomotion/quartz_token_a" });

        Assert.True(PlayerContactLockComposition.TryResolveClip(states, catalogue, "OpaqueState", out StandingLocomotionClip? clip));
        Assert.Equal("stride_alpha", clip!.Role);
        states.RemoveNode("OpaqueState");
        states.AddNode("OpaqueState", new AnimationNodeAnimation { Animation = "idle/quartz_token_a" });
        Assert.False(PlayerContactLockComposition.TryResolveClip(states, catalogue, "OpaqueState", out _));
    }

    private static StandingLocomotionCatalogue LoadCatalogue(SyntheticLocomotionRuntimeFixture fixture)
        => StandingLocomotionCatalogue.Parse(
            File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationCataloguePath)),
            fixture.NavigationCataloguePath);

    private static AuthoredPerFootContactTrackV1 LoadBoundTrack(
        SyntheticLocomotionRuntimeFixture fixture,
        StandingLocomotionCatalogue catalogue)
        => AuthoredPerFootContactTrackV1.Load(
            File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationContactTrackPath)),
            catalogue);

    private static void AssertLifecycle(
        AuthoredPerFootContactTrackV1 track,
        StandingLocomotionClip clip,
        LimbSide foot,
        float phase,
        ContactLockLifecycle expected)
    {
        Assert.True(track.TryGetLifecycle(clip.Role, foot, clip.Identity, phase, out AuthoredContactLifecycleWindow window));
        Assert.Equal(expected, window.Lifecycle);
    }
}
