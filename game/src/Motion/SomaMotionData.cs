using System.Text.Json;
using Godot;

namespace AlleyCat.Motion;

/// <summary>Explicit source-proportioned local poses; rotations use SOMA's canonical, identity-rest axes.</summary>
public sealed class SomaMotionData
{
    /// <inheritdoc/>
    public string[] Names
    {
        get;
    }
    /// <inheritdoc/>
    public int[] Parents
    {
        get;
    }
    /// <inheritdoc/>
    public Transform3D[] Rest
    {
        get;
    }
    /// <inheritdoc/>
    public Transform3D[] BlenderRest
    {
        get;
    }
    /// <inheritdoc/>
    internal JsonElement Manifest
    {
        get;
    }
    /// <inheritdoc/>
    public static Basis BlenderToModel => new(Vector3.Right, new Vector3(0, 0, -1), Vector3.Up);
    /// <inheritdoc/>
    public static Basis ModelToWorld => new(Vector3.Left, Vector3.Up, Vector3.Forward);

    /// <inheritdoc/>
    public SomaMotionData(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Manifest = document.RootElement.Clone();
        Names = Manifest.GetProperty("names").Deserialize<string[]>()!;
        Parents = Manifest.GetProperty("parents").Deserialize<int[]>()!;
        Rest = ReadPose(Manifest.GetProperty("rest"));
        BlenderRest = [.. Manifest.GetProperty("blenderRest").EnumerateArray().Select(ReadMatrix)];
        if (Names[0] != "Hips" || Parents[0] != -1)
        {
            throw new InvalidDataException("Invalid SOMA source layout.");
        }
    }

    private static Transform3D[] ReadPose(JsonElement rows) => [.. rows.EnumerateArray().Select(row =>
        new Transform3D(new Basis(new Quaternion(row[3].GetSingle(), row[4].GetSingle(), row[5].GetSingle(), row[6].GetSingle()).Normalized()),
            new Vector3(row[0].GetSingle(), row[1].GetSingle(), row[2].GetSingle())))];

    /// <inheritdoc/>
    public static Transform3D ReadMatrix(JsonElement m) => new(new Basis(
        new Vector3(m[0][0].GetSingle(), m[1][0].GetSingle(), m[2][0].GetSingle()),
        new Vector3(m[0][1].GetSingle(), m[1][1].GetSingle(), m[2][1].GetSingle()),
        new Vector3(m[0][2].GetSingle(), m[1][2].GetSingle(), m[2][2].GetSingle())),
        new Vector3(m[0][3].GetSingle(), m[1][3].GetSingle(), m[2][3].GetSingle()));

    /// <inheritdoc/>
    public void WorldPose(ReadOnlySpan<Transform3D> local, Span<Transform3D> world)
    {
        for (int i = 0; i < Parents.Length; i++)
        {
            world[i] = Parents[i] < 0 ? local[i] : world[Parents[i]] * local[i];
        }
    }

    /// <inheritdoc/>
    public static Transform3D Blend(Transform3D a, Transform3D b, float weight) => new(
        new Basis(a.Basis.GetRotationQuaternion().Slerp(b.Basis.GetRotationQuaternion(), weight).Normalized()), a.Origin.Lerp(b.Origin, weight));

    /// <inheritdoc/>
    public static void Sample(Transform3D[][] frames, double seconds, Span<Transform3D> result)
    {
        double frame = Math.Clamp(seconds * 30, 0, frames.Length - 1);
        int a = (int)frame;
        int b = Math.Min(a + 1, frames.Length - 1);
        for (int j = 0; j < result.Length; j++)
        {
            result[j] = Blend(frames[a][j], frames[b][j], (float)(frame - a));
        }
    }
}
