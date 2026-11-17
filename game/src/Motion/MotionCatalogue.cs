using System.Text.Json;
using Godot;

namespace AlleyCat.Motion;

/// <summary>Variant stages carry source speed and four-frame action entry/exit windows at 30 Hz.</summary>
public sealed class MotionCatalogue
{
    /// <summary>A reusable authored stage; the normal runtime catalogue provisions idle.</summary>
    public sealed record Stage(string Action, float Speed, int Entry, int Exit);
    /// <summary>The selected variant's authored stages.</summary>
    public Stage[] Stages
    {
        get;
    }

    /// <summary>Loads and validates the selected variant against its extracted clips.</summary>
    public MotionCatalogue(string path, SomaMotionData data, string variant = "sad")
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement selected = doc.RootElement.GetProperty("variants").EnumerateArray()
            .Single(v => v.GetProperty("name").GetString() == variant);
        Stages = [.. selected.GetProperty("stages").EnumerateArray().Select(s => new Stage(
            s.GetProperty("action").GetString()!, s.GetProperty("speed").GetSingle(),
            s.GetProperty("entry").GetInt32(), s.GetProperty("exit").GetInt32()))];
        foreach (Stage stage in Stages)
        {
            Transform3D[][] clip = Clip(data, stage.Action);
            if (!float.IsFinite(stage.Speed) || stage.Speed < 0 || stage.Speed > 1 || stage.Entry < 0 || stage.Exit < 0
                || stage.Entry + 4 > clip.Length || stage.Exit + 4 > clip.Length)
            {
                throw new InvalidDataException("Invalid speed stage or four-frame action window.");
            }
        }
    }

    /// <summary>Selects the nearest declared stage; moving roles are supplied separately by the shared source set.</summary>
    public Stage Select(float speed) => Stages.MinBy(s => Math.Abs(s.Speed - speed))!;

    /// <summary>Resolves one of the provisioned actions, refusing missing content.</summary>
    public static Transform3D[][] Clip(SomaMotionData data, string action) => action == "idle" ? data.Idle : data.ReadClip(action);
}
