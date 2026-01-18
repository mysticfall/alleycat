using AlleyCat.Common;
using Godot;

namespace AlleyCat.Rig.Ik;

internal class LegIk(
    Side legSide,
    Vector3.Axis forwardAxis,
    bool invertAxis,
    Skeleton3D skeleton,
    Node constraint,
    Node3D markerOwner
)
{
    private readonly int _upperLegIndex = skeleton.FindBone("UpperLeg".PrefixWith(legSide));

    private readonly int _lowerLegIndex = skeleton.FindBone("LowerLeg".PrefixWith(legSide));

    private readonly int _footIndex = skeleton.FindBone("Foot".PrefixWith(legSide));

    private readonly Node3D _footIk = constraint.GetParent<Node3D>();

    private float _initialLegLength;

    private const string PoleName = "KneePole";
    private const float PoleLength = 0.3f;

    public bool Debug
    {
        set
        {
            if (!value)
            {
                _marker?.Free();
                _marker = null;
            }
            else if (_marker == null)
            {
                _marker = new Marker3D
                {
                    Name = PoleName.PrefixWith(legSide)
                };

                markerOwner.AddChild(_marker, @internal: Node.InternalMode.Back);

                _marker.Owner = markerOwner;
            }
        }
    }

    private Marker3D? _marker = (Marker3D?)markerOwner.FindChild(
        PoleName.PrefixWith(legSide));

    public Transform3D UpperLegPose => skeleton.GetBoneGlobalPose(_upperLegIndex);

    public Transform3D LowerLegPose => skeleton.GetBoneGlobalPose(_lowerLegIndex);

    public Transform3D FootPose => skeleton.GetBoneGlobalPose(_footIndex);

    public void Process()
    {
        if (_initialLegLength == 0)
        {
            var p1 = skeleton.GetBoneGlobalRest(_upperLegIndex).Origin;
            var p2 = skeleton.GetBoneGlobalRest(_footIndex).Origin;

            _initialLegLength = (p1 - p2).Length();
        }

        var skeletonTransform = skeleton.GlobalTransform;
        var toSkeleton = skeletonTransform.Inverse();

        var origin = UpperLegPose.Origin;

        var knee = LowerLegPose.Origin;
        var footTrans = FootPose;
        var foot = footTrans.Origin;
        var footIk = toSkeleton * _footIk.GlobalTransform.Origin;

        var animLeg = foot - origin;
        var animLegDir = animLeg.Normalized();

        var physicalLeg = footIk - origin;
        var physicalLegDir = physicalLeg.Normalized();

        var closest = origin + animLegDir * (knee - origin).Dot(animLegDir);

        var offset = knee - closest;
        var distance = offset.Length();

        var forward = forwardAxis switch
        {
            Vector3.Axis.X => Vector3.Right,
            Vector3.Axis.Y => Vector3.Up,
            Vector3.Axis.Z => Vector3.Forward,
            _ => throw new InvalidOperationException($"Invalid forward axis: {forwardAxis}")
        } * (invertAxis ? -1 : 1) * footTrans;

        var poleDir = forward.Lerp(offset.Normalized(), Math.Min(distance / 0.05f, 1f));

        var pole = origin +
                   physicalLegDir * physicalLeg.Length() / 2 +
                   poleDir * PoleLength;

        constraint.Set("pole_position", pole);

        if (_marker != null)
        {
            _marker.GlobalPosition = skeletonTransform.Origin + skeletonTransform * pole;
        }
    }
}

[GlobalClass, Tool]
public partial class LegsIk : SkeletonModifier3D
{
    [ExportGroup("Pole Target")] [Export] public Node? RightPole { get; set; }
    [Export] public Node? LeftPole { get; set; }

    [ExportGroup("Foot Orientation")]
    [Export]
    public Vector3.Axis ForwardAxis { get; set; } = Vector3.Axis.Y;

    [Export] public bool InvertAxis { get; set; }

    [Export]
    public bool Debug
    {
        get => _debug;
        set
        {
            _debug = value;

            if (_rightLeg != null)
            {
                _rightLeg.Debug = _debug;
            }

            if (_leftLeg != null)
            {
                _leftLeg.Debug = _debug;
            }
        }
    }

    private LegIk? _rightLeg;
    private LegIk? _leftLeg;

    private bool _debug;

    public override void _Ready()
    {
        base._Ready();

        var skeleton = this.RequireNode(GetSkeleton(), "Skeleton");

        var rightPole = this.RequireNode(RightPole, nameof(RightPole));
        var leftPole = this.RequireNode(LeftPole, nameof(LeftPole));

        _rightLeg = new LegIk(
            Side.Right,
            ForwardAxis,
            InvertAxis,
            skeleton,
            rightPole,
            this);

        _leftLeg = new LegIk(
            Side.Left,
            ForwardAxis,
            InvertAxis,
            skeleton,
            leftPole,
            this);
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        base._ProcessModificationWithDelta(delta);

        _rightLeg?.Process();
        _leftLeg?.Process();
    }

    public override void _ExitTree()
    {
        if (_rightLeg != null)
        {
            _rightLeg.Debug = false;
        }

        if (_leftLeg != null)
        {
            _leftLeg.Debug = false;
        }

        base._ExitTree();
    }
}