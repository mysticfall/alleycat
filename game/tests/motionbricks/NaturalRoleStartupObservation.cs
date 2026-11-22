using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Tests.Motion;

/// <summary>Passive startup ordering wrapper; the inherited observer retains the original request/window semantics.</summary>
public partial class NaturalRoleStartupObservation : NaturalRoleObservation
{
    private sealed record Stamp(long Clock, string Kind, string Message, long? ProductionClock = null);
    private readonly ConcurrentQueue<Stamp> _stamps = new();
    private readonly List<object> _startupPoses = [];
    private MotionBricksNavigation? _navigation;
    private Node3D? _actor;
    private Skeleton3D? _skeleton;
    private string? _startupPath;

    private sealed class PhaseLogger(ConcurrentQueue<Stamp> stamps, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level))
            {
                return;
            }

            long? productionClock = null;
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
            {
                foreach (KeyValuePair<string, object?> field in fields)
                {
                    if (field.Key == "Clock" && field.Value is long clock)
                    {
                        productionClock = clock;
                    }
                }
            }

            stamps.Enqueue(new(Stopwatch.GetTimestamp(), category, formatter(state, error), productionClock));
        }
    }
    private sealed class PhaseProvider(ConcurrentQueue<Stamp> stamps) : ILoggerProvider
    {
        public ILogger CreateLogger(string category) => category is "AlleyCat.Navigation.MotionBricksNavigation" or "AlleyCat.Game"
            ? new PhaseLogger(stamps, category) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public void Dispose()
        {
        }
    }
    private sealed class NoInference : SomaInferenceWorker.ISession
    {
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target) => throw new InvalidOperationException("Ordering rehearsal never submits a destination.");
        public void Dispose()
        {
        }
    }

    /// <inheritdoc/>
    public override void _EnterTree()
    {
        base._EnterTree();
        GetTree().NodeAdded += AddedForOrdering;
    }
    /// <summary>Arm the passive production logger before any normal scene/NPC exists.</summary>
    public void ArmRecording()
    {
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(new PhaseProvider(_stamps));
        Mark("recorder-armed");
    }
    /// <summary>Mark a scene-thread capture/lifecycle boundary using the same monotonic clock as production logs.</summary>
    public void Mark(string stage) => _stamps.Enqueue(new(Stopwatch.GetTimestamp(), "harness", stage));

    /// <summary>Attach observations only, before the unchanged complete scene is inserted normally.</summary>
    public void AttachScene(Node scene)
    {
        _actor = scene.GetNode<Node3D>("NavigationTestNpc");
        if (_actor.IsInsideTree())
        {
            throw new InvalidOperationException("Startup must be observed before scene insertion.");
        }

        _actor.Ready += () => Mark("npc-ready");
        _skeleton = _actor.GetNode<Skeleton3D>("Female/SOMA/Native/GeneralSkeleton");
        _skeleton.SkeletonUpdated += ObserveStartup;
        Mark("startup-observers-attached");
    }
    private void AddedForOrdering(Node node)
    {
        if (node is not MotionBricksNavigation nav)
        {
            return;
        }

        _navigation = nav;
        Mark("navigation-added-before-ready");
        nav.Ready += () => Mark("navigation-ready");
        if (Framing)
        {
            ConcurrentQueue<Stamp> stamps = _stamps;
            nav.SessionFactory = () =>
            {
                stamps.Enqueue(new(Stopwatch.GetTimestamp(), "rehearsal", "framing-factory-enter"));
                return new NoInference();
            };
        }
    }
    private void ObserveStartup()
    {
        if (_navigation is null || _actor is null)
        {
            return;
        }

        _startupPoses.Add(new
        {
            clock = Stopwatch.GetTimestamp(),
            wall = Wall,
            tick = Engine.GetPhysicsFrames(),
            _navigation.Phase,
            _navigation.NativePhase,
            _navigation.SamplesRecorded,
            actor = new[] { _actor.GlobalPosition.X, _actor.GlobalPosition.Y, _actor.GlobalPosition.Z },
            observedRoot = _navigation.ObservedRoot,
            _navigation.PositioningPlacementIntent
        });
    }
    /// <summary>Keep the original Begin arithmetic/timing; separately identify this boundary in the startup record.</summary>
    public void BeginObserved(Node actor)
    {
        Mark("observer-begin-enter");
        Begin(actor);
        Mark("observer-begin-exit");
    }
    /// <summary>Save startup clocks and production phase logs; called again after normal worker-drained scene exit.</summary>
    public void SaveStartup(string path)
    {
        _startupPath = path;
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            frequency = Stopwatch.Frequency,
            wall = Wall,
            stamps = _stamps.ToArray(),
            poses = _startupPoses
        },
            new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
    }
    /// <inheritdoc/>
    public override void _ExitTree()
    {
        Mark("observer-exit-tree");
        if (_startupPath is { } path)
        {
            SaveStartup(path);
        }

        GetTree().NodeAdded -= AddedForOrdering;
        if (_skeleton is not null && IsInstanceValid(_skeleton))
        {
            _skeleton.SkeletonUpdated -= ObserveStartup;
        }

        base._ExitTree();
    }
}
