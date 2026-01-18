using AlleyCat.Common;
using Godot;

namespace AlleyCat.Physics;

[GlobalClass]
public partial class PhysicalRemoteTransform : CharacterBody3D
{
    [Export] public Node3D? Source { get; set; }

    [Export] public Node3D? Target { get; set; }

    [Export] public Node3D? Origin { get; set; }

    private Transform3D _lastSource;
    private Transform3D _lastOrigin;

    public override void _PhysicsProcess(double delta)
    {
        base._PhysicsProcess(delta);
return;
        var source = this.RequireNode(Source, nameof(Source));
        var target = this.RequireNode(Target, nameof(Target));
        var origin = this.RequireNode(Origin, nameof(Origin));

        if (_lastSource == default)
        {
            _lastSource = source.GlobalTransform;
            _lastOrigin = origin.GlobalTransform;

            return;
        }

        var sourceTrans = source.GlobalTransform;
        var targetTrans = target.GlobalTransform;
        var originTrans = origin.GlobalTransform;

        var baseOffset = originTrans.Origin - _lastOrigin.Origin;
        var sourceOffset = sourceTrans.Origin - _lastSource.Origin;

        var velocity = (sourceOffset - baseOffset) / (float)delta;

        var offset = sourceTrans.Origin - targetTrans.Origin;
        var direction = offset.Normalized();

        Velocity = velocity + Vector3.Zero.Lerp(direction * 0.5f, Math.Min(1.0f, offset.Length() / 0.3f));
        GlobalBasis = source.GlobalBasis;

        MoveAndSlide();

        target.GlobalTransform = GlobalTransform;

        _lastSource = sourceTrans;
        _lastOrigin = originTrans;
    }
}