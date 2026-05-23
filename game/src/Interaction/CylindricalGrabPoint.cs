using AlleyCat.Body;
using Godot;

namespace AlleyCat.Interaction;

/// <summary>
/// Local-Y cylindrical grab zone that can be approached along the authored cylinder length.
/// </summary>
[GlobalClass]
public partial class CylindricalGrabPoint : Marker3D, IGrabPoint
{
    /// <summary>
    /// Default cylinder grab-zone length in metres for a medium pipe-style target.
    /// </summary>
    public const float DefaultLengthMetres = 0.4f;

    /// <summary>
    /// Default maximum hand-to-axis reach distance in metres for a narrow pipe-style target.
    /// </summary>
    public const float DefaultReachDistanceMetres = 0.08f;

    /// <summary>
    /// Default minimum dot product for a palm direction within sixty degrees of the closest point direction.
    /// </summary>
    public const float DefaultPalmFacingMinimumDot = 0.5f;

    private static readonly Vector3 _defaultPalmLocalDirection = Vector3.Down;

    /// <summary>
    /// Gets or sets the length of the grab zone along this node's local Y axis, in metres.
    /// </summary>
    [Export(PropertyHint.Range, "0.001,2.0,0.001,or_greater,suffix:m")]
    public float LengthMetres { get; set; } = DefaultLengthMetres;

    /// <summary>
    /// Gets or sets the maximum allowed distance from either acquisition reference to its closest point on the local-Y
    /// segment.
    /// </summary>
    [Export(PropertyHint.Range, "0.001,1.0,0.001,or_greater,suffix:m")]
    public float ReachDistanceMetres { get; set; } = DefaultReachDistanceMetres;

    /// <summary>
    /// Gets or sets the additional perpendicular acquisition distance allowed before snapping to the actual cylinder
    /// axis.
    /// </summary>
    [Export(PropertyHint.Range, "0.0,1.0,0.001,or_greater,suffix:m")]
    public float SnapDistanceMetres { get; set; } = 0.0f;

    /// <summary>
    /// Gets or sets the minimum dot product between the palm-side direction and the direction to the closest point.
    /// </summary>
    [Export(PropertyHint.Range, "-1.0,1.0,0.01")]
    public float PalmFacingMinimumDot { get; set; } = DefaultPalmFacingMinimumDot;

    /// <summary>
    /// Gets or sets the hand-local direction treated as the palm side.
    /// </summary>
    /// <remarks>
    /// The default assumes the palm side points along the hand's local negative Y axis. This is exported because hand
    /// rigs and controller proxy nodes can use different local axes; callers should tune it to the runtime hand frame.
    /// </remarks>
    [Export]
    public Vector3 PalmLocalDirection { get; set; } = _defaultPalmLocalDirection;

    /// <summary>
    /// Gets or sets the animation to play for a successful cylindrical grab candidate.
    /// </summary>
    [Export]
    public Animation? GrabAnimation { get; set; } = null;

    /// <summary>
    /// Gets or sets the authored position offset from the hand attachment to the selected/contact grab point once held.
    /// </summary>
    [Export]
    public Vector3 GrabPointPositionOffsetFromHand { get; set; } = Vector3.Zero;

    /// <summary>
    /// Gets or sets the authored Euler rotation offset from the hand attachment to this grab point once held, in
    /// radians.
    /// </summary>
    [Export]
    public Vector3 GrabPointRotationOffsetFromHand { get; set; } = Vector3.Zero;

    /// <inheritdoc />
    public GrabPointCandidate? GetGrabPoint(LimbSide handSide, Transform3D handTransform)
        => GetGrabPoint(handSide, handTransform, 0.0f);

    /// <inheritdoc />
    public GrabPointCandidate? GetGrabPoint(
        LimbSide handSide,
        Transform3D handTransform,
        float acquisitionToleranceMetres)
    {
        if (GrabAnimation is null || LengthMetres <= 0.0f || ReachDistanceMetres <= 0.0f || SnapDistanceMetres < 0.0f)
        {
            return null;
        }

        Transform3D grabPointGlobalTransform = GlobalTransform;
        Basis handBasis = handTransform.Basis.Orthonormalized();
        Basis selectedBasis = CreateHandReferencedAxisBasis(
            grabPointGlobalTransform.Basis,
            handBasis,
            GrabPointRotationOffsetFromHand);
        Vector3 rawHandReference = handTransform.Origin;
        Vector3 authoredGripReference = rawHandReference + (handBasis * GrabPointPositionOffsetFromHand);
        float tolerance = Mathf.Max(0.0f, acquisitionToleranceMetres);
        float effectiveReachDistanceMetres = ReachDistanceMetres + tolerance;
        float reachDistanceSquared = effectiveReachDistanceMetres * effectiveReachDistanceMetres;

        AcquisitionCandidate rawHandAcquisition = GetAcquisitionCandidate(grabPointGlobalTransform, rawHandReference);
        AcquisitionCandidate authoredGripAcquisition = GetAcquisitionCandidate(grabPointGlobalTransform, authoredGripReference);
        AcquisitionCandidate selectedAcquisition;
        if (IsWithinReach(rawHandAcquisition, reachDistanceSquared))
        {
            selectedAcquisition = rawHandAcquisition;
        }
        else if (IsWithinReach(authoredGripAcquisition, reachDistanceSquared))
        {
            selectedAcquisition = authoredGripAcquisition;
        }
        else
        {
            return null;
        }

        if (PalmLocalDirection.LengthSquared() <= 0.0f)
        {
            return null;
        }

        Vector3 closestPoint = selectedAcquisition.ClosestPoint;
        Vector3 directionToClosestPoint = closestPoint - selectedAcquisition.ReferencePoint;
        bool skipPalmFacingForCentredContact = directionToClosestPoint.LengthSquared() <= 0.0f
            && selectedAcquisition.ReferencePoint.DistanceSquaredTo(closestPoint) <= 0.0f;
        if (directionToClosestPoint.LengthSquared() <= 0.0f && !skipPalmFacingForCentredContact)
        {
            return null;
        }

        Vector3 palmSideDirection = (handBasis * PalmLocalDirection).Normalized();
        if (!skipPalmFacingForCentredContact
            && palmSideDirection.Dot(directionToClosestPoint.Normalized()) < PalmFacingMinimumDot)
        {
            return null;
        }

        Transform3D grabPointOffsetFromHand = GrabPointCandidate.ComposeGrabPointOffsetFromHand(
            GrabPointPositionOffsetFromHand,
            GrabPointRotationOffsetFromHand);
        Transform3D selectedGrabPointTransform = new(selectedBasis, closestPoint);
        Transform3D handTarget = selectedGrabPointTransform * grabPointOffsetFromHand.AffineInverse();

        return new GrabPointCandidate(
            this,
            handTarget,
            GrabAnimation,
            handSide,
            handTransform,
            selectedGrabPointTransform,
            GrabPointPositionOffsetFromHand,
            GrabPointRotationOffsetFromHand,
            Mathf.Sqrt(selectedAcquisition.DistanceSquared))
        {
            AcquisitionToleranceMetres = tolerance,
        };
    }

