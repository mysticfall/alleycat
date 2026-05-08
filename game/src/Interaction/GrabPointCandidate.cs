using Godot;

namespace AlleyCat.Interaction;

/// <summary>
/// Immutable query result describing the source grab point, target hand pose, and animation for a potential grab.
/// </summary>
/// <param name="Source">The source grab-point component that produced this candidate.</param>
/// <param name="HandTarget">The global transform where the hand should be positioned when grabbing.</param>
/// <param name="Animation">The hand animation resource to play for the grab.</param>
public sealed record GrabPointCandidate(
    IGrabPoint Source,
    Transform3D HandTarget,
    Godot.Animation Animation);
