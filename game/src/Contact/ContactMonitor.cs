using AlleyCat.Core.Logging;
using Godot;
using Microsoft.Extensions.Logging;
using CharacterHub = AlleyCat.Character.Character;
using NumericsVector3 = System.Numerics.Vector3;

namespace AlleyCat.Contact;

/// <summary>
/// The receiver-side aggregation and emission component (INTR-005 TR5): a monitor hosts the raw contact
/// lifecycle stream for its receiver host — the character or surface whose receiver bodies feed it —
/// contributes the receiver-side velocity sample for every observation (TR18f), transforms contact points into
/// the bone-local space of the contacted body part to refine them against authored
/// <see cref="ContactRegionProfile" /> regions (TR16), hosts the classification engine with one state machine
/// per active contact, and emits classified <see cref="ContactEvent" />s on classification transitions only.
/// Raw observations arrive through the receiver itself: generated character body parts forward what their
/// sweeper delivered through <see cref="IContactObservationReceiver" />, and world receivers feed the monitor
/// they host on their own body, so one delivery path serves character and world receivers alike (TR11).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Tick cycle.</strong> Each physics tick the monitor completes the raw stream's tick — ending pairs no
/// sweeper observed — routes every raw event through the classification engine, and emits one event per
/// classification transition. Deliveries that arrive after the monitor's tick processing land in the next tick's
/// processing, preserving the stream's Begin/Update/End ordering regardless of scene-tree order between
/// sweepers and monitors.
/// </para>
/// <para>
/// <strong>Receiver velocity sampling (TR18f).</strong> Receiver velocity is the per-physics-tick finite
/// difference of the receiver body's world origin, sampled in the monitor at observation time. A body's first
/// observation carries no receiver velocity — there is no earlier sample to difference — so readers of
/// <see cref="GetActiveRawContacts" /> see finite relative velocities from the second observed tick onwards.
/// </para>
/// <para>
/// <strong>Validation instrumentation (TR4).</strong> The monitor exposes the raw stream's active-contact
/// snapshots and a bounded record of recent raw events so tests can assert contact identity stability,
/// lifecycle ordering, and every carried measurement without subscribing below the raw/classified seam.
/// </para>
/// </remarks>
[GlobalClass]
public partial class ContactMonitor : Node
{
    private const int RecentRawEventCapacity = 64;
    private const double TimestampEpsilonSeconds = 1e-6;

    /// <summary>
    /// Host surface's name for receiver attribution — the fallback owner identity for receivers that carry
    /// no owner on the raw-event path, such as world surfaces, which author the name explicitly. When left
    /// blank it resolves from the nearest ancestor <see cref="CharacterHub" /> hub's
    /// <see cref="CharacterHub.Id" />: character hubs host the monitor with blank authored name, and
    /// generated body parts independently carry the same identity wired by their rig, so classified
    /// receiver attribution rides the raw-event path for character parts rather than depending on this
    /// resolution. The blank-name resolution runs once at <c>_Ready</c>, so a character
    /// <see cref="CharacterHub.Id" /> override applied after tree entry is not picked up — the installers
    /// override the identity before adding the character to the tree.
    /// </summary>
    [ExportGroup("Identity")]
    [Export]
    public string OwnerName
    {
        get;
        set;
    } = string.Empty;

    /// <summary>
    /// Authored sub-part regions this monitor refines contacts against (INTR-005 TR16, TR18c); unset contacts
    /// stay at body-part granularity.
    /// </summary>
    [ExportGroup("Regions")]
    [Export]
    public ContactRegionProfile? RegionProfile
    {
        get;
        set;
    }

    /// <summary>
    /// Approach normal speed at or above which a contact begins as Impact/Hit, in m/s; positive
    /// (INTR-005 TR17). Defaults to the shipped code default; scene-authored values win.
    /// </summary>
    [ExportGroup("Classification")]
    [Export]
    public float ImpactApproachSpeedThreshold
    {
        get;
        set;
    } = ContactClassificationPolicy.Default.ImpactApproachSpeedThreshold;

    /// <summary>
    /// Sustained penetration depth that keeps a press condition alive, in m; positive (INTR-005 TR17).
    /// Defaults to the shipped code default; scene-authored values win.
    /// </summary>
    [Export]
    public float PressDepthThreshold
    {
        get;
        set;
    } = ContactClassificationPolicy.Default.PressDepthThreshold;

