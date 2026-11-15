using AlleyCat.Contact;
using Xunit;

namespace AlleyCat.Tests.Contact;

/// <summary>
/// Unit coverage of the classification engine and its per-contact state machines (INTR-005 TR5, TR6): all five
/// classes arising from scripted raw-contact sequences, Impact/Hit classified at Begin, the single
/// Touch→Caress/Stroke revision transition, transitions-only emission with steady-contact silence, silent
/// contact End, per-contact isolation, and lifecycle misrouting guards.
/// </summary>
public sealed class ContactClassificationEngineTests
{
    /// <summary>An approaching contact at or above the impact threshold classifies as Impact/Hit at Begin itself.</summary>
    [Fact]
    public void Begin_ClassifiesImpactFromApproachKinematics()
    {
        ContactClassificationEngine engine = new(ContactClassificationPolicy.Default);

        ContactClassificationTransition? entry = engine.Process(ContactScenarioScript.Raw(
            RawContactPhase.Begin, new ContactId(1), ContactScenarioScript.Sample(0f, normalSpeed: 2.0f)));

        Assert.NotNull(entry);
        Assert.Equal(ContactClass.Impact, entry!.Class);
        Assert.Null(entry.PreviousClass);
        Assert.Equal(2.0f, entry.Intensity, 5);
        Assert.Equal(1, engine.ActiveContactCount);
    }

    /// <summary>A gentle begin defaults to Touch.</summary>
    [Fact]
    public void Begin_ClassifiesAGentleBeginAsTouch()
    {
        ContactClassificationEngine engine = new(ContactClassificationPolicy.Default);

        ContactClassificationTransition? entry = engine.Process(ContactScenarioScript.Raw(
            RawContactPhase.Begin, new ContactId(1), ContactScenarioScript.Sample(0f, normalSpeed: 0.1f)));

        Assert.NotNull(entry);
        Assert.Equal(ContactClass.Touch, entry!.Class);
        Assert.Null(entry.PreviousClass);
        Assert.Equal(0.1f, entry.Intensity, 5);
    }

    /// <summary>A stroke that began as Touch is revised to Caress/Stroke exactly once, after the motion window.</summary>
    [Fact]
    public void Update_RevisesTouchToCaressExactlyOnce()
    {
        ContactClassificationEngine engine = new(ContactClassificationPolicy.Default);
        ContactId contact = new(1);

        ContactClassificationTransition? entry = engine.Process(ContactScenarioScript.Raw(
            RawContactPhase.Begin, contact, ContactScenarioScript.Sample(0f, normalSpeed: 0.1f)));
        Assert.NotNull(entry);
        Assert.Equal(ContactClass.Touch, entry!.Class);

        // Tangential motion at 0.3 m/s, above the 0.2 m/s default slip threshold. The 0.25 s window is reached
        // after four binary-exact 0.0625 s steps.
        List<ContactClassificationTransition> revisions = [];
        for (int step = 1; step <= 10; step++)
        {
            ContactClassificationTransition? transition = engine.Process(ContactScenarioScript.Raw(
                RawContactPhase.Update, contact,
                ContactScenarioScript.Sample(step * ContactScenarioScript.TickSeconds, tangentialSpeed: 0.3f)));
            if (transition is not null)
            {
                revisions.Add(transition);
            }
        }

        ContactClassificationTransition revision = Assert.Single(revisions);
        Assert.Equal(ContactClass.Caress, revision.Class);
        Assert.Equal(ContactClass.Touch, revision.PreviousClass);
        Assert.Equal(0.3f, revision.Intensity, 5);
        Assert.Equal(TimeSpan.FromSeconds(4 * ContactScenarioScript.TickSeconds), revision.Duration);
        Assert.Equal(contact, revision.Contact);

        // Continued stroking stays a steady Caress/Stroke and emits nothing further.
        for (int step = 11; step <= 30; step++)
        {
            Assert.Null(engine.Process(ContactScenarioScript.Raw(
                RawContactPhase.Update, contact,
                ContactScenarioScript.Sample(step * ContactScenarioScript.TickSeconds, tangentialSpeed: 0.3f))));
        }
    }

