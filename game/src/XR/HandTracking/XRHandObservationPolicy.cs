namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Optical hand-tracking source classification mirrored from the runtime's hand-tracker reports
/// (XR-002 TR2). Runtime-agnostic so the observation policy stays unit-testable without Godot.
/// </summary>
public enum XROpticalHandTrackingSource
{
    /// <summary>
    /// The runtime did not report how hand data is captured.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Hand data is captured optically without obstruction.
    /// </summary>
    Unobstructed = 1,

    /// <summary>
    /// Hand data is inferred from a held controller.
    /// </summary>
    Controller = 2,

    /// <summary>
    /// The runtime explicitly reports the source as not tracked.
    /// </summary>
    NotTracked = 3,
}

/// <summary>
/// Kind of interaction profile currently bound to a side's controller input path, derived from the bound
/// <c>XRPositionalTracker</c> profile (XR-002 TR2).
/// </summary>
public enum XRControllerProfileKind
{
    /// <summary>
    /// No interaction profile is bound yet — an empty profile, no tracker registered for the side, or the runtime's
    /// <c>/interaction_profiles/none</c> startup placeholder (WiVRn reports this before any device binds).
    /// </summary>
    None = 0,

    /// <summary>
    /// A vendor controller device profile is bound (for example
    /// <c>/interaction_profiles/oculus/touch_controller</c>), indicating a held physical controller.
    /// </summary>
    ControllerDevice = 1,

    /// <summary>
    /// The OpenXR hand-interaction profile is bound (<c>/interaction_profiles/ext/hand_interaction_ext</c>), meaning the
    /// runtime routes hand input optically with pinch/grasp emulation instead of from a held controller.
    /// </summary>
    HandInteraction = 2,

    /// <summary>
    /// The Khronos simple-controller profile is bound (<c>/interaction_profiles/khr/simple_controller</c>). WiVRn never
    /// reports <c>hand_interaction</c>: while the hands are active it drives a pose-alive controller tracker from an
    /// emulated <c>simple_controller</c> device, so this profile indicates hand input rather than a held controller on
    /// such runtimes.
    /// </summary>
    SimpleController = 3,
}

/// <summary>
/// Per-side runtime inputs used to derive an <see cref="XRHandSourceObservation" /> (XR-002 TR2).
/// </summary>
/// <param name="ControllerTracked">Whether the side's XR controller node currently reports active tracking data.</param>
/// <param name="ControllerProfileKind">
/// Kind of interaction profile bound to the side's controller input path; runtimes such as WiVRn keep the controller
/// tracker pose-alive through an emulated profile after the physical controllers are put down — either the
/// hand-interaction profile (runtimes that switch profiles properly) or a Khronos simple-controller device driven from
/// the hands (WiVRn's actual behaviour).
/// </param>
/// <param name="OpticalTrackerExists">Whether the side's optical hand tracker is registered with the XR server.</param>
/// <param name="OpticalHasTrackingData">
/// Whether the side's optical hand tracker reports tracking data this frame.
/// </param>
/// <param name="OpticalWristTracked">
/// Whether the tracker's wrist joint reports both position and orientation as actively tracked.
/// </param>
/// <param name="OpticalSource">How the runtime reports the optical hand data is being captured.</param>
public readonly record struct XRHandObservationInputs(
    bool ControllerTracked,
    XRControllerProfileKind ControllerProfileKind,
    bool OpticalTrackerExists,
    bool OpticalHasTrackingData,
    bool OpticalWristTracked,
    XROpticalHandTrackingSource OpticalSource);

