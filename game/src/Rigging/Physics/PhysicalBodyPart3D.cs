using AlleyCat.Contact;
using AlleyCat.Interaction.Physical;
using Godot;

namespace AlleyCat.Rigging.Physics;

/// <summary>
/// Generated body-part proxy that can receive physical interactions without applying reactions.
/// </summary>
[GlobalClass]
public partial class PhysicalBodyPart3D : AnimatableBody3D, IPhysicalInteractionReceiver, IBoneAnchoredContactReceiver, IContactObservationReceiver
{
    private readonly SortedSet<string> _tags = new(StringComparer.Ordinal);

    /// <summary>
    /// Emitted once whenever this generated body-part proxy receives a physical interaction.
    /// </summary>
    /// <param name="receipt">Variant-compatible receipt that preserves the delivered interaction value.</param>
    /// <param name="boneIndex">The skeleton bone index associated with this receiver proxy.</param>
    /// <param name="tags">Receiver-owned metadata tags associated with this proxy.</param>
    [Signal]
    public delegate void PhysicalInteractionReceivedEventHandler(
        PhysicalInteractionReceipt receipt,
        int boneIndex,
        string[] tags);

    /// <summary>
    /// Gets or sets the skeleton bone name that owns this receiver proxy.
    /// </summary>
    public StringName BoneName { get; set; } = new(string.Empty);

    /// <summary>
    /// Gets or sets the skeleton bone index that owns this receiver proxy.
    /// </summary>
    public int BoneIndex { get; set; } = -1;

    /// <summary>
    /// Gets or sets optional Godot-authored receiver metadata tags for collision-side discovery or debugging.
    /// Authored tags describe the receiver and must not be interpreted as interaction-side target semantics.
    /// </summary>
    [Export]
    public string[] AuthoredTags { get; set; } = [];

    /// <summary>
    /// Gets receiver-owned metadata tags for collision-side receiver discovery or debugging.
    /// Tags describe the receiver and must not be interpreted as interaction-side target semantics.
    /// </summary>
    public IReadOnlySet<string> Tags
    {
        get
        {
            if (_tags.Count == 0 && AuthoredTags.Length > 0)
            {
                InitialiseTagsFromAuthoredTags();
            }

            return _tags;
        }
    }

    /// <summary>
    /// Gets or sets the authored collider shape identifier that produced this proxy.
    /// </summary>
    [Export]
    public string SourceShapeId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the rig that generated this receiver proxy.
    /// </summary>
    [Export]
    public DynamicPhysicalRig? OwningRig
    {
        get; set;
    }

    /// <inheritdoc />
    public Skeleton3D? ContactSkeleton
        => OwningRig is not null && IsInstanceValid(OwningRig) ? OwningRig.TargetSkeleton : null;

    /// <summary>
    /// Gets or sets the character's <see cref="ContactMonitor" /> classifier hub this part forwards raw
    /// contact observations to. Wired once by the owning rig when this part is generated — any placement
    /// of the hub under the character root resolves — so raw delivery never searches for a hub at contact
    /// time (INTR-005 TR5, TR10, TR11).
    /// </summary>
    public ContactMonitor? ContactHub
    {
        get;
        internal set;
    }

    /// <summary>
    /// Gets or sets the owning character's name carried on every raw contact event this part forwards, so
    /// receiver owner attribution rides the raw-event path (INTR-005 TR11); empty when the owning rig
    /// resolved no character identity, in which case the hub falls back to its own host attribution.
    /// </summary>
    public string ContactOwnerName
    {
        get;
        internal set;
    } = string.Empty;

    /// <inheritdoc />
    public int ContactBoneIndex => BoneIndex;

    /// <inheritdoc />
    public string ContactDisplayName
        => !string.IsNullOrWhiteSpace(BoneName) ? BoneName.ToString() : Name;

    /// <summary>
    /// Receives one sweeper's coalesced raw observation of this body part and forwards it — with this
    /// part's owner identity — to the character hub wired at generation; observations on a part whose hub
    /// is not wired or has been freed are dropped.
    /// </summary>
    /// <param name="source">The contacting counterpart party.</param>
    /// <param name="observation">The sweeper's coalesced observation for this tick.</param>
    public void ObserveContact(ContactParty source, in ContactSweepObservation observation)
    {
        ContactMonitor? hub = ContactHub;
        if (hub is null || !IsInstanceValid(hub))
        {
            return;
        }

        hub.ObserveContact(source, this, ContactOwnerName, in observation);
    }

    /// <summary>
    /// Interprets a physical interaction source and generates interaction data when applicable.
    /// </summary>
    /// <param name="source">The source carrying interaction properties to interpret.</param>
    /// <returns>The created interaction, or <see langword="null"/> when the source is unsupported.</returns>
    public IPhysicalInteraction? InteractWith(IPhysicalInteractionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source is not IImpactPhysicalInteractionSource impactSource)
        {
            return null;
        }

        ImpactPhysicalInteraction interaction = new(impactSource, GlobalPosition, impactSource.Velocity);

        IReadOnlySet<string> tags = Tags;
        string[] emittedTags = tags.Count > 0 ? [.. tags] : [];

        _ = EmitSignal(SignalName.PhysicalInteractionReceived, new PhysicalInteractionReceipt(interaction), BoneIndex, emittedTags);
        return interaction;
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
