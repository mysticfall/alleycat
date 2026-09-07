using AlleyCat.IntegrationTests.Support;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.AnimationCatalogue;

/// <summary>Exercises a bare AnimationPlayer/AnimationTree graph built from the synthetic package.</summary>
public sealed class LocomotionPlaybackMechanicsProbeIntegrationTests
{
    /// <inheritdoc/>
    [Fact]
    public async Task SyntheticLoopAndFiniteStates_ExposeDeclaredPlaybackAndRootMotion()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(nameof(LocomotionPlaybackMechanicsProbeIntegrationTests));
        SceneTree sceneTree = GetSceneTree();
        AnimationTree tree = fixture.CreatePlaybackTree();
        sceneTree.Root.AddChild(fixture.Root);
        try
        {
            await WaitForFramesAsync(sceneTree, 2);
            tree.Active = true;
            tree.Set("parameters/conditions/synthetic_finite_complete", true);
            AnimationNodeStateMachinePlayback playback = tree.Get("parameters/playback").As<AnimationNodeStateMachinePlayback>()
                ?? throw new Xunit.Sdk.XunitException("Synthetic state-machine playback was not available.");
            SyntheticClipDeclaration loop = fixture.Declaration["stride_loop"];
            playback.Start(loop.Name);
            tree.Advance(loop.DurationSeconds / 2f);

            Assert.Equal(loop.Name, playback.GetCurrentNode());
            Assert.Equal(new NodePath("SyntheticSkeleton:Root"), tree.RootMotionTrack);
            int rootTrack = fixture.AnimationFor(loop.Name).FindTrack(tree.RootMotionTrack, Animation.TrackType.Position3D);
            Assert.Equal(loop.RootResponse / 2f, fixture.AnimationFor(loop.Name).PositionTrackInterpolate(rootTrack, loop.DurationSeconds / 2f));

            SyntheticClipDeclaration finite = fixture.Declaration["exit_alpha"];
            AnimationNodeStateMachine states = Assert.IsType<AnimationNodeStateMachine>(tree.TreeRoot, exactMatch: false);
            AssertTransition(states, loop.Name, finite.Name);
            playback.Travel(finite.Name);
            // Travel requests are committed on the tree's next evaluation; do not consume the finite clip while
            // that request is still queued.
            tree.Advance(0d);
            Assert.Equal(finite.Name, playback.GetCurrentNode());
            tree.Advance(0d);
            tree.Advance(finite.DurationSeconds + 0.01f);
            // The tree's declared callback mode is physics, so commit the auto-at-end transition at its native
            // scheduling boundary rather than assuming a process-frame settle will evaluate it.
            await WaitForPhysicsFramesAsync(sceneTree, 1);
            Assert.Equal("idle", playback.GetCurrentNode());
        }
        finally
        {
            fixture.Root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <inheritdoc/>
    [Fact]
    public async Task SyntheticPivots_ExposeDeclaredSignedRootRotation()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create("synthetic-pivot-playback");
        SceneTree sceneTree = GetSceneTree();
        AnimationTree tree = fixture.CreatePlaybackTree();
        sceneTree.Root.AddChild(fixture.Root);
        try
        {
            await WaitForFramesAsync(sceneTree, 2);
            tree.Active = true;
            AnimationNodeStateMachinePlayback playback = tree.Get("parameters/playback").As<AnimationNodeStateMachinePlayback>()
                ?? throw new Xunit.Sdk.XunitException("Synthetic state-machine playback was not available.");
            foreach (SyntheticClipDeclaration pivot in fixture.Declaration.Clips.Where(clip => clip.Name.StartsWith("pivot_", StringComparison.Ordinal)))
            {
                playback.Start(pivot.Name);
                tree.Advance(pivot.DurationSeconds);
                int rootTrack = fixture.AnimationFor(pivot.Name).FindTrack(tree.RootMotionTrack, Animation.TrackType.Rotation3D);
                Assert.Equal(pivot.YawRadians, fixture.AnimationFor(pivot.Name).RotationTrackInterpolate(rootTrack, pivot.DurationSeconds).GetEuler().Y, 4);
            }
        }
        finally
        {
            fixture.Root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static void AssertTransition(AnimationNodeStateMachine states, StringName from, StringName to)
    {
        for (int index = 0; index < states.GetTransitionCount(); index++)
        {
            if (states.GetTransitionFrom(index) == from && states.GetTransitionTo(index) == to)
            {
                return;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected a transition from '{from}' to '{to}'.");
    }
}
