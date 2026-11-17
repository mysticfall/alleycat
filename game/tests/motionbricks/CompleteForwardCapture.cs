using System.Collections.Concurrent;
using AlleyCat.Motion;
using Godot;

namespace AlleyCat.Tests.Motion;

/// <summary>Worker-only evidence portion of the representative probe.</summary>
public partial class ReviewedPlaybackProbe
{
    private sealed class CompleteForwardCapture
    {
        internal sealed record Generation(int Index, int Tokens, float[] Raw,
            float[][][] DecodedModelBeforeEndpoints, float[][][] CorrectedCanonical,
            float[][][] Context, float[][][] Target);

        private readonly ConcurrentQueue<Generation> _generations = new();
        internal Generation[] Records => [.. _generations];

        internal SomaInferenceWorker.ISession Wrap(SomaInference session) => new Session(session, _generations);

        private sealed class Session : SomaInferenceWorker.ISession
        {
            private readonly SomaInference _inner;
            private readonly ConcurrentQueue<Generation> _records;
            private float[][][]? _decoded;
            private float[]? _raw;
            private int _index;

            internal Session(SomaInference inner, ConcurrentQueue<Generation> records)
            {
                _inner = inner;
                _records = records;
                inner.DecodedObserved = poses => _decoded = Poses(poses);
                inner.GenerationObserved = (_, raw) => _raw = raw.ToArray();
            }

            public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
            {
                _decoded = null;
                _raw = null;
                Transform3D[][] result = _inner.Generate(context, target);
                _records.Enqueue(new Generation(++_index, _inner.LastTokenCount,
                    _raw ?? throw new InvalidOperationException("Native output observer did not execute."),
                    _decoded ?? throw new InvalidOperationException("Pre-endpoint decode observer did not execute."),
                    Poses(result), Poses(context), Poses(target)));
                return result;
            }

            public void Dispose() => _inner.Dispose();
        }

        private static float[][][] Poses(Transform3D[][] poses) => [.. poses.Select(frame =>
        frame.Select(p =>
        {
            Quaternion q = p.Basis.GetRotationQuaternion();
            return new[] { p.Origin.X, p.Origin.Y, p.Origin.Z, q.X, q.Y, q.Z, q.W };
        }).ToArray())];
    }
}