    /// <summary>
    /// Normal closing speed that counts as increasing pressure, in m/s; positive (INTR-005 TR17).
    /// Defaults to the shipped code default; scene-authored values win.
    /// </summary>
    [Export]
    public float PressRateThreshold
    {
        get;
        set;
    } = ContactClassificationPolicy.Default.PressRateThreshold;

    /// <summary>
    /// Continuous press condition required before Press is confirmed, in seconds; positive (INTR-005 TR17).
    /// Defaults to the shipped code default; scene-authored values win.
    /// </summary>
    [Export]
    public float PressConfirmationSeconds
    {
        get;
        set;
    } = ContactClassificationPolicy.Default.PressConfirmationSeconds;

    /// <summary>
    /// Tangential slip speed at or above which motion counts as a stroke, in m/s; positive (INTR-005 TR17).
    /// Defaults to the shipped code default; scene-authored values win.
    /// </summary>
    [Export]
    public float CaressSlipSpeedThreshold
    {
        get;
        set;
    } = ContactClassificationPolicy.Default.CaressSlipSpeedThreshold;

    /// <summary>
    /// Continuous stroke condition required before an earlier Touch is revised to Caress/Stroke, in seconds;
    /// positive (INTR-005 TR17). Defaults to the shipped code default; scene-authored values win.
    /// </summary>
    [Export]
    public float CaressConfirmationSeconds
    {
        get;
        set;
    } = ContactClassificationPolicy.Default.CaressConfirmationSeconds;

    /// <summary>
    /// Continuous settled time required before contact settles into Rest/Lean, in seconds; positive
    /// (INTR-005 TR17). Defaults to the shipped code default; scene-authored values win.
    /// </summary>
    [Export]
    public float RestDelaySeconds
    {
        get;
        set;
    } = ContactClassificationPolicy.Default.RestDelaySeconds;

    /// <summary>
    /// Relative speed at or below which contact counts as settled, in m/s; positive and strictly below
    /// <see cref="CaressSlipSpeedThreshold" /> (INTR-005 TR17). Defaults to the shipped code default;
    /// scene-authored values win.
    /// </summary>
    [Export]
    public float RestSettledSpeedThreshold
    {
        get;
        set;
    } = ContactClassificationPolicy.Default.RestSettledSpeedThreshold;

    /// <summary>Raised once for every emitted classified contact event; steady contact emits nothing (TR6).</summary>
    public event Action<ContactEvent>? ContactEventEmitted;

    private readonly List<RawContactEvent> _rawEvents = [];

    private readonly Queue<RawContactEvent> _recentRawEvents = [];

    private readonly Dictionary<ulong, ContactParty> _receiverParties = [];

    private readonly Dictionary<string, PhysicsBody3D> _receiverBodiesByPartyKey = [];

    private readonly Dictionary<ulong, ReceiverVelocitySample> _receiverVelocitySamples = [];

    private readonly RawContactStream _stream = new();

    private ContactClassificationEngine _engine = new(ContactClassificationPolicy.Default);

    /// <summary>
    /// Gets the classification policy built from the exported classification tunables (INTR-005 TR17).
    /// Built once at <c>_Ready</c> — the point where malformed authored tunables fail fast — so before the
    /// monitor enters the tree it carries the shipped code defaults.
    /// </summary>
    public ContactClassificationPolicy ClassificationPolicy { get; private set; } = ContactClassificationPolicy.Default;

    private ContactRegionResolver? _regionResolver;

    private double _timestampSeconds;

    private string _receiverKeyPrefix = string.Empty;

    private ILogger<ContactMonitor>? _logger;

    /// <inheritdoc />
    public override void _Ready()
    {
        _logger = GameLoggerResolver.ResolveRequired<ContactMonitor>();
        OwnerName = ResolveOwnerName();
        ClassificationPolicy = ResolveClassificationPolicy();
        _engine = new ContactClassificationEngine(ClassificationPolicy);
        _regionResolver = RegionProfile is null
            ? null
            : new ContactRegionResolver(RegionProfile.QueryRegionDefinitions());
        _receiverKeyPrefix = BuildReceiverKeyPrefix();
        _logger.LogDebug(
            "Contact monitor '{MonitorName}' ready with host owner '{OwnerName}' and {RegionCount} authored regions.",
            Name,
            string.IsNullOrWhiteSpace(OwnerName) ? "unowned" : OwnerName,
            _regionResolver?.RegionCount ?? 0);
    }

