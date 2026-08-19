using System.Diagnostics;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Core.Time;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Godot-runtime coverage for Mind's notable-observation wait: threshold wakes, quiet expiry, window reset,
/// single-active-wait enforcement, disable-pause semantics, and game-time stamping.
/// </summary>
[Headless]
public sealed partial class MindWaitIntegrationTests
{
    /// <summary>
    /// Crossing the cumulative-importance threshold mid-wait completes the wait early with the accumulated
    /// window in FIFO order (AI-001 TR-6, AI-002 TR-33).
    /// </summary>
    [Fact]
    public async Task Wait_WhenThresholdCrossesMidWait_CompletesEarlyWithFIFOWindow()
    {
        FakeGameClock clock = new()
        {
            NowSeconds = 100d
        };
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        mind.SetGameClockLoaderForTesting(() => clock);
        try
        {
            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            mind.ObserveForTest(new TestObservation(1f, "notable"));

            MindBase.WaitOutcome outcome = await waitTask;

            Assert.False(outcome.AttendedSpeakerFinished);
            Assert.Equal(["notable"], NotableValues(outcome));
            Assert.All(outcome.Notable, observation => Assert.Equal(100d, observation.ObservedAt));
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// Quiet expiry returns no sub-threshold observations, never promotes them, and resets the accumulation
    /// window for the next wait (AI-002 TR-34, AI-001 TR-6).
    /// </summary>
    [Fact]
    public async Task Wait_WhenQuietExpiry_ReturnsNothingNeverPromotesAndResetsWindow()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        try
        {
            var stopwatch = Stopwatch.StartNew();
            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(0.2), CancellationToken.None);
            await Task.Delay(50);
            mind.ObserveForTest(new TestObservation(0.5f, "sub-threshold"));
            MindBase.WaitOutcome outcome = await waitTask;
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds >= 150, "The quiet wait must run its requested duration.");
            Assert.Empty(outcome.Notable);
            Assert.False(outcome.AttendedSpeakerFinished);
            // Sub-threshold observations stay recorded in the timeline, reachable through the history tool.
            Assert.Equal(["sub-threshold"], TimelineValues(mind));

            // The window reset at completion: the next wait delivers only what accumulates after it.
            mind.ObserveForTest(new TestObservation(1f, "later-notable"));
            MindBase.WaitOutcome second = await mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal(["later-notable"], NotableValues(second));
            Assert.Equal(["sub-threshold", "later-notable"], TimelineValues(mind));
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// An already-notable window is delivered immediately by the next wait call, which then resets the window
    /// (AI-001 TR-6).
    /// </summary>
    [Fact]
    public async Task Wait_WhenWindowIsAlreadyNotable_DeliversImmediatelyAndResets()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        try
        {
            mind.ObserveForTest(new TestObservation(1f, "held"));

            MindBase.WaitOutcome outcome = await mind.WaitForNotableForTestAsync(
                TimeSpan.FromSeconds(5),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(["held"], NotableValues(outcome));

            MindBase.WaitOutcome quiet = await mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(0.05), CancellationToken.None);
            Assert.Empty(quiet.Notable);
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// Exactly one observation wait may be active at a time; a second wait fails clearly until the first
    /// completes.
    /// </summary>
    [Fact]
    public async Task Wait_EnforcesSingleActiveWait()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestMind mind = new();
        mind.SetSceneContextLoaderForTesting(() => new TestSceneContext([mind.Owner]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);
        try
        {
            Task<MindBase.WaitOutcome> firstWait = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(0.4), CancellationToken.None);
            await Task.Delay(50);

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(0.4), CancellationToken.None));
            Assert.Contains("exactly one active observation wait", error.Message, StringComparison.Ordinal);

            _ = await firstWait;
            MindBase.WaitOutcome secondWait = await mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(0.05), CancellationToken.None);
            Assert.Empty(secondWait.Notable);
        }
        finally
        {
            mind.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// The wait duration is bounded by the configured maximum observation wait, whose default is 10 seconds
    /// (AI-001 TR-7, AI-002 TR-31).
    /// </summary>
    [Fact]
    public async Task Wait_BoundsDurationToConfiguredMaximum()
    {
        TestMind defaultMind = new();
        TestMind boundedMind = new()
        {
            MaxObservationWaitSeconds = 0.05f,
        };
        try
        {
            Assert.Equal(10f, defaultMind.MaxObservationWaitSeconds);

            // A non-positive requested duration falls back to the configured maximum (here the 0.05s floor).
            MindBase.WaitOutcome outcome = await boundedMind.WaitForNotableForTestAsync(
                TimeSpan.Zero,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(outcome.Notable);
        }
        finally
        {
            defaultMind.Free();
            boundedMind.Free();
        }
    }

    /// <summary>
    /// The notable signal fires exactly once per settled threshold crossing — only while enabled and only when
    /// no wait is active (AI-001 TR-6, TR-35).
    /// </summary>
    [Fact]
    public async Task NotableSignal_FiresOncePerCrossingOnlyWhenEnabledAndIdle()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        int signalled = 0;
        mind.NotableSignalForTest(() => signalled++);
        try
        {
            mind.ObserveForTest(new TestObservation(0.5f, "sub-threshold"));
            Assert.Equal(0, signalled);

            mind.ObserveForTest(new TestObservation(1f, "crossing"));
            Assert.Equal(1, signalled);

            // The already-notable window does not re-signal while it stays pending.
            mind.ObserveForTest(new TestObservation(1f, "still-pending"));
            Assert.Equal(1, signalled);

            // Taking the window re-arms the signal for the next crossing.
            Assert.NotNull(mind.TryTakePendingNotableWindowForTest());
            mind.ObserveForTest(new TestObservation(1f, "next-crossing"));
            Assert.Equal(2, signalled);
            Assert.NotNull(mind.TryTakePendingNotableWindowForTest());

            // While a wait is active the crossing wakes the wait instead of signalling.
            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            mind.ObserveForTest(new TestObservation(1f, "while-waiting"));
            MindBase.WaitOutcome outcome = await waitTask;
            Assert.Equal(["while-waiting"], NotableValues(outcome));
            Assert.Equal(2, signalled);
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// Try-take delivers the pending notable window and resets it, and reserves the window while a wait is
    /// active.
    /// </summary>
    [Fact]
    public async Task TryTakePendingNotableWindow_DeliversAndResetsOnlyWhenNotableAndIdle()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        try
        {
            Assert.Null(mind.TryTakePendingNotableWindowForTest());

            mind.ObserveForTest(new TestObservation(1f, "held"));
            Assert.Equal(["held"], [.. mind.TryTakePendingNotableWindowForTest()!
                .Cast<TestObservation>()
                .Select(static observation => observation.Value)]);
            Assert.Null(mind.TryTakePendingNotableWindowForTest());

            mind.ObserveForTest(new TestObservation(1f, "reserved"));
            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            Assert.Null(mind.TryTakePendingNotableWindowForTest());

            MindBase.WaitOutcome outcome = await waitTask;
            Assert.Equal(["reserved"], NotableValues(outcome));
            Assert.Null(mind.TryTakePendingNotableWindowForTest());
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// Disabling Mind pauses wake and signal while preserving accumulation; re-enable wakes a held-notable wait
    /// but does not fire a newly-notable interrupt (AI-001 TR-5).
    /// </summary>
    [Fact]
    public async Task DisabledMind_PausesWakeAndSignalWhilePreservingAccumulation()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        int signalled = 0;
        mind.NotableSignalForTest(() => signalled++);
        try
        {
            // While a wait is active: disabling pauses the wake; re-enable wakes the held-notable wait.
            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            mind.Enabled = false;
            mind.ObserveForTest(new TestObservation(1f, "while-disabled"));
            await Task.Delay(150);
            Assert.False(waitTask.IsCompleted, "A disabled Mind must not wake an active wait.");
            Assert.Equal(0, signalled);

            mind.Enabled = true;
            MindBase.WaitOutcome outcome = await waitTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(["while-disabled"], NotableValues(outcome));
            Assert.Equal(0, signalled);

            // While idle: re-enable preserves the held window for the next wait without firing the signal.
            mind.Enabled = false;
            mind.ObserveForTest(new TestObservation(1f, "held-while-disabled"));
            Assert.Equal(0, signalled);

            mind.Enabled = true;
            Assert.Equal(0, signalled);

            MindBase.WaitOutcome delivered = await mind.WaitForNotableForTestAsync(
                TimeSpan.FromSeconds(5),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(["held-while-disabled"], NotableValues(delivered));
            Assert.Equal(0, signalled);
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// A wait that quietly expires while Mind is disabled still returns the already-notable window: disable pauses
    /// wake and interrupt signalling, not the wait's own completion contract (AI-001 TR-5, AI-002 UR-3).
    /// </summary>
    [Fact]
    public async Task DisabledMind_WithQuietlyExpiringWait_StillReturnsAlreadyNotableWindow()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        int signalled = 0;
        mind.NotableSignalForTest(() => signalled++);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(0.2), CancellationToken.None);
            await Task.Delay(50);
            mind.Enabled = false;
            mind.ObserveForTest(new TestObservation(1f, "notable-while-disabled"));

            MindBase.WaitOutcome outcome = await waitTask.WaitAsync(TimeSpan.FromSeconds(2));
            stopwatch.Stop();

            // The wait ran its requested duration and completed on its own quiet expiry despite staying disabled.
            Assert.True(
                stopwatch.ElapsedMilliseconds >= 150,
                "A disabled Mind must still expire an active wait on schedule.");
            Assert.True(
                stopwatch.ElapsedMilliseconds < 1500,
                "The wait must expire on its own while disabled, not stay held for re-enable.");
            // Its completion still delivered the already-notable window accumulated while disabled.
            Assert.Equal(["notable-while-disabled"], NotableValues(outcome));
            Assert.False(outcome.AttendedSpeakerFinished);
            Assert.Equal(0, signalled);
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// Every committed observation is stamped once with the game clock's seconds at ingestion (AI-001 TR-33).
    /// </summary>
    [Fact]
    public void ObservationIntake_StampsObservedAtFromTheGameClock()
    {
        FakeGameClock clock = new()
        {
            NowSeconds = 123.5d
        };
        TestMind mind = new();
        mind.SetGameClockLoaderForTesting(() => clock);
        try
        {
            mind.ObserveForTest(new TestObservation(1f, "first"));
            clock.NowSeconds = 124.25d;
            mind.ObserveForTest(new TestObservation(1f, "second"));

            IReadOnlyList<AgentObservation> timeline = mind.GetTimelineForTest();
            Assert.Collection(
                timeline,
                observation => Assert.Equal(123.5d, observation.ObservedAt),
                observation => Assert.Equal(124.25d, observation.ObservedAt));
        }
        finally
        {
            mind.Free();
        }
    }

    private static IReadOnlyList<string> NotableValues(MindBase.WaitOutcome outcome)
        => [.. outcome.Notable.Cast<TestObservation>().Select(static observation => observation.Value)];

    private static IReadOnlyList<string> TimelineValues(TestMind mind)
        => [.. mind.GetTimelineForTest().Cast<TestObservation>().Select(static observation => observation.Value)];

    private sealed partial class TestMind : MindBase
    {
        private readonly TestCharacter _character = new();

        public new ICharacter Owner => _character;

        public void ObserveForTest(AgentObservation observation) => Observe(observation);

        public Task<WaitOutcome> WaitForNotableForTestAsync(TimeSpan maxWait, CancellationToken cancellationToken)
            => WaitForNotableObservationsAsync(maxWait, cancellationToken);

        public IReadOnlyList<AgentObservation>? TryTakePendingNotableWindowForTest()
            => TryTakePendingNotableWindow();

        public void NotableSignalForTest(Action handler) => NotableObservationsSignalled += handler;

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        protected override ICharacter ResolveOwningCharacter() => _character;
    }

    private sealed class FakeGameClock : IGameClock
    {
        public double NowSeconds
        {
            get;
            set;
        }
    }

    private sealed record TestSceneContext(IReadOnlyCollection<ICharacter> Characters) : ISceneContext
    {
        public ICharacter Player => throw new InvalidOperationException(
            "Scene context contains no player character. Scene authoring guarantees the player is present.");

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId)
            => Characters.FirstOrDefault(character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException();
    }

    private sealed record TestObservation(float Importance, string Value) : AgentObservation
    {
        public override string TypeKey => "test.wait";

        public override float CalculateImportance(ObservationContext context) => Importance;
    }

    private sealed class TestCharacter : ICharacter
    {
        public string Id { get; set; } = "wait_owner";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
    }
}
