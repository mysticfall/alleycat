using AlleyCat.Core;
using Godot;

namespace AlleyCat.Body.Hands;

/// <summary>
/// Scene holder for left/right hand components.
/// </summary>
[GlobalClass]
public partial class HandComponentHolder : Node, IHasHands
{
    private IComponent[] _components = [];

    /// <inheritdoc />
    public IReadOnlyList<IComponent> Components => _components;

    /// <inheritdoc />
    public override void _Ready() => RefreshComponents();

    /// <summary>
    /// Refreshes the deterministic child component cache.
    /// </summary>
    public void RefreshComponents()
    {
        var components = new List<IComponent>();
        foreach (Node child in GetChildren())
        {
            if (child is IComponent component)
            {
                components.Add(component);
            }
        }

        _components = [.. components];
    }
}
