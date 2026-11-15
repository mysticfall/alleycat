using System.Numerics;
using AlleyCat.Contact;
using Xunit;

namespace AlleyCat.Tests.Contact;

/// <summary>
/// Unit coverage of the classified <see cref="ContactEvent" /> contract (INTR-005 TR8, TR9): payload mapping,
/// immutability, region-slot invariants, the nullable description contract, owner attribution, and validation.
/// </summary>
public sealed class ContactEventTests
{
    private static readonly ContactParty _sourceHand = ContactScenarioScript.SourceHand;

    private static readonly ContactParty _receiverHead = ContactScenarioScript.ReceiverHead;

    /// <summary>The factory maps every transition field plus the monitor's payload slots onto the event.</summary>
    [Fact]
    public void FromTransition_MapsEveryPayloadField()
    {
        ContactClassificationTransition transition = new(
            new ContactId(7),
            _sourceHand,
            _receiverHead,
            ContactClass.Touch,
            ContactClass.Caress,
            0.31f,
            Vector3.UnitZ,
            TimeSpan.FromSeconds(1.25d));

        var contactEvent = ContactEvent.FromTransition(
            transition,
            [new ContactRegionMatch("Right Cheek", true), new ContactRegionMatch("Crown", false)],
            boneLocalDirection: new Vector3(0.1f, -0.2f, 0.97f));

        Assert.Equal(new ContactId(7), contactEvent.Contact);
        Assert.Equal(ContactClass.Caress, contactEvent.Class);
        Assert.Equal(ContactClass.Touch, contactEvent.PreviousClass);
        Assert.Equal(0.31f, contactEvent.Intensity, 5);
        Assert.Equal(_receiverHead, contactEvent.Receiver);
        Assert.Equal(_sourceHand, contactEvent.Counterpart);
        Assert.Equal(2, contactEvent.Regions.Count);
        Assert.Equal("Right Cheek", contactEvent.Regions[0].Name);
        Assert.True(contactEvent.Regions[0].IsPrimary);
        Assert.False(contactEvent.Regions[1].IsPrimary);
        Assert.Equal(new Vector3(0.1f, -0.2f, 0.97f), contactEvent.Direction);
        Assert.Equal(TimeSpan.FromSeconds(1.25d), contactEvent.Duration);
    }

    /// <summary>The description is null unless authored — never synthesised (User Requirement 7).</summary>
    [Fact]
    public void Description_IsNullUnlessAuthored()
    {
        ContactClassificationTransition transition = new(
            new ContactId(1), _sourceHand, _receiverHead, null, ContactClass.Touch, 0.1f, Vector3.UnitZ,
            TimeSpan.Zero);

        var unauthored = ContactEvent.FromTransition(transition, regions: null, Vector3.UnitZ);
        Assert.Null(unauthored.Description);

        var authored = ContactEvent.FromTransition(
            transition, regions: null, Vector3.UnitZ, description: "A soft touch grazes Sky's right cheek.");
        Assert.Equal("A soft touch grazes Sky's right cheek.", authored.Description);
    }

    /// <summary>The event is immutable: the region list is defensively copied, not aliased.</summary>
    [Fact]
    public void Regions_AreDefensivelyCopied()
    {
        ContactRegionMatch[] regions =
        [
            new("Right Cheek", true),
            new("Crown", false),
        ];
        ContactEvent contactEvent = new(
            new ContactId(1), ContactClass.Touch, null, 0.1f, _receiverHead, _sourceHand, regions, Vector3.UnitZ,
            TimeSpan.Zero, description: null);

        regions[0] = new("Left Cheek", true);
        regions[1] = new("Chin", false);

        Assert.Equal("Right Cheek", contactEvent.Regions[0].Name);
        Assert.Equal("Crown", contactEvent.Regions[1].Name);
        Assert.True(contactEvent.Regions[0].IsPrimary);
    }

