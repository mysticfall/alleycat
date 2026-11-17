using System.Text.Json;
using AlleyCat.Motion;
using Godot;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>Opt-in diagnostic placement only; the worker, actual context and generated playback remain unchanged.</summary>
internal sealed class PivotIntermediateProbeSession : SomaInferenceWorker.ISession
{
    internal static readonly int[] Starts = [16, 32, 46];
    private readonly SomaInferenceWorker.ISession _inner;
    private readonly bool _candidate;
    private readonly Action<object> _record;
    private readonly PivotIntermediateGuides _guides;
    private Transform3D[]? _anchor;
    private int _request;

    internal PivotIntermediateProbeSession(SomaInferenceWorker.ISession inner, bool candidate, string source,
        SomaMotionData data, string modelFrames, Action<object> record)
    {
        _inner = inner;
        _candidate = candidate;
        _record = record;
        _guides = new PivotIntermediateGuides(source, data, modelFrames);
    }

    public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
    {
        _request++;
        _anchor ??= (Transform3D[])context[3].Clone();
        // Validate EVERY planned phase before the first native invocation, using the actual initial anchor.
        if (_request == 1)
        {
            foreach (int start in Starts)
            {
                (Transform3D[][] idle, Transform3D[][] pivot) = _guides.Pair(_anchor, start);
                _record(new
                {
                    kind = "preflight",
                    startFrame = start + 1,
                    comparison = _guides.Verify(idle, pivot)
                });
            }
        }
        Transform3D[][] effective = target;
        object? comparison = null;
        if (_request <= Starts.Length)
        {
            (Transform3D[][] idle, Transform3D[][] pivot) = _guides.Pair(_anchor, Starts[_request - 1]);
            comparison = _guides.Verify(idle, pivot);
            effective = _candidate ? pivot : idle;
        }
        _record(new
        {
            kind = "request",
            requestOrdinal = _request,
            sourceStartFrame = _request <= Starts.Length ? Starts[_request - 1] + 1 : (int?)null,
            candidate = _candidate,
            context = context.Select(p => p.Select(PivotIntermediateGuides.Flatten).ToArray()).ToArray(),
            originalTarget = target.Select(p => p.Select(PivotIntermediateGuides.Flatten).ToArray()).ToArray(),
            effectiveTarget = effective.Select(p => p.Select(PivotIntermediateGuides.Flatten).ToArray()).ToArray(),
            encodedTarget = _guides.Encode(effective),
            comparison
        });
        return _inner.Generate(context, effective);
    }

    public void Dispose() => _inner.Dispose();
}

internal sealed class PivotIntermediateGuides
{
    private readonly SomaMotionData _data;
    private readonly SomaModelFrames _frames;
    private readonly Transform3D[][] _source;
    private readonly int _left;
    private readonly int _right;

    internal PivotIntermediateGuides(string source, SomaMotionData data, string modelFrames)
    {
        _data = data;
        _frames = new SomaModelFrames(data, modelFrames);
        _left = Array.IndexOf(data.Names, "LeftLeg");
        _right = Array.IndexOf(data.Names, "RightLeg");
        using var document = JsonDocument.Parse(File.ReadAllText(source));
        _source = [.. document.RootElement.GetProperty("clips").GetProperty("pivot").GetProperty("frames")
            .EnumerateArray().Select(p => p.EnumerateArray().Select(row => new Transform3D(
                new Basis(new Quaternion(row[3].GetSingle(), row[4].GetSingle(), row[5].GetSingle(), row[6].GetSingle())),
                new Vector3(row[0].GetSingle(), row[1].GetSingle(), row[2].GetSingle()))).ToArray())];
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

    internal (Transform3D[][] Idle, Transform3D[][] Pivot) Pair(Transform3D[] anchor, int start)
    {
        Transform3D[] entry = World(_source[0]), actual = World(anchor);
        float yaw = Heading(actual) - Heading(entry);
        var rotation = new Basis(Vector3.Up, yaw);
        Vector3 origin = actual[0].Origin;
        origin.Y = 0;
        Vector3 sourceOrigin = entry[0].Origin;
        sourceOrigin.Y = 0;
        var idle = new Transform3D[4][];
        var pivot = new Transform3D[4][];
        for (int i = 0; i < 4; i++)
        {
            Transform3D[] sourceWorld = World(_source[start + i]);
            Vector3 position = origin + (rotation * (sourceWorld[0].Origin - sourceOrigin));
            float heading = Heading(sourceWorld) + yaw;
            idle[i] = Place(_data.Idle[i], heading, position);
            pivot[i] = Place(_source[start + i], heading, position);
        }
        return (idle, pivot);
    }

    private Transform3D[] Place(Transform3D[] pose, float heading, Vector3 position)
    {
        var copy = (Transform3D[])pose.Clone();
        float delta = heading - Heading(World(pose));
        copy[0] = new Transform3D(new Basis(Vector3.Up, delta) * pose[0].Basis, position);
        return copy;
    }

    // Model-world quantities before context alignment. A common rigid alignment preserves equality.
    internal float[][] Encode(Transform3D[][] poses)
    {
        Transform3D[][] world = [.. poses.Select(World)];
        float[] heading = [.. world.Select(Heading)];
        return [.. Enumerable.Range(0, 4).Select(i =>
        {
            int next = Math.Min(i + 1, 3);
            Vector3 hips = world[i][0].Origin;
            Vector3 velocity = (world[next][0].Origin - hips) * 30;
            float angular = Mathf.Atan2(Mathf.Sin(heading[next] - heading[i]), Mathf.Cos(heading[next] - heading[i])) * 30;
            return new[] { hips.X, hips.Y, hips.Z, Mathf.Cos(heading[i]), Mathf.Sin(heading[i]), angular, velocity.X, velocity.Z };
        })];
    }

    internal object Verify(Transform3D[][] idle, Transform3D[][] pivot)
    {
        float[][] a = Encode(idle), b = Encode(pivot);
        float maximum = Enumerable.Range(0, 4).Max(i => Enumerable.Range(0, 8).Max(j => Math.Abs(a[i][j] - b[i][j])));
        return maximum > 0.00001f
            ? throw new InvalidDataException($"Intermediate guide encoded quantities differ: {maximum:R}.")
            : new
            {
                maximum,
                idle = a,
                pivot = b,
                globalPoseMasks = new[] { 1, 1, 1, 1 },
                velocityMask = new[] { 1, 1, 1, 0 }
            };
    }

    internal static float[] Flatten(Transform3D t) =>
        [t.Origin.X, t.Origin.Y, t.Origin.Z, t.Basis.X.X, t.Basis.X.Y, t.Basis.X.Z,
            t.Basis.Y.X, t.Basis.Y.Y, t.Basis.Y.Z, t.Basis.Z.X, t.Basis.Z.Y, t.Basis.Z.Z];
}
