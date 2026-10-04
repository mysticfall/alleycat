using AlleyCat.Control.Locomotion;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Navigation;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Navigation.NAV001ArrivalOutcomeIntegrationTests;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>
/// NPC navigation plans from runtime-scale clip distances: the real NPC in the navigation test scene binds its clip
/// trajectory table to the skeleton that its AnimationTree's root-motion track resolves.
/// </summary>
public sealed class NPCNavigationTrajectoryBindingIntegrationTests
{
    private const int TicksPerRequest = 90;

    /// <summary>
    /// The production NPC binding builds its table from the tree's root-motion skeleton, so its exit distance is the
    /// catalogue distance scaled by the rig's motion scale rather than the raw catalogue distance. The table, including
    /// its rig-specific ball data, is built once at binding: the same instance serves every tick and every request.
    /// </summary>
    [Fact]
    public async Task NPCNavigationBinding_UsesTheBoundSkeletonMotionScale()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = Assert.IsType<Node3D>(LoadPackedScene(NavigationTestScenePath).Instantiate(), exactMatch: false);
        try
        {
            Node3D actor = root.GetNode<Node3D>(ActorNodeName);
            PrepareActorBeforeTreeEntry(actor);
            sceneTree.Root.AddChild(root);
            INavigation requests = await WaitForReadyNavigationAsync(sceneTree, root, actor);
            NPCNavigation navigation = actor.GetNode<NPCNavigation>("Navigation");
            Skeleton3D skeleton = actor.GetNode<Skeleton3D>(SkeletonPath);
            Assert.True(navigation.IsRuntimeBindingActive, "SETUP: NPC navigation binding is not active.");

            ClipTrajectoryTable table = Assert.IsType<ClipTrajectoryTable>(navigation.Trajectories);
            StandingLocomotionCatalogue catalogue = ProductionContentBindings.ParseReferenceFemaleLocomotionCatalogue();
            Vector3 catalogueExit = catalogue[StandingLocomotionCapability.ExitForwardLeft].EndpointRootDisplacement;
            float catalogueDistance = new Vector2(catalogueExit.X, catalogueExit.Z).Length();
            float runtimeDistance = table[StandingLocomotionCapability.ExitForwardLeft].Endpoint.Planar.Length();

            Assert.Equal(skeleton.MotionScale, table.MotionScale);
            Assert.All(table.Clips.Values, clip => Assert.Equal(skeleton.MotionScale, clip.BallMotionScale));
            Assert.True(skeleton.MotionScale < 0.95f, $"SETUP: the female rig motion scale {skeleton.MotionScale} does not discriminate.");
            Assert.True(
                Math.Abs(runtimeDistance - (catalogueDistance * skeleton.MotionScale))
                    <= ClipTrajectoryTable.CatalogueEndpointTolerance * skeleton.MotionScale,
                FormattableString.Invariant($"Exit distance {runtimeDistance:0.######} m is not catalogue {catalogueDistance:0.######} m × {skeleton.MotionScale}."));

            // Built once: a walk request and a superseding request never rebuild the table.
            await WaitForPhysicsFramesAsync(sceneTree, WarmUpPhysicsTicks);
            Assert.Same(table, navigation.Trajectories);
            _ = IssueRequest(requests, new ArrivalRequest(new Vector3(0f, 0f, -4f), 0f), "trajectory-binding", 0);
            await WaitForPhysicsFramesAsync(sceneTree, TicksPerRequest);
            Assert.Same(table, navigation.Trajectories);
            _ = IssueRequest(requests, new ArrivalRequest(new Vector3(3f, 0f, 0f), 0f), "trajectory-binding", 1);
            await WaitForPhysicsFramesAsync(sceneTree, TicksPerRequest);
            Assert.Same(table, navigation.Trajectories);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }
}
