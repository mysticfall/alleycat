using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using Godot;

namespace AlleyCat.Tests.Motion;

/// <summary>One passive native nearby-left observation; no role, pose, source, route or target overrides.</summary>
public partial class NaturalRoleObservation : Node
{
    /// <summary>Framing-only mode cannot create or call a native session.</summary>
    public bool Framing
    {
        get; set;
    }
    /// <summary>Complete window and tail are finished, or the explicitly reported safety ceiling was reached.</summary>
    public bool Finished
    {
        get; private set;
    }
    /// <summary>Wall time includes initial idle and every native cold hold.</summary>
    public double Wall => _clock.Elapsed.TotalSeconds;
    /// <summary>Current production phase.</summary>
    public string Phase => _nav?.Phase ?? "not-installed";
    /// <summary>Current native worker phase.</summary>
    public string NativePhase => _nav?.NativePhase ?? "not-started";
    /// <summary>Frozen actual initial centre.</summary>
    public Vector3 Start
    {
        get; private set;
    }
    /// <summary>One metre left of the actual initial anatomical forward.</summary>
    public Vector3 Goal
    {
        get; private set;
    }
    /// <summary>Actual initial anatomical forward.</summary>
    public Vector3 Forward
    {
        get; private set;
    }
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<object> _samples = [], _windows = [], _events = [], _handoffs = [];
    private MotionBricksNavigation? _nav;
    private Character.Character? _actor;
    private Skeleton3D? _skeleton;
    private Transform3D _destination;
    private double _began = -1, _submitted = -1, _tail = -1;
    private int _nativeCalls;
    private string _endReason = "not-started";
    private sealed class NoNative : SomaInferenceWorker.ISession
    {
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target) => throw new InvalidOperationException("Framing cannot generate.");
        public void Dispose()
        {
        }
    }
    private sealed class Counted(SomaInferenceWorker.ISession inner, Action count) : SomaInferenceWorker.ISession
    {
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            count();
            return inner.Generate(context, target);
        }
        public void Dispose() => inner.Dispose();
    }
    /// <inheritdoc/>
    public override void _EnterTree() => GetTree().NodeAdded += Added;
    private void Added(Node node)
    {
        if (node is not MotionBricksNavigation nav)
        {
            return;
        }

        _nav = nav;
        if (Framing)
        {
            nav.SessionFactory = () => new NoNative();
        }
        else
        {
            nav.SessionDecorator = inner => new Counted(inner, () => Interlocked.Increment(ref _nativeCalls));
        }

        nav.WindowSubmitted += (ticket, context, target) =>
        {
            Transform3D[][] history = Field<RealisedPoseHistory>("_history").Context();
            _windows.Add(new
            {
                wall = Wall,
                ticket,
                nav.SubmittedRole,
                nav.RoleDecision,
                nav.SourceRateCurveIntent,
                fromActual = context.Zip(history).All(p => p.First.SequenceEqual(p.Second)),
                roots = target.Select(p => Pack(SomaMotionData.ModelToWorld * p[0].Origin)).ToArray()
            });
        };
        nav.NativeHandoffObserved = h => _handoffs.Add(new { wall = Wall, h.Request, h.PreviousRequest, h.Revision, h.Accepted, h.Reason, h.Age, h.OldRemaining });
    }
    private T Field<T>(string name) => (T)typeof(MotionBricksNavigation).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_nav)!;
    private float Heading() => (float)typeof(MotionBricksNavigation).GetMethod("AnatomicalHeading", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_nav, [Field<RealisedPoseHistory>("_history").Context()[3]])! + Mathf.Pi;
    private static float[] Pack(Vector3 p) => [p.X, p.Y, p.Z];
    /// <summary>Called after normal Ready and forty physics frames; never moves the actor.</summary>
    public void Begin(Node actor)
    {
        _actor = (Character.Character)actor;
        if (_nav is null || _actor.Navigation != _nav)
        {
            throw new InvalidOperationException("Normal NPC navigation was not captured.");
        }

        _skeleton = actor.GetNode<Skeleton3D>("Female/GeneralSkeleton");
        _skeleton.SkeletonUpdated += Observe;
        float heading = Heading();
        Start = _actor.GlobalPosition;
        Forward = new(Mathf.Sin(heading), 0, Mathf.Cos(heading));
        Goal = Start + new Vector3(Mathf.Cos(heading), 0, -Mathf.Sin(heading));
        _destination = new(new Basis(Vector3.Up, heading - Mathf.Pi), Goal);
        _began = Wall;
        _events.Add(new
        {
            kind = "initial-actual",
            wall = Wall,
            heading,
            start = Pack(Start),
            goal = Pack(Goal),
            facing = Pack(Forward),
            Framing
        });
    }
    private void Observe() => _samples.Add(new
    {
        wall = Wall,
        tick = Engine.GetPhysicsFrames(),
        _nav!.ObservationTime,
        _nav.Phase,
        _nav.NativePhase,
        _nav.Failure,
        actor = Pack(_actor!.GlobalPosition),
        heading = Heading(),
        _nav.ReviewedRole,
        _nav.RoleDecision,
        source = Field<ReviewedRoleSampler>("_reviewed").Selection,
        outcomes = _nav.TerminalOutcomes.ToArray(),
        unavailable = Field<bool>("_unavailable"),
        route = _nav.CurrentPath.Select(Pack).ToArray()
    });
    /// <inheritdoc/>
    public override void _PhysicsProcess(double delta)
    {
        if (_began < 0 || Framing || Finished)
        {
            return;
        }

        if (_submitted < 0 && Wall - _began >= 2)
        {
            _submitted = Wall;
            int accepted = _nav!.SelectDestination(_destination, []);
            _events.Add(new
            {
                kind = "selected",
                wall = Wall,
                accepted,
                goal = Pack(Goal)
            });
        }
        if (_submitted < 0)
        {
            return;
        }

        if (Wall - _submitted >= 12 && _nav!.TerminalOutcomes.Count != 0 && Phase is "settled-idle" or "failed")
        {
            if (_tail < 0)
            {
                _tail = Wall;
                _events.Add(new
                {
                    kind = "tail-start",
                    wall = Wall
                });
            }
            if (Wall - _tail >= 3)
            {
                Finished = true;
                _endReason = "complete-request-window-and-terminal-three-second-tail";
            }
        }
        else
        {
            _tail = -1;
        }

        if (Wall - _submitted >= 120)
        {
            Finished = true;
            _endReason = "safety-ceiling-incomplete";
        }
    }
    /// <summary>Save immutable observer evidence; no controller or source state is changed.</summary>
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        Framing,
        Finished,
        endReason = _endReason,
        wall = Wall,
        began = _began,
        selected = _submitted,
        tailStart = _tail,
        nativeCalls = Volatile.Read(ref _nativeCalls),
        injectedRolePoseRouteTargetPhase = false,
        _events,
        _windows,
        _handoffs,
        _samples,
        final = new
        {
            Phase,
            NativePhase,
            _nav!.Failure,
            outcomes = _nav.TerminalOutcomes.ToArray()
        }
    }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
    /// <inheritdoc/>
    public override void _ExitTree()
    {
        GetTree().NodeAdded -= Added;
        if (_skeleton is not null && IsInstanceValid(_skeleton))
        {
            _skeleton.SkeletonUpdated -= Observe;
        }
    }
}
