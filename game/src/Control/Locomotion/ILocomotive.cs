using AlleyCat.Component;
using Godot;

namespace AlleyCat.Control.Locomotion;

/// <summary>
/// Trait for objects that can move and rotate through their composed locomotion capability.
/// </summary>
public interface ILocomotive : IComponentHolder
{
    /// <summary>
    /// Updates the current movement-intent vector for this movable object.
    /// </summary>
    /// <param name="input">Unitless movement intent.</param>
    void Move(Vector2 input) => this.RequireComponent<ILocomotion>().Move(input);

    /// <summary>
    /// Updates the current rotation input vector for this movable object.
    /// </summary>
    /// <param name="input">Rotation input vector.</param>
    void Rotate(Vector2 input) => this.RequireComponent<ILocomotion>().Rotate(input);
}
