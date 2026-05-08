using AlleyCat.Body;
using Godot;

namespace AlleyCat.Interaction;

/// <summary>
/// Centre-origin grab point for small spherical objects that can be approached from any direction.
/// </summary>
[GlobalClass]
public partial class SphericalGrabPoint : Marker3D, IGrabPoint
{
    /// <summary>
    /// Default maximum centre-to-hand reach distance in metres for a four-centimetre ball-style target.
    /// </summary>
    public const float DefaultReachDistanceMetres = 0.08f;

    /// <summary>
    /// Default minimum dot product for a palm direction within sixty degrees of the centre direction.
    /// </summary>
    public const float DefaultPalmFacingMinimumDot = 0.5f;

    private static readonly Vector3 _defaultPalmLocalDirection = Vector3.Down;

    /// <summary>
    /// Gets or sets the maximum allowed distance from the hand origin to this spherical centre, in metres.
    /// </summary>
    [Export(PropertyHint.Range, "0.001,1.0,0.001,or_greater,suffix:m")]
    public float ReachDistanceMetres { get; set; } = DefaultReachDistanceMetres;

    /// <summary>
    /// Gets or sets the minimum dot product between the palm-side direction and the direction to this centre.
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
    /// Gets or sets the animation to play for a successful spherical grab candidate.
    /// </summary>
    [Export]
    public Godot.Animation? GrabAnimation { get; set; } = null;

    /// <inheritdoc />
    public GrabPointCandidate? GetGrabPoint(LimbSide handSide, Transform3D handTransform)
    {
        if (GrabAnimation is null || ReachDistanceMetres <= 0.0f)
        {
            return null;
        }

        Vector3 centre = GlobalTransform.Origin;
        Vector3 handToCentre = centre - handTransform.Origin;
        float distanceSquared = handToCentre.LengthSquared();
        if (distanceSquared <= 0.0f || distanceSquared > ReachDistanceMetres * ReachDistanceMetres)
        {
            return null;
        }

        if (PalmLocalDirection.LengthSquared() <= 0.0f)
        {
            return null;
        }

        Vector3 directionToCentre = handToCentre.Normalized();
        Vector3 palmSideDirection = (handTransform.Basis * PalmLocalDirection).Normalized();
        if (palmSideDirection.Dot(directionToCentre) < PalmFacingMinimumDot)
        {
            return null;
        }

        var handTarget = new Transform3D(handTransform.Basis, centre);

        return new GrabPointCandidate(this, handTarget, GrabAnimation);
    }
}
