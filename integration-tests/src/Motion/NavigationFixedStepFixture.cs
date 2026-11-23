using System.Reflection;
using System.Threading.Channels;
using AlleyCat.Common;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    private sealed class NavigationFixedStepFixture : IAsyncDisposable
    {
        public const double Delta = 1.0 / 60;
        public static readonly Vector3 CornerStart = new(-.5f, 0, -.4f);
        public static readonly Vector3 CornerVia = new(.5f, 0, -2.5f);
        public static readonly Vector3 CornerGoal = new(.75f, 0, -3.5f);
        public static readonly Vector3 Corner = new(0, 0, -1);
        private readonly SceneTree _tree = TestUtils.GetSceneTree();
        private readonly ProbeGame _root = new() { Name = "NavigationFixedStepFixture" };
        private readonly Rid _map = NavigationServer3D.MapCreate();
        private readonly NavigationRegion3D _region = new() { NavigationMesh = MakeMesh() };
        private readonly Skeleton3D _source;
        private readonly Skeleton3D _target;
        private readonly int _originalPhysicsTicks = Engine.PhysicsTicksPerSecond;

        private NavigationFixedStepFixture(Vector3 position, float yaw)
        {
            Actor = new()
            {
                Position = position,
                Rotation = new(0, yaw, 0)
            };
            Node3D model = MountNavigationModel(out _source, out _target);
            Actor.AddChild(model);
            typeof(AlleyCat.Character.Character).GetField("_motionTarget", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Actor, _target); // Only replace role installation; production navigation and native feedback run normally.
            Actor.AddChild(new CollisionShape3D
            {
                Position = new(0, .5f, 0),
                Shape = new BoxShape3D { Size = new(.2f, 1, .2f) }
            });
            Navigation = new()
            {
                SessionFactory = () => Provider
            };
            Navigation.Ready += () => Navigation.SetPhysicsProcess(false);
            Actor.AddChild(Navigation);
            Actor.Navigation = Navigation;
            _root.AddChild(_region);
            _root.AddChild(Actor);
        }

        public AlleyCat.Character.Character Actor
        {
            get;
        }
        public MotionBricksNavigation Navigation
        {
            get;
        }
        public NavigationFixedStepProvider Provider { get; } = new();
        public NavigationRouteSnapshot Route => RefusalRoute(Navigation) ?? throw new InvalidOperationException("No active native route snapshot.");
        public T NavigationField<T>(string name) => (T)typeof(MotionBricksNavigation)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Navigation)!;

        public static async Task<NavigationFixedStepFixture> CreateAsync(Vector3 position, float yaw, Vector3? wall = null)
        {
            var fixture = new NavigationFixedStepFixture(position, yaw);
            try
            {
                if (wall is { } centre)
                {
                    var body = new StaticBody3D { Position = centre };
                    body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new(2, 2, .2f) } });
                    fixture._root.AddChild(body);
                }
                fixture._tree.Root.AddChild(fixture._root);
                NavigationServer3D.MapSetActive(fixture._map, true);
                fixture._region.SetNavigationMap(fixture._map);
                fixture.Navigation.SetNavigationMap(fixture._map);
                // Engine map upload is asynchronous; no controller time elapses during this startup barrier.
                await TestUtils.WaitForPhysicsFramesAsync(fixture._tree, 2);
                for (int i = 0; i < 20 &&
                    NavigationServer3D.MapGetClosestPointOwner(fixture._map, position) != fixture._region.GetRid(); i++)
                {
                    await TestUtils.WaitForPhysicsFramesAsync(fixture._tree, 1);
                }
                Assert.Equal("", fixture.Navigation.Failure);
                Assert.True(fixture._region.GetRid() == NavigationServer3D.MapGetClosestPointOwner(fixture._map, position),
                    $"Map {fixture._map}: iteration {NavigationServer3D.MapGetIterationId(fixture._map)}, " +
                    $"regions {string.Join(", ", NavigationServer3D.MapGetRegions(fixture._map))}, " +
                    $"region map {NavigationServer3D.RegionGetMap(fixture._region.GetRid())}.");
                fixture._source.ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Manual;
                fixture._target.ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Manual;
                // Native path-index updates are limited to distinct engine ticks. The controller and skin
                // still advance explicitly at Delta; this rate does not set worker wait deadlines.
                Engine.PhysicsTicksPerSecond = 600;
                for (int i = 0; i < 16; i++)
                {
                    await fixture.StepAsync();
                }
                Assert.True(fixture.Navigation.SamplesRecorded >= 8);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task StepAsync()
        {
            if (Navigation.Phase != "settled-idle" || Navigation.HasDestination)
            {
                await TestUtils.WaitForNextPhysicsFrameAsync(_tree);
            }
            Navigation._PhysicsProcess(Delta);
            _source.Advance(Delta);
            _target.Advance(Delta);
            // Godot enqueues the modifier/skin update. Fence it in the same deferred queue before the next tick.
            var skinReady = new TaskCompletionSource();
            Callable.From(skinReady.SetResult).CallDeferred();
            await skinReady.Task;
        }

        public async Task ReleaseAsync(long ticket, bool expectDiscard = false, bool alreadyStarted = false)
        {
            if (!alreadyStarted)
            {
                await Provider.WaitForStartAsync();
            }
            SomaInferenceWorker worker = NavigationField<SomaInferenceWorker>("_worker");
            int? discardedBefore = expectDiscard ? worker.DiscardedResults : null;
            Provider.Release();
            await WaitForMotionPublicationAsync(_tree, worker, ticket, discardedBefore);
        }

        public async ValueTask DisposeAsync()
        {
            Engine.PhysicsTicksPerSecond = _originalPhysicsTicks;
            try
            {
                Task? completion = Navigation.WorkerCompletion;
                _root.QueueFree();
                await TestUtils.WaitForPhysicsFramesAsync(_tree, 2);
                Provider.ReleaseForShutdown();
                if (completion is not null)
                {
                    await completion.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            finally
            {
                NavigationServer3D.FreeRid(_map);
            }
        }

        private static Node3D MountNavigationModel(out Skeleton3D source, out Skeleton3D target)
        {
            var mountHost = new Node3D();
            mountHost.AddChild(new AnimationPlayer { Name = "AnimationPlayer" });
            Node asset = GD.Load<PackedScene>("res://assets/characters/reference/female/reference_female.blend").Instantiate();
            try
            {
                Node3D model = asset.RequireNode<Node3D>("Female");
                model.Reparent(mountHost, keepGlobalTransform: false);
                target = model.RequireNode<Skeleton3D>("GeneralSkeleton");
                using GodotObject mount = GD.Load<GDScript>("res://src/Motion/native_soma_mount.gd").New().AsGodotObject();
                Assert.True(mount.Call("install", mountHost, target, "Female", false).AsBool());
                source = target.GetParent().GetParent<Skeleton3D>();
                mountHost.RemoveChild(model);
                return model;
            }
            finally
            {
                asset.Free();
                mountHost.Free();
            }
        }

        private static NavigationMesh MakeMesh()
        {
            // A 1 m-wide L, with a missing inside quadrant; there is no navigable straight chord.
            var mesh = new NavigationMesh
            {
                Vertices =
                [
                    new(-3, 0, -1), Corner, new(0, 0, 0), new(-3, 0, 0),
                    new(1, 0, -1), new(1, 0, 0), new(0, 0, -4), new(1, 0, -4)
                ]
            };
            mesh.AddPolygon([0, 1, 2, 3]);
            mesh.AddPolygon([1, 4, 5, 2]);
            mesh.AddPolygon([6, 7, 4, 1]);
            return mesh;
        }
    }

    private sealed class NavigationFixedStepProvider : SomaInferenceWorker.ISession
    {
        private readonly Channel<bool> _releases = Channel.CreateUnbounded<bool>();
        private readonly Channel<bool> _starts = Channel.CreateUnbounded<bool>();
        public bool FailNext
        {
            get; set;
        }
        public int Frames { get; set; } = 50;
        public Func<Transform3D[][], Transform3D[][], int, Transform3D[][]>? Response
        {
            get; set;
        }

        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            _ = _starts.Writer.TryWrite(true);
            return _releases.Reader.ReadAsync().AsTask().GetAwaiter().GetResult()
                ? GenerateReleased(context, target)
                : [context[3], context[3]];
        }

        private Transform3D[][] GenerateReleased(Transform3D[][] context, Transform3D[][] target)
            => FailNext ? throw new InvalidOperationException("Navigation fixture provider failure") :
                Response is { } response ? response(context, target, Frames) :
                [.. Enumerable.Range(0, Frames).Select(i => context[3].Select(
                    (pose, joint) => SomaMotionData.Blend(pose, target[0][joint], i / (float)(Frames - 1))).ToArray())];

        public async Task WaitForStartAsync() =>
            _ = await _starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        public void Release() => _releases.Writer.TryWrite(true);
        public void ReleaseForShutdown() => _releases.Writer.TryWrite(false);

        public void Dispose()
        {
        }
    }
}
