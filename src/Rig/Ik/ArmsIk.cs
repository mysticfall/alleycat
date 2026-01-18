using AlleyCat.Common;
using Godot;

namespace AlleyCat.Rig.Ik;

internal class ArmIk(
    Side armSide,
    Skeleton3D skeleton,
    Node constraint,
    Node3D markerOwner
)
{
    private readonly int _shoulderIndex = skeleton.FindBone("Shoulder".PrefixWith(armSide));
    private readonly int _upperArmIndex = skeleton.FindBone("UpperArm".PrefixWith(armSide));

    private readonly Node3D _handIk = constraint.GetParent<Node3D>();

    private const string PoleName = "KneePole";
    private const float PoleLength = 0.3f;
    private const float PoleSideOffset = 0.1f;
    private const float InsideBodyPushOffMultiplier = 2f;

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
                    Name = PoleName.PrefixWith(armSide)
                };

                markerOwner.AddChild(_marker, @internal: Node.InternalMode.Back);

                _marker.Owner = markerOwner;
            }
        }
    }

    private Marker3D? _marker = (Marker3D?)markerOwner.FindChild(
        PoleName.PrefixWith(armSide));

    public Transform3D UpperArmPose => skeleton.GetBoneGlobalPose(_upperArmIndex);

    public void Process(
        Transform3D toLocal,
        Vector3 right,
        Vector3 up,
        Vector3 forward
    )
    {
        var side = armSide == Side.Right ? right : right * -1;

        var skeletonTransform = skeleton.GlobalTransform;
        var toSkeleton = skeletonTransform.Inverse();

        var upperArm = UpperArmPose;

        var hand = toSkeleton * _handIk.GlobalTransform;
        var arm = hand.Origin - upperArm.Origin;
        var armDirection = arm.Normalized();

        var localOffset = toLocal * hand.Origin - toLocal * upperArm.Origin;
        var horizontalOffset = armSide switch
        {
            Side.Right => PoleSideOffset -
                          Math.Min(localOffset.X * InsideBodyPushOffMultiplier, 0),
            Side.Left => PoleSideOffset +
                         Math.Max(localOffset.X * InsideBodyPushOffMultiplier, 0),
            _ => throw new InvalidOperationException($"Invalid arm side: {armSide}")
        } * side;

        var dot = up.Dot(armDirection);
        var poleDirection = (forward * dot - up * (1 - Math.Abs(dot))).Normalized();

        var pole = upperArm.Origin +
                   armDirection * arm.Length() / 2 +
                   horizontalOffset +
                   poleDirection * PoleLength;

        var shoulderRotation = Math.Clamp(localOffset.Y, 0, 0.5f) * 2 *
                               float.DegreesToRadians(45);

        var shoulderRest = skeleton
            .GetBoneRest(_shoulderIndex)
            .Basis
            .GetRotationQuaternion()
            .GetEuler();

        skeleton.SetBonePoseRotation(
            _shoulderIndex,
            Quaternion.FromEuler(
                new Vector3(shoulderRest.X + shoulderRotation, shoulderRest.Y, shoulderRest.Z)
            )
        );

        constraint.Set("pole_position", pole);

        if (_marker != null)
        {
            _marker.GlobalPosition = skeletonTransform.Origin + skeletonTransform * pole;
        }
    }
}

[GlobalClass, Tool]
public partial class ArmsIk : SkeletonModifier3D
{
    [Export] public Node? RightPole { get; set; }
    [Export] public Node? LeftPole { get; set; }

    [Export]
    public bool Debug
    {
        get => _debug;
        set
        {
            _debug = value;

            if (_rightArm != null)
            {
                _rightArm.Debug = _debug;
            }

            if (_leftArm != null)
            {
                _leftArm.Debug = _debug;
            }
        }
    }

    private ArmIk? _rightArm;
    private ArmIk? _leftArm;

    private int _upperChestIndex = -1;
    private bool _debug;

    public override void _Ready()
    {
        base._Ready();

        var skeleton = this.RequireNode(GetSkeleton(), "Skeleton");

        _upperChestIndex = skeleton.FindBone("UpperChest");

        var rightPole = this.RequireNode(RightPole, nameof(RightPole));
        var leftPole = this.RequireNode(LeftPole, nameof(LeftPole));

        _rightArm = new ArmIk(Side.Right, skeleton, rightPole, this);
        _leftArm = new ArmIk(Side.Left, skeleton, leftPole, this);
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        base._ProcessModificationWithDelta(delta);

        if (_rightArm == null || _leftArm == null || !Active) return;

        var skeleton = this.RequireNode(GetSkeleton(), "Skeleton");

        var rightUpperArm = _rightArm.UpperArmPose;
        var leftUpperArm = _leftArm.UpperArmPose;

        var origin = (rightUpperArm.Origin + leftUpperArm.Origin) / 2;
        var upperChest = skeleton.GetBoneGlobalPose(_upperChestIndex);

        var up = (origin - upperChest.Origin).Normalized();
        var right = (rightUpperArm.Origin - leftUpperArm.Origin).Normalized();
        var forward = up.Cross(right);

        var basis = new Basis(right, up, forward);
        var toLocal = new Transform3D(basis, origin).Inverse();

        _rightArm.Process(toLocal, right, up, forward);
        _leftArm.Process(toLocal, right, up, forward);
    }

    public override void _ExitTree()
    {
        if (_rightArm != null)
        {
            _rightArm.Debug = false;
        }

        if (_leftArm != null)
        {
            _leftArm.Debug = false;
        }

        base._ExitTree();
    }
}