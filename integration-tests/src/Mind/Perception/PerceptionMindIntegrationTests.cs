using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Core.Time;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Mind.Perception;
using AlleyCat.Scene;
using AlleyCat.Sense;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.Perception;

/// <summary>Runtime contracts for Mind-owned perception registration, delivery, and transactional ingestion.</summary>
[Headless]
public sealed class PerceptionMindIntegrationTests
{
    /// <inheritdoc/>
    [Fact]
    public async Task Registry_DiscoversDirectChildrenAndFansOutAssignableFamiliesInChildOrder()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var sense = new TestSense(typeof(FirstPercept), typeof(SecondPercept));
        var owner = new TestCharacter(sense);
        var mind = new TestMind(owner);
        var family = new RecordingFaculty<BasePercept>("family");
        var exact = new RecordingFaculty<FirstPercept>("exact");
        mind.AddChild(family);
        mind.AddChild(exact);
        var nestedHolder = new Node();
        nestedHolder.AddChild(new RecordingFaculty<FirstPercept>("nested"));
        mind.AddChild(nestedHolder);
        var root = new Node();
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            sense.Publish(new FirstPercept("one"));
            sense.Publish(new SecondPercept("two"));
            await mind.DrainPerceptionsForTestingAsync();

            Assert.Equal(["family:one", "exact:one", "family:two"], Values(mind.Timeline));
            Assert.Equal(1, sense.SubscriptionCount);
            _ = Assert.Throws<InvalidOperationException>(() => sense.Publish(new UndeclaredPercept()));
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }

        AssertActivationFails(new TestCharacter(new TestSense(typeof(FirstPercept))), []);
        AssertActivationFails(
            new TestCharacter(new TestSense(typeof(IPercept))),
            [new DelegatingFaculty<IPercept>((_, _, _) => ValueTask.CompletedTask)]);
    }

    /// <summary>
    /// Percept interpretation serialises in publication order, faculty bindings snapshot per publication, and a
    /// component refresh rebinds observation subscriptions so emissions from replaced faculties stop committing
    /// (AI-001 TR-8, AI-006 TR-2).
    /// </summary>
    [Fact]
    public async Task Intake_SerialisesAsyncFacultiesAndSnapshotsBindingsAcrossRefresh()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var sense = new TestSense(typeof(FirstPercept));
        var owner = new TestCharacter(sense);
        var mind = new TestMind(owner);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> invocations = [];
        DelegatingFaculty<FirstPercept>? oldFaculty = null;
        oldFaculty = new DelegatingFaculty<FirstPercept>(async (percept, _, _) =>
        {
            invocations.Add($"old:{percept.Id}");
            if (percept.Id == "first")
            {
                // Emitted while the faculty is still bound, so Mind commits this observation.
                oldFaculty!.EmitForTest(new TestObservation("old:first", 0f));
                _ = started.TrySetResult();
                await gate.Task;
            }
            else
            {
                // Emitted after the rebind removed this faculty's observation subscription, so Mind drops it.
                oldFaculty!.EmitForTest(new TestObservation($"old:{percept.Id}", 0f));
            }
        });
        mind.AddChild(oldFaculty);
        var root = new Node();
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            sense.Publish(new FirstPercept("first"));
            await started.Task;
            sense.Publish(new FirstPercept("second"));

            mind.RemoveChild(oldFaculty);
            DelegatingFaculty<FirstPercept>? replacement = null;
            replacement = new DelegatingFaculty<FirstPercept>((percept, _, _) =>
            {
                invocations.Add($"new:{percept.Id}");
                replacement!.EmitForTest(new TestObservation($"new:{percept.Id}", 0f));
                return ValueTask.CompletedTask;
            });
            mind.AddChild(replacement);
            owner.RefreshComponents(sense);
            sense.Publish(new FirstPercept("third"));

            // The gated first percept still blocks the queued second percept: serial publication-order processing.
            Assert.Equal(["old:first"], invocations);
            _ = gate.TrySetResult();
            await mind.DrainPerceptionsForTestingAsync();

            // Pre-refresh publications dispatch to the snapshotted old faculty; the post-refresh publication uses the
            // replacement. Only emissions raised while subscribed commit.
            Assert.Equal(["old:first", "old:second", "new:third"], invocations);
            Assert.Equal(["old:first", "new:third"], Values(mind.Timeline));
            oldFaculty.Free();
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// Faults and invalid observations roll back only their own observation: earlier committed observations stand
    /// and later queued items continue (AI-001 TR-8, AI-006 TR-2).
    /// </summary>
    [Fact]
    public async Task Aggregate_FaultsAndInvalidObservationsRollBackOnlyThatObservationWithoutBlockingLaterPercepts()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var sense = new TestSense(typeof(FirstPercept));
        var owner = new TestCharacter(sense);
        var mind = new TestMind(owner) { ObservationImportanceThreshold = 100f };
        DelegatingFaculty<FirstPercept>? faculty = null;
        faculty = new DelegatingFaculty<FirstPercept>((percept, _, _) =>
        {
            if (percept.Id == "fault")
            {
                faculty!.EmitForTest(new TestObservation("first:before", 0f));
                faculty.EmitForTest(new FaultingObservation());
                faculty.EmitForTest(new TestObservation("first:after", 0f));
            }
            else
            {
                faculty!.EmitForTest(new TestObservation("second:before", 0f));
                faculty.EmitForTest(new TestObservation("invalid-importance", float.NaN));
                faculty.EmitForTest(new AttentionObservation("invalid-id", 0.5f));
                faculty.EmitForTest(new TestObservation("second:after", 0f));
            }

            return ValueTask.CompletedTask;
        });
        mind.AddChild(faculty);
        var root = new Node();
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            sense.Publish(new FirstPercept("fault"));
            sense.Publish(new FirstPercept("invalid"));
            await mind.DrainPerceptionsForTestingAsync();

            Assert.Equal(["first:before", "first:after", "second:before", "second:after"], Values(mind.Timeline));
            Assert.Equal(4, mind.Ingested.Count);
            Assert.Equal(0f, mind.GetAttention("char:invalid"));
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <inheritdoc/>
    [Fact]
    public async Task Exit_OverlappingPublicationCannotRemainQueuedOrExecute()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var sense = new TestSense(typeof(FirstPercept));
        var owner = new TestCharacter(sense);
        var mind = new TestMind(owner);
        var intakeReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseIntake = new ManualResetEventSlim(initialState: false);
        var facultyInvocations = new Counter();
        DelegatingFaculty<FirstPercept>? faculty = null;
        faculty = new DelegatingFaculty<FirstPercept>((_, _, _) =>
        {
            facultyInvocations.Value++;
            faculty!.EmitForTest(new TestObservation("too-late", 0f));
            return ValueTask.CompletedTask;
        });
        mind.AddChild(faculty);
        mind.SetBeforePerceptionEnqueueForTesting(() =>
        {
            _ = intakeReached.TrySetResult();
            releaseIntake.Wait();
        });
        var root = new Node();
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        var publication = Task.Run(() => sense.Publish(new FirstPercept("overlap")));
        await intakeReached.Task;
        root.RemoveChild(mind);
        releaseIntake.Set();
        await publication;

        Assert.Equal(0, mind.GetPendingPerceptionCountForTesting());
        await mind.DrainPerceptionsForTestingAsync();
        Assert.Equal(0, facultyInvocations.Value);
        Assert.Empty(mind.Timeline);
        Assert.Empty(mind.Ingested);
        Assert.Equal(0, sense.SubscriptionCount);
        mind.QueueFree();
        root.QueueFree();
        await TestUtils.WaitForFramesAsync(tree, 2);
    }

    /// <inheritdoc/>
    [Fact]
    public async Task Exit_DuringAwaitedFacultyPreventsLaterFacultiesAndCommit()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var sense = new TestSense(typeof(FirstPercept));
        var owner = new TestCharacter(sense);
        var mind = new TestMind(owner);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterFacultyInvocations = new Counter();
        DelegatingFaculty<FirstPercept>? gatedFaculty = null;
        gatedFaculty = new DelegatingFaculty<FirstPercept>(async (_, _, _) =>
        {
            _ = started.TrySetResult();
            await release.Task;
            gatedFaculty!.EmitForTest(new TestObservation("too-late", 0f));
        });
        mind.AddChild(gatedFaculty);
        DelegatingFaculty<FirstPercept>? laterFaculty = null;
        laterFaculty = new DelegatingFaculty<FirstPercept>((_, _, _) =>
        {
            laterFacultyInvocations.Value++;
            laterFaculty!.EmitForTest(new TestObservation("later", 0f));
            return ValueTask.CompletedTask;
        });
        mind.AddChild(laterFaculty);
        var root = new Node();
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        sense.Publish(new FirstPercept("first"));
        await started.Task;
        root.RemoveChild(mind);
        _ = release.TrySetResult();
        await mind.DrainPerceptionsForTestingAsync();

        Assert.Equal(0, laterFacultyInvocations.Value);
        Assert.Empty(mind.Timeline);
        Assert.Empty(mind.Ingested);
        Assert.Equal(0, sense.SubscriptionCount);
        mind.QueueFree();
        root.QueueFree();
        await TestUtils.WaitForFramesAsync(tree, 2);
    }

    /// <inheritdoc/>
    [Fact]
    public async Task LifetimePolicies_DefaultToAcceptUnknownTypesAndOwnVisualEvidenceSuppression()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var sense = new TestSense(typeof(FirstPercept));
        var owner = new TestCharacter(sense);
        var mind = new TestMind(owner) { ObservationImportanceThreshold = 100f };
        var importanceCounter = new Counter();
        var faculty = new BatchFaculty();
        mind.AddChild(faculty);
        var root = new Node();
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            faculty.Observations =
            [
                new TestObservation("allowed", 0f),
                new TestObservation("allowed", 0f),
                Equivalent("a", "same", importanceCounter),
                Equivalent("a", "same", importanceCounter),
                Equivalent("b", "same", importanceCounter),
                Equivalent("A", "same", importanceCounter),
            ];
            sense.Publish(new FirstPercept("batch"));
            await mind.DrainPerceptionsForTestingAsync();

            // The legacy duplicate members on arbitrary types are ignored. Undeclared types receive the fallback
            // policy, while concrete visual evidence uses its own registered policy.
            Assert.Equal(["allowed", "allowed", "a:same", "a:same", "b:same", "A:same"], Values(mind.Timeline));
            Assert.Equal(4, importanceCounter.Value);
            Assert.Equal(6, mind.Ingested.Count);

            faculty.Observations =
            [
                Equivalent("a", "same", importanceCounter) with { ObservedAt = 999d },
                Equivalent("a", "different", importanceCounter),
                Equivalent("a", "same", importanceCounter),
            ];
            sense.Publish(new FirstPercept("latest"));
            await mind.DrainPerceptionsForTestingAsync();

            Assert.Equal(
                ["allowed", "allowed", "a:same", "a:same", "b:same", "A:same", "a:same", "a:different", "a:same"],
                Values(mind.Timeline));
            Assert.Equal(7, importanceCounter.Value);
            Assert.Equal(9, mind.Ingested.Count);

            faculty.Observations =
            [
                Equivalent("a", "same", importanceCounter),
                new TestObservation("invalid", float.NaN),
            ];
            sense.Publish(new FirstPercept("rollback"));
            await mind.DrainPerceptionsForTestingAsync();
            // Faculty emissions are independently queued, so the valid first emission commits before the following
            // invalid one is contained. The malformed observation itself has no acceptance effects.
            Assert.Equal(10, mind.Timeline.Count);
            Assert.Equal(8, importanceCounter.Value);
            Assert.Equal(10, mind.Ingested.Count);

            faculty.Observations =
            [
                new ObservedVisualDescription("char:a", "same") { ObservedAt = 1d },
                new ObservedVisualDescription("char:a", "same") { ObservedAt = 2d },
                new ObservedVisualDescription("char:a", "different"),
                new ObservedVisualDescription("char:b", "same"),
            ];
            sense.Publish(new FirstPercept("visual-batch"));
            await mind.DrainPerceptionsForTestingAsync();

            Assert.Empty(mind.Timeline.OfType<ObservedVisualDescription>());
            Assert.Equal(
                [("char:a", "different"), ("char:b", "same")],
                mind.Retained.OfType<ObservedVisualDescription>().Select(item => (item.SubjectId, item.Description)));

            faculty.Observations = [new ObservedVisualDescription("char:a", "different")];
            sense.Publish(new FirstPercept("visual-latest-equivalent"));
            await mind.DrainPerceptionsForTestingAsync();
            Assert.Equal(2, mind.Retained.OfType<ObservedVisualDescription>().Count());

            faculty.Observations = [new ObservedVisualDescription("char:a", "same")];
            sense.Publish(new FirstPercept("visual-latest-different"));
            await mind.DrainPerceptionsForTestingAsync();
            Assert.Equal(
                [("char:b", "same"), ("char:a", "same")],
                mind.Retained.OfType<ObservedVisualDescription>().Select(item => (item.SubjectId, item.Description)));
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// Direct, tool-result, and perception intake share policy-owned duplicate staging before every ingestion side
    /// effect. Tool observations are actor-stamped before semantic comparison, while scheduling claims carry no
    /// observation payload (AI-001 TR-3; AI-002 TR-7/8).
    /// </summary>
    [Fact]
    public async Task DuplicatePolicy_AllIngestionRoutesSuppressBeforeImportanceTimestampMutationAndNotification()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var sense = new TestSense(typeof(FirstPercept));
        var owner = new TestCharacter(sense);
        var mind = new TestMind(owner) { ObservationImportanceThreshold = 1f };
        var faculty = new BatchFaculty();
        var importanceCounter = new Counter();
        var clock = new CountingGameClock { CurrentSeconds = 10d };
        string ownerFullId = ((ICharacter)owner).FullId;
        int notableSignals = 0;
        mind.SetGameClockLoaderForTesting(() => clock);
        mind.DeliverySignalForTest(_ => notableSignals++);
        mind.AddChild(faculty);
        var root = new Node();
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            mind.ObserveForTest(EquivalentAction(ownerFullId, "direct", "same", importanceCounter));

            mind.ObserveForTest(EquivalentAction(ownerFullId, "direct", "same", importanceCounter) with
            {
                ObservedAt = 999d,
            });

            Assert.Equal(1, importanceCounter.Value);
            Assert.Equal(2, clock.ReadCount);
            Assert.Equal(1, notableSignals);
            _ = Assert.Single(mind.Timeline);
            _ = Assert.Single(mind.Ingested);

            mind.IngestToolObservationsForTest(
            [
                EquivalentAction("spoofed:first", "tool", "same", importanceCounter),
                EquivalentAction("spoofed:second", "tool", "same", importanceCounter) with { ObservedAt = 999d },
            ]);

            EquivalentActionObservation toolObservation = Assert.IsType<EquivalentActionObservation>(mind.Timeline[1]);
            Assert.Equal(ownerFullId, toolObservation.ActorId);
            Assert.Equal(2, importanceCounter.Value);
            Assert.Equal(3, clock.ReadCount);
            Assert.Equal(1, notableSignals);
            Assert.Equal(2, mind.Timeline.Count);
            Assert.Equal(2, mind.Ingested.Count);

            faculty.Observations =
            [
                EquivalentAction(ownerFullId, "perception", "same", importanceCounter),
                EquivalentAction(ownerFullId, "perception", "same", importanceCounter) with { ObservedAt = 999d },
            ];
            sense.Publish(new FirstPercept("duplicate-batch"));
            await mind.DrainPerceptionsForTestingAsync();

            Assert.Equal(3, importanceCounter.Value);
            Assert.Equal(5, clock.ReadCount);
            Assert.Equal(1, notableSignals);
            Assert.Equal(3, mind.Timeline.Count);
            Assert.Equal(3, mind.Ingested.Count);
            Assert.All(mind.Timeline, observation => Assert.Equal(10d, observation.ObservedAt));
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <inheritdoc/>
    [Fact]
    public async Task Conversation_HearingIsTheOnlyVoiceListenerAndExternalSpeechCreatesOneObservation()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var hearing = new Hearing();
        var owner = new TestCharacter(hearing);
        var mind = new TestMind(owner);
        mind.AddChild(new HearingFaculty());
        var source = new TestVoice { Id = "external" };
        var root = new Node();
        root.AddChild(hearing);
        root.AddChild(mind);
        root.AddChild(source);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            Assert.Contains(hearing, tree.GetNodesInGroup(IHearing.GroupName));
            Assert.DoesNotContain(mind, tree.GetNodesInGroup(IHearing.GroupName));
            source.Speak("external speech");
            await mind.DrainPerceptionsForTestingAsync();
            Assert.Equal("external speech", Assert.IsType<ObservedSpeech>(Assert.Single(mind.Timeline)).Content);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// Grouped hearing transport remains private to Mind ingestion while duplicate group segments are rejected before
    /// timestamps, importance, delivery, and timeline side effects; ungrouped repeated speech remains allowed.
    /// </summary>
    [Fact]
    public async Task SpeechPerception_GroupedTransportIsImmutableAndDuplicateSegmentsSuppressBeforeIngestion()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var hearing = new Hearing();
        var ownerVoice = new TestVoice { Id = "owner-voice" };
        var source = new TestVoice { Id = "external-voice" };
        var owner = new TestCharacter(hearing, ownerVoice);
        var speaker = new TestCharacter(source) { Id = "speaker" };
        var clock = new CountingGameClock { CurrentSeconds = 10d };
        var mind = new TestMind(owner);
        mind.SetGameClockLoaderForTesting(() => clock);
        mind.SetSceneContextLoaderForTesting(() => new TestSceneContext([owner, speaker]));
        mind.AddChild(new SpeechPerception());
        int deliveries = 0;
        mind.DeliverySignalForTest(_ => deliveries++);
        var root = new Node();
        root.AddChild(hearing);
        root.AddChild(ownerVoice);
        root.AddChild(source);
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            var metadata = new SpeechSegmentMetadata("automatic-group", 1);
            source.PublishCompletedSpeech("first grouped segment", metadata);
            await mind.DrainPerceptionsForTestingAsync();

            ObservedSpeech grouped = Assert.IsType<ObservedSpeech>(Assert.Single(mind.Timeline));
            Assert.Equal(10d, grouped.ObservedAt);
            Assert.Equal(1, clock.ReadCount);
            Assert.Equal(1, deliveries);

            source.PublishCompletedSpeech("duplicate grouped segment", metadata);
            await mind.DrainPerceptionsForTestingAsync();

            _ = Assert.Single(mind.Timeline);
            Assert.Equal(1, clock.ReadCount);
            Assert.Equal(1, deliveries);

            source.PublishCompletedSpeech("equal ungrouped segment");
            source.PublishCompletedSpeech("equal ungrouped segment");
            await mind.DrainPerceptionsForTestingAsync();

            ObservedSpeech[] ungrouped = [.. mind.Timeline.OfType<ObservedSpeech>().Skip(1)];
            Assert.Equal(2, ungrouped.Length);
            Assert.Equal(3, clock.ReadCount);
            // Scheduling pressure remains pending until a provider response confirms its request snapshot, so it
            // does not signal another payload-free delivery merely because later speech was accepted.
            Assert.Equal(1, deliveries);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// The commit-identity gate is generic (AI-001 TR-9, AC-37): an arbitrary observation type supplying the
    /// contract's identity tuple receives exact-once enforcement before every ingestion effect, while
    /// identity-free observations keep the ordinary allow policy regardless of content.
    /// </summary>
    [Fact]
    public async Task CommitIdentity_ArbitraryObservationTypeIsSuppressedExactlyOnceBeforeIngestionEffects()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var sense = new TestSense(typeof(FirstPercept));
        var owner = new TestCharacter(sense);
        var clock = new CountingGameClock { CurrentSeconds = 10d };
        var mind = new TestMind(owner);
        mind.SetGameClockLoaderForTesting(() => clock);
        var root = new Node();
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            mind.ObserveForTest(new CommitIdentifiedObservation("stream-a", 4L, "first"));
            mind.ObserveForTest(new CommitIdentifiedObservation("stream-a", 4L, "duplicate"));

            _ = Assert.Single(mind.Timeline);
            Assert.Equal(1, clock.ReadCount);
            _ = Assert.Single(mind.Ingested);

            mind.ObserveForTest(new CommitIdentifiedObservation("stream-a", 5L, "other turn"));
            mind.ObserveForTest(new TestObservation("identity-free", 0f));
            mind.ObserveForTest(new TestObservation("identity-free", 0f));

            Assert.Equal(
                ["stream-a:4:first", "stream-a:5:other turn", "identity-free", "identity-free"],
                mind.Timeline.Select(static observation => observation switch
                {
                    CommitIdentifiedObservation identified => $"{identified.Stream}:{identified.Turn}:{identified.Value}",
                    TestObservation test => test.Value,
                    _ => observation.TypeKey,
                }));
            Assert.Equal(4, clock.ReadCount);
            Assert.Equal(4, mind.Ingested.Count);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// External textless resume is transient and gated by source-generic attention, while exact-self activity never
    /// crosses the Mind boundary.
    /// </summary>
    [Fact]
    public async Task SpeechLifecycle_ResumeForwardsOnceWithoutPerceptionOrWaitEffectsAndRequiresAttention()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var hearing = new Hearing();
        var ownerVoice = new TestVoice { Id = "owner-voice" };
        var source = new TestVoice { Id = "external-voice" };
        var strangerVoice = new TestVoice { Id = "stranger-voice" };
        var owner = new TestCharacter(hearing, ownerVoice);
        var speaker = new TestCharacter(source) { Id = "speaker" };
        var stranger = new TestCharacter(strangerVoice) { Id = "stranger" };
        var mind = new TestMind(owner);
        mind.SetSceneContextLoaderForTesting(() => new TestSceneContext([owner, speaker, stranger]));
        mind.AddChild(new SpeechPerception());
        int deliveries = 0;
        mind.DeliverySignalForTest(_ => deliveries++);
        List<SpeechPercept> percepts = [];
        hearing.Perceived += percept => percepts.Add(Assert.IsType<SpeechPercept>(percept));
        var root = new Node();
        root.AddChild(hearing);
        root.AddChild(ownerVoice);
        root.AddChild(source);
        root.AddChild(strangerVoice);
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        mind.AttentionDecayPerSecond = 0f;
        mind.ReinforceAttentionForTest(((IIdentifiable)speaker).FullId);

        using CancellationTokenSource cancellation = new();
        try
        {
            Task<MindBase.WaitOutcome> wait = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), cancellation.Token);
            source.EmitSpeechResumed(new SpeechSegmentMetadata("automatic-group", 1));
            strangerVoice.EmitSpeechResumed(new SpeechSegmentMetadata("stranger-group", 1));
            ownerVoice.EmitSpeechResumed(new SpeechSegmentMetadata("self-group", 1));
            await TestUtils.WaitForFramesAsync(tree, 2);

            SpeechSegmentLifecycleNotification notification = Assert.Single(mind.LifecycleNotifications);
            Assert.Equal("external-voice", notification.SourceVoiceID);
            Assert.Equal("automatic-group", notification.Metadata.SpeechGroupID);
            Assert.Equal(1, notification.Metadata.SegmentIndex);
            Assert.Equal(SpeechSegmentLifecycleTransition.Resumed, notification.Transition);
            Assert.False(wait.IsCompleted);
            Assert.Empty(percepts);
            Assert.Empty(mind.Timeline);
            Assert.Empty(mind.Ingested);
            Assert.Equal(0, deliveries);
        }
        finally
        {
            cancellation.Cancel();
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>
    /// The textless start cue forwards only for an attended external speaker, samples attention once at cue
    /// receipt, and never creates percept, hearing, timeline, wait, or delivery effects on its own.
    /// </summary>
    [Fact]
    public async Task SpeechLifecycle_StartedForwardsOnlyWhenAttendedAndSamplesAttentionAtCueReceipt()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var hearing = new Hearing();
        var ownerVoice = new TestVoice { Id = "owner-voice" };
        var source = new TestVoice { Id = "external-voice" };
        var strangerVoice = new TestVoice { Id = "stranger-voice" };
        var owner = new TestCharacter(hearing, ownerVoice);
        var speaker = new TestCharacter(source) { Id = "speaker" };
        var stranger = new TestCharacter(strangerVoice) { Id = "stranger" };
        var mind = new TestMind(owner);
        mind.SetSceneContextLoaderForTesting(() => new TestSceneContext([owner, speaker, stranger]));
        mind.AddChild(new SpeechPerception());
        int deliveries = 0;
        mind.DeliverySignalForTest(_ => deliveries++);
        List<SpeechPercept> percepts = [];
        hearing.Perceived += percept => percepts.Add(Assert.IsType<SpeechPercept>(percept));
        var root = new Node();
        root.AddChild(hearing);
        root.AddChild(ownerVoice);
        root.AddChild(source);
        root.AddChild(strangerVoice);
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        mind.AttentionDecayPerSecond = 0f;
        mind.ReinforceAttentionForTest(((IIdentifiable)speaker).FullId);

        using CancellationTokenSource cancellation = new();
        try
        {
            Task<MindBase.WaitOutcome> wait = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), cancellation.Token);
            source.EmitSpeechStarted(new SpeechSegmentMetadata("attended-group", 0));
            strangerVoice.EmitSpeechStarted(new SpeechSegmentMetadata("stranger-group", 0));
            ownerVoice.EmitSpeechStarted(new SpeechSegmentMetadata("self-group", 0));
            await TestUtils.WaitForFramesAsync(tree, 2);

            SpeechSegmentLifecycleNotification notification = Assert.Single(mind.LifecycleNotifications);
            Assert.Equal("external-voice", notification.SourceVoiceID);
            Assert.Equal("attended-group", notification.Metadata.SpeechGroupID);
            Assert.Equal(0, notification.Metadata.SegmentIndex);
            Assert.Equal(SpeechSegmentLifecycleTransition.Started, notification.Transition);
            Assert.False(wait.IsCompleted);
            Assert.Empty(percepts);
            Assert.Empty(mind.Timeline);
            Assert.Empty(mind.Ingested);
            Assert.Equal(0, deliveries);

            // Attention gained after an unattended cue never creates a retroactive hold: the stranger's earlier
            // group stays dropped while a fresh cue from the now-attended stranger forwards.
            mind.ReinforceAttentionForTest(((IIdentifiable)stranger).FullId);
            strangerVoice.EmitSpeechStarted(new SpeechSegmentMetadata("stranger-late", 0));
            await TestUtils.WaitForFramesAsync(tree, 2);

            Assert.Equal(
                ["attended-group", "stranger-late"],
                mind.LifecycleNotifications.Select(entry => entry.Metadata.SpeechGroupID));
            Assert.Equal(2, mind.LifecycleNotifications.Count);
            Assert.Empty(percepts);
            Assert.Empty(mind.Timeline);
        }
        finally
        {
            cancellation.Cancel();
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>Only textless terminal releases cross the transient lifecycle boundary; published speech does not.</summary>
    [Fact]
    public async Task SpeechLifecycle_TerminalSettlementsForwardOnceWithoutHearingOrTimelineEffects()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var hearing = new Hearing();
        var ownerVoice = new TestVoice { Id = "owner-voice" };
        var source = new TestVoice { Id = "external-voice" };
        var owner = new TestCharacter(hearing, ownerVoice);
        var speaker = new TestCharacter(source) { Id = "speaker" };
        var mind = new TestMind(owner);
        mind.SetSceneContextLoaderForTesting(() => new TestSceneContext([owner, speaker]));
        mind.AddChild(new SpeechPerception());
        List<SpeechPercept> percepts = [];
        hearing.Perceived += percept => percepts.Add(Assert.IsType<SpeechPercept>(percept));
        var root = new Node();
        root.AddChild(hearing);
        root.AddChild(ownerVoice);
        root.AddChild(source);
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        try
        {
            source.EmitSpeechSettlement("blank", 0, SpeechSegmentSettlementKind.Blank);
            source.EmitSpeechSettlement("failed", 1, SpeechSegmentSettlementKind.Failed);
            source.EmitSpeechSettlement("abandoned", 2, SpeechSegmentSettlementKind.Abandoned);
            source.EmitSpeechSettlement("published", 3, SpeechSegmentSettlementKind.Published);

            Assert.Equal(
                [
                    SpeechSegmentLifecycleTransition.Blank,
                    SpeechSegmentLifecycleTransition.Failed,
                    SpeechSegmentLifecycleTransition.Abandoned,
                ],
                mind.LifecycleNotifications.Select(notification => notification.Transition));
            Assert.Equal(["blank", "failed", "abandoned"], mind.LifecycleNotifications.Select(notification => notification.Metadata.SpeechGroupID));
            Assert.Empty(percepts);
            Assert.Empty(mind.Timeline);
            Assert.Empty(mind.Ingested);
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>Voice replacement and Mind exit detach transient lifecycle subscriptions without duplication.</summary>
    [Fact]
    public async Task SpeechLifecycle_VoiceReplacementAndMindExitDetachSubscriptions()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var ownerVoice = new TestVoice { Id = "owner-voice" };
        var firstSource = new TestVoice { Id = "first-source" };
        var replacementSource = new TestVoice { Id = "replacement-source" };
        var owner = new TestCharacter(ownerVoice);
        var speaker = new TestCharacter(firstSource) { Id = "speaker" };
        var mind = new TestMind(owner);
        mind.SetSceneContextLoaderForTesting(() => new TestSceneContext([owner, speaker]));
        var root = new Node();
        root.AddChild(ownerVoice);
        root.AddChild(firstSource);
        root.AddChild(replacementSource);
        root.AddChild(mind);
        AddToTree(tree, root);
        await TestUtils.WaitForFramesAsync(tree, 2);

        mind.AttentionDecayPerSecond = 0f;
        mind.ReinforceAttentionForTest(((IIdentifiable)speaker).FullId);

        try
        {
            firstSource.EmitSpeechResumed(new SpeechSegmentMetadata("first", 1));
            speaker.RefreshComponents(replacementSource);
            owner.RefreshComponents(ownerVoice);
            await TestUtils.WaitForFramesAsync(tree, 2);

            firstSource.EmitSpeechResumed(new SpeechSegmentMetadata("stale", 1));
            replacementSource.EmitSpeechResumed(new SpeechSegmentMetadata("replacement", 1));
            Assert.Equal(["first", "replacement"], mind.LifecycleNotifications.Select(notification => notification.Metadata.SpeechGroupID));

            root.RemoveChild(mind);
            replacementSource.EmitSpeechResumed(new SpeechSegmentMetadata("after-exit", 1));
            Assert.Equal(2, mind.LifecycleNotifications.Count);
            mind.QueueFree();
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    private static EquivalentObservation Equivalent(string scope, string value, Counter counter)
        => new(scope, value, () => counter.Value++);

    private static EquivalentActionObservation EquivalentAction(
        string? actorId,
        string scope,
        string value,
        Counter counter)
        => new(actorId, scope, value, () => counter.Value++);

    private static IReadOnlyList<string> Values(IReadOnlyList<AgentObservation> observations)
        => [.. observations.Select(observation => observation switch
        {
            TestObservation test => test.Value,
            EquivalentObservation equivalent => $"{equivalent.Scope}:{equivalent.Value}",
            _ => observation.TypeKey,
        })];

    private static void AssertActivationFails(TestCharacter owner, IReadOnlyList<IPerception> faculties)
    {
        var mind = new TestMind(owner);
        foreach (IPerception faculty in faculties)
        {
            mind.AddChild((Node)faculty);
        }

        _ = Assert.Throws<InvalidOperationException>(mind._Ready);
        Assert.All(owner.Components.OfType<TestSense>(), sense => Assert.Equal(0, sense.SubscriptionCount));
        mind.Free();
    }

    private static void AddToTree(SceneTree tree, Node node) => (tree.CurrentScene ?? tree.Root).AddChild(node);

    private record BasePercept(string Id) : IPercept;
    private sealed record FirstPercept(string Id) : BasePercept(Id);
    private sealed record SecondPercept(string Id) : BasePercept(Id);
    private sealed record UndeclaredPercept : IPercept;

    private sealed partial class TestSense(params Type[] perceptTypes) : Node, ISense
    {
        private Action<IPercept>? _perceived;
        public int SubscriptionCount
        {
            get; private set;
        }
        public event Action<IPercept>? Perceived
        {
            add
            {
                _perceived += value;
                SubscriptionCount++;
            }
            remove
            {
                _perceived -= value;
                SubscriptionCount--;
            }
        }
        public IReadOnlyList<Type> PerceptTypes { get; } = perceptTypes;
        public void Publish(IPercept percept) => _perceived?.Invoke(percept);
    }

    private sealed class Counter
    {
        public int Value
        {
            get; set;
        }
    }

    private sealed class CountingGameClock : IGameClock
    {
        public double CurrentSeconds
        {
            get; set;
        }
        public int ReadCount
        {
            get; private set;
        }

        public double NowSeconds
        {
            get
            {
                ReadCount++;
                return CurrentSeconds;
            }
        }
    }

    private sealed class TestCharacter(params IComponent[] components) : ICharacter, IComponentProjectionNotifier
    {
        private IComponent[] _components = components;
        private Action? _componentsRefreshed;

        public string Id { get; set; } = "owner";
        public IReadOnlyList<IComponent> Components => _components;
        public bool HasComponentProjection { get; private set; } = true;
        public IReadOnlyList<VisualCue> VisualCues => [];
        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
        public event Action? ComponentsRefreshed
        {
            add => _componentsRefreshed += value;
            remove => _componentsRefreshed -= value;
        }
        public void RefreshComponents(params IComponent[] components)
        {
            _components = components;
            HasComponentProjection = true;
            _componentsRefreshed?.Invoke();
        }
    }

    private sealed partial class TestMind : MindBase
    {
        private readonly ICharacter _owner;

        public TestMind(ICharacter owner)
        {
            _owner = owner;
            SpeechSegmentLifecycleNotified += LifecycleNotifications.Add;
        }

        public List<AgentObservation> Ingested { get; } = [];
        public List<SpeechSegmentLifecycleNotification> LifecycleNotifications { get; } = [];
        public IReadOnlyList<AgentObservation> Timeline => GetObservationTimelineSnapshot();
        public IReadOnlyList<AgentObservation> Retained =>
            [.. GetRetainedObservationSnapshot().Select(static entry => entry.Payload)];
        public void IngestToolObservationsForTest(IReadOnlyList<AgentObservation> observations)
            => IngestToolObservations(observations);
        public void DeliverySignalForTest(Action<ObservationDeliverySignal> handler)
            => ObservationDeliverySignalled += handler;
        public void ObserveForTest(AgentObservation observation) => Observe(observation);
        public Task<WaitOutcome> WaitForNotableForTestAsync(TimeSpan maxWait, CancellationToken cancellationToken)
            => WaitForNotableObservationsAsync(maxWait, cancellationToken);
        public void ReinforceAttentionForTest(string fullId)
            => ReinforceAttention(fullId, 1f, AttentionSettings.Create(1f, 0f, 0.05f, 0.25f));
        protected override ICharacter ResolveOwningCharacter() => _owner;
        protected override IEnumerable<IObservationLifetimePolicy> CreateLifetimePolicies()
            => [.. base.CreateLifetimePolicies(), new EquivalentActionLifetimePolicy()];
        protected override void OnObservationIngested(AgentObservation observation) => Ingested.Add(observation);
    }

    private sealed partial class RecordingFaculty<TPercept>(string prefix) : Perception<TPercept>
        where TPercept : BasePercept
    {
        public override ValueTask PerceiveAsync(
            TPercept percept,
            PerceptionContext context,
            CancellationToken cancellationToken)
        {
            Emit(new TestObservation($"{prefix}:{percept.Id}", 0f));
            return ValueTask.CompletedTask;
        }
    }

    private sealed partial class DelegatingFaculty<TPercept>(
        Func<TPercept, PerceptionContext, CancellationToken, ValueTask> handler) : Perception<TPercept>
        where TPercept : IPercept
    {
        public void EmitForTest(AgentObservation observation) => Emit(observation);

        public override ValueTask PerceiveAsync(
            TPercept percept,
            PerceptionContext context,
            CancellationToken cancellationToken)
            => handler(percept, context, cancellationToken);
    }

    private sealed partial class BatchFaculty : Perception<FirstPercept>
    {
        public IReadOnlyList<AgentObservation> Observations { get; set; } = [];

        public override ValueTask PerceiveAsync(
            FirstPercept percept,
            PerceptionContext context,
            CancellationToken cancellationToken)
        {
            foreach (AgentObservation observation in Observations)
            {
                Emit(observation);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed partial class HearingFaculty : Perception<SpeechPercept>
    {
        public override ValueTask PerceiveAsync(
            SpeechPercept percept,
            PerceptionContext context,
            CancellationToken cancellationToken)
        {
            Emit(new ObservedSpeech(null, percept.Content));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record TestObservation(string Value, float Importance) : AgentObservation
    {
        public override string TypeKey => "test";
        public override float CalculateImportance(ObservationContext context) => Importance;
    }

    private sealed record FaultingObservation : AgentObservation
    {
        public override string TypeKey => "fault.test";
        public override float CalculateImportance(ObservationContext context)
            => throw new InvalidOperationException("expected fault");
    }

    /// <summary>
    /// Arbitrary non-speech observation supplying an identity tuple through the generic commit-identity contract
    /// (AI-001 TR-9), proving the gate never depends on concrete observation types.
    /// </summary>
    private sealed record CommitIdentifiedObservation(string Stream, long Turn, string Value) : AgentObservation, IHasCommitIdentity
    {
        public ObservationCommitIdentity? CommitIdentity => new(Stream, Turn);
        public override string TypeKey => "commit.test";
        public override float CalculateImportance(ObservationContext context) => 1f;
    }

    private sealed record AttentionObservation(string SubjectId, float Contribution) : AgentObservation
    {
        public override string TypeKey => "attention.test";
        public override float CalculateImportance(ObservationContext context) => 0f;

        public override IReadOnlyList<AttentionEffect> GetAttentionEffects(ObservationContext context)
            => [new AttentionEffect(SubjectId, Contribution)];
    }

    private sealed record EquivalentObservation(
        string Scope,
        string Value,
        Action ImportanceCalculated) : AgentObservation
    {
        public override string TypeKey => "equivalent.test";
        public override float CalculateImportance(ObservationContext context)
        {
            ImportanceCalculated();
            return 0f;
        }
        public override bool IsSemanticallyEquivalentTo(AgentObservation other)
            => other is EquivalentObservation equivalent
                && string.Equals(Scope, equivalent.Scope, StringComparison.Ordinal)
                && string.Equals(Value, equivalent.Value, StringComparison.Ordinal);
    }

    private sealed record EquivalentActionObservation(
        string? ActorId,
        string Scope,
        string Value,
        Action ImportanceCalculated) : ObservedAction(ActorId)
    {
        public override string TypeKey => "equivalent.action.test";
        public override float CalculateImportance(ObservationContext context)
        {
            ImportanceCalculated();
            return 1f;
        }
        public override bool IsSemanticallyEquivalentTo(AgentObservation other)
            => other is EquivalentActionObservation equivalent
                && string.Equals(ActorId, equivalent.ActorId, StringComparison.Ordinal)
                && string.Equals(Scope, equivalent.Scope, StringComparison.Ordinal)
                && string.Equals(Value, equivalent.Value, StringComparison.Ordinal);
    }

    /// <summary>Test-only policy proving source-neutral ingestion delegates duplicate semantics to policy ownership.</summary>
    private sealed class EquivalentActionLifetimePolicy : IObservationLifetimePolicy
    {
        public Type DeclaredConcreteType => typeof(EquivalentActionObservation);

        public ObservationLifetimePolicyDecision Evaluate(
            AgentObservation candidate,
            IReadOnlyList<AcceptedObservationEntry> activeEntries,
            double observedAtSeconds)
        {
            _ = observedAtSeconds;
            EquivalentActionObservation action = Assert.IsType<EquivalentActionObservation>(candidate);
            return activeEntries.Any(entry => action.IsSemanticallyEquivalentTo(entry.Payload))
                ? ObservationLifetimePolicyDecision.Suppressed
                : ObservationLifetimePolicyDecision.Accept;
        }

        public bool IsEventEligible(AgentObservation observation)
        {
            ArgumentNullException.ThrowIfNull(observation);
            return true;
        }

        public bool HasFiniteLifetime => false;

        public bool IsExpired(AcceptedObservationEntry entry, double nowSeconds)
        {
            ArgumentNullException.ThrowIfNull(entry);
            _ = nowSeconds;
            return false;
        }
    }

    private sealed partial class TestVoice : Voice
    {
        public override void Speak(string speech) => base.Speak(speech);
        public void PublishCompletedSpeech(string speech, SpeechSegmentMetadata? metadata = null) => PublishSpeech(speech, metadata);
        public void EmitSpeechStarted(SpeechSegmentMetadata metadata) => RaiseSpeechSegmentStarted(metadata);

        public void EmitSpeechResumed(SpeechSegmentMetadata metadata) => RaiseSpeechResumed(metadata);
        public void EmitSpeechSettlement(string groupId, int segmentIndex, SpeechSegmentSettlementKind kind)
            => RaiseSpeechSegmentSettled(new SpeechSegmentSettlement(new SpeechSegmentMetadata(groupId, segmentIndex), kind));
    }

    private sealed record TestSceneContext(IReadOnlyCollection<ICharacter> Characters) : ISceneContext
    {
        public ICharacter Player => throw new InvalidOperationException("The test scene has no player.");
        public ContentContext Content => ContentContext.Default;
        public IIdentifiable? Find(string fullId)
            => Characters.FirstOrDefault(character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));
        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException();
    }
}
