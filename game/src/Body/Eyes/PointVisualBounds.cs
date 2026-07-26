using Godot;

namespace AlleyCat.Body.Eyes;

/// <summary>A single point at the cue origin.</summary>
[GlobalClass]
public sealed partial class PointVisualBounds : VisualBounds
{
    private static readonly IReadOnlyList<Vector3> _samples = Array.AsReadOnly([Vector3.Zero]);

    /// <inheritdoc />
    public override IReadOnlyList<Vector3> GetSampleLocalPositions() => _samples;
}