    private AcquisitionCandidate GetAcquisitionCandidate(Transform3D grabPointGlobalTransform, Vector3 referencePoint)
    {
        Transform3D grabPointWorldToLocal = grabPointGlobalTransform.AffineInverse();
        Vector3 localReferencePoint = grabPointWorldToLocal * referencePoint;
        float halfLength = LengthMetres * 0.5f;
        float clampedY = Mathf.Clamp(localReferencePoint.Y, -halfLength, halfLength);
        bool projectionInsideSegment = localReferencePoint.Y >= -halfLength && localReferencePoint.Y <= halfLength;
        Vector3 projectedAxisPoint = grabPointGlobalTransform * new Vector3(0.0f, localReferencePoint.Y, 0.0f);
        Vector3 closestPoint = grabPointGlobalTransform * new Vector3(0.0f, clampedY, 0.0f);
        float perpendicularDistance = referencePoint.DistanceTo(projectedAxisPoint);

        return new AcquisitionCandidate(
            referencePoint,
            closestPoint,
            referencePoint.DistanceSquaredTo(closestPoint),
            perpendicularDistance,
            projectionInsideSegment);
    }

    private bool IsWithinReach(AcquisitionCandidate candidate, float reachDistanceSquared) =>
        candidate.DistanceSquared <= reachDistanceSquared
        || (SnapDistanceMetres > 0.0f
            && candidate.ProjectionInsideSegment
            && candidate.PerpendicularDistance <= SnapDistanceMetres);

    private static Basis CreateHandReferencedAxisBasis(
        Basis grabPointBasis,
        Basis handBasis,
        Vector3 grabPointRotationOffsetFromHand)
    {
        Basis authoredGrabFrameBasis = (handBasis * Basis.FromEuler(grabPointRotationOffsetFromHand)).Orthonormalized();
        Vector3 yAxis = grabPointBasis.Orthonormalized().Y.Normalized();
        if (authoredGrabFrameBasis.Y.Dot(yAxis) < 0.0f)
        {
            yAxis = -yAxis;
        }

        Vector3 projectedXAxis = ProjectOntoPlane(authoredGrabFrameBasis.X, yAxis);
        if (projectedXAxis.LengthSquared() > 0.000001f)
        {
            return CreateBasisFromXAxis(projectedXAxis.Normalized(), yAxis);
        }

        Vector3 projectedZAxis = ProjectOntoPlane(authoredGrabFrameBasis.Z, yAxis);
        if (projectedZAxis.LengthSquared() > 0.000001f)
        {
            Vector3 zAxis = projectedZAxis.Normalized();
            Vector3 xAxis = yAxis.Cross(zAxis).Normalized();

            return CreateBasisFromXAxis(xAxis, yAxis);
        }

        Vector3 referenceAxis = Mathf.Abs(yAxis.Dot(Vector3.Up)) > 0.98f ? Vector3.Right : Vector3.Up;
        Vector3 fallbackXAxis = ProjectOntoPlane(referenceAxis, yAxis).Normalized();

        return CreateBasisFromXAxis(fallbackXAxis, yAxis);
    }

    private static Vector3 ProjectOntoPlane(Vector3 axis, Vector3 planeNormal) =>
        axis - (planeNormal * axis.Dot(planeNormal));

    private static Basis CreateBasisFromXAxis(Vector3 xAxis, Vector3 yAxis)
    {
        Vector3 zAxis = xAxis.Cross(yAxis).Normalized();

        return new Basis(xAxis, yAxis, zAxis).Orthonormalized();
    }

    private readonly record struct AcquisitionCandidate(
        Vector3 ReferencePoint,
        Vector3 ClosestPoint,
        float DistanceSquared,
        float PerpendicularDistance,
        bool ProjectionInsideSegment);
}