    /// <inheritdoc />
    public override void _PhysicsProcess(double delta)
    {
        _timestampSeconds += delta;

        _stream.CompleteTick(_rawEvents);

        foreach (RawContactEvent raw in _rawEvents)
        {
            RecordRecentRawEvent(raw);

            if (_engine.Process(raw) is not { } transition)
            {
                continue;
            }

            ContactEvent contactEvent = BuildContactEvent(raw, transition);
            ContactEventEmitted?.Invoke(contactEvent);
            _logger?.LogDebug(
                "Contact {ContactId} transitioned {PreviousClass} → {Class} on receiver {Receiver}.{Regions}",
                contactEvent.Contact.Value,
                transition.PreviousClass?.ToString() ?? "begin",
                contactEvent.Class,
                contactEvent.Receiver.Descriptor,
                DescribeRegions(contactEvent));
        }

        // Raw events are consumed after processing so sweeper deliveries that arrive later in the same physics
        // frame — after this monitor's tick — survive until the next tick's processing.
        _rawEvents.Clear();

        PruneCachesWhenIdle();
    }

    /// <summary>
    /// Renders the event's region matches as a log-friendly suffix, primary region first; empty when the contact
    /// resolved to no authored region.
    /// </summary>
    private static string DescribeRegions(ContactEvent contactEvent)
    {
        return contactEvent.Regions.Count == 0
            ? string.Empty
            : " [" + string.Join(", ", contactEvent.Regions.Select(region => region.Name)) + "]";
    }

    /// <summary>
    /// Receives one raw observation of a receiver body for the current physics tick — the hub's external
    /// raw-event submission surface: generated body parts forward the sweeper's observation through
    /// <see cref="IContactObservationReceiver" /> (INTR-005 TR5, TR10, TR11), and a world receiver's sweeper
    /// submits directly to the monitor the receiver body hosts. The monitor contributes the receiver-side
    /// velocity sample (TR18f), builds the raw frame, and feeds the raw stream.
    /// </summary>
    /// <param name="source">The contacting counterpart party.</param>
    /// <param name="receiverBody">The receiver-eligible body the sweeper contacted.</param>
    /// <param name="receiverOwnerName">
    /// Owner identity carried on the raw-event path: the character a generated part belongs to. Blank or
    /// <see langword="null" /> falls back to the monitor's host <see cref="OwnerName" /> — the authored world
    /// host's attribution.
    /// </param>
    /// <param name="observation">The sweeper's coalesced observation for this tick.</param>
    internal void ObserveContact(
        ContactParty source,
        PhysicsBody3D receiverBody,
        string? receiverOwnerName,
        in ContactSweepObservation observation)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(receiverBody);

        ContactParty receiverParty = GetOrCreateReceiverParty(receiverBody, receiverOwnerName);
        NumericsVector3 receiverVelocity = SampleReceiverVelocity(receiverBody);

        RawContactFrame frame = new(
            ContactPoint: observation.ContactPoint,
            ContactNormal: observation.ContactNormal,
            CounterpartVelocity: observation.CounterpartVelocity,
            ReceiverVelocity: receiverVelocity,
            PenetrationDepth: observation.PenetrationDepth,
            ContactBreadth: observation.ContactBreadth);

