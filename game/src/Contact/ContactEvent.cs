using System.Numerics;

namespace AlleyCat.Contact;

/// <summary>
/// The immutable, self-describing classified contact contract consumers subscribe to (INTR-005 TR8, TR9): the
/// class; a class-appropriate intensity; the receiver body part with resolved region slots; the counterpart
/// descriptor with owner attribution; the contact direction as a receiver-relative normal in the bone-local
/// space of the contacted body part; the duration; and an optional template-authored natural-language
/// description that is <see langword="null" /> unless authored. Events carry payload data only — never
/// classification logic — and stay character-relative so a future percept bridge can wrap them without
/// re-deriving context.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Immutability.</strong> Every property is get-only and the region list is defensively copied at
/// construction; nothing on an event can change after delivery.
/// </para>
/// <para>
/// <strong>Intensity units.</strong> Impact/Hit and Touch report the approach normal speed at Begin in m/s;
/// Press and Rest/Lean report the penetration depth in m; Caress/Stroke reports the tangential slip speed in
/// m/s. World-space data remains available from the raw layer.
/// </para>
/// </remarks>
public sealed record ContactEvent
{
    private readonly ContactRegionMatch[] _regions;

    /// <summary>
    /// Creates a validated, immutable classified contact event.
    /// </summary>
    /// <param name="contact">Stable identity of the classified contact pair — correlates revisions.</param>
    /// <param name="contactClass">The confirmed contact class.</param>
    /// <param name="previousClass">
    /// The class the contact held before this event; <see langword="null" /> on the entry transition at
    /// contact Begin.
    /// </param>
    /// <param name="intensity">Class-appropriate intensity; finite and non-negative.</param>
    /// <param name="receiver">The receiving body-part or surface party.</param>
    /// <param name="counterpart">The contacting counterpart party with owner attribution.</param>
    /// <param name="regions">
    /// Resolved sub-part region slots, highest-priority primary marked; empty when the contact matched no
    /// authored region. Defensively copied.
    /// </param>
    /// <param name="direction">
    /// Receiver-relative contact normal in the bone-local space of the contacted body part; finite.
    /// </param>
    /// <param name="duration">Contact duration at emission; non-negative.</param>
    /// <param name="description">
    /// Template-authored natural-language description, carried verbatim; <see langword="null" /> unless
    /// authored — blank is rejected because absence is <see langword="null" />.
    /// </param>
    public ContactEvent(
        ContactId contact,
        ContactClass contactClass,
        ContactClass? previousClass,
        float intensity,
        ContactParty receiver,
        ContactParty counterpart,
        IEnumerable<ContactRegionMatch>? regions,
        Vector3 direction,
        TimeSpan duration,
        string? description)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(counterpart);
        if (!Enum.IsDefined(contactClass))
        {
            throw new ArgumentOutOfRangeException(nameof(contactClass), contactClass, "Unknown contact class.");
        }

        if (previousClass is { } previous && (!Enum.IsDefined(previous) || previous == contactClass))
        {
            throw new ArgumentException(
                "The previous class must be a defined class different from the event's class; entry events use " +
                "null.", nameof(previousClass));
        }

