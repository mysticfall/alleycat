using System.Text.Json;
using Godot;

namespace AlleyCat.Motion;

/// <summary>Re-expresses canonical source poses in the authoritative Kimodo BONES joint frames without moving joints.</summary>
public sealed class SomaModelFrames
{
    private readonly int[] _parents;
    private readonly Basis[] _offsets;
    private readonly Basis[] _inverseOffsets;

    /// <summary>Loads named global frame offsets exported from the source's Kimodo skeleton, not USD rest defaults.</summary>
    public SomaModelFrames(SomaMotionData data, string path)
    {
        _parents = data.Parents;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        string[] names = root.GetProperty("names").Deserialize<string[]>()!;
        JsonElement rows = root.GetProperty("offsets");
        if (names.Distinct().Count() != names.Length || rows.GetArrayLength() != names.Length)
        {
            throw new InvalidDataException("Invalid named SOMA model-frame calibration.");
        }
        _offsets = new Basis[data.Names.Length];
        _inverseOffsets = new Basis[data.Names.Length];
        for (int i = 0; i < data.Names.Length; i++)
        {
            int index = Array.IndexOf(names, data.Names[i]);
            if (index < 0)
            {
                throw new InvalidDataException($"Missing SOMA model frame for {data.Names[i]}.");
            }
            JsonElement m = rows[index];
            var basis = new Basis(
                new Vector3(m[0][0].GetSingle(), m[1][0].GetSingle(), m[2][0].GetSingle()),
                new Vector3(m[0][1].GetSingle(), m[1][1].GetSingle(), m[2][1].GetSingle()),
                new Vector3(m[0][2].GetSingle(), m[1][2].GetSingle(), m[2][2].GetSingle()));
            if (!basis.IsFinite() || !Mathf.IsEqualApprox(basis.Determinant(), 1) ||
                !(basis.Transposed() * basis).IsEqualApprox(Basis.Identity))
            {
                throw new InvalidDataException($"SOMA model frame is not a rotation: {data.Names[i]}.");
            }
            _offsets[i] = basis;
            _inverseOffsets[i] = basis.Inverse();
        }
    }

    /// <summary>Canonical world R becomes R * offset; parent-frame translation changes but world positions do not.</summary>
    public void ToModel(ReadOnlySpan<Transform3D> canonical, Span<Transform3D> model) => Convert(canonical, model, false);

    /// <summary>Returns model output to the canonical calibrated rig/history representation.</summary>
    public void ToCanonical(ReadOnlySpan<Transform3D> model, Span<Transform3D> canonical) => Convert(model, canonical, true);

    private void Convert(ReadOnlySpan<Transform3D> input, Span<Transform3D> output, bool inverse)
    {
        if (input.Length != _parents.Length || output.Length != _parents.Length)
        {
            throw new ArgumentException("Pose and SOMA model-frame joint counts must match.");
        }
        for (int i = 0; i < _parents.Length; i++)
        {
            int parent = _parents[i];
            Basis left = parent < 0 ? Basis.Identity : inverse ? _offsets[parent] : _inverseOffsets[parent];
            Basis right = inverse ? _inverseOffsets[i] : _offsets[i];
            Transform3D pose = input[i];
            output[i] = new Transform3D(left * pose.Basis * right, left * pose.Origin);
        }
    }
}
