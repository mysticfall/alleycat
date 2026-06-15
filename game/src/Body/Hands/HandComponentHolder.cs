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
    private bool _componentsDirty = true;

    /// <inheritdoc />
    public IReadOnlyList<IComponent> Components => _components;

    /// <inheritdoc />
    public override void _EnterTree()
    {
        ChildEnteredTree += OnChildEnteredTree;
        ChildExitingTree += OnChildExitingTree;
    }

    /// <inheritdoc />
    public override void _Ready() => RefreshComponents();

    /// <inheritdoc />
    public override void _ExitTree()
    {
        ChildEnteredTree -= OnChildEnteredTree;
        ChildExitingTree -= OnChildExitingTree;
    }

    /// <inheritdoc />
    public bool TryGetHand(LimbSide side, out IHand? hand)
    {
        RefreshComponentsIfDirty();

        IHand? match = null;
        int count = 0;

        foreach (IComponent component in _components)
        {
            if (component is not IHand candidate || candidate.Side != side)
            {
                continue;
            }

            count++;
            if (count == 1)
            {
                match = candidate;
            }
            else
            {
                hand = null;
                return false;
            }
        }

        hand = count == 1 ? match : null;

        return count == 1;
    }

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
        _componentsDirty = false;
    }

    private void OnChildEnteredTree(Node child)
    {
        _ = child;
        _componentsDirty = true;
    }

    private void OnChildExitingTree(Node child)
    {
        _ = child;
        _componentsDirty = true;
    }

    private void RefreshComponentsIfDirty()
    {
        if (_componentsDirty)
        {
            RefreshComponents();
        }
    }
}
