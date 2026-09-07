using AlleyCat.IK;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Navigation;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Bounded failure reporting for terminal ANIM-002 package errors (ANIM-002 delivery TR13, IK-003 TR12): a latched
/// failure produces exactly one diagnostic per installation attempt with no per-physics-tick re-read, while genuinely
/// incomplete installer wiring keeps its readiness retry.
/// </summary>
public sealed class ContactLockPackageFailureLatchingIntegrationTests
{
    private const int SimulatedPhysicsTicks = 12;
    private const float PhysicsDeltaSeconds = 1f / 60f;

    /// <summary>A broken package is reported once and never re-parsed on later physics ticks of the same attempt.</summary>
    [Fact]
    public async Task TerminalPackageFailure_IsLatchedOnceAndNotReReadOnLaterPhysicsTicks()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(
            nameof(TerminalPackageFailure_IsLatchedOnceAndNotReReadOnLaterPhysicsTicks));
        string brokenCataloguePath = WriteStaleV2Catalogue(fixture);
        SceneTree sceneTree = GetSceneTree();
        Node3D npc = BuildWiredNpc(brokenCataloguePath);
        var composition = new NPCContactLockPlaytestComposition { NPC = npc };
        sceneTree.Root.AddChild(npc);
        sceneTree.Root.AddChild(composition);
        try
        {
            await WaitForNextFrameAsync(sceneTree);

            for (int tick = 0; tick < SimulatedPhysicsTicks; tick++)
            {
                composition._PhysicsProcess(PhysicsDeltaSeconds);
            }

            Assert.False(composition.IsInstalled);
            Assert.True(composition.IsTerminalPackageFailureLatched);
            Assert.Equal(1, composition.TerminalPackageFailureDiagnosticCount);
        }
        finally
        {
            composition.QueueFree();
            npc.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>Missing installers or nodes stay a legitimate readiness retry and install once provided.</summary>
    [Fact]
    public async Task IncompleteInstallerWiring_RetriesUntilNodesAreProvidedAndThenInstalls()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(
            nameof(IncompleteInstallerWiring_RetriesUntilNodesAreProvidedAndThenInstalls));
        SceneTree sceneTree = GetSceneTree();
        var npc = new Node3D { Name = "NavigationTestNpc" };
        var navigation = new NPCNavigation
        {
            Name = "Navigation",
            CataloguePath = fixture.NavigationCataloguePath,
            AwaitInstallerActivation = true,
        };
        npc.AddChild(navigation);
        var composition = new NPCContactLockPlaytestComposition { NPC = npc };
        sceneTree.Root.AddChild(npc);
        sceneTree.Root.AddChild(composition);
        try
        {
            await WaitForNextFrameAsync(sceneTree);

            for (int tick = 0; tick < 3; tick++)
            {
                composition._PhysicsProcess(PhysicsDeltaSeconds);
            }

            Assert.False(composition.IsInstalled);
            Assert.False(composition.IsTerminalPackageFailureLatched);
            Assert.Equal(0, composition.TerminalPackageFailureDiagnosticCount);

            npc.AddChild(new CharacterIK
            {
                Name = "CharacterIK",
                LeftFootIKTarget = NewFootTarget(npc, "LeftFootTarget"),
                RightFootIKTarget = NewFootTarget(npc, "RightFootTarget"),
            });
            composition._PhysicsProcess(PhysicsDeltaSeconds);

            Assert.True(composition.IsInstalled);
            Assert.NotNull(composition.Coordinator);
            Assert.False(composition.IsTerminalPackageFailureLatched);
            Assert.Equal(0, composition.TerminalPackageFailureDiagnosticCount);
        }
        finally
        {
            composition.QueueFree();
            npc.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Navigation runtime binding degrades (instead of throwing forever) for a broken package, reports once, and a new
    /// explicit installation attempt through a corrected catalogue path recovers.
    /// </summary>
    [Fact]
    public async Task NavigationRuntimeBinding_DegradesOnceAndRecoversOnNewInstallationAttempt()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(
            nameof(NavigationRuntimeBinding_DegradesOnceAndRecoversOnNewInstallationAttempt));
        SyntheticNavigationRuntime runtime = fixture.CreateNavigationRuntime();
        SceneTree sceneTree = GetSceneTree();
        sceneTree.Root.AddChild(runtime.Root);
        try
        {
            await WaitForFramesAsync(sceneTree, 2);
            runtime.Navigation.CataloguePath = WriteStaleV2Catalogue(fixture);

            for (int attempt = 0; attempt < 5; attempt++)
            {
                runtime.Navigation.ActivateRuntimeBinding();
            }

            Assert.False(runtime.Navigation.IsRuntimeBindingActive);
            Assert.True(runtime.Navigation.IsRuntimeBindingPackageFailureLatched);
            Assert.Equal(1, runtime.Navigation.RuntimeBindingPackageFailureDiagnosticCount);

            runtime.Navigation.CataloguePath = fixture.NavigationCataloguePath;
            runtime.Navigation.ActivateRuntimeBinding();

            Assert.True(runtime.Navigation.IsRuntimeBindingActive);
            Assert.Equal(1, runtime.Navigation.RuntimeBindingPackageFailureDiagnosticCount);
        }
        finally
        {
            runtime.Root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>Downgrades the healthy synthetic catalogue to the stale schema version 2 clip-path contract.</summary>
    internal static string WriteStaleV2Catalogue(SyntheticLocomotionRuntimeFixture fixture)
    {
        string brokenPath = fixture.NavigationCataloguePath.Replace(
            "navigation_catalogue.json",
            "stale_v2_catalogue.json",
            StringComparison.Ordinal);
        string healthyJson = File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationCataloguePath));
        File.WriteAllText(
            ProjectSettings.GlobalizePath(brokenPath),
            healthyJson.Replace("\"schema_version\":3", "\"schema_version\":2", StringComparison.Ordinal));
        return brokenPath;
    }

    private static Node3D BuildWiredNpc(string cataloguePath)
    {
        var npc = new Node3D { Name = "NavigationTestNpc" };
        npc.AddChild(new CharacterIK
        {
            Name = "CharacterIK",
            LeftFootIKTarget = NewFootTarget(npc, "LeftFootTarget"),
            RightFootIKTarget = NewFootTarget(npc, "RightFootTarget"),
        });
        npc.AddChild(new NPCNavigation
        {
            Name = "Navigation",
            CataloguePath = cataloguePath,
            AwaitInstallerActivation = true,
        });
        return npc;
    }

    private static Node3D NewFootTarget(Node parent, string name)
    {
        var target = new Node3D { Name = name };
        parent.AddChild(target);
        return target;
    }
}
