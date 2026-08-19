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
/// notable-signal interruption bridge, and node-exit cancellation of generation and tool work.
/// </summary>
[Headless]
public sealed partial class AgenticMindSessionLifecycleIntegrationTests
{
    /// <summary>
    /// Missing session configuration is contained: the failure is logged once, the session never issues a
    /// request, and it stays ended for the node's remaining lifetime (AI-002 TR-1/2).
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
    /// A notable signal during generation interrupts the in-flight request, injects the rendered notable summary
    /// as a user message, and resumes with a fresh request replaying the complete transcript (AI-001 TR-6,
    /// AI-002 TR-40/41).
    /// </summary>
    [Fact]
    public async Task NotableSignal_DuringGeneration_InjectsRenderedSummaryAndResumesWithFullTranscript()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CapturingTool tool = new();
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        clientProvider.EnqueueCall("capture_context");
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

            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            IReadOnlyList<ChatMessage> freshRequest = clientProvider.Requests[1];
            // The fresh request replays the session bootstrap input followed by the injected notable summary
            // (AI-002 TR-7/40).
            Assert.Equal(
                [ChatRole.User, ChatRole.User],
                freshRequest.Select(message => message.Role));
            ChatMessage injected = freshRequest[1];
            Assert.Equal(AgenticMind.SessionBootstrapInput, freshRequest[0].Text);
            Assert.Contains("Important scene events require your attention:", injected.Text, StringComparison.Ordinal);
            Assert.Contains("test.lifecycle", injected.Text, StringComparison.Ordinal);

            await WaitUntilAsync(sceneTree, () => tool.CapturedContexts.Count == 1);
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 3);
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
                clientProvider.Requests[2].Select(message => message.Role));

            // Node exit ends the held third request quietly.
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);

            Assert.Equal(3, clientProvider.Requests.Count);
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
    /// Node exit cancels an in-flight generation request: the session ends quietly without retry and without
    /// backend-failure diagnostics, while the Mind timeline persists (AI-002 TR-44, AI-001 TR-18).
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
    /// ends quietly without backend-failure diagnostics (AI-002 TR-39/44/45).
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

    private sealed record TestObservation(float Importance, string Value) : AgentObservation
    {
        public override string TypeKey => "test.lifecycle";

        public override float CalculateImportance(ObservationContext context) => Importance;
    }

    private sealed partial class TestAgenticMind(ICharacter owner) : AgenticMind
    {
        public void ObserveForTest(AgentObservation observation) => Observe(observation);

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        protected override ICharacter ResolveOwningCharacter() => owner;
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

        public void EnqueueCall(string toolName)
            => _steps.Enqueue(cancellationToken => Task.FromResult(new ChatResponse(
                new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent($"call-{Requests.Count + 1}", toolName, new Dictionary<string, object?>())]))));

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

    private sealed class TestCharacter : ICharacter
    {
        public string Id { get; set; } = "owner";

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?> { ["FullId"] = FullId };
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
