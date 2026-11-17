using System.Text.Json;
using AlleyCat.Navigation;
using Godot;

namespace AlleyCat.Tests.MotionBricks;

/// <summary>Passive evidence only: never writes poses, moves the actor or advances the skeleton.</summary>
public partial class MotionBricksRouteObserver : Node
{
    private MotionBricksNavigation? _nav;
    private Skeleton3D? _skeleton;
    private CharacterBody3D? _actor;
    private readonly List<object> _samples = [];
    private readonly List<object> _requests = [];
    private ulong _start;

    /// <summary>Subscribe after normal role installation, before any request.</summary>
    public void Begin(Node actor)
    {
        _actor = (CharacterBody3D)actor;
        _nav = ((Character.Character)actor).Navigation as MotionBricksNavigation
            ?? throw new InvalidOperationException("Normal role did not expose MotionBricks navigation.");
        _skeleton = actor.GetNode<Skeleton3D>("Female/GeneralSkeleton");
        _start = Time.GetTicksUsec();
        _skeleton.SkeletonUpdated += Observe;
        _nav.WindowSubmitted += Request;
    }

    private void Request(long id, Transform3D[][] context, Transform3D[][] target) => _requests.Add(new
    {
        id,
        time = (Time.GetTicksUsec() - _start) / 1e6,
        samples = _nav!.SamplesRecorded,
        context = context.Select(Pose).ToArray(),
        target = target.Select(Pose).ToArray(),
        route = _nav.CurrentPath.Select(Vec).ToArray()
    });

    private static float[][] Pose(Transform3D[] pose) => [.. pose.Select(t =>
    {
        Quaternion q = t.Basis.GetRotationQuaternion();
        return new[] { t.Origin.X, t.Origin.Y, t.Origin.Z, q.X, q.Y, q.Z, q.W };
    })];

    private void Observe()
    {
        Vector3 Bone(string name)
        {
            return _skeleton!.GlobalTransform * _skeleton.GetBoneGlobalPose(_skeleton.FindBone(name)).Origin;
        }

        Vector3 hip = Bone("LeftUpperLeg"), knee = Bone("LeftLowerLeg"), foot = Bone("LeftFoot");
        if (!hip.IsFinite() || !knee.IsFinite() || hip.DistanceTo(knee) < 0.1f || knee.DistanceTo(foot) < 0.1f)
        {
            throw new InvalidDataException("Non-finite/collapsed displayed leg anatomy.");
        }
        _samples.Add(new
        {
            time = (Time.GetTicksUsec() - _start) / 1e6,
            tick = Engine.GetPhysicsFrames(),
            phase = _nav!.Phase,
            actor = Vec(_actor!.GlobalPosition),
            hips = Pose([_nav.ObservedRoot])[0],
            head = Vec(Bone("Head")),
            leftHip = Vec(hip),
            rightHip = Vec(Bone("RightUpperLeg")),
            leftKnee = Vec(knee),
            leftFoot = Vec(foot),
            rightFoot = Vec(Bone("RightFoot")),
            leftHand = Vec(Bone("LeftHand")),
            rightHand = Vec(Bone("RightHand"))
        });
    }

    private static float[] Vec(Vector3 value) => [value.X, value.Y, value.Z];

    /// <summary>Write to the caller's fresh run directory.</summary>
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        failure = _nav!.Failure,
        phase = _nav.Phase,
        samples = _samples,
        requests = _requests,
        outcomes = _nav.TerminalOutcomes
    }, new JsonSerializerOptions { WriteIndented = true }));

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        if (_nav is not null && IsInstanceValid(_nav))
        {
            _skeleton!.SkeletonUpdated -= Observe;
            _nav.WindowSubmitted -= Request;
        }
    }
}
