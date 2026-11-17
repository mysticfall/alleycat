using System.Text.Json;
using AlleyCat.Motion;
using Godot;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>Single stationary turn carrying an existing raised-foot pose; diagnostics only.</summary>
internal sealed class StrongStepProbeSession(SomaInferenceWorker.ISession inner, bool candidate,
    StrongStepGuides guides, Action<object> record) : SomaInferenceWorker.ISession
{
    private int _request;

    public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
    {
        _request++;
        Transform3D[][] effective = target;
        object? comparison = null;
        if (_request == 1)
        {
            (Transform3D[][] idle, Transform3D[][] step) = guides.Pair(context[3]);
            comparison = guides.Verify(idle, step);
            effective = candidate ? step : idle;
        }
        record(new
        {
            kind = "request",
            requestOrdinal = _request,
            candidate,
            context = context.Select(p => p.Select(PivotIntermediateGuides.Flatten).ToArray()).ToArray(),
            originalTarget = target.Select(p => p.Select(PivotIntermediateGuides.Flatten).ToArray()).ToArray(),
            effectiveTarget = effective.Select(p => p.Select(PivotIntermediateGuides.Flatten).ToArray()).ToArray(),
            encodedTarget = guides.Encode(effective),
            modelWorld = guides.ModelWorld(effective),
            comparison
        });
        return inner.Generate(context, effective);
    }

    public void Dispose() => inner.Dispose();
}

internal sealed class StrongStepGuides
{
    private readonly SomaMotionData _data;
    private readonly SomaModelFrames _frames;
    private readonly PivotIntermediateGuides _encoder;
    private readonly Transform3D[][] _source;
    private readonly Dictionary<string, float[]> _originalHeights = [];
    private readonly int _left;
    private readonly int _right;

    internal StrongStepGuides()
    {
        const string sourceFolder = "res://temp/motionbricks/player-stepping-source-01/original-circle/";
        _data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
        string modelFrames = ProjectSettings.GlobalizePath("res://data/motionbricks/model-frames.json");
        _frames = new SomaModelFrames(_data, modelFrames);
        string source = ProjectSettings.GlobalizePath(sourceFolder + "pivot-poses.json");
        _encoder = new PivotIntermediateGuides(source, _data, modelFrames);
        _left = Array.IndexOf(_data.Names, "LeftLeg");
        _right = Array.IndexOf(_data.Names, "RightLeg");
        using var document = JsonDocument.Parse(File.ReadAllText(source));
        _source = [.. document.RootElement.GetProperty("clips").GetProperty("pivot").GetProperty("frames")
            .EnumerateArray().Skip(48).Take(4).Select(p => p.EnumerateArray().Select(row => new Transform3D(
                new Basis(new Quaternion(row[3].GetSingle(), row[4].GetSingle(), row[5].GetSingle(), row[6].GetSingle())),
                new Vector3(row[0].GetSingle(), row[1].GetSingle(), row[2].GetSingle()))).ToArray())];
        using var observations = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(sourceFolder + "source-observations.json")));
        foreach (string name in new[] { "LeftFoot", "RightFoot", "LeftToeBase", "RightToeBase" })
        {
            _originalHeights[name] = [.. observations.RootElement.GetProperty("frames").EnumerateArray().Skip(48).Take(4)
                .Select(f => f.GetProperty(name)[2][3].GetSingle())];
        }
    }

    private Transform3D[] World(Transform3D[] pose)
    {
        var model = new Transform3D[pose.Length];
        var world = new Transform3D[pose.Length];
        _frames.ToModel(pose, model);
        _data.WorldPose(model, world);
        return world;
    }

    private float Heading(Transform3D[] world) => SomaInference.Heading(world[_right].Origin, world[_left].Origin);

    internal (Transform3D[][] Idle, Transform3D[][] Step) Pair(Transform3D[] anchor)
    {
        Transform3D[] actual = World(anchor);
        float heading = Heading(actual) - (Mathf.Pi / 4);
        var idle = new Transform3D[4][];
        var step = new Transform3D[4][];
        for (int i = 0; i < 4; i++)
        {
            Vector3 position = actual[0].Origin;
            position.Y = World(_source[i])[0].Origin.Y;
            idle[i] = Place(_data.Idle[i], heading, position);
            step[i] = Place(_source[i], heading, position);
        }
        return (idle, step);
    }

    private Transform3D[] Place(Transform3D[] pose, float heading, Vector3 position)
    {
        var copy = (Transform3D[])pose.Clone();
        copy[0] = new Transform3D(new Basis(Vector3.Up, heading - Heading(World(pose))) * pose[0].Basis, position);
        return copy;
    }

    internal object Verify(Transform3D[][] idle, Transform3D[][] step)
    {
        object conditions = _encoder.Verify(idle, step);
        var heights = new Dictionary<string, float[]>();
        foreach ((string name, float[] expected) in _originalHeights)
        {
            int j = Array.IndexOf(_data.Names, name);
            float[] actual = [.. step.Select(p => World(p)[j].Origin.Y)];
            if (actual.Zip(expected).Any(pair => Math.Abs(pair.First - pair.Second) > 0.00001f))
            {
                throw new InvalidDataException("Rigid placement lost original raised-foot height: " + name);
            }
            heights[name] = actual;
        }
        return heights["LeftFoot"].Zip(heights["RightFoot"]).Any(pair => pair.First <= pair.Second) ||
            heights["LeftToeBase"].Zip(heights["RightToeBase"]).Any(pair => pair.First <= pair.Second)
            ? throw new InvalidDataException("The selected raised-foot cue did not survive model conversion.")
            : new
            {
                conditions,
                sourceHeights = _originalHeights,
                modelHeights = heights
            };
    }

    internal float[][] Encode(Transform3D[][] poses) => _encoder.Encode(poses);

    internal float[][][] ModelWorld(Transform3D[][] poses) =>
        [.. poses.Select(p => SomaInference.ModelNames.Select(n => PivotIntermediateGuides.Flatten(World(p)[Array.IndexOf(_data.Names, n)])).ToArray())];

    internal object SkinData(Transform3D[][] poses)
    {
        float[][][] rows = [.. poses.Select(p =>
        {
            var model = new Transform3D[p.Length];
            _frames.ToModel(p, model);
            return model.Select(t =>
            {
                Quaternion q = t.Basis.GetRotationQuaternion();
                return new[] { t.Origin.X, t.Origin.Y, t.Origin.Z, q.X, q.Y, q.Z, q.W };
            }).ToArray();
        })];
        return new
        {
            names = _data.Names,
            parents = _data.Parents,
            model_pose = rows
        };
    }
}
