using Godot;

namespace AlleyCat.Interaction.Physical;

/// <summary>
/// Physical interaction source subtype carrying impact source state.
/// </summary>
public interface IImpactPhysicalInteractionSource : IPhysicalInteractionSource
{
    /// <summary>
    /// Gets the source velocity at impact time.
    /// </summary>
    Vector3 Velocity
    {
        get;
    }
}
