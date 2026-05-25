using Godot;

namespace AlleyCat.Interaction.Physical;

/// <summary>
/// Common contract for polymorphic physical interactions delivered from sources to receivers.
/// </summary>
public interface IPhysicalInteraction
{
    /// <summary>
    /// Gets the source that provided the interaction properties used to create the interaction.
    /// </summary>
    IPhysicalInteractionSource Source
    {
        get;
    }

    /// <summary>
    /// Gets the world-space contact point where the interaction was interpreted.
    /// </summary>
    Vector3 ContactPoint
    {
        get;
    }
}

/// <summary>
/// Impact-specific physical interaction containing source velocity at contact time.
/// </summary>
public interface IImpactPhysicalInteraction : IPhysicalInteraction
{
    /// <summary>
    /// Gets the impact source that provided source state for this interaction.
    /// </summary>
    new IImpactPhysicalInteractionSource Source
    {
        get;
    }

    /// <summary>
    /// Gets the source velocity associated with the impact.
    /// </summary>
    Vector3 Velocity
    {
        get;
    }
}

/// <summary>
/// Immutable impact interaction created by a receiver from an impact source.
/// </summary>
public sealed record ImpactPhysicalInteraction(
    IImpactPhysicalInteractionSource Source,
    Vector3 ContactPoint,
    Vector3 Velocity) : IImpactPhysicalInteraction
{
    IPhysicalInteractionSource IPhysicalInteraction.Source => Source;
}
