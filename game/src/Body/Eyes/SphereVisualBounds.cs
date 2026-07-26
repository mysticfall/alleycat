using Godot;

namespace AlleyCat.Body.Eyes;

/// <summary>A spherical cue-local bound sampled at its centre and axial extremes.</summary>
[GlobalClass]
public sealed partial class SphereVisualBounds : VisualBounds
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Style",
        "IDE0032:Use auto property",
        Justification = "The backing field avoids regenerating representative samples when the radius is unchanged.")]
    private float _radius = 0.25f;
    private IReadOnlyList<Vector3> _samples;

    /// <summary>Initialises a spherical bound with its default representative samples.</summary>
    public SphereVisualBounds()
    {
        _samples = CreateSamples(_radius);
    }

    /// <summary>Gets or sets the sphere radius in cue-local metres.</summary>
    [Export(PropertyHint.Range, "0,100,0.01,or_greater")]
    public float Radius
    {
        get => _radius;
        set
        {
            float radius = Mathf.Max(0f, value);
            if (Mathf.IsEqualApprox(_radius, radius))
            {
                return;
            }

            _radius = radius;
            _samples = CreateSamples(radius);
        }
    }

    /// <inheritdoc />
    public override IReadOnlyList<Vector3> GetSampleLocalPositions() => _samples;

    private static IReadOnlyList<Vector3> CreateSamples(float radius)
        => Array.AsReadOnly(
        [
            Vector3.Zero,
            Vector3.Right * radius,
            Vector3.Left * radius,
            Vector3.Up * radius,
            Vector3.Down * radius,
            Vector3.Forward * radius,
            Vector3.Back * radius,
        ]);
}
