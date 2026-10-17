using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using AlleyCat.Testing;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Godot-runtime coverage for the production-AI boundary of integration-test sessions (TEST-001 UR-7): an
/// ordinary integration session must never construct a production chat client or issue a provider request;
/// only explicitly opted-in live tests may contact real providers, exclusively through the separate
/// <c>AITest</c> configuration boundary.
/// </summary>
/// <remarks>
/// <para>
/// A scene-started AgenticMind defaults its <c>ClientProvider</c> to <see cref="OpenAIClientProvider" />,
/// whose instance <see cref="OpenAIClientProvider.CreateChatClient()" /> path loads merged production
/// settings — including the private user override — and builds the real transport. The required boundary:
/// in an integration-test session, that instance path must reject with one fixed, secret-free diagnostic
/// before production settings loading or transport construction. The static internal construction path
/// (<c>OpenAIClientProvider.CreateChatClient(kind, settings, options)</c>) used by the opt-in
/// <c>AITest</c> live factory is a separate path and stays available.
/// </para>
/// <para>
/// The boundary fact pins that contract through a fail-safe sentinel: an isolated temporary configuration
/// with no <c>AI</c> section. If the boundary is missing or bypassed, the call proceeds into production
/// settings loading and transport construction and stops only at the sentinel's deterministic
/// missing-<c>AI/Host</c> failure — so the fact can never construct a network client, read real
/// configuration, or issue a request, in red or green state alike. While the boundary is missing it fails
/// red by surfacing exactly that settings-stage diagnostic instead of the fixed guard rejection.
/// </para>
/// </remarks>
[Headless]
public sealed class ProductionAIProviderBoundaryIntegrationTests
{
    /// <summary>The fixed, secret-free rejection the production provider's instance boundary must emit.</summary>
    private const string GuardDiagnostic =
        "Production AI chat clients are disabled in integration-test sessions.";

