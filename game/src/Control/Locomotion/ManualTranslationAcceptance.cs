using Godot;

namespace AlleyCat.Control.Locomotion;

/// <summary>
/// Identifies a player locomotion component that can decide whether a raw manual translation sample will be applied.
/// </summary>
/// <remarks>
/// This remains separate from <see cref="ILocomotion"/> so generic locomotion consumers retain their metadata-agnostic
/// movement contract. Player-navigation arbitration uses this optional input-boundary contract and fails closed when
/// the configured locomotion component does not provide it.
/// </remarks>
public interface IManualTranslationAcceptance
{
    /// <summary>
    /// Gets whether the supplied raw manual translation sample survives the component's current intent and permission
    /// filters.
    /// </summary>
    bool IsManualTranslationAccepted(Vector2 input);
}

/// <summary>
/// Shared player manual-translation filtering used by input acceptance and locomotion intent storage.
/// </summary>
internal static class ManualTranslationAcceptance
{
    internal static bool IsAccepted(Vector2 input, float deadzone, LocomotionPermissions permissions)
        => !ApplyMovementPermissions(input, deadzone, permissions).IsZeroApprox();

    internal static Vector2 ApplyRadialDeadzone(Vector2 input, float deadzone)
    {
        if (!input.IsFinite())
        {
            return Vector2.Zero;
        }

        float threshold = Mathf.Clamp(deadzone, 0f, 0.999f);
        float length = input.Length();
        return length <= threshold ? Vector2.Zero : input * (Mathf.Clamp((length - threshold) / (1f - threshold), 0f, 1f) / length);
    }

    private static Vector2 ApplyMovementPermissions(
        Vector2 input,
        float deadzone,
        LocomotionPermissions permissions)
        => permissions.MovementAllowed ? ApplyRadialDeadzone(input, deadzone).LimitLength(1f) : Vector2.Zero;
}
