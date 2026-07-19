namespace AlleyCat.Navigation;

/// <summary>
/// Result returned when a navigation destination is requested.
/// </summary>
public enum NavigationDestinationResult
{
    /// <summary>
    /// The destination was accepted for path calculation or following.
    /// </summary>
    Accepted,

    /// <summary>
    /// The destination input was not usable.
    /// </summary>
    Invalid,

    /// <summary>
    /// The destination is valid but cannot currently be reached by the navigation map.
    /// </summary>
    Unreachable,

    /// <summary>
    /// The navigation map is not ready, so reachability is unknown and no request state was changed.
    /// </summary>
    NotReady,
}
