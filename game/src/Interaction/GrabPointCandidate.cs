using AlleyCat.Body;
using Godot;

namespace AlleyCat.Interaction;

/// <summary>
/// Immutable query result describing the source grab point, target hand pose, and animation for a potential grab.
/// </summary>
/// <param name="Source">The source grab-point component that produced this candidate.</param>
/// <param name="HandTarget">The global transform where the hand should be positioned when grabbing.</param>
/// <param name="Animation">The hand animation resource to play for the grab.</param>
/// <param name="HandSide">The hand side used to produce this candidate.</param>
/// <param name="HandTransform">The global hand transform used to produce this candidate.</param>
/// <param name="GrabPointTransform">The selected global grab-point transform at query time.</param>
/// <param name="GrabPointPositionOffsetFromHand">Authored position offset from the hand attachment to the grab point when held.</param>
/// <param name="GrabPointRotationOffsetFromHand">Authored Euler rotation offset from the hand attachment to the grab point when held.</param>
/// <param name="AcquisitionDistance">The canonical distance used for discovery, ranking, and filtering.</param>
public sealed record GrabPointCandidate(
    IGrabPoint Source,
    Transform3D HandTarget,
    Animation Animation,
    LimbSide HandSide,
    Transform3D HandTransform,
    Transform3D GrabPointTransform,
    Vector3 GrabPointPositionOffsetFromHand,
    Vector3 GrabPointRotationOffsetFromHand,
    float AcquisitionDistance)
{
    /// <summary>
    /// Gets the composed hand-relative transform from the authored position and Euler rotation offsets.
    /// </summary>
    public Transform3D GrabPointOffsetFromHand => ComposeGrabPointOffsetFromHand(
        GrabPointPositionOffsetFromHand,
        GrabPointRotationOffsetFromHand);

    /// <summary>
    /// Initialises a candidate with an identity held grab-point offset for backwards-compatible call sites.
    /// </summary>
    public GrabPointCandidate(
        IGrabPoint source,
        Transform3D handTarget,
        Animation animation,
        LimbSide handSide,
        Transform3D handTransform,
        Transform3D grabPointTransform)
        : this(
            source,
            handTarget,
            animation,
            handSide,
            handTransform,
            grabPointTransform,
            Vector3.Zero,
            Vector3.Zero,
            handTransform.Origin.DistanceTo(grabPointTransform.Origin))
    {
    }

    /// <summary>
    /// Initialises a candidate from separate held grab-point offsets.
    /// </summary>
    public GrabPointCandidate(
        IGrabPoint source,
        Transform3D handTarget,
        Animation animation,
        LimbSide handSide,
        Transform3D handTransform,
        Transform3D grabPointTransform,
        Vector3 grabPointPositionOffsetFromHand,
        Vector3 grabPointRotationOffsetFromHand)
        : this(
            source,
            handTarget,
            animation,
            handSide,
            handTransform,
            grabPointTransform,
            grabPointPositionOffsetFromHand,
            grabPointRotationOffsetFromHand,
            handTransform.Origin.DistanceTo(grabPointTransform.Origin))
    {
    }

    /// <summary>
    /// Initialises a candidate from an already-composed held grab-point offset.
    /// </summary>
    public GrabPointCandidate(
        IGrabPoint source,
        Transform3D handTarget,
        Animation animation,
        LimbSide handSide,
        Transform3D handTransform,
        Transform3D grabPointTransform,
        Transform3D grabPointOffsetFromHand)
        : this(
            source,
            handTarget,
            animation,
            handSide,
            handTransform,
            grabPointTransform,
            grabPointOffsetFromHand.Origin,
            grabPointOffsetFromHand.Basis.Orthonormalized().GetEuler(),
            handTransform.Origin.DistanceTo(grabPointTransform.Origin))
    {
    }

    /// <summary>
    /// Initialises a candidate from an already-composed held grab-point offset and explicit acquisition distance.
    /// </summary>
    public GrabPointCandidate(
        IGrabPoint source,
        Transform3D handTarget,
        Animation animation,
        LimbSide handSide,
        Transform3D handTransform,
        Transform3D grabPointTransform,
        Transform3D grabPointOffsetFromHand,
        float acquisitionDistance)
        : this(
            source,
            handTarget,
            animation,
            handSide,
            handTransform,
            grabPointTransform,
            grabPointOffsetFromHand.Origin,
            grabPointOffsetFromHand.Basis.Orthonormalized().GetEuler(),
            acquisitionDistance)
    {
    }

    /// <summary>
    /// Composes the hand-relative grab-point transform from separate authoring vectors.
    /// </summary>
    public static Transform3D ComposeGrabPointOffsetFromHand(Vector3 positionOffsetFromHand, Vector3 rotationOffsetFromHand)
        => new(Basis.FromEuler(rotationOffsetFromHand), positionOffsetFromHand);
}
