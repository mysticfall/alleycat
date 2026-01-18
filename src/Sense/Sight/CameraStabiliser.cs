using Godot;

namespace AlleyCat.Sense.Sight;

[GlobalClass]
public partial class CameraStabiliser : Node
{
    [Export] public Camera3D? Camera { get; set; }

    public override void _Process(double delta)
    {
        base._Process(delta);

        if (Camera == null) return;

        var basis = Camera.GlobalBasis;

        var forward = basis * Vector3.Forward;
        var right = forward.Cross(Vector3.Up);

        Camera.GlobalBasis = new Basis(right, Vector3.Up, -forward);
    }
}