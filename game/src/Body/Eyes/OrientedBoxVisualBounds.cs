using Godot;

namespace AlleyCat.Body.Eyes;

/// <summary>An oriented cue-local box sampled at its centre and eight corners.</summary>
[GlobalClass]
public sealed partial class OrientedBoxVisualBounds : VisualBounds
{
    private Vector3 _size = Vector3.One * 0.5f;
    private IReadOnlyList<Vector3> _samples;

    /// <summary>Initialises an oriented box with its default representative samples.</summary>
    public OrientedBoxVisualBounds()
    {
        _samples = CreateSamples(_size);
    }

    /// <summary>Gets or sets the full cue-local box size in metres.</summary>
    [Export]
    public Vector3 Size
    {
        get => _size;
        set
        {
            Vector3 size = new(Mathf.Max(0f, value.X), Mathf.Max(0f, value.Y), Mathf.Max(0f, value.Z));
            if (_size.IsEqualApprox(size))
            {
                return;
            }

            _size = size;
            _samples = CreateSamples(size);
        }
    }

    /// <inheritdoc />
    public override IReadOnlyList<Vector3> GetSampleLocalPositions() => _samples;

    private static IReadOnlyList<Vector3> CreateSamples(Vector3 size)
    {
        Vector3 halfSize = size * 0.5f;
        return Array.AsReadOnly(
        [
            Vector3.Zero,
            new(-halfSize.X, -halfSize.Y, -halfSize.Z),
            new(-halfSize.X, -halfSize.Y, halfSize.Z),
            new(-halfSize.X, halfSize.Y, -halfSize.Z),
            new(-halfSize.X, halfSize.Y, halfSize.Z),
            new(halfSize.X, -halfSize.Y, -halfSize.Z),
            new(halfSize.X, -halfSize.Y, halfSize.Z),
            new(halfSize.X, halfSize.Y, -halfSize.Z),
            new(halfSize.X, halfSize.Y, halfSize.Z),
        ]);
    }
}
