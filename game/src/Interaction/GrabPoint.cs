using Godot;

namespace AlleyCat.Interaction;

/// <summary>
/// Describes the target pose and animation returned by a successful grabbable query.
/// </summary>
/// <param name="HandTarget">The global transform where the hand should be positioned when grabbing.</param>
/// <param name="Animation">The hand animation resource to play for the grab.</param>
public readonly record struct GrabPoint(
    Transform3D HandTarget,
    Animation Animation);
