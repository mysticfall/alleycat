using AlleyCat.Scene;
using Godot;

namespace AlleyCat.Body.Eyes;

/// <summary>
/// Base contract for a scene-authored point of visual interest.
/// </summary>
[GlobalClass]
public abstract partial class VisualCue : Node3D
{
    /// <summary>
    /// Gets or sets the cue identifier, unique within its provider.
    /// </summary>
    [Export]
    public string ID { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cue's relative selection prominence. Zero disables the cue.
    /// </summary>
    [Export]
    public float Prominence { get; set; } = 1.0f;

    /// <summary>
    /// Samples the cue's current world-space position.
    /// </summary>
    public abstract Vector3 SampleGlobalPosition();

    /// <summary>
    /// Describes the cue relative to the supplied observer and scene.
    /// </summary>
    public abstract string Describe(ISceneContext scene, IVisualObserver observer);
}
