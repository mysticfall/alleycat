using System.Numerics;
using AlleyCat.Contact;
using Xunit;

namespace AlleyCat.Tests.Contact;

/// <summary>
/// Unit coverage of the raw contact lifecycle stream (INTR-005 TR3, TR4, TR18a, TR18b): stable contact ids,
/// Begin/Update/End ordering, every carried measurement, duration accumulation, frame validation, and the
/// validation-instrumentation snapshots.
/// </summary>
public sealed class RawContactStreamTests
{
    /// <summary>A pair's lifecycle is exactly Begin, zero or more Updates, then End, all under one stable id.</summary>
    [Fact]
    public void Lifecycle_FollowsBeginUpdateEndOrderUnderOneStableId()
    {
        RawContactStream stream = new();
        List<RawContactEvent> events = [];

        stream.ObservePair(ContactScenarioScript.SourceHand, ContactScenarioScript.ReceiverHead,
            ContactScenarioScript.Frame(), 0d, events);
        stream.CompleteTick(events);

        stream.ObservePair(ContactScenarioScript.SourceHand, ContactScenarioScript.ReceiverHead,
            ContactScenarioScript.Frame(), ContactScenarioScript.TickSeconds, events);
        stream.CompleteTick(events);

        // The pair is gone this tick, so completing the tick ends it.
        stream.CompleteTick(events);

        Assert.Equal(
        [
            RawContactPhase.Begin,
            RawContactPhase.Update,
            RawContactPhase.End,
        ], events.Select(raw => raw.Phase));
        // One stable id spans the whole lifecycle.
        ContactId lifecycleId = events[0].Contact;
        Assert.All(events, raw => Assert.Equal(lifecycleId, raw.Contact));
        Assert.Equal(TimeSpan.Zero, events[0].Sample.Duration);
        Assert.Equal(TimeSpan.FromSeconds(ContactScenarioScript.TickSeconds), events[1].Sample.Duration);
        Assert.Equal(events[1].Sample, events[2].Sample);
    }

    /// <summary>A pair observed again after its End is a new contact with a new id (identity stability per TR3).</summary>
    [Fact]
    public void Recontact_AfterEndBeginsANewLifecycleWithANewId()
    {
        RawContactStream stream = new();
        List<RawContactEvent> events = [];

        stream.ObservePair(ContactScenarioScript.SourceHand, ContactScenarioScript.ReceiverHead,
            ContactScenarioScript.Frame(), 0d, events);
        stream.CompleteTick(events);
        stream.CompleteTick(events);

        stream.ObservePair(ContactScenarioScript.SourceHand, ContactScenarioScript.ReceiverHead,
            ContactScenarioScript.Frame(), 1d, events);

        Assert.Equal(RawContactPhase.Begin, events[0].Phase);
        Assert.Equal(RawContactPhase.End, events[1].Phase);
        Assert.Equal(RawContactPhase.Begin, events[2].Phase);
        // Begin and End share the lifecycle's stable id; the recontacted pair receives a fresh one.
        Assert.Equal(events[0].Contact, events[1].Contact);
        Assert.NotEqual(events[0].Contact, events[2].Contact);
    }

    /// <summary>Every TR3 measurement derives from the observed frame: point, unit normal, relative velocity,
    /// depth, press rate, slip speed, duration, and breadth.</summary>
    [Fact]
    public void ObservePair_DerivesEveryCarriedMeasurement()
    {
        RawContactStream stream = new();
        List<RawContactEvent> events = [];

        RawContactFrame frame = new(
            ContactPoint: new Vector3(0.2f, 1.5f, 0.3f),
            ContactNormal: new Vector3(0f, 0f, 3f),
            CounterpartVelocity: new Vector3(1.0f, 0f, -2f),
            ReceiverVelocity: new Vector3(0.5f, 0f, 0f),
            PenetrationDepth: 0.01f,
            ContactBreadth: 0.03f);
        stream.ObservePair(ContactScenarioScript.SourceHand, ContactScenarioScript.ReceiverHead, in frame, 8d,
            events);

        RawContactSample sample = events[0].Sample;
        Assert.Equal(new Vector3(0.2f, 1.5f, 0.3f), sample.ContactPoint);
        Assert.Equal(Vector3.UnitZ, sample.ContactNormal);
        Assert.Equal(new Vector3(0.5f, 0f, -2f), sample.RelativeVelocity);
        Assert.Equal(0.01f, sample.PenetrationDepth, 5);
        Assert.Equal(2.0f, sample.PressRate, 5);
        Assert.Equal(0.5f, sample.TangentialSlipSpeed, 5);
        Assert.Equal(TimeSpan.Zero, sample.Duration);
        Assert.Equal(0.03f, sample.ContactBreadth, 5);
    }

    /// <summary>Receiver-side motion cancels out of the relative velocity the measurements derive from (TR18f).</summary>
    [Fact]
    public void ObservePair_SubtractsReceiverVelocityFromTheCounterpartVelocity()
    {
        RawContactStream stream = new();
        List<RawContactEvent> events = [];

        // The counterpart slides right at 1.0 m/s while the receiver surface itself moves right at 0.5 m/s,
        // so the contact slips at 0.5 m/s and builds no normal pressure.
        RawContactFrame frame = new(
            ContactPoint: new Vector3(0.2f, 1.5f, 0.3f),
            ContactNormal: ContactScenarioScript.OutwardNormal,
            CounterpartVelocity: new Vector3(1.0f, 0f, 0f),
            ReceiverVelocity: new Vector3(0.5f, 0f, 0f),
            PenetrationDepth: 0.001f,
            ContactBreadth: 0.02f);
        stream.ObservePair(ContactScenarioScript.SourceHand, ContactScenarioScript.ReceiverHead, in frame, 0d,
            events);

        Assert.Equal(new Vector3(0.5f, 0f, 0f), events[0].Sample.RelativeVelocity);
        Assert.Equal(0.5f, events[0].Sample.TangentialSlipSpeed, 5);
        Assert.Equal(0f, events[0].Sample.PressRate, 5);
    }

