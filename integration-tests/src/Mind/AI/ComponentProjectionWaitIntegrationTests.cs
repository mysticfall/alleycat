using AlleyCat.Core;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Coverage of the session-start wait for the owning character's component projection (AI-002 TR-1, Technical
/// Acceptance 28): node-lifetime cancellation while preparation is parked in that wait ends the session quietly.
/// </summary>
/// <remarks>
/// The host is a real character that never builds its projection, standing in for a role installation that has not
/// refreshed components yet; no role installer or reference body is needed to park the wait.
/// </remarks>
[Headless]
public sealed class ComponentProjectionWaitIntegrationTests
{
    private const string HostId = "projection_wait_host";

    /// <summary>The message <see cref="AgenticMind" /> logs for a contained session (backend) failure.</summary>
    private const string SessionFailureMessage = "AgenticMind agent session failed.";

    /// <summary>
    /// Removing and freeing a never-projected host while its mind waits for the projection ends the session without a
    /// session failure, without creating or calling a chat client, and without an active runner.
    /// </summary>
    [Fact]
    public async Task HostExitDuringProjectionWait_EndsTheSessionQuietly()
    {
        SceneTree tree = GetSceneTree();
        await using ParkedSession parked = await ParkedSession.StartAsync(tree);

        parked.MarkLifetimeEnd();
        tree.Root.RemoveChild(parked.Host);
        parked.Host.Free();
        await WaitForFramesAsync(tree, 10);

        parked.AssertEndedQuietly();
    }

    /// <summary>
    /// Freeing only the mind while it waits for the projection ends the wait itself: a later projection refresh on the
    /// still-live host resumes nothing: preparation never continues past the wait (its next step, the scene-context
    /// load, never happens), no chat client is ever created (which would precede any runner), and no session failure
    /// is logged. A wait that outlived the mind's lifetime would be completed by that refresh and resume preparation.
    /// </summary>
    [Fact]
    public async Task MindExitDuringProjectionWait_EndsTheWaitBeforeALaterRefresh()
    {
        SceneTree tree = GetSceneTree();
        await using ParkedSession parked = await ParkedSession.StartAsync(tree);

        parked.MarkLifetimeEnd();
        parked.Host.RemoveChild(parked.Mind);
        parked.Mind.Free();
        await WaitForFramesAsync(tree, 5);
        parked.Host.RefreshComponents();
        await WaitForFramesAsync(tree, 10);

        Assert.True(parked.Host.HasComponentProjection);
        parked.AssertEndedQuietly();
    }

    /// <summary>
    /// A hermetic mind (counting stub client, static prompts, visual context off, no transcripts) under a
    /// never-projected host in the tree, verified parked in the projection wait.
    /// </summary>
    private sealed class ParkedSession : IAsyncDisposable
    {
        private readonly RecordingLoggerProvider _logs = new();
        private readonly CountingClientProvider _provider = new();
        private readonly FixturePlayerCharacter _player = new();
        private int _sceneContextLoads;
        private int _sceneContextLoadsAtExit = -1;

        private ParkedSession()
        {
            Host = new ComponentlessCharacterHost
            {
                Name = "ProjectionWaitHost",
                Id = HostId,
            };
            Mind = new AgenticMind
            {
                Name = "Mind",
                ClientProvider = _provider,
                SystemInstruction = new PromptStack
                {
                    Sections = [new TextPromptSection { Text = "Static session guidance.", Name = "Static" }],
                },
                CurrentSceneStatus = new PromptStack
                {
                    Sections = [new TextPromptSection { Text = "Static scene status.", Name = "Status" }],
                },
            };
            Mind.SetDiagnosticsSettingsLoaderForTesting(() => new AIDiagnosticsSettings(
                EnableRequestResponseLogging: false,
                EnableReasoningLogging: false,
                EnableSessionTranscriptLogging: false));
            Mind.SetVisualContextModeLoaderForTesting(() => VisualContextMode.Off);
            ComponentlessCharacterHost host = Host;
            FixturePlayerCharacter player = _player;
            Mind.SetSceneContextLoaderForTesting(() =>
            {
                _ = Interlocked.Increment(ref _sceneContextLoads);
                return new SceneContext([host, player]);
            });
            Host.AddChild(Mind);
        }

        public ComponentlessCharacterHost Host
        {
            get;
        }

        public AgenticMind Mind
        {
            get;
        }

        public static async Task<ParkedSession> StartAsync(SceneTree tree)
        {
            ParkedSession parked = new();
            Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(parked._logs);
            tree.Root.AddChild(parked.Host);
            await WaitForFramesAsync(tree, 5);

            // Precondition: preparation is parked in the projection wait, not ended or failed for another reason.
            Assert.False(parked.Host.HasComponentProjection);
            Assert.Contains(parked._logs.Entries, entry =>
                entry.Message.Contains($"'{HostId}' is waiting for its component projection", StringComparison.Ordinal));
            Assert.Null(parked.Mind.ActiveRunner);
            Assert.Equal(0, parked._provider.ClientsCreated);
            return parked;
        }

        /// <summary>
        /// Marks the node-lifetime end. Session preparation resolves the scene context through the test loader as its
        /// first step after the projection wait, so a load after this point means the wait outlived the lifetime.
        /// </summary>
        public void MarkLifetimeEnd() => _sceneContextLoadsAtExit = Volatile.Read(ref _sceneContextLoads);

        public void AssertEndedQuietly()
        {
            Assert.True(_sceneContextLoadsAtExit >= 0, "The test must mark the node-lifetime end.");
            Assert.Equal(_sceneContextLoadsAtExit, Volatile.Read(ref _sceneContextLoads));
            Assert.DoesNotContain(_logs.Entries, entry =>
                entry.Message.StartsWith(SessionFailureMessage, StringComparison.Ordinal));
            Assert.Equal(0, _provider.ClientsCreated);
            Assert.Equal(0, _provider.RequestsReceived);
        }

        public ValueTask DisposeAsync()
        {
            if (GodotObject.IsInstanceValid(Mind) && Mind.GetParent() is null)
            {
                Mind.Free();
            }

            if (GodotObject.IsInstanceValid(Host))
            {
                Host.GetParent()?.RemoveChild(Host);
                Host.Free();
            }

            _provider.Free();
            _player.Free();
            _logs.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Counts chat-client creation and requests; any request is held until cancelled.</summary>
    private sealed partial class CountingClientProvider : ClientProvider
    {
        private int _clientsCreated;
        private int _requestsReceived;

        public int ClientsCreated => Volatile.Read(ref _clientsCreated);

        public int RequestsReceived => Volatile.Read(ref _requestsReceived);

        private void RecordRequest() => _ = Interlocked.Increment(ref _requestsReceived);

        public override IChatClient CreateChatClient(string subjectFullId)
        {
            _ = Interlocked.Increment(ref _clientsCreated);
            return new CountingClient(this);
        }

        private sealed class CountingClient(CountingClientProvider owner) : IChatClient
        {
            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                owner.RecordRequest();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                owner.RecordRequest();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                yield break;
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }

    /// <summary>Records every log entry from the moment it is added until disposed.</summary>
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

        private void Record(LogLevel level, string message)
        {
            if (_disposed)
            {
                return;
            }

            lock (_lock)
            {
                _entries.Add(new LogEntry(level, message));
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
                provider.Record(logLevel, formatter(state, exception) + (exception is null ? string.Empty : $" {exception}"));
            }
        }

        public sealed record LogEntry(LogLevel Level, string Message);
    }
}
