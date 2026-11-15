using System.Numerics;

namespace AlleyCat.Contact;

/// <summary>
/// Maintains the raw contact lifecycle stream for one monitor host (INTR-005 TR3): a stable contact id and a
/// Begin/Update/End lifecycle per contact pair — one sweeper's coalesced query shapes against one receiver
/// body (TR18b) — with the objective measurement set derived from successive frames. The stream is purely
/// objective; no classification logic lives here, below the raw/classified seam.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Tick contract.</strong> Each physics tick, the caller observes every detected pair via
/// <see cref="ObservePair" /> and then calls <see cref="CompleteTick" />, which ends pairs not seen during the
/// tick. Observing the same pair twice within one tick is a caller error — sweeper-side coalescing (TR18b)
/// must already have merged the query shapes — and the stream fails fast instead of emitting duplicate
/// updates.
/// </para>
/// <para>
/// <strong>Validation instrumentation.</strong> Emitted events and the <see cref="ActiveContacts" />
/// snapshots expose the full raw state — identity stability, lifecycle ordering, and every carried
/// measurement (TR4). The stream is monitor-internal by default and not publicly subscribable (TR18a default).
/// </para>
/// <para>
/// <strong>Measurement derivation.</strong> The contact normal is normalised; penetration depth and breadth
/// clamp negative solver jitter to zero; relative velocity is the counterpart velocity less the receiver
/// velocity; press rate is the normal closing speed; tangential slip speed is the tangential component of the
/// relative velocity; duration accumulates from observed timestamps and never decreases.
/// </para>
/// </remarks>
public sealed class RawContactStream
{
    private sealed class TrackedContact
    {
        public required ContactId Id
        {
            get; init;
        }

        public required ContactParty Source
        {
            get; init;
        }

        public required ContactParty Receiver
        {
            get; init;
        }

        public required double BeginTimestampSeconds
        {
            get; init;
        }

        public RawContactSample LastSample
        {
            get; set;
        }

        public long LastSeenTick
        {
            get; set;
        }
    }

    private readonly Dictionary<(string SourceKey, string ReceiverKey), TrackedContact> _activePairs = [];

    private readonly Dictionary<ContactId, TrackedContact> _activeById = [];

    private readonly List<KeyValuePair<(string SourceKey, string ReceiverKey), TrackedContact>> _endedScratch =
        [];

    private long _nextContactValue;

    private long _currentTick;

    /// <summary>
    /// Gets the current raw state of every active contact, keyed by stable contact id — the raw layer's
    /// validation instrumentation surface (INTR-005 TR4). Enumerating allocates one snapshot per active
    /// contact; it is a diagnostics surface, not a per-frame hot path.
    /// </summary>
    public IEnumerable<RawContactSnapshot> ActiveContacts
    {
        get
        {
            foreach (KeyValuePair<ContactId, TrackedContact> entry in _activeById)
            {
                TrackedContact tracked = entry.Value;
                yield return new RawContactSnapshot(tracked.Id, tracked.Source, tracked.Receiver, tracked.LastSample);
            }
        }
    }

    /// <summary>Number of contacts currently in the stream — an allocation-free activity probe for hosts.</summary>
    public int ActiveContactCount => _activePairs.Count;

    /// <summary>
    /// Records one physics tick's observation of a contact pair, appending the pair's Begin event on first
    /// sight or its Update event when the contact persists (INTR-005 TR3). All query shapes of the sweeper
    /// against this receiver must already be coalesced into <paramref name="frame" /> (TR18b).
    /// </summary>
    /// <param name="source">The contacting counterpart party.</param>
    /// <param name="receiver">The receiving body-part or surface party.</param>
    /// <param name="frame">The coalesced objective observation of the pair for this tick.</param>
    /// <param name="timestampSeconds">Monotonic physics-tick timestamp in seconds.</param>
    /// <param name="events">Caller-owned list the raw event is appended to; reused across ticks.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when the frame carries non-finite values or a zero contact normal.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the same pair is observed twice within one tick.
    /// </exception>
    public void ObservePair(
        ContactParty source,
        ContactParty receiver,
        in RawContactFrame frame,
        double timestampSeconds,
        List<RawContactEvent> events)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(events);
        ValidateFrame(in frame);