    /// <summary>Negative solver jitter clamps to zero rather than reporting negative depth.</summary>
    [Fact]
    public void ObservePair_ClampsNegativePenetrationJitterToZero()
    {
        RawContactStream stream = new();
        List<RawContactEvent> events = [];

        RawContactFrame frame = ContactScenarioScript.Frame(penetrationDepth: -0.005f);
        stream.ObservePair(ContactScenarioScript.SourceHand, ContactScenarioScript.ReceiverHead, in frame, 0d,
            events);

        Assert.Equal(0f, events[0].Sample.PenetrationDepth, 5);
    }

    /// <summary>Duration accumulates from observed timestamps and never decreases within a lifecycle.</summary>
    [Fact]
    public void ObservePair_AccumulatesDurationAndNeverDecreasesIt()
    {
        RawContactStream stream = new();
        List<RawContactEvent> events = [];
        ContactParty source = ContactScenarioScript.SourceHand;
        ContactParty receiver = ContactScenarioScript.ReceiverHead;

        stream.ObservePair(source, receiver, ContactScenarioScript.Frame(), 8d, events);
        stream.CompleteTick(events);
        stream.ObservePair(source, receiver, ContactScenarioScript.Frame(), 10d, events);
        stream.CompleteTick(events);
        // A regressing timestamp cannot shrink the accumulated duration.
        stream.ObservePair(source, receiver, ContactScenarioScript.Frame(), 9.5d, events);

        Assert.Equal(TimeSpan.Zero, events[0].Sample.Duration);
        Assert.Equal(TimeSpan.FromSeconds(2d), events[1].Sample.Duration);
        Assert.Equal(TimeSpan.FromSeconds(2d), events[2].Sample.Duration);
    }

    /// <summary>Observing one pair twice in a tick fails fast: sweeper-side coalescing must merge shapes (TR18b).</summary>
    [Fact]
    public void ObservePair_RejectsADuplicateObservationWithinOneTick()
    {
        RawContactStream stream = new();
        List<RawContactEvent> events = [];

        stream.ObservePair(ContactScenarioScript.SourceHand, ContactScenarioScript.ReceiverHead,
            ContactScenarioScript.Frame(), 0d, events);

        _ = Assert.Throws<InvalidOperationException>(() => stream.ObservePair(
            ContactScenarioScript.SourceHand, ContactScenarioScript.ReceiverHead,
            ContactScenarioScript.Frame(), 0.01d, events));
    }

    /// <summary>Frames carrying non-finite values or a zero normal are rejected before use (TR3 frame validation).</summary>
    [Fact]
    public void ObservePair_RejectsMalformedFrames()
    {
        RawContactStream stream = new();
        List<RawContactEvent> events = [];
        ContactParty source = ContactScenarioScript.SourceHand;
        ContactParty receiver = ContactScenarioScript.ReceiverHead;

        RawContactFrame nonFiniteVelocity = ContactScenarioScript.Frame();
        nonFiniteVelocity = nonFiniteVelocity with
        {
            CounterpartVelocity = new Vector3(float.NaN, 0f, 0f)
        };
        _ = Assert.Throws<ArgumentException>(
            () => stream.ObservePair(source, receiver, in nonFiniteVelocity, 0d, events));

        RawContactFrame zeroNormal = ContactScenarioScript.Frame() with
        {
            ContactNormal = Vector3.Zero
        };
        _ = Assert.Throws<ArgumentException>(() => stream.ObservePair(source, receiver, in zeroNormal, 0d, events));

        RawContactFrame nonFiniteDepth = ContactScenarioScript.Frame() with
        {
            PenetrationDepth = float.NaN
        };
        _ = Assert.Throws<ArgumentException>(() => stream.ObservePair(source, receiver, in nonFiniteDepth, 0d, events));
    }

    /// <summary>Active contact snapshots expose the raw stream state for validation instrumentation (TR4).</summary>
    [Fact]
    public void ActiveContacts_ExposeSnapshotsAndClearOnEnd()
    {
        RawContactStream stream = new();
        List<RawContactEvent> events = [];

        Assert.Empty(stream.ActiveContacts);

        stream.ObservePair(ContactScenarioScript.SourceHand, ContactScenarioScript.ReceiverHead,
            ContactScenarioScript.Frame(), 0d, events);
        RawContactSnapshot snapshot = Assert.Single(stream.ActiveContacts);

        Assert.Equal(events[0].Contact, snapshot.Contact);
        Assert.Equal(ContactScenarioScript.SourceHand, snapshot.Source);
        Assert.Equal(ContactScenarioScript.ReceiverHead, snapshot.Receiver);
        Assert.Equal(events[0].Sample, snapshot.LastSample);
        Assert.Equal(1, stream.ActiveContactCount);

        stream.CompleteTick(events);
        stream.CompleteTick(events);

        Assert.Empty(stream.ActiveContacts);
        Assert.Equal(0, stream.ActiveContactCount);
    }
}
