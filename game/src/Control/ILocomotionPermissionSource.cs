namespace AlleyCat.Control;

/// <summary>
/// Supplies movement and rotation permissions to a locomotion component.
/// </summary>
public interface ILocomotionPermissionSource
{
    /// <summary>
    /// Resolves the current locomotion permissions contributed by this source.
    /// </summary>
    LocomotionPermissions LocomotionPermissions
    {
        get;
    }
}
