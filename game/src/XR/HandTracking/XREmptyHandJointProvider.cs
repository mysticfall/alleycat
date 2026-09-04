using AlleyCat.Rigging;
namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Optical joint provider without data, used by XR runtimes without optical hand tracking.
/// </summary>
public sealed class XREmptyHandJointProvider : IXRHandJointProvider
{
    /// <summary>
    /// Shared stateless instance.
    /// </summary>
    public static readonly XREmptyHandJointProvider Instance = new();

    /// <inheritdoc />
    public bool TryGetJoint(LimbSide side, XRHandJoint joint, out XRHandJointSourceSample sample)
    {
        sample = new XRHandJointSourceSample(
            side,
            joint,
            HasTracker: false,
            HasTrackingData: false,
            RawFlags: 0,
            OrientationValid: false,
            OrientationTracked: false,
            PositionValid: false,
            PositionTracked: false,
            Godot.Transform3D.Identity,
            Godot.Transform3D.Identity,
            TrackerLocalTransformFinite: true,
            ProductionWorldTransformFinite: true,
            ProductionAccepted: false,
            XRHandJointSourceRejection.NoTracker);

        return false;
    }
}