    /// <summary>A non-empty region list must mark exactly one primary region (TR18d).</summary>
    [Fact]
    public void Regions_RequireExactlyOnePrimaryWhenNonEmpty()
    {
        ContactRegionMatch[][] malformed =
        [
            [new("Right Cheek", false), new("Crown", false)],
            [new("Right Cheek", true), new("Crown", true)],
        ];

        foreach (ContactRegionMatch[] regions in malformed)
        {
            _ = Assert.Throws<ArgumentException>(() => new ContactEvent(
                new ContactId(1), ContactClass.Touch, null, 0.1f, _receiverHead, _sourceHand, regions,
                Vector3.UnitZ, TimeSpan.Zero, description: null));
        }

        // Empty region lists stay valid: body-part granularity when no region matched.
        ContactEvent bodyPartGranularity = new(
            new ContactId(1), ContactClass.Touch, null, 0.1f, _receiverHead, _sourceHand, regions: [],
            Vector3.UnitZ, TimeSpan.Zero, description: null);
        Assert.Empty(bodyPartGranularity.Regions);
    }

    /// <summary>Malformed payloads — blank descriptions, bad intensities, directions, durations, and class
    /// transitions — are rejected at construction.</summary>
    [Fact]
    public void Constructor_RejectsMalformedPayloads()
    {
        _ = Assert.Throws<ArgumentException>(() => new ContactEvent(
            new ContactId(1), ContactClass.Touch, null, 0.1f, _receiverHead, _sourceHand, regions: [],
            Vector3.UnitZ, TimeSpan.Zero, description: "  "));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ContactEvent(
            new ContactId(1), ContactClass.Touch, null, intensity: -0.1f, _receiverHead, _sourceHand, regions: [],
            Vector3.UnitZ, TimeSpan.Zero, description: null));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ContactEvent(
            new ContactId(1), ContactClass.Touch, null, intensity: float.NaN, _receiverHead, _sourceHand,
            regions: [], Vector3.UnitZ, TimeSpan.Zero, description: null));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ContactEvent(
            new ContactId(1), ContactClass.Touch, null, 0.1f, _receiverHead, _sourceHand, regions: [],
            direction: new Vector3(float.NaN, 0f, 0f), TimeSpan.Zero, description: null));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ContactEvent(
            new ContactId(1), ContactClass.Touch, null, 0.1f, _receiverHead, _sourceHand, regions: [],
            Vector3.UnitZ, TimeSpan.FromSeconds(-1d), description: null));

        _ = Assert.Throws<ArgumentException>(() => new ContactEvent(
            new ContactId(1), ContactClass.Touch, ContactClass.Touch, 0.1f, _receiverHead, _sourceHand,
            regions: [], Vector3.UnitZ, TimeSpan.Zero, description: null));

        _ = Assert.Throws<ArgumentNullException>(() => new ContactEvent(
            new ContactId(1), ContactClass.Touch, null, 0.1f, receiver: null!, _sourceHand, regions: [],
            Vector3.UnitZ, TimeSpan.Zero, description: null));
    }

    /// <summary>Events compare by value, regions element-wise, despite separately built region arrays.</summary>
    [Fact]
    public void Equality_ComparesByValueIncludingRegions()
    {
        static ContactEvent Build(float intensity)
        {
            return new(
            new ContactId(3), ContactClass.Press, ContactClass.Touch, intensity, _receiverHead, _sourceHand,
            [new ContactRegionMatch("Crown", true)], Vector3.UnitY, TimeSpan.FromSeconds(2d), description: null);
        }

        Assert.Equal(Build(0.03f), Build(0.03f));
        Assert.NotEqual(Build(0.03f), Build(0.04f));
    }

    /// <summary>The counterpart descriptor attributes whose hand, foot, or object caused the contact (UR4).</summary>
    [Fact]
    public void ContactParty_FormatsOwnershipAttribution()
    {
        Assert.Equal("Mochi's Right Hand", _sourceHand.Descriptor);

        ContactParty broom = new("world/broom", "Broom Handle");
        Assert.Equal("Broom Handle", broom.Descriptor);
        Assert.Null(broom.OwnerName);

        _ = Assert.Throws<ArgumentException>(() => new ContactParty(" ", "Right Hand", "Mochi"));
        _ = Assert.Throws<ArgumentException>(() => new ContactParty("hand", " "));
        _ = Assert.Throws<ArgumentException>(() => new ContactParty("hand", "Right Hand", " "));
    }
}
