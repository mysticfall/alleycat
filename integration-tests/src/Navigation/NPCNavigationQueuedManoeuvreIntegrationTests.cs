using AlleyCat.Core.Installer;
using AlleyCat.IK;
using AlleyCat.Navigation;
using AlleyCat.Rigging.Installation;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>
/// Guards that a cancelled or replaced request leaves no queued pivot or side step for its successor. Each scenario
/// interrupts the first clip of a left 90° pivot chain in the navigation test scene and then requires a right turn:
/// the first pivot the successor plays must be a right pivot, reached through the authored idle hand-off.
/// </summary>
public sealed class NPCNavigationQueuedManoeuvreIntegrationTests
{
    private const string NavigationTestScenePath = "res://assets/testing/navigation/navigation_test.tscn";
    private const string ActorNodeName = "NavigationTestNpc";
    private const int ReadinessTimeoutPhysicsTicks = 600;
    private const int StageTimeoutPhysicsTicks = 600;
    private const int TicksIntoFirstPivot = 20;
    private static readonly StringName _pivotLeft = new("NPCPivotL");
    private static readonly StringName _pivotRight = new("NPCPivotR");
    private static readonly StringName _idle = new("NPCIdle");

    /// <summary>Cancel mid-way through a left pivot chain, then request a right turn.</summary>
    [Fact]
    public Task CancelDuringPivotChain_SuccessorStartsWithItsOwnPivot()
        => RunScenarioAsync(replace: false);

    /// <summary>Replace a left pivot chain mid-way with a right-turn request.</summary>
    [Fact]
    public Task ReplaceDuringPivotChain_SuccessorStartsWithItsOwnPivot()
        => RunScenarioAsync(replace: true);

    private static async Task RunScenarioAsync(bool replace)
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = Assert.IsType<Node3D>(LoadPackedScene(NavigationTestScenePath).Instantiate(), exactMatch: false);
        try
        {
            Node3D actor = root.GetNode<Node3D>(ActorNodeName);
            PrepareActorBeforeTreeEntry(actor);
            sceneTree.Root.AddChild(root);
            NPCNavigation navigation = await WaitForReadyNavigationAsync(sceneTree, root, actor);
            AnimationNodeStateMachinePlayback playback = actor.GetNode<AnimationTree>("AnimationTree")
                .Get("parameters/States/playback").As<AnimationNodeStateMachinePlayback>();
            await WaitForPhysicsFramesAsync(sceneTree, 30);

            float startYaw = YawOf(actor.GlobalBasis);
            Issue(navigation, actor, startYaw + (Mathf.Pi / 2f));
            await WaitUntilAsync(
                sceneTree,
                () => playback.GetCurrentNode() == _pivotLeft,
                "the first left pivot of the 90° chain did not start");
            await WaitForPhysicsFramesAsync(sceneTree, TicksIntoFirstPivot);
            Assert.Equal(_pivotLeft, playback.GetCurrentNode());

            if (!replace)
            {
                navigation.ClearDestination();
                await WaitUntilAsync(
                    sceneTree,
                    () => playback.GetCurrentNode() == _idle && playback.GetFadingFromNode().IsEmpty,
                    "the cancelled pivot did not return to idle");
            }

            Issue(navigation, actor, YawOf(actor.GlobalBasis) - (Mathf.Pi / 2f));

            // Record the successor's first pivot clip. Replacement must first finish the interrupted clip into idle.
            bool idleSeen = !replace;
            StringName? firstPivot = null;
            for (int tick = 0; tick < StageTimeoutPhysicsTicks && firstPivot is null; tick++)
            {
                await WaitForNextPhysicsFrameAsync(sceneTree);
                StringName node = playback.GetCurrentNode();
                if (!idleSeen)
                {
                    // A looping pivot may cross its loop point before the first aligned exit seam.
                    idleSeen = node == _idle;
                    continue;
                }

                if (node == _pivotLeft || node == _pivotRight)
                {
                    firstPivot = node;
                }
            }

            Assert.True(firstPivot is not null, "the successor request played no pivot clip");
            Assert.True(
                firstPivot == _pivotRight,
                $"the successor's first pivot was '{firstPivot}', expected '{_pivotRight}'; a stale queued manoeuvre from the {(replace ? "replaced" : "cancelled")} request played first");
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    private static void Issue(INavigation navigation, Node3D actor, float yaw)
    {
        var destination = new Transform3D(new Basis(Vector3.Up, yaw), actor.GlobalPosition);
        Assert.Equal(NavigationDestinationResult.Accepted, navigation.SetDestination(destination));
    }

    /// <summary>Yaw of the actor's forward (-Z) about +Y, matching <c>new Basis(Vector3.Up, yaw)</c>.</summary>
    private static float YawOf(Basis basis)
    {
        Vector3 forward = basis * Vector3.Forward;
        return Mathf.Atan2(-forward.X, -forward.Z);
    }

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> condition, string failure)
    {
        for (int tick = 0; tick < StageTimeoutPhysicsTicks; tick++)
        {
            if (condition())
            {
                return;
            }

            await WaitForNextPhysicsFrameAsync(sceneTree);
        }

        Assert.Fail(failure);
    }

    private static void PrepareActorBeforeTreeEntry(Node3D actor)
    {
        RigRoleTemplateSceneInstaller installer = actor.GetNode<RigRoleTemplateSceneInstaller>("NPCCharacterInstaller");
        installer.AutoInstallOnReady = false;
        SceneInstallationResult installation = installer.Install(new SceneInstallationContext(actor));
        Assert.True(installation.Succeeded, "SETUP: NPC installation failed:\n" + string.Join('\n', installation.Errors));

        if (actor.GetNodeOrNull("CharacterIK") is CharacterIK characterIK)
        {
            characterIK.HeadTargetIntentProvider = null;
            characterIK.HeadFallbackIntentProvider = null;
        }

        if (actor.GetNodeOrNull("Mind") is { } mind)
        {
            actor.RemoveChild(mind);
            mind.Free();
        }
    }

    private static async Task<NPCNavigation> WaitForReadyNavigationAsync(SceneTree sceneTree, Node3D root, Node3D actor)
    {
        NavigationRegion3D region = root.GetNode<NavigationRegion3D>("NavigationRegion3D");
        for (int tick = 0; tick < ReadinessTimeoutPhysicsTicks; tick++)
        {
            await WaitForNextPhysicsFrameAsync(sceneTree);
            Rid map = region.GetNavigationMap();
            if (actor.GetNodeOrNull("Navigation") is NPCNavigation { IsRuntimeBindingActive: true } navigation
                && map.IsValid
                && NavigationServer3D.MapGetIterationId(map) > 0)
            {
                return navigation;
            }
        }

        throw new InvalidOperationException("SETUP: NPC navigation or navigation map was not ready in time.");
    }
}
