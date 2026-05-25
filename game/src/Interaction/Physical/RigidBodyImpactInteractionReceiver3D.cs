using AlleyCat.Body;
using Godot;

namespace AlleyCat.Interaction.Physical;

/// <summary>
/// Impact receiver that turns accepted impact interactions into impulses on a rigid body.
/// </summary>
[GlobalClass]
public partial class RigidBodyImpactInteractionReceiver3D : Node, IPhysicalInteractionReceiver
{
    private readonly SortedSet<string> _tags = new(StringComparer.Ordinal);
    private RigidBody3D? _resolvedBody;

    /// <summary>
    /// Emitted after this receiver accepts and applies an impact interaction.
    /// </summary>
    /// <param name="receipt">Variant-compatible receipt preserving the delivered interaction.</param>
    /// <param name="tags">Receiver-owned metadata tags associated with this receiver.</param>
    [Signal]
    public delegate void PhysicalInteractionReceivedEventHandler(PhysicalInteractionReceipt receipt, string[] tags);

    /// <summary>
    /// Gets or sets the rigid body receiving impulses. Defaults to this node's parent rigid body.
    /// </summary>
    [Export]
    public RigidBody3D? Body
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the multiplier applied to source velocity to create an impulse.
    /// </summary>
    [Export]
    public float ImpulseScale { get; set; } = 0.08f;

    /// <summary>
    /// Gets or sets the minimum source speed required before an impulse is applied.
    /// </summary>
    [Export]
    public float MinimumSpeedMetresPerSecond { get; set; } = 0.01f;

    /// <summary>
    /// Gets or sets authored receiver metadata tags for discovery, filtering, or debugging.
    /// </summary>
    [Export]
    public string[] AuthoredTags { get; set; } = ["ImpactReceiver"];

    /// <summary>
    /// Gets receiver-owned metadata tags.
    /// </summary>
    public IReadOnlySet<string> Tags => _tags;

    /// <summary>
    /// Gets the most recent interaction accepted by this receiver for tests and lightweight diagnostics.
    /// </summary>
    public IImpactPhysicalInteraction? LastImpactInteraction
    {
        get; private set;
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        _resolvedBody = ResolveBody();
        InitialiseTagsFromAuthoredTags();
    }

    /// <summary>
    /// Interprets an impact source and applies the corresponding impulse to the configured rigid body.
    /// </summary>
    /// <param name="source">The source carrying interaction properties to interpret.</param>
    /// <returns>The created interaction, or <see langword="null"/> when the source is unsupported.</returns>
    public IPhysicalInteraction? InteractWith(IPhysicalInteractionSource source)
        => InteractWith(source, ResolveContactPoint());

    /// <summary>
    /// Interprets an impact source using an event-specific contact point supplied by the collision path.
    /// </summary>
    /// <param name="source">The source carrying interaction properties to interpret.</param>
    /// <param name="contactPoint">The world-space contact point for this interaction event.</param>
    /// <returns>The created interaction, or <see langword="null"/> when the source is unsupported.</returns>
    public IPhysicalInteraction? InteractWith(IPhysicalInteractionSource source, Vector3 contactPoint)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source is not IImpactPhysicalInteractionSource impactSource)
        {
            return null;
        }

        ImpactPhysicalInteraction interaction = new(impactSource, contactPoint, impactSource.Velocity);
        LastImpactInteraction = interaction;

        RigidBody3D body = _resolvedBody ??= ResolveBody();
        ApplyImpactImpulse(body, interaction);

        string[] emittedTags = _tags.Count > 0 ? [.. _tags] : [];
        _ = EmitSignal(SignalName.PhysicalInteractionReceived, new PhysicalInteractionReceipt(interaction), emittedTags);

        return interaction;
    }

    private RigidBody3D ResolveBody()
        => Body
            ?? GetParent<RigidBody3D>()
            ?? throw new InvalidOperationException(
                $"{nameof(RigidBodyImpactInteractionReceiver3D)} requires an exported {nameof(Body)} or parent {nameof(RigidBody3D)}.");

    private Vector3 ResolveContactPoint()
        => (_resolvedBody ??= ResolveBody()).GlobalPosition;

    private void ApplyImpactImpulse(RigidBody3D body, IImpactPhysicalInteraction interaction)
    {
        Vector3 velocity = interaction.Velocity;
        if (velocity.Length() < MinimumSpeedMetresPerSecond)
        {
            return;
        }

        body.ApplyCentralImpulse(velocity * ImpulseScale);
    }

    private void InitialiseTagsFromAuthoredTags()
    {
        _tags.Clear();
        foreach (string tag in AuthoredTags)
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                _ = _tags.Add(tag);
            }
        }
    }
}
