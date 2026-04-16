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
[GlobalClass]
public partial class LegIkController : SkeletonModifier3D
{
    private const float DegenerateThreshold = 1e-6f;
    private static readonly Vector3 _skeletonSpaceForwardAxis = new(0.0f, 0.0f, 1.0f);
    private static readonly Vector3 _skeletonSpaceUpAxis = new(0.0f, 1.0f, 0.0f);

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
    /// Small side-outward bias to keep neutral knee planes from crossing inward.
    /// </summary>
    [Export(PropertyHint.Range, "0,1.0,0.01")]
    public float SideBiasWeight
    {
        get;
        set;
    } = 0.2f;

    private Skeleton3D? _skeleton;
    private bool _bonesResolved;
    private bool _footAxesResolved;

    private int _hipsIdx = -1;
    private int _upperLegIdx = -1;
    private int _footIdx = -1;

    private Vector3 _footForwardLocalAxis = Vector3.Forward;
    private Vector3 _footUpLocalAxis = Vector3.Up;

    /// <inheritdoc />
    public override void _ProcessModificationWithDelta(double delta)
    {
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

        if (!_footAxesResolved && !TryCacheFootTargetLocalAxes())
        {
            return;
        }

        Vector3 upperLegPosition = BoneGlobalPosition(_upperLegIdx);
        Vector3 desiredFootPosition = FootTarget.GlobalPosition;
        Vector3 legDirection = desiredFootPosition - upperLegPosition;
        if (legDirection.LengthSquared() <= DegenerateThreshold)
        {
            return;
        }

        legDirection = legDirection.Normalized();

        Basis footBasis = FootTarget.GlobalTransform.Basis.Orthonormalized();
        Vector3 footForwardAxis = footBasis * _footForwardLocalAxis;
        Vector3 footUpAxis = footBasis * _footUpLocalAxis;

        Vector3 sideOutwardAxis = ComputeSideOutwardAxis(upperLegPosition, legDirection);
        Vector3 poleDirection = ComputePoleDirection(
            legDirection,
            footForwardAxis,
            footUpAxis,
            sideOutwardAxis,
            SideBiasWeight);

        if (poleDirection.LengthSquared() <= DegenerateThreshold)
        {
            return;
        }

        Vector3 midpoint = (upperLegPosition + desiredFootPosition) * 0.5f;
        float offset = Mathf.Max(MinimumPoleOffset, (desiredFootPosition - upperLegPosition).Length() * PoleOffsetRatio);

        PoleTarget.GlobalPosition = midpoint + (poleDirection * offset);
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

        _hipsIdx = skeleton.FindBone("Hips");
        _upperLegIdx = skeleton.FindBone($"{sidePrefix}UpperLeg");
        int lowerLegIdx = skeleton.FindBone($"{sidePrefix}LowerLeg");
        _footIdx = skeleton.FindBone($"{sidePrefix}Foot");

        return _hipsIdx >= 0
            && _upperLegIdx >= 0
            && lowerLegIdx >= 0
            && _footIdx >= 0;
    }

    private Vector3 ComputeSideOutwardAxis(Vector3 upperLegPosition, Vector3 legDirection)
    {
        if (_skeleton is null)
        {
            return Side == LegSide.Left ? Vector3.Left : Vector3.Right;
        }

        Vector3 hipsPosition = BoneGlobalPosition(_hipsIdx);
        Vector3 hipsToLeg = upperLegPosition - hipsPosition;
        Vector3 projected = hipsToLeg - (hipsToLeg.Dot(legDirection) * legDirection);

        return projected.LengthSquared() > DegenerateThreshold
            ? projected.Normalized()
            : Side == LegSide.Left
            ? -_skeleton.GlobalTransform.Basis.Column0.Normalized()
            : _skeleton.GlobalTransform.Basis.Column0.Normalized();
    }

    private Vector3 BoneGlobalPosition(int boneIndex) =>
        _skeleton!.GlobalTransform * _skeleton.GetBoneGlobalPose(boneIndex).Origin;

    private bool TryCacheFootTargetLocalAxes()
    {
        Transform3D footRestPose = _skeleton!.GetBoneGlobalRest(_footIdx);
        Basis footRestBasis = footRestPose.Basis.Orthonormalized();
        Basis inverseFootRest = footRestBasis.Inverse();

        Vector3 footForwardLocal = inverseFootRest * _skeletonSpaceForwardAxis;
        Vector3 footUpLocal = inverseFootRest * _skeletonSpaceUpAxis;

        if (!TryNormalise(footForwardLocal, out footForwardLocal)
            || !TryNormalise(footUpLocal, out footUpLocal))
        {
            return false;
        }

        footUpLocal = Reject(footUpLocal, footForwardLocal);
        if (!TryNormalise(footUpLocal, out footUpLocal))
        {
            return false;
        }

        _footForwardLocalAxis = footForwardLocal;
        _footUpLocalAxis = footUpLocal;
        _footAxesResolved = true;

        return true;
    }

