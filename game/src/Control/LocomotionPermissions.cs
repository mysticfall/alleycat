namespace AlleyCat.Control;

/// <summary>
/// Aggregated locomotion permissions for movement and rotation.
/// </summary>
public readonly record struct LocomotionPermissions(bool MovementAllowed, bool RotationAllowed)
{
    /// <summary>
    /// Fully allows locomotion movement and rotation.
    /// </summary>
    public static LocomotionPermissions Allowed => new(MovementAllowed: true, RotationAllowed: true);

    /// <summary>
    /// Allows rotation while blocking movement.
    /// </summary>
    public static LocomotionPermissions RotationOnly => new(MovementAllowed: false, RotationAllowed: true);

    /// <summary>
    /// Blocks both movement and rotation.
    /// </summary>
    public static LocomotionPermissions Blocked => new(MovementAllowed: false, RotationAllowed: false);

    /// <summary>
    /// Combines this permission set with another source using logical AND.
    /// </summary>
    public LocomotionPermissions Combine(LocomotionPermissions other)
        => new(
            MovementAllowed && other.MovementAllowed,
            RotationAllowed && other.RotationAllowed);
}