        (string sourceKey, string receiverKey) pairKey = (source.Key, receiver.Key);
        if (!_activePairs.TryGetValue(pairKey, out TrackedContact? tracked))
        {
            tracked = new TrackedContact
            {
                Id = new ContactId(++_nextContactValue),
                Source = source,
                Receiver = receiver,
                BeginTimestampSeconds = timestampSeconds,
                LastSample = CreateSample(in frame, TimeSpan.Zero),
                LastSeenTick = _currentTick,
            };
            _activePairs.Add(pairKey, tracked);
            _activeById.Add(tracked.Id, tracked);
            events.Add(new RawContactEvent(RawContactPhase.Begin, tracked.Id, source, receiver, tracked.LastSample));
            return;
        }

        if (tracked.LastSeenTick == _currentTick)
        {
            throw new InvalidOperationException(
                $"The pair '{source.Key}' → '{receiver.Key}' was observed twice within one physics tick; " +
                "one sweeper's query shapes against one receiver must coalesce into a single frame (INTR-005 TR18b).");
        }

        var duration = TimeSpan.FromSeconds(Math.Max(0d, timestampSeconds - tracked.BeginTimestampSeconds));
        if (duration < tracked.LastSample.Duration)
        {
            duration = tracked.LastSample.Duration;
        }

        tracked.LastSample = CreateSample(in frame, duration);
        tracked.LastSeenTick = _currentTick;
        events.Add(new RawContactEvent(RawContactPhase.Update, tracked.Id, tracked.Source, tracked.Receiver,
            tracked.LastSample));
    }

    /// <summary>
    /// Completes the physics tick: every pair not observed during the tick ends, appending its End event with
    /// the final measurement set (INTR-005 TR3). A pair observed again later is a new contact with a new id.
    /// </summary>
    /// <param name="events">Caller-owned list the End events are appended to; reused across ticks.</param>
    public void CompleteTick(List<RawContactEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        foreach (KeyValuePair<(string SourceKey, string ReceiverKey), TrackedContact> entry in _activePairs)
        {
            if (entry.Value.LastSeenTick != _currentTick)
            {
                _endedScratch.Add(entry);
            }
        }

        foreach (KeyValuePair<(string SourceKey, string ReceiverKey), TrackedContact> entry in _endedScratch)
        {
            TrackedContact tracked = entry.Value;
            _ = _activePairs.Remove(entry.Key);
            _ = _activeById.Remove(tracked.Id);
            events.Add(new RawContactEvent(RawContactPhase.End, tracked.Id, tracked.Source, tracked.Receiver,
                tracked.LastSample));
        }

        _endedScratch.Clear();
        _currentTick++;
    }

    private static RawContactSample CreateSample(in RawContactFrame frame, TimeSpan duration)
    {
        Vector3 normal = ContactMath.Normalised(frame.ContactNormal);
        Vector3 relativeVelocity = frame.CounterpartVelocity - frame.ReceiverVelocity;
        return new RawContactSample(
            ContactPoint: frame.ContactPoint,
            ContactNormal: normal,
            RelativeVelocity: relativeVelocity,
            PenetrationDepth: MathF.Max(0f, frame.PenetrationDepth),
            PressRate: ContactMath.NormalApproachSpeed(relativeVelocity, normal),
            TangentialSlipSpeed: ContactMath.TangentialSpeed(relativeVelocity, normal),
            Duration: duration,
            ContactBreadth: MathF.Max(0f, frame.ContactBreadth));
    }

    private static void ValidateFrame(in RawContactFrame frame)
    {
        if (!IsFinite(frame.ContactPoint) || !IsFinite(frame.CounterpartVelocity) || !IsFinite(frame.ReceiverVelocity))
        {
            throw new ArgumentException("The frame's world-space vectors must be finite.");
        }

        if (!IsFinite(frame.ContactNormal) || frame.ContactNormal == Vector3.Zero)
        {
            throw new ArgumentException("The frame's contact normal must be finite and non-zero.");
        }

        if (!float.IsFinite(frame.PenetrationDepth) || !float.IsFinite(frame.ContactBreadth))
        {
            throw new ArgumentException("The frame's penetration depth and contact breadth must be finite.");
        }
    }

    private static bool IsFinite(Vector3 vector) =>
        float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
}
