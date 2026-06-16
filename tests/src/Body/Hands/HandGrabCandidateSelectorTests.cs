using AlleyCat.Body.Hands;
using AlleyCat.Core;
using AlleyCat.Interaction;
using AlleyCat.Rigging;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Body.Hands;

/// <summary>
/// Unit coverage for deterministic BODY-001/INTR-002 hand grab selection.
/// </summary>
public sealed class HandGrabCandidateSelectorTests
{
    /// <summary>
    /// Verifies the closest candidate inside the discovery range is selected.
    /// </summary>
    [Fact]
    public void Select_ChoosesClosestCandidateWithinDiscoveryRange()
    {
        FakeGrabbable farther = new(new Vector3(0.2f, 0.0f, 0.0f));
        FakeGrabbable closer = new(new Vector3(0.1f, 0.0f, 0.0f));

        HandGrabSelection? selection = HandGrabCandidateSelector.Select(
            [farther, closer],
            LimbSide.Right,
            Transform3D.Identity,
            0.3f);

        Assert.NotNull(selection);
        Assert.Same(closer, selection.Grabbable);
    }

    /// <summary>
    /// Verifies equal-distance candidates keep discovery order as tie-breaker.
    /// </summary>
    [Fact]
    public void Select_EqualDistancesKeepDiscoveryOrder()
    {
        FakeGrabbable first = new(new Vector3(0.1f, 0.0f, 0.0f));
        FakeGrabbable second = new(new Vector3(-0.1f, 0.0f, 0.0f));

        HandGrabSelection? selection = HandGrabCandidateSelector.Select(
            [first, second],
            LimbSide.Left,
            Transform3D.Identity,
            0.3f);

        Assert.NotNull(selection);
        Assert.Same(first, selection.Grabbable);
    }

    /// <summary>
    /// Verifies candidates outside the hand discovery range are rejected.
    /// </summary>
    [Fact]
    public void Select_RejectsCandidatesOutsideDiscoveryRange()
    {
        FakeGrabbable candidate = new(new Vector3(0.31f, 0.0f, 0.0f), 0.31f);

        HandGrabSelection? selection = HandGrabCandidateSelector.Select(
            [candidate],
            LimbSide.Left,
            Transform3D.Identity,
            0.3f);

        Assert.Null(selection);
    }

    /// <summary>
    /// Verifies discovery range filtering uses acquisition distance rather than the target IK pose.
    /// </summary>
    [Fact]
    public void Select_HandTargetOutsideDiscoveryRangeButAcquisitionInRange_SelectsCandidate()
    {
        FakeGrabbable candidate = new(new Vector3(2.0f, 0.0f, 0.0f), 0.1f);

        HandGrabSelection? selection = HandGrabCandidateSelector.Select(
            [candidate],
            LimbSide.Right,
            Transform3D.Identity,
            0.3f);

        Assert.NotNull(selection);
        Assert.Same(candidate, selection.Grabbable);
    }

    /// <summary>
    /// Verifies ranking ignores misleading hand-target distances and uses acquisition distance.
    /// </summary>
    [Fact]
    public void Select_MisleadingHandTargetDistances_ChoosesNearestAcquisitionDistance()
    {
        FakeGrabbable misleadingTargetNear = new(new Vector3(0.01f, 0.0f, 0.0f), 0.2f);
        FakeGrabbable misleadingTargetFar = new(new Vector3(2.0f, 0.0f, 0.0f), 0.05f);

        HandGrabSelection? selection = HandGrabCandidateSelector.Select(
            [misleadingTargetNear, misleadingTargetFar],
            LimbSide.Left,
            Transform3D.Identity,
            0.3f);

        Assert.NotNull(selection);
        Assert.Same(misleadingTargetFar, selection.Grabbable);
    }

    /// <summary>
    /// Verifies BODY-001 exposes grab state and grab/release actions on IHand.
    /// </summary>
    [Fact]
    public void IHandContract_ExposesGrabStateAndActions()
    {
        Assert.NotNull(typeof(IHand).GetProperty(nameof(IHand.Side)));
        Assert.NotNull(typeof(IHand).GetProperty(nameof(IHand.CurrentGrabbed)));
        Assert.NotNull(typeof(IHand).GetMethod(nameof(IHand.Grab), Type.EmptyTypes));
        Assert.NotNull(typeof(IHand).GetMethod(nameof(IHand.Release), Type.EmptyTypes));

        Assert.Null(typeof(IHand).GetProperty("Pose"));
        Assert.Null(typeof(IHand).GetProperty("PoseWeight"));
        Assert.Null(typeof(IHand).GetProperty("CurrentPose"));
        Assert.Null(typeof(IHand).GetMethod("SetPose"));
        Assert.Null(typeof(IHand).GetMethod("ClearPose"));
    }

    private sealed class FakeGrabbable(Vector3 targetOrigin, float? acquisitionDistance = null) : IGrabbable
    {
        private readonly FakeGrabPoint _grabPoint = new(targetOrigin, acquisitionDistance);

        public IReadOnlyList<IComponent> Components => [_grabPoint];

        public GrabbableMobility Mobility => GrabbableMobility.Movable;

        public bool Grab(GrabPointCandidate grabPoint) => ReferenceEquals(grabPoint.Source, _grabPoint);
    }

    private sealed class FakeGrabPoint(Vector3 targetOrigin, float? acquisitionDistance) : IGrabPoint
    {
        public GrabPointCandidate? GetGrabPoint(LimbSide handSide, Transform3D handTransform)
            => new(
                this,
                new Transform3D(Basis.Identity, targetOrigin),
                NullAnimationForPlainUnitTestHost(),
                handSide,
                handTransform,
                new Transform3D(Basis.Identity, targetOrigin),
                Vector3.Zero,
                Vector3.Zero,
                acquisitionDistance ?? handTransform.Origin.DistanceTo(targetOrigin));
    }

    private static Animation NullAnimationForPlainUnitTestHost() => null!;
}
