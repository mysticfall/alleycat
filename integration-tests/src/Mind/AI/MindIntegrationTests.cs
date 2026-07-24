using System.Runtime.CompilerServices;
using AlleyCat.Body.Eyes;
using AlleyCat.Body.Voice;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Isolated runtime coverage for the migrated AgenticMind speech path without reference character assets or backend calls.
/// </summary>
[Headless]
public sealed partial class MindIntegrationTests : IDisposable
{
    private readonly AIPipelineDebugLogFixture _debugLogFixture = new();

    /// <summary>
    /// Clears the isolated AI pipeline logger override after each test.
    /// </summary>
    public void Dispose() => _debugLogFixture.Dispose();

    /// <summary>
    /// Production runtime defaults select Responses and discourage provider-side multi-call batches.
    /// </summary>
    [Fact]
    public void RuntimeDefaults_UseResponsesAndDisableMultipleToolCalls()
    {
        AgenticMind mind = new();
        OpenAIClientProvider provider = Assert.IsType<OpenAIClientProvider>(mind.ClientProvider);

        Assert.Equal(OpenAIChatClientKind.Responses, provider.ChatClientKind);
        Assert.False(mind.AllowMultipleToolCalls);
        Assert.Empty(OpenAIClientProvider.CreateRunMessages(OpenAIChatClientKind.ChatCompletions));
        _ = Assert.Single(OpenAIClientProvider.CreateRunMessages(OpenAIChatClientKind.Responses));

        mind.Free();
    }

    /// <summary>
    /// Mind accepts every nonblank external voice regardless of ID and rejects only its exact output instance.
    /// </summary>
    [Fact]
    public void ShouldHandleVoice_AcceptsAllExternalVoicesAndRejectsOwnOutput()
    {
        var output = new RecordingVoice { Id = "shared-id" };
        var sameIDExternal = new RecordingVoice { Id = "shared-id" };
        var otherExternal = new PlainVoice("another-character");
        var mind = new TestMind { Voice = output };

        Assert.True(mind.ShouldHandleVoiceForTest("hello", sameIDExternal));
        Assert.True(mind.ShouldHandleVoiceForTest("hello", otherExternal));
        Assert.False(mind.ShouldHandleVoiceForTest("hello", output));
        Assert.False(mind.ShouldHandleVoiceForTest("  ", otherExternal));
    }

    /// <summary>
    /// Successful speech produces one actorless envelope observation that the wrapper stamps as the owner.
    /// </summary>
    [Fact]
    public async Task SpeechTool_AcceptedDispatch_IngestsOneStampedObservedSpeech()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        CapturingAgenticMind mind = new()
        {
            Enabled = false
        };
        TestCharacter character = AddAgenticMindFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            var voice = new PlainVoice("private-output-device");
            AIFunction function = new SpeechTool().CreateFunction(new ToolInvocationProvider(mind, character, voice));

            object? result = await function.InvokeAsync(
                new AIFunctionArguments { ["speech"] = "  Hello there.  " },
                CancellationToken.None);

