using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.Observation;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>Godot-runtime coverage for payload-free wait scheduling and confirmation-owned pressure clearing.</summary>
[Headless]
public sealed partial class MindWaitIntegrationTests
{
    /// <summary>Threshold pressure wakes a wait without making event payload available through the wait outcome.</summary>
    [Fact]
    public async Task Wait_WhenThresholdCrosses_WakesWithoutDeliveringEventPayload()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        try
        {
            Task<MindBase.WaitOutcome> wait = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            mind.ObserveForTest(new TestObservation(1f, "notable"));

            MindBase.WaitOutcome outcome = await wait;

            Assert.Equal(MindBase.ObservationWaitWake.ThresholdCrossed, outcome.Wake);
            Assert.Equal(["notable"], mind.GetTimelineValues());
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>Fresh scheduling wins over lower-priority threshold pressure and remains payload-free.</summary>
    [Fact]
    public async Task Wait_WhenFreshEventArrives_WakesWithFreshPriorityAndNoPayload()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 2f,
        };
        try
        {
            Task<MindBase.WaitOutcome> wait = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            mind.ObserveForTest(new TestObservation(1.5f, "ordinary"));
            mind.ObserveForTest(new TestObservation(0.1f, "fresh", Fresh: true));

            MindBase.WaitOutcome outcome = await wait;

            Assert.Equal(MindBase.ObservationWaitWake.FreshObservation, outcome.Wake);
            Assert.Equal(["ordinary", "fresh"], mind.GetTimelineValues());
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>Timeout does not promote routine pressure or expose its event text.</summary>
    [Fact]
    public async Task Wait_WhenOnlyRoutinePressureExists_TimesOutWithoutPayload()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        try
        {
            Task<MindBase.WaitOutcome> wait = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(0.15), CancellationToken.None);
            await Task.Delay(30);
            mind.ObserveForTest(new TestObservation(0.5f, "routine"));

            MindBase.WaitOutcome outcome = await wait;

            Assert.Equal(MindBase.ObservationWaitWake.QuietExpiry, outcome.Wake);
            Assert.Equal(["routine"], mind.GetTimelineValues());
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>Wait completion leaves pressure pending until its containing request context is accepted.</summary>
    [Fact]
    public async Task Wait_CompletionDoesNotAdvancePressureCursor_OnlyContextConfirmationClearsIt()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        try
        {
            mind.ObserveForTest(new TestObservation(1f, "pending"));
            MindBase.WaitOutcome first = await mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            MindBase.WaitOutcome repeated = await mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

            Assert.Equal(MindBase.ObservationWaitWake.ThresholdCrossed, first.Wake);
            Assert.Equal(MindBase.ObservationWaitWake.ThresholdCrossed, repeated.Wake);

            mind.ConfirmContextForTest(1);
            MindBase.WaitOutcome afterConfirmation = await mind.WaitForNotableForTestAsync(
                TimeSpan.FromSeconds(0.05),
                CancellationToken.None);
            Assert.Equal(MindBase.ObservationWaitWake.QuietExpiry, afterConfirmation.Wake);
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>A stale confirmation cannot consume newer scheduling pressure.</summary>
    [Fact]
    public async Task Wait_ContextConfirmationPreservesEventsAcceptedAfterTheRequestWatermark()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        try
        {
            mind.ObserveForTest(new TestObservation(1f, "first"));
            mind.ObserveForTest(new TestObservation(1f, "second"));

            mind.ConfirmContextForTest(1);
            MindBase.WaitOutcome outcome = await mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

            Assert.Equal(MindBase.ObservationWaitWake.ThresholdCrossed, outcome.Wake);
            Assert.Equal(["first", "second"], mind.GetTimelineValues());
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>Only one wait may be active, and cancellation before a wake remains terminal.</summary>
    [Fact]
    public async Task Wait_EnforcesSingleActiveWaitAndCancellationBeforeWake()
    {
        TestMind mind = new();
        using CancellationTokenSource cancellation = new();
        try
        {
            Task<MindBase.WaitOutcome> first = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), cancellation.Token);
            await Task.Delay(50);

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.Contains("exactly one active observation wait", error.Message, StringComparison.Ordinal);

            cancellation.Cancel();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }
        finally
        {
            mind.Free();
        }
    }

    private sealed partial class TestMind : MindBase
    {
        private readonly TestCharacter _character = new();

        public void ObserveForTest(AgentObservation observation) => Observe(observation);

        public Task<WaitOutcome> WaitForNotableForTestAsync(TimeSpan maxWait, CancellationToken cancellationToken)
            => WaitForNotableObservationsAsync(maxWait, cancellationToken);

        public void ConfirmContextForTest(long watermark) => ConfirmProviderRequestContext(watermark);

        public IReadOnlyList<string> GetTimelineValues()
            => [.. GetObservationTimelineSnapshot().Cast<TestObservation>().Select(static observation => observation.Value)];

        protected override ICharacter ResolveOwningCharacter() => _character;
    }

    private sealed record TestObservation(float Importance, string Value, bool Fresh = false) : AgentObservation
    {
        public override string TypeKey => "test.wait";

        public override float CalculateImportance(ObservationContext context) => Importance;

        public override bool RequiresFreshTurn(ObservationContext context) => Fresh;
    }

    private sealed class TestCharacter : ICharacter
    {
        public string Id { get; set; } = "wait_owner";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }
}
