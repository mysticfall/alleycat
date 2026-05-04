using Godot;

namespace AlleyCat.Control;

/// <summary>
/// Describes the animation-state pair used for locomotion travel and root-motion gating.
/// </summary>
public readonly record struct LocomotionStateTarget(
    StringName IdleStateName,
    StringName MovementStateName);
