using System.Text;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.AI.Watch;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Mind.Perception;
using AlleyCat.Scene;
using AlleyCat.Speech;
using AlleyCat.Speech.Generation;
using AlleyCat.Speech.LipSync;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Godot-runtime coverage for the AgenticMind session lifecycle: fire-and-forget start with containment, the
/// urgency-aware scheduling bridge — ordinary pressure versus fresh-turn invalidation, and
/// wait-owned fresh delivery through the wait's natural result — and node-exit cancellation of generation and tool
/// work.
/// </summary>
[Headless]
public sealed partial class AgenticMindSessionLifecycleIntegrationTests
{
    /// <summary>
    /// Missing session configuration is contained: the failure is logged once, the session never issues a
    /// request, and it stays ended for the node's remaining lifetime (AI-002 TR-1).
    /// </summary>
    [Fact]
    public async Task SessionFailure_WithMissingConfiguration_IsContainedLoggedOnceAndStaysEnded()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        ScriptedSessionClientProvider clientProvider = new();
        TestAgenticMind mind = new(owner)
        {
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await WaitUntilAsync(
                sceneTree,
                () => loggerProvider.Entries.Any(entry =>
                    entry.Level == LogLevel.Error
                    && entry.Exception is InvalidOperationException
                    && entry.Exception.Message.Contains("SystemInstruction prompt stack", StringComparison.Ordinal)));
            mind.ObserveForTest(new TestObservation(1f, "after-failure"));
            await TestUtils.WaitForFramesAsync(sceneTree, 6);

            _ = Assert.Single(loggerProvider.Entries, entry =>
                    entry.Level == LogLevel.Error
                    && entry.Exception is InvalidOperationException);
            Assert.Empty(clientProvider.Requests);
            Assert.Empty(mind.GetLatestRenderContext());
            Assert.Equal(["after-failure"], TimelineValues(mind));
        }
        finally
        {
            mind.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// An ordinary notable signal during held generation never cancels the request. The next natural request renders
    /// it only in the canonical event-timeline prefix, with no scheduling payload (AI-002 TR-7–10).
    /// </summary>
    [Fact]
    public async Task NotableSignal_DuringGeneration_CompletesGenerationAndRendersAtNextContextBoundary()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseGeneration = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CapturingTool tool = new();
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHoldUntilReleasedCall(firstRequestStarted, releaseGeneration, "capture_context");
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            Tools = [tool],
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            mind.ObserveForTest(new TestObservation(1f, "bridge"));
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            // No cancellation and no separate delivery request: the held generation is still the only request.
            _ = Assert.Single(clientProvider.Requests);
            _ = releaseGeneration.TrySetResult();

            await WaitUntilAsync(sceneTree, () => tool.CapturedContexts.Count == 1);
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            IReadOnlyList<ChatMessage> nextRequest = clientProvider.Requests[1];
            // The naturally-next request puts its newly rendered event timeline and current scene ahead of bootstrap
            // and the accepted replay. Scheduling contributed no observation payload (AI-002 TR-2/7–10).
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
                nextRequest.Select(message => message.Role));
            Assert.Contains("--- New Since Your Previous Response ---", nextRequest[0].Text, StringComparison.Ordinal);
            Assert.Contains("- bridge", nextRequest[0].Text, StringComparison.Ordinal);
            Assert.Equal(AgenticMind.SessionBootstrapInput, nextRequest[2].Text);

