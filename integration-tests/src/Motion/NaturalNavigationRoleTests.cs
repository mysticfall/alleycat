using System.Reflection;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Recording;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>NAV's nearby pure lateral positioning selects non-crossing source intent before guide placement.</summary>
    [Headless]
    [Fact]
    public async Task NearbyLeftPositioningSelectsNonCrossingBeforeTargetConstruction()
    {
        await using PositioningNavigationFixture fixture = await PositioningNavigationFixture.CreateAsync();
        MotionBricksNavigation nav = fixture.Navigation;
        Assert.Same(nav, fixture.Actor.Navigation);
        var roles = new List<string>();
        var contextChecks = new List<bool>();
        var unsafeSweeps = new List<(Vector3 From, Vector3 To)>();
        Vector3 start = fixture.Actor.GlobalPosition;
        float heading = fixture.Heading;
        Vector3 goal = start + new Vector3(Mathf.Cos(heading), 0, -Mathf.Sin(heading));
        Assert.True(fixture.OnMapSegment(start, goal));
        Vector3 prior = start;
        nav.PoseObserved += () =>
        {
            Vector3 current = fixture.Actor.GlobalPosition;
            if (!fixture.OnMapSegment(prior, current))
            {
                unsafeSweeps.Add((prior, current));
            }
            prior = current;
        };
        nav.WindowSubmitted += (_, context, _) =>
        {
            contextChecks.Add(fixture.MatchesHistory(context) && context[3][0].Origin == nav.ObservedRoot.Origin);
            if (nav.ReviewedRole.Length > 0)
            {
                roles.Add(nav.ReviewedRole);
            }
        };

        await fixture.SelectDestinationAsync(new(new Basis(Vector3.Up, heading - Mathf.Pi), goal));
        // Preserve the original 12 s observation, up to 600 ticks to terminate, and 3 s post-request tail.
        await fixture.StepAsync(12 * PositioningNavigationFixture.TicksPerSecond);
        for (int i = 0; i < 600 && (nav.TerminalOutcomes.Count == 0 || nav.Phase is not ("settled-idle" or "failed")); i++)
        {
            await fixture.StepAsync(1);
        }
        await fixture.StepAsync(3 * PositioningNavigationFixture.TicksPerSecond);
        Assert.Equal(0, fixture.Provider.Faults);
        Assert.False(fixture.ReadField<bool>("_unavailable"));
        Assert.Empty(unsafeSweeps);
        Assert.NotEmpty(contextChecks);
        Assert.All(contextChecks, Assert.True);
        Assert.NotEmpty(roles);
        Assert.Equal("left_noncross", roles[0]);
    }

    /// <summary>The actual playtest floor and map, with only the native motion rig and navigation component.</summary>
    private sealed class PositioningNavigationFixture : IAsyncDisposable
    {
        internal const int TicksPerSecond = 60;
        private const double Delta = 1.0 / TicksPerSecond;
        private readonly SceneTree _tree = TestUtils.GetSceneTree();
        private readonly int _originalPhysicsTicks = Engine.PhysicsTicksPerSecond;
        private readonly ProbeGame _root = new() { Name = "PositioningNavigationFixture" };
        private readonly Rid _map = NavigationServer3D.MapCreate();
        private readonly Skeleton3D _source;
        private readonly Skeleton3D _target;
        private readonly NavigationRegion3D _region;
        private readonly Vector2[][] _floorPolygons;
        private readonly SomaMotionData _data = new(ProjectSettings.GlobalizePath("res://assets/animations/motionbricks/poses.json"));

        private PositioningNavigationFixture()
        {
            Node playtest = GD.Load<PackedScene>(NavigationPlaytestRecorder.PlaytestScene).Instantiate();
            _region = playtest.GetNode<NavigationRegion3D>("NavigationRegion3D");
            StaticBody3D floor = playtest.GetNode<StaticBody3D>("ClickTargetGround");
            playtest.RemoveChild(_region);
            playtest.RemoveChild(floor);
            playtest.Free(); // No recorder, camera or role installer is entered into the tree.
            _root.AddChild(_region);
            _root.AddChild(floor);
            NavigationMesh mesh = _region.NavigationMesh;
            Vector3[] vertices = mesh.GetVertices();
            _floorPolygons = [.. Enumerable.Range(0, mesh.GetPolygonCount())
                .Select(i => mesh.GetPolygon(i)).Where(p => p.Length == 4)
                .Select(p => p.Select(i => Flat(vertices[i])).ToArray())];

            Actor = new()
            {
                Position = new(-2, 0, -2)
            };
            Actor.AddChild(MotionBricksIntegrationTests.MountMotionModel(false, out _target));
            _source = _target.GetParent().GetParent<Skeleton3D>();
            typeof(AlleyCat.Character.Character).GetField("_motionTarget", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Actor, _target);
            Actor.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = .2f }, Position = new(0, .2f, 0) });
            // Navigation resolves locomotion and idles through the character's catalogue (ANIM-003).
            Actor.AddChild(MotionFixtureCatalogue.Create(npc: true));
            Navigation = new()
            {
                SessionFactory = () => new LifecycleSession(Provider, false)
            };
            Navigation.Ready += () => Navigation.SetPhysicsProcess(false);
            Actor.Navigation = Navigation;
            Actor.AddChild(Navigation);
            _root.AddChild(Actor);
        }

        internal AlleyCat.Character.Character Actor
        {
            get;
        }
        internal MotionBricksNavigation Navigation
        {
            get;
        }
        internal LifecycleProviderState Provider { get; } = new();
        internal float Heading => Hip(History.Context()[3]) + Mathf.Pi;
        private RealisedPoseHistory History => ReadField<RealisedPoseHistory>("_history");
        private SomaInferenceWorker Worker => ReadField<SomaInferenceWorker>("_worker");

        internal T ReadField<T>(string name) =>
            (T)typeof(MotionBricksNavigation).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Navigation)!;

        internal bool MatchesHistory(Transform3D[][] context) =>
            context.Zip(History.Context()).All(pair => pair.First.SequenceEqual(pair.Second));

        internal float Hip(Transform3D[] pose)
        {
            int left = Array.IndexOf(_data.Names, "LeftLeg"), right = Array.IndexOf(_data.Names, "RightLeg");
            return SomaInference.Heading(pose[0] * pose[right].Origin, pose[0] * pose[left].Origin);
        }

        internal static async Task<PositioningNavigationFixture> CreateAsync()
        {
            var fixture = new PositioningNavigationFixture();
            try
            {
                fixture._tree.Root.AddChild(fixture._root);
                NavigationServer3D.MapSetActive(fixture._map, true);
                fixture._region.SetNavigationMap(fixture._map);
                fixture.Navigation.SetNavigationMap(fixture._map);
                for (int i = 0; i < 40 && NavigationServer3D.MapGetClosestPointOwner(fixture._map, fixture.Actor.GlobalPosition) != fixture._region.GetRid(); i++)
                {
                    await TestUtils.WaitForNextPhysicsFrameAsync(fixture._tree);
                }
                Assert.Equal(fixture._region.GetRid(), NavigationServer3D.MapGetClosestPointOwner(fixture._map, fixture.Actor.GlobalPosition));
                Assert.Equal("", fixture.Navigation.Failure);
                fixture._source.ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Manual;
                fixture._target.ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Manual;
                // The native agent and skeleton use distinct engine tick boundaries; only the
                // scheduling is accelerated, not the explicit controller or skeleton delta.
                Engine.PhysicsTicksPerSecond = 600;
                await fixture.StepAsync(16);
                Assert.True(fixture.Navigation.SamplesRecorded >= 8);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal async Task SelectDestinationAsync(Transform3D goal)
        {
            // Agent target assignment wakes automatic processing. Keep controller and rig time test-owned.
            Assert.Equal((int)NavigationDestinationResult.Accepted, Navigation.SelectDestination(goal, []));
            Navigation.SetPhysicsProcess(false);
            await TestUtils.WaitForNextPhysicsFrameAsync(_tree);
        }

        internal async Task StepAsync(int count)
        {
            for (int i = 0; i < count; i++)
            {
                await TestUtils.WaitForNextPhysicsFrameAsync(_tree);
                int requests = Navigation.Requests;
                Navigation._PhysicsProcess(Delta);
                _source.Advance(Delta);
                _target.Advance(Delta);
                if (Navigation.Requests != requests && ReadField<bool>("_pending"))
                {
                    await WaitForMotionPublicationAsync(_tree, Worker);
                }
            }
        }

        private static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

        internal bool OnMapSegment(Vector3 from, Vector3 to)
        {
            Vector2 a = Flat(from), b = Flat(to), delta = b - a;
            var cuts = new SortedSet<float> { 0, 1 };
            foreach (Vector2 p in _floorPolygons.SelectMany(p => p))
            {
                if (delta.X != 0 && (p.X - a.X) / delta.X is > 0 and < 1)
                {
                    _ = cuts.Add((p.X - a.X) / delta.X);
                }
                if (delta.Y != 0 && (p.Y - a.Y) / delta.Y is > 0 and < 1)
                {
                    _ = cuts.Add((p.Y - a.Y) / delta.Y);
                }
            }
            bool OnMap(Vector2 p)
            {
                return _floorPolygons.Any(poly => p.X >= poly.Min(v => v.X) && p.X <= poly.Max(v => v.X) &&
                    p.Y >= poly.Min(v => v.Y) && p.Y <= poly.Max(v => v.Y));
            }
            return OnMap(a) && OnMap(b) && cuts.Zip(cuts.Skip(1)).All(c => OnMap(a.Lerp(b, (c.First + c.Second) / 2)));
        }

        public async ValueTask DisposeAsync()
        {
            Navigation.SetPhysicsProcess(false);
            Engine.PhysicsTicksPerSecond = _originalPhysicsTicks;
            await _root.DrainMotionAndDiagnosticsAsync();
            _root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(_tree, 2);
            NavigationServer3D.FreeRid(_map);
        }
    }
}
