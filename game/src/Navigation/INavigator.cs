using AlleyCat.Core;
using Godot;

namespace AlleyCat.Navigation;

/// <summary>
/// Trait for objects that expose navigation through their composed navigation capability.
/// </summary>
public interface INavigator : IComponentHolder
{
    /// <summary>
    /// Resolves the single navigation component for this object.
    /// </summary>
    /// <returns>The navigation component.</returns>
    INavigation RequireNavigation() => this.RequireComponent<INavigation>();

    /// <summary>
    /// Requests navigation toward a transform intent for this object.
    /// </summary>
    /// <param name="destination">Destination and final-orientation intent.</param>
    /// <returns>The destination request result.</returns>
    NavigationDestinationResult SetNavigationDestination(Transform3D destination) => RequireNavigation().SetDestination(destination);
}
