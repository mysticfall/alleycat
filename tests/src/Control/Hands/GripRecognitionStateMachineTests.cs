using AlleyCat.Control.Hands;
using Xunit;

namespace AlleyCat.Tests.Control.Hands;

/// <summary>
/// Unit coverage of the grip hysteresis and stability state machine (XR-002 TR48, TR51; CTRL-002 TR12, TR14):
/// stability-interval edge emission, interrupt and candidate-change resets, hysteresis between the
/// thresholds, over-clench and insufficient-validity fail-closed behaviour.
/// </summary>
public sealed class GripRecognitionStateMachineTests
{
    private static readonly PowerGripRecognitionSettings _settings = new(
        grabThreshold: 0.85f,
        releaseThreshold: 0.55f,
        stabilitySeconds: 0.10f);

    private readonly object _candidateA = new();

    private readonly object _candidateB = new();

    /// <summary>A grab edge is emitted only after the grab threshold holds for the full stability interval.</summary>
    [Fact]
    public void GrabEdge_RequiresContinuousHoldForTheStabilityInterval()
    {
        GripRecognitionStateMachine machine = new(_settings);
        var closed = new GripRecognitionEvaluation(0.95f, SufficientValidity: true);
        var open = new GripRecognitionEvaluation(0.1f, SufficientValidity: true);

        // Sub-interval holds alone never reach the edge.
        Assert.Equal(GripEdge.None, machine.Evaluate(closed, 0.05f, _candidateA));
        Assert.Equal(GripEdge.None, machine.Evaluate(closed, 0.04f, _candidateA));
        Assert.False(machine.IsGrabRecognised);

        // An interrupt resets the accumulation, so a fresh continuous full-interval hold is required.
        Assert.Equal(GripEdge.None, machine.Evaluate(open, 0.02f, _candidateA));
        Assert.Equal(GripEdge.None, machine.Evaluate(closed, 0.06f, _candidateA));
        Assert.Equal(GripEdge.Grab, machine.Evaluate(closed, 0.05f, _candidateA));
        Assert.True(machine.IsGrabRecognised);

        // After the edge, the same closed pose emits nothing further.
        Assert.Equal(GripEdge.None, machine.Evaluate(closed, 0.20f, _candidateA));
        Assert.True(machine.IsGrabRecognised);
    }

    /// <summary>An interrupt between threshold-holding evaluations resets the accumulated stability (no edge).</summary>
    [Fact]
    public void InterruptedHold_ResetsAccumulation()
    {
        GripRecognitionStateMachine machine = new(_settings);
        var closed = new GripRecognitionEvaluation(0.95f, true);
        var open = new GripRecognitionEvaluation(0.1f, true);

        Assert.Equal(GripEdge.None, machine.Evaluate(closed, 0.06f, _candidateA));
        Assert.Equal(GripEdge.None, machine.Evaluate(open, 0.02f, _candidateA));
        Assert.Equal(GripEdge.None, machine.Evaluate(closed, 0.06f, _candidateA));
        Assert.False(machine.IsGrabRecognised);

        // Only a fully continuous hold after the interrupt emits the edge.
        Assert.Equal(GripEdge.Grab, machine.Evaluate(closed, 0.05f, _candidateA));
    }

    /// <summary>A candidate-identity change resets stability accumulation without locking a speculative candidate.</summary>
    [Fact]
    public void CandidateChange_ResetsAccumulation()
    {
        GripRecognitionStateMachine machine = new(_settings);
        var closed = new GripRecognitionEvaluation(0.95f, true);

        Assert.Equal(GripEdge.None, machine.Evaluate(closed, 0.06f, _candidateA));
        Assert.Equal(GripEdge.None, machine.Evaluate(closed, 0.03f, _candidateB));

        // The candidate changed: the 0.09s total spans two candidates, so no edge yet.
        Assert.Equal(GripEdge.None, machine.Evaluate(closed, 0.03f, _candidateB));
        Assert.Equal(GripEdge.Grab, machine.Evaluate(closed, 0.05f, _candidateB));
    }

    /// <summary>While grabbed, opening to or below the release threshold emits a release edge after stability.</summary>
    [Fact]
    public void ReleaseEdge_EmitsAfterStableOpening()
    {
        GripRecognitionStateMachine machine = new(_settings);
        var closed = new GripRecognitionEvaluation(0.95f, true);
        var open = new GripRecognitionEvaluation(0.2f, true);

        Assert.Equal(GripEdge.Grab, machine.Evaluate(closed, _settings.StabilitySeconds + 0.01f, _candidateA));
        Assert.True(machine.IsGrabRecognised);

        Assert.Equal(GripEdge.None, machine.Evaluate(open, 0.08f, _candidateA));
        Assert.Equal(GripEdge.Release, machine.Evaluate(open, 0.03f, _candidateA));
        Assert.False(machine.IsGrabRecognised);
    }

