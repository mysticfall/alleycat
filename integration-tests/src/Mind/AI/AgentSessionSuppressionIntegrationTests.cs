using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
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
/// Godot-runtime coverage for the <c>--no-ai</c> process-wide agent-session suppression: a suppressed AgenticMind
/// never creates a chat client or issues a request, emits exactly one Information notice naming the switch without
/// any Error-level noise, and still ingests observations into its timeline, while the unsuppressed control path
/// still starts its session.
/// </summary>
[Headless]
public sealed partial class AgentSessionSuppressionIntegrationTests
{
    /// <summary>
    /// A suppressed mind never starts its session — no chat client is created, no request leaves, no render context
    /// is published, the notice is logged exactly once per process even for a second suppressed mind, and the
    /// observation timeline still ingests.
    /// </summary>
    [Fact]
    public async Task SuppressedMind_NeverStartsSessionAndStillIngestsObservations()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        ScriptedSessionClientProvider clientProvider = new();
        TestAgenticMind mind = CreateWiredMind(owner, player, clientProvider);
        TestAgenticMind secondMind = CreateWiredMind(owner, player, clientProvider);

        AgentSessionSuppression.DisableForTesting();
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 6);

            // Suppression blocks the whole session pipeline: no chat client, no request, no published context.
            Assert.Equal(0, clientProvider.CreateChatClientCallCount);
            Assert.Empty(clientProvider.Requests);
            Assert.Empty(mind.GetLatestRenderContext());

            // The suppression surfaces as exactly one Information notice naming the switch, never an Error.
            _ = Assert.Single(loggerProvider.Entries, entry =>
                entry.Level == LogLevel.Information
                && entry.Message.Contains(AgentSessionSuppression.NoAISwitch, StringComparison.Ordinal));
            Assert.DoesNotContain(loggerProvider.Entries, entry => entry.Level == LogLevel.Error);

            // A second suppressed mind in the same process adds no further notice and no session activity.
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(secondMind);
            await TestUtils.WaitForFramesAsync(sceneTree, 4);
            _ = Assert.Single(loggerProvider.Entries, entry =>
                entry.Level == LogLevel.Information
                && entry.Message.Contains(AgentSessionSuppression.NoAISwitch, StringComparison.Ordinal));
            Assert.Equal(0, clientProvider.CreateChatClientCallCount);
            Assert.Empty(clientProvider.Requests);

            // Perception stays live: the timeline still ingests observations while no session exists.
            mind.ObserveForTest(new TestObservation(1f, "still-observed"));
            await TestUtils.WaitForFramesAsync(sceneTree, 4);
            Assert.Equal(["still-observed"], TimelineValues(mind));
            Assert.Empty(clientProvider.Requests);
        }
        finally
        {
            AgentSessionSuppression.ResetForTesting();
            mind.QueueFree();
            secondMind.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// Without suppression the same wired mind still starts its session: the provider creates a chat client and the
    /// session issues its first request, proving the gate-open path is intact.
    /// </summary>
    [Fact]
    public async Task UnsuppressedMind_WithoutSuppressionSwitch_StartsItsSession()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(requestStarted);
        TestAgenticMind mind = CreateWiredMind(owner, player, clientProvider);
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The gate-open control path is intact: one chat client served the started session's first request.
            Assert.Equal(1, clientProvider.CreateChatClientCallCount);
            _ = Assert.Single(clientProvider.Requests);
            Assert.DoesNotContain(loggerProvider.Entries, entry =>
                entry.Message.Contains(AgentSessionSuppression.NoAISwitch, StringComparison.Ordinal));

            // Node exit ends the held request quietly.
            (sceneTree.CurrentScene ?? sceneTree.Root).RemoveChild(mind);
            await WaitUntilAsync(sceneTree, clientProvider.EndedByCancellation);
        }
        finally
        {
            AgentSessionSuppression.ResetForTesting();
            mind.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    private static TestAgenticMind CreateWiredMind(
        ICharacter owner,
        FixturePlayerCharacter player,
        ClientProvider clientProvider)
    {
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        return mind;
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
        public override string TypeKey => ObservedSpeech.TypeKeyValue;

        public override float CalculateImportance(ObservationContext context) => Importance;
    }

    private sealed partial class TestAgenticMind(ICharacter owner) : AgenticMind
    {
        public void ObserveForTest(AgentObservation observation) => Observe(observation);

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    /// <summary>
    /// Scripted provider whose client records every request and served step; unscripted requests fail loudly so
    /// unexpected session activity surfaces in assertions.
    /// </summary>
    private sealed partial class ScriptedSessionClientProvider : ClientProvider
    {
        private readonly Queue<Func<CancellationToken, Task<ChatResponse>>> _steps = new();

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public int CreateChatClientCallCount => Volatile.Read(ref _createChatClientCallCount);

        public void EnqueueHold(TaskCompletionSource started)
            => _steps.Enqueue(async cancellationToken =>
            {
                _ = started.TrySetResult();
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

        public bool EndedByCancellation()
            => Volatile.Read(ref _endedByCancellation) != 0;

        public override IChatClient CreateChatClient()
        {
            _ = Interlocked.Increment(ref _createChatClientCallCount);
            return new ScriptedClient(this);
        }

        private int _createChatClientCallCount;
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