        if (!float.IsFinite(intensity) || intensity < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(intensity), intensity,
                "The intensity must be finite and non-negative.");
        }

        if (!IsFinite(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction,
                "The direction must be finite in every component.");
        }

        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "The duration must be non-negative.");
        }

        if (description is not null && string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException(
                "Authored descriptions are non-blank and carried verbatim; absence is null.", nameof(description));
        }

        _regions = regions is null ? [] : CopyRegions(regions);
        if (_regions.Length > 0 && _regions.Count(static region => region.IsPrimary) != 1)
        {
            throw new ArgumentException(
                "A non-empty region list must mark exactly one primary region (INTR-005 TR18d).", nameof(regions));
        }

        Contact = contact;
        Class = contactClass;
        PreviousClass = previousClass;
        Intensity = intensity;
        Receiver = receiver;
        Counterpart = counterpart;
        Direction = direction;
        Duration = duration;
        Description = description;
    }

    /// <summary>Stable identity of the classified contact pair — correlates revisions of one contact.</summary>
    public ContactId Contact
    {
        get;
    }

    /// <summary>The confirmed contact class.</summary>
    public ContactClass Class
    {
        get;
    }

    /// <summary>
    /// The class the contact held before this event; <see langword="null" /> on the entry transition at
    /// contact Begin. A Touch→Caress/Stroke revision reports <see cref="ContactClass.Touch" /> here.
    /// </summary>
    public ContactClass? PreviousClass
    {
        get;
    }

    /// <summary>
    /// Class-appropriate intensity: approach normal speed at Begin in m/s for Impact/Hit and Touch,
    /// penetration depth in m for Press and Rest/Lean, tangential slip speed in m/s for Caress/Stroke.
    /// </summary>
    public float Intensity
    {
        get;
    }

    /// <summary>The receiving body-part or surface party.</summary>
    public ContactParty Receiver
    {
        get;
    }

    /// <summary>The contacting counterpart party, with owner attribution via <see cref="ContactParty.Descriptor" />.</summary>
    public ContactParty Counterpart
    {
        get;
    }

    /// <summary>
    /// Resolved sub-part region slots with the primary marked; empty when the contact matched no authored
    /// region and stayed at body-part granularity (INTR-005 TR16, TR18d).
    /// </summary>
    public IReadOnlyList<ContactRegionMatch> Regions => _regions;

    /// <summary>
    /// Receiver-relative contact normal expressed in the bone-local space of the contacted body part; the
    /// world-space normal remains available from the raw layer (INTR-005 TR8).
    /// </summary>
    public Vector3 Direction
    {
        get;
    }

    /// <summary>Contact duration at emission.</summary>
    public TimeSpan Duration
    {
        get;
    }

    /// <summary>
    /// Template-authored natural-language description carried verbatim; <see langword="null" /> unless
    /// authored — never synthesised (INTR-005 User Requirement 7).
    /// </summary>
    public string? Description
    {
        get;
    }

    /// <summary>
    /// Builds a delivered event from a classification transition plus the monitor's payload slots: resolved
    /// regions, the bone-local direction, and any authored description.
    /// </summary>
    /// <param name="transition">The emitted classification transition.</param>
    /// <param name="regions">Resolved region slots; empty when none matched.</param>
    /// <param name="boneLocalDirection">The transition's world normal re-expressed in bone-local space.</param>
    /// <param name="description">Optional authored description; <see langword="null" /> by default.</param>
    public static ContactEvent FromTransition(
        ContactClassificationTransition transition,
        IReadOnlyList<ContactRegionMatch>? regions,
        Vector3 boneLocalDirection,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(transition);
        return new ContactEvent(
            transition.Contact,
            transition.Class,
            transition.PreviousClass,
            transition.Intensity,
            transition.Receiver,
            transition.Source,
            regions,
            boneLocalDirection,
            transition.Duration,
            description);
    }

    /// <summary>Value equality across all payload fields, regions compared element-wise.</summary>
    public bool Equals(ContactEvent? other) =>
        other is not null
        && other.Contact == Contact
        && other.Class == Class
        && other.PreviousClass == PreviousClass
        && other.Intensity.Equals(Intensity)
        && Equals(other.Receiver, Receiver)
        && Equals(other.Counterpart, Counterpart)
        && other._regions.AsSpan().SequenceEqual(_regions)
        && other.Direction == Direction
        && other.Duration == Duration
        && other.Description == Description;

    /// <summary>Hash code consistent with <see cref="Equals(ContactEvent)" />.</summary>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Contact);
        hash.Add(Class);
        hash.Add(PreviousClass);
        hash.Add(Intensity);
        hash.Add(Receiver);
        hash.Add(Counterpart);
        foreach (ContactRegionMatch region in _regions)
        {
            hash.Add(region);
        }

        hash.Add(Direction);
        hash.Add(Duration);
        hash.Add(Description);
        return hash.ToHashCode();
    }

    private static ContactRegionMatch[] CopyRegions(IEnumerable<ContactRegionMatch> regions)
    {
        ContactRegionMatch[] copy = [.. regions];
        foreach (ContactRegionMatch region in copy)
        {
            if (region is null)
            {
                throw new ArgumentException("Region slots must not contain null entries.", nameof(regions));
            }
        }

        return copy;
    }

    private static bool IsFinite(Vector3 vector) =>
        float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
}
