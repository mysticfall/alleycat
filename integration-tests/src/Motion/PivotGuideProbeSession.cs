using System.Text.Json;
using AlleyCat.Motion;
using Godot;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>Task-local first-turn conditioning only; native generation/context and settlement stay production-owned.</summary>
internal sealed class PivotGuideProbeSession(SomaInferenceWorker.ISession inner, bool candidate, string directory,
    Action<object> record) : SomaInferenceWorker.ISession
{
    private int _requests;

    public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
    {
        _requests++;
        Transform3D[][] effective = target;
        if (candidate && _requests == 1)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "..", "pivot-poses.json")));
            JsonElement frames = document.RootElement.GetProperty("clips").GetProperty("pivot").GetProperty("frames");
            effective = new Transform3D[4][];
            for (int i = 0; i < 4; i++)
            {
                effective[i] = [.. frames[46 + i].EnumerateArray().Select(row => new Transform3D(
                    new Basis(new Quaternion(row[3].GetSingle(), row[4].GetSingle(), row[5].GetSingle(), row[6].GetSingle())),
                    new Vector3(row[0].GetSingle(), row[1].GetSingle(), row[2].GetSingle())))];
                // Preserve all production root constraints exactly: no second pivot rotation or authored fake history.
                effective[i][0] = target[i][0];
            }
        }
        record(new
        {
            requestOrdinal = _requests,
            candidate = candidate && _requests == 1,
            context = context.Select(p => p.Select(Flatten).ToArray()).ToArray(),
            originalTarget = target.Select(p => p.Select(Flatten).ToArray()).ToArray(),
            effectiveTarget = effective.Select(p => p.Select(Flatten).ToArray()).ToArray()
        });
        return inner.Generate(context, effective);
    }

    private static float[] Flatten(Transform3D t) =>
        [t.Origin.X, t.Origin.Y, t.Origin.Z, t.Basis.X.X, t.Basis.X.Y, t.Basis.X.Z,
            t.Basis.Y.X, t.Basis.Y.Y, t.Basis.Y.Z, t.Basis.Z.X, t.Basis.Z.Y, t.Basis.Z.Z];

    public void Dispose() => inner.Dispose();
}
