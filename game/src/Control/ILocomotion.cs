using Godot;

namespace AlleyCat.Control;

/// <summary>
/// Control-layer contract for player locomotion.
/// </summary>
public interface ILocomotion
{
    /// <summary>
    /// Updates the current movement input vector.
    /// </summary>
    void SetMovementInput(Vector2 input);

    /// <summary>
    /// Updates the current rotation input vector.
    /// </summary>
    void SetRotationInput(Vector2 input);
}
