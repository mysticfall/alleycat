using Godot;

namespace AlleyCat.Navigation;

/// <summary>
/// Pure coordinate and steering conversions used by locomotive navigation consumers.
/// </summary>
internal static class LocomotiveNavigationInput
{
    private const float MinimumAxisLengthSquared = 0.000001f;

    /// <summary>
    /// Converts a world-space travel direction into bounded actor-local lateral and forward input.
    /// </summary>
    internal static Vector2 ToLocalMovement(Basis actorWorldBasis, Vector3 worldTravelDirection)
    {
        if (!actorWorldBasis.IsFinite()
            || !worldTravelDirection.IsFinite()
            || actorWorldBasis.X.LengthSquared() <= MinimumAxisLengthSquared
            || actorWorldBasis.Y.LengthSquared() <= MinimumAxisLengthSquared
            || actorWorldBasis.Z.LengthSquared() <= MinimumAxisLengthSquared)
        {
            return Vector2.Zero;
        }

        Basis rotation = actorWorldBasis.Orthonormalized();
        if (!rotation.IsFinite())
        {
            return Vector2.Zero;
        }

        Vector3 localDirection = rotation.Transposed() * worldTravelDirection;
        var input = new Vector2(localDirection.X, -localDirection.Z);
        if (!input.IsFinite())
        {
            return Vector2.Zero;
        }

        float lengthSquared = input.LengthSquared();
        return lengthSquared > 1.0f ? input / Mathf.Sqrt(lengthSquared) : input;
    }

    /// <summary>
    /// Maps signed yaw error to finite, continuous and bounded horizontal rotation input.
    /// </summary>
    internal static Vector2 ToRotation(float signedYawError, float gain, float deadZoneRadians)
    {
        if (!float.IsFinite(signedYawError) || !float.IsFinite(gain) || !float.IsFinite(deadZoneRadians))
        {
            return Vector2.Zero;
        }

        float magnitude = Mathf.Max(Mathf.Abs(signedYawError) - Mathf.Max(deadZoneRadians, 0.0f), 0.0f)
            * Mathf.Max(gain, 0.0f);
        float horizontal = Mathf.Sign(signedYawError) * Mathf.Min(magnitude, 1.0f);
        return new Vector2(float.IsFinite(horizontal) ? horizontal : 0.0f, 0.0f);
    }

    /// <summary>
    /// Converts Godot world-up yaw (positive left, negative right) to the locomotion command convention
    /// (negative left, positive right).
    /// </summary>
    internal static float ToSemanticTurnInput(float worldYawCommand)
        => float.IsFinite(worldYawCommand) ? -worldYawCommand : 0.0f;
}
