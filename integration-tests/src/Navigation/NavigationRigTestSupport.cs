using AlleyCat.Control.Locomotion;
using AlleyCat.Core.Installer;
using AlleyCat.IK;
using AlleyCat.Navigation;
using AlleyCat.Rigging.Installation;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>Shared normal-rig setup and public request observation, independent of any test class.</summary>
internal static class NavigationRigTestSupport
{
    internal const string NavigationTestScenePath = "res://assets/testing/navigation/navigation_test.tscn";
    internal const string ActorNodeName = "NavigationTestNpc";
    internal const string SkeletonPath = "Female/GeneralSkeleton";
    internal const int WarmUpPhysicsTicks = 30;

    internal static void PrepareActorBeforeTreeEntry(Node3D actor)
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

    internal static async Task<INavigation> WaitForReadyNavigationAsync(SceneTree sceneTree, Node3D root, Node3D actor)
    {
        NavigationRegion3D region = root.GetNode<NavigationRegion3D>("NavigationRegion3D");
        for (int tick = 0; tick < 600; tick++)
        {
            await WaitForNextPhysicsFrameAsync(sceneTree);
            Rid map = region.GetNavigationMap();
            if (actor.GetNodeOrNull("Navigation") is INavigation navigation
                && map.IsValid && NavigationServer3D.MapGetIterationId(map) > 0
                && actor.GetNodeOrNull<Skeleton3D>(SkeletonPath) is not null)
            {
                return navigation;
            }
        }
        throw new InvalidOperationException("SETUP: navigation component, navigation map, or skeleton was not ready in time.");
    }

    internal static AnimationGraphPlantedStateRecorder BindRecorder(Node3D actor) => AnimationGraphPlantedStateRecorder.Bind(
        actor, actor.GetNode<AnimationTree>("AnimationTree"), actor.GetNode<AnimationPlayer>("AnimationPlayer"),
        actor.GetNode<Skeleton3D>(SkeletonPath));

    internal static void AssertIdleWarmUp(AnimationGraphPlantedStateRecorder recorder, Node3D actor)
    {
        Assert.NotEmpty(recorder.Samples);
        PlantedStateSample idle = recorder.Samples[^1];
        Assert.True(idle.Graph.Fading is null && idle.Contributions.Length == 1 && idle.Planted == new PlantedFeetState(true, true),
            "SETUP: expected settled, fully planted single-clip Idle.");
        Assert.True(actor.GlobalPosition.Length() < 0.001f && Mathf.Abs(Mathf.RadToDeg(YawOf(actor.GlobalTransform))) < 0.05f);
    }

    internal readonly record struct ArrivalRequest(Vector3 Destination, float YawRadians);

    internal static NavigationRequestIdentity IssueRequest(INavigation navigation, ArrivalRequest request, string scenario, int index)
    {
        var destination = new Transform3D(new Basis(Vector3.Up, request.YawRadians), request.Destination);
        Assert.True(navigation.SetDestination(destination) == NavigationDestinationResult.Accepted,
            $"[{scenario}] request {index + 1} was refused.");
        NavigationRequestIdentity? identity = navigation.CurrentStatus.ActiveRequest;
        Assert.True(identity.HasValue, "Accepted request has no identity.");
        return identity!.Value;
    }

    internal static NavigationTerminalOutcomeRecord? FindOutcome(INavigation navigation, NavigationRequestIdentity identity)
    {
        for (int index = navigation.TerminalOutcomes.Count - 1; index >= 0; index--)
        {
            if (navigation.TerminalOutcomes[index].Request == identity)
            {
                return navigation.TerminalOutcomes[index];
            }
        }
        return null;
    }

    internal static float YawOf(Transform3D transform)
    {
        Vector3 forward = transform.Basis * Vector3.Forward;
        return Mathf.Atan2(-forward.X, -forward.Z);
    }
}
