using AlleyCat.IK;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Rigging;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>Exercises discrete transition stitching only against fixture-declared clips and contacts.</summary>
public sealed class NPCDiscreteExitStitchAuditIntegrationTests
{
    private const float CrossfadeSeconds = 0.12f;

    /// <summary>Verifies declared lifecycle selection and root samples across a synthetic discrete stitch.</summary>
    [Fact]
    public async Task FixtureDeclaredReleaseAndFiniteEntry_ProduceCompletePairwiseAudit()
    {
        SceneTree sceneTree = GetSceneTree();
        using var fixture = SyntheticLocomotionRuntimeFixture.Create("discrete-exit-stitch");
        sceneTree.Root.AddChild(fixture.Root);
        try
        {
            await WaitForNextFrameAsync(sceneTree);
            SyntheticClipDeclaration outgoing = fixture.Declaration["exit_alpha"];
            SyntheticClipDeclaration incoming = fixture.Declaration["exit_beta"];
            AuthoredPerFootContactTrackV1 contacts = fixture.LoadContactTrack();

            Assert.True(contacts.TryGetLifecycle(
                outgoing.Name,
                LimbSide.Right,
                outgoing.Identity,
                0.75f,
                out AuthoredContactLifecycleWindow release));
            Assert.Equal(ContactLockLifecycle.Release, release.Lifecycle);
            Assert.True(contacts.TryGetLifecycle(
                incoming.Name,
                LimbSide.Left,
                incoming.Identity,
                0f,
                out AuthoredContactLifecycleWindow acquire));
            Assert.Equal(ContactLockLifecycle.Acquire, acquire.Lifecycle);

            PairAudit audit = CapturePair(fixture, outgoing, incoming);

            Assert.Equal(5, audit.Samples.Count);
            Assert.Equal(0f, audit.Samples[0].ElapsedSeconds, 6);
            Assert.Equal(CrossfadeSeconds, audit.Samples[^1].ElapsedSeconds, 6);
            Assert.All(audit.Samples, sample => Assert.True(sample.IsFinite));
            Assert.Equal(outgoing.RootResponse * 0.75f, audit.Samples[0].OutgoingRoot);
            Assert.Equal(incoming.RootResponse * (CrossfadeSeconds / incoming.DurationSeconds), audit.Samples[^1].IncomingRoot);
            Assert.True(audit.MaximumRootDelta > 0f);
        }
        finally
        {
            fixture.Root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static PairAudit CapturePair(
        SyntheticLocomotionRuntimeFixture fixture,
        SyntheticClipDeclaration outgoing,
        SyntheticClipDeclaration incoming)
    {
        float sourceStep = 1f / NPCFootRawClipBaselineLoader.NativeRate;
        float[] elapsed = [0f, sourceStep, 2f * sourceStep, 3f * sourceStep, CrossfadeSeconds];
        List<PairSample> samples = [];
        foreach (float time in elapsed)
        {
            float outgoingTime = Mathf.Min(outgoing.DurationSeconds, (outgoing.DurationSeconds * 0.75f) + time);
            float incomingTime = Mathf.Min(incoming.DurationSeconds, time);
            samples.Add(new(
                time,
                SampleRoot(fixture, outgoing.Name, outgoingTime),
                SampleRoot(fixture, incoming.Name, incomingTime)));
        }

        float maximumRootDelta = samples.Max(sample => sample.OutgoingRoot.DistanceTo(sample.IncomingRoot));
        return new(samples, maximumRootDelta);
    }

    private static Vector3 SampleRoot(SyntheticLocomotionRuntimeFixture fixture, string clip, float time)
        => fixture.AnimationFor(clip).PositionTrackInterpolate(0, time);

    private sealed record PairSample(float ElapsedSeconds, Vector3 OutgoingRoot, Vector3 IncomingRoot)
    {
        public bool IsFinite => float.IsFinite(ElapsedSeconds)
            && OutgoingRoot.IsFinite()
            && IncomingRoot.IsFinite();
    }

    private sealed record PairAudit(IReadOnlyList<PairSample> Samples, float MaximumRootDelta);
}
