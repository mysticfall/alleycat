using AlleyCat.Core;
using Godot;

namespace AlleyCat.Body.Eyes;

/// <summary>
/// Component capability representing paired eye look and blink control.
/// </summary>
public interface IEyes : IComponent
{
    /// <summary>
    /// Gets or sets the optional target node the eyes should look towards.
    /// </summary>
    Node3D? LookTarget
    {
        get;
        set;
    }

    /// <summary>
    /// Sets the optional target node the eyes should look towards.
    /// </summary>
    void SetLookTarget(Node3D? target);

    /// <summary>
    /// Clears the current directed look target.
    /// </summary>
    void ClearLookTarget();
}
