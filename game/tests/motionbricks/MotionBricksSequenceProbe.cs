using System.Text.Json;
using AlleyCat.Common;
using AlleyCat.Core.Installer;
using AlleyCat.Core.Logging;
using AlleyCat.Motion;
using AlleyCat.Rigging.Installation;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Tests.MotionBricks;

/// <summary>Bounded actual-scene experiment; not a production route controller.</summary>
[GlobalClass]
public partial class MotionBricksSequenceProbe : Node
{
    /// <summary>Install the real role before entering the tree so the test can suppress live provider calls.</summary>
    public void Prepare(Node scene)
    {
        Name = "MotionBricksSequenceProbe";
        Node actor = scene.RequireNode<Node>("NavigationTestNpc");
        RigRoleTemplateSceneInstaller installer = actor.RequireNode<RigRoleTemplateSceneInstaller>("NPCCharacterInstaller");
        SceneInstallationResult result = installer.Install(new SceneInstallationContext(actor));
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors));
        }
        actor.RemoveChild(installer);
        installer.Free();
    }
    private SomaMotionData? _data;
    private SomaInference? _engine;
    private Task<SomaInference>? _loading;
    // Standalone probe: owns its native session directly instead of sharing one through a registry.
    private SomaNativeSession? _native;
    private Task<Transform3D[][]>? _generation;
    private CharacterBody3D? _actor;
    private Skeleton3D? _skeleton;
    private NativeSomaRig? _rig;
    private RealisedPoseHistory? _history;
    private ILogger<MotionBricksSequenceProbe>? _logger;
    private Transform3D[] _pose = [];
    private Transform3D[] _observed = [];
    private Transform3D[][] _travel = [];
    private Transform3D[][]? _segment;
    private double _phaseTime;
    private int _segmentIndex;
    private bool _record;
    private Vector3 _clipPlacement;
    private Vector3 _lastDesiredGround;
    private readonly List<object> _trace = [];
    private readonly List<object> _calls = [];
    /// <inheritdoc/>
    public string Phase { get; private set; } = "loading";
    /// <inheritdoc/>
    public bool Finished
    {
        get; private set;
    }
    /// <inheritdoc/>
    public string Failure { get; private set; } = "";
    /// <inheritdoc/>
    public double ObservationTime
    {
        get; private set;
    }

    /// <inheritdoc/>
    public void Begin(Node scene)
    {
        _logger = GameLoggerResolver.ResolveRequired<MotionBricksSequenceProbe>();
        _data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
        var source = new ReviewedMotionSource(ProjectSettings.GlobalizePath("res://data/motionbricks/reviewed-feminine/landing-ablation-01.json"), _data);
        var travel = new Transform3D[_data.Names.Length * 4];
        source.CopyWindow(source.GetRole("feminine", "normal", "forward"), 58, travel);
        _travel = [.. Enumerable.Range(0, 4).Select(f => travel.AsSpan(f * _data.Names.Length, _data.Names.Length).ToArray())];
        _actor = scene.RequireNode<CharacterBody3D>("NavigationTestNpc");
        _skeleton = _actor.RequireNode<Skeleton3D>("Female/SOMA/Native/GeneralSkeleton");
        _rig = new NativeSomaRig(_data, _skeleton);
        _pose = new Transform3D[_data.Names.Length];
        _observed = new Transform3D[_data.Names.Length];
        _history = new RealisedPoseHistory(_pose.Length);
        _skeleton.ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Manual;
        _skeleton.SkeletonUpdated += Observe;
        // The retained eye/hand graph has no active body state. Leave its modifiers installed.
        string model = System.Environment.GetEnvironmentVariable("MOTIONBRICKS_MODEL")
            ?? throw new InvalidOperationException("Set MOTIONBRICKS_MODEL to the external SOMA FP32 ONNX file.");
        string frames = System.Environment.GetEnvironmentVariable("MOTIONBRICKS_FRAMES")
            ?? throw new InvalidOperationException("Set MOTIONBRICKS_FRAMES to the source Kimodo frame calibration JSON.");
        _loading = Task.Run(() =>
        {
            _native = SomaNativeSession.Load(model);
            return new SomaInference(_data, _native, frames);
        });
        SampleClip(_data.Idle, 0, Vector3.Zero);
        _rig.Apply(_pose);
        SetPhysicsProcess(true);
    }

    /// <inheritdoc/>
    public override void _PhysicsProcess(double delta)
    {
        if (_data is null || Finished)
        {
            return;
        }
        try
        {
            Step(delta);
        }
        catch (Exception e)
        {
            Failure = e.ToString();
            _logger!.LogError(e, "MotionBricks sequence failed in phase {Phase}", Phase);
            Finish();
        }
    }

    private void Step(double delta)
    {
        if (_engine is null)
        {
            if (!_loading!.IsCompleted)
            {
                return;
            }
            _engine = _loading.GetAwaiter().GetResult();
            Phase = "initial-idle";
            _logger!.LogInformation("Godot-hosted MotionBricks CUDA session loaded");
        }
        ObservationTime += delta;
        _phaseTime += delta;
        if (_generation is not null)
        {
            if (_generation.IsCompleted)
            {
                _segment = _generation.GetAwaiter().GetResult();
                _generation = null;
                _phaseTime = 0;
                Phase = _segmentIndex == 4 ? "arrival" : _segmentIndex == 1 ? "departure" : "sustained-walk";
                _calls.Add(new
                {
                    segment = _segmentIndex,
                    milliseconds = _engine.LastMilliseconds,
                    tokens = _engine.LastTokenCount,
                    frames = _segment.Length,
                    recorded = _history!.SamplesRecorded
                });
                _logger!.LogInformation("Generated segment {Segment}: {Frames} frames, {Milliseconds} ms", _segmentIndex, _segment.Length, _engine.LastMilliseconds);
            }
            // Hold the last shown pose during worker inference; this latency is visible evidence,
            // not hidden via a predicted-context asynchronous scheduling claim.
        }
        else if (Phase == "initial-idle")
        {
            SampleClip(_data!.Idle, _phaseTime, Vector3.Zero);
            if (_phaseTime >= 2)
            {
                GenerateNext();
            }
        }
        else if (Phase == "settled-idle")
        {
            SampleClip(_data!.Idle, _phaseTime, _clipPlacement);
            if (_phaseTime >= 3)
            {
                Finish();
                return;
            }
        }
        else if (_segment is not null)
        {
            SomaMotionData.Sample(_segment, _phaseTime, _pose);
            if (_phaseTime >= (_segment.Length - 1) / 30.0)
            {
                if (_segmentIndex == 4)
                {
                    _clipPlacement = new Vector3(_pose[0].Origin.X, 0, _pose[0].Origin.Z);
                    Phase = "settled-idle";
                    _phaseTime = 0;
                }
                else
                {
                    // Defer request to after this endpoint's actual skin-ready observation.
                    Phase = "request-next";
                }
            }
        }
        Vector3 desiredGround = _pose[0].Origin;
        desiredGround.Y = 0;
        Vector3 deltaRoot = SomaMotionData.ModelToWorld * (desiredGround - _lastDesiredGround);
        _ = _actor!.MoveAndCollide(deltaRoot);
        _lastDesiredGround = desiredGround;
        _rig!.Apply(_pose);
        _record = true;
        _skeleton!.Advance(delta);
    }

    private void SampleClip(Transform3D[][] clip, double time, Vector3 placement)
    {
        SomaMotionData.Sample(clip, time % ((clip.Length - 1) / 30.0), _pose);
        Vector3 initial = clip[0][0].Origin;
        initial.Y = 0;
        _pose[0].Origin += placement - initial;
    }

    private void GenerateNext()
    {
        Transform3D[][] context = _history!.Context();
        _segmentIndex++;
        Transform3D[][] clip = _segmentIndex == 4 ? _data!.Idle : _travel;
        Vector3 ground = context[3][0].Origin;
        ground.Y = 0;
        Vector3 placement = ground + new Vector3(0, 0, _segmentIndex == 4 ? 0.65f : 0.9f);
        var target = new Transform3D[4][];
        Vector3 initial = clip[0][0].Origin;
        initial.Y = 0;
        for (int f = 0; f < 4; f++)
        {
            target[f] = (Transform3D[])clip[f].Clone();
            target[f][0].Origin += placement - initial;
        }
        _calls.Add(new
        {
            request = _segmentIndex,
            time = ObservationTime,
            samples = _history.SamplesRecorded,
            contextRoot = context.Select(p => Vec(p[0].Origin)).ToArray(),
            targetRoot = Vec(target[0][0].Origin),
            contextHead = context.Select(p => Vec(p[Array.IndexOf(_data!.Names, "Head")].Basis.GetEuler())).ToArray()
        });
        _generation = Task.Run(() => _engine!.Generate(context, target));
        Phase = "inference-hold";
    }

    private void Observe()
    {
        if (!_record || Finished)
        {
            return;
        }
        _record = false;
        _rig!.Observe(_observed);
        _history!.Record(ObservationTime, _observed);
        int left = _skeleton!.FindBone("LeftFoot"), right = _skeleton.FindBone("RightFoot");
        Vector3 footL = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(left).Origin;
        Vector3 footR = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(right).Origin;
        Vector3 kneeL = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(_skeleton.FindBone("LeftLowerLeg")).Origin;
        Vector3 hipL = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(_skeleton.FindBone("LeftUpperLeg")).Origin;
        Vector3 hipR = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(_skeleton.FindBone("RightUpperLeg")).Origin;
        Vector3 head = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(_skeleton.FindBone("Head")).Origin;
        if (!footL.IsFinite() || !footR.IsFinite() || hipL.DistanceTo(kneeL) < 0.1f || kneeL.DistanceTo(footL) < 0.1f)
        {
            throw new InvalidDataException("Non-finite or collapsed displayed leg anatomy.");
        }
        _trace.Add(new
        {
            time = ObservationTime,
            phase = Phase,
            actor = Vec(_actor!.GlobalPosition),
            realisedHips = Vec(_observed[0].Origin),
            predictedHips = Vec(_pose[0].Origin),
            leftFoot = Vec(footL),
            rightFoot = Vec(footR),
            leftKnee = Vec(kneeL),
            leftHip = Vec(hipL),
            rightHip = Vec(hipR),
            head = Vec(head),
            heading = SomaInference.Heading(hipR, hipL)
        });
        if (Phase == "request-next")
        {
            GenerateNext();
        }
    }

    private static float[] Vec(Vector3 v) => [v.X, v.Y, v.Z];

    private void Finish()
    {
        Finished = true;
        SetPhysicsProcess(false);
        string path = ProjectSettings.GlobalizePath("res://temp/motionbricks/sequence-trace.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            failure = Failure,
            calls = _calls,
            frames = _trace
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        if (_skeleton is not null && IsInstanceValid(_skeleton))
        {
            _skeleton.SkeletonUpdated -= Observe;
        }
        _engine?.Dispose();
        // Disposing during an in-flight Task.Run generation was already unsupported by this probe.
        _native?.Dispose();
    }
}
