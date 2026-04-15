using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Drives IK target <see cref="CharacterBody3D"/> nodes using velocity + <see cref="CharacterBody3D.MoveAndSlide"/>.
/// </summary>
public sealed class IKTargetBodyFollower(CharacterBody3D body, Func<Transform3D> targetTransformSource)
{
    private const float DeltaEpsilon = 1e-6f;

    /// <summary>
    /// Maximum translation speed in metres per second.
    /// </summary>
    public float MaximumSpeed
    {
        get;
        set;
    } = 24.0f;

    /// <summary>
    /// Distance below which the target snaps directly.
    /// </summary>
    public float SnapDistance
    {
        get;
        set;
    } = 0.0025f;

    /// <summary>
    /// Moves the configured body towards its configured transform source.
    /// </summary>
    public void Follow(double delta)
    {
        float deltaSeconds = (float)delta;
        if (deltaSeconds <= DeltaEpsilon)
        {
            return;
        }

        Transform3D targetTransform = targetTransformSource();

        Vector3 displacement = targetTransform.Origin - body.GlobalPosition;
        float snapDistanceSquared = SnapDistance * SnapDistance;
        if (displacement.LengthSquared() <= snapDistanceSquared)
        {
            body.GlobalPosition = targetTransform.Origin;
            body.Velocity = Vector3.Zero;
            body.GlobalBasis = targetTransform.Basis.Orthonormalized();
            return;
        }

        Vector3 desiredVelocity = displacement / deltaSeconds;
        float maxSpeed = Mathf.Max(MaximumSpeed, 0.0f);
        body.Velocity = maxSpeed <= DeltaEpsilon
            ? Vector3.Zero
            : desiredVelocity.LengthSquared() > (maxSpeed * maxSpeed)
                ? desiredVelocity.Normalized() * maxSpeed
                : desiredVelocity;

        _ = body.MoveAndSlide();
        body.GlobalBasis = targetTransform.Basis.Orthonormalized();
    }
}
