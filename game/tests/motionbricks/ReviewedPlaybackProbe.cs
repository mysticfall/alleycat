using System.Text.Json;
using AlleyCat.Common;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using Godot;

namespace AlleyCat.Tests.Motion;

/// <summary>Explicit two-role evidence script on the normal NPC composition, never a gameplay controller.</summary>
public partial class ReviewedPlaybackProbe : Node
{
    private const double Horizon = .25;
    private MotionBricksNavigation? _nav;
    private CharacterBody3D? _actor;
    private Skeleton3D? _skeleton;
    private SomaMotionData? _data;
    private ReviewedMotionSource? _source;
    private ReviewedMotionSource.Role? _role;
    private NativeSomaRig? _observer;
    private readonly SourcePhaseClock _clock = new();
    private SourcePhaseClock.Selection? _selection;
    private readonly List<object> _samples = [], _requests = [], _events = [];
    private readonly Dictionary<long, string> _requestRoles = [];
    private readonly HashSet<string> _playedRoles = [];
    private Transform3D[] _observed = [], _window = [];
    private double _began, _roleBegan, _nextRequest, _playbackAt = -1;
    private long _playbackRequest;
    private double _playbackFrame;
    private bool _started, _switched, _released;
    private Vector3 _rawRoot;
    private readonly CompleteForwardCapture _capture = new();
    private bool _completeEndpoint, _exhaustionLogged;
    private int _completeFrames;

    /// <summary>Explicit single-request diagnostic only. Set before entering the tree; never a gameplay option.</summary>
    public bool CompleteForward
    {
        get; set;
    }
    /// <summary>Proves the diagnostic observer was attached before normal worker initialisation.</summary>
    public int CapturedNavigationNodes
    {
        get; private set;
    }

    /// <inheritdoc/>
    public override void _EnterTree()
    {
        if (CompleteForward)
        {
            GetTree().NodeAdded += Added;
        }
    }

    private void Added(Node node)
    {
        if (node is MotionBricksNavigation nav)
        {
            CapturedNavigationNodes++;
            CompleteForwardCapture capture = _capture;
            nav.SessionDecorator = session => capture.Wrap((SomaInference)session);
        }
    }

    /// <inheritdoc/>
    public string State { get; private set; } = "initial";
    /// <inheritdoc/>
    public bool Finished
    {
        get; private set;
    }
    /// <inheritdoc/>
    public bool Passed => Finished && Failure == "" && (CompleteForward
        ? _completeEndpoint && _released && _playedRoles.SetEquals(["forward"]) && _capture.Records.Length == 2
        : _playedRoles.SetEquals(["forward", "left_noncross"]));
    /// <inheritdoc/>
    public string Failure { get; private set; } = "";
    /// <inheritdoc/>
    public double Time => _nav?.ObservationTime ?? 0;
    /// <inheritdoc/>
    public double Settled
    {
        get; private set;
    }

    /// <inheritdoc/>
    public void Begin(Node actor)
    {
        _actor = (CharacterBody3D)actor;
        _nav = (MotionBricksNavigation)((Character.Character)actor).Navigation!;
        _skeleton = actor.RequireNode<Skeleton3D>("Female/SOMA/Native/GeneralSkeleton");
        _data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
        _source = new ReviewedMotionSource(ProjectSettings.GlobalizePath(
            "res://temp/motionbricks/source-pipeline-05/reviewed-ingestion-01/reviewed-source.json"), _data);
        _observer = new NativeSomaRig(_data, _skeleton);
        _observed = new Transform3D[_data.Names.Length];
        _window = new Transform3D[_data.Names.Length * 4];
        _began = Time;
        _nav.TargetWindowOverride = Target;
        _nav.WindowSubmitted += Submitted;
        _nav.PoseObserved += Observe;
        _nav.PlaybackObserved = (request, poses, frame) =>
        {
            _playbackAt = Time;
            _playbackRequest = request;
            _playbackFrame = frame;
            if (poses is not null && _requestRoles.TryGetValue(request, out string? role) && role != "idle")
            {
                _ = _playedRoles.Add(role);
                if (CompleteForward && role == "forward")
                {
                    _completeFrames = poses.Length;
                    _completeEndpoint |= frame >= poses.Length - 1;
                }
            }
        };
    }

