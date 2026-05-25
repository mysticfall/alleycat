using AlleyCat.Common;

namespace AlleyCat.Interaction.Physical;

/// <summary>
/// Capability implemented by objects that can interpret physical interaction sources and relay receipts.
/// </summary>
public interface IPhysicalInteractionReceiver : ITagged
{
    /// <summary>
    /// Interprets a physical interaction source, relays accepted interactions downstream, and returns the created interaction.
    /// </summary>
    /// <param name="source">The source carrying interaction properties to interpret.</param>
    /// <returns>The created interaction, or <see langword="null"/> when the source is unsupported.</returns>
    IPhysicalInteraction? InteractWith(IPhysicalInteractionSource source);
}