    /// <summary>Sustained deep pressure confirms Press from Touch after the press window.</summary>
    [Fact]
    public void Update_ConfirmsPressFromSustainedDepth()
    {
        ContactClassificationEngine engine = new(ContactClassificationPolicy.Default);
        ContactId contact = new(1);

        ContactClassificationTransition? entry = engine.Process(ContactScenarioScript.Raw(
            RawContactPhase.Begin, contact, ContactScenarioScript.Sample(0f, normalSpeed: 0.1f)));
        Assert.Equal(ContactClass.Touch, entry!.Class);

        // Depth of 0.03 m holds the press condition (threshold 0.02 m); the 0.15 s window is exceeded on the
        // third 0.0625 s step.
        List<ContactClassificationTransition> transitions = [];
        for (int step = 1; step <= 5; step++)
        {
            ContactClassificationTransition? transition = engine.Process(ContactScenarioScript.Raw(
                RawContactPhase.Update, contact,
                ContactScenarioScript.Sample(step * ContactScenarioScript.TickSeconds, penetrationDepth: 0.03f)));
            if (transition is not null)
            {
                transitions.Add(transition);
            }
        }

        ContactClassificationTransition press = Assert.Single(transitions);
        Assert.Equal(ContactClass.Press, press.Class);
        Assert.Equal(ContactClass.Touch, press.PreviousClass);
        Assert.Equal(0.03f, press.Intensity, 5);
    }

    /// <summary>Press also confirms through the press-rate arm alone: penetration stays below the depth
    /// threshold while the normal closing speed holds above the press-rate threshold for the window.</summary>
    [Fact]
    public void Update_ConfirmsPressFromSustainedPressRateAlone()
    {
        ContactClassificationEngine engine = new(ContactClassificationPolicy.Default);
        ContactId contact = new(1);

        ContactClassificationTransition? entry = engine.Process(ContactScenarioScript.Raw(
            RawContactPhase.Begin, contact, ContactScenarioScript.Sample(0f, normalSpeed: 0.1f)));
        Assert.Equal(ContactClass.Touch, entry!.Class);

        // Shallow depth of 0.001 m sits below the 0.02 m depth threshold, but the 0.08 m/s closing speed holds
        // above the 0.05 m/s press-rate threshold; the 0.15 s window is exceeded on the third 0.0625 s step.
        List<ContactClassificationTransition> transitions = [];
        for (int step = 1; step <= 5; step++)
        {
            ContactClassificationTransition? transition = engine.Process(ContactScenarioScript.Raw(
                RawContactPhase.Update, contact,
                ContactScenarioScript.Sample(step * ContactScenarioScript.TickSeconds, normalSpeed: 0.08f)));
            if (transition is not null)
            {
                transitions.Add(transition);
            }
        }

        ContactClassificationTransition press = Assert.Single(transitions);
        Assert.Equal(ContactClass.Press, press.Class);
        Assert.Equal(ContactClass.Touch, press.PreviousClass);
        Assert.Equal(0.001f, press.Intensity, 5);
    }

    /// <summary>Prolonged settled contact settles into Rest/Lean, reporting the lean depth as its intensity.</summary>
    [Fact]
    public void Update_SettlesProlongedStillContactIntoRest()
    {
        ContactClassificationEngine engine = new(ContactClassificationPolicy.Default);
        ContactId contact = new(1);

        ContactClassificationTransition? entry = engine.Process(ContactScenarioScript.Raw(
            RawContactPhase.Begin, contact, ContactScenarioScript.Sample(0f, normalSpeed: 0.1f)));
        Assert.Equal(ContactClass.Touch, entry!.Class);

        List<ContactClassificationTransition> transitions = [];
        // Still contact for two seconds: exactly thirty-two 0.0625 s steps reach the 2.0 s default window.
        for (int step = 1; step <= 32; step++)
        {
            ContactClassificationTransition? transition = engine.Process(ContactScenarioScript.Raw(
                RawContactPhase.Update, contact, ContactScenarioScript.Sample(step * ContactScenarioScript.TickSeconds)));
            if (transition is not null)
            {
                transitions.Add(transition);
            }
        }

        ContactClassificationTransition rest = Assert.Single(transitions);
        Assert.Equal(ContactClass.Rest, rest.Class);
        Assert.Equal(ContactClass.Touch, rest.PreviousClass);
        Assert.Equal(0.001f, rest.Intensity, 5);

        // Settled Rest stays silent however long the contact persists.
        for (int step = 33; step <= 80; step++)
        {
            Assert.Null(engine.Process(ContactScenarioScript.Raw(
                RawContactPhase.Update, contact, ContactScenarioScript.Sample(step * ContactScenarioScript.TickSeconds))));
        }
    }

