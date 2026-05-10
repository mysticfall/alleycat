using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Query-only shape data used by explicit hand-to-dynamic-body interaction.
/// </summary>
public readonly record struct HandDynamicInteractionShape(
    Shape3D Shape,
    Transform3D Transform,
    bool Disabled = false);
