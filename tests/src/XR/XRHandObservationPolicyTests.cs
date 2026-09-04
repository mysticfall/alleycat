using AlleyCat.XR.HandTracking;
using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>
/// Unit coverage for the per-side optical observation policy (XR-002 TR2-TR4), including the interaction-profile
/// discriminator for runtimes that do not report a hand-tracking data source (WiVRn).
/// </summary>
public sealed class XRHandObservationPolicyTests
{
    /// <summary>
    /// An unobstructed, wrist-tracked optical tracker proposes optical even while a controller device profile is bound
    /// and the controller is tracked: the runtime's explicit source keeps precedence over the profile signal.
    /// </summary>
    [Theory]
    [InlineData(XRControllerProfileKind.ControllerDevice)]
    [InlineData(XRControllerProfileKind.HandInteraction)]
    [InlineData(XRControllerProfileKind.None)]
    public void Classify_UnobstructedOpticalWristTracking_ProposesOpticalRegardlessOfProfile(
        XRControllerProfileKind controllerProfileKind)
    {
        XRHandSourceObservation result = XRHandObservationPolicy.Classify(CreateInputs(
            controllerTracked: true,
            controllerProfileKind,
            opticalTrackerExists: true,
            opticalHasTrackingData: true,
            opticalWristTracked: true,
            source: XROpticalHandTrackingSource.Unobstructed));

        Assert.Equal(XRHandSourceObservation.Optical, result);
    }

    /// <summary>
    /// A wrist-tracked optical tracker with an unknown source proposes optical only when the discriminator excludes a
    /// held controller: the controller side is not tracked (controllerless runtimes without the data-source
    /// extension), or the controller path is bound to a hand-input emulation profile — hand interaction, or the
    /// Khronos simple controller WiVRn keeps pose-alive from the hands after the controllers are put down (WiVRn never
    /// reports <c>hand_interaction</c>).
    /// </summary>
    [Theory]
    [InlineData(false, XRControllerProfileKind.ControllerDevice)]
    [InlineData(false, XRControllerProfileKind.None)]
    [InlineData(true, XRControllerProfileKind.HandInteraction)]
    [InlineData(true, XRControllerProfileKind.SimpleController)]
    public void Classify_UnknownSourceWristTrackedWithoutHeldController_ProposesOptical(
        bool controllerTracked,
        XRControllerProfileKind controllerProfileKind)
    {
        XRHandSourceObservation result = XRHandObservationPolicy.Classify(CreateInputs(
            controllerTracked,
            controllerProfileKind,
            opticalTrackerExists: true,
            opticalHasTrackingData: true,
            opticalWristTracked: true,
            source: XROpticalHandTrackingSource.Unknown));

        Assert.Equal(XRHandSourceObservation.Optical, result);
    }

    /// <summary>
    /// A wrist-tracked optical tracker with an unknown source stays ambiguous while a held controller is indicated:
    /// the controller is tracked with a controller-device profile bound, or tracked with no profile bound yet
    /// (startup). A controller-device profile with an untracked controller is covered by the optical fallback above.
    /// </summary>
    [Theory]
    [InlineData(true, XRControllerProfileKind.ControllerDevice)]
    [InlineData(true, XRControllerProfileKind.None)]
    public void Classify_UnknownSourceWithHeldControllerIndicator_RemainsAmbiguous(
        bool controllerTracked,
        XRControllerProfileKind controllerProfileKind)
    {
        XRHandSourceObservation result = XRHandObservationPolicy.Classify(CreateInputs(
            controllerTracked,
            controllerProfileKind,
            opticalTrackerExists: true,
            opticalHasTrackingData: true,
            opticalWristTracked: true,
            source: XROpticalHandTrackingSource.Unknown));

        Assert.Equal(XRHandSourceObservation.Ambiguous, result);
    }

    /// <summary>
    /// A tracked controller with a controller-device profile proposes controller when the optical side is absent, not
    /// tracking, reports a not-tracked source, or explicitly reports controller-inferred hand data.
    /// </summary>
    [Theory]
    [InlineData(false, false, false, XROpticalHandTrackingSource.Unknown)]
    [InlineData(true, false, false, XROpticalHandTrackingSource.Unknown)]
    [InlineData(true, true, true, XROpticalHandTrackingSource.Controller)]
    [InlineData(true, true, true, XROpticalHandTrackingSource.NotTracked)]
    public void Classify_TrackedControllerDeviceWithoutOpticalCompetition_ProposesController(
        bool opticalTrackerExists,
        bool opticalHasTrackingData,
        bool opticalWristTracked,
        XROpticalHandTrackingSource source)
    {
        XRHandSourceObservation result = XRHandObservationPolicy.Classify(CreateInputs(
            controllerTracked: true,
            controllerProfileKind: XRControllerProfileKind.ControllerDevice,
            opticalTrackerExists,
            opticalHasTrackingData,
            opticalWristTracked,
            source));

        Assert.Equal(XRHandSourceObservation.Controller, result);
    }