        _stream.ObservePair(source, receiverParty, in frame, _timestampSeconds, _rawEvents);
    }

    /// <summary>
    /// Gets a snapshot of every active raw contact — the raw layer's validation instrumentation surface
    /// (INTR-005 TR4). Allocates; it is a diagnostics surface, not a per-frame hot path.
    /// </summary>
    public IReadOnlyList<RawContactSnapshot> GetActiveRawContacts() => [.. _stream.ActiveContacts];

    /// <summary>
    /// Gets the most recent raw lifecycle events, oldest first, bounded to a fixed record length — validation
    /// instrumentation for lifecycle ordering and carried measurements (INTR-005 TR4). Allocates on access.
    /// </summary>
    public IReadOnlyList<RawContactEvent> GetRecentRawEvents() => [.. _recentRawEvents];

    /// <summary>
    /// Builds the classification policy from the exported classification tunables. Validation is shared with
    /// the <see cref="ContactClassificationPolicy" /> constructor — the single owner of the validation
    /// semantics — so authored values that are non-finite, non-positive, or settle the rest threshold at or
    /// above the caress slip threshold are rejected fail-fast at <c>_Ready</c>, before any classification
    /// decision runs (INTR-005 TR17).
    /// </summary>
    private ContactClassificationPolicy ResolveClassificationPolicy()
        => new(
            ImpactApproachSpeedThreshold,
            PressDepthThreshold,
            PressRateThreshold,
            PressConfirmationSeconds,
            CaressSlipSpeedThreshold,
            CaressConfirmationSeconds,
            RestDelaySeconds,
            RestSettledSpeedThreshold);

    private ContactEvent BuildContactEvent(RawContactEvent raw, ContactClassificationTransition transition)
    {
        NumericsVector3 boneLocalDirection = transition.ContactNormal;
        IReadOnlyList<ContactRegionMatch>? regions = null;

        if (_receiverBodiesByPartyKey.TryGetValue(raw.Receiver.Key, out PhysicsBody3D? body)
            && IsInstanceValid(body))
        {
            Transform3D receiverFrame = ResolveReceiverFrame(body);
            boneLocalDirection = ResolveFrameLocalDirection(receiverFrame, transition.ContactNormal);
            regions = ResolveRegionMatches(body, receiverFrame, raw.Sample.ContactPoint);
        }

        return ContactEvent.FromTransition(transition, regions, boneLocalDirection);
    }

    /// <summary>
    /// Re-expresses a world-space contact normal in the receiver frame — the anchored bone's world frame when
    /// the receiver is bone-anchored, otherwise the receiver body's own frame (TR5, TR8). Falls back to the
    /// world normal when the frame is degenerate.
    /// </summary>
    private static NumericsVector3 ResolveFrameLocalDirection(Transform3D receiverFrame, NumericsVector3 worldNormal)
    {
        Vector3 localDirection = receiverFrame.Basis.Inverse() * ContactVectors.ToGodot(worldNormal);
        localDirection = localDirection.Normalized();

        return localDirection.LengthSquared() > 0.5f ? ContactVectors.ToNumerics(localDirection) : worldNormal;
    }

    /// <summary>
    /// Refines one contact to its authored sub-part regions (INTR-005 TR5, TR16): transforms the world contact
    /// point into the bone-local space of the contacted body part and asks the region resolver which authored
    /// volumes on that bone contain it. Receivers without a bone anchor — or without authored regions — resolve
    /// no matches and stay at body-part granularity.
    /// </summary>
    private IReadOnlyList<ContactRegionMatch>? ResolveRegionMatches(
        PhysicsBody3D body,
        Transform3D receiverFrame,
        NumericsVector3 worldContactPoint)
    {
        if (_regionResolver is null || body is not IBoneAnchoredContactReceiver anchored)
        {
            return null;
        }

        Skeleton3D? skeleton = anchored.ContactSkeleton;
        if (!IsInstanceValid(skeleton)
            || anchored.ContactBoneIndex < 0
            || anchored.ContactBoneIndex >= skeleton.GetBoneCount())
        {
            return null;
        }

        string boneName = skeleton.GetBoneName(anchored.ContactBoneIndex).ToString();
        Vector3 boneLocalPoint = receiverFrame.AffineInverse() * ContactVectors.ToGodot(worldContactPoint);
        return _regionResolver.Resolve(boneName, ContactVectors.ToNumerics(boneLocalPoint));
    }

    private static Transform3D ResolveReceiverFrame(PhysicsBody3D body)
    {
        if (body is IBoneAnchoredContactReceiver anchored)
        {
            Skeleton3D? skeleton = anchored.ContactSkeleton;
            if (IsInstanceValid(skeleton)
                && anchored.ContactBoneIndex >= 0
                && anchored.ContactBoneIndex < skeleton.GetBoneCount())
            {
                return skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(anchored.ContactBoneIndex);
            }
        }

        return body.GlobalTransform;
    }

    /// <summary>
    /// Resolves the receiver party for one receiver body: the display name comes from the body part
    /// (INTR-005 TR8) and the owner identity rides the raw-event path — the character the part reported —
    /// falling back to the monitor's host <see cref="OwnerName" /> for receivers that carry no owner, such
    /// as world surfaces (TR11).
    /// </summary>
    private ContactParty GetOrCreateReceiverParty(PhysicsBody3D body, string? ownerName)
    {
        ulong bodyId = body.GetInstanceId();
        if (_receiverParties.TryGetValue(bodyId, out ContactParty? party))
        {
            return party;
        }

        string displayName = body is IBoneAnchoredContactReceiver anchored
            && !string.IsNullOrWhiteSpace(anchored.ContactDisplayName)
                ? anchored.ContactDisplayName
                : body.Name.ToString();

        string? resolvedOwnerName = !string.IsNullOrWhiteSpace(ownerName)
            ? ownerName
            : string.IsNullOrWhiteSpace(OwnerName) ? null : OwnerName;

        party = new ContactParty(
            $"{_receiverKeyPrefix}#{bodyId}",
            displayName,
            resolvedOwnerName);

        _receiverParties[bodyId] = party;
        _receiverBodiesByPartyKey[party.Key] = body;
        return party;
    }

    /// <summary>
    /// Samples one receiver body's velocity as the finite difference of its world origin since the previous
    /// observation (TR18f): the first observation of a body carries no velocity — there is no earlier sample —
    /// and two observations within the same tick reuse the first sample's velocity so multiple sweepers cannot
    /// zero each other's differences.
    /// </summary>
    private NumericsVector3 SampleReceiverVelocity(PhysicsBody3D body)
    {
        ulong bodyId = body.GetInstanceId();
        Vector3 origin = body.GlobalPosition;
        double timestamp = _timestampSeconds;

        if (_receiverVelocitySamples.TryGetValue(bodyId, out ReceiverVelocitySample previous))
        {
            if (previous.TimestampSeconds == timestamp)
            {
                return ContactVectors.ToNumerics(previous.Velocity);
            }

            double elapsedSeconds = timestamp - previous.TimestampSeconds;
            Vector3 velocity = elapsedSeconds > TimestampEpsilonSeconds
                ? (origin - previous.WorldOrigin) / (float)elapsedSeconds
                : previous.Velocity;
            _receiverVelocitySamples[bodyId] = new ReceiverVelocitySample(timestamp, origin, velocity);
            return ContactVectors.ToNumerics(velocity);
        }

        _receiverVelocitySamples[bodyId] = new ReceiverVelocitySample(timestamp, origin, Vector3.Zero);
        return NumericsVector3.Zero;
    }

    private void RecordRecentRawEvent(RawContactEvent raw)
    {
        _recentRawEvents.Enqueue(raw);
        while (_recentRawEvents.Count > RecentRawEventCapacity)
        {
            _ = _recentRawEvents.Dequeue();
        }
    }

    /// <summary>
    /// Releases per-body caches once no contact is active: receiver parties, body lookups, and velocity samples
    /// are only meaningful within a contact's lifetime, and clearing between contacts bounds memory on rigs that
    /// regenerate proxies.
    /// </summary>
    private void PruneCachesWhenIdle()
    {
        if (_stream.ActiveContactCount > 0)
        {
            return;
        }

        if (_receiverParties.Count > 0)
        {
            _receiverParties.Clear();
            _receiverBodiesByPartyKey.Clear();
        }

        if (_receiverVelocitySamples.Count > 0)
        {
            _receiverVelocitySamples.Clear();
        }
    }

    private string BuildReceiverKeyPrefix()
        => !string.IsNullOrWhiteSpace(OwnerName)
            ? OwnerName
            : !string.IsNullOrWhiteSpace(Name.ToString()) ? Name.ToString() : "host";

    /// <summary>
    /// Resolves the host owner name for receiver attribution: the exported name when authored, otherwise the
    /// nearest ancestor <see cref="CharacterHub" /> hub's identity, so template-installed characters
    /// attribute their received contacts to themselves. Hosts on no character hub stay unowned.
    /// </summary>
    private string ResolveOwnerName()
    {
        if (!string.IsNullOrWhiteSpace(OwnerName))
        {
            return OwnerName;
        }

        for (Node? node = GetParent(); node is not null; node = node.GetParent())
        {
            if (node is CharacterHub character && !string.IsNullOrWhiteSpace(character.Id))
            {
                return character.Id;
            }
        }

        return OwnerName;
    }

    private readonly record struct ReceiverVelocitySample(
        double TimestampSeconds,
        Vector3 WorldOrigin,
        Vector3 Velocity);
}
