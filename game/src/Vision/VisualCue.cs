using AlleyCat.Scene;
using Godot;

namespace AlleyCat.Vision;

/// <summary>
/// Base contract for a scene-authored visual cue.
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

    /// <summary>Gets or sets the cue-local bounds used for scan samples.</summary>
    [Export]
    public VisualBounds? Bounds
    {
        get; set;
    }

    /// <summary>Gets or sets the maximum visible distance in metres; zero is unlimited.</summary>
    [Export(PropertyHint.Range, "0,1000,0.1,or_greater")]
    public float MaxVisibleDistance
    {
        get; set;
    }

    /// <summary>
    /// Samples the cue's current world-space position.
    /// </summary>
    public virtual Vector3 SampleGlobalPosition()
    {
        IReadOnlyList<Vector3> samples = GetSampleLocalPositions();
        return GlobalTransform * samples[0];
    }

    /// <summary>Gets representative cue-local samples, always including the origin.</summary>
    public IReadOnlyList<Vector3> GetSampleLocalPositions() => Bounds?.GetSampleLocalPositions() ?? PointVisualBoundsSamples;

    private static IReadOnlyList<Vector3> PointVisualBoundsSamples { get; } = Array.AsReadOnly([Vector3.Zero]);

    /// <summary>
    /// Describes the cue relative to the supplied observer and scene.
    /// </summary>
    public abstract string Describe(ISceneContext scene, IHasVision observer);
}
