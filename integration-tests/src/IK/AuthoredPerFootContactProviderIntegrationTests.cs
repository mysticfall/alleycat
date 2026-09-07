using AlleyCat.IK;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Rigging;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>Checks per-foot contact-provider admission against a test-owned contact declaration.</summary>
public sealed class AuthoredPerFootContactProviderIntegrationTests
{
    /// <inheritdoc/>
    [Fact]
    public void ContactTrack_RepresentsCyclicSupportAcrossTheClipBoundary()
    {
        const string json = /*lang=json,strict*/ """
            {
              "schema_version": 1,
              "package_identity": "cyclic_fixture_package",
              "samples": [
                {
                  "schema_version": 1, "role": "cyclic_fixture", "foot": "left",
                  "lifecycle": "support", "timing": [0.8, 1.0], "validity": "valid", "confidence": 0.9,
                  "provenance": "synthetic", "clip": "cyclic_clip_identity", "variant": "fixture",
                  "reference": "synthetic", "identity": "cyclic-identity", "generation": "fixture"
                },
                {
                  "schema_version": 1, "role": "cyclic_fixture", "foot": "left",
                  "lifecycle": "support", "timing": [0.0, 0.2], "validity": "valid", "confidence": 0.9,
                  "provenance": "synthetic", "clip": "cyclic_clip_identity", "variant": "fixture",
                  "reference": "synthetic", "identity": "cyclic-identity", "generation": "fixture"
                }
              ]
            }
            """;
        var track = AuthoredPerFootContactTrackV1.Load(json);

        Assert.True(track.TryGetLifecycle(
            "cyclic_fixture", LimbSide.Left, "cyclic_clip_identity", 0.9f, out AuthoredContactLifecycleWindow closing));
        Assert.Equal(ContactLockLifecycle.Support, closing.Lifecycle);
        Assert.True(track.TryGetLifecycle(
            "cyclic_fixture", LimbSide.Left, "cyclic_clip_identity", 0.1f, out AuthoredContactLifecycleWindow opening));
        Assert.Equal(ContactLockLifecycle.Support, opening.Lifecycle);
    }

    /// <inheritdoc/>
    [Fact]
    public void ContactTrack_RepresentsFiniteBoundarySupportAndReleaseWithoutInventingAcquire()
    {
        const string json = /*lang=json,strict*/ """
            {
              "schema_version": 1,
              "package_identity": "finite_fixture_package",
              "samples": [
                {
                  "schema_version": 1, "role": "finite_fixture", "foot": "left",
                  "lifecycle": "support", "timing": [0.0, 0.6], "validity": "valid", "confidence": 0.9,
                  "provenance": "synthetic", "clip": "finite_clip_identity", "variant": "fixture",
                  "reference": "synthetic", "identity": "finite-identity", "generation": "fixture"
                },
                {
                  "schema_version": 1, "role": "finite_fixture", "foot": "left",
                  "lifecycle": "release", "timing": [0.6], "validity": "valid", "confidence": 0.9,
                  "provenance": "synthetic", "clip": "finite_clip_identity", "variant": "fixture",
                  "reference": "synthetic", "identity": "finite-identity", "generation": "fixture"
                }
              ]
            }
            """;
        var track = AuthoredPerFootContactTrackV1.Load(json);

        Assert.True(track.TryGetLifecycle(
            "finite_fixture", LimbSide.Left, "finite_clip_identity", 0.25f, out AuthoredContactLifecycleWindow support));
        Assert.Equal(ContactLockLifecycle.Support, support.Lifecycle);
        Assert.True(track.TryGetLifecycle(
            "finite_fixture", LimbSide.Left, "finite_clip_identity", 0.75f, out AuthoredContactLifecycleWindow release));
        Assert.Equal(ContactLockLifecycle.Release, release.Lifecycle);
        Assert.True(track.TryGetLifecycle(
            "finite_fixture", LimbSide.Left, "finite_clip_identity", 0.6f, out AuthoredContactLifecycleWindow boundary));
        Assert.Equal(ContactLockLifecycle.Release, boundary.Lifecycle);
    }

    /// <inheritdoc/>
    [Fact]
    public void SyntheticContactTrack_ResolvesDeclaredSideWithoutMutatingSourceTarget()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(nameof(AuthoredPerFootContactProviderIntegrationTests));
        SyntheticClipDeclaration clip = fixture.Declaration["exit_alpha"];
        var target = new Node3D { GlobalPosition = new Vector3(0.2f, 0.1f, -0.3f) };
        var provider = new AuthoredPerFootContactProvider
        {
            Side = LimbSide.Right,
            AnimationSynchronizedFootTarget = target,
            ContactTrackPath = fixture.ContactTrackPath,
            Role = clip.Name,
            Variant = fixture.Declaration.Variant,
            ExpectedIdentity = clip.Identity,
            ExpectedGeneration = fixture.Declaration.Generation,
        };
        try
        {
            Transform3D original = target.GlobalTransform;
            Assert.True(provider.TryGetTargetIntent(out IKTargetIntentResolution result));
            Assert.False(result.ShouldApplyTargetTransform);
            Assert.Equal(original, target.GlobalTransform);
            Assert.Equal(AuthoredContactRefusalReason.None, provider.LastRefusalReason);
        }
        finally
        {
            provider.Free();
            target.Free();
        }
    }

    /// <inheritdoc/>
    [Fact]
    public void SyntheticContactTrack_RejectsMismatchedDeclaredIdentity()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create("provider-refusal");
        var provider = new AuthoredPerFootContactProvider
        {
            Side = LimbSide.Right,
            AnimationSynchronizedFootTarget = new Node3D(),
            ContactTrackPath = fixture.ContactTrackPath,
            Role = "exit_alpha",
            ExpectedIdentity = "different_fixture_identity",
        };
        try
        {
            Assert.False(provider.TryGetTargetIntent(out _));
            Assert.Equal(AuthoredContactRefusalReason.RoleOrIdentityMismatched, provider.LastRefusalReason);
        }
        finally
        {
            provider.AnimationSynchronizedFootTarget?.Free();
            provider.Free();
        }
    }
}