    /// <summary>
    /// A tracked controller without a bound profile (empty profile at startup) stays ambiguous instead of proposing
    /// controller, retaining the initial controller mode without asserting controller identity.
    /// </summary>
    [Fact]
    public void Classify_TrackedControllerWithNoneProfileAtStartup_IsAmbiguous()
    {
        XRHandSourceObservation result = XRHandObservationPolicy.Classify(CreateInputs(
            controllerTracked: true,
            controllerProfileKind: XRControllerProfileKind.None,
            opticalTrackerExists: false,
            opticalHasTrackingData: false,
            opticalWristTracked: false,
            source: XROpticalHandTrackingSource.Unknown));

        Assert.Equal(XRHandSourceObservation.Ambiguous, result);
    }

    /// <summary>
    /// A tracked controller bound to a hand-input emulation profile (hand interaction, or the simple controller WiVRn
    /// emulates from the hands) never proposes controller — the profile indicates optical input even when the optical
    /// tracker momentarily has no data, so the side stays ambiguous and the retained mode governs: an optical session
    /// freezes on loss instead of reverting, and a controller session retains controller.
    /// </summary>
    [Theory]
    [InlineData(XRControllerProfileKind.HandInteraction)]
    [InlineData(XRControllerProfileKind.SimpleController)]
    public void Classify_TrackedControllerWithHandInputEmulationProfileAndNoOpticalData_IsAmbiguous(
        XRControllerProfileKind controllerProfileKind)
    {
        XRHandSourceObservation result = XRHandObservationPolicy.Classify(CreateInputs(
            controllerTracked: true,
            controllerProfileKind,
            opticalTrackerExists: true,
            opticalHasTrackingData: false,
            opticalWristTracked: false,
            source: XROpticalHandTrackingSource.Unknown));

        Assert.Equal(XRHandSourceObservation.Ambiguous, result);
    }

    /// <summary>
    /// Neither source tracked, incomplete optical validity, or a missing controller side is ambiguous.
    /// </summary>
    [Fact]
    public void Classify_NoTrackedSourceOrIncompleteValidity_IsAmbiguous()
    {
        Assert.Equal(
            XRHandSourceObservation.Ambiguous,
            XRHandObservationPolicy.Classify(CreateInputs(
                controllerTracked: false,
                controllerProfileKind: XRControllerProfileKind.ControllerDevice,
                opticalTrackerExists: false,
                opticalHasTrackingData: false,
                opticalWristTracked: false,
                source: XROpticalHandTrackingSource.Unknown)));

        // Tracker exists and has data, but the wrist joint is not actively tracked.
        Assert.Equal(
            XRHandSourceObservation.Ambiguous,
            XRHandObservationPolicy.Classify(CreateInputs(
                controllerTracked: false,
                controllerProfileKind: XRControllerProfileKind.ControllerDevice,
                opticalTrackerExists: true,
                opticalHasTrackingData: true,
                opticalWristTracked: false,
                source: XROpticalHandTrackingSource.Unobstructed)));

        // Controller absent while optical data is controller-inferred.
        Assert.Equal(
            XRHandSourceObservation.Ambiguous,
            XRHandObservationPolicy.Classify(CreateInputs(
                controllerTracked: false,
                controllerProfileKind: XRControllerProfileKind.ControllerDevice,
                opticalTrackerExists: true,
                opticalHasTrackingData: true,
                opticalWristTracked: true,
                source: XROpticalHandTrackingSource.Controller)));
    }

    private static XRHandObservationInputs CreateInputs(
        bool controllerTracked,
        XRControllerProfileKind controllerProfileKind,
        bool opticalTrackerExists,
        bool opticalHasTrackingData,
        bool opticalWristTracked,
        XROpticalHandTrackingSource source)
        => new(
            controllerTracked,
            controllerProfileKind,
            opticalTrackerExists,
            opticalHasTrackingData,
            opticalWristTracked,
            source);
}
