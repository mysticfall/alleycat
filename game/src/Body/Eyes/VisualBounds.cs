using Godot;

namespace AlleyCat.Body.Eyes;

/// <summary>
/// Cue-local bounds used to select representative visual-scan sample points.
/// </summary>
[GlobalClass]
public abstract partial class VisualBounds : Resource
{
    /// <summary>Gets the representative cue-local sample points.</summary>
    public abstract IReadOnlyList<Vector3> GetSampleLocalPositions();
}
