using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Which leg this controller drives.
/// </summary>
public enum LegSide
{
    /// <summary>
    /// The left leg.
    /// </summary>
    Left,

    /// <summary>
    /// The right leg.
    /// </summary>
    Right,
}

/// <summary>
/// Computes and applies per-leg knee pole-target placement before
/// <see cref="TwoBoneIK3D"/> solving, treating foot targets as read-only goals.
/// </summary>
[Tool]
[GlobalClass]
public partial class LegIKController : SkeletonModifier3D
{
    private const float DegenerateThreshold = 1e-6f;

    /// <summary>
    /// The foot IK goal consumed by downstream solvers.
    /// </summary>
    [Export]
    public Node3D? FootTarget
    {
        get;
        set;
    }

    /// <summary>
    /// The pole marker updated by this controller before IK solve.
    /// </summary>
    [Export]
    public Node3D? PoleTarget
    {
        get;
        set;
    }

    /// <summary>
    /// Side assignment for this per-leg controller instance.
    /// </summary>
    [Export]
    public LegSide Side
    {
        get;
        set;
    }

    /// <summary>
    /// Pole offset as a fraction of upper-leg-to-target distance.
    /// </summary>
    [Export(PropertyHint.Range, "0.1,1.0,0.01")]
    public float PoleOffsetRatio
    {
        get;
        set;
    } = 0.5f;

    /// <summary>
    /// Lower bound for pole offset distance.
    /// </summary>
    [Export(PropertyHint.Range, "0.01,0.5,0.001")]
    public float MinimumPoleOffset
    {
        get;
        set;
    } = 0.12f;

    /// <summary>
    /// Extra distance added to the rest-leg-based minimum pole offset floor.
    /// </summary>
    [Export(PropertyHint.Range, "0.0,0.2,0.001")]
    public float RestLegHalfPoleOffsetMargin
    {
        get;
        set;
    } = 0.1f;

    private Skeleton3D? _skeleton;
    private bool _bonesResolved;

    private int _upperLegIdx = -1;
    private int _lowerLegIdx = -1;
    private int _footIdx = -1;
    private float _restLegLength;

    /// <inheritdoc />
    public override void _ProcessModificationWithDelta(double delta)
    {
        if (!Active)
        {
            return;
        }

        if (!_bonesResolved)
        {
            if (!TryResolveBones())
            {
                return;
            }

            _bonesResolved = true;
        }

        if (FootTarget is null || PoleTarget is null || _skeleton is null)
        {
            return;
        }

        Vector3 upperLegPosition = BoneGlobalPosition(_upperLegIdx);
        Vector3 desiredFootPosition = FootTarget.GlobalPosition;
        Vector3 currentFootPosition = BoneGlobalPosition(_footIdx);
        Vector3 lowerLegPosition = BoneGlobalPosition(_lowerLegIdx);

        Vector3 o1 = (upperLegPosition + desiredFootPosition) * 0.5f;
        Vector3 o2 = (upperLegPosition + currentFootPosition) * 0.5f;

        Vector3 animationPoleDirectionRaw = lowerLegPosition - o2;
        if (!TryNormalise(animationPoleDirectionRaw, out Vector3 poleDirection))
        {
            return;
        }

        float currentLegLength = (o1 - upperLegPosition).Length() * 2.0f;
        float ratioBasedOffset = currentLegLength * PoleOffsetRatio;
        float offset = Mathf.Max(MinimumPoleOffset, ratioBasedOffset);

        float restLegMinimumOffset = (_restLegLength * 0.5f) + RestLegHalfPoleOffsetMargin;
        float floorOffset = Mathf.Max(MinimumPoleOffset, restLegMinimumOffset);
        offset = Mathf.Max(offset, floorOffset);

        PoleTarget.GlobalPosition = o2 + (poleDirection * offset);
    }

    private bool TryResolveBones()
    {
        Skeleton3D? skeleton = GetSkeleton();
        if (skeleton is null)
        {
            return false;
        }

        _skeleton = skeleton;

        string sidePrefix = Side == LegSide.Left ? "Left" : "Right";

        _upperLegIdx = skeleton.FindBone($"{sidePrefix}UpperLeg");
        _lowerLegIdx = skeleton.FindBone($"{sidePrefix}LowerLeg");
        _footIdx = skeleton.FindBone($"{sidePrefix}Foot");

        if (_upperLegIdx < 0
            || _lowerLegIdx < 0
            || _footIdx < 0)
        {
            return false;
        }

        Vector3 upperLegRestPosition = skeleton.GetBoneGlobalRest(_upperLegIdx).Origin;
        Vector3 lowerLegRestPosition = skeleton.GetBoneGlobalRest(_lowerLegIdx).Origin;
        Vector3 footRestPosition = skeleton.GetBoneGlobalRest(_footIdx).Origin;

        float upperToLowerRestLength = lowerLegRestPosition.DistanceTo(upperLegRestPosition);
        float lowerToFootRestLength = footRestPosition.DistanceTo(lowerLegRestPosition);
        _restLegLength = upperToLowerRestLength + lowerToFootRestLength;

        return _restLegLength > DegenerateThreshold;
    }

    private Vector3 BoneGlobalPosition(int boneIndex) =>
        _skeleton!.GlobalTransform * _skeleton.GetBoneGlobalPose(boneIndex).Origin;

    private static bool TryNormalise(Vector3 value, out Vector3 normalised)
    {
        if (value.LengthSquared() <= DegenerateThreshold)
        {
            normalised = Vector3.Zero;
            return false;
        }

        normalised = value.Normalized();
        return true;
    }
}