    /// <inheritdoc/>
    public override void _PhysicsProcess(double delta)
    {
        if (_nav is null || Finished)
        {
            return;
        }
        if (_nav.Failure != "" || Time - _began > 65)
        {
            Failure = _nav.Failure != "" ? _nav.Failure : "Complete observation exceeded 65 seconds.";
            Finished = true;
            return;
        }
        if (!_started)
        {
            if (Time - _began >= 1 && _nav.NativePhase == "load-exit" && _nav.SamplesRecorded >= 8)
            {
                _started = true;
                Activate("forward");
            }
            return;
        }
        if (CompleteForward && !_released)
        {
            SourcePhaseClock.Selection current = _clock.Select(Time, Horizon);
            if (!current.Available && !_exhaustionLogged)
            {
                _exhaustionLogged = true;
                _events.Add(new
                {
                    time = Time,
                    reason = "finite_source_exhausted_no_more_submissions",
                    selection = current
                });
            }
            if (_completeEndpoint)
            {
                Release(current, "complete_native_endpoint_release");
            }
        }
        if (!CompleteForward && !_switched && Time - _roleBegan >= 1.4)
        {
            _switched = true;
            Activate("left_noncross");
        }
        if (!CompleteForward && !_released)
        {
            SourcePhaseClock.Selection current = _clock.Select(Time, Horizon);
            if (!current.Available)
            {
                Release(current);
            }
            else if (Time >= _nextRequest)
            {
                Resubmit();
            }
        }
        if (_released && _nav.Phase == "settled-idle")
        {
            Settled += delta;
            State = "settled-idle";
            Finished = Settled >= 3.2;
        }
    }

    private void Activate(string name)
    {
        _role = _source!.GetRole("reviewed_probe", "normal", name);
        _roleBegan = Time;
        int initialFrame = CompleteForward ? 64 : _role.First;
        _clock.Activate("reviewed_probe/normal/" + name, initialFrame, _role.Last, Time);
        State = name;
        _events.Add(new
        {
            time = Time,
            reason = "explicit_role_initialisation",
            role = name,
            transfer = "unverified_fresh_initialisation_not_idle_reset",
            first = _role.First,
            last = _role.Last,
            initialFrame
        });
        Resubmit();
    }

    private void Resubmit()
    {
        // The probe's explicit replacement opportunities are not normal NPC/player scheduling policy.
        _nextRequest = Time + .6;
        NavigationDestinationResult result = _nav!.SetDestination(new Transform3D(Basis.Identity,
            _actor!.GlobalPosition + new Vector3(0, 0, -2)));
        if (result != NavigationDestinationResult.Accepted)
        {
            Failure = "Normal route facade refused probe destination: " + result;
            Finished = true;
        }
    }

    private void Release(SourcePhaseClock.Selection current, string reason = "non_cyclic_exhaustion_release")
    {
        _events.Add(new
        {
            time = Time,
            reason,
            selection = current
        });
        _released = true;
        _clock.Stop(Time);
        _selection = null;
        _nav!.ClearDestination();
        State = "idle-transition";
    }

    private Transform3D[][]? Target(double time, Transform3D[][] context)
    {
        SourcePhaseClock.Selection selected = _clock.Select(time, Horizon);
        if (!selected.Available)
        {
            Release(selected);
            return null;
        }
        if (CompleteForward && selected.WindowStart != 72)
        {
            throw new InvalidOperationException("Predetermined complete-forward request must select original frames 72–75.");
        }
        _selection = selected;
        _source!.CopyWindow(_role!, selected.WindowStart, _window);
        _rawRoot = _window[0].Origin;
        Vector3 origin = Ground(_rawRoot);
        Vector3 placement = Ground(context[3][0].Origin) + (Ground(_role!.ReferenceVelocity) * (float)Horizon);
        var target = new Transform3D[4][];
        for (int f = 0; f < 4; f++)
        {
            target[f] = _window.AsSpan(f * _source.JointCount, _source.JointCount).ToArray();
            target[f][0].Origin = placement + (target[f][0].Origin - origin);
        }
        return target;
    }

