using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Profile-backed query-only shape data for hand actuators: feeds generated hand movement collision shapes
/// and the hand physical-interaction channel's sweep queries.
/// </summary>
public readonly record struct HandProfileShape(
    Shape3D Shape,
    Transform3D Transform,
    bool Disabled = false);
