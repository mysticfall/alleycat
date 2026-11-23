using System.Reflection;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Recording;
using AlleyCat.TestFramework;
using Godot;
using Godot.Collections;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>First real completion followed by an ordinary controller floor-ray destination, without ONNX.</summary>
    [Headless]
    [Fact]
    public async Task SourceRateCurve_CompletedTripAllowsSecondFloorDestination()
    {
        await using RepeatNavigationFixture fixture = await RepeatNavigationFixture.CreateAsync();
        using var ray = PhysicsRayQueryParameters3D.Create(new Vector3(0, 2, -.5f), new Vector3(0, -2, -.5f), 1);
        await fixture.TripAsync(new Transform3D(Basis.Identity, fixture.GroundHit(ray)), 1, verifyBeforeTail: true);
        // Fixed ordinary pixel from the original scenario, not an injected ground height.
        await fixture.TripAsync(fixture.ControllerGoal(new(40, 40)), 2);
    }

    private sealed class RepeatNavigationFixture : IAsyncDisposable
    {
        private const int TicksPerSecond = 60;
        private readonly IDisposable _acceleration = TestUtils.AcceleratePhysicsSimulation(5);
        private readonly SceneTree _tree = TestUtils.GetSceneTree();
        private readonly ProbeGame _root = new() { Name = "RepeatNavigationFixture" };
        private readonly Node _scene = GD.Load<PackedScene>(NavigationPlaytestRecorder.PlaytestScene).Instantiate();
        private MotionBricksNavigation? _navigation;

        private RepeatNavigationFixture(Vector3? position)
        {
            Actor = _scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
            if (position is { } start)
            {
                Actor.Position = start;
            }
            // The ray/controller is the integration boundary. Preserve its recorder references
            // without activating capture, which only runs for a direct child of the playtest.
            DeactivateNavigationRecording(_scene);
            _root.AddChild(_scene);
        }

        private AlleyCat.Character.Character Actor
        {
            get;
        }
        private MotionBricksNavigation Navigation => Assert.IsType<MotionBricksNavigation>(_navigation);
        internal Camera3D Camera => _scene.GetNode<Camera3D>("Camera3D");

        internal static async Task<RepeatNavigationFixture> CreateAsync(Vector3? position = null)
        {
            var fixture = new RepeatNavigationFixture(position);
            fixture._tree.NodeAdded += fixture.Configure;
            try
            {
                fixture._tree.Root.AddChild(fixture._root);
                for (int i = 0; i < 40 && (fixture._navigation?.SamplesRecorded ?? 0) < 8; i++)
                {
                    await TestUtils.WaitForNextPhysicsFrameAsync(fixture._tree);
                }
                Assert.True(fixture.Navigation.SamplesRecorded >= 8);
                fixture.Navigation.SetPhysicsProcess(false);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
            finally
            {
                fixture._tree.NodeAdded -= fixture.Configure;
            }
        }

        private void Configure(Node node)
        {
            if (node is MotionBricksNavigation nav && nav.GetParent() == Actor)
            {
                _navigation = nav;
                nav.SessionFactory = () => new TargetEndpointSession(true);
            }
        }

        internal Vector3 GroundHit(PhysicsRayQueryParameters3D ray)
        {
            Dictionary hit = Actor.GetWorld3D().DirectSpaceState.IntersectRay(ray);
            Assert.NotEmpty(hit);
            Assert.Equal("ClickTargetGround", ((Node)hit["collider"]).Name.ToString());
            return (Vector3)hit["position"];
        }

        internal Transform3D ControllerGoal(Vector2 pixel)
        {
            Vector3 rayStart = Camera.ProjectRayOrigin(pixel);
            using var ray = PhysicsRayQueryParameters3D.Create(rayStart, rayStart + (Camera.ProjectRayNormal(pixel) * 1000), 1);
            Vector3 hit = GroundHit(ray);
            var goal = (Vector3)_scene.Call("ground_at", pixel);
            Assert.Equal(hit, goal);
            var facing = (Basis)_scene.Call("_facing", goal, goal);
            Assert.NotEmpty(NavigationServer3D.MapGetPath(Navigation.GetNavigationMap(), Actor.GlobalPosition, goal, true, Navigation.NavigationLayers));
            return new(facing, goal);
        }

        internal async Task TripAsync(Transform3D target, int expectedOutcomes, bool verifyBeforeTail = false)
        {
            MotionBricksNavigation nav = Navigation;
            Assert.Equal((int)NavigationDestinationResult.Accepted, nav.SelectDestination(target, []));
            nav.SetPhysicsProcess(false);
            var worker = (SomaInferenceWorker)typeof(MotionBricksNavigation)
                .GetField("_worker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)!;
            for (int tick = 0; tick < 600 && nav.TerminalOutcomes.Count < expectedOutcomes && nav.Failure.Length == 0; tick++)
            {
                await StepAsync(nav, worker);
            }
            if (verifyBeforeTail)
            {
                CheckCompleted(nav, expectedOutcomes);
            }
            for (int tick = 0; tick < 3 * TicksPerSecond; tick++)
            {
                await StepAsync(nav, worker);
            }
            CheckCompleted(nav, expectedOutcomes);
        }

        private async Task StepAsync(MotionBricksNavigation nav, SomaInferenceWorker worker)
        {
            // Physics worlds and the real skeleton callback still need engine boundaries. Explicit
            // controller delta and worker publication keep wait duration out of simulated motion time.
            await TestUtils.WaitForNextPhysicsFrameAsync(_tree);
            int requests = nav.Requests;
            nav._PhysicsProcess(1.0 / TicksPerSecond);
            if (nav.Requests != requests && (bool)typeof(MotionBricksNavigation)
                .GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)!)
            {
                await WaitForMotionPublicationAsync(_tree, worker);
            }
        }

        private static void CheckCompleted(MotionBricksNavigation nav, int expected)
        {
            Assert.Equal("", nav.Failure);
            Assert.Equal(expected, nav.TerminalOutcomes.Count);
            Assert.Equal(NavigationTerminalOutcome.Completed, nav.TerminalOutcomes[expected - 1].Outcome);
            Assert.Equal("settled-idle", nav.Phase);
        }

        public async ValueTask DisposeAsync()
        {
            _navigation?.SetPhysicsProcess(false);
            _acceleration.Dispose();
            await _root.DrainMotionAndDiagnosticsAsync();
            _root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(_tree, 2);
        }
    }

    private static void DeactivateNavigationRecording(Node scene)
    {
        var host = new Node { Name = "InactiveRecorderHost" };
        scene.AddChild(host);
        scene.GetNode<Node>("SessionRecorder").Reparent(host);
    }
}
