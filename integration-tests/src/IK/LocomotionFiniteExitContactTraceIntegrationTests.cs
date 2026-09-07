using AlleyCat.IK;
using AlleyCat.IntegrationTests.Support;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>Samples finite test clips and checks their declared contact windows at the exact boundaries.</summary>
public sealed class LocomotionFiniteExitContactTraceIntegrationTests
{
    /// <inheritdoc/>
    [Fact]
    public void SyntheticFiniteExits_RootAndContactLifecyclesMatchFixtureDeclaration()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(nameof(LocomotionFiniteExitContactTraceIntegrationTests));
        AuthoredPerFootContactTrackV1 contacts = fixture.LoadContactTrack();

        foreach (SyntheticClipDeclaration clip in fixture.Declaration.Clips.Where(clip => clip.IsFinite))
        {
            Animation animation = fixture.AnimationFor(clip.Name);
            int rootTrack = animation.FindTrack(new NodePath("SyntheticSkeleton:Root"), Animation.TrackType.Position3D);
            Assert.Equal(clip.RootResponse, animation.PositionTrackInterpolate(rootTrack, clip.DurationSeconds));

            foreach (SyntheticContactWindow expected in clip.ContactWindows)
            {
                float phase = ExpectedPhase(expected);
                Assert.True(contacts.TryGetLifecycle(clip.Name, expected.Foot, clip.Identity, phase, out AuthoredContactLifecycleWindow actual));
                Assert.Equal(expected.Lifecycle, actual.Sample.Lifecycle);
                Assert.Equal(expected.Foot, actual.Sample.Foot);
                Assert.Equal(clip.Identity, actual.Sample.Identity);
            }
        }
    }

    private static float ExpectedPhase(SyntheticContactWindow window)
        => window.Lifecycle == "support"
            ? (float)((window.Timing[0] + window.Timing[1]) / 2d)
            : window.Lifecycle == "release"
                ? MathF.Min(1f, (float)window.Timing[0] + 0.001f)
                : (float)window.Timing[0];
}
