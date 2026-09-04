using System.Diagnostics.CodeAnalysis;
using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Controller-mode default hand-tracking surface for XR runtimes without optical support (XR-002 TR26-TR29).
/// </summary>
/// <remarks>
/// The committed mode is permanently <see cref="XRHandTrackingMode.Controller" />, wrists come from the existing
/// controller hand-position calibration anchors (XR-002 TR8), and no optical joints are available.
/// </remarks>
public sealed class XRControllerHandTracking(IXRHandController rightController, IXRHandController leftController)
{
    private readonly XRControllerHandPoseSource _rightSource = new(LimbSide.Right, rightController);

    private readonly XRControllerHandPoseSource _leftSource = new(LimbSide.Left, leftController);

    /// <summary>
    /// Gets the committed hand-pose mode, permanently controller for runtimes without optical support.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public XRHandTrackingMode HandTrackingMode => XRHandTrackingMode.Controller;

    /// <summary>
    /// Gets the per-side controller hand-pose source.
    /// </summary>
    /// <param name="side">Limb side of the hand.</param>
    /// <returns>The controller-only hand-pose source for the requested side.</returns>
    public IXRHandPoseSource GetHandPoseSource(LimbSide side)
        => side == LimbSide.Right ? _rightSource : _leftSource;

    /// <summary>
    /// Gets the shared optical joint provider without data.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public IXRHandJointProvider OpticalHandJoints => XREmptyHandJointProvider.Instance;
}

/// <summary>
/// Controller-side hand-pose source returning the live controller hand-position calibration anchor as the wrist.
/// </summary>
/// <remarks>
/// Reports unambiguous controller observations; a runtime without optical support can never leave
/// <see cref="XRHandTrackingMode.Controller" />.
/// </remarks>
internal sealed class XRControllerHandPoseSource(LimbSide side, IXRHandController controller) : IXRHandPoseSource
{
    /// <inheritdoc />
    public LimbSide Side => side;

    /// <inheritdoc />
    public XRHandTrackingMode SelectedMode => XRHandTrackingMode.Controller;

    /// <inheritdoc />
    public XRHandSourceObservation Observation => XRHandSourceObservation.Controller;

    /// <inheritdoc />
    public bool EverCapturedWrist => false;

    /// <inheritdoc />
    public bool TryGetCalibratedWristTransform(out Transform3D transform)
    {
        transform = controller.HandPositionNode.GlobalTransform;

        return true;
    }
}
