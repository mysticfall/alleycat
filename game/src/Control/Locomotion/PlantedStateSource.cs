using Godot;

namespace AlleyCat.Control.Locomotion;

/// <summary>
/// Scene-wireable source of the ANIM-002 per-foot planted state of the pose evaluated on the current physics tick.
/// </summary>
[GlobalClass]
public abstract partial class PlantedStateSource : Node
{
    /// <summary>
    /// Gets the planted state of the pose the animation evaluated on the current physics tick.
    /// </summary>
    /// <param name="state">The per-foot planted state under the ANIM-002 Planted Combination Rule.</param>
    /// <returns>
    /// <see langword="false" /> when no evaluation of the current tick is available; a state from an earlier tick is
    /// never returned.
    /// </returns>
    public abstract bool TryGetPlantedState(out PlantedFeetState state);
}
