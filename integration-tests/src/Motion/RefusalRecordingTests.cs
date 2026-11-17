using System.Reflection;
using System.Text.Json;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Recording;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>A labelled synthetic event verifies sink isolation/current native-route recording, not gameplay failure.</summary>
    [Headless]
    [Fact]
    public async Task NativeRouteRefusalRecordingSurvivesThrowingObserver()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame();
        Node scene = GD.Load<PackedScene>(NavigationPlaytestRecorder.PlaytestScene).Instantiate();
        MotionBricksNavigation? nav = null;
        JsonElement? observed = null;
        int throws = 0;
        void Throw(JsonElement _)
        {
            throws++;
            throw new InvalidOperationException("Synthetic refusal sink failure");
        }
        void Observe(JsonElement value)
        {
            observed = value;
        }

        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation n)
            {
                return;
            }
            nav = n;
            n.Ready += () => n.SetPhysicsProcess(false);
            n.SessionFactory = () => new TargetEndpointSession(true);
            n.OrdinaryRefusalObserved += Throw;
            n.OrdinaryRefusalObserved += Observe;
        }
        tree.NodeAdded += Configure;
        root.AddChild(scene);
        tree.Root.AddChild(root);
        try
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 40);
            Assert.NotNull(nav);
            AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
            Assert.Equal(0, nav.SelectDestination(new(Basis.Identity, actor.GlobalPosition + new Vector3(0, 0, -2)), []));
            _ = nav.Poll(nav.RecordingMeasuredTransform);
            _ = typeof(MotionBricksNavigation).GetMethod("PublishOrdinaryRefusal", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(nav, ["synthetic-observer-contract"]);
            Assert.Equal(1, throws);
            _ = Assert.NotNull(observed);
            Assert.Equal(3, observed.Value.GetProperty("schema").GetInt32());
            Assert.Equal("Godot NavigationAgent3D", observed.Value.GetProperty("authority").GetString());
            Assert.True(observed.Value.GetProperty("route").GetProperty("owned").GetBoolean());
            Assert.False(observed.Value.TryGetProperty("admission", out _));
            Assert.True(nav.HasDestination);
            Assert.Empty(nav.TerminalOutcomes);
            Assert.Equal(0, nav.Requests);
            await root.DrainMotionAndDiagnosticsAsync();
            NavigationPlaytestRecorder recorder = scene.GetNode<NavigationPlaytestRecorder>("SessionRecorder");
            NavigationSessionReader.Entry item = Assert.Single(NavigationSessionReader.ReadCheckpoint(recorder.SessionPath), e => e.Event.GetProperty("kind").GetString() == "navigation-refusal");
            Assert.Equal(observed.Value.GetRawText(), item.Event.GetProperty("data").GetProperty("data").GetRawText());
        }
        finally
        {
            tree.NodeAdded -= Configure;
            if (nav is not null)
            {
                nav.SetPhysicsProcess(false);
                nav.OrdinaryRefusalObserved -= Throw;
                nav.OrdinaryRefusalObserved -= Observe;
            }
            await root.DrainMotionAndDiagnosticsAsync();
            root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
        }
    }
}
