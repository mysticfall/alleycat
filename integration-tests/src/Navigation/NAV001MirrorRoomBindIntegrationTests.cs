using AlleyCat.Navigation;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>
/// Bind-crash regression (user-reported): loading mirror_room.tscn — which instances the NON-reference
/// Vadim NPC rig (reference_male tree, no pivot-continuation alias states) — must bind its NPC
/// navigation cleanly through the deferred installer chain instead of crashing at
/// ActivateRuntimeBinding's continuation validation. RED before the availability-gated fix: the bind
/// threw KeyNotFoundException ('NPCPivotLContinuation') and IsRuntimeBindingActive never became true.
/// </summary>
public sealed class NAV001MirrorRoomBindIntegrationTests
{
    private const string MirrorRoomScenePath = "res://assets/testing/mirror_room/mirror_room.tscn";
    private const int BindTimeoutFrames = 240;

    [Fact]
    public async Task MirrorRoom_VadimNpcBindsNavigationWithoutCrash()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = Assert.IsType<Node3D>(LoadPackedScene(MirrorRoomScenePath).Instantiate(), exactMatch: false);
        sceneTree.Root.AddChild(root);

        try
        {
            NPCNavigation? bound = null;
            for (int frame = 0; frame < BindTimeoutFrames; frame++)
            {
                await WaitForFramesAsync(sceneTree, 1);
                foreach (NPCNavigation navigation in root.FindChildren("*", "", true, false).OfType<NPCNavigation>())
                {
                    if (navigation.IsRuntimeBindingActive)
                    {
                        bound = navigation;
                        break;
                    }
                }

                if (bound is not null)
                {
                    break;
                }
            }

            Assert.True(bound is not null,
                "mirror_room's Vadim NPC never completed its runtime navigation binding (bind crashed or never activated); " +
                "see the engine log for the ActivateRuntimeBinding failure.");
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }
}
