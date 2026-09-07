using AlleyCat.Control.Locomotion;
using AlleyCat.IK;
using AlleyCat.IntegrationTests.AnimationCatalogue;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Rigging;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>Exercises coordinator state transitions from synthetic contact declarations.</summary>
public sealed class ContactLockCoordinatorIntegrationTests
{
    /// <inheritdoc/>
    [Fact]
    public void GenericCyclicEvidence_RepeatedAcquireObservationAndBothFeetRemainIndependent()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(
            nameof(GenericCyclicEvidence_RepeatedAcquireObservationAndBothFeetRemainIndependent),
            LocomotionRuntimeCatalogueV3IntegrationTests.CreateGenericManifest());
        var catalogue = StandingLocomotionCatalogue.Parse(
            File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationCataloguePath)),
            fixture.NavigationCataloguePath);
        var track = AuthoredPerFootContactTrackV1.Load(
            File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationContactTrackPath)),
            catalogue);
        var coordinator = new ContactLockCoordinator(track);
        StandingLocomotionClip clip = catalogue["stride_alpha"];
        Transform3D leftAnchor = new(Basis.Identity, new Vector3(-0.2f, 0f, 0f));
        Transform3D rightAnchor = new(Basis.Identity, new Vector3(0.2f, 0f, 0f));

        Observe(0.8f, 1);
        Assert.True(coordinator.TryGetResolution(LimbSide.Left, leftAnchor, out _));
        Assert.True(coordinator.TryGetResolution(LimbSide.Left, leftAnchor, out _));
        Assert.Equal(leftAnchor, coordinator.GetSnapshot(LimbSide.Left).Anchor);
        Observe(0.9f, 2);
        Assert.True(coordinator.TryGetResolution(LimbSide.Left, leftAnchor, out _));
        Observe(0.3f, 3);
        Assert.False(coordinator.TryGetResolution(LimbSide.Left, leftAnchor, out _));
        Assert.True(coordinator.TryGetResolution(LimbSide.Right, rightAnchor, out _));
        Assert.Equal(rightAnchor, coordinator.GetSnapshot(LimbSide.Right).Anchor);
        Assert.False(coordinator.GetSnapshot(LimbSide.Left).HasAnchor);

        void Observe(float phase, ulong tick)
        {
            coordinator.ObserveLiveState(new(
            true,
            clip.Role,
            clip.Identity,
            phase,
            catalogue.PackageVariant,
            clip.Identity,
            catalogue.ContactEvidence!.Generation,
            tick,
            1f / 60f));
        }
    }

    /// <inheritdoc/>
    [Fact]
    public void SyntheticFiniteExit_AcquireSupportReleaseAndLeadingHandoffFollowDeclaration()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(nameof(ContactLockCoordinatorIntegrationTests));
        SyntheticClipDeclaration clip = fixture.Declaration["exit_alpha"];
        var coordinator = new ContactLockCoordinator(fixture.LoadContactTrack());
        Transform3D anchor = new(Basis.Identity, new Vector3(0.2f, 0f, 0f));

        coordinator.ObserveLiveState(fixture.Live(clip.Name, 0f, 10));
        Assert.True(coordinator.TryGetResolution(LimbSide.Right, anchor, out _));
        Assert.False(coordinator.TryGetResolution(LimbSide.Left, anchor, out _));
        Assert.Equal(ContactLockLifecycle.Acquire, coordinator.GetSnapshot(LimbSide.Right).Lifecycle);

        coordinator.ObserveLiveState(fixture.Live(clip.Name, 0.25f, 11));
        Assert.True(coordinator.TryGetResolution(LimbSide.Right, anchor, out _));
        Assert.Equal(ContactLockLifecycle.Support, coordinator.GetSnapshot(LimbSide.Right).Lifecycle);

        coordinator.ObserveLiveState(fixture.Live(clip.Name, 0.451f, 12));
        Assert.True(coordinator.TryGetResolution(LimbSide.Right, anchor, out _));
        Assert.Equal(ContactLockLifecycle.Release, coordinator.GetSnapshot(LimbSide.Right).Lifecycle);

        coordinator.ObserveLiveState(fixture.Live(clip.Name, 0.60f, 13));
        Assert.True(coordinator.TryGetResolution(LimbSide.Left, anchor, out _));
        Assert.Equal(ContactLockLifecycle.Acquire, coordinator.GetSnapshot(LimbSide.Left).Lifecycle);
    }

    /// <inheritdoc/>
    [Fact]
    public void SyntheticFiniteExit_IdentityMismatchRefusesTheDeclaredContact()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create("coordinator-refusal");
        var coordinator = new ContactLockCoordinator(fixture.LoadContactTrack());
        coordinator.ObserveLiveState(fixture.Live("exit_beta", 0f, 20, "incorrect_fixture_identity"));

        Assert.False(coordinator.TryGetResolution(LimbSide.Left, Transform3D.Identity, out _));
        Assert.Equal(ContactLockFeasibilityReason.Refused, coordinator.GetSnapshot(LimbSide.Left).Feasibility);
    }
}
