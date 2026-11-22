using System.Collections.Concurrent;
using System.Text.Json;
using AlleyCat.Common;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using Godot;

namespace AlleyCat.Tests.MotionBricks;

/// <summary>Passive predicted-contact evidence. Never supplies motion, contact correction or scene-thread waits.</summary>
public partial class MotionBricksContactObserver : Node
{
    private sealed class WorkerEvidence
    {
        public readonly ConcurrentDictionary<Transform3D[][], float[][]> Contacts = new();

        public void Capture(Transform3D[][] poses, ReadOnlyMemory<float> raw)
        {
            float[][] contacts = new float[poses.Length][];
            for (int f = 0; f < poses.Length; f++)
            {
                contacts[f] = raw.Span.Slice(((f + 3) * 366) + 362, 4).ToArray();
            }
            Contacts[poses] = contacts;
        }
    }

    private readonly WorkerEvidence _worker = new();
    private readonly List<object> _samples = [];
    private readonly Dictionary<long, float[][]> _generations = [];
    private MotionBricksNavigation? _nav;
    private Skeleton3D? _skeleton;
    private long _request;
    private double _frame;
    private Transform3D[][]? _poses;
    private ulong _start;
    private int[] _bones = [];

    /// <inheritdoc/>
    public override void _EnterTree() => GetTree().NodeAdded += Added;

    private void Added(Node node)
    {
        if (node is not MotionBricksNavigation nav)
        {
            return;
        }
        WorkerEvidence evidence = _worker;
        nav.SessionDecorator = session =>
        {
            ((SomaInference)session).GenerationObserved = evidence.Capture;
            return session;
        };
        nav.PlaybackObserved = (request, poses, frame) =>
        {
            _request = request;
            _poses = poses;
            _frame = frame;
        };
    }

    /// <summary>Called after ordinary role installation. SkeletonUpdated supplies the actually displayed pose.</summary>
    public void Begin(Node actor)
    {
        _nav = (MotionBricksNavigation)((Character.Character)actor).Navigation!;
        _skeleton = actor.RequireNode<Skeleton3D>("Female/SOMA/Native/GeneralSkeleton");
        _bones = [.. new[] { "LeftFoot", "LeftToes", "RightFoot", "RightToes" }.Select(_skeleton.FindBone)];
        if (_bones.Any(b => b < 0))
        {
            throw new InvalidDataException("Calibrated heel/toe target bones are required.");
        }
        _start = Time.GetTicksUsec();
        _skeleton.SkeletonUpdated += Observe;
    }

    private void Observe()
    {
        float[]? confidence = null;
        int lo = (int)Math.Floor(_frame), hi = lo;
        if (_poses is not null)
        {
            float[][] raw = _worker.Contacts[_poses];
            _ = _generations.TryAdd(_request, raw);
            hi = Math.Min(lo + 1, raw.Length - 1);
            confidence = [.. Enumerable.Range(0, 4).Select(c => Mathf.Lerp(
                Math.Clamp(raw[lo][c], 0, 1), Math.Clamp(raw[hi][c], 0, 1), (float)(_frame - lo)))];
        }
        float[][] points = [.. _bones.Select(b =>
        {
            Vector3 p = _skeleton!.GlobalTransform * _skeleton.GetBoneGlobalPose(b).Origin;
            return new[] { p.X, p.Y, p.Z };
        })];
        _samples.Add(new
        {
            time = (Time.GetTicksUsec() - _start) / 1e6,
            tick = Engine.GetPhysicsFrames(),
            phase = _nav!.Phase,
            request = _request,
            frame = _frame,
            rawLo = lo + 3,
            rawHi = hi + 3,
            alpha = _frame - lo,
            confidence,
            points
        });
    }

    /// <summary>Write raw channels and observed world-metre points to a fresh caller-owned directory.</summary>
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        channels = new[] { "left heel/LeftFoot", "left toe/LeftToes", "right heel/RightFoot", "right toe/RightToes" },
        generations = _generations,
        samples = _samples
    }));

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        GetTree().NodeAdded -= Added;
        if (_skeleton is not null && IsInstanceValid(_skeleton))
        {
            _skeleton.SkeletonUpdated -= Observe;
        }
        if (_nav is not null && IsInstanceValid(_nav))
        {
            _nav.PlaybackObserved = null;
        }
    }
}