    /// <summary>
    /// The production provider's instance construction path rejects integration-test sessions before
    /// production settings loading or transport construction, with one fixed, secret-free diagnostic.
    /// </summary>
    [Fact]
    public void IntegrationSession_RejectsProductionProviderInstance_BeforeSettingsOrTransportConstruction()
    {
        // The boundary's activation context must be live: this fact runs inside a host-launched
        // --integration-test-session process.
        Assert.True(
            RuntimeContext.IsIntegrationTest(),
            "The production-AI boundary regression must run inside an integration-test session.");

        // Fail-safe sentinel: a configuration with no AI section, isolated in a temporary directory. An
        // unguarded provider call fails deterministically at the missing AI/Host endpoint diagnostic instead
        // of ever building a transport, so this fact stays network-free in every state.
        string sentinelRoot = Path.Combine(
            Path.GetTempPath(),
            "AlleyCat.ProductionAIProviderBoundaryIntegrationTests",
            Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(sentinelRoot);
        string sentinelConfigPath = Path.Combine(sentinelRoot, "no-ai-section.yaml");
        File.WriteAllText(sentinelConfigPath, "Sentinel: no-ai-section\n");

        OpenAIClientProvider provider = new()
        {
            ConfigPath = sentinelConfigPath
        };
        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                provider.CreateChatClient);

            // The fixed, secret-free rejection names the disabled production boundary.
            Assert.Contains(GuardDiagnostic, exception.Message, StringComparison.Ordinal);

            // The rejection fires before production settings loading or transport construction: the
            // sentinel's own settings-stage diagnostics — which embed its path — must never surface,
            // because nothing past the boundary may run.
            Assert.DoesNotContain("AI/Host", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinelConfigPath, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            provider.Free();
            try
            {
                Directory.Delete(sentinelRoot, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // Nothing was ever written under this root.
            }
        }
    }

    /// <summary>
    /// The live-LLM opt-in is a host-side selection permission, never an integration-context exit: even with
    /// a <c>--live-llm</c> argument present, the context the boundary keys on stays an integration-test
    /// session, so an opted-in run can never re-enable production scene AI. Parse-level only — no live
    /// configuration is consulted and no process argument is mutated.
    /// </summary>
    [Fact]
    public void LiveLlmArgument_NeverExitsIntegrationTestContext()
    {
        Assert.True(RuntimeContext.IsIntegrationTest(null, ["--integration-test-session", "--live-llm"]));
        Assert.True(RuntimeContext.IsIntegrationTest(
            RuntimeContext.IntegrationTestContextValue,
            ["--live-llm"]));
    }

    /// <summary>
    /// A mind left on the production default composition — no <c>ClientProvider</c> injected, so
    /// <c>AgenticMind</c>'s default <c>OpenAIClientProvider</c> with the default merged-configuration path —
    /// fails contained through the ordinary session-start path across the complete fixture lifecycle:
    /// construction, settled background work, and teardown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The positive oracle is the strict contained failure itself: exactly one Error log entry whose
    /// <see cref="InvalidOperationException" /> carries the fixed guard rejection. On the default
    /// configuration path a missing or misordered guard would not fail locally here — the recorded symptom
    /// shows settings loading succeeds on this machine and the session dispatches a real request — so the
    /// guard message, the absence of any settings-stage diagnostic, and the published-but-unconsumed render
    /// context together prove the rejection fired at the client-construction boundary, before production
    /// settings loading or transport construction and before any request could materialise. Containment
    /// follows the AI-002 TR-1 contract: the scene never crashes, perception keeps ingesting, the failure
    /// logs exactly once, and the session stays ended through node exit.
    /// </para>
    /// <para>
    /// The observation windows are the Mind lifecycle fixtures' standard bounded frame polls — a 300-frame
    /// condition wait for the contained failure, 6 frames of post-failure settle, and 2 frames of teardown
    /// settle — matching the contained-failure and suppression precedents; none is shortened to pass.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DefaultCompositionMind_ThroughFullLifecycle_FailsContainedBeforeAnyProductionAccess()
    {
        Assert.True(
            RuntimeContext.IsIntegrationTest(),
            "The production-AI boundary regression must run inside an integration-test session.");

        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        // No ClientProvider is assigned: the mind runs the production default composition — exactly the path
        // that previously reached a real production request without opt-in.
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        bool tornDown = false;
        try
        {
            // Contained failure through the ordinary start path: one Error with the strict guard failure type
            // and message (bounded 300-frame condition wait, as in the lifecycle precedents).
            await WaitUntilAsync(sceneTree, () => loggerProvider.Entries.Any(entry =>
                entry.Level == LogLevel.Error
                && entry.Exception is InvalidOperationException
                && entry.Exception.Message.Contains(GuardDiagnostic, StringComparison.Ordinal)));

            RecordingLoggerProvider.LogEntry failure = Assert.Single(loggerProvider.Entries, entry =>
                entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException);
            Assert.Contains(GuardDiagnostic, failure.Exception!.Message, StringComparison.Ordinal);

            // Construction order on the default path: no settings-stage diagnostic can precede or replace the
            // guard rejection, because nothing past the boundary may run.
            Assert.DoesNotContain("AI/Host", failure.Exception.Message, StringComparison.Ordinal);

            // The failure is the production boundary, not the process-wide --no-ai suppression notice.
            Assert.DoesNotContain(loggerProvider.Entries, entry =>
                entry.Message.Contains(AgentSessionSuppression.NoAISwitch, StringComparison.Ordinal));

            // Ordering witness: session start progressed through its local prompt preparation — the render
            // context is published — up to the client-construction boundary, where the guard rejected. No
            // request materialises after it: the single contained failure is the guard rejection itself, and
            // the session stays ended.
            Assert.NotEmpty(mind.GetLatestRenderContext());

            // Settled background work keeps the containment: perception still ingests, the failure logs exactly
            // once, and no session restarts.
            mind.ObserveForTest(new TestObservation(1f, "after-failure"));
            await TestUtils.WaitForFramesAsync(sceneTree, 6);
            Assert.Equal(["after-failure"], TimelineValues(mind));
            _ = Assert.Single(loggerProvider.Entries, entry =>
                entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException);

            // Teardown: node exit stays quiet — no further diagnostics, no restart, no crash.
            mind.QueueFree();
            tornDown = true;
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            _ = Assert.Single(loggerProvider.Entries, entry =>
                entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException);
        }
        finally
        {
            if (!tornDown)
            {
                mind.QueueFree();
                await TestUtils.WaitForFramesAsync(sceneTree, 2);
            }

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
        public override string TypeKey => ObservedSpeech.TypeKeyValue;

        public override float CalculateImportance(ObservationContext context) => Importance;
    }

    private sealed partial class TestAgenticMind(ICharacter owner) : AgenticMind
    {
        public void ObserveForTest(AgentObservation observation) => Observe(observation);

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        protected override ICharacter ResolveOwningCharacter() => owner;
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
