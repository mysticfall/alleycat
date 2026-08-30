using System.Diagnostics;
using AlleyCat.Character;
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
/// Godot-runtime coverage for Mind's observation-wait scheduling: ordinary threshold and fresh wakes, quiet expiry,
/// delivery claims and abandonment, window reset, single-active-wait enforcement, disable-pause semantics, node-exit
/// precedence, and game-time stamping.
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

            Assert.Equal(MindBase.ObservationWaitWake.ThresholdCrossed, outcome.Wake);
            Assert.Equal(["notable"], DeliveredValues(outcome));
            Assert.All(outcome.Delivered, observation => Assert.Equal(100d, observation.ObservedAt));
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
            Assert.Empty(outcome.Delivered);
            Assert.Equal(MindBase.ObservationWaitWake.QuietExpiry, outcome.Wake);
            // Sub-threshold observations stay recorded in the timeline, reachable through the history tool.
            Assert.Equal(["sub-threshold"], TimelineValues(mind));

            // The window reset at completion: the next wait delivers only what accumulates after it.
            mind.ObserveForTest(new TestObservation(1f, "later-notable"));
            MindBase.WaitOutcome second = await mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal(["later-notable"], DeliveredValues(second));
            Assert.Equal(["sub-threshold", "later-notable"], TimelineValues(mind));
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// An already-deliverable window is delivered immediately by the next wait call, which then resets the window
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

            Assert.Equal(["held"], DeliveredValues(outcome));
            Assert.Equal(MindBase.ObservationWaitWake.ThresholdCrossed, outcome.Wake);

            MindBase.WaitOutcome quiet = await mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(0.05), CancellationToken.None);
            Assert.Empty(quiet.Delivered);
            Assert.Equal(MindBase.ObservationWaitWake.QuietExpiry, quiet.Wake);
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
            Assert.Empty(secondWait.Delivered);
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
            Assert.Empty(outcome.Delivered);
        }
        finally
        {
            defaultMind.Free();
            boundedMind.Free();
        }
    }

    /// <summary>
    /// A below-threshold fresh observation wakes an active wait early — regardless of cumulative importance — and
    /// the wake delivers the complete accumulation including preceding sub-threshold observations in FIFO order
    /// (AI-001 TR-43, AI-002 TR-32/33).
    /// </summary>
    [Fact]
    public async Task Wait_WhenFreshObservationArrivesBelowThreshold_WakesEarlyWithFIFOWindow()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 2f,
        };
        try
        {
            var stopwatch = Stopwatch.StartNew();
            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            mind.ObserveForTest(new TestObservation(0.3f, "sub-threshold-first"));
            mind.ObserveForTest(new TestObservation(0.2f, "sub-threshold-second"));
            await Task.Delay(100);
            Assert.False(waitTask.IsCompleted, "An ordinary below-threshold accumulation must not wake the wait.");

            mind.ObserveForTest(new TestObservation(0.5f, "fresh-arrival", Fresh: true));
            MindBase.WaitOutcome outcome = await waitTask;
            stopwatch.Stop();

            Assert.True(
                stopwatch.ElapsedMilliseconds < 2500,
                "A fresh observation must complete the wait early instead of running its requested duration.");
            Assert.Equal(MindBase.ObservationWaitWake.FreshObservation, outcome.Wake);
            Assert.Equal(["sub-threshold-first", "sub-threshold-second", "fresh-arrival"], DeliveredValues(outcome));
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// A fresh wake returns the fresh observation through the wait result — the sole delivery channel for
    /// wait-owned fresh observations: nothing stays claimable afterwards, so no duplicate injected delivery can
    /// follow (AI-001 TR-43, AI-002 TR-41).
    /// </summary>
    [Fact]
    public async Task Wait_WhenFreshObservationWakes_ReturnsItOnceWithNoRemainingClaim()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 2f,
        };
        try
        {
            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            mind.ObserveForTest(new TestObservation(0.5f, "fresh-speech", Fresh: true));
            MindBase.WaitOutcome outcome = await waitTask;

            Assert.Equal(MindBase.ObservationWaitWake.FreshObservation, outcome.Wake);
            Assert.Equal(["fresh-speech"], DeliveredValues(outcome));
            // The wait consumed the window: no second delivery claim exists for the same batch.
            Assert.Null(mind.TryClaimDeliveryForTest());
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// External non-self speech wakes an active wait below the importance threshold through Mind's real speech
    /// observation contract: recognised-external speakers require a fresh turn regardless of attention or
    /// importance (AI-001 TR-43, UR-9).
    /// </summary>
    [Fact]
    public async Task Wait_WhenExternalSpeechArrivesBelowThreshold_FreshWakesThroughSpeechContract()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 2f,
        };
        try
        {
            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            mind.ObserveForTest(new ObservedSpeech("char:someone-else", "voice-x", "Hello there."));

            MindBase.WaitOutcome outcome = await waitTask;

            Assert.Equal(MindBase.ObservationWaitWake.FreshObservation, outcome.Wake);
            ObservedSpeech speech = Assert.IsType<ObservedSpeech>(Assert.Single(outcome.Delivered));
            Assert.Equal("Hello there.", speech.Content);
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// A wait that was woken delivers its window even when the external token is cancelled immediately after the
    /// wake — the exact ordering of a wait-owned fresh signal whose invalidation cancels the runner phase: only a
    /// wait that never woke treats cancellation as abandonment (AI-002 TR-41, AI-001 TR-19).
    /// </summary>
    [Fact]
    public async Task Wait_WokenThenExternallyCancelled_DeliversItsWindowAnyway()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        CancellationTokenSource external = new();
        // The signal fires synchronously after the wake and before the wait continuation can run: cancelling here
        // reproduces the production ordering where the invalidation cancels the phase after the wake.
        mind.DeliverySignalForTest(_ => external.Cancel());
        try
        {
            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), external.Token);
            await Task.Delay(50);
            mind.ObserveForTest(new TestObservation(0.5f, "fresh-raced-cancel", Fresh: true));

            MindBase.WaitOutcome outcome = await waitTask;

            Assert.Equal(MindBase.ObservationWaitWake.FreshObservation, outcome.Wake);
            Assert.Equal(["fresh-raced-cancel"], DeliveredValues(outcome));
            Assert.Null(mind.TryClaimDeliveryForTest());
        }
        finally
        {
            mind.Free();
            external.Dispose();
        }
    }

    /// <summary>
    /// External cancellation of a wait that was never woken still abandons it: the wait throws cancellation and
    /// never surfaces a normal result, so ordinary runner cancellation from other sources keeps its meaning
    /// (AI-001 TR-19, AI-002 TR-40).
    /// </summary>
    [Fact]
    public async Task Wait_WhenExternalCancelsWithoutAWake_ThrowsAsAbandoned()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        CancellationTokenSource external = new();
        try
        {
            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), external.Token);
            await Task.Delay(50);
            external.Cancel();

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);
            Assert.Null(mind.TryClaimDeliveryForTest());
        }
        finally
        {
            mind.Free();
            external.Dispose();
        }
    }

    /// <summary>
    /// Exact self speech never fresh-wakes: below the threshold it leaves the wait running to quiet expiry with
    /// nothing delivered, while the observation stays recorded in the timeline (AI-001 TR-43).
    /// </summary>
    [Fact]
    public async Task Wait_WhenExactSelfSpeechArrivesBelowThreshold_DoesNotWake()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        try
        {
            var stopwatch = Stopwatch.StartNew();
            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(0.3), CancellationToken.None);
            await Task.Delay(50);
            mind.ObserveForTest(new ObservedSpeech(mind.Owner.FullId, null, "I said this myself."));
            await Task.Delay(100);
            Assert.False(waitTask.IsCompleted, "Exact self speech must never fresh-wake a wait.");

            MindBase.WaitOutcome outcome = await waitTask;
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds >= 250, "The wait must run its requested duration without a self-speech wake.");
            Assert.Equal(MindBase.ObservationWaitWake.QuietExpiry, outcome.Wake);
            Assert.Empty(outcome.Delivered);
            Assert.Equal(["I said this myself."], SpeechContents(mind));
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// An ordinary below-threshold observation never completes an active wait early; the wait runs its requested
    /// duration and the observation remains reachable only through the timeline (AI-002 TR-34).
    /// </summary>
    [Fact]
    public async Task Wait_WhenOrdinaryBelowThresholdObservationArrives_DoesNotCompleteEarly()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        try
        {
            var stopwatch = Stopwatch.StartNew();
            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(0.4), CancellationToken.None);
            await Task.Delay(60);
            mind.ObserveForTest(new TestObservation(0.5f, "ordinary-sub-threshold"));
            await Task.Delay(160);
            Assert.False(waitTask.IsCompleted, "An ordinary below-threshold observation must not complete the wait early.");

            MindBase.WaitOutcome outcome = await waitTask;
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds >= 350, "The wait must run its requested duration.");
            Assert.Equal(MindBase.ObservationWaitWake.QuietExpiry, outcome.Wake);
            Assert.Empty(outcome.Delivered);
            Assert.Equal(["ordinary-sub-threshold"], TimelineValues(mind));
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// The delivery signal fires once per urgency upgrade — never for sub-threshold accumulation or an unchanged
    /// pending window — carrying delivery urgency and wait ownership (AI-001 TR-44, AI-002 TR-41).
    /// </summary>
    [Fact]
    public async Task DeliverySignal_FiresOnUrgencyUpgradeAndCarriesWaitOwnership()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        List<MindBase.ObservationDeliverySignal> signals = [];
        mind.DeliverySignalForTest(signals.Add);
        try
        {
            mind.ObserveForTest(new TestObservation(0.5f, "sub-threshold"));
            Assert.Empty(signals);

            mind.ObserveForTest(new TestObservation(1f, "crossing"));
            MindBase.ObservationDeliverySignal crossing = Assert.Single(signals);
            Assert.Equal(MindBase.ObservationDeliveryUrgency.Ordinary, crossing.Urgency);
            Assert.False(crossing.WaitOwned);

            // The already-deliverable window does not re-signal while it stays pending.
            mind.ObserveForTest(new TestObservation(1f, "still-pending"));
            _ = Assert.Single(signals);

            // Claiming the window re-arms the signal for the next crossing.
            Assert.NotNull(mind.TryClaimDeliveryForTest());
            mind.ObserveForTest(new TestObservation(1f, "next-crossing"));
            Assert.Equal(2, signals.Count);
            Assert.Equal(MindBase.ObservationDeliveryUrgency.Ordinary, signals[1].Urgency);
            Assert.NotNull(mind.TryClaimDeliveryForTest());

            // Fresh urgency upgrades a sub-threshold window with one fresh signal — never another ordinary one.
            mind.ObserveForTest(new TestObservation(0.2f, "fresh-upgrade", Fresh: true));
            Assert.Equal(3, signals.Count);
            Assert.Equal(MindBase.ObservationDeliveryUrgency.Fresh, signals[2].Urgency);
            Assert.False(signals[2].WaitOwned);
            Assert.NotNull(mind.TryClaimDeliveryForTest());

            // While a wait is active the crossing wakes the wait and still signals, carrying wait ownership.
            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            mind.ObserveForTest(new TestObservation(1f, "while-waiting"));
            MindBase.WaitOutcome outcome = await waitTask;
            Assert.Equal(["while-waiting"], DeliveredValues(outcome));
            Assert.Equal(MindBase.ObservationWaitWake.ThresholdCrossed, outcome.Wake);
            MindBase.ObservationDeliverySignal waitOwned = Assert.Single(signals, signal => signal.WaitOwned);
            Assert.Equal(MindBase.ObservationDeliveryUrgency.Ordinary, waitOwned.Urgency);
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// A fresh signal with no active wait carries fresh urgency without wait ownership, and the window bypasses the
    /// threshold: it is claimable for immediate fresh delivery exactly once (AI-001 TR-43/44).
    /// </summary>
    [Fact]
    public void FreshObservation_WithNoActiveWait_BypassesThresholdAndIsClaimableForImmediateDelivery()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        List<MindBase.ObservationDeliverySignal> signals = [];
        mind.DeliverySignalForTest(signals.Add);
        try
        {
            mind.ObserveForTest(new TestObservation(0.2f, "fresh-idle", Fresh: true));

            MindBase.ObservationDeliverySignal signal = Assert.Single(signals);
            Assert.Equal(MindBase.ObservationDeliveryUrgency.Fresh, signal.Urgency);
            Assert.False(signal.WaitOwned);

            MindBase.ObservationDeliveryClaim claim = mind.TryClaimDeliveryForTest()!;
            Assert.Equal(["fresh-idle"], ClaimValues(claim));
            Assert.Equal(MindBase.ObservationDeliveryUrgency.Fresh, claim.Urgency);
            mind.CompleteDeliveryForTest(claim);

            // Exactly one delivery claim exists for the batch: nothing stays pending afterwards.
            Assert.Null(mind.TryClaimDeliveryForTest());
            Assert.Equal(["fresh-idle"], TimelineValues(mind));
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// A fresh observation arriving while an ordinary window is already pending upgrades that window — including
    /// its threshold-qualified observations — into exactly one claim with fresh urgency, never stranding the fresh
    /// arrival behind the pending window (AI-001 TR-43/44).
    /// </summary>
    [Fact]
    public void FreshObservation_ArrivingOnOrdinaryPendingWindow_UpgradesItIntoOneClaim()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        List<MindBase.ObservationDeliverySignal> signals = [];
        mind.DeliverySignalForTest(signals.Add);
        try
        {
            mind.ObserveForTest(new TestObservation(1f, "ordinary-crossing"));
            MindBase.ObservationDeliverySignal ordinary = Assert.Single(signals);
            Assert.Equal(MindBase.ObservationDeliveryUrgency.Ordinary, ordinary.Urgency);

            mind.ObserveForTest(new TestObservation(0.1f, "fresh-upgrade", Fresh: true));
            Assert.Equal(2, signals.Count);
            Assert.Equal(MindBase.ObservationDeliveryUrgency.Fresh, signals[1].Urgency);

            // One claim delivers the merged window in FIFO order; a second claim finds nothing.
            MindBase.ObservationDeliveryClaim claim = mind.TryClaimDeliveryForTest()!;
            Assert.Equal(["ordinary-crossing", "fresh-upgrade"], ClaimValues(claim));
            Assert.Equal(MindBase.ObservationDeliveryUrgency.Fresh, claim.Urgency);
            mind.CompleteDeliveryForTest(claim);
            Assert.Null(mind.TryClaimDeliveryForTest());
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// A fresh observation racing quiet expiry is never lost or duplicated: it is either included in the completed
    /// wait's fresh delivery, or — when it arrives after the wait completed — retained for an immediate fresh
    /// claim (AI-001 TR-43, acceptance TR-33).
    /// </summary>
    [Fact]
    public async Task FreshObservation_RacingQuietExpiry_IsIncludedOrRetainedNeverLostOrDuplicated()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        try
        {
            // Included: the fresh observation arrives mid-wait and the completing wait delivers it with fresh
            // urgency — whether it won the wake race outright or committed just before completion took the window.
            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(0.3), CancellationToken.None);
            await Task.Delay(80);
            mind.ObserveForTest(new TestObservation(0.2f, "fresh-during", Fresh: true));
            MindBase.WaitOutcome included = await waitTask;

            Assert.Equal(MindBase.ObservationWaitWake.FreshObservation, included.Wake);
            Assert.Equal(["fresh-during"], DeliveredValues(included));
            Assert.Null(mind.TryClaimDeliveryForTest());

            // Retained: a fresh observation arriving after the wait completed stays claimable for fresh delivery.
            MindBase.WaitOutcome quiet = await mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(0.05), CancellationToken.None);
            Assert.Empty(quiet.Delivered);
            mind.ObserveForTest(new TestObservation(0.2f, "fresh-after", Fresh: true));

            MindBase.ObservationDeliveryClaim claim = mind.TryClaimDeliveryForTest()!;
            Assert.Equal(["fresh-after"], ClaimValues(claim));
            Assert.Equal(MindBase.ObservationDeliveryUrgency.Fresh, claim.Urgency);
            mind.CompleteDeliveryForTest(claim);
            Assert.Null(mind.TryClaimDeliveryForTest());
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// Claiming delivers the pending window and resets it, refuses sub-threshold or wait-owned windows, and
    /// reserves the window while a wait is active (AI-001 TR-6/44).
    /// </summary>
    [Fact]
    public async Task DeliveryClaim_DeliversAndResetsOnlyWhenDeliverableAndIdle()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        try
        {
            Assert.Null(mind.TryClaimDeliveryForTest());

            // A sub-threshold ordinary accumulation is not deliverable.
            mind.ObserveForTest(new TestObservation(0.5f, "sub-threshold"));
            Assert.Null(mind.TryClaimDeliveryForTest());

            mind.ObserveForTest(new TestObservation(1f, "held"));
            MindBase.ObservationDeliveryClaim claim = mind.TryClaimDeliveryForTest()!;
            // The claim owns the complete accumulation window in FIFO order, including the sub-threshold
            // predecessor that crossed with it.
            Assert.Equal(["sub-threshold", "held"], ClaimValues(claim));
            Assert.Equal(MindBase.ObservationDeliveryUrgency.Ordinary, claim.Urgency);
            mind.CompleteDeliveryForTest(claim);
            Assert.Null(mind.TryClaimDeliveryForTest());

            mind.ObserveForTest(new TestObservation(1f, "reserved"));
            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            Assert.Null(mind.TryClaimDeliveryForTest());

            MindBase.WaitOutcome outcome = await waitTask;
            Assert.Equal(["reserved"], DeliveredValues(outcome));
            Assert.Null(mind.TryClaimDeliveryForTest());
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// An abandoned claim restores its observations to the front of the pending accumulation in FIFO order with
    /// their urgency, so a failed asynchronous rendering never silently loses the window (AI-001 TR-44).
    /// </summary>
    [Fact]
    public void DeliveryClaim_WhenAbandoned_RestoresWindowForTheNextClaimInFIFOOrder()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        try
        {
            mind.ObserveForTest(new TestObservation(1f, "claimed-then-failed"));
            MindBase.ObservationDeliveryClaim failed = mind.TryClaimDeliveryForTest()!;
            Assert.Equal(["claimed-then-failed"], ClaimValues(failed));

            // The render failed: the claim is abandoned and its window becomes deliverable again.
            mind.AbandonDeliveryForTest(failed);
            mind.ObserveForTest(new TestObservation(0.3f, "fresh-after-abandon", Fresh: true));

            MindBase.ObservationDeliveryClaim restored = mind.TryClaimDeliveryForTest()!;
            Assert.Equal(["claimed-then-failed", "fresh-after-abandon"], ClaimValues(restored));
            Assert.Equal(MindBase.ObservationDeliveryUrgency.Fresh, restored.Urgency);
            mind.CompleteDeliveryForTest(restored);
            Assert.Null(mind.TryClaimDeliveryForTest());

            // Settling an already-abandoned claim never delivers or restores twice.
            mind.AbandonDeliveryForTest(failed);
            Assert.Null(mind.TryClaimDeliveryForTest());
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// Disabling Mind pauses wake and signal while preserving accumulation; re-enable wakes a held-deliverable wait
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
        mind.DeliverySignalForTest(_ => signalled++);
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
            Assert.Equal(["while-disabled"], DeliveredValues(outcome));
            Assert.Equal(MindBase.ObservationWaitWake.ThresholdCrossed, outcome.Wake);
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
            Assert.Equal(["held-while-disabled"], DeliveredValues(delivered));
            Assert.Equal(MindBase.ObservationWaitWake.ThresholdCrossed, delivered.Wake);
            Assert.Equal(0, signalled);
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// A disabled Mind retains fresh urgency without waking or signalling: re-enable wakes an active wait with the
    /// fresh reason, and an idle retained fresh window becomes claimable — with no signal fired while disabled
    /// (AI-001 TR-5, acceptance TR-33).
    /// </summary>
    [Fact]
    public async Task DisabledMind_RetainsFreshUrgencyAndWakesOnReenable()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        int signalled = 0;
        mind.DeliverySignalForTest(_ => signalled++);
        try
        {
            // Active wait: the fresh observation stays retained while disabled and wakes on re-enable.
            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            mind.Enabled = false;
            mind.ObserveForTest(new TestObservation(0.2f, "fresh-while-disabled", Fresh: true));
            await Task.Delay(150);
            Assert.False(waitTask.IsCompleted, "A disabled Mind must not wake an active wait for a fresh observation.");
            Assert.Equal(0, signalled);

            mind.Enabled = true;
            MindBase.WaitOutcome outcome = await waitTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(MindBase.ObservationWaitWake.FreshObservation, outcome.Wake);
            Assert.Equal(["fresh-while-disabled"], DeliveredValues(outcome));
            Assert.Equal(0, signalled);

            // Idle: the retained fresh urgency is claimable after re-enable without any disabled-period signal.
            mind.Enabled = false;
            mind.ObserveForTest(new TestObservation(0.2f, "fresh-idle-while-disabled", Fresh: true));
            Assert.Equal(0, signalled);
            Assert.Null(mind.TryClaimDeliveryForTest());

            mind.Enabled = true;
            Assert.Equal(0, signalled);
            MindBase.ObservationDeliveryClaim claim = mind.TryClaimDeliveryForTest()!;
            Assert.Equal(["fresh-idle-while-disabled"], ClaimValues(claim));
            Assert.Equal(MindBase.ObservationDeliveryUrgency.Fresh, claim.Urgency);
            mind.CompleteDeliveryForTest(claim);
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
        mind.DeliverySignalForTest(_ => signalled++);
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
            Assert.Equal(["notable-while-disabled"], DeliveredValues(outcome));
            Assert.Equal(MindBase.ObservationWaitWake.QuietExpiry, outcome.Wake);
            Assert.Equal(0, signalled);
        }
        finally
        {
            mind.Free();
        }
    }

    /// <summary>
    /// Node exit during an active wait is terminal cancellation: even a fresh-woken delivery mechanism must never
    /// swallow lifetime cancellation into a normal wait result (AI-001 TR-18/19, acceptance TR-33).
    /// </summary>
    [Fact]
    public async Task NodeExit_DuringActiveWait_RemainsTerminalCancellation()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(() => new TestSceneContext([mind.Owner]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);
        bool exited = false;
        try
        {
            // A fresh-woken wait first completes normally with its delivery.
            Task<MindBase.WaitOutcome> freshWait = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            mind.ObserveForTest(new TestObservation(0.2f, "fresh-before-exit", Fresh: true));
            MindBase.WaitOutcome freshOutcome = await freshWait;
            Assert.Equal(MindBase.ObservationWaitWake.FreshObservation, freshOutcome.Wake);
            Assert.Equal(["fresh-before-exit"], DeliveredValues(freshOutcome));

            // Node exit while another wait is active stays terminal: the wait throws instead of returning a result.
            Task<MindBase.WaitOutcome> terminalWait = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            mind.QueueFree();
            exited = true;
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => terminalWait);
        }
        finally
        {
            if (!exited)
            {
                mind.QueueFree();
            }

            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// Freshness is evaluated only for accepted observations: a duplicate-suppressed fresh observation contributes
    /// neither importance nor fresh urgency, leaving an active wait to quiet expiry (AI-001 TR-42, acceptance
    /// TR-31).
    /// </summary>
    [Fact]
    public async Task Wait_WhenFreshObservationIsDuplicateSuppressed_DoesNotWake()
    {
        TestMind mind = new()
        {
            ObservationImportanceThreshold = 1f,
        };
        try
        {
            mind.ObserveForTest(new ScopedTestObservation("wait-dup", 0.3f, "repeated", Fresh: true));
            MindBase.ObservationDeliveryClaim first = mind.TryClaimDeliveryForTest()!;
            mind.CompleteDeliveryForTest(first);

            Task<MindBase.WaitOutcome> waitTask = mind.WaitForNotableForTestAsync(TimeSpan.FromSeconds(0.3), CancellationToken.None);
            await Task.Delay(50);
            mind.ObserveForTest(new ScopedTestObservation("wait-dup", 0.3f, "repeated", Fresh: true));
            await Task.Delay(100);
            Assert.False(waitTask.IsCompleted, "A suppressed duplicate must contribute neither importance nor freshness.");

            MindBase.WaitOutcome outcome = await waitTask;
            Assert.Equal(MindBase.ObservationWaitWake.QuietExpiry, outcome.Wake);
            Assert.Empty(outcome.Delivered);
            // Only the first, accepted occurrence entered the timeline.
            Assert.Equal(["repeated"], ScopedValues(mind));
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

    private static IReadOnlyList<string> DeliveredValues(MindBase.WaitOutcome outcome)
        => [.. outcome.Delivered.Cast<TestObservation>().Select(static observation => observation.Value)];

    private static IReadOnlyList<string> ClaimValues(MindBase.ObservationDeliveryClaim claim)
        => [.. claim.Observations.Cast<TestObservation>().Select(static observation => observation.Value)];

    private static IReadOnlyList<string> TimelineValues(TestMind mind)
        => [.. mind.GetTimelineForTest().Cast<TestObservation>().Select(static observation => observation.Value)];

    private static IReadOnlyList<string> SpeechContents(TestMind mind)
        => [.. mind.GetTimelineForTest().Cast<ObservedSpeech>().Select(static observation => observation.Content)];

    private static IReadOnlyList<string> ScopedValues(TestMind mind)
        => [.. mind.GetTimelineForTest().Cast<ScopedTestObservation>().Select(static observation => observation.Value)];

    private sealed partial class TestMind : MindBase
    {
        private readonly TestCharacter _character = new();

        public new ICharacter Owner => _character;

        public void ObserveForTest(AgentObservation observation) => Observe(observation);

        public Task<WaitOutcome> WaitForNotableForTestAsync(TimeSpan maxWait, CancellationToken cancellationToken)
            => WaitForNotableObservationsAsync(maxWait, cancellationToken);

        public ObservationDeliveryClaim? TryClaimDeliveryForTest() => TryClaimPendingObservationDelivery();

        public void CompleteDeliveryForTest(ObservationDeliveryClaim claim) => CompleteObservationDelivery(claim);

        public void AbandonDeliveryForTest(ObservationDeliveryClaim claim) => AbandonObservationDelivery(claim);

        public void DeliverySignalForTest(Action<ObservationDeliverySignal> handler)
            => ObservationDeliverySignalled += handler;

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

    private sealed record TestObservation(float Importance, string Value, bool Fresh = false) : AgentObservation
    {
        public override string TypeKey => "test.wait";

        public override float CalculateImportance(ObservationContext context) => Importance;

        public override bool RequiresFreshTurn(ObservationContext context) => Fresh;
    }

    private sealed record ScopedTestObservation(
        string Scope,
        float Importance,
        string Value,
        bool Fresh = false) : AgentObservation
    {
        public override string TypeKey => "test.wait.scoped";

        public override ObservationDuplicatePolicy DuplicatePolicy => ObservationDuplicatePolicy.IgnoreEquivalent;

        public override string? DuplicateScope => Scope;

        public override float CalculateImportance(ObservationContext context) => Importance;

        public override bool RequiresFreshTurn(ObservationContext context) => Fresh;

        public override bool IsSemanticallyEquivalentTo(AgentObservation other)
            => other is ScopedTestObservation scoped
                && string.Equals(scoped.Scope, Scope, StringComparison.Ordinal)
                && string.Equals(scoped.Value, Value, StringComparison.Ordinal);
    }

    private sealed class TestCharacter : ICharacter
    {
        public string Id { get; set; } = "wait_owner";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }
}
