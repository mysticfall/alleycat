using AlleyCat.Interaction.Physical;
using Godot;

namespace AlleyCat.Body;

/// <summary>
/// Godot signal interop wrapper that carries the original delivered <see cref="IPhysicalInteraction"/> value.
/// </summary>
/// <remarks>
/// Godot C# signal parameters must be Variant-compatible. <see cref="IPhysicalInteraction"/> is a plain C# domain
/// interface, so generated body-part signals use this <see cref="RefCounted"/> wrapper to pass the exact interaction
/// instance to subscribers without changing the domain API into a Godot object type.
/// </remarks>
public sealed partial class PhysicalInteractionReceipt(IPhysicalInteraction interaction) : RefCounted
{
    /// <summary>
    /// Gets the original physical interaction payload delivered by the source.
    /// </summary>
    public IPhysicalInteraction Interaction => interaction;
}
