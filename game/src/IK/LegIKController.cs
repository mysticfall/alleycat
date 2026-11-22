using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Computes and applies per-leg knee pole-target placement before
/// <see cref="TwoBoneIK3D"/> solving, treating foot targets as read-only goals.
/// </summary>
[Tool]
[GlobalClass]
public partial class LegIKController : SkeletonModifier3D
{
    private const float DegenerateThreshold = 1e-6f;
    private const float ReliableBendMetres = 0.025f;
    private const float CueFollowRadiansPerSecond = 75f * Mathf.Pi / 180f;
    private float _poleAngle;
    private Vector3 _lastPoleLocal;

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
    public LimbSide Side
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

        Vector3 goal = desiredFootPosition - upperLegPosition;
        if (!TryNormalise(goal, out Vector3 axis)
            || !TryNormalise(currentFootPosition - upperLegPosition, out Vector3 animatedAxis))
        {
            return;
        }

        // Measure transverse bend, not the offset from the leg midpoint (which
        // contains thigh/shin length asymmetry even on a perfectly straight leg).
        Vector3 bend = (lowerLegPosition - upperLegPosition).Slide(animatedAxis);
        float currentLegLength = goal.Length();
        Basis frame = _skeleton.GlobalBasis.Orthonormalized();
        Vector3 reference = ResolveReference(axis, currentLegLength / _restLegLength,
            frame, FootTarget.GlobalBasis.Orthonormalized(), Side, bend,
            out Vector3 transport);
        if (reference.IsZeroApprox())
        {
            // No defined swing axis for a leg pointing straight up. Leave the target in place.
            return;
        }

        float angle = transport.SignedAngleTo(reference, axis);
        float maxStep = CueFollowRadiansPerSecond * (float)delta;
        _poleAngle = Mathf.RotateToward(_poleAngle, angle, maxStep);
        Vector3 poleDirection = transport.Rotated(axis, _poleAngle);
        // Outside the supported envelope the swing chart can turn arbitrarily fast near
        // its antipode. Carry the previous plane through re-entry instead of snapping.
        if (axis.Dot(frame.Y) > 0.8660254f
            && TryNormalise((frame * _lastPoleLocal).Slide(axis), out Vector3 previous))
        {
            float turn = previous.SignedAngleTo(poleDirection, axis);
            poleDirection = previous.Rotated(axis, Mathf.Clamp(turn, -maxStep, maxStep));
            _poleAngle = transport.SignedAngleTo(poleDirection, axis);
        }

        _lastPoleLocal = frame.Transposed() * poleDirection;
        float ratioBasedOffset = currentLegLength * PoleOffsetRatio;
        float offset = Mathf.Max(MinimumPoleOffset, ratioBasedOffset);

        float restLegMinimumOffset = (_restLegLength * 0.5f) + RestLegHalfPoleOffsetMargin;
        float floorOffset = Mathf.Max(MinimumPoleOffset, restLegMinimumOffset);
        offset = Mathf.Max(offset, floorOffset);
        PoleTarget.GlobalPosition = upperLegPosition + (goal * 0.5f) + (poleDirection * offset);
    }

    internal static Vector3 ResolveReference(
        Vector3 axis, float reach, Basis frame, Basis foot, LimbSide side, Vector3 bend, out Vector3 transport)
    {
        Vector3 down = -frame.Y;
        float cosine = Mathf.Clamp(down.Dot(axis), -1f, 1f);
        transport = Vector3.Zero;
        if (1f + cosine <= DegenerateThreshold)
        {
            return Vector3.Zero;
        }

        // Shortest-swing transport of a tangent to down. Unlike a projection of forward,
        // this has no singularity at forward reach: on that meridian forward swings into up.
        Vector3 swung = (frame.Z - ((down + axis) * (frame.Z.Dot(axis) / (1f + cosine)))).Normalized();
        float ordinary = 1f - Mathf.SmoothStep(Mathf.DegToRad(35f), Mathf.Pi / 4f, Mathf.Acos(cosine));
        float fold = 1f - Mathf.SmoothStep(0.45f, 0.7f, reach);
        float raise = Mathf.SmoothStep(0.3f, 0.8f, axis.Dot(frame.Z));
        float opening = (Mathf.Pi / 4f) + (Mathf.Pi / 2f * Mathf.SmoothStep(0f, 0.6f, axis.Dot(frame.Y)));
        float outward = side == LimbSide.Left ? -1f : 1f;
        transport = swung.Rotated(axis, outward * fold * raise * opening);
        float footAngle = 0f;
        float usable = Mathf.SmoothStep(0.8f, 0.9f, Mathf.Abs(frame.Y.Dot(foot.Y)));
        if (ordinary > 0f)
        {
            Vector3 projected = frame.Z.Slide(axis).Normalized();
            transport = transport.Slerp(projected, ordinary);
            if (usable > 0f)
            {
                // Unwrap the projected-foot angle about the actual foot heading, so
                // changing the leg axis does not cross atan2's seam for a fixed foot.
                float yaw = frame.Z.SignedAngleTo(foot.Z.Slide(down).Normalized(), down);
                float projectedYaw = projected.SignedAngleTo(foot.Z.Slide(axis).Normalized(), axis);
                footAngle = ordinary * usable * (yaw + Mathf.AngleDifference(yaw, projectedYaw));
            }
        }

        Vector3 weakReference = transport.Rotated(axis, footAngle);
        Vector3 transverse = bend.Slide(axis);
        float strength = transverse.Length();
        if (strength == 0f)
        {
            return weakReference;
        }

        Vector3 cue = transverse / strength;
        float confidence = Mathf.SmoothStep(0f, ReliableBendMetres, strength);
        float alignment = cue.Dot(weakReference);
        if (alignment <= 0f)
        {
            return weakReference;
        }

        // On an unusably tilted foot the coherent animation plane owns the solution.
        // Fade out its authority at the side plane too: a hard cut there swaps a
        // sideways animated pole for the neutral reference in a single step.
        float coherent = Mathf.SmoothStep(0f, 0.5f, alignment);
        float authority = confidence * Mathf.Lerp(alignment, coherent, ordinary * (1f - usable));
        return weakReference.Rotated(axis, weakReference.SignedAngleTo(cue, axis) * authority);
    }

    private bool TryResolveBones()
    {
        Skeleton3D? skeleton = GetSkeleton();
        if (skeleton is null)
        {
            return false;
        }

        _skeleton = skeleton;

        string sidePrefix = Side == LimbSide.Left ? "Left" : "Right";

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
