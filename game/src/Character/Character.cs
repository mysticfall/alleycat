using AlleyCat.Control.Locomotion;
using AlleyCat.Core;
using AlleyCat.Interaction.Hands;
using AlleyCat.Navigation;
using AlleyCat.Rigging;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using AlleyCat.Vision;
using Godot;

namespace AlleyCat.Character;

/// <summary>
/// Godot scene composition hub for a fully embodied humanoid character.
/// </summary>
[GlobalClass]
public partial class Character : CharacterBody3D, ICharacter, IComponentProjectionNotifier
{
    private IComponent[] _components = [];

    /// <inheritdoc />
    [Export]
    public string Id
    {
        get; set;
    } = string.Empty;

    /// <summary>
    /// Gets or sets the player-role-authored locomotion reference. NPC templates leave it unassigned.
    /// </summary>
    [Export]
    public CharacterLocomotion? Locomotion
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the template-authored navigation capability reference.
    /// </summary>
    [Export]
    public NavigationBase? Navigation
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
    /// Gets or sets the optional Hearing-modality-owned speech sense.
    /// </summary>
    [Export]
    public Hearing? Hearing
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
    /// <summary>
    /// Gets or sets template-authored visual cue references in deterministic discovery order.
    /// </summary>
    [Export]
    public VisualCue[] AuthoredVisualCues
    {
        get; set;
    } = [];

    /// <inheritdoc />
    public IReadOnlyList<VisualCue> VisualCues { get; private set; } = [];

    /// <inheritdoc />
    public IReadOnlyList<IComponent> Components => _components;

    /// <inheritdoc />
    public bool HasComponentProjection
    {
        get; private set;
    }

    /// <inheritdoc />
    public event Action? ComponentsRefreshed;

    /// <inheritdoc />
    public override void _Ready()
    {
        // Runtime role installation instantiates imported character roots before the installer copies and rebases the
        // final template-authored capability references.  Keep validation at RefreshComponents() call sites so the
        // installer can finalise explicit wiring before populating the deterministic component cache.
    }

    /// <summary>
    /// Rebuilds the deterministic component cache from explicit template-authored capability references.
    /// The common projection order is stable: eyes, navigation (optional), voice, hearing (optional), left hand, right hand.
    /// </summary>
    public void RefreshComponents()
    {
        List<IComponent> components = [];
        ComposeComponents(components);

        ValidateDistinctReferences(components);
        ValidateVisualCues();

        _components = [.. components];
        VisualCues = Array.AsReadOnly(AuthoredVisualCues.ToArray());
        HasComponentProjection = true;
        ComponentsRefreshed?.Invoke();
    }

    /// <summary>
    /// Composes the stable role-appropriate capability projection.
    /// </summary>
    /// <param name="components">The component projection being built.</param>
    protected virtual void ComposeComponents(List<IComponent> components)
    {
        components.Add(RequireComponentReference(Eyes, nameof(Eyes)));
        if (Navigation is not null)
        {
            components.Add(Navigation);
        }

        components.Add(RequireComponentReference(Voice, nameof(Voice)));
        if (Hearing is not null)
        {
            components.Add(Hearing);
        }

        components.Add(RequireHandReference(LeftHand, LimbSide.Left, nameof(LeftHand)));
        components.Add(RequireHandReference(RightHand, LimbSide.Right, nameof(RightHand)));
    }

    /// <summary>
    /// Resolves a required hand component and validates its authored side.
    /// </summary>
    /// <param name="hand">The authored hand reference.</param>
    /// <param name="expectedSide">The required limb side.</param>
    /// <param name="propertyName">The authoring property name.</param>
    /// <returns>The validated hand component.</returns>
    protected IHand RequireHandReference(HandPoseBehaviour? hand, LimbSide expectedSide, string propertyName)
    {
        HandPoseBehaviour component = RequireComponentReference(hand, propertyName);
        return component.Side != expectedSide
            ? throw new InvalidOperationException(
                $"Character node '{GetPath()}' requires {propertyName} to reference a {expectedSide} hand, but found {component.Side} on {DescribeComponentNode(hand)}.")
            : component;
    }

    /// <summary>
    /// Resolves a required role-authored component reference.
    /// </summary>
    /// <typeparam name="T">The required component node type.</typeparam>
    /// <param name="node">The authored node reference.</param>
    /// <param name="propertyName">The authoring property name.</param>
    /// <returns>The required component.</returns>
    protected T RequireComponentReference<T>(T? node, string propertyName)
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

    private void ValidateVisualCues()
    {
        HashSet<string> IDs = new(StringComparer.Ordinal);
        for (int index = 0; index < AuthoredVisualCues.Length; index++)
        {
            VisualCue cue = AuthoredVisualCues[index]
                ?? throw new InvalidOperationException(
                    $"Character node '{GetPath()}' has a null visual cue reference at index {index}.");

            if (string.IsNullOrWhiteSpace(cue.ID))
            {
                throw new InvalidOperationException(
                    $"Character node '{GetPath()}' visual cue at index {index} requires a non-empty ID.");
            }

            if (!float.IsFinite(cue.Prominence) || cue.Prominence < 0.0f)
            {
                throw new InvalidOperationException(
                    $"Character node '{GetPath()}' visual cue '{cue.ID}' requires finite, non-negative prominence, but found {cue.Prominence}.");
            }

            if (!float.IsFinite(cue.MaxVisibleDistance) || cue.MaxVisibleDistance < 0.0f)
            {
                throw new InvalidOperationException(
                    $"Character node '{GetPath()}' visual cue '{cue.ID}' requires finite, non-negative maximum visible distance, but found {cue.MaxVisibleDistance}.");
            }

            if (!IDs.Add(cue.ID))
            {
                throw new InvalidOperationException(
                    $"Character node '{GetPath()}' has duplicate visual cue ID '{cue.ID}'. IDs must be ordinally unique.");
            }

            IProvidesVisualCues? nearestProvider = FindNearestVisualCueProvider(cue);
            if (!ReferenceEquals(nearestProvider, this))
            {
                string ownership = nearestProvider is null
                    ? "no IProvidesVisualCues ancestor was found"
                    : $"the nearest provider is {DescribeVisualCueProvider(nearestProvider)}";
                throw new InvalidOperationException(
                    $"Character node '{GetPath()}' visual cue '{cue.ID}' at index {index} must have this Character as its nearest {nameof(IProvidesVisualCues)} ancestor, but {ownership}.");
            }
        }
    }

    private static IProvidesVisualCues? FindNearestVisualCueProvider(VisualCue cue)
    {
        for (Node? ancestor = cue.GetParent(); ancestor is not null; ancestor = ancestor.GetParent())
        {
            if (ancestor is IProvidesVisualCues provider)
            {
                return provider;
            }
        }

        return null;
    }

    private static string DescribeVisualCueProvider(IProvidesVisualCues provider)
        => provider is Node node
            ? $"{provider.GetType().FullName ?? provider.GetType().Name} node '{node.Name}' ({node.GetPath()})"
            : provider.GetType().FullName ?? provider.GetType().Name;

    private static string DescribeComponentNode(Node? node)
        => node is null
            ? "null"
            : $"{node.GetType().FullName ?? node.GetType().Name} node '{node.Name}' ({node.GetPath()})";

    private static string DescribeComponent(IComponent component)
        => component is Node node
            ? DescribeComponentNode(node)
            : component.GetType().FullName ?? component.GetType().Name;
}