    /// <summary>Over-clench while grabbed never releases, and poses between the thresholds emit no chatter.</summary>
    [Fact]
    public void OverClench_AndIntermediateScores_DoNotChatter()
    {
        GripRecognitionStateMachine machine = new(_settings);
        var closed = new GripRecognitionEvaluation(0.95f, true);
        var overClench = new GripRecognitionEvaluation(1.6f, true);
        var intermediate = new GripRecognitionEvaluation(0.7f, true);

        Assert.Equal(GripEdge.Grab, machine.Evaluate(closed, _settings.StabilitySeconds + 0.01f, _candidateA));

        // Over-clench and intermediate scores hold the grab; alternating them never emits an edge.
        for (int frame = 0; frame < 12; frame++)
        {
            GripRecognitionEvaluation evaluation = frame % 2 == 0 ? overClench : intermediate;
            Assert.Equal(GripEdge.None, machine.Evaluate(evaluation, 0.016f, _candidateA));
        }

        Assert.True(machine.IsGrabRecognised);
    }

    /// <summary>
    /// Chattering across the grab threshold never emits an edge: the condition must hold continuously.
    /// </summary>
    [Fact]
    public void GrabThresholdChatter_EmitsNoEdge()
    {
        GripRecognitionStateMachine machine = new(_settings);
        var closed = new GripRecognitionEvaluation(0.9f, true);
        var intermediate = new GripRecognitionEvaluation(0.7f, true);

        for (int frame = 0; frame < 20; frame++)
        {
            GripRecognitionEvaluation evaluation = frame % 2 == 0 ? closed : intermediate;
            Assert.Equal(GripEdge.None, machine.Evaluate(evaluation, 0.016f, _candidateA));
        }

        Assert.False(machine.IsGrabRecognised);
    }

    /// <summary>
    /// Insufficient validity resets accumulation and emits no edge — including no synthetic release while
    /// grabbed (XR-002 TR48, TR51; CTRL-002 TR15).
    /// </summary>
    [Fact]
    public void InsufficientValidity_FailsClosedWithoutSyntheticRelease()
    {
        GripRecognitionStateMachine machine = new(_settings);
        var closed = new GripRecognitionEvaluation(0.95f, true);
        var invalid = new GripRecognitionEvaluation(0.1f, SufficientValidity: false);

        Assert.Equal(GripEdge.Grab, machine.Evaluate(closed, _settings.StabilitySeconds + 0.01f, _candidateA));

        Assert.Equal(GripEdge.None, machine.Evaluate(invalid, 1.0f, _candidateA));
        Assert.True(machine.IsGrabRecognised, "Insufficient validity must never synthesise a release.");

        // The invalid stretch reset accumulation: the release needs the full interval after recovery.
        var open = new GripRecognitionEvaluation(0.2f, true);
        Assert.Equal(GripEdge.None, machine.Evaluate(open, 0.09f, _candidateA));
        Assert.Equal(GripEdge.Release, machine.Evaluate(open, 0.02f, _candidateA));
    }

    /// <summary>
    /// Reset forces the grabbed latch and clears accumulation (CTRL-002 TR6, TR8, TR18): the coordinator's
    /// reconciliation hook for externally driven lifecycle changes and pause boundaries.
    /// </summary>
    [Fact]
    public void Reset_ForcesGrabbedLatchAndClearsAccumulation()
    {
        GripRecognitionStateMachine machine = new(_settings);
        var closed = new GripRecognitionEvaluation(0.95f, true);

        // A partially accumulated grab hold is discarded by a reset to not-grabbed.
        Assert.Equal(GripEdge.None, machine.Evaluate(closed, 0.09f, _candidateA));
        machine.Reset(isGrabRecognised: false);
        Assert.False(machine.IsGrabRecognised);
        Assert.Equal(GripEdge.None, machine.Evaluate(closed, 0.09f, _candidateA));
        Assert.Equal(GripEdge.Grab, machine.Evaluate(closed, 0.02f, _candidateA));

        // A reset to grabbed — for example after external reconciliation with a held grab — keeps the latch and
        // requires a full fresh stability interval before a release edge.
        var open = new GripRecognitionEvaluation(0.1f, true);
        Assert.Equal(GripEdge.None, machine.Evaluate(open, 0.09f, _candidateA));
        machine.Reset(isGrabRecognised: true);
        Assert.True(machine.IsGrabRecognised);
        Assert.Equal(GripEdge.None, machine.Evaluate(open, 0.09f, _candidateA));
        Assert.Equal(GripEdge.Release, machine.Evaluate(open, 0.02f, _candidateA));
        Assert.False(machine.IsGrabRecognised);
    }

    /// <summary>
    /// A reset to not-grabbed suppresses the post-reset edge burst a threshold condition held across the reset
    /// would otherwise emit (CTRL-002 TR18 — no burst fires on unpause).
    /// </summary>
    [Fact]
    public void ResetToNotGrabbed_WithGrabThresholdHeld_DoesNotEmitABurst()
    {
        GripRecognitionStateMachine machine = new(_settings);
        var closed = new GripRecognitionEvaluation(0.95f, true);

        machine.Reset(isGrabRecognised: false);
        for (int frame = 0; frame < 4; frame++)
        {
            Assert.Equal(GripEdge.None, machine.Evaluate(closed, 0.02f, _candidateA));
            machine.Reset(isGrabRecognised: false);
        }

        Assert.False(machine.IsGrabRecognised);
    }

    /// <summary>The machine rejects a null settings record explicitly.</summary>
    [Fact]
    public void Constructor_RejectsNullSettings()
        => Assert.Throws<ArgumentNullException>(() => new GripRecognitionStateMachine(null!));
}
