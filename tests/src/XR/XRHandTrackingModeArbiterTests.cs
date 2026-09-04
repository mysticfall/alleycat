using AlleyCat.XR.HandTracking;
using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>
/// Unit coverage for the bilateral hand-pose mode arbiter state table (XR-002 TR1-TR5, AC10).
/// </summary>
public sealed class XRHandTrackingModeArbiterTests
{
    /// <summary>
    /// The arbiter must start committed to the controller mode before any evaluation (XR-002 TR1).
    /// </summary>
    [Fact]
    public void CommittedMode_BeforeAnyEvaluation_IsController()
    {
        XRHandTrackingModeArbiter arbiter = new();

        Assert.Equal(XRHandTrackingMode.Controller, arbiter.CommittedMode);
    }

    /// <summary>
    /// Initial bilateral ambiguity must retain the initial controller mode without raising a change event.
    /// </summary>
    [Fact]
    public void Evaluate_WithInitialAmbiguity_RetainsControllerModeWithoutEvent()
    {
        XRHandTrackingModeArbiter arbiter = new();
        int modeChanges = 0;
        arbiter.ModeChanged += () => modeChanges++;

        bool changed = arbiter.Evaluate(XRHandSourceObservation.Ambiguous, XRHandSourceObservation.Ambiguous);

        Assert.False(changed);
        Assert.Equal(XRHandTrackingMode.Controller, arbiter.CommittedMode);
        Assert.Equal(0, modeChanges);
    }

    /// <summary>
    /// Bilateral controller agreement while already committed to controller must retain the mode without a change
    /// notification (state table row 2).
    /// </summary>
    [Fact]
    public void Evaluate_BothController_WhileCommittedController_ReturnsNoChangeWithoutEvent()
    {
        XRHandTrackingModeArbiter arbiter = new();
        int modeChanges = 0;
        arbiter.ModeChanged += () => modeChanges++;

        bool changed = arbiter.Evaluate(XRHandSourceObservation.Controller, XRHandSourceObservation.Controller);

        Assert.False(changed);
        Assert.Equal(XRHandTrackingMode.Controller, arbiter.CommittedMode);
        Assert.Equal(0, modeChanges);
    }

    /// <summary>
    /// Bilateral optical agreement while committed to controller must commit optical and raise the change event
    /// exactly once (state table row 1).
    /// </summary>
    [Fact]
    public void Evaluate_BothOptical_WhileCommittedController_CommitsOpticalAndRaisesOnce()
    {
        XRHandTrackingModeArbiter arbiter = new();
        int modeChanges = 0;
        arbiter.ModeChanged += () => modeChanges++;

        bool changed = arbiter.Evaluate(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);

        Assert.True(changed);
        Assert.Equal(XRHandTrackingMode.Optical, arbiter.CommittedMode);
        Assert.Equal(1, modeChanges);
    }

    /// <summary>
    /// Every disagreement permutation must retain the prior committed mode under both committed bases
    /// (state table rows 3 and 6, AC3).
    /// </summary>
    [Theory]
    [InlineData(XRHandTrackingMode.Controller, XRHandSourceObservation.Controller, XRHandSourceObservation.Optical)]
    [InlineData(XRHandTrackingMode.Controller, XRHandSourceObservation.Optical, XRHandSourceObservation.Controller)]
    [InlineData(XRHandTrackingMode.Optical, XRHandSourceObservation.Controller, XRHandSourceObservation.Optical)]
    [InlineData(XRHandTrackingMode.Optical, XRHandSourceObservation.Optical, XRHandSourceObservation.Controller)]
    public void Evaluate_Disagreement_RetainsCommittedMode(
        XRHandTrackingMode committedMode,
        XRHandSourceObservation left,
        XRHandSourceObservation right)
    {
        XRHandTrackingModeArbiter arbiter = new();
        CommitMode(arbiter, committedMode);
        int modeChanges = 0;
        arbiter.ModeChanged += () => modeChanges++;

        bool changed = arbiter.Evaluate(left, right);

        Assert.False(changed);
        Assert.Equal(committedMode, arbiter.CommittedMode);
        Assert.Equal(0, modeChanges);
    }

