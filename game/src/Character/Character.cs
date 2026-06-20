using AlleyCat.Body.Eyes;
using AlleyCat.Body.Hands;
using AlleyCat.Body.Voice;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core;
using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.Character;

/// <summary>
/// Godot scene composition hub for a fully embodied humanoid character.
/// </summary>
[GlobalClass]
public partial class Character : CharacterBody3D, ICharacter
{
    private IComponent[] _components = [];

    /// <summary>
    /// Gets or sets the template-authored locomotion capability reference.
    /// </summary>
    [Export]
    public CharacterLocomotion? Locomotion
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the template-authored eyes capability reference.
    /// </summary>
    [Export]
    public EyesBehaviour? Eyes
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the template-authored voice capability reference.
    /// </summary>
    [Export]
    public Voice? Voice
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the template-authored left hand capability reference.
    /// </summary>
    [Export]
    public HandPoseBehaviour? LeftHand
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the template-authored right hand capability reference.
    /// </summary>
    [Export]
    public HandPoseBehaviour? RightHand
    {
        get; set;
    }

    /// <inheritdoc />
    public IReadOnlyList<IComponent> Components => _components;

    /// <inheritdoc />
    public override void _Ready()
    {
        // Runtime role installation instantiates imported character roots before the installer copies and rebases the
        // final template-authored capability references.  Keep validation at RefreshComponents() call sites so the
        // installer can finalise explicit wiring before populating the deterministic component cache.
    }

    /// <summary>
    /// Rebuilds the deterministic component cache from explicit template-authored capability references.
    /// The projection order is stable: locomotion, eyes, voice, left hand, right hand.
    /// </summary>
    public void RefreshComponents()
    {
        IComponent[] components =
        [
            RequireComponentReference(Locomotion, nameof(Locomotion)),
            RequireComponentReference(Eyes, nameof(Eyes)),
            RequireComponentReference(Voice, nameof(Voice)),
            RequireHandReference(LeftHand, LimbSide.Left, nameof(LeftHand)),
            RequireHandReference(RightHand, LimbSide.Right, nameof(RightHand)),
        ];

        ValidateDistinctReferences(components);

        _components = components;
    }

    private IHand RequireHandReference(HandPoseBehaviour? hand, LimbSide expectedSide, string propertyName)
    {
        HandPoseBehaviour component = RequireComponentReference(hand, propertyName);
        return component.Side != expectedSide
            ? throw new InvalidOperationException(
                $"Character node '{GetPath()}' requires {propertyName} to reference a {expectedSide} hand, but found {component.Side} on {DescribeComponentNode(hand)}.")
            : component;
    }

    private T RequireComponentReference<T>(T? node, string propertyName)
        where T : Node, IComponent
        => node
            ?? throw new InvalidOperationException(
                $"Character node '{GetPath()}' requires {propertyName} to reference a non-null {typeof(T).FullName} component node, but found null.");

    private void ValidateDistinctReferences(IReadOnlyList<IComponent> components)
    {
        HashSet<IComponent> seen = [];
        foreach (IComponent component in components)
        {
            if (!seen.Add(component))
            {
                throw new InvalidOperationException(
                    $"Character node '{GetPath()}' has duplicate capability reference {DescribeComponent(component)}. Each required capability must reference a distinct authored component node.");
            }
        }
    }

    private static string DescribeComponentNode(Node? node)
        => node is null
            ? "null"
            : $"{node.GetType().FullName ?? node.GetType().Name} node '{node.Name}' ({node.GetPath()})";

    private static string DescribeComponent(IComponent component)
        => component is Node node
            ? DescribeComponentNode(node)
            : component.GetType().FullName ?? component.GetType().Name;
}
