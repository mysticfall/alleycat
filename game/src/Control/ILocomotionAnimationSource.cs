namespace AlleyCat.Control;

/// <summary>
/// Optionally supplies a pose-specific locomotion animation-state pair.
/// </summary>
public interface ILocomotionAnimationSource
{
    /// <summary>
    /// Resolves the current locomotion animation-state pair contributed by this source.
    /// </summary>
    LocomotionStateTarget? LocomotionStateTarget
    {
        get;
    }
}
