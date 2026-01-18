using Godot;
using LanguageExt;

namespace AlleyCat.Sense.Hearing;

public partial class SoundBubble : Area3D
{
    public required ISound Data { get; init; }

    public required ISoundSource Source { get; init; }

    public Length Range { get; init; } = 10.Metres();

    public Duration TimeToLive { get; init; } = 3.Seconds();

    public override void _Ready()
    {
        base._Ready();

        var shape = new SphereShape3D
        {
            Radius = (float)Range.Metres
        };

        var collider = new CollisionShape3D
        {
            Shape = shape,
        };

        AddChild(collider);

        collider.Owner = this;

        SetCollisionLayerValue(32, true);

        Monitorable = true;
        Monitoring = false;
    }

    public override void _Process(double delta)
    {
        base._Process(delta);

        var elapsed = new Duration((DateTime.Now - Data.Timestamp).TotalMilliseconds);

        if (elapsed >= TimeToLive)
        {
            QueueFree();
        }
    }
}