using System.Reflection;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    private sealed class LifecycleProviderState
    {
        public int Created, Calls, Faults;
    }
    private sealed class LifecycleSession : SomaInferenceWorker.ISession
    {
        private readonly LifecycleProviderState _state;
        private readonly bool _throws;
        private readonly TargetEndpointSession _inner = new(true);
        public LifecycleSession(LifecycleProviderState state, bool throws)
        {
            _state = state;
            _throws = throws;
            _ = Interlocked.Increment(ref state.Created);
        }
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            _ = Interlocked.Increment(ref _state.Calls);
            if (_throws)
            {
                _ = Interlocked.Increment(ref _state.Faults);
                throw new InvalidDataException("Synthetic lifecycle control provider-generation failure");
            }
            return _inner.Generate(context, target);
        }
        public void Dispose() => _inner.Dispose();
    }

    /// <summary>Actual worker errors remain latched unavailable, not a routing refusal or an idle/retry.</summary>
    [Headless]
    [Fact]
    public async Task GenuineProviderFailureRemainsUnavailableAndLatched()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame();
        Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
        MotionBricksNavigation? nav = null;
        var provider = new LifecycleProviderState();
        int idleCount = 0;
        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation n)
            {
                return;
            }
            nav = n;
            n.SessionFactory = () => new LifecycleSession(provider, true);
            n.WindowSubmitted += (_, _, _) =>
            {
                object packet = typeof(MotionBricksNavigation).GetField("_nativePending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(n)!;
                if ((bool)packet.GetType().GetProperty("Idle")!.GetValue(packet)!)
                {
                    idleCount++;
                }
            };
        }
        tree.NodeAdded += Configure;
        root.AddChild(scene);
        tree.Root.AddChild(root);
        try
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 40);
            Assert.NotNull(nav);
            AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
            Vector3 goal = actor.GlobalPosition + new Vector3(0, 0, -2);
            Assert.Equal(0, nav.SelectDestination(new(Basis.Identity, goal), []));
            for (int i = 0; i < 120 && nav.TerminalOutcomes.Count < 1; i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            Assert.Equal("failed", nav.Phase);
            string firstFailure = nav.Failure;
            Assert.Contains("Synthetic lifecycle control provider-generation failure", firstFailure);
            Assert.Equal(0, nav.SelectDestination(new(Basis.Identity, goal + Vector3.Left), []));
            for (int i = 0; i < 120 && nav.TerminalOutcomes.Count < 2; i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            Assert.Equal(2, nav.TerminalOutcomes.Count);
            Assert.All(nav.TerminalOutcomes, o => Assert.Equal(NavigationTerminalReason.MotionUnavailable, o.Reason));
            Assert.Equal(firstFailure, nav.Failure);
            Assert.Equal(0, idleCount);
            Assert.Equal(1, Volatile.Read(ref provider.Calls));
            Assert.Equal(1, Volatile.Read(ref provider.Faults));
        }
        finally
        {
            tree.NodeAdded -= Configure;
            nav?.SetPhysicsProcess(false);
            await root.DrainMotionAndDiagnosticsAsync();
            root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
        }
    }
}
