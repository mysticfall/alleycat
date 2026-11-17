using Godot;

namespace AlleyCat.Motion;

/// <summary>Time-based ring of actual skin-ready poses, resampled over 0.1 s at 30 Hz.</summary>
public sealed class RealisedPoseHistory(int joints, int capacity = 128)
{
    private readonly Transform3D[][] _poses = [.. Enumerable.Range(0, capacity).Select(_ => new Transform3D[joints])];
    private readonly double[] _times = new double[capacity];
    private int _next;
    private int _count;
    /// <inheritdoc/>
    public long SamplesRecorded
    {
        get; private set;
    }

    /// <inheritdoc/>
    public void Record(double time, ReadOnlySpan<Transform3D> pose)
    {
        pose.CopyTo(_poses[_next]);
        _times[_next] = time;
        _next = (_next + 1) % _poses.Length;
        _count = Math.Min(_count + 1, _poses.Length);
        SamplesRecorded++;
    }

    /// <inheritdoc/>
    public Transform3D[][] Context()
    {
        if (_count == 0)
        {
            throw new InvalidOperationException("No realised poses have been recorded.");
        }
        int latest = (_next + _poses.Length - 1) % _poses.Length;
        var result = new Transform3D[4][];
        for (int f = 0; f < 4; f++)
        {
            double time = _times[latest] - ((3 - f) / 30.0);
            int b = latest;
            int a = latest;
            for (int k = 1; k < _count; k++)
            {
                a = (latest - k + _poses.Length) % _poses.Length;
                if (_times[a] <= time)
                {
                    break;
                }
                b = a;
            }
            float weight = _times[b] <= _times[a] ? 0 : (float)Math.Clamp((time - _times[a]) / (_times[b] - _times[a]), 0, 1);
            result[f] = new Transform3D[_poses[a].Length];
            for (int j = 0; j < result[f].Length; j++)
            {
                result[f][j] = SomaMotionData.Blend(_poses[a][j], _poses[b][j], weight);
            }
        }
        return result;
    }
}