            // Node exit ends the held second request quietly.
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);

            Assert.Equal(2, clientProvider.Requests.Count);
            Assert.Equal(["bridge"], TimelineValues(mind));
        }
        finally
        {
            mind.Free();
            tool.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// Non-self observed speech during held generation requires a fresh turn immediately. Its replacement request
    /// carries the rendered speech only in its event-timeline message (AI-002 UR-5, TR-2/7–10).
    /// </summary>
    [Fact]
    public async Task ExternalSpeech_DuringGeneration_InvalidatesStaleGenerationImmediately()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            mind.ObserveForTest(new ObservedSpeech("char:someone-else", "You there?"));

            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
            IReadOnlyList<ChatMessage> freshRequest = clientProvider.Requests[1];
            // The fresh replacement request starts with canonical timeline and scene messages, then bootstrap. It
            // contains no scheduling payload (AI-002 TR-2/7–10).
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User],
                freshRequest.Select(message => message.Role));
            Assert.Contains("--- New Since Your Previous Response ---", freshRequest[0].Text, StringComparison.Ordinal);
            Assert.Contains("Heard char:someone-else say: You there?", freshRequest[0].Text, StringComparison.Ordinal);
            Assert.Equal(AgenticMind.SessionBootstrapInput, freshRequest[2].Text);

            // Node exit ends the held replacement request quietly.
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
        }
        finally
        {
            mind.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// A textless continued-segment resume crosses the real Voice-to-Mind subscription boundary, cancels the
    /// stale request, and holds the replacement until its own grouped completion has been perceived. Unrelated
    /// group/index settlements and published settlement never release the lease (AI-002 TR-4/5, TR-11).
    /// </summary>
    [Fact]
    public async Task GroupedContinuation_ResumeCancelsAndHoldsUntilMatchingJoinedCompletion()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        TestVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        TestCharacter owner = new(hearing, ownerVoice);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = CreateVoiceRoutedMind(owner, speaker, player, clientProvider);
        List<SpeechPercept> percepts = [];
        hearing.Perceived += percept => percepts.Add(Assert.IsType<SpeechPercept>(percept));
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.EmitSpeechResumed(new SpeechSegmentMetadata("continued", 1));

            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
            await TestUtils.WaitForFramesAsync(sceneTree, 3);
            _ = Assert.Single(clientProvider.Requests);
            Assert.Empty(percepts);
            Assert.Empty(mind.GetTimelineForTest());

            // Other-group, other-index, and Published are all non-matching lifecycle traffic. They must not
            // release the active continued segment before text has completed.
            source.EmitSpeechResumed(new SpeechSegmentMetadata("other", 1));
            source.EmitSpeechSettlement(new SpeechSegmentMetadata("other", 1), SpeechSegmentSettlementKind.Blank);
            source.EmitSpeechSettlement(new SpeechSegmentMetadata("continued", 2), SpeechSegmentSettlementKind.Failed);
            source.EmitSpeechSettlement(new SpeechSegmentMetadata("continued", 1), SpeechSegmentSettlementKind.Published);
            source.PublishCompletedSpeech("late prefix", new SpeechSegmentMetadata("continued", 0));
            await mind.DrainPerceptionsForTestAsync();
            await TestUtils.WaitForFramesAsync(sceneTree, 3);
            _ = Assert.Single(clientProvider.Requests);

            source.PublishCompletedSpeech("continued text", new SpeechSegmentMetadata("continued", 1));
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 2);

            IReadOnlyList<ChatMessage> replacement = clientProvider.Requests[1];
            Assert.Equal([ChatRole.User, ChatRole.User, ChatRole.User], replacement.Select(static message => message.Role));
            ChatMessage timeline = replacement[0];
            Assert.Contains("Heard char:speaker say: late prefix … continued text", timeline.Text, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(timeline.Text, "late prefix … continued text"));
            Assert.Equal(
                ["late prefix", "continued text"],
                mind.GetTimelineForTest().OfType<ObservedSpeech>().Select(speech => speech.Content));
        }
        finally
        {
            root.QueueFree();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// A textless onset cue crosses the real Voice-to-Mind subscription boundary, cancels the stale request, and
    /// holds the replacement until its own grouped completion has been perceived. Unrelated group settlements and
    /// the published settlement never release the lease (AI-002 TR-4/5, TR-11).
    /// </summary>
    [Fact]
    public async Task StartedContinuation_OnsetCancelsAndHoldsUntilMatchingJoinedCompletion()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        TestVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        TestCharacter owner = new(hearing, ownerVoice);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = CreateVoiceRoutedMind(owner, speaker, player, clientProvider);
        List<SpeechPercept> percepts = [];
        hearing.Perceived += percept => percepts.Add(Assert.IsType<SpeechPercept>(percept));
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.EmitSpeechStarted(new SpeechSegmentMetadata("onset-group", 0));

            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
            await TestUtils.WaitForFramesAsync(sceneTree, 3);
            _ = Assert.Single(clientProvider.Requests);
            Assert.Empty(percepts);
            Assert.Empty(mind.GetTimelineForTest());

            // Other-group traffic and the published settlement are non-matching: they must not release the pending
            // onset before text has completed.
            source.EmitSpeechSettlement(new SpeechSegmentMetadata("other-group", 0), SpeechSegmentSettlementKind.Blank);
            source.EmitSpeechSettlement(new SpeechSegmentMetadata("onset-group", 0), SpeechSegmentSettlementKind.Published);
            await mind.DrainPerceptionsForTestAsync();
            await TestUtils.WaitForFramesAsync(sceneTree, 3);
            _ = Assert.Single(clientProvider.Requests);

            source.PublishCompletedSpeech("onset text", new SpeechSegmentMetadata("onset-group", 0));
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 2);

            IReadOnlyList<ChatMessage> replacement = clientProvider.Requests[1];
            ChatMessage joined = GetEventTimelineMessage(replacement);
            Assert.Contains("Heard char:speaker say: onset text", joined.Text, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(joined.Text, "Heard char:speaker say: onset text"));
        }
        finally
        {
            root.QueueFree();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// A duplicate onset cue for the same identity is idempotent: no second lease is stacked, so a single blank
    /// settlement releases the hold and the replacement request proceeds.
    /// </summary>
    [Fact]
    public async Task StartedContinuation_DuplicateOnsetCueIsIdempotent()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        TestVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        TestCharacter owner = new(hearing, ownerVoice);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = CreateVoiceRoutedMind(owner, speaker, player, clientProvider);
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.EmitSpeechStarted(new SpeechSegmentMetadata("onset-group", 0));
            source.EmitSpeechStarted(new SpeechSegmentMetadata("onset-group", 0));
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);

            // One blank settlement releases every lease the two identical cues created: a stacked second lease
            // would keep the replacement request held behind the rendering barrier.
            source.EmitSpeechSettlement(new SpeechSegmentMetadata("onset-group", 0), SpeechSegmentSettlementKind.Blank);
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 2);
            Assert.Equal([ChatRole.User, ChatRole.User, ChatRole.User], clientProvider.Requests[1].Select(message => message.Role));
            Assert.Equal(AgenticMind.SessionBootstrapInput, clientProvider.Requests[1][2].Text);
            Assert.Empty(mind.GetTimelineForTest());
        }
        finally
        {
            root.QueueFree();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// A manual synthetic token's hold completes when the source voice's ungrouped speech observation is delivered:
    /// the replacement request carries the rendered speech exactly once while the public observation stays
    /// ungrouped.
    /// </summary>
    [Fact]
    public async Task StartedContinuation_ManualSyntheticToken_CompletesThroughUngroupedSpeechDelivery()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        TestVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        TestCharacter owner = new(hearing, ownerVoice);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = CreateVoiceRoutedMind(owner, speaker, player, clientProvider);
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.EmitSpeechStarted(new SpeechSegmentMetadata("synthetic-manual-token", 0));
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
            await TestUtils.WaitForFramesAsync(sceneTree, 3);
            _ = Assert.Single(clientProvider.Requests);
            Assert.Empty(mind.GetTimelineForTest());

            // Manual completed speech publishes ungrouped: no segment metadata reaches hearing or the timeline.
            source.PublishCompletedSpeech("manual speech");
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 2);

            IReadOnlyList<ChatMessage> replacement = clientProvider.Requests[1];
            ChatMessage delivered = GetEventTimelineMessage(replacement);
            Assert.Contains("Heard char:speaker say: manual speech", delivered.Text, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(delivered.Text, "Heard char:speaker say: manual speech"));

            ObservedSpeech ungrouped = Assert.IsType<ObservedSpeech>(Assert.Single(mind.GetTimelineForTest()));
        }
        finally
        {
            root.QueueFree();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// A start cue that arrives after its group's completed speech already committed and its delivery was consumed
    /// cannot settle through delivery: the settled-hold watchdog releases the lease textlessly on the cue's own
    /// sweep, so the runner issues its next model request with no further settlement cue and without duplicating
    /// the already-delivered speech (AI-002 TR-11 liveness).
    /// </summary>
    [Fact]
    public async Task StartedContinuation_CueAfterSpeechAlreadyDelivered_WatchdogReleasesHoldTextlessly()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        TestVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        TestCharacter owner = new(hearing, ownerVoice);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        clientProvider.EnqueueHoldForever();
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = CreateVoiceRoutedMind(owner, speaker, player, clientProvider);
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Completed grouped speech commits while the first generation is held: the fresh delivery cancels the
            // held request, the replacement request consumes the deliverable window, and the record settles into
            // the timeline — all before any cue exists (the reverse of production's cue-first ordering).
            source.PublishCompletedSpeech("late text", new SpeechSegmentMetadata("late-group", 0));
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 2);
            await WaitUntilAsync(sceneTree, () => mind.GetTimelineForTest().Count == 1);

            IReadOnlyList<ChatMessage> delivered = clientProvider.Requests[1];
            ChatMessage heard = GetEventTimelineMessage(delivered);
            Assert.Contains("Heard char:speaker say: late text", heard.Text, StringComparison.Ordinal);
            ObservedSpeech committed = Assert.IsType<ObservedSpeech>(Assert.Single(mind.GetTimelineForTest()));
            Assert.Equal("late text", committed.Content);

            // The onset cue now arrives for speech that already committed and left the deliverable window. No
            // settlement and no further completed speech follows: only the watchdog's sweep can release the hold.
            source.EmitSpeechStarted(new SpeechSegmentMetadata("late-group", 0));
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 3);

            // The next model request replays the cancelled request's mutable turn exactly once — bootstrap plus
            // one heard-speech turn — with no fabricated duplicate for the group.
            IReadOnlyList<ChatMessage> swept = clientProvider.Requests[2];
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User],
                swept.Select(message => message.Role));
            Assert.Equal(AgenticMind.SessionBootstrapInput, swept[2].Text);
            ChatMessage replayed = GetEventTimelineMessage(swept);
            Assert.Equal(1, CountOccurrences(replayed.Text, "Heard char:speaker say: late text"));
            _ = Assert.Single(mind.GetTimelineForTest());
        }
        finally
        {
            root.QueueFree();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// A cue from a speaker the Mind does not attend registers nothing and cancels nothing: generation and the
    /// tool batch continue through the cue, no hold appears, and the speaker's completed
    /// speech still arrives later as an ordinary all-hearer fresh turn (SPCH-005 TR-37, AI-002 UR-5).
    /// </summary>
    [Fact]
    public async Task StartedContinuation_FromUnattendedSpeaker_RegistersNothingAndCancelsNothing()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        TestVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        TestCharacter owner = new(hearing, ownerVoice);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseGeneration = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHoldUntilReleasedCallBatch(
            firstRequestStarted,
            releaseGeneration,
            new FunctionCallContent("after-call", "speak", new Dictionary<string, object?> { ["speech"] = "After" }));
        clientProvider.EnqueueHoldForever();
        // No attention reinforcement: the speaker stays outside this Mind's attention snapshot, so its cues are
        // never forwarded (SPCH-005 TR-37).
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, speaker, player]));
        mind.AddChild(new SpeechPerception());
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.EmitSpeechResumed(new SpeechSegmentMetadata("unattended-group", 1));
            await TestUtils.WaitForFramesAsync(sceneTree, 6);

            // The cue suppressed nothing: the request still holds without cancellation, no hold exists, and the
            // delivery window stays empty.
            Assert.False(clientProvider.EndedByCancellation());
            _ = Assert.Single(clientProvider.Requests);
            Assert.Empty(mind.GetTimelineForTest());

            // Generation and its tool batch complete normally through the cue.
            _ = releaseGeneration.TrySetResult();
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 2);
            Assert.False(clientProvider.EndedByCancellation());

            // The unattended speaker's completed speech is ordinary all-hearer freshness: it invalidates the
            // following held request and delivers one joined replacement turn.
            source.PublishCompletedSpeech("unattended words", new SpeechSegmentMetadata("unattended-group", 1));
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 3);

            ChatMessage joined = GetEventTimelineMessage(clientProvider.Requests[2]);
            Assert.Contains("Heard char:speaker say: unattended words", joined.Text, StringComparison.Ordinal);
        }
        finally
        {
            root.QueueFree();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// A shared speaker onset is conversation-scoped, never room-wide: of three Minds hearing the same source
    /// voice, only the two attending the speaker cancel their in-flight request and hold their replacement
    /// behind their own cue lease — each independently, on its own session — while the unattended Mind's
    /// generation and tool batch continue through the cue, and the speaker's later completed speech still
    /// reaches all three Minds as ordinary fresh observations (SPCH-005 TR-37, AI-002 UR-5/TR-11).
    /// </summary>
    [Fact]
    public async Task SharedSpeakerOnset_SuppressesAttendedMindsIndependentlyWhileUnattendedMindContinues()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearingA = new();
        Hearing hearingB = new();
        Hearing hearingC = new();
        TestVoice ownerVoiceA = new()
        {
            Id = "voice-a",
        };
        TestVoice ownerVoiceB = new()
        {
            Id = "voice-b",
        };
        TestVoice ownerVoiceC = new()
        {
            Id = "voice-c",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        TestCharacter ownerA = new(hearingA, ownerVoiceA)
        {
            Id = "npc_a",
        };
        TestCharacter ownerB = new(hearingB, ownerVoiceB)
        {
            Id = "npc_b",
        };
        TestCharacter ownerC = new(hearingC, ownerVoiceC)
        {
            Id = "npc_c",
        };
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        CapturingTool continuingTool = new();
        TaskCompletionSource requestAStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource requestBStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource requestCStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseUnattendedGeneration = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProviderA = new();
        clientProviderA.EnqueueHold(requestAStarted);
        clientProviderA.EnqueueHoldForever();
        ScriptedSessionClientProvider clientProviderB = new();
        clientProviderB.EnqueueHold(requestBStarted);
        clientProviderB.EnqueueHoldForever();
        ScriptedSessionClientProvider clientProviderC = new();
        clientProviderC.EnqueueHoldUntilReleasedCallBatch(
            requestCStarted,
            releaseUnattendedGeneration,
            new FunctionCallContent("after-cue-call", "capture_context", new Dictionary<string, object?>()));
        clientProviderC.EnqueueHoldForever();
        TestAgenticMind attendedMindA = CreateVoiceRoutedMind(ownerA, speaker, player, clientProviderA);
        TestAgenticMind attendedMindB = CreateVoiceRoutedMind(ownerB, speaker, player, clientProviderB);
        // No attention reinforcement for the third Mind: the speaker stays outside its attention snapshot, so
        // the shared cue is never forwarded to it (SPCH-005 TR-37).
        TestAgenticMind unattendedMindC = CreateVoiceRoutedMind(
            ownerC,
            speaker,
            player,
            clientProviderC,
            extraSceneMember: null,
            attendSpeaker: false,
            tools: [continuingTool]);
        List<SpeechPercept> perceptsA = [];
        List<SpeechPercept> perceptsB = [];
        List<SpeechPercept> perceptsC = [];
        hearingA.Perceived += percept => perceptsA.Add(Assert.IsType<SpeechPercept>(percept));
        hearingB.Perceived += percept => perceptsB.Add(Assert.IsType<SpeechPercept>(percept));
        hearingC.Perceived += percept => perceptsC.Add(Assert.IsType<SpeechPercept>(percept));
        Node root = AddSharedVoiceRoute(
            sceneTree,
            source,
            (hearingA, ownerVoiceA, attendedMindA),
            (hearingB, ownerVoiceB, attendedMindB),
            (hearingC, ownerVoiceC, unattendedMindC));

        try
        {
            await requestAStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await requestBStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await requestCStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // One onset cue with a real group id reaches every subscribed Mind through the same source voice.
            source.EmitSpeechStarted(new SpeechSegmentMetadata("shared-group", 0));

            // Each attended Mind suppressed independently: its own session cancelled its own request, and its
            // own hold keeps the replacement request from issuing while the hold is pending.
            await WaitUntilAsync(sceneTree, clientProviderA.EndedByCancellation);
            await WaitUntilAsync(sceneTree, clientProviderB.EndedByCancellation);
            await TestUtils.WaitForFramesAsync(sceneTree, 3);
            _ = Assert.Single(clientProviderA.Requests);
            _ = Assert.Single(clientProviderB.Requests);
            Assert.Empty(attendedMindA.GetTimelineForTest());
            Assert.Empty(attendedMindB.GetTimelineForTest());
            Assert.Empty(perceptsA);
            Assert.Empty(perceptsB);

            // The unattended Mind's work is untouched by the cue: no cancellation, no hold, and its held
            // generation plus tool batch run to completion once released.
            Assert.False(clientProviderC.EndedByCancellation());
            _ = Assert.Single(clientProviderC.Requests);
            _ = releaseUnattendedGeneration.TrySetResult();
            await WaitUntilAsync(sceneTree, () => clientProviderC.Requests.Count == 2);
            Assert.False(clientProviderC.EndedByCancellation());
            _ = Assert.Single(continuingTool.CapturedContexts);
            Assert.Empty(unattendedMindC.GetTimelineForTest());
            Assert.Empty(perceptsC);

            // Completed speech is attention-independent all-hearer freshness: every Mind — attended or not —
            // receives the same speech and turns it into one fresh replacement observation.
            source.PublishCompletedSpeech("shared room words", new SpeechSegmentMetadata("shared-group", 0));
            await attendedMindA.DrainPerceptionsForTestAsync();
            await attendedMindB.DrainPerceptionsForTestAsync();
            await unattendedMindC.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => clientProviderA.Requests.Count == 2);
            await WaitUntilAsync(sceneTree, () => clientProviderB.Requests.Count == 2);
            await WaitUntilAsync(sceneTree, () => clientProviderC.Requests.Count == 3);

            (ScriptedSessionClientProvider ClientProvider, int ReplacementIndex)[] replacements =
                [(clientProviderA, 1), (clientProviderB, 1), (clientProviderC, 2)];
            foreach ((ScriptedSessionClientProvider clientProvider, int replacementIndex) in replacements)
            {
                ChatMessage joined = Assert.Single(
                    clientProvider.Requests[replacementIndex],
                    message => message.Role == ChatRole.User && message.Text.StartsWith("Established Event History:", StringComparison.Ordinal));
                Assert.Contains("Heard char:speaker say: shared room words", joined.Text, StringComparison.Ordinal);
                Assert.Equal(1, CountOccurrences(joined.Text, "Heard char:speaker say: shared room words"));
            }

            (TestAgenticMind Mind, IReadOnlyList<SpeechPercept> Percepts)[] hearers =
                [(attendedMindA, perceptsA), (attendedMindB, perceptsB), (unattendedMindC, perceptsC)];
            foreach ((TestAgenticMind mind, IReadOnlyList<SpeechPercept> percepts) in hearers)
            {
                ObservedSpeech observed = Assert.IsType<ObservedSpeech>(
                    Assert.Single(mind.GetTimelineForTest()));
                Assert.Equal(speaker.FullId, observed.ActorId);
                Assert.Equal("shared room words", observed.Content);
                SpeechPercept percept = Assert.Single(percepts);
                Assert.Equal("shared room words", percept.Content);
            }
        }
        finally
        {
            root.QueueFree();
            continuingTool.Free();
            clientProviderA.Free();
            clientProviderB.Free();
            clientProviderC.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// An attended onset that wins before speak admission refuses it with zero side effects: no TTS request, no
    /// queue item, no hearing event, and no self-observation exist, and the refused batch — whose assistant call
    /// received the non-throwing not-delivered result before settlement — is disposed whole, leaving no protocol
    /// trace in the replacement request, which waits behind the cue's hold until the player's completed speech
    /// settles it (AI-002 TR-17/22; SPCH-005 TR-37).
    /// </summary>
    [Fact]
    public async Task SpeechAdmission_CueFirst_RefusesAdmissionWithZeroSideEffects()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        TestVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        SessionSpeechGenerator speechGenerator = new()
        {
            NextResult = CreateWaveFileBytes([0x34, 0x12], 16000),
        };
        StubSessionLipSyncPlayer lipSyncPlayer = new();
        SessionAIVoice ownerAiVoice = new()
        {
            Id = "owner-voice",
            SpeechGenerator = speechGenerator,
            LipSyncPlayer = lipSyncPlayer,
        };
        TestCharacter owner = new(hearing, ownerAiVoice);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSpeakCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHoldUntilReleasedCallBatch(
            firstRequestStarted,
            releaseSpeakCall,
            new FunctionCallContent("speak-call", "speak", new Dictionary<string, object?> { ["speech"] = "Hi there" }));
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = CreateVoiceRoutedMind(owner, speaker, player, clientProvider);
        List<SpeechPercept> percepts = [];
        hearing.Perceived += percept => percepts.Add(Assert.IsType<SpeechPercept>(percept));
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind, speechGenerator, lipSyncPlayer, ownerAiVoice);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The attended speaker's window opens before the speak call is released, so the submission blocks in
            // the turn-taking guard — selected but never admitted.
            source.BeginSpeechWindow();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            _ = releaseSpeakCall.TrySetResult();
            await TestUtils.WaitForFramesAsync(sceneTree, 6);

            source.EmitSpeechStarted(new SpeechSegmentMetadata("onset-group", 0));
            await TestUtils.WaitForFramesAsync(sceneTree, 6);

            Assert.Equal(0, speechGenerator.GenerateCallCount);
            Assert.Equal(0, ownerAiVoice.HandOffCount);
            Assert.False(ownerAiVoice.IsSpeaking);
            Assert.Empty(percepts);
            Assert.Empty(mind.GetTimelineForTest());
            _ = Assert.Single(clientProvider.Requests);

            source.PublishCompletedSpeech("player words", new SpeechSegmentMetadata("onset-group", 0));
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 2);

            Assert.Equal(0, speechGenerator.GenerateCallCount);
            Assert.Equal(0, ownerAiVoice.HandOffCount);
            // The refused speak batch settled and was disposed: the replacement request carries no protocol trace
            // of it — no assistant call and no not-delivered tool result — and the refused speech left no timeline
            // entry (AI-002 TR-17/22).
            IReadOnlyList<ChatMessage> replacement = clientProvider.Requests[1];
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User],
                replacement.Select(static message => message.Role));
            Assert.Empty(replacement.SelectMany(static message => message.Contents.OfType<FunctionResultContent>()));
            Assert.DoesNotContain(
                mind.GetTimelineForTest(),
                observation => observation is ObservedSpeech speech && speech.ActorId == owner.FullId);
            ChatMessage joined = Assert.Single(
                clientProvider.Requests[1],
                message => message.Role == ChatRole.User && message.Text.StartsWith("Established Event History:", StringComparison.Ordinal));
            Assert.Contains("Heard char:speaker say: player words", joined.Text, StringComparison.Ordinal);
        }
        finally
        {
            root.QueueFree();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// A submission admitted before the attended onset is protected for its whole pipeline life: the cue's hold
    /// never cancels the in-flight TTS, playback hand-off commits exactly one self-observation, and the next model
    /// request stays blocked until the player's completed speech settles the hold (SPCH-005 TR-25/37). The settled
    /// speak exchange is disposed — the committed speech reaches the model exactly once as its own timeline event
    /// (AI-002 TR-22).
    /// </summary>
    [Fact]
    public async Task SpeechAdmission_AdmissionFirst_ProtectedSpeakSettlesNaturallyAndHoldsNextRequest()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        TestVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        TaskCompletionSource<byte[]> generationHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionSpeechGenerator speechGenerator = new()
        {
            PendingResult = generationHeld,
        };
        StubSessionLipSyncPlayer lipSyncPlayer = new();
        SessionAIVoice ownerAiVoice = new()
        {
            Id = "owner-voice",
            SpeechGenerator = speechGenerator,
            LipSyncPlayer = lipSyncPlayer,
        };
        TestCharacter owner = new(hearing, ownerAiVoice);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueCallBatch(
            new FunctionCallContent("speak-call", "speak", new Dictionary<string, object?> { ["speech"] = "Greetings" }));
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = CreateVoiceRoutedMind(owner, speaker, player, clientProvider);
        List<SpeechPercept> percepts = [];
        hearing.Perceived += percept => percepts.Add(Assert.IsType<SpeechPercept>(percept));
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind, speechGenerator, lipSyncPlayer, ownerAiVoice);

        try
        {
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 1);
            // Admission commits — the TTS request exists — while its generation stays held.
            await WaitUntilAsync(sceneTree, () => speechGenerator.GenerateCallCount == 1);
            Assert.True(ownerAiVoice.IsSpeaking, "Admission opens the speaking window before generation.");

            source.EmitSpeechStarted(new SpeechSegmentMetadata("onset-group", 0));
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            // The protected pipeline continues past the cue: releasing generation completes conversion,
            // preparation, and playback hand-off, committing exactly one self-observation.
            _ = generationHeld.TrySetResult(CreateWaveFileBytes([0x34, 0x12], 16000));
            await WaitUntilAsync(sceneTree, () => ownerAiVoice.HandOffCount == 1);
            await WaitUntilAsync(sceneTree, () => mind.GetTimelineForTest().Any(
                observation => observation is ObservedSpeech speech && speech.ActorId == owner.FullId));
            ObservedSpeech selfSpeech = Assert.IsType<ObservedSpeech>(
                Assert.Single(
                    mind.GetTimelineForTest(),
                    observation => observation is ObservedSpeech speech && speech.ActorId == owner.FullId));
            Assert.Equal("Greetings", selfSpeech.Content);
            Assert.Equal(1, speechGenerator.GenerateCallCount);

            // The cue's hold keeps the next model request blocked even after the speak settled naturally.
            await TestUtils.WaitForFramesAsync(sceneTree, 6);
            _ = Assert.Single(clientProvider.Requests);

            source.PublishCompletedSpeech("player words", new SpeechSegmentMetadata("onset-group", 0));
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 2);

            // The settled speak exchange was disposed: no assistant call and no spoken tool result survive in the
            // replacement, and the committed speech appears exactly once as the mind's own timeline event
            // (AI-002 TR-22).
            IReadOnlyList<ChatMessage> replacement = clientProvider.Requests[1];
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User],
                replacement.Select(static message => message.Role));
            Assert.Empty(replacement.SelectMany(static message => message.Contents.OfType<FunctionResultContent>()));
            ChatMessage joined = Assert.Single(
                clientProvider.Requests[1],
                message => message.Role == ChatRole.User && message.Text.StartsWith("Established Event History:", StringComparison.Ordinal));
            Assert.Contains("Heard char:speaker say: player words", joined.Text, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(joined.Text, "I said: Greetings"));
            _ = Assert.Single(percepts, percept => percept.Content == "player words");
        }
        finally
        {
            root.QueueFree();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// An unrelated speaker's completed speech — fresh content carrying none of the protected speak's speech keys
    /// — withdraws the admitted submission before playback hand-off: no self-observation exists, and the disposed
    /// stale speak batch leaves no protocol trace in the replacement, while the cueing player's own completion
    /// still settles the surviving hold.
    /// </summary>
    [Fact]
    public async Task SpeechAdmission_UnrelatedCompletedSpeech_CancelsTheProtectedAdmittedSpeak()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        TestVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        TestVoice unrelated = new()
        {
            Id = "unrelated-voice",
        };
        TaskCompletionSource<byte[]> generationHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionSpeechGenerator speechGenerator = new()
        {
            PendingResult = generationHeld,
        };
        StubSessionLipSyncPlayer lipSyncPlayer = new();
        SessionAIVoice ownerAiVoice = new()
        {
            Id = "owner-voice",
            SpeechGenerator = speechGenerator,
            LipSyncPlayer = lipSyncPlayer,
        };
        TestCharacter owner = new(hearing, ownerAiVoice);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        TestCharacter unrelatedSpeaker = new(unrelated)
        {
            Id = "unrelated",
        };
        FixturePlayerCharacter player = new();
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueCallBatch(
            new FunctionCallContent("speak-call", "speak", new Dictionary<string, object?> { ["speech"] = "Greetings" }));
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = CreateVoiceRoutedMind(owner, speaker, player, clientProvider, unrelatedSpeaker);
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind, speechGenerator, lipSyncPlayer, ownerAiVoice, unrelated);

        try
        {
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 1);
            await WaitUntilAsync(sceneTree, () => speechGenerator.GenerateCallCount == 1);

            source.EmitSpeechStarted(new SpeechSegmentMetadata("onset-group", 0));
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            // The unrelated speaker's completed speech is ordinary all-hearer freshness: with no speech key of the
            // protected turn, it withdraws the unhand-offed submission silently.
            unrelated.PublishCompletedSpeech("unrelated chatter");
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => mind.GetTimelineForTest().Any(
                observation => observation is ObservedSpeech speech && speech.Content == "unrelated chatter"));
            await TestUtils.WaitForFramesAsync(sceneTree, 6);

            Assert.Equal(0, ownerAiVoice.HandOffCount);
            Assert.DoesNotContain(
                mind.GetTimelineForTest(),
                observation => observation is ObservedSpeech speech && speech.ActorId == owner.FullId);

            // The surviving cue hold still gates the replacement until the cueing speaker completes.
            _ = Assert.Single(clientProvider.Requests);
            source.PublishCompletedSpeech("player words", new SpeechSegmentMetadata("onset-group", 0));
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 2);

            // The withdrawn speak batch settled with its not-delivered result and was disposed whole: the
            // replacement carries no assistant call and no tool result (AI-002 TR-17/22).
            IReadOnlyList<ChatMessage> replacement = clientProvider.Requests[1];
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User],
                replacement.Select(static message => message.Role));
            Assert.Empty(replacement.SelectMany(static message => message.Contents.OfType<FunctionResultContent>()));
            ChatMessage joined = Assert.Single(
                clientProvider.Requests[1],
                message => message.Role == ChatRole.User && message.Text.StartsWith("Established Event History:", StringComparison.Ordinal));
            Assert.Contains("Heard char:speaker say: player words", joined.Text, StringComparison.Ordinal);
            Assert.Contains("Heard char:unrelated say: unrelated chatter", joined.Text, StringComparison.Ordinal);
        }
        finally
        {
            root.QueueFree();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// A voice without the admission capability is never refused at a cue and is not arbitration-protected
    /// (SPCH-005 TR-38): the ordinary cancellable submission starts even though an attended onset cue is pending,
    /// the cue's invalidation then withdraws the pre-hand-off submission silently — no hearing event, no
    /// self-observation — and the settled speak batch is disposed whole, so the replacement request carries no
    /// protocol trace of the withdrawn call once the cueing speech settles (AI-002 TR-17/22).
    /// </summary>
    [Fact]
    public async Task OrdinaryVoiceFallback_CueAfterSubmission_StartsAnywayThenWithdrawsPreHandOffSilently()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        ControllableFallbackVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        TestCharacter owner = new(hearing, ownerVoice);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueCallBatch(
            new FunctionCallContent("speak-call", "speak", new Dictionary<string, object?> { ["speech"] = "Greetings" }));
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = CreateVoiceRoutedMind(owner, speaker, player, clientProvider);
        List<SpeechPercept> percepts = [];
        hearing.Perceived += percept => percepts.Add(Assert.IsType<SpeechPercept>(percept));
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind);

        try
        {
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 1);
            await ownerVoice.SubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The submission was never refused: an ordinary voice's speech starts through the ordinary path
            // regardless of pending arbitration state (SPCH-005 TR-38).
            Assert.Equal(["Greetings"], ownerVoice.Submissions);

            source.EmitSpeechStarted(new SpeechSegmentMetadata("onset-group", 0));
            await WaitUntilAsync(sceneTree, () => ownerVoice.CancellationObserved);

            // Not arbitration-protected: the cue's invalidation cancels the pre-hand-off submission with the
            // ordinary silent-cancellation semantics (SPCH-005 TR-25/38) — no self-observation exists.
            Assert.True(ownerVoice.CancellationObserved, "The cue must withdraw the unhand-offed ordinary submission.");
            Assert.DoesNotContain(
                mind.GetTimelineForTest(),
                observation => observation is ObservedSpeech speech && speech.ActorId == owner.FullId);
            _ = Assert.Single(clientProvider.Requests);

            source.PublishCompletedSpeech("player words", new SpeechSegmentMetadata("onset-group", 0));
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 2);

            // The withdrawn speak batch settled with its not-delivered result and was disposed whole: the
            // replacement carries no assistant call and no tool result (AI-002 TR-17/22).
            IReadOnlyList<ChatMessage> replacement = clientProvider.Requests[1];
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User],
                replacement.Select(static message => message.Role));
            Assert.Empty(replacement.SelectMany(static message => message.Contents.OfType<FunctionResultContent>()));
            ChatMessage joined = Assert.Single(
                clientProvider.Requests[1],
                message => message.Role == ChatRole.User && message.Text.StartsWith("Established Event History:", StringComparison.Ordinal));
            Assert.Contains("Heard char:speaker say: player words", joined.Text, StringComparison.Ordinal);
        }
        finally
        {
            root.QueueFree();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// A voice without the admission capability keeps its post-hand-off speech committed (SPCH-005 TR-38): a cue
    /// arriving after playback hand-off neither cuts the audible speech nor retracts its exactly-once
    /// self-observation, and the settled speak exchange is disposed — the committed speech reaches the model
    /// exactly once as its own timeline event instead of a retained tool result (AI-002 TR-22).
    /// </summary>
    [Fact]
    public async Task OrdinaryVoiceFallback_CueAfterHandOff_KeepsCommittedSpeechAndSelfObservation()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        ControllableFallbackVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        TestCharacter owner = new(hearing, ownerVoice);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueCallBatch(
            new FunctionCallContent("speak-call", "speak", new Dictionary<string, object?> { ["speech"] = "Committed" }));
        clientProvider.EnqueueHoldForever();
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = CreateVoiceRoutedMind(owner, speaker, player, clientProvider);
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind);

        try
        {
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 1);
            await ownerVoice.SubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Playback hand-off commits the ordinary submission irreversibly: the speaking window stays open and
            // exactly one self-observation commits (AI-002 TR-14, SPCH-005 UR-14).
            ownerVoice.CompleteHandOff();
            await WaitUntilAsync(sceneTree, () => mind.GetTimelineForTest().Any(
                observation => observation is ObservedSpeech speech && speech.ActorId == owner.FullId));
            ObservedSpeech selfSpeech = Assert.IsType<ObservedSpeech>(
                Assert.Single(
                    mind.GetTimelineForTest(),
                    observation => observation is ObservedSpeech speech && speech.ActorId == owner.FullId));
            Assert.Equal("Committed", selfSpeech.Content);
            Assert.True(ownerVoice.IsSpeaking, "Committed ordinary speech must never be cut by the later cue.");

            source.EmitSpeechStarted(new SpeechSegmentMetadata("onset-group", 0));
            source.PublishCompletedSpeech("player words", new SpeechSegmentMetadata("onset-group", 0));
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(
                sceneTree,
                () => clientProvider.Requests.Any(request => request.Any(message =>
                    message.Role == ChatRole.User
                    && message.Text != AgenticMind.SessionBootstrapInput
                    && message.Text.Contains("player words", StringComparison.Ordinal))));

            IReadOnlyList<ChatMessage> replacement = clientProvider.Requests[^1];
            // The settled speak exchange was disposed: no assistant call and no spoken tool result survive in the
            // replacement, while the committed speech appears exactly once as the mind's own timeline event
            // (AI-002 TR-22).
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User],
                replacement.Select(static message => message.Role));
            Assert.Empty(replacement.SelectMany(static message => message.Contents.OfType<FunctionResultContent>()));
            Assert.True(ownerVoice.IsSpeaking, "The committed audible speech stays uncut after the cue settles.");
            ChatMessage eventTimeline = GetEventTimelineMessage(replacement);
            Assert.Equal(1, CountOccurrences(eventTimeline.Text, "I said: Committed"));
            ObservedSpeech retained = Assert.IsType<ObservedSpeech>(
                Assert.Single(
                    mind.GetTimelineForTest(),
                    observation => observation is ObservedSpeech speech && speech.ActorId == owner.FullId));
            Assert.Equal("Committed", retained.Content);
        }
        finally
        {
            root.QueueFree();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// Node exit remains terminal while several cue holds coexist: the session ends quietly with no replacement
    /// request, no fabricated replay, and no lifecycle work after teardown (AI-002 TR-14).
    /// </summary>
    [Fact]
    public async Task NodeExit_WithCoexistingContinuationHolds_EndsSessionQuietlyWithoutReplay()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        TestVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        TestCharacter owner = new(hearing, ownerVoice);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = CreateVoiceRoutedMind(owner, speaker, player, clientProvider);
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.EmitSpeechStarted(new SpeechSegmentMetadata("first-group", 0));
            source.EmitSpeechResumed(new SpeechSegmentMetadata("second-group", 2));
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
            _ = Assert.Single(clientProvider.Requests);

            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            Assert.True(clientProvider.EndedByCancellation());
            _ = Assert.Single(clientProvider.Requests);
            Assert.Empty(mind.GetTimelineForTest());
        }
        finally
        {
            root.QueueFree();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>Blank settlement releases only the matching grouped continuation lease.</summary>
    [Fact]
    public async Task GroupedContinuation_BlankSettlement_ReleasesOnlyItsMatchingHold()
        => await AssertTerminalContinuationSettlementAsync(SpeechSegmentSettlementKind.Blank);

    /// <summary>Failed settlement releases only the matching grouped continuation lease.</summary>
    [Fact]
    public async Task GroupedContinuation_FailedSettlement_ReleasesOnlyItsMatchingHold()
        => await AssertTerminalContinuationSettlementAsync(SpeechSegmentSettlementKind.Failed);

    /// <summary>Abandoned settlement releases only the matching grouped continuation lease.</summary>
    [Fact]
    public async Task GroupedContinuation_AbandonedSettlement_ReleasesOnlyItsMatchingHold()
        => await AssertTerminalContinuationSettlementAsync(SpeechSegmentSettlementKind.Abandoned);

    /// <summary>
    /// Same-group continuation leases retain their segment identity through AgenticMind: completing either segment
    /// cannot start the replacement request while the other is still unresolved (AI-002 TR-11).
    /// </summary>
    [Fact]
    public async Task GroupedContinuation_TwoOutstandingSegmentsRequireBothMatchingCompletions()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        TestVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        TestCharacter owner = new(hearing, ownerVoice);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = CreateVoiceRoutedMind(owner, speaker, player, clientProvider);
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.EmitSpeechResumed(new SpeechSegmentMetadata("same-group", 1));
            source.EmitSpeechResumed(new SpeechSegmentMetadata("same-group", 2));
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);

            source.PublishCompletedSpeech("first continuation", new SpeechSegmentMetadata("same-group", 1));
            await mind.DrainPerceptionsForTestAsync();
            await TestUtils.WaitForFramesAsync(sceneTree, 3);
            _ = Assert.Single(clientProvider.Requests);

            source.PublishCompletedSpeech("second continuation", new SpeechSegmentMetadata("same-group", 2));
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 2);

            ChatMessage joined = Assert.Single(
                clientProvider.Requests[1],
                message => message.Role == ChatRole.User && message.Text.StartsWith("Established Event History:", StringComparison.Ordinal));
            Assert.Contains("first continuation … second continuation", joined.Text, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(joined.Text, "first continuation … second continuation"));
        }
        finally
        {
            root.QueueFree();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// Once a prefix has reached accepted transcript history with an assistant call and tool result, a later
    /// continuation appears through the event-timeline tail. The accepted exchange is retained unchanged
    /// (AI-002 TR-5, TR-11).
    /// </summary>
    [Fact]
    public async Task GroupedContinuation_AfterAcceptedPrefix_RendersWithoutRewritingAcceptedExchange()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        TestVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        TestCharacter owner = new(hearing, ownerVoice);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        CapturingTool tool = new();
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueCall("capture_context");
        TaskCompletionSource secondRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        clientProvider.EnqueueHold(secondRequestStarted);
        clientProvider.EnqueueCall("capture_context");
        TaskCompletionSource fourthRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        clientProvider.EnqueueHold(fourthRequestStarted);
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = CreateVoiceRoutedMind(owner, speaker, player, clientProvider, tool);
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind);

        try
        {
            await secondRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.PublishCompletedSpeech("accepted prefix", new SpeechSegmentMetadata("causal", 0));
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 4);
            await fourthRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, tool.CapturedContexts.Count);

            source.EmitSpeechResumed(new SpeechSegmentMetadata("causal", 1));
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
            source.PublishCompletedSpeech("later continuation", new SpeechSegmentMetadata("causal", 1));
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 5);

            IReadOnlyList<ChatMessage> continuedRequest = clientProvider.Requests[4];
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool],
                continuedRequest.Select(message => message.Role));
            ChatMessage eventTimeline = GetEventTimelineMessage(continuedRequest);
            Assert.Contains("accepted prefix … later continuation", eventTimeline.Text, StringComparison.Ordinal);
            string newHistory = eventTimeline.Text[(eventTimeline.Text.IndexOf(
                "--- New Since Your Previous Response ---", StringComparison.Ordinal)
                + "--- New Since Your Previous Response ---".Length)..];
            Assert.Equal(1, CountOccurrences(newHistory, "accepted prefix … later continuation"));
            _ = Assert.IsType<FunctionCallContent>(Assert.Single(continuedRequest[5].Contents));
            _ = Assert.IsType<FunctionResultContent>(Assert.Single(continuedRequest[6].Contents));
        }
        finally
        {
            root.QueueFree();
            tool.Free();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// A resume cancels a still-active wait with the canonical result, and that settled wait batch is disposed
    /// whole: its later grouped completion is delivered exactly once as projected timeline text with no protocol
    /// trace of the disposed exchange, and a naturally-settled later wait is likewise disposed rather than
    /// retained as accepted history (AI-002 TR-17/22).
    /// </summary>
    [Fact]
    public async Task GroupedContinuation_ActiveAndAcceptedWaitsPreserveCanonicalOwnershipAndReconciliation()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        TestVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        TestCharacter owner = new(hearing, ownerVoice);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueCall("wait");
        clientProvider.EnqueueCall("wait");
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = CreateVoiceRoutedMind(owner, speaker, player, clientProvider);
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind);

        try
        {
            await WaitUntilAsync(sceneTree, () => mind.HasActiveObservationWait);
            source.EmitSpeechResumed(new SpeechSegmentMetadata("wait-group", 1));
            await TestUtils.WaitForFramesAsync(sceneTree, 3);
            _ = Assert.Single(clientProvider.Requests);

            source.PublishCompletedSpeech("wait prefix", new SpeechSegmentMetadata("wait-group", 0));
            source.PublishCompletedSpeech("wait continuation", new SpeechSegmentMetadata("wait-group", 1));
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 2);

            IReadOnlyList<ChatMessage> afterCancelledWait = clientProvider.Requests[1];
            // The cancelled wait batch settled with canonical cancellation results and was disposed whole: the
            // replacement carries no assistant call and no tool result, and the window is delivered only through
            // the canonical event timeline (AI-002 TR-17/22).
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User],
                afterCancelledWait.Select(static message => message.Role));
            Assert.Empty(afterCancelledWait.SelectMany(static message => message.Contents.OfType<FunctionResultContent>()));
            ChatMessage delivery = Assert.Single(
                afterCancelledWait,
                message => message.Role == ChatRole.User && message.Text.StartsWith("Established Event History:", StringComparison.Ordinal));
            Assert.Contains("wait prefix … wait continuation", delivery.Text, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(delivery.Text, "wait prefix … wait continuation"));

            // The second wait consumes a prefix naturally. Its settled batch is disposed before the resumed
            // segment arrives, so the later projected utterance must be a new event-timeline entry, never a
            // retained wait result or a rewrite.
            await WaitUntilAsync(sceneTree, () => mind.HasActiveObservationWait);
            source.PublishCompletedSpeech("accepted wait prefix", new SpeechSegmentMetadata("accepted-wait", 0));
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 3);
            source.EmitSpeechResumed(new SpeechSegmentMetadata("accepted-wait", 1));
            source.PublishCompletedSpeech("accepted wait continuation", new SpeechSegmentMetadata("accepted-wait", 1));
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 4);

            IReadOnlyList<ChatMessage> continuedRequest = clientProvider.Requests[3];
            // No wait result — natural or cancelled — survives disposal into the later request.
            Assert.Empty(continuedRequest.SelectMany(static message => message.Contents.OfType<FunctionResultContent>()));
            ChatMessage eventTimeline = GetEventTimelineMessage(continuedRequest);
            Assert.Contains("accepted wait prefix … accepted wait continuation", eventTimeline.Text, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(eventTimeline.Text, "accepted wait prefix … accepted wait continuation"));
        }
        finally
        {
            root.QueueFree();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// Consecutively completed speak batches keep later request contexts lean end-to-end: every settled exchange is
    /// disposed, committed speech reaches the model exactly once as its own timeline event, and the later request
    /// carries only the canonical context prefix and the bootstrap input (AI-002 TR-17/21/22).
    /// </summary>
    [Fact]
    public async Task CompletedSpeakBatches_DoNotAccumulateInLaterRequests()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        TestVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        TestCharacter owner = new(hearing, ownerVoice);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        TaskCompletionSource fourthRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueCallBatch(
            new FunctionCallContent("speak-1", "speak", new Dictionary<string, object?> { ["speech"] = "One." }));
        clientProvider.EnqueueCallBatch(
            new FunctionCallContent("speak-2", "speak", new Dictionary<string, object?> { ["speech"] = "Two." }));
        clientProvider.EnqueueCallBatch(
            new FunctionCallContent("speak-3", "speak", new Dictionary<string, object?> { ["speech"] = "Three." }));
        clientProvider.EnqueueHold(fourthRequestStarted);
        TestAgenticMind mind = CreateVoiceRoutedMind(owner, speaker, player, clientProvider);
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind);

        try
        {
            await fourthRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // All committed self speech settles on the timeline before the assertion request is triggered, so its
            // captured context is deterministic.
            await WaitUntilAsync(sceneTree, () => mind.GetTimelineForTest()
                .OfType<ObservedSpeech>()
                .Count(speech => speech.ActorId == owner.FullId) == 3);
            source.PublishCompletedSpeech("outer words", new SpeechSegmentMetadata("outer-group", 0));
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 5);

            IReadOnlyList<ChatMessage> latest = clientProvider.Requests[4];
            // Three settled speak batches left no protocol trace: the replacement carries only the canonical
            // context prefix and the bootstrap input (AI-002 TR-21/22).
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User],
                latest.Select(static message => message.Role));
            Assert.Empty(latest.SelectMany(static message => message.Contents.OfType<FunctionResultContent>()));
            Assert.Equal(AgenticMind.SessionBootstrapInput, latest[2].Text);
            ChatMessage eventTimeline = GetEventTimelineMessage(latest);
            Assert.Equal(1, CountOccurrences(eventTimeline.Text, "I said: One."));
            Assert.Equal(1, CountOccurrences(eventTimeline.Text, "I said: Two."));
            Assert.Equal(1, CountOccurrences(eventTimeline.Text, "I said: Three."));
            Assert.Equal(1, CountOccurrences(eventTimeline.Text, "Heard char:speaker say: outer words"));
        }
        finally
        {
            root.QueueFree();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// Non-opted tool exchanges stay retained: watch arming and unwatch settle as ordinary retained exchanges whose
    /// assistant calls and tool results replay in later requests, and neither arming nor removal fabricates timeline
    /// observations (AI-002 TR-20; AI-010).
    /// </summary>
    [Fact]
    public async Task WatchToolExchanges_AreRetainedInLaterRequestsWithoutFabricatedObservations()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        WatchRegistry watchRegistry = new()
        {
            Conditions = [new ProximityWatchTool()],
        };
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueCallBatch(new FunctionCallContent(
            "arm-call",
            "watch_proximity",
            new Dictionary<string, object?> { ["subject_id"] = player.FullId, ["maximum_distance"] = 2f }));
        clientProvider.EnqueueCallBatch(new FunctionCallContent(
            "unwatch-call",
            "unwatch",
            new Dictionary<string, object?> { ["watch_id"] = "w1" }));
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
        };
        mind.AddChild(watchRegistry);
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            IReadOnlyList<ChatMessage> afterArm = clientProvider.Requests[1];
            // The settled arming exchange is retained whole (AI-002 TR-20): its assistant call and opaque-ID tool
            // result replay in the next request.
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
                afterArm.Select(static message => message.Role));
            FunctionResultContent armResult = Assert.IsType<FunctionResultContent>(
                Assert.Single(afterArm[4].Contents.OfType<FunctionResultContent>()));
            Assert.Equal("arm-call", armResult.CallId);
            Assert.Contains("w1", armResult.Result?.ToString(), StringComparison.Ordinal);

            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 3);
            IReadOnlyList<ChatMessage> afterRemoval = clientProvider.Requests[2];
            // Both settled watch exchanges stay retained, and neither arming nor removal invented an event.
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool],
                afterRemoval.Select(static message => message.Role));
            FunctionResultContent removalResult = Assert.IsType<FunctionResultContent>(
                Assert.Single(afterRemoval[6].Contents.OfType<FunctionResultContent>()));
            Assert.Equal("unwatch-call", removalResult.CallId);
            Assert.Contains("Removed watch w1.", removalResult.Result?.ToString(), StringComparison.Ordinal);
            Assert.Empty(mind.GetTimelineForTest());
            Assert.Empty(watchRegistry.GetActiveWatchSnapshot());
        }
        finally
        {
            watchRegistry.EndSession();
            mind.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>Ungrouped external speech retains ordinary fresh invalidation and delivery behaviour (AI-002 TR-4/5).</summary>
    [Fact]
    public async Task UngroupedExternalSpeech_StillInvalidatesAndDeliversWithoutContinuationHold()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        TestVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        TestCharacter owner = new(hearing, ownerVoice);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = CreateVoiceRoutedMind(owner, speaker, player, clientProvider);
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.PublishCompletedSpeech("ordinary external speech");
            await mind.DrainPerceptionsForTestAsync();
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 2);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);

            ChatMessage delivered = Assert.Single(
                clientProvider.Requests[1],
                message => message.Role == ChatRole.User && message.Text.StartsWith("Established Event History:", StringComparison.Ordinal));
            Assert.Contains("ordinary external speech", delivered.Text, StringComparison.Ordinal);
            _ = Assert.Single(mind.GetTimelineForTest().OfType<ObservedSpeech>());
        }
        finally
        {
            root.QueueFree();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// External non-self speech observed mid-wait fulfils the wait through its normal completion mechanism: the
    /// wait's own tool result carries the fresh speech plus its preceding FIFO accumulation — never generic
    /// action-interrupted wording — the batch's trailing call is skipped with the canonical cancellation result,
    /// no duplicate observation text exists for the wait-owned window, and exactly one replacement request
    /// follows (AI-002 UR-5, TR-4/5 and TR-10).
    /// </summary>
    [Fact]
    public async Task ExternalSpeech_DuringActiveWait_DeliversWindowThroughNaturalWaitResultWithSingleReplacement()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        CapturingTool tool = new();
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueCallBatch(
            new FunctionCallContent("wait-call", "wait", new Dictionary<string, object?> { ["seconds"] = 5f }),
            new FunctionCallContent("trailing-call", "capture_context", new Dictionary<string, object?>()));
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            Tools = [tool],
            AllowMultipleToolCalls = true,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 1);
            // The observation must land while the wait owns delivery, so synchronise on its registration.
            await WaitUntilAsync(sceneTree, () => mind.HasActiveObservationWait);

            // A sub-threshold ordinary observation accumulates without waking the wait, then external non-self
            // speech upgrades the complete window to fresh urgency and wakes the wait through its normal
            // completion mechanism.
            mind.ObserveForTest(new RouteObservation("world.before-speech"));
            mind.ObserveForTest(new ObservedSpeech("char:someone-else", "You there?"));

            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            // Exactly one replacement request replays the complete exchange: bootstrap input, the validated
            // batch's assistant calls, and one protocol-valid tool-result message — with no extra event message
            // duplicating the wait-owned window.
            IReadOnlyList<ChatMessage> replacement = clientProvider.Requests[1];
            Assert.Equal(2, clientProvider.Requests.Count);
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
                replacement.Select(message => message.Role));

            FunctionResultContent waitResult = Assert.IsType<FunctionResultContent>(replacement[4].Contents[0]);
            FunctionResultContent trailingResult = Assert.IsType<FunctionResultContent>(replacement[4].Contents[1]);
            string waitText = waitResult.Result?.ToString() ?? string.Empty;

            // The wait result reports scheduling metadata only; the canonical event-timeline message owns all
            // model-visible event text in FIFO order.
            Assert.Equal("wait-call", waitResult.CallId);
            Assert.Contains("Wait ended: fresh event.", waitText, StringComparison.Ordinal);
            Assert.DoesNotContain("world.before-speech", waitText, StringComparison.Ordinal);
            Assert.DoesNotContain("You there?", waitText, StringComparison.Ordinal);
            ChatMessage eventTimeline = GetEventTimelineMessage(replacement);
            int preceding = eventTimeline.Text.IndexOf("((Received world.before-speech event.))", StringComparison.Ordinal);
            int speech = eventTimeline.Text.IndexOf("Heard char:someone-else say: You there?", StringComparison.Ordinal);
            Assert.True(
                preceding >= 0 && speech > preceding,
                $"The canonical event timeline must order the accumulation before the speech: '{eventTimeline.Text}'");

            // No generic action-interrupted wording anywhere in the exchange: the wait reports its natural
            // delivery, and the canonical cancelled result appears exactly once — only for the never-started
            // trailing call the stale batch required the runner to skip.
            Assert.DoesNotContain("cancelled", waitText, StringComparison.Ordinal);
            Assert.DoesNotContain("Nothing notable happened", waitText, StringComparison.Ordinal);
            Assert.Empty(tool.CapturedContexts);
            Assert.Equal("trailing-call", trailingResult.CallId);
            Assert.Equal("The action was cancelled before it completed.", trailingResult.Result?.ToString());
            Assert.Contains(
                replacement.SelectMany(static message => message.Contents.OfType<FunctionResultContent>()),
                result => result.Result?.ToString()?.Contains("cancelled before it completed", StringComparison.Ordinal) == true
                    && result.CallId == "trailing-call");

            // The wait completed naturally and retains no payload-delivery state.
            Assert.False(mind.HasActiveObservationWait);

            // Node exit ends the held replacement request quietly without issuing another request.
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
            Assert.Equal(2, clientProvider.Requests.Count);
        }
        finally
        {
            mind.Free();
            tool.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// An ordinary visual description at its provisional importance during held generation is delivered without
    /// cancellation and rendered through its type-owned safe fallback in accumulation order at the next natural
    /// boundary (AI-002 UR-5).
    /// </summary>
    [Fact]
    public async Task NotableVisualDescription_DuringGeneration_UsesTypeOwnedFallbackChronologically()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseGeneration = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CapturingTool tool = new();
        ScriptedSessionClientProvider clientProvider = new();
        const float provisionalVisualDescriptionImportance = 0.1f;
        // Keep the threshold strictly below the provisional importance so this remains a crossing if Mind uses
        // strictly-greater threshold semantics.
        float visualDescriptionDeliveryThreshold = provisionalVisualDescriptionImportance - 0.01f;
        clientProvider.EnqueueHoldUntilReleasedCall(firstRequestStarted, releaseGeneration, "capture_context");
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            Tools = [tool],
            ObservationImportanceThreshold = visualDescriptionDeliveryThreshold,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            mind.ObserveForTest(new RouteObservation("world.changed"));
            mind.ObserveForTest(new ObservedVisualDescription("char:coat", "A weathered red coat."));
            // The visual description crossed the threshold as an ordinary delivery: the generation still holds.
            _ = Assert.Single(clientProvider.Requests);
            _ = releaseGeneration.TrySetResult();

            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            ChatMessage eventTimeline = GetEventTimelineMessage(clientProvider.Requests[1]);
            Assert.Contains("((Received world.changed event.))", eventTimeline.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("weathered red coat", eventTimeline.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("vision.description", eventTimeline.Text, StringComparison.Ordinal);

            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);

            IReadOnlyList<AgentObservation> timeline = mind.GetTimelineForTest();
            Assert.Collection(
                timeline,
                observation => Assert.IsType<RouteObservation>(observation));
        }
        finally
        {
            mind.Free();
            tool.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// An ordinary notable signal during generation leaves the held request untouched until the next request
    /// boundary materialises its canonical context.
    /// </summary>
    [Fact]
    public async Task NotableSignal_DuringGeneration_DoesNotAffectActiveRequest()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            mind.ObserveForTest(new TestObservation(1f, "bridge"));

            await TestUtils.WaitForFramesAsync(sceneTree, 6);

            // Delivery scheduling is payload-free and cannot send a second request while the original generation
            // remains active.
            Assert.DoesNotContain(loggerProvider.Entries, entry => entry.Level == LogLevel.Error);
            _ = Assert.Single(clientProvider.Requests);

            // Canonical context consumes scheduling pressure only after response confirmation.

            // Node exit ends the still-held generation quietly.
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
            Assert.Equal(["bridge"], TimelineValues(mind));
        }
        finally
        {
            mind.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// A second ordinary notable signal during generation likewise leaves the active request intact.
    /// </summary>
    [Fact]
    public async Task RepeatedNotableSignals_DuringGeneration_DoNotAffectActiveRequest()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            mind.ObserveForTest(new TestObservation(1f, "bridge"));

            await TestUtils.WaitForFramesAsync(sceneTree, 6);

            // The active request's canonical context was already materialised and cannot gain a second request.
            Assert.DoesNotContain(loggerProvider.Entries, entry => entry.Level == LogLevel.Error);
            _ = Assert.Single(clientProvider.Requests);

            // Canonical context consumes scheduling pressure only after response confirmation.

            // Node exit ends the still-held generation quietly.
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
            Assert.Equal(["bridge"], TimelineValues(mind));
        }
        finally
        {
            mind.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// A fresh speech event invalidates stale generation and the replacement request materialises canonical timeline
    /// context without an out-of-band payload (AI-001 TR-7, AI-002 UR-5/TR-4/5).
    /// </summary>
    [Fact]
    public async Task FreshSpeech_SupersedesGenerationThroughCanonicalContext()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            mind.ObserveForTest(new ObservedSpeech("char:someone-else", "You there?"));

            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            await TestUtils.WaitForFramesAsync(sceneTree, 6);

            // The stale generation was invalidated and replaced, but the replacement carries no extra event message:
            // the canonical event context is materialised at the replacement boundary instead of at delivery time.
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User],
                clientProvider.Requests[1].Select(message => message.Role));
            Assert.DoesNotContain(loggerProvider.Entries, entry => entry.Level == LogLevel.Error);

            // The fresh event stays in the persistent timeline until context confirmation.

            // Node exit ends the still-held replacement request quietly.
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
        }
        finally
        {
            mind.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// An ordinary pending window followed by a fresh observation coalesces in FIFO order into exactly one
    /// event-timeline message on the single fresh replacement request (AI-002 TR-7–10, TR-4/5).
    /// </summary>
    [Fact]
    public async Task OrdinaryAndFreshObservations_CoalesceFIFOIntoOneFreshReplacementRequest()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            mind.ObserveForTest(new TestObservation(1f, "ordinary-event"));
            await TestUtils.WaitForFramesAsync(sceneTree, 4);
            mind.ObserveForTest(new TestObservation(1f, "fresh-event", Fresh: true));

            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            IReadOnlyList<ChatMessage> freshRequest = clientProvider.Requests[1];
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User],
                freshRequest.Select(message => message.Role));
            // Both windows are present in the canonical event-timeline message in FIFO order: the ordinary event's
            // rendered record precedes the fresh event's record inside the new-history tail.
            ChatMessage eventTimeline = GetEventTimelineMessage(freshRequest);
            int ordinary = eventTimeline.Text.IndexOf("- ordinary-event", StringComparison.Ordinal);
            int fresh = eventTimeline.Text.IndexOf("- fresh-event", StringComparison.Ordinal);
            Assert.True(
                ordinary >= 0 && fresh > ordinary,
                $"The canonical event timeline must render both windows in FIFO order: '{eventTimeline.Text}'");

            // Node exit ends the held replacement request quietly.
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
        }
        finally
        {
            mind.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// Node exit cancels an in-flight generation request: the session ends quietly without retry and without
    /// backend-failure diagnostics, while the Mind timeline persists (AI-002 TR-14, AI-001 TR-11).
    /// </summary>
    [Fact]
    public async Task NodeExit_DuringGeneration_EndsSessionQuietlyWithoutRetryOrBackendFailure()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(requestStarted);
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);
        // Sub-threshold: the observation must not interrupt the held generation, unlike node exit.
        mind.ObserveForTest(new TestObservation(0.5f, "persisted"));

        try
        {
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
            await TestUtils.WaitForFramesAsync(sceneTree, 6);

            _ = Assert.Single(clientProvider.Requests);
            Assert.DoesNotContain(
                loggerProvider.Entries,
                entry => entry.Level == LogLevel.Error);
            Assert.Equal(["persisted"], TimelineValues(mind));
        }
        finally
        {
            mind.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// Node exit while a tool is in flight settles the tool work without successful observation, and the session
    /// ends quietly without backend-failure diagnostics (AI-002 UR-5, TR-14).
    /// </summary>
    [Fact]
    public async Task NodeExit_DuringToolPhase_SettlesToolWorkWithoutObservation()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        BlockingTool tool = new();
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueCall(BlockingTool.ToolNameValue);
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            Tools = [tool],
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await tool.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, () => tool.ObservedCancellation);
            await TestUtils.WaitForFramesAsync(sceneTree, 6);

            Assert.True(tool.ObservedCancellation, "Node exit must cancel the in-flight tool work.");
            // The session ended without another request: the tool phase never resumed generation.
            _ = Assert.Single(clientProvider.Requests);
            Assert.Empty(TimelineValues(mind));
            Assert.DoesNotContain(
                loggerProvider.Entries,
                entry => entry.Level == LogLevel.Error);
        }
        finally
        {
            mind.Free();
            tool.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// The Mind forwards its configured invalid-response recovery budget to the active runner. Invalid provider
    /// output is discarded without tool effects, and exhaustion remains contained by <see cref="AgenticMind" />.
    /// </summary>
    [Fact]
    public async Task Session_InvalidResponseRecoveryBudget_IsForwardedToContainedLifecycleFailure()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        CapturingTool tool = new();
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueInvalidResponse();
        clientProvider.EnqueueInvalidResponse();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            Tools = [tool],
            ObservationImportanceThreshold = 1f,
            InvalidResponseRecoveryBudget = 2,
            InvalidResponseRecoveryBackoffSeconds = [0f],
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await WaitUntilAsync(
                sceneTree,
                () => loggerProvider.Entries.Any(entry =>
                    entry.Level == LogLevel.Error
                    && entry.Exception is AgentSessionException
                    && entry.Exception.Message.Contains("invalid response recovery budget", StringComparison.Ordinal)));

            Assert.Equal(2, clientProvider.Requests.Count);
            Assert.Empty(tool.CapturedContexts);
            Assert.All(
                clientProvider.Requests,
                request => Assert.Equal([ChatRole.User, ChatRole.User, ChatRole.User], request.Select(message => message.Role)));
            _ = Assert.Single(
                loggerProvider.Entries,
                entry => entry.Level == LogLevel.Error && entry.Exception is AgentSessionException);
        }
        finally
        {
            mind.Free();
            tool.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    private static TestAgenticMind CreateVoiceRoutedMind(
        TestCharacter owner,
        TestCharacter speaker,
        FixturePlayerCharacter player,
        ScriptedSessionClientProvider clientProvider,
        params AgentTool[] tools)
        => CreateVoiceRoutedMind(owner, speaker, player, clientProvider, extraSceneMember: null, attendSpeaker: true, tools);

    private static TestAgenticMind CreateVoiceRoutedMind(
        TestCharacter owner,
        TestCharacter speaker,
        FixturePlayerCharacter player,
        ScriptedSessionClientProvider clientProvider,
        ICharacter? extraSceneMember,
        params AgentTool[] tools)
        => CreateVoiceRoutedMind(owner, speaker, player, clientProvider, extraSceneMember, attendSpeaker: true, tools);

    private static TestAgenticMind CreateVoiceRoutedMind(
        TestCharacter owner,
        TestCharacter speaker,
        FixturePlayerCharacter player,
        ScriptedSessionClientProvider clientProvider,
        ICharacter? extraSceneMember,
        bool attendSpeaker,
        params AgentTool[] tools)
    {
        var mind = new TestAgenticMind(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            Tools = [.. tools],
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(
            () => new SceneContext(extraSceneMember is null ? [owner, speaker, player] : [owner, speaker, player, extraSceneMember]));
        mind.AddChild(new SpeechPerception());
        // Start and resume cues are gated by source-generic attention, so the routed speaker stays attended for
        // the whole session without relying on decay-sensitive defaults.
        mind.AttentionDecayPerSecond = 0f;
        if (attendSpeaker)
        {
            mind.ReinforceAttentionForTest(speaker.FullId);
        }

        return mind;
    }

    private static Node AddVoiceRoute(
        SceneTree sceneTree,
        Hearing hearing,
        TestVoice ownerVoice,
        TestVoice source,
        TestAgenticMind mind,
        params Node[] extraNodes)
    {
        var root = new Node();
        root.AddChild(hearing);
        root.AddChild(ownerVoice);
        root.AddChild(source);
        root.AddChild(mind);
        foreach (Node extraNode in extraNodes)
        {
            root.AddChild(extraNode);
        }

        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(root);
        return root;
    }

    /// <summary>
    /// Routes one shared source voice to several Mind fixtures under a single root, so every listener hears the
    /// same speech while each Mind keeps its own hearing, owner voice, session, and scripted provider.
    /// </summary>
    private static Node AddSharedVoiceRoute(
        SceneTree sceneTree,
        TestVoice source,
        params (Hearing Hearing, TestVoice OwnerVoice, TestAgenticMind Mind)[] listeners)
    {
        var root = new Node();
        root.AddChild(source);
        foreach ((Hearing hearing, TestVoice ownerVoice, TestAgenticMind mind) in listeners)
        {
            root.AddChild(hearing);
            root.AddChild(ownerVoice);
            root.AddChild(mind);
        }

        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(root);
        return root;
    }

    private static async Task AssertTerminalContinuationSettlementAsync(SpeechSegmentSettlementKind settlement)
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Hearing hearing = new();
        TestVoice ownerVoice = new()
        {
            Id = "owner-voice",
        };
        TestVoice source = new()
        {
            Id = "external-voice",
        };
        TestCharacter owner = new(hearing, ownerVoice);
        TestCharacter speaker = new(source)
        {
            Id = "speaker",
        };
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        clientProvider.EnqueueHoldForever();
        TestAgenticMind mind = CreateVoiceRoutedMind(owner, speaker, player, clientProvider);
        Node root = AddVoiceRoute(sceneTree, hearing, ownerVoice, source, mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.EmitSpeechResumed(new SpeechSegmentMetadata("terminal", 1));
            source.EmitSpeechResumed(new SpeechSegmentMetadata("other-terminal", 1));
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);

            source.EmitSpeechSettlement(new SpeechSegmentMetadata("terminal", 1), settlement);
            await TestUtils.WaitForFramesAsync(sceneTree, 3);
            _ = Assert.Single(clientProvider.Requests);
            Assert.Empty(mind.GetTimelineForTest());

            source.EmitSpeechSettlement(new SpeechSegmentMetadata("other-terminal", 1), settlement);
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count == 2);
            Assert.Equal([ChatRole.User, ChatRole.User, ChatRole.User], clientProvider.Requests[1].Select(message => message.Role));
            Assert.Equal(AgenticMind.SessionBootstrapInput, clientProvider.Requests[1][2].Text);
            Assert.Empty(mind.GetTimelineForTest());
        }
        finally
        {
            root.QueueFree();
            clientProvider.Free();
            player.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    private static int CountOccurrences(string value, string expected)
    {
        int count = 0;
        int position = 0;
        while ((position = value.IndexOf(expected, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += expected.Length;
        }

        return count;
    }

    private static ChatMessage GetEventTimelineMessage(IReadOnlyList<ChatMessage> request)
        => Assert.Single(
            request,
            static message => message.Role == ChatRole.User
                && message.Text.StartsWith("Established Event History:", StringComparison.Ordinal));

    private static IReadOnlyList<string> TimelineValues(TestAgenticMind mind)
        => [.. mind.GetTimelineForTest().Cast<TestObservation>().Select(static observation => observation.Value)];

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> predicate, int maxFrames = 300)
    {
        for (int frame = 0; frame < maxFrames && !predicate(); frame++)
        {
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(predicate(), $"Condition was not met within {maxFrames} frames.");
    }

    private sealed record TestObservation(float Importance, string Value, bool Fresh = false) : AgentObservation
    {
        public override string TypeKey => ObservedSpeech.TypeKeyValue;

        protected override string RenderBody(ICharacter character)
        {
            ArgumentNullException.ThrowIfNull(character);
            return $"- {Value}";
        }

        public override float CalculateImportance(ObservationContext context) => Importance;

        public override bool RequiresFreshTurn(ObservationContext context) => Fresh;
    }

    private sealed record RouteObservation(string Key) : AgentObservation
    {
        public override string TypeKey => Key;

        public override float CalculateImportance(ObservationContext context) => 0f;
    }

    private sealed partial class TestAgenticMind(ICharacter owner) : AgenticMind
    {
        public void ObserveForTest(AgentObservation observation) => Observe(observation);

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        public Task DrainPerceptionsForTestAsync() => DrainPerceptionsForTestingAsync();

        public void ReinforceAttentionForTest(string fullId)
            => ReinforceAttention(fullId, 1f, AttentionSettings.Create(1f, 0f, 0.05f, 0.25f));

        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    private partial class TestVoice : Voice
    {
        public override void Speak(string speech) => base.Speak(speech);

        public void BeginSpeechWindow() => OpenSpeakingWindow();

        public void EndSpeechWindow() => CloseSpeakingWindow();

        public void PublishCompletedSpeech(string speech, SpeechSegmentMetadata? metadata = null)
            => PublishSpeech(speech, metadata);

        public void EmitSpeechStarted(SpeechSegmentMetadata metadata) => RaiseSpeechSegmentStarted(metadata);

        public void EmitSpeechResumed(SpeechSegmentMetadata metadata) => RaiseSpeechResumed(metadata);

        public void EmitSpeechSettlement(SpeechSegmentMetadata metadata, SpeechSegmentSettlementKind kind)
            => RaiseSpeechSegmentSettled(new SpeechSegmentSettlement(metadata, kind));
    }

    /// <summary>
    /// Plain <see cref="TestVoice" /> double without the admission capability (SPCH-005 TR-38) whose ordinary
    /// cancellable submission the test controls: the submission is recorded, completes only at the demanded
    /// playback hand-off, and observes caller cancellation — the non-capable owner voice for the ordinary-voice
    /// fallback coverage (SPCH-005 TR-38/AC-27).
    /// </summary>
    private sealed partial class ControllableFallbackVoice : TestVoice
    {
        private readonly TaskCompletionSource _handOff = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Submissions { get; } = [];

        public TaskCompletionSource SubmissionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved
        {
            get;
            private set;
        }

        public override async ValueTask SpeakCancellableAsync(string speech, CancellationToken cancellationToken = default)
        {
            Submissions.Add(speech);
            OpenSpeakingWindow();
            _ = SubmissionStarted.TrySetResult();
            try
            {
                await _handOff.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                CloseSpeakingWindow();
                throw;
            }
        }

        public void CompleteHandOff() => _ = _handOff.TrySetResult();
    }

    /// <summary>
    /// Speech generator whose TTS generation the test controls: each request records its text and completes only
    /// when the held result — or the immediate fallback — releases it.
    /// </summary>
    private sealed partial class SessionSpeechGenerator : SpeechGenerator
    {
        public byte[] NextResult { get; set; } = [];

        public TaskCompletionSource<byte[]>? PendingResult
        {
            get;
            set;
        }

        public int GenerateCallCount
        {
            get;
            private set;
        }

        protected override Task<byte[]> GenerateCore(string text, string? instruction = null)
        {
            _ = instruction;
            GenerateCallCount++;
            return PendingResult?.Task ?? Task.FromResult(NextResult);
        }
    }

    /// <summary>Lip-sync player whose backend inference resolves synchronously without a mesh rig.</summary>
    private sealed partial class StubSessionLipSyncPlayer : LipSyncPlayer
    {
        protected override int BackendSampleRate => 16000;

        protected override void InitialiseBackend()
        {
        }

        protected override LipSyncInferenceResult RunBackendInference(
            AudioStreamWav speech,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new LipSyncInferenceResult([[0f]], ["jawOpen"], 30f);
        }

        protected override void DisposeBackend()
        {
        }
    }

    /// <summary>
    /// <see cref="AIVoice" /> double whose lip-sync preparation is fabricated and whose playback hand-off is
    /// recorded instead of dispatched to real playback, so session tests can count hand-offs without an audio
    /// device or mesh rig.
    /// </summary>
    private sealed partial class SessionAIVoice : AIVoice
    {
        public int HandOffCount
        {
            get;
            private set;
        }

        protected override Task<LipSyncPlayer.PreparedPlayback> PrepareGeneratedSpeechAsync(
            AudioStreamWav speechStream,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LipSyncPlayer.PreparedPlayback(speechStream, [[0f]], ["jawOpen"], 30f));
        }

        protected override void PlayGeneratedSpeech(LipSyncPlayer.PreparedPlayback preparedPlayback)
        {
            _ = preparedPlayback;
            HandOffCount++;
        }
    }

    /// <summary>Builds a minimal valid PCM-16 mono WAV container around the supplied sample bytes.</summary>
    private static byte[] CreateWaveFileBytes(byte[] data, int sampleRate)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);

        int channelCount = 1;
        int bitsPerSample = 16;
        short blockAlign = (short)(channelCount * bitsPerSample / 8);
        int byteRate = sampleRate * blockAlign;

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + data.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channelCount);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write((short)bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();

        return stream.ToArray();
    }

    private sealed partial class CapturingTool : AgentTool
    {
        public CapturingTool()
        {
            ToolName = "capture_context";
            ToolDescription = "Capture the trusted session context.";
        }

        public List<ScenarioContext> CapturedContexts { get; } = [];

        protected override Delegate CreateDelegate() => Capture;

        private ValueTask<AgentToolResult> Capture(ScenarioContext context)
        {
            CapturedContexts.Add(context);
            return ValueTask.FromResult(new AgentToolResult());
        }
    }

    /// <summary>Tool that blocks until cancelled, mirroring an in-flight action at node exit.</summary>
    private sealed partial class BlockingTool : AgentTool
    {
        public const string ToolNameValue = "block_until_exit";

        public BlockingTool()
        {
            ToolName = ToolNameValue;
            ToolDescription = "Block until the session ends.";
        }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ObservedCancellation
        {
            get; private set;
        }

        protected override Delegate CreateDelegate() => Block;

        private async ValueTask<AgentToolResult> Block(ScenarioContext context, CancellationToken cancellationToken)
        {
            _ = Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }

            return new AgentToolResult();
        }
    }

    /// <summary>
    /// Scripted provider whose client records every request and serves enqueued steps; unscripted requests fail
    /// loudly so unexpected session activity surfaces in assertions.
    /// </summary>
    private sealed partial class ScriptedSessionClientProvider : ClientProvider
    {
        private readonly Queue<Func<CancellationToken, Task<ChatResponse>>> _steps = new();

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public void EnqueueHold(TaskCompletionSource started)
            => EnqueueHoldStep(started);

        public void EnqueueHoldForever()
            => EnqueueHoldStep(null);

        /// <summary>
        /// Enqueues a step that holds its request — ignoring cancellation, so ordinary observation delivery never
        /// disturbs it — until released, then returns one valid call for the supplied tool.
        /// </summary>
        public void EnqueueHoldUntilReleasedCall(
            TaskCompletionSource started,
            TaskCompletionSource release,
            string toolName)
            => EnqueueHoldUntilReleasedCallBatch(
                started,
                release,
                new FunctionCallContent($"call-{Requests.Count + 1}", toolName, new Dictionary<string, object?>()));

        /// <summary>
        /// Enqueues a step that holds its request until released, then returns the supplied complete call batch —
        /// so a fixture can open world state (for example a speaking window) before the batch's tools run.
        /// </summary>
        public void EnqueueHoldUntilReleasedCallBatch(
            TaskCompletionSource started,
            TaskCompletionSource release,
            params FunctionCallContent[] calls)
            => _steps.Enqueue(async cancellationToken =>
            {
                _ = started.TrySetResult();
                _ = cancellationToken;
                await release.Task;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, calls));
            });

        public void EnqueueCall(string toolName)
            => _steps.Enqueue(cancellationToken => Task.FromResult(new ChatResponse(
                new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent($"call-{Requests.Count + 1}", toolName, new Dictionary<string, object?>())]))));

        /// <summary>
        /// Enqueues a step returning one complete multi-call batch — validated whole before any call executes — so
        /// a trailing call can prove the batch's remaining calls are skipped after an invalidation.
        /// </summary>
        public void EnqueueCallBatch(params FunctionCallContent[] calls)
            => _steps.Enqueue(cancellationToken => Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, calls))));

        public void EnqueueInvalidResponse()
            => _steps.Enqueue(cancellationToken => Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "invalid ordinary assistant text"))));

        public bool EndedByCancellation()
            => Volatile.Read(ref _endedByCancellation) != 0;

        public override IChatClient CreateChatClient() => new ScriptedClient(this);

        private void EnqueueHoldStep(TaskCompletionSource? started)
        {
            _steps.Enqueue(async cancellationToken =>
            {
                _ = (started?.TrySetResult());
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Volatile.Write(ref _endedByCancellation, 1);
                    throw;
                }

                return new ChatResponse();
            });
        }

        private int _endedByCancellation;

        private sealed class ScriptedClient(ScriptedSessionClientProvider owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                _ = options;
                owner.Requests.Add([.. messages]);
                cancellationToken.ThrowIfCancellationRequested();
                return owner._steps.Count == 0
                    ? throw new InvalidOperationException(
                        "The scripted session client received an unexpected request.")
                    : owner._steps.Dequeue()(cancellationToken);
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
                foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
                {
                    yield return update;
                }
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }

    private sealed class TestCharacter(params IComponent[] components) : ICharacter
    {
        public string Id { get; set; } = "owner";

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = components;

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly Lock _lock = new();
        private readonly List<LogEntry> _entries = [];
        private bool _disposed;

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_lock)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void Dispose() => _disposed = true;

        private void Record(LogLevel level, string message, Exception? exception)
        {
            if (_disposed)
            {
                return;
            }

            lock (_lock)
            {
                _entries.Add(new LogEntry(level, message, exception));
            }
        }

        private sealed class RecordingLogger(RecordingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _ = eventId;
                provider.Record(logLevel, formatter(state, exception), exception);
            }
        }

        public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
    }
}