    /// <summary>
    /// One or both sides ambiguous must retain the prior committed mode under both committed bases
    /// (state table rows 3 and 6, AC3).
    /// </summary>
    [Theory]
    [InlineData(XRHandTrackingMode.Controller, XRHandSourceObservation.Ambiguous, XRHandSourceObservation.Ambiguous)]
    [InlineData(XRHandTrackingMode.Controller, XRHandSourceObservation.Ambiguous, XRHandSourceObservation.Controller)]
    [InlineData(XRHandTrackingMode.Controller, XRHandSourceObservation.Controller, XRHandSourceObservation.Ambiguous)]
    [InlineData(XRHandTrackingMode.Optical, XRHandSourceObservation.Ambiguous, XRHandSourceObservation.Ambiguous)]
    [InlineData(XRHandTrackingMode.Optical, XRHandSourceObservation.Ambiguous, XRHandSourceObservation.Optical)]
    [InlineData(XRHandTrackingMode.Optical, XRHandSourceObservation.Optical, XRHandSourceObservation.Ambiguous)]
    [InlineData(XRHandTrackingMode.Optical, XRHandSourceObservation.Ambiguous, XRHandSourceObservation.Controller)]
    [InlineData(XRHandTrackingMode.Optical, XRHandSourceObservation.Controller, XRHandSourceObservation.Ambiguous)]
    public void Evaluate_Ambiguity_RetainsCommittedMode(
        XRHandTrackingMode committedMode,
        XRHandSourceObservation left,
        XRHandSourceObservation right)
    {
        XRHandTrackingModeArbiter arbiter = new();
        CommitMode(arbiter, committedMode);
        int modeChanges = 0;
        arbiter.ModeChanged += () => modeChanges++;

        bool changed = arbiter.Evaluate(left, right);

        Assert.False(changed);
        Assert.Equal(committedMode, arbiter.CommittedMode);
        Assert.Equal(0, modeChanges);
    }

    /// <summary>
    /// Optical sample loss — one or both sides dropping to ambiguity while committed to optical — must retain the
    /// optical mode; sample loss never exits optical (XR-002 TR5, state table row 6, AC3).
    /// </summary>
    [Theory]
    [InlineData(XRHandSourceObservation.Ambiguous, XRHandSourceObservation.Optical)]
    [InlineData(XRHandSourceObservation.Optical, XRHandSourceObservation.Ambiguous)]
    [InlineData(XRHandSourceObservation.Ambiguous, XRHandSourceObservation.Ambiguous)]
    public void Evaluate_OpticalSampleLoss_WhileCommittedOptical_RetainsOpticalMode(
        XRHandSourceObservation left,
        XRHandSourceObservation right)
    {
        XRHandTrackingModeArbiter arbiter = new();
        CommitMode(arbiter, XRHandTrackingMode.Optical);
        int modeChanges = 0;
        arbiter.ModeChanged += () => modeChanges++;

        bool changed = arbiter.Evaluate(left, right);

        Assert.False(changed);
        Assert.Equal(XRHandTrackingMode.Optical, arbiter.CommittedMode);
        Assert.Equal(0, modeChanges);
    }

    /// <summary>
    /// Later bilateral controller agreement while committed to optical must switch back to controller
    /// (state table row 4, AC5).
    /// </summary>
    [Fact]
    public void Evaluate_BothController_WhileCommittedOptical_CommitsController()
    {
        XRHandTrackingModeArbiter arbiter = new();
        int modeChanges = 0;
        arbiter.ModeChanged += () => modeChanges++;

        _ = arbiter.Evaluate(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);
        bool changed = arbiter.Evaluate(XRHandSourceObservation.Controller, XRHandSourceObservation.Controller);

        Assert.True(changed);
        Assert.Equal(XRHandTrackingMode.Controller, arbiter.CommittedMode);
        Assert.Equal(2, modeChanges);
    }

    /// <summary>
    /// Repeated bilateral agreement with the already-committed mode must not raise duplicate change notifications
    /// (state table rows 2 and 5).
    /// </summary>
    [Fact]
    public void Evaluate_RepeatedAgreementWithCommittedMode_RaisesNoDuplicateEvents()
    {
        XRHandTrackingModeArbiter arbiter = new();
        int modeChanges = 0;
        arbiter.ModeChanged += () => modeChanges++;

        _ = arbiter.Evaluate(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);
        Assert.Equal(1, modeChanges);

        for (int i = 0; i < 3; i++)
        {
            bool changed = arbiter.Evaluate(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);

            Assert.False(changed);
        }

        Assert.Equal(XRHandTrackingMode.Optical, arbiter.CommittedMode);
        Assert.Equal(1, modeChanges);
    }

    private static void CommitMode(XRHandTrackingModeArbiter arbiter, XRHandTrackingMode mode)
        => _ = arbiter.Evaluate(ObservationFor(mode), ObservationFor(mode));

    private static XRHandSourceObservation ObservationFor(XRHandTrackingMode mode)
        => mode == XRHandTrackingMode.Optical
            ? XRHandSourceObservation.Optical
            : XRHandSourceObservation.Controller;
}
