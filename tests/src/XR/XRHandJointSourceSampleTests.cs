using AlleyCat.Rigging;
using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>Unit coverage for the value-type production source-sample boundary.</summary>
public sealed class XRHandJointSourceSampleTests
{
    /// <summary>The empty provider preserves caller-stable identity and reports an allocation-free missing tracker.</summary>
    [Fact]
    public void EmptyProvider_ReturnsIdentifiedMissingTrackerSample()
    {
        bool accepted = XREmptyHandJointProvider.Instance.TryGetJoint(
            LimbSide.Right,
            XRHandJoint.IndexProximal,
            out XRHandJointSourceSample sample);

        Assert.False(accepted);
        Assert.Equal(LimbSide.Right, sample.Side);
        Assert.Equal(XRHandJoint.IndexProximal, sample.Joint);
        Assert.False(sample.HasTracker);
        Assert.False(sample.HasTrackingData);
        Assert.Equal(0, sample.RawFlags);
        Assert.False(sample.OrientationValid);
        Assert.False(sample.OrientationTracked);
        Assert.False(sample.PositionValid);
        Assert.False(sample.PositionTracked);
        Assert.Equal(Transform3D.Identity, sample.TrackerLocalTransform);
        Assert.Equal(Transform3D.Identity, sample.ProductionWorldTransform);
        Assert.True(sample.TrackerLocalTransformFinite);
        Assert.True(sample.ProductionWorldTransformFinite);
        Assert.True(sample.TransformFinite);
        Assert.False(sample.ProductionAccepted);
        Assert.Equal(XRHandJointSourceRejection.NoTracker, sample.RejectionReason);
    }

    /// <summary>Position validity can be absent while an orientation-accepted source sample stays accepted.</summary>
    [Fact]
    public void PositionInvalidOrientationAcceptedSample_RetainsAcceptanceIndependently()
    {
        XRHandJointSourceSample sample = new(
            LimbSide.Left,
            XRHandJoint.ThumbDistal,
            HasTracker: true,
            HasTrackingData: true,
            RawFlags: 1,
            OrientationValid: true,
            OrientationTracked: false,
            PositionValid: false,
            PositionTracked: false,
            Transform3D.Identity,
            Transform3D.Identity,
            TrackerLocalTransformFinite: true,
            ProductionWorldTransformFinite: true,
            ProductionAccepted: true,
            XRHandJointSourceRejection.None);

        Assert.True(sample.ProductionAccepted);
        Assert.False(sample.PositionValid);
        Assert.False(sample.PositionTracked);
        Assert.Equal(XRHandJointSourceRejection.None, sample.RejectionReason);
    }
}