    private void Submitted(long request, Transform3D[][] context, Transform3D[][] target)
    {
        bool idle = _released;
        string role = idle ? "idle" : _role!.Name;
        _requestRoles[request] = role;
        _requests.Add(new
        {
            request,
            time = Time,
            role,
            selection = idle ? null : _selection,
            horizonSeconds = idle ? 0 : Horizon,
            sourceRoot = Vec(_rawRoot),
            physicalReference = idle ? null : new
            {
                velocity = Vec(_role!.ReferenceVelocity),
                speed = _role.ReferenceSpeed
            },
            sourceIdentity = _source!.Manifest.GetProperty("descriptor").GetProperty("source"),
            context = context.Select(Pose).ToArray(),
            target = target.Select(Pose).ToArray(),
            actualSamples = _nav!.SamplesRecorded
        });
    }

    private void Observe()
    {
        _observer!.Observe(_observed);
        Vector3 Bone(string name)
        {
            return _skeleton!.GlobalTransform * _skeleton.GetBoneGlobalPose(_skeleton.FindBone(name)).Origin;
        }

        Vector3 hip = Bone("LeftUpperLeg"), knee = Bone("LeftLowerLeg"), foot = Bone("LeftFoot");
        if (!hip.IsFinite() || !knee.IsFinite() || !foot.IsFinite() || hip.DistanceTo(knee) < .1f || knee.DistanceTo(foot) < .1f)
        {
            Failure = "Actual displayed leg has non-finite/collapsed anatomy.";
            Finished = true;
        }
        _samples.Add(new
        {
            time = Time,
            tick = Engine.GetPhysicsFrames(),
            state = State,
            phase = _nav!.Phase,
            source = _clock.Select(Time, Horizon),
            native = _nav.NativePhase,
            actor = Vec(_actor!.GlobalPosition),
            playbackRequest = _playbackRequest,
            playbackFrame = _playbackFrame,
            appliedThisTick = _playbackAt == Time,
            canonical = Pose(_observed),
            head = Vec(Bone("Head")),
            hip = Vec(hip),
            knee = Vec(knee),
            leftFoot = Vec(foot),
            rightFoot = Vec(Bone("RightFoot")),
            leftHand = Vec(Bone("LeftHand"))
        });
    }

    /// <inheritdoc/>
    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            state = State,
            Finished,
            Passed,
            Failure,
            settledSeconds = Settled,
            playedRoles = _playedRoles,
            discarded = _nav!.DiscardedResults,
            requests = _requests,
            samples = _samples,
            events = _events
        }));
        if (CompleteForward)
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "native-arrays.json"),
                JsonSerializer.Serialize(new
                {
                    generations = _capture.Records,
                    forwardFrames = _completeFrames,
                    forwardDurationSeconds = (_completeFrames - 1) / 30.0,
                    decodedCoordinates = "canonical world placement / model BONES local rotations before endpoint correction",
                    correctedCoordinates = "canonical identity-rest local rotations after existing endpoint correction"
                }));
        }
    }

    private static Vector3 Ground(Vector3 p) => new(p.X, 0, p.Z);
    private static float[] Vec(Vector3 p) => [p.X, p.Y, p.Z];
    private static float[][] Pose(Transform3D[] pose) => [.. pose.Select(p =>
    {
        Quaternion q = p.Basis.GetRotationQuaternion();
        return new[] { p.Origin.X, p.Origin.Y, p.Origin.Z, q.X, q.Y, q.Z, q.W };
    })];

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        if (CompleteForward)
        {
            GetTree().NodeAdded -= Added;
        }
        if (_nav is not null && IsInstanceValid(_nav))
        {
            _nav.WindowSubmitted -= Submitted;
            _nav.PoseObserved -= Observe;
            _nav.TargetWindowOverride = null;
            _nav.PlaybackObserved = null;
        }
    }
}