            Assert.Equal("Spoken through the configured voice.", result);
            ObservedSpeech observation = Assert.IsType<ObservedSpeech>(Assert.Single(mind.GetTimelineForTest()));
            Assert.Equal(character.Id, observation.ActorId);
            Assert.Null(observation.VoiceId);
            Assert.Equal("Hello there.", observation.Content);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, character);
        }
    }

    /// <summary>
    /// Blank, missing, failed, throwing, and cancelled speech calls ingest no successful observation.
    /// </summary>
    [Fact]
    public async Task SpeechTool_UnsuccessfulDispatches_IngestNothing()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        CapturingAgenticMind mind = new()
        {
            Enabled = false
        };
        TestCharacter character = AddAgenticMindFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            SpeechTool tool = new();
            AIFunction blank = tool.CreateFunction(new ToolInvocationProvider(mind, character, new PlainVoice("voice")));
            AIFunction missing = tool.CreateFunction(new ToolInvocationProvider(mind, character, null));
            AIFunction failed = tool.CreateFunction(new ToolInvocationProvider(mind, character, new FailingVoice()));
            AIFunction throwing = tool.CreateFunction(new ToolInvocationProvider(mind, character, new ThrowingVoice()));

            _ = await Assert.ThrowsAsync<ArgumentException>(
                blank.InvokeAsync(new AIFunctionArguments { ["speech"] = "  " }, CancellationToken.None).AsTask);
            _ = await Assert.ThrowsAsync<InvalidOperationException>(
                missing.InvokeAsync(new AIFunctionArguments { ["speech"] = "hello" }, CancellationToken.None).AsTask);
            _ = await Assert.ThrowsAsync<InvalidOperationException>(
                failed.InvokeAsync(new AIFunctionArguments { ["speech"] = "hello" }, CancellationToken.None).AsTask);
            _ = await Assert.ThrowsAsync<InvalidOperationException>(
                throwing.InvokeAsync(new AIFunctionArguments { ["speech"] = "hello" }, CancellationToken.None).AsTask);
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                blank.InvokeAsync(new AIFunctionArguments { ["speech"] = "hello" }, cancellation.Token).AsTask);

            Assert.Empty(mind.GetTimelineForTest());
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, character);
        }
    }

    /// <summary>
    /// Cancelling a queued main-thread dispatch prevents both the underlying voice call and observation recording.
    /// </summary>
    [Fact]
    public async Task SpeechTool_WhenDeferredDispatchIsCancelledBeforeFlush_DoesNotDispatchOrRecord()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecordingVoice voice = new();
        CapturingAgenticMind mind = new()
        {
            Voice = voice,
        };
        AddTestNode(sceneTree, voice);
        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            AIFunction function = new SpeechTool().CreateFunction(mind);
            using CancellationTokenSource cancellation = new();
            ValueTask<object?> invocation = function.InvokeAsync(
                new AIFunctionArguments { ["speech"] = "must not dispatch" },
                cancellation.Token);
            cancellation.Cancel();

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(invocation.AsTask);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Empty(voice.SpokenLines);
            Assert.Empty(mind.GetTimelineForTest());
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind, voice);
        }
    }

    /// <summary>
    /// Removing a mind settles queued speech without dispatching or recording after its node lifetime ends.
    /// </summary>
    [Fact]
    public async Task SpeechTool_WhenMindExitsBeforeDeferredFlush_CancelsQueuedDispatch()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecordingVoice voice = new();
        CapturingAgenticMind mind = new()
        {
            Voice = voice,
        };
        Node parent = sceneTree.CurrentScene ?? sceneTree.Root;
        AddTestNode(sceneTree, voice);
        parent.AddChild(mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            AIFunction function = new SpeechTool().CreateFunction(mind);
            ValueTask<object?> invocation = function.InvokeAsync(
                new AIFunctionArguments { ["speech"] = "must not survive exit" },
                CancellationToken.None);

            parent.RemoveChild(mind);

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => invocation.AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            await TestUtils.WaitForNextFrameAsync(sceneTree);

            Assert.Empty(voice.SpokenLines);
            Assert.Empty(mind.GetTimelineForTest());
        }
        finally
        {
            mind.Free();
            await DestroyFixtureAsync(sceneTree, voice);
        }
    }

    /// <summary>
    /// A distinct source instance is attributed by its exact voice ID rather than object identity.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WithDistinctSameIDSource_CreatesRecognisedObservedSpeech()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecognitionTestMind mind = new()
        {
            ObservationImportanceThreshold = 1f
        };
        PlainVoice ownedVoice = new("voice.Mixed-Case");
        PlainVoice spoofedVoice = new("voice.Mixed-Case");
        TestCharacter owner = AddAgenticMindFixture(sceneTree, mind);
        TestCharacter speaker = AddSceneCharacter(sceneTree, "Known-Character", ownedVoice);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("  trimmed speech  ", spoofedVoice);
            await WaitUntilAsync(sceneTree, () => mind.Observations.Count == 1, maxFrames: 120);

            ObservedSpeech observation = Assert.IsType<ObservedSpeech>(Assert.Single(mind.Observations));
            Assert.Equal("voice.Mixed-Case", observation.VoiceId);
            Assert.Equal("Known-Character", observation.ActorId);
            Assert.Equal("trimmed speech", observation.Content);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, speaker, owner);
        }
    }

    /// <summary>
    /// A current character's composed voice ID is recognised even when it differs from the character ID.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WithCurrentCharacterOwnedSource_CreatesRecognisedObservedSpeech()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecognitionTestMind mind = new()
        {
            ObservationImportanceThreshold = 1f
        };
        PlainVoice voice = new("private-device-id");
        TestCharacter owner = AddAgenticMindFixture(sceneTree, mind);
        TestCharacter speaker = AddSceneCharacter(sceneTree, "Known Character", voice);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("unknown speaker", voice);
            await WaitUntilAsync(sceneTree, () => mind.Observations.Count == 1, maxFrames: 120);

            ObservedSpeech observation = Assert.IsType<ObservedSpeech>(Assert.Single(mind.Observations));
            Assert.Equal("private-device-id", observation.VoiceId);
            Assert.Equal("Known Character", observation.ActorId);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, speaker, owner);
        }
    }

    /// <summary>
    /// Case-distinct, blank, and unmatched source IDs remain unknown, and blank configured IDs never match.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WithNonMatchingOrBlankIDs_CreatesUnrecognisedObservedSpeech()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecognitionTestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        TestCharacter owner = AddAgenticMindFixture(sceneTree, mind);
        TestCharacter exactSpeaker = AddSceneCharacter(sceneTree, "ExactSpeaker", new PlainVoice("Voice.Exact"));
        TestCharacter blankSpeaker = AddSceneCharacter(sceneTree, "BlankSpeaker", new PlainVoice("  "));
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("case mismatch", new PlainVoice("voice.Exact"));
            mind.ReceiveVoice("blank source", new PlainVoice(" "));
            mind.ReceiveVoice("no match", new PlainVoice("missing"));
            await WaitUntilAsync(sceneTree, () => mind.Observations.Count == 3, maxFrames: 120);

            ObservedSpeech[] observations = [.. mind.Observations.Cast<ObservedSpeech>()];
            Assert.Equal(3, observations.Length);
            Assert.All(observations, observation => Assert.Null(observation.ActorId));
            Assert.Equal(["voice.Exact", " ", "missing"], observations.Select(observation => observation.VoiceId));
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, blankSpeaker, exactSpeaker, owner);
        }
    }

    /// <summary>
    /// A voice owned only by a character absent from the current scene remains unknown.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WithOffSceneOwnedSource_CreatesUnrecognisedObservedSpeech()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecognitionTestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        PlainVoice voice = new("off-scene-device");
        var offSceneCharacter = new TestCharacter(new Dictionary<string, object?>())
        {
            Id = "OffSceneCharacter",
            Components = [voice],
        };
        TestCharacter owner = AddAgenticMindFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("off scene speech", voice);
            await WaitUntilAsync(sceneTree, () => mind.Observations.Count == 1, maxFrames: 120);

            ObservedSpeech observation = Assert.IsType<ObservedSpeech>(Assert.Single(mind.Observations));
            Assert.Null(observation.ActorId);
            Assert.Equal("off-scene-device", observation.VoiceId);
        }
        finally
        {
            offSceneCharacter.Free();
            await DestroyFixtureAsync(sceneTree, owner);
        }
    }

    /// <summary>
    /// Duplicate exact configured voice IDs are rejected before the speech observation is ingested.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WithAmbiguousCurrentSceneOwnership_FailsClearly()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecognitionTestMind mind = new();
        PlainVoice source = new("shared-device");
        TestCharacter owner = AddAgenticMindFixture(sceneTree, mind);
        TestCharacter first = AddSceneCharacter(sceneTree, "FirstOwner", new PlainVoice("shared-device"));
        TestCharacter second = AddSceneCharacter(sceneTree, "SecondOwner", new PlainVoice("shared-device"));
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => mind.ReceiveVoice("ambiguous speech", source));

            Assert.Contains("ambiguously matches current-scene characters", exception.Message, StringComparison.Ordinal);
            Assert.Contains("FirstOwner", exception.Message, StringComparison.Ordinal);
            Assert.Contains("SecondOwner", exception.Message, StringComparison.Ordinal);
            Assert.Empty(mind.Observations);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, second, first, owner);
        }
    }

    /// <summary>
    /// Multiple speech actions should dispatch while later observations remain queued for the next turn.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WithMultipleSpeechActions_QueuesLaterObservationsForNextTurn()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecordingVoice npcVoice = new()
        {
            Id = "alley",
        };
        RecordingVoice playerVoice = new()
        {
            Id = "Speaker",
        };
        FakeClientProvider clientProvider = new()
        {
            FirstSpeech = "First reply.",
            SecondSpeech = "Second reply.",
        };
        CapturingAgenticMind mind = new()
        {
            ClientProvider = clientProvider,
            SystemInstruction = CreateTestSystemInstruction(),
            Voice = npcVoice,
            MaxObservationWaitSeconds = 0.05f,
            ObservationImportanceThreshold = 1f,
            Tools = [new SpeechTool()],
        };
        mind.SetDiagnosticsSettingsLoaderForTesting(() => new AIDiagnosticsSettings(EnableRequestResponseLogging: false));

        bool sentDuringTurn = false;
        clientProvider.AfterFirstSpeakAsync = () =>
        {
            if (!sentDuringTurn)
            {
                sentDuringTurn = true;
                mind.ReceiveVoice("interrupting player speech", playerVoice);
            }

            return Task.CompletedTask;
        };

        IServiceProvider toolServices = mind;
        Assert.Same(mind, toolServices.GetService(typeof(AgenticMind)));
        IVoice toolVoice = Assert.IsAssignableFrom<IVoice>(toolServices.GetService(typeof(IVoice)));
        Assert.Equal("alley", toolVoice.Id);

        AddTestNode(sceneTree, npcVoice);
        AddTestNode(sceneTree, playerVoice);
        TestCharacter character = AddAgenticMindFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("  hello Alley  ", playerVoice);

            await WaitUntilAsync(
                sceneTree,
                () => clientProvider.CreatedClients.Count > 0 && clientProvider.CreatedClients[0].Completed,
                maxFrames: 120);
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            FakeChatClient client = clientProvider.CreatedClients[0];
            Assert.Equal(3, client.ModelRequestCount);
            Assert.True(character.ContextRequestCount >= 1);
            Assert.NotNull(character.ReceivedScene);
            Assert.Same(character, character.ReceivedObserver);
            Assert.Contains("Heard an unknown speaker: hello Alley", client.Prompts[0]);
            Assert.Equal(3, client.MessageSnapshots.Count);
            Assert.Empty(client.MessageSnapshots[0]);
            Assert.Equal("Spoken through the configured voice.", client.FirstSpeakResult);
            Assert.False(client.CancellationObservedAfterFirstSpeak);
            Assert.True(client.ReturnedResponse);
            Assert.Equal("Spoken through the configured voice.", client.SecondSpeakResult);
            Assert.Equal(["First reply.", "Second reply."], npcVoice.SpokenLines.Take(2));
            Assert.DoesNotContain("interrupting player speech", client.Prompts[0], StringComparison.Ordinal);
            Assert.DoesNotContain("First reply.", client.Prompts[0], StringComparison.Ordinal);
            Assert.DoesNotContain("Second reply.", client.Prompts[0], StringComparison.Ordinal);

            Assert.Collection(
                mind.GetTimelineForTest().Cast<ObservedSpeech>().Take(4),
                observation =>
                {
                    Assert.Null(observation.ActorId);
                    Assert.Equal("Speaker", observation.VoiceId);
                    Assert.Equal("hello Alley", observation.Content);
                },
                observation =>
                {
                    Assert.Equal(character.Id, observation.ActorId);
                    Assert.Null(observation.VoiceId);
                    Assert.Equal("First reply.", observation.Content);
                },
                observation =>
                {
                    Assert.Null(observation.ActorId);
                    Assert.Equal("Speaker", observation.VoiceId);
                    Assert.Equal("interrupting player speech", observation.Content);
                },
                observation =>
                {
                    Assert.Equal(character.Id, observation.ActorId);
                    Assert.Null(observation.VoiceId);
                    Assert.Equal("Second reply.", observation.Content);
                });

            await WaitUntilAsync(sceneTree, () => clientProvider.CreatedClients.Count == 2 && clientProvider.Client is { Completed: true }, maxFrames: 120);
            string laterPrompt = clientProvider.CreatedClients[1].Prompts[0];
            Assert.True(laterPrompt.IndexOf("Heard an unknown speaker: hello Alley", StringComparison.Ordinal)
                < laterPrompt.IndexOf("Said: First reply.", StringComparison.Ordinal));
            Assert.True(laterPrompt.IndexOf("Said: First reply.", StringComparison.Ordinal)
                < laterPrompt.IndexOf("Heard an unknown speaker: interrupting player speech", StringComparison.Ordinal));
            Assert.True(laterPrompt.IndexOf("Heard an unknown speaker: interrupting player speech", StringComparison.Ordinal)
                < laterPrompt.IndexOf("Said: Second reply.", StringComparison.Ordinal));
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, character, playerVoice, npcVoice);
        }
    }

    /// <summary>
    /// Diagnostics must not alter a genuine production tool-only loop or its completion.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WithEquivalentDiagnosticsModes_PreservesToolOnlyLoopSemantics()
    {
        ToolLoopScenarioResult disabled = await RunToolLoopScenarioAsync(
            diagnosticsEnabled: false,
            cancelDuringSecondModelCall: false);
        ToolLoopScenarioResult enabled = await RunToolLoopScenarioAsync(
            diagnosticsEnabled: true,
            cancelDuringSecondModelCall: false);

        AssertEquivalentSemantics(disabled.Semantics, enabled.Semantics);
        Assert.Equal(3, enabled.Semantics.ModelInvocationCount);
        Assert.Equal(["First diagnostic reply.", "Second diagnostic reply."], enabled.Semantics.ToolInvocations);
        Assert.Equal(
            ["Spoken through the configured voice.", "Spoken through the configured voice."],
            enabled.Semantics.FunctionResults);
        Assert.Equal(3, enabled.Semantics.AdmittedEffects.Length);
        Assert.True(enabled.Semantics.TurnCompleted);
        Assert.False(enabled.Semantics.CancellationObserved);

        AssertDiagnosticsDisabled(disabled.LogEntries);
        AssertEnabledToolLoopDiagnostics(enabled.LogEntries, expectedRequestCount: 3, expectedResponseCount: 3);
    }

    /// <summary>
    /// Diagnostics must not alter cancellation settlement within a production tool-only loop.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WithEquivalentDiagnosticsModes_PreservesToolOnlyLoopCancellation()
    {
        ToolLoopScenarioResult disabled = await RunToolLoopScenarioAsync(
            diagnosticsEnabled: false,
            cancelDuringSecondModelCall: true);
        ToolLoopScenarioResult enabled = await RunToolLoopScenarioAsync(
            diagnosticsEnabled: true,
            cancelDuringSecondModelCall: true);

        AssertEquivalentSemantics(disabled.Semantics, enabled.Semantics);
        Assert.Equal(2, enabled.Semantics.ModelInvocationCount);
        Assert.Equal(["First diagnostic reply."], enabled.Semantics.ToolInvocations);
        Assert.Equal(["Spoken through the configured voice."], enabled.Semantics.FunctionResults);
        Assert.Equal(2, enabled.Semantics.AdmittedEffects.Length);
        Assert.False(enabled.Semantics.TurnCompleted);
        Assert.True(enabled.Semantics.CancellationObserved);

        AssertDiagnosticsDisabled(disabled.LogEntries);
        AssertEnabledToolLoopDiagnostics(enabled.LogEntries, expectedRequestCount: 2, expectedResponseCount: 1);
    }

    /// <summary>
    /// AgenticMind should bind the currently exported tools to every fresh provider turn.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WhenToolsChangeBetweenTurns_BindsCurrentToolsToFreshProviderTurn()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecordingVoice npcVoice = new()
        {
            Id = "alley",
        };
        RecordingVoice playerVoice = new()
        {
            Id = "Speaker",
        };
        FakeClientProvider clientProvider = new();
        AgenticMind mind = new()
        {
            ClientProvider = clientProvider,
            SystemInstruction = CreateTestSystemInstruction(),
            Voice = npcVoice,
            MaxObservationWaitSeconds = 0.05f,
            ObservationImportanceThreshold = 1f,
            Tools = [new MarkerTool("first_tool")],
        };

        AddTestNode(sceneTree, npcVoice);
        AddTestNode(sceneTree, playerVoice);
        TestCharacter character = AddAgenticMindFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("first turn", playerVoice);
            await WaitUntilAsync(sceneTree, () => clientProvider.Client is { ModelRequestCount: 2, Completed: true }, maxFrames: 120);

            mind.Tools = [new MarkerTool("second_tool")];
            clientProvider.Client!.Completed = false;
            mind.ReceiveVoice("second turn", playerVoice);

            await WaitUntilAsync(sceneTree, () => clientProvider.CreatedClients.Count == 2 && clientProvider.Client is { Completed: true }, maxFrames: 120);

            Assert.NotNull(clientProvider.Client);
            Assert.Equal(2, clientProvider.CreatedClients.Count);
            Assert.All(clientProvider.CreatedClients, client => Assert.Equal(2, client.ModelRequestCount));
            Assert.Equal("first_tool", Assert.Single(clientProvider.CreatedClients[0].ToolNamesByProviderTurn));
            Assert.Equal("second_tool", Assert.Single(clientProvider.CreatedClients[1].ToolNamesByProviderTurn));
            Assert.Empty(npcVoice.SpokenLines);
            string laterPrompt = clientProvider.CreatedClients[1].Prompts[0];
            Assert.True(laterPrompt.IndexOf("Heard an unknown speaker: first turn", StringComparison.Ordinal)
                < laterPrompt.IndexOf("Heard an unknown speaker: second turn", StringComparison.Ordinal));
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, character, playerVoice, npcVoice);
        }
    }

    /// <summary>
    /// Every turn rebuilds current prompt/context and starts with fresh provider protocol state.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_OnLaterTurn_RebuildsPromptAndUsesFreshProtocolState()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecordingVoice npcVoice = new()
        {
            Id = "alley",
        };
        PlainVoice playerVoice = new("Speaker");
        FakeClientProvider clientProvider = new();
        CountingPromptSection firstSection = new("First instruction for {{character.displayName}}.");
        CountingPromptSection secondSection = new("Second instruction for {{character.displayName}}.");
        PromptStack firstSystemInstruction = CreateCountingSystemInstruction(firstSection);
        PromptStack secondSystemInstruction = CreateCountingSystemInstruction(secondSection);
        Dictionary<string, object?> context = new()
        {
            ["displayName"] = "First Alley",
        };
        AgenticMind mind = new()
        {
            ClientProvider = clientProvider,
            SystemInstruction = firstSystemInstruction,
            Voice = npcVoice,
            MaxObservationWaitSeconds = 0.05f,
            ObservationImportanceThreshold = 1f,
            Tools = [new MarkerTool("cache_probe")],
        };

        AddTestNode(sceneTree, npcVoice);
        TestCharacter character = AddAgenticMindFixture(sceneTree, mind, context);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("first turn", playerVoice);
            await WaitUntilAsync(sceneTree, () => clientProvider.Client is { ModelRequestCount: 2, Completed: true }, maxFrames: 120);

            context["displayName"] = "Second Alley";
            mind.SystemInstruction = secondSystemInstruction;
            mind.ReceiveVoice("second turn", playerVoice);
            await WaitUntilAsync(sceneTree, () => clientProvider.CreatedClients.Count == 2 && clientProvider.Client is { Completed: true }, maxFrames: 120);

            Assert.Equal(1, firstSection.ContentRequestCount);
            Assert.Equal(1, secondSection.ContentRequestCount);
            Assert.Equal(2, clientProvider.CreatedClients.Count);
            Assert.All(clientProvider.CreatedClients, client => Assert.Equal(2, client.ModelRequestCount));
            Assert.Equal(2, character.ContextRequestCount);
            Assert.All(clientProvider.CreatedClients, client => Assert.Empty(client.MessageSnapshots[0]));
            Assert.Contains("First instruction for First Alley", clientProvider.CreatedClients[0].Prompts[0], StringComparison.Ordinal);
            Assert.Contains("Second instruction for Second Alley", clientProvider.CreatedClients[1].Prompts[0], StringComparison.Ordinal);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, character, npcVoice);
        }
    }

    /// <summary>
    /// The active turn uses the timeline captured with its claimed batch while later observations remain FIFO-queued.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_AfterBatchClaimBeforePromptConstruction_IsDeferredToNextTurnSnapshot()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        PlainVoice playerVoice = new("Speaker");
        FakeClientProvider clientProvider = new();
        TaskCompletionSource batchClaimed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releasePromptConstruction = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CapturingAgenticMind mind = new()
        {
            ClientProvider = clientProvider,
            SystemInstruction = CreateTestSystemInstruction(),
            ObservationImportanceThreshold = 1f,
            Tools = [],
            ObservationBatchClaimedHookForTesting = async cancellationToken =>
            {
                _ = batchClaimed.TrySetResult();
                await releasePromptConstruction.Task.WaitAsync(cancellationToken);
            },
        };

        TestCharacter character = AddAgenticMindFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("observation A", playerVoice);
            await batchClaimed.Task.WaitAsync(TimeSpan.FromSeconds(2));

            mind.ReceiveVoice("observation B", playerVoice);
            mind.ReceiveVoice("observation C", playerVoice);
            mind.ObservationBatchClaimedHookForTesting = null;
            _ = releasePromptConstruction.TrySetResult();

            await WaitUntilAsync(
                sceneTree,
                () => clientProvider.CreatedClients.Count == 2 && clientProvider.CreatedClients[1].Completed,
                maxFrames: 180);

            Assert.Equal(2, mind.ClaimedBatches.Count);
            Assert.Equal(
                ["observation A"],
                mind.ClaimedBatches[0].Cast<ObservedSpeech>().Select(observation => observation.Content));
            Assert.Equal(
                ["observation B", "observation C"],
                mind.ClaimedBatches[1].Cast<ObservedSpeech>().Select(observation => observation.Content));

            string activeInstruction = clientProvider.CreatedClients[0].Prompts[0];
            Assert.Contains("Heard an unknown speaker: observation A", activeInstruction, StringComparison.Ordinal);
            Assert.DoesNotContain("observation B", activeInstruction, StringComparison.Ordinal);
            Assert.DoesNotContain("observation C", activeInstruction, StringComparison.Ordinal);

            string subsequentInstruction = clientProvider.CreatedClients[1].Prompts[0];
            int firstIndex = subsequentInstruction.IndexOf("observation A", StringComparison.Ordinal);
            int secondIndex = subsequentInstruction.IndexOf("observation B", StringComparison.Ordinal);
            int thirdIndex = subsequentInstruction.IndexOf("observation C", StringComparison.Ordinal);
            Assert.True(firstIndex >= 0 && firstIndex < secondIndex && secondIndex < thirdIndex);
        }
        finally
        {
            _ = releasePromptConstruction.TrySetResult();
            await DestroyFixtureAsync(sceneTree, character);
        }
    }

    /// <summary>
    /// Exiting the tree cancels an active backend invocation and does not wait for backend completion.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WhenMindExitsDuringBlockedBackend_CancelsActiveTurn()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        PlainVoice playerVoice = new("Speaker");
        BlockingClientProvider clientProvider = new();
        AgenticMind mind = new()
        {
            ClientProvider = clientProvider,
            SystemInstruction = CreateTestSystemInstruction(),
            ObservationImportanceThreshold = 1f,
            Tools = [],
        };
        TestCharacter character = AddAgenticMindFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("start blocked backend", playerVoice);
            await clientProvider.InvocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            mind.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
            await clientProvider.InvocationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(clientProvider.CancellationObserved);
            Assert.False(clientProvider.ReturnedResponse);
            Assert.Equal(1, clientProvider.RunCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, character);
        }
    }

    /// <summary>
    /// Mind tree exit irreversibly closes intake and scheduling, cancels active work quietly, and rejects re-entry.
    /// </summary>
    [Fact]
    public async Task NodeLifetime_AfterExit_RejectsIntakeSchedulingAndReentryWithoutFailureDiagnostic()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node parent = sceneTree.CurrentScene ?? sceneTree.Root;
        PlainVoice externalVoice = new("external");
        LifetimeBoundaryTestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);

        parent.AddChild(mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            _ = mind.ObserveForTest(new TestObservation(1f, "active"));
            await mind.ProcessingStarted.WaitAsync(TimeSpan.FromSeconds(2));

            _ = mind.ObserveForTest(new TestObservation(1f, "pending"));
            mind.Enabled = false;
            mind.Enabled = true;
            IReadOnlyList<AgentObservation> timelineAtExit = mind.GetTimelineForTest();
            bool hadPendingAtExit = mind.HasPendingObservationsForTest;

            parent.RemoveChild(mind);
            await mind.ProcessingSettled.WaitAsync(TimeSpan.FromSeconds(2));

            (bool shouldProcess, bool shouldSchedule) = mind.ObserveForTest(
                new TestObservation(1f, "ignored direct intake"));
            mind.ReceiveVoice("ignored voice intake", externalVoice);
            _ = mind.ObserveForTest(new TestObservation(0f, "ignored intake"));
            await TestUtils.WaitForFramesAsync(sceneTree, 3);

            parent.AddChild(mind);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.False(mind.IsInsideTree());
            Assert.Null(mind.GetParent());
            Assert.Equal(1, mind.ReadyCallCount);
            Assert.True(mind.LifetimeCancellationObserved);
            Assert.True(mind.LifetimeEndedForTest);
            Assert.False(mind.Enabled);
            Assert.False(shouldProcess);
            Assert.False(shouldSchedule);
            Assert.Equal(timelineAtExit, mind.GetTimelineForTest());
            Assert.Equal(hadPendingAtExit, mind.HasPendingObservationsForTest);
            Assert.True(hadPendingAtExit);
            Assert.Equal(1, mind.ProcessCallCount);
            Assert.DoesNotContain(
                loggerProvider.Entries,
                entry => entry.Level >= LogLevel.Error);
        }
        finally
        {
            if (mind.GetParent() is { } currentParent)
            {
                currentParent.RemoveChild(mind);
            }

            if (mind.ProcessingStarted.IsCompleted)
            {
                await mind.ProcessingSettled.WaitAsync(TimeSpan.FromSeconds(2));
            }

            mind.Free();
        }
    }

    /// <summary>
    /// A configured voice and production action are both optional when the protocol returns end_turn.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WithoutVoiceOrTools_AcceptsNoActionEndTurn()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        PlainVoice playerVoice = new("Speaker");
        FakeClientProvider clientProvider = new();
        AgenticMind mind = new()
        {
            ClientProvider = clientProvider,
            SystemInstruction = CreateTestSystemInstruction(),
            ObservationImportanceThreshold = 1f,
            Tools = [],
        };
        TestCharacter character = AddAgenticMindFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("quiet turn", playerVoice);
            await WaitUntilAsync(sceneTree, () => clientProvider.Client is { Completed: true }, maxFrames: 120);

            FakeChatClient client = Assert.Single(clientProvider.CreatedClients);
            Assert.True(client.ReturnedResponse);
            Assert.Empty(client.ToolNamesByProviderTurn);
            Assert.Empty(client.MessageSnapshots[0]);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, character);
        }
    }

    /// <summary>
    /// Backend creation failures should be contained by AgenticMind so the scene keeps running and no NPC speech emits.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WhenBackendCreationFails_DoesNotCrashOrSpeak()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecordingVoice npcVoice = new()
        {
            Id = "alley",
        };
        RecordingVoice playerVoice = new()
        {
            Id = "Speaker",
        };
        ThrowingClientProvider clientProvider = new();
        AgenticMind mind = new()
        {
            ClientProvider = clientProvider,
            SystemInstruction = CreateTestSystemInstruction(),
            Voice = npcVoice,
            MaxObservationWaitSeconds = 0.05f,
            ObservationImportanceThreshold = 1f,
            Tools = [new SpeechTool()],
        };

        AddTestNode(sceneTree, npcVoice);
        AddTestNode(sceneTree, playerVoice);
        TestCharacter character = AddAgenticMindFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("backend unavailable", playerVoice);

            await WaitUntilAsync(sceneTree, () => clientProvider.CreateChatClientCallCount == 1, maxFrames: 120);
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            Assert.Empty(npcVoice.SpokenLines);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, character, playerVoice, npcVoice);
        }
    }

    /// <summary>
    /// AgenticMind should accept IVoice identifiers directly, without requiring the concrete Voice node type for input.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WithNonNodeIVoice_UsesInterfaceIdForPlayerRouting()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecordingVoice npcVoice = new()
        {
            Id = "alley",
        };
        PlainVoice playerVoice = new("Speaker");
        FakeClientProvider clientProvider = new()
        {
            FirstSpeech = "Interface reply.",
        };
        AgenticMind mind = new()
        {
            ClientProvider = clientProvider,
            SystemInstruction = CreateTestSystemInstruction(),
            Voice = npcVoice,
            MaxObservationWaitSeconds = 0.05f,
            ObservationImportanceThreshold = 1f,
            Tools = [new SpeechTool()],
        };

        AddTestNode(sceneTree, npcVoice);
        TestCharacter character = AddAgenticMindFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("hello through interface", playerVoice);

            await WaitUntilAsync(sceneTree, () => clientProvider.Client is { Completed: true }, maxFrames: 120);
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            Assert.NotNull(clientProvider.Client);
            Assert.Contains("Heard an unknown speaker: hello through interface", clientProvider.Client.Prompts[0]);
            Assert.NotEmpty(npcVoice.SpokenLines);
            Assert.All(npcVoice.SpokenLines, line => Assert.Equal("Interface reply.", line));
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, character, npcVoice);
        }
    }

    /// <summary>
    /// Below-threshold speech should wait for the configured maximum observation wait instead of polling frequently.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WhenBelowImportanceThreshold_RunsAfterMaxObservationWait()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecordingVoice npcVoice = new()
        {
            Id = "alley",
        };
        RecordingVoice playerVoice = new()
        {
            Id = "Speaker",
        };
        FakeClientProvider clientProvider = new()
        {
            FirstSpeech = "Delayed reply.",
        };
        AgenticMind mind = new()
        {
            ClientProvider = clientProvider,
            SystemInstruction = CreateTestSystemInstruction(),
            Voice = npcVoice,
            MaxObservationWaitSeconds = 0.05f,
            ObservationImportanceThreshold = 2f,
            Tools = [new SpeechTool()],
        };

        AddTestNode(sceneTree, npcVoice);
        AddTestNode(sceneTree, playerVoice);
        TestCharacter character = AddAgenticMindFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("below threshold", playerVoice);
            await TestUtils.WaitForNextFrameAsync(sceneTree);

            Assert.Null(clientProvider.Client);

            await WaitUntilAsync(sceneTree, () => clientProvider.Client is { Completed: true }, maxFrames: 120);
            Assert.Equal(["Delayed reply."], npcVoice.SpokenLines);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, character, playerVoice, npcVoice);
        }
    }

    /// <summary>
    /// The base Mind processing loop should run immediately once cumulative observation importance reaches the threshold.
    /// </summary>
    [Fact]
    public async Task Observe_WhenImportanceThresholdReached_ProcessesThroughBaseMindLoop()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };

        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "important"));

            await WaitUntilAsync(sceneTree, () => mind.ProcessedBatches.Count == 1, maxFrames: 120);

            Assert.Equal("important", Assert.IsType<TestObservation>(Assert.Single(Assert.Single(mind.ProcessedBatches))).Prompt);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Disabling Mind should stop deferred timer starts and preserve pending observations until re-enabled.
    /// </summary>
    [Fact]
    public async Task EnabledFalse_StopsDeferredTimerAndPreservesPendingObservationsUntilReenabled()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestMind mind = new()
        {
            MaxObservationWaitSeconds = 0.05f,
            ObservationImportanceThreshold = 2f,
        };

        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "deferred"));
            mind.Enabled = false;

            await TestUtils.WaitForFramesAsync(sceneTree, 20);
            Assert.Empty(mind.ProcessedBatches);
            Assert.True(mind.HasPendingObservationsForTest);

            mind.Enabled = true;

            await WaitUntilAsync(sceneTree, () => mind.ProcessedBatches.Count == 1, maxFrames: 120);
            Assert.Equal("deferred", Assert.IsType<TestObservation>(Assert.Single(Assert.Single(mind.ProcessedBatches))).Prompt);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Disabled AgenticMind should not queue voice observations or create backend clients.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WhenDisabled_DoesNotRunAgenticMindTurn()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecordingVoice npcVoice = new()
        {
            Id = "alley",
        };
        PlainVoice playerVoice = new("Speaker");
        FakeClientProvider clientProvider = new()
        {
            FirstSpeech = "Should not speak.",
        };
        AgenticMind mind = new()
        {
            ClientProvider = clientProvider,
            Enabled = false,
            SystemInstruction = CreateTestSystemInstruction(),
            Voice = npcVoice,
            MaxObservationWaitSeconds = 0.05f,
            ObservationImportanceThreshold = 1f,
            Tools = [new SpeechTool()],
        };

        AddTestNode(sceneTree, npcVoice);
        TestCharacter character = AddAgenticMindFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("ignored while disabled", playerVoice);

            await TestUtils.WaitForFramesAsync(sceneTree, 20);

            Assert.Null(clientProvider.Client);
            Assert.Empty(npcVoice.SpokenLines);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, character, npcVoice);
        }
    }

    private static TestCharacter AddAgenticMindFixture(
        SceneTree sceneTree,
        AgenticMind mind,
        IReadOnlyDictionary<string, object?>? context = null)
    {
        TestCharacter character = new(context ?? new Dictionary<string, object?>
        {
            ["displayName"] = "Integration Alley",
        })
        {
            Name = "AgenticMindFixtureCharacter",
            Id = "agentic-mind-fixture-character",
            Components = mind.Voice is IComponent voice ? [voice] : [],
        };

        character.AddChild(mind);
        AddTestNode(sceneTree, character);
        character.AddToGroup("Actors");

        return character;
    }

    private static TestCharacter AddSceneCharacter(SceneTree sceneTree, string id, IVoice voice)
    {
        var character = new TestCharacter(new Dictionary<string, object?>())
        {
            Name = id,
            Id = id,
            Components = [voice],
        };
        AddTestNode(sceneTree, character);
        character.AddToGroup("Actors");
        return character;
    }

    private static void AddTestNode(SceneTree sceneTree, Node node)
    {
        Node parent = sceneTree.CurrentScene ?? sceneTree.Root;
        parent.AddChild(node);
    }

    private static PromptStack CreateTestSystemInstruction() => new()
    {
        Sections =
        [
            new TextPromptSection
            {
                Name = "Test Instructions",
                Text = "CTX display name: {{character.displayName}}. Run the integration test turn.",
            },
            new EventHistoryPromptSection
            {
                Name = "Event History",
                Fragments =
                [
                    new EventHistoryPromptFragment
                    {
                        TypeKey = "speech.observed",
                        Source = "{{#if ActorId}}{{#if (eqOrdinal ActorId \"agentic-mind-fixture-character\")}}Said: {{Content}}{{else}}Heard {{ActorId}}: {{Content}}{{/if}}{{else}}Heard an unknown speaker: {{Content}}{{/if}}\n",
                    },
                ],
                FallbackSource = "((Received {{TypeKey}} event.))\n",
            },
        ],
    };

    private static PromptStack CreateCountingSystemInstruction(CountingPromptSection section) => new()
    {
        Sections = [section],
    };

    private static async Task<ToolLoopScenarioResult> RunToolLoopScenarioAsync(
        bool diagnosticsEnabled,
        bool cancelDuringSecondModelCall)
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecordingVoice npcVoice = new()
        {
            Id = "alley"
        };
        RecordingVoice playerVoice = new()
        {
            Id = "Speaker"
        };
        ToolOnlyLoopClientProvider clientProvider = new(cancelDuringSecondModelCall);
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        ScenarioAgenticMind mind = new()
        {
            ClientProvider = clientProvider,
            SystemInstruction = CreateTestSystemInstruction(),
            Voice = npcVoice,
            MaxObservationWaitSeconds = 0.05f,
            ObservationImportanceThreshold = 1f,
            Tools = [new SpeechTool()],
        };
        mind.SetDiagnosticsSettingsLoaderForTesting(
            () => new AIDiagnosticsSettings(EnableRequestResponseLogging: diagnosticsEnabled));

        AddTestNode(sceneTree, npcVoice);
        AddTestNode(sceneTree, playerVoice);
        TestCharacter character = AddAgenticMindFixture(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            AdmittedEffect[]? effectsBeforeCancellation = null;
            mind.ReceiveVoice("equivalent diagnostic scenario", playerVoice);
            if (cancelDuringSecondModelCall)
            {
                await clientProvider.SecondInvocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                effectsBeforeCancellation =
                [
                    .. mind.GetTimelineForTest().OfType<ObservedSpeech>().Select(AdmittedEffect.From),
                ];
                mind.QueueFree();
                await TestUtils.WaitForNextFrameAsync(sceneTree);
                await WaitUntilAsync(
                    sceneTree,
                    () => clientProvider.Client is { CancellationObserved: true },
                    maxFrames: 120);
            }
            else
            {
                await WaitUntilAsync(
                    sceneTree,
                    () => clientProvider.Client is { Completed: true },
                    maxFrames: 120);
            }

            await mind.ProcessingSettled.WaitAsync(TimeSpan.FromSeconds(2));
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            ToolOnlyLoopChatClient client = Assert.IsType<ToolOnlyLoopChatClient>(clientProvider.Client);
            ToolLoopSemantics semantics = new(
                client.InvocationCount,
                [.. npcVoice.SpokenLines],
                [.. client.FunctionResults],
                effectsBeforeCancellation
                    ?? [.. mind.GetTimelineForTest().OfType<ObservedSpeech>().Select(AdmittedEffect.From)],
                mind.TurnCompleted,
                client.CancellationObserved);
            return new ToolLoopScenarioResult(semantics, [.. loggerProvider.Entries]);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, character, playerVoice, npcVoice);
        }
    }

    private static void AssertEquivalentSemantics(ToolLoopSemantics expected, ToolLoopSemantics actual)
    {
        Assert.Equal(expected.ModelInvocationCount, actual.ModelInvocationCount);
        Assert.Equal(expected.ToolInvocations, actual.ToolInvocations);
        Assert.Equal(expected.FunctionResults, actual.FunctionResults);
        Assert.Equal(expected.AdmittedEffects, actual.AdmittedEffects);
        Assert.Equal(expected.TurnCompleted, actual.TurnCompleted);
        Assert.Equal(expected.CancellationObserved, actual.CancellationObserved);
    }

    private static void AssertDiagnosticsDisabled(IReadOnlyList<RecordingLoggerProvider.LogEntry> entries)
    {
        Assert.DoesNotContain(entries, entry =>
            entry.Category == "Microsoft.Extensions.AI.LoggingChatClient");
        Assert.DoesNotContain(entries, entry =>
            entry.Message.Contains("runtime-sensitive-initial-call", StringComparison.Ordinal)
            || entry.Message.Contains("runtime-sensitive-intermediate-call", StringComparison.Ordinal)
            || entry.Message.Contains("Spoken through the configured voice.", StringComparison.Ordinal));
    }

    private static void AssertEnabledToolLoopDiagnostics(
        IReadOnlyList<RecordingLoggerProvider.LogEntry> entries,
        int expectedRequestCount,
        int expectedResponseCount)
    {
        RecordingLoggerProvider.LogEntry[] requestEntries =
        [
            .. entries.Where(entry =>
                entry.Category == "Microsoft.Extensions.AI.LoggingChatClient"
                && entry.Level == LogLevel.Trace
                && entry.Message.Contains(" invoked:", StringComparison.Ordinal)),
        ];
        RecordingLoggerProvider.LogEntry[] responseEntries =
        [
            .. entries.Where(entry =>
                entry.Category == "Microsoft.Extensions.AI.LoggingChatClient"
                && entry.Level == LogLevel.Trace
                && entry.Message.Contains(" completed:", StringComparison.Ordinal)),
        ];

        Assert.Equal(expectedRequestCount, requestEntries.Length);
        Assert.Equal(expectedResponseCount, responseEntries.Length);
        Assert.Contains("runtime-sensitive-initial-call", responseEntries[0].Message, StringComparison.Ordinal);
        if (expectedResponseCount == 3)
        {
            Assert.Contains("runtime-sensitive-intermediate-call", responseEntries[1].Message, StringComparison.Ordinal);
            Assert.Contains(ToolOnlyTurnRunner.EndTurnToolName, responseEntries[2].Message, StringComparison.Ordinal);
        }

        Assert.Contains(requestEntries.Skip(1), entry =>
            entry.Message.Contains("Spoken through the configured voice.", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry =>
            entry.Category is "System.ClientModel.Primitives.MessageLoggingPolicy"
                or "System.ClientModel.Primitives.PipelineTransport");
        Assert.DoesNotContain(entries, entry =>
            entry.Category == typeof(AgenticMind).FullName && entry.Level >= LogLevel.Error);
    }

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> predicate, int maxFrames)
    {
        for (int frame = 0; frame < maxFrames; frame++)
        {
            if (predicate())
            {
                return;
            }

            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(predicate(), $"Condition was not met within {maxFrames} frames.");
    }

    private static async Task DestroyFixtureAsync(SceneTree sceneTree, params Node[] nodes)
    {
        foreach (Node node in nodes)
        {
            node.QueueFree();
        }

        await TestUtils.WaitForFramesAsync(sceneTree, 2);
    }

    private sealed partial class RecordingVoice : Voice
    {
        public List<string> SpokenLines { get; } = [];

        public override void Speak(string speech)
            => base.Speak(speech);

        public override ValueTask SpeakAsync(
            string speech,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string acceptedSpeech = ValidateSubmission(speech);
            SpokenLines.Add(acceptedSpeech);
            _ = TryNotifySpeechGeneratedWhenEnabled(acceptedSpeech);
            return ValueTask.CompletedTask;
        }
    }

    private sealed partial class TestMind : MindBase
    {
        private readonly StandaloneCharacter _owner = new();

        public List<IReadOnlyList<AgentObservation>> ProcessedBatches { get; } = [];

        public bool HasPendingObservationsForTest => HasPendingObservations;

        public void ObserveForTest(AgentObservation observation) => _ = Observe(observation);

        public bool ShouldHandleVoiceForTest(string speech, IVoice source) => ShouldHandleVoice(speech, source);

        public override void ReceiveVoice(string speech, IVoice source)
        {
            if (ShouldHandleVoice(speech, source))
            {
                _ = Observe(new TestObservation(1f, speech.Trim()));
            }
        }

        protected override Task ProcessObservationsAsync(
            IReadOnlyList<AgentObservation> observations,
            IReadOnlyList<AgentObservation> timelineSnapshot,
            CancellationToken cancellationToken)
        {
            ProcessedBatches.Add([.. observations]);
            return Task.CompletedTask;
        }

        protected override ICharacter ResolveOwningCharacter() => _owner;
    }

    private sealed partial class LifetimeBoundaryTestMind : MindBase
    {
        private readonly StandaloneCharacter _owner = new();
        private readonly TaskCompletionSource _processingStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _processingSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ProcessingStarted => _processingStarted.Task;

        public Task ProcessingSettled => _processingSettled.Task;

        public int ProcessCallCount
        {
            get; private set;
        }

        public int ReadyCallCount
        {
            get; private set;
        }

        public bool HasPendingObservationsForTest => HasPendingObservations;

        public bool LifetimeCancellationObserved
        {
            get; private set;
        }

        public bool LifetimeEndedForTest => IsNodeLifetimeEnded;

        public override void _Ready()
        {
            base._Ready();
            ReadyCallCount++;
        }

        public (bool ShouldProcess, bool ShouldSchedule) ObserveForTest(AgentObservation observation)
        {
            MindScheduleDecision decision = Observe(observation);
            return (decision.ShouldProcessImmediately, decision.ShouldEnsureIntervalScheduled);
        }

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        public override void ReceiveVoice(string speech, IVoice source)
        {
            if (ShouldHandleVoice(speech, source))
            {
                _ = Observe(new TestObservation(1f, speech.Trim()));
            }
        }

        protected override async Task ProcessObservationsAsync(
            IReadOnlyList<AgentObservation> observations,
            IReadOnlyList<AgentObservation> timelineSnapshot,
            CancellationToken cancellationToken)
        {
            _ = observations;
            _ = timelineSnapshot;
            ProcessCallCount++;
            _ = _processingStarted.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                LifetimeCancellationObserved = true;
                throw;
            }
            finally
            {
                _ = _processingSettled.TrySetResult();
            }
        }

        protected override ICharacter ResolveOwningCharacter() => _owner;
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly Lock _entriesLock = new();
        private readonly List<LogEntry> _entries = [];
        private bool _disposed;

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entriesLock)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

        public void Dispose() => _disposed = true;

        private void Record(LogLevel level, string category, string message, Exception? exception)
        {
            if (_disposed)
            {
                return;
            }

            lock (_entriesLock)
            {
                _entries.Add(new LogEntry(level, category, message, exception));
            }
        }

        private sealed class RecordingLogger(RecordingLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel is not LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _ = eventId;
                provider.Record(logLevel, category, formatter(state, exception), exception);
            }
        }

        public sealed record LogEntry(LogLevel Level, string Category, string Message, Exception? Exception);
    }

    private sealed partial class RecognitionTestMind : AgenticMind
    {
        public List<AgentObservation> Observations { get; } = [];

        protected override Task ProcessObservationsAsync(
            IReadOnlyList<AgentObservation> observations,
            IReadOnlyList<AgentObservation> timelineSnapshot,
            CancellationToken cancellationToken)
        {
            Observations.AddRange(observations);
            return Task.CompletedTask;
        }
    }

    private sealed class StandaloneCharacter : ICharacter
    {
        public string Id { get; set; } = "standalone-mind-owner";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
    }

    private sealed partial class CapturingAgenticMind : AgenticMind
    {
        public List<IReadOnlyList<AgentObservation>> ClaimedBatches { get; } = [];

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        protected override Task ProcessObservationsAsync(
            IReadOnlyList<AgentObservation> observations,
            IReadOnlyList<AgentObservation> timelineSnapshot,
            CancellationToken cancellationToken)
        {
            ClaimedBatches.Add([.. observations]);
            return base.ProcessObservationsAsync(observations, timelineSnapshot, cancellationToken);
        }
    }

    private sealed partial class ScenarioAgenticMind : AgenticMind
    {
        private readonly TaskCompletionSource _processingSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ProcessingSettled => _processingSettled.Task;

        public bool TurnCompleted
        {
            get;
            private set;
        }

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        protected override async Task ProcessObservationsAsync(
            IReadOnlyList<AgentObservation> observations,
            IReadOnlyList<AgentObservation> timelineSnapshot,
            CancellationToken cancellationToken)
        {
            try
            {
                await base.ProcessObservationsAsync(observations, timelineSnapshot, cancellationToken);
                TurnCompleted = true;
            }
            finally
            {
                _ = _processingSettled.TrySetResult();
            }
        }
    }

    private sealed record ToolLoopScenarioResult(
        ToolLoopSemantics Semantics,
        RecordingLoggerProvider.LogEntry[] LogEntries);

    private sealed record ToolLoopSemantics(
        int ModelInvocationCount,
        string[] ToolInvocations,
        string[] FunctionResults,
        AdmittedEffect[] AdmittedEffects,
        bool TurnCompleted,
        bool CancellationObserved);

    private sealed record AdmittedEffect(string? ActorID, string? VoiceID, string Content)
    {
        public static AdmittedEffect From(ObservedSpeech speech)
            => new(speech.ActorId, speech.VoiceId, speech.Content);
    }

    private sealed record TestObservation(float Importance, string Prompt) : AgentObservation
    {
        public override string TypeKey => "test.observation";

        public override float CalculateImportance(ObservationContext context) => Importance;

    }

    private sealed partial class TestCharacter(IReadOnlyDictionary<string, object?> context) : Node, ICharacter
    {
        public string Id { get; set; } = string.Empty;

        public IReadOnlyList<IComponent> Components { get; init; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public int ContextRequestCount
        {
            get; private set;
        }

        public ISceneContext? ReceivedScene
        {
            get; private set;
        }

        public IContextual? ReceivedObserver
        {
            get; private set;
        }

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
        {
            ContextRequestCount++;
            ReceivedScene = scene;
            ReceivedObserver = observer;

            return context;
        }
    }

    private sealed partial class CountingPromptSection : PromptSection
    {
        private readonly string _text;

        public CountingPromptSection()
            : this(string.Empty)
        {
        }

        public CountingPromptSection(string text)
        {
            _text = text;
            Name = "Counting Instructions";
        }

        public int ContentRequestCount
        {
            get; private set;
        }

        public override Task<string> GetContentAsync(
            PromptSectionBuildContext buildContext,
            CancellationToken cancellationToken = default)
        {
            ContentRequestCount++;
            return Task.FromResult(_text);
        }
    }

    private sealed class FakeClientProvider : ClientProvider
    {
        public string FirstSpeech { get; init; } = string.Empty;

        public string SecondSpeech { get; init; } = string.Empty;

        public Func<Task>? AfterFirstSpeakAsync
        {
            get;
            set;
        }

        public FakeChatClient? Client
        {
            get;
            private set;
        }

        public List<FakeChatClient> CreatedClients { get; } = [];

        public override IChatClient CreateChatClient()
        {
            Client = new FakeChatClient(FirstSpeech, SecondSpeech, AfterFirstSpeakAsync);
            CreatedClients.Add(Client);
            return Client;
        }
    }

    private sealed class ToolOnlyLoopClientProvider(bool cancelDuringSecondModelCall) : ClientProvider
    {
        public TaskCompletionSource SecondInvocationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ToolOnlyLoopChatClient? Client
        {
            get;
            private set;
        }

        public override IChatClient CreateChatClient()
        {
            Client = new ToolOnlyLoopChatClient(cancelDuringSecondModelCall, SecondInvocationStarted);
            return Client;
        }
    }

    private sealed class ToolOnlyLoopChatClient(
        bool cancelDuringSecondModelCall,
        TaskCompletionSource secondInvocationStarted) : IChatClient
    {
        private const string InitialCallID = "runtime-sensitive-initial-call";
        private const string IntermediateCallID = "runtime-sensitive-intermediate-call";

        public int InvocationCount
        {
            get;
            private set;
        }

        public bool Completed
        {
            get;
            private set;
        }

        public bool CancellationObserved
        {
            get;
            private set;
        }

        public List<string> FunctionResults { get; } = [];

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(["speak", ToolOnlyTurnRunner.EndTurnToolName], options?.Tools?.Select(tool => tool.Name));
            Assert.Null(options?.ResponseFormat);
            _ = Assert.IsType<RequiredChatToolMode>(options?.ToolMode);
            InvocationCount++;

            ChatMessage[] request = [.. messages];
            if (InvocationCount > 1)
            {
                FunctionResultContent[] results =
                [
                    .. request.SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
                ];
                Assert.Equal(InvocationCount - 1, results.Length);
                FunctionResults.Add(Assert.IsType<string>(results[^1].Result));
            }

            if (InvocationCount == 2 && cancelDuringSecondModelCall)
            {
                _ = secondInvocationStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    CancellationObserved = true;
                    throw;
                }
            }

            ChatMessage response = InvocationCount switch
            {
                1 => CreateSpeechCall(InitialCallID, "First diagnostic reply."),
                2 => CreateSpeechCall(IntermediateCallID, "Second diagnostic reply."),
                3 => CreateCall("end-call", ToolOnlyTurnRunner.EndTurnToolName),
                _ => throw new InvalidOperationException("The tool-only loop made an unexpected model invocation."),
            };
            Completed = InvocationCount == 3;
            return new ChatResponse(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
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

        private static ChatMessage CreateSpeechCall(string callID, string speech)
            => new(
                ChatRole.Assistant,
                [new FunctionCallContent(
                    callID,
                    "speak",
                    new Dictionary<string, object?> { ["speech"] = speech })]);

        private static ChatMessage CreateCall(string callID, string name)
            => new(
                ChatRole.Assistant,
                [new FunctionCallContent(callID, name, new Dictionary<string, object?>())]);
    }

    private sealed class ThrowingClientProvider : ClientProvider
    {
        public int CreateChatClientCallCount
        {
            get;
            private set;
        }

        public override IChatClient CreateChatClient()
        {
            CreateChatClientCallCount++;
            throw new InvalidOperationException("Backend configuration is invalid for test.");
        }
    }

    private sealed class BlockingClientProvider : ClientProvider
    {
        public TaskCompletionSource InvocationStarted
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource InvocationCompleted
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RunCount
        {
            get;
            private set;
        }

        public bool CancellationObserved
        {
            get;
            private set;
        }

        public bool ReturnedResponse
        {
            get;
            private set;
        }

        public override IChatClient CreateChatClient() => new BlockingChatClient(this);

        private sealed class BlockingChatClient(BlockingClientProvider owner) : IChatClient
        {
            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                _ = messages;
                _ = options;
                owner.RunCount++;
                _ = owner.InvocationStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    owner.ReturnedResponse = true;
                    return new ChatResponse(new ChatMessage(
                        ChatRole.Assistant,
                        [new FunctionCallContent(
                            "end-call",
                            ToolOnlyTurnRunner.EndTurnToolName,
                            new Dictionary<string, object?>())]));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    owner.CancellationObserved = true;
                    throw;
                }
                finally
                {
                    _ = owner.InvocationCompleted.TrySetResult();
                }
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
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

    private sealed class FakeChatClient(
        string firstSpeech,
        string secondSpeech,
        Func<Task>? afterFirstSpeakAsync) : IChatClient
    {
        public int ModelRequestCount
        {
            get;
            private set;
        }

        public bool Completed
        {
            get;
            set;
        }

        public List<string> Prompts { get; } = [];

        public List<ChatMessage[]> MessageSnapshots { get; } = [];

        public List<string> ToolNamesByProviderTurn { get; } = [];

        public bool CancellationObservedAfterFirstSpeak
        {
            get;
            private set;
        }

        public bool ReturnedResponse
        {
            get;
            private set;
        }

        public string? FirstSpeakResult
        {
            get;
            private set;
        }

        public string? SecondSpeakResult
        {
            get;
            private set;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ModelRequestCount++;
            ChatMessage[] messageSnapshot = [.. messages];
            MessageSnapshots.Add(messageSnapshot);
            Assert.NotNull(options);
            Assert.False(string.IsNullOrWhiteSpace(options.Instructions));
            Prompts.Add(options.Instructions);

            try
            {
                Assert.Null(options.ResponseFormat);
                _ = Assert.IsType<RequiredChatToolMode>(options.ToolMode);
                Assert.NotNull(options.Tools);
                AIFunction[] actionFunctions =
                [
                    .. options.Tools
                        .OfType<AIFunction>()
                        .Where(function => function.Name != ToolOnlyTurnRunner.EndTurnToolName),
                ];
                Assert.Contains(
                    options.Tools,
                    tool => tool.Name == ToolOnlyTurnRunner.EndTurnToolName);
                if (actionFunctions.Length == 0)
                {
                    ReturnedResponse = true;
                    return EndTurnResponse();
                }

                AIFunction toolFunction = Assert.Single(actionFunctions);
                if (ToolNamesByProviderTurn.Count == 0)
                {
                    ToolNamesByProviderTurn.Add(toolFunction.Name);
                }

                if (toolFunction.Name != "speak")
                {
                    if (ModelRequestCount == 1)
                    {
                        return CallResponse("marker-call", toolFunction.Name, new Dictionary<string, object?>());
                    }

                    ReturnedResponse = true;
                    return EndTurnResponse();
                }

                FunctionResultContent[] results =
                [
                    .. messageSnapshot.SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
                ];
                if (results.Length >= 1)
                {
                    FirstSpeakResult = results[0].Result?.ToString();
                }

                if (results.Length >= 2)
                {
                    SecondSpeakResult = results[1].Result?.ToString();
                }

                switch (ModelRequestCount)
                {
                    case 1 when !string.IsNullOrWhiteSpace(firstSpeech):
                        return CallResponse(
                            "first-speak-call",
                            "speak",
                            new Dictionary<string, object?> { ["speech"] = firstSpeech });
                    case 2 when !string.IsNullOrWhiteSpace(secondSpeech):
                        if (afterFirstSpeakAsync is not null)
                        {
                            await afterFirstSpeakAsync();
                        }

                        return CallResponse(
                            "second-speak-call",
                            "speak",
                            new Dictionary<string, object?> { ["speech"] = secondSpeech });
                    default:
                        ReturnedResponse = true;
                        return EndTurnResponse();
                }
            }
            finally
            {
                Completed = ReturnedResponse;
            }
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
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

        private static ChatResponse CallResponse(
            string callID,
            string name,
            IDictionary<string, object?> arguments)
            => new(new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent(callID, name, arguments)]));

        private static ChatResponse EndTurnResponse()
            => CallResponse(
                "end-call",
                ToolOnlyTurnRunner.EndTurnToolName,
                new Dictionary<string, object?>());
    }

    private sealed class PlainVoice(string id) : IVoice
    {
        public string Id => id;

        public Vector3 Origin => Vector3.Zero;

        public void Speak(string speech)
        {
        }

        public ValueTask SpeakAsync(
            string speech,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Speak(speech);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ToolInvocationProvider(
        AgenticMind mind,
        ICharacter character,
        IVoice? voice) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IVoice) ? voice
                : serviceType == typeof(ICharacter) ? character
                : serviceType.IsInstanceOfType(mind) ? mind
                : null;
    }

    private sealed class ThrowingVoice : IVoice
    {
        public string Id => "throwing";

        public Vector3 Origin => Vector3.Zero;

        public void Speak(string speech) => throw new InvalidOperationException("Dispatch failed.");

        public ValueTask SpeakAsync(
            string speech,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Speak(speech);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingVoice : IVoice
    {
        public string Id => "rejecting";

        public Vector3 Origin => Vector3.Zero;

        public void Speak(string speech) => throw new InvalidOperationException("Failing voice must not dispatch.");

        public ValueTask SpeakAsync(
            string speech,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Speech submission failed for test.");
        }
    }

    private sealed partial class MarkerTool : AgentTool
    {
        public MarkerTool(string toolName)
        {
            ToolName = toolName;
            ToolDescription = "Records that this test tool was selected.";
        }

        protected override Delegate CreateDelegate() => Mark;

        private static ValueTask<AgentToolResult> Mark()
            => ValueTask.FromResult(new AgentToolResult("Marked."));
    }
}
