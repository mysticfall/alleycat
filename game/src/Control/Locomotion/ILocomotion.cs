using AlleyCat.Core;
using Godot;

namespace AlleyCat.Control.Locomotion;

/// <summary>
/// Control-layer contract for player locomotion.
/// </summary>
public interface ILocomotion : IComponent
{
    /// <summary>
    /// Updates the current movement-intent vector.
    /// This input is unitless locomotion intent rather than a desired velocity in any fixed unit.
    /// Implementations may use it for animation blending or locomotion-state selection, but it must not be treated as a direct planar velocity.
    /// </summary>
    void Move(Vector2 input);

    /// <summary>
    /// Updates the current rotation input vector.
    /// </summary>
    void Rotate(Vector2 input);
}