/// <summary>
/// Conservative per-side observation policy feeding the bilateral mode arbiter (XR-002 TR2-TR4).
/// </summary>
/// <remarks>
/// <para>
/// This resolves the spec's deliberately-open per-side signal choice with the following policy:
/// <list type="bullet">
/// <item><description>
/// <see cref="XRHandSourceObservation.Optical" /> — the tracker exists, has tracking data, tracks the wrist, and
/// reports an <see cref="XROpticalHandTrackingSource.Unobstructed" /> source. Practical fallbacks for an
/// <see cref="XROpticalHandTrackingSource.Unknown" /> source (runtimes that do not implement
/// <c>XR_EXT_hand_tracking_data_source</c>, such as WiVRn): the wrist is tracked while the controller side is not
/// tracked, or while the controller path is bound to a hand-input emulation profile — hand-interaction or the
/// Khronos simple controller WiVRn drives from the hands.
/// </description></item>
/// <item><description>
/// <see cref="XRHandSourceObservation.Controller" /> — the controller node has current tracking data, a vendor
/// controller-device profile is bound, and the optical side is absent, has no tracking data, or reports a
/// <see cref="XROpticalHandTrackingSource.NotTracked" /> or
/// <see cref="XROpticalHandTrackingSource.Controller" /> source.
/// </description></item>
/// <item><description>
/// <see cref="XRHandSourceObservation.Ambiguous" /> — everything else: both sources active with an unknown source
/// while a controller-device profile is bound, an emulation profile with no optical data (neither identity is
/// asserted), neither tracked, incomplete validity, or an empty profile at startup. Ambiguity retains the committed
/// mode.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// Profile identity — not controller tracker liveness — is the reliable discriminator for unknown-source runtimes:
/// vendor device profiles (for example <c>/interaction_profiles/oculus/touch_controller</c>) indicate a held
/// controller, while hand-input emulation profiles indicate optical input. WiVRn hardware logs show it never reports
/// <c>hand_interaction</c>: while the hands are active it keeps the controller tracker pose-alive through an emulated
/// <c>/interaction_profiles/khr/simple_controller</c> device driven from the hands, and it reports
/// <c>/interaction_profiles/none</c> at startup. A tracked controller
/// with a <see cref="XRControllerProfileKind.SimpleController" /> or
/// <see cref="XRControllerProfileKind.HandInteraction" /> profile therefore indicates optical input rather than a
/// held controller; with an emulation profile bound and no optical data, no identity is asserted, so the side stays
/// ambiguous and the retained mode governs. On runtimes that do expose a data source,
/// <see cref="XROpticalHandTrackingSource.Unobstructed" /> and
/// <see cref="XROpticalHandTrackingSource.Controller" /> keep precedence over the profile signal.
/// </para>
/// </remarks>
public static class XRHandObservationPolicy
{
    /// <summary>
    /// Classifies one side's runtime inputs into a per-side observation for the arbiter.
    /// </summary>
    /// <param name="inputs">Per-side runtime tracking inputs.</param>
    /// <returns>The unambiguous proposal for the side, or ambiguity when no single source is identifiable.</returns>
    public static XRHandSourceObservation Classify(XRHandObservationInputs inputs)
        => ProposesOptical(inputs)
            ? XRHandSourceObservation.Optical
            : ProposesController(inputs)
                ? XRHandSourceObservation.Controller
                : XRHandSourceObservation.Ambiguous;

    private static bool ProposesOptical(XRHandObservationInputs inputs)
        => inputs.OpticalTrackerExists
           && inputs.OpticalHasTrackingData
           && inputs.OpticalWristTracked
           && (inputs.OpticalSource == XROpticalHandTrackingSource.Unobstructed
               || (inputs.OpticalSource == XROpticalHandTrackingSource.Unknown
                   && (inputs.ControllerProfileKind
                           is XRControllerProfileKind.HandInteraction or XRControllerProfileKind.SimpleController
                       || !inputs.ControllerTracked)));

    private static bool ProposesController(XRHandObservationInputs inputs)
        => inputs.ControllerTracked
           && inputs.ControllerProfileKind == XRControllerProfileKind.ControllerDevice
           && (!inputs.OpticalTrackerExists
               || !inputs.OpticalHasTrackingData
               || inputs.OpticalSource is XROpticalHandTrackingSource.Controller
                   or XROpticalHandTrackingSource.NotTracked);
}