    /// <summary>An Impact can settle into Rest — a slap whose hand then stays on the receiver.</summary>
    [Fact]
    public void Update_SettlesAnImpactIntoRest()
    {
        ContactClassificationEngine engine = new(ContactClassificationPolicy.Default);
        ContactId contact = new(1);

        ContactClassificationTransition? entry = engine.Process(ContactScenarioScript.Raw(
            RawContactPhase.Begin, contact, ContactScenarioScript.Sample(0f, normalSpeed: 2.0f)));
        Assert.Equal(ContactClass.Impact, entry!.Class);

        List<ContactClassificationTransition> transitions = [];
        for (int step = 1; step <= 32; step++)
        {
            ContactClassificationTransition? transition = engine.Process(ContactScenarioScript.Raw(
                RawContactPhase.Update, contact, ContactScenarioScript.Sample(step * ContactScenarioScript.TickSeconds)));
            if (transition is not null)
            {
                transitions.Add(transition);
            }
        }

        ContactClassificationTransition rest = Assert.Single(transitions);
        Assert.Equal(ContactClass.Rest, rest.Class);
        Assert.Equal(ContactClass.Impact, rest.PreviousClass);
    }

    /// <summary>One contact may chain Touch → Press → Rest, emitting one transition per class change.</summary>
    [Fact]
    public void Update_ChainsTouchPressRest()
    {
        ContactClassificationEngine engine = new(ContactClassificationPolicy.Default);
        ContactId contact = new(1);

        List<ContactClassificationTransition> transitions =
        [
            engine.Process(ContactScenarioScript.Raw(
                RawContactPhase.Begin, contact, ContactScenarioScript.Sample(0f, normalSpeed: 0.1f)))!,
        ];

        for (int step = 1; step <= 40; step++)
        {
            // Three deep steps confirm Press, then the contact stays still and settles into Rest at 2.0 s.
            float depth = step <= 3 ? 0.03f : 0.001f;
            ContactClassificationTransition? transition = engine.Process(ContactScenarioScript.Raw(
                RawContactPhase.Update, contact,
                ContactScenarioScript.Sample(step * ContactScenarioScript.TickSeconds, penetrationDepth: depth)));
            if (transition is not null)
            {
                transitions.Add(transition);
            }
        }

        Assert.Equal(
        [
            ContactClass.Touch,
            ContactClass.Press,
            ContactClass.Rest,
        ], transitions.Select(transition => transition.Class));
        Assert.Equal([null, ContactClass.Touch, ContactClass.Press], transitions.Select(transition => transition.PreviousClass));
    }

    /// <summary>Steady contact within the rest window emits nothing after its entry transition (TR6, AC-TR3).</summary>
    [Fact]
    public void Update_SteadyTouchEmitsNothingAfterTheEntryTransition()
    {
        ContactClassificationEngine engine = new(ContactClassificationPolicy.Default);
        ContactId contact = new(1);

        int emissions = 0;
        if (engine.Process(ContactScenarioScript.Raw(
                RawContactPhase.Begin, contact, ContactScenarioScript.Sample(0f, normalSpeed: 0.1f))) is not null)
        {
            emissions++;
        }

        for (int step = 1; step <= 5; step++)
        {
            if (engine.Process(ContactScenarioScript.Raw(
                    RawContactPhase.Update, contact,
                    ContactScenarioScript.Sample(step * ContactScenarioScript.TickSeconds))) is not null)
            {
                emissions++;
            }
        }

        Assert.Equal(1, emissions);
    }

    /// <summary>Contact End emits nothing and releases the contact's state machine.</summary>
    [Fact]
    public void End_EmitsNothingAndReleasesTheMachine()
    {
        ContactClassificationEngine engine = new(ContactClassificationPolicy.Default);
        ContactId contact = new(1);

        _ = engine.Process(ContactScenarioScript.Raw(
            RawContactPhase.Begin, contact, ContactScenarioScript.Sample(0f, normalSpeed: 0.1f)));

        Assert.Null(engine.Process(ContactScenarioScript.Raw(
            RawContactPhase.End, contact, ContactScenarioScript.Sample(ContactScenarioScript.TickSeconds))));
        Assert.Equal(0, engine.ActiveContactCount);

        // The raw lifecycle ends exactly once; a second End is misrouted.
        _ = Assert.Throws<InvalidOperationException>(() => engine.Process(ContactScenarioScript.Raw(
            RawContactPhase.End, contact, ContactScenarioScript.Sample(ContactScenarioScript.TickSeconds))));
    }

