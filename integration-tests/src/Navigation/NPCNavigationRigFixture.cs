using AlleyCat.Control.Locomotion;
using AlleyCat.Navigation;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Navigation.NAV001ArrivalOutcomeIntegrationTests;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>Reference rig of the NPC in the navigation test scene.</summary>
internal enum NPCNavigationRig
{
    Female,
    Male,
}

/// <summary>
/// The real NPC of the navigation test scene on either reference rig (the scene's own character-variant selection),
/// installed without its AI mind, with one physics step per frame. It exposes the production navigation, its
/// AnimationTree driver, and the CharacterIK planted-state source.
/// </summary>
internal sealed class NPCNavigationRigFixture : IAsyncDisposable
{
    private readonly SceneTree _sceneTree;
    private readonly Node3D _root;
    private readonly int _previousStepLimit;

    private NPCNavigationRigFixture(SceneTree sceneTree, Node3D root, Node3D actor, NPCNavigationRig rig, int previousStepLimit)
    {
        _sceneTree = sceneTree;
        _root = root;
        _previousStepLimit = previousStepLimit;
        Rig = rig;
        Actor = actor;
        Skeleton = actor.GetNode<Skeleton3D>(SkeletonPathOf(rig));
        Tree = actor.GetNode<AnimationTree>("AnimationTree");
        Navigation = actor.GetNode<NPCNavigation>("Navigation");
        PlantedSource = actor.GetNode<AnimationGraphPlantedStateSource>("CharacterIK/PlantedStateSource");
    }

    public NPCNavigationRig Rig
    {
        get;
    }

    public Node3D Actor
    {
        get;
    }

    public Skeleton3D Skeleton
    {
        get;
    }

    public AnimationTree Tree
    {
        get;
    }

    public NPCNavigation Navigation
    {
        get;
    }

    public NPCAnimationTreeDriver Driver => Navigation.AnimationDriver
        ?? throw new InvalidOperationException("SETUP: the NPC navigation has no AnimationTree driver.");

    public AnimationGraphPlantedStateSource PlantedSource
    {
        get;
    }

    public static string SkeletonPathOf(NPCNavigationRig rig)
        => rig == NPCNavigationRig.Male ? "Male/GeneralSkeleton" : "Female/GeneralSkeleton";

    public static async Task<NPCNavigationRigFixture> CreateAsync(NPCNavigationRig rig)
    {
        SceneTree sceneTree = GetSceneTree();
        int previousStepLimit = Engine.MaxPhysicsStepsPerFrame;
        Engine.MaxPhysicsStepsPerFrame = 1;
        Node3D root = Assert.IsType<Node3D>(LoadPackedScene(NavigationTestScenePath).Instantiate(), exactMatch: false);
        if (rig == NPCNavigationRig.Male)
        {
            var controller = (DirectTransformNavigationPlaytestController)root;
            controller.CharacterVariant = NavigationPlaytestCharacterVariant.ReferenceMale;
            controller.ApplyCharacterVariantSelection();
        }

        Node3D actor = root.GetNode<Node3D>(ActorNodeName);
        PrepareActorBeforeTreeEntry(actor);
        sceneTree.Root.AddChild(root);
        NavigationRegion3D region = root.GetNode<NavigationRegion3D>("NavigationRegion3D");
        for (int tick = 0; tick < 600; tick++)
        {
            await WaitForNextPhysicsFrameAsync(sceneTree);
            Rid map = region.GetNavigationMap();
            if (map.IsValid
                && NavigationServer3D.MapGetIterationId(map) > 0
                && actor.GetNodeOrNull("Navigation") is NPCNavigation { IsRuntimeBindingActive: true })
            {
                return new NPCNavigationRigFixture(sceneTree, root, actor, rig, previousStepLimit);
            }
        }

        root.QueueFree();
        Engine.MaxPhysicsStepsPerFrame = previousStepLimit;
        throw new InvalidOperationException($"SETUP: the {rig} NPC navigation was not ready in time.");
    }

    /// <summary>Binds the independent test recorder to this NPC.</summary>
    public AnimationGraphPlantedStateRecorder BindRecorder()
        => AnimationGraphPlantedStateRecorder.Bind(
            Actor,
            Tree,
            Actor.GetNode<AnimationPlayer>("AnimationPlayer"),
            Skeleton);

    /// <summary>Issues a request and steps until its outcome, returning the terminal tick (zero on timeout).</summary>
    public async Task<ulong> RunRequestAsync(Vector3 destination, float yaw, string scenario, int timeoutTicks)
    {
        NavigationRequestIdentity identity = IssueRequest(Navigation, new ArrivalRequest(destination, yaw), scenario, 0);
        for (int tick = 0; tick < timeoutTicks; tick++)
        {
            await WaitForNextPhysicsFrameAsync(_sceneTree);
            if (FindOutcome(Navigation, identity) is not null)
            {
                return Engine.GetPhysicsFrames();
            }
        }

        return 0;
    }

    public Task StepAsync(int ticks) => WaitForPhysicsFramesAsync(_sceneTree, ticks);

    /// <summary>Lets the last tick's deferred skeleton update land.</summary>
    public Task SettleAsync() => WaitForFramesAsync(_sceneTree, 1);

    public async ValueTask DisposeAsync()
    {
        Engine.MaxPhysicsStepsPerFrame = _previousStepLimit;
        _root.QueueFree();
        await WaitForFramesAsync(_sceneTree, 1);
    }
}
