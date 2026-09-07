using Godot;

namespace AlleyCat.Navigation;

/// <summary>
/// Player navigation facade and movement-authority state.
/// Physical execution is owned by the future independent player navigation consumer.
/// </summary>
[GlobalClass]
public partial class PlayerNavigation : NavigationBase
{
    /// <summary>
    /// Initialises optional player navigation without movement authority.
    /// </summary>
    public PlayerNavigation()
    {
        DeactivateMovementAuthority();
    }
}