    private static AxisBlendWeights ComputeAxisBlendWeights(
        Vector3 legDirection,
        Vector3 footForwardAxis,
        Vector3 footUpAxis)
    {
        if (!TryNormalise(legDirection, out Vector3 legDir)
            || !TryNormalise(footForwardAxis, out Vector3 footForward)
            || !TryNormalise(footUpAxis, out _))
        {
            return new AxisBlendWeights(0.5f, 0.5f);
        }

        float forwardAlignment = Mathf.Abs(footForward.Dot(legDir));
        float footUpWeight = Mathf.SmoothStep(0.0f, 1.0f, Mathf.Clamp(forwardAlignment, 0.0f, 1.0f));
        float footForwardWeight = 1.0f - footUpWeight;

        return new AxisBlendWeights(footForwardWeight, footUpWeight);
    }

    private static Vector3 ComputePoleDirection(
        Vector3 legDirection,
        Vector3 footForwardAxis,
        Vector3 footUpAxis,
        Vector3 sideOutwardAxis,
        float sideBiasWeight)
    {
        if (!TryNormalise(legDirection, out Vector3 legDir)
            || !TryNormalise(footForwardAxis, out Vector3 footForward)
            || !TryNormalise(footUpAxis, out Vector3 footUp))
        {
            return Vector3.Zero;
        }

        Vector3 projectedForwardRaw = Reject(footForward, legDir);
        Vector3 projectedUpRaw = Reject(footUp, legDir);

        bool hasProjectedForward = TryNormalise(projectedForwardRaw, out Vector3 projectedForward);
        bool hasProjectedUp = TryNormalise(projectedUpRaw, out Vector3 projectedUp);

        if (!hasProjectedForward && !hasProjectedUp)
        {
            projectedForwardRaw = Reject(sideOutwardAxis, legDir);
            return TryNormalise(projectedForwardRaw, out Vector3 sideFallback)
                ? sideFallback
                : Vector3.Zero;
        }

        AxisBlendWeights weights = ComputeAxisBlendWeights(legDir, footForward, footUp);

        float projectedForwardStrength = hasProjectedForward ? projectedForwardRaw.Length() : 0.0f;
        float projectedUpStrength = hasProjectedUp ? projectedUpRaw.Length() : 0.0f;

        float forwardWeight = weights.FootForwardWeight * projectedForwardStrength;
        float upWeight = weights.FootUpWeight * projectedUpStrength;
        float combinedWeight = forwardWeight + upWeight;

        Vector3 blendedProjected = combinedWeight > DegenerateThreshold
            ? ((projectedForward * forwardWeight) + (projectedUp * upWeight)) / combinedWeight
            : hasProjectedForward
                ? projectedForward
                : projectedUp;

        if (!TryNormalise(blendedProjected, out Vector3 poleDirection))
        {
            Vector3 preferredAxis = weights.FootForwardWeight >= weights.FootUpWeight
                ? (hasProjectedForward ? projectedForward : projectedUp)
                : (hasProjectedUp ? projectedUp : projectedForward);
            Vector3 secondaryAxis = weights.FootForwardWeight >= weights.FootUpWeight
                ? (hasProjectedUp ? projectedUp : projectedForward)
                : (hasProjectedForward ? projectedForward : projectedUp);

            if (!TryNormalise(preferredAxis, out poleDirection))
            {
                if (!TryNormalise(secondaryAxis, out poleDirection))
                {
                    Vector3 sideFallback = Reject(sideOutwardAxis, legDir);
                    if (!TryNormalise(sideFallback, out poleDirection))
                    {
                        return Vector3.Zero;
                    }
                }
            }
        }

        if (TryNormalise(Reject(sideOutwardAxis, legDir), out Vector3 sideAxis) && sideBiasWeight > 0.0f)
        {
            Vector3 biased = poleDirection + (sideAxis * sideBiasWeight);
            if (TryNormalise(biased, out Vector3 biasedDirection))
            {
                poleDirection = biasedDirection;
            }
        }

        return poleDirection;
    }

    private static Vector3 Reject(Vector3 vector, Vector3 normal) =>
        vector - (vector.Dot(normal) * normal);

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

    private readonly record struct AxisBlendWeights(float FootForwardWeight, float FootUpWeight);
}
