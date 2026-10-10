using AlleyCat.IK;
using AlleyCat.Navigation;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Navigation.NavigationRigTestSupport;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>Content-independent anatomical binding refusal; no scripted poses, clip calibration or walk replays.</summary>
public sealed class IK003FootPlantingRigIntegrationTests
{
    /// <summary>A missing toe bone disables both planting providers and navigation, rather than partially binding.</summary>
    [Fact]
    public async Task Female_MissingToeBoneFailsClosed()
    {
        SceneTree tree = GetSceneTree();
        Node3D root = Assert.IsType<Node3D>(LoadPackedScene(NavigationTestScenePath).Instantiate(), exactMatch: false);
        try
        {
            Node3D actor = root.GetNode<Node3D>(ActorNodeName);
            PrepareActorBeforeTreeEntry(actor);
            Skeleton3D skeleton = actor.GetNode<Skeleton3D>(SkeletonPath);
            skeleton.SetBoneName(skeleton.FindBone("RightToes"), "RightToesRemovedForFixture");
            tree.Root.AddChild(root);
            await WaitForPhysicsFramesAsync(tree, WarmUpPhysicsTicks);
            CharacterIK ik = actor.GetNode<CharacterIK>("CharacterIK");
            foreach (FootPlantingTargetProvider provider in new[]
                     {
                         Assert.IsType<FootPlantingTargetProvider>(ik.LeftFootTargetIntentProvider),
                         Assert.IsType<FootPlantingTargetProvider>(ik.RightFootTargetIntentProvider),
                     })
            {
                Assert.False(provider.IsPlantingAvailable);
                Assert.False(provider.TryGetTargetIntent(out _));
            }
            Assert.All(ik.LeftFootModifierGroup.Concat(ik.RightFootModifierGroup), modifier => Assert.True(!modifier.Active || modifier.Influence == 0f));
            Assert.False(actor.GetNode<NPCNavigation>("Navigation").IsRuntimeBindingActive);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(tree, 1);
        }
    }
}