    /// <summary>Concurrent contacts hold independent state machines with independent classifications.</summary>
    [Fact]
    public void Process_TracksConcurrentContactsIndependently()
    {
        ContactClassificationEngine engine = new(ContactClassificationPolicy.Default);
        ContactId impact = new(1);
        ContactId stroke = new(2);

        Assert.Equal(ContactClass.Impact, engine.Process(ContactScenarioScript.Raw(
            RawContactPhase.Begin, impact, ContactScenarioScript.Sample(0f, normalSpeed: 2.0f)))!.Class);
        Assert.Equal(ContactClass.Touch, engine.Process(ContactScenarioScript.Raw(
            RawContactPhase.Begin, stroke, ContactScenarioScript.Sample(0f, normalSpeed: 0.05f)))!.Class);
        Assert.Equal(2, engine.ActiveContactCount);

        List<ContactClassificationTransition> strokeRevisions = [];
        for (int step = 1; step <= 6; step++)
        {
            if (engine.Process(ContactScenarioScript.Raw(
                    RawContactPhase.Update, stroke,
                    ContactScenarioScript.Sample(step * ContactScenarioScript.TickSeconds,
                        tangentialSpeed: 0.3f))) is { } revision)
            {
                strokeRevisions.Add(revision);
            }

            // The impact contact's still updates emit nothing and do not disturb its neighbour.
            Assert.Null(engine.Process(ContactScenarioScript.Raw(
                RawContactPhase.Update, impact,
                ContactScenarioScript.Sample(step * ContactScenarioScript.TickSeconds))));
        }

        Assert.Equal(ContactClass.Caress, Assert.Single(strokeRevisions).Class);
    }

    /// <summary>Misrouted lifecycle events fail fast instead of silently inventing classification state.</summary>
    [Fact]
    public void Process_RejectsMisroutedLifecycleEvents()
    {
        ContactClassificationEngine engine = new(ContactClassificationPolicy.Default);

        _ = Assert.Throws<InvalidOperationException>(() => engine.Process(ContactScenarioScript.Raw(
            RawContactPhase.Update, new ContactId(1), ContactScenarioScript.Sample(0f))));

        _ = Assert.Throws<InvalidOperationException>(() => engine.Process(ContactScenarioScript.Raw(
            RawContactPhase.End, new ContactId(1), ContactScenarioScript.Sample(0f))));

        ContactId contact = new(7);
        _ = engine.Process(ContactScenarioScript.Raw(
            RawContactPhase.Begin, contact, ContactScenarioScript.Sample(0f)));
        _ = Assert.Throws<InvalidOperationException>(() => engine.Process(ContactScenarioScript.Raw(
            RawContactPhase.Begin, contact, ContactScenarioScript.Sample(0f))));
    }

    /// <summary>The engine consumes the raw stream's real events: a scripted stroke through observed frames
    /// produces the entry Touch and exactly one Caress revision (TR3 feeding TR6).</summary>
    [Fact]
    public void Process_ClassifiesACompleteRawStreamPipeline()
    {
        RawContactStream stream = new();
        ContactClassificationEngine engine = new(ContactClassificationPolicy.Default);
        List<RawContactEvent> rawEvents = [];
        List<ContactClassificationTransition> transitions = [];

        for (int tick = 0; tick <= 10; tick++)
        {
            double timestamp = tick * ContactScenarioScript.TickSeconds;
            if (tick == 0)
            {
                // Gentle arrival begins as Touch.
                stream.ObservePair(ContactScenarioScript.SourceHand, ContactScenarioScript.ReceiverHead,
                    ContactScenarioScript.Frame(normalSpeed: 0.1f), timestamp, rawEvents);
            }
            else
            {
                stream.ObservePair(ContactScenarioScript.SourceHand, ContactScenarioScript.ReceiverHead,
                    ContactScenarioScript.Frame(tangentialSpeed: 0.3f), timestamp, rawEvents);
            }

            stream.CompleteTick(rawEvents);
        }

        foreach (RawContactEvent raw in rawEvents)
        {
            if (engine.Process(raw) is { } transition)
            {
                transitions.Add(transition);
            }
        }

        Assert.Equal(
        [
            ContactClass.Touch,
            ContactClass.Caress,
        ], transitions.Select(transition => transition.Class));
        Assert.Equal(ContactScenarioScript.SourceHand, transitions[0].Source);
        Assert.Equal(ContactScenarioScript.ReceiverHead, transitions[0].Receiver);
    }
}
