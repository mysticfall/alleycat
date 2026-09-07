using AlleyCat.Core;
using AlleyCat.Interaction;
using AlleyCat.Interaction.Hands;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Interaction.Hands;

/// <summary>
/// Godot-running coverage for deterministic BODY-001/INTR-002 hand grab selection over real validated
/// candidate content. The shared selection boundary validates an instance-exact authored reference for every
/// candidate (<see cref="GrabPointCandidate.TryGetValidatedReference" />), which requires a real
/// <see cref="Animation" /> resource the plain dotnet unit host cannot construct, so nearest-candidate ranking,
/// discovery-order tie-breaking, range filtering, and mixed held/available eligibility are verified here
/// against the shipped grab-pose reference.
/// </summary>
public sealed class HandGrabCandidateSelectorIntegrationTests
{
    private const string ValidGrabPoseAnimationPath =
        "res://assets/characters/reference/female/animations/Grab-ball-40.tres";

    /// <summary>
    /// Verifies the closest candidate inside the discovery range is selected.
    /// </summary>
    [Headless]
    [Fact]
    public void Select_ChoosesClosestCandidateWithinDiscoveryRange()
    {
        Animation grabPose = LoadValidGrabPoseAnimation();
        FakeGrabbable farther = new(grabPose, new Vector3(0.2f, 0.0f, 0.0f));
        FakeGrabbable closer = new(grabPose, new Vector3(0.1f, 0.0f, 0.0f));

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
    [Headless]
    [Fact]
    public void Select_EqualDistancesKeepDiscoveryOrder()
    {
        Animation grabPose = LoadValidGrabPoseAnimation();
        FakeGrabbable first = new(grabPose, new Vector3(0.1f, 0.0f, 0.0f));
        FakeGrabbable second = new(grabPose, new Vector3(-0.1f, 0.0f, 0.0f));

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
    [Headless]
    [Fact]
    public void Select_RejectsCandidatesOutsideDiscoveryRange()
    {
        Animation grabPose = LoadValidGrabPoseAnimation();
        FakeGrabbable candidate = new(grabPose, new Vector3(0.31f, 0.0f, 0.0f), 0.31f);

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
    [Headless]
    [Fact]
    public void Select_HandTargetOutsideDiscoveryRangeButAcquisitionInRange_SelectsCandidate()
    {
        Animation grabPose = LoadValidGrabPoseAnimation();
        FakeGrabbable candidate = new(grabPose, new Vector3(2.0f, 0.0f, 0.0f), 0.1f);

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
    [Headless]
    [Fact]
    public void Select_MisleadingHandTargetDistances_ChoosesNearestAcquisitionDistance()
    {
        Animation grabPose = LoadValidGrabPoseAnimation();
        FakeGrabbable misleadingTargetNear = new(grabPose, new Vector3(0.01f, 0.0f, 0.0f), 0.2f);
        FakeGrabbable misleadingTargetFar = new(grabPose, new Vector3(2.0f, 0.0f, 0.0f), 0.05f);

        HandGrabSelection? selection = HandGrabCandidateSelector.Select(
            [misleadingTargetNear, misleadingTargetFar],
            LimbSide.Left,
            Transform3D.Identity,
            0.3f);

        Assert.NotNull(selection);
        Assert.Same(misleadingTargetFar, selection.Grabbable);
    }

    /// <summary>
    /// Verifies a nearer held grabbable neither wins nor obscures a farther available candidate
    /// (INTR-001 TR18; INTR-002 TR3) once candidate content has passed shared validation.
    /// </summary>
    [Headless]
    [Fact]
    public void Select_MixedHeldAndAvailableGrabbables_RanksOnlyAvailableCandidates()
    {
        Animation grabPose = LoadValidGrabPoseAnimation();
        FakeGrabbable heldNearer = new(grabPose, new Vector3(0.05f, 0.0f, 0.0f))
        {
            IsGrabbed = true,
        };
        FakeGrabbable availableFarther = new(grabPose, new Vector3(0.2f, 0.0f, 0.0f));

        HandGrabSelection? selection = HandGrabCandidateSelector.Select(
            [heldNearer, availableFarther],
            LimbSide.Right,
            Transform3D.Identity,
            0.3f);

        Assert.NotNull(selection);
        Assert.Same(availableFarther, selection.Grabbable);
    }

    private static Animation LoadValidGrabPoseAnimation()
        => ResourceLoader.Load<Animation>(ValidGrabPoseAnimationPath)
            ?? throw new InvalidOperationException($"Could not load valid grab pose '{ValidGrabPoseAnimationPath}'.");

    private sealed class FakeGrabbable(Animation animation, Vector3 targetOrigin, float? acquisitionDistance = null)
        : IGrabbable
    {
        private readonly FakeGrabPoint _grabPoint = new(animation, targetOrigin, acquisitionDistance);

        public IReadOnlyList<IComponent> Components => [_grabPoint];

        public GrabbableMobility Mobility => GrabbableMobility.Movable;

        public bool IsGrabbed
        {
            get; set;
        }

        public bool Grab(GrabPointCandidate grabPoint) => ReferenceEquals(grabPoint.Source, _grabPoint);
    }

    private sealed class FakeGrabPoint(Animation animation, Vector3 targetOrigin, float? acquisitionDistance)
        : IGrabPoint
    {
        public GrabPointCandidate? GetGrabPoint(LimbSide handSide, Transform3D handTransform)
            => new(
                this,
                new Transform3D(Basis.Identity, targetOrigin),
                animation,
                handSide,
                handTransform,
                new Transform3D(Basis.Identity, targetOrigin),
                Vector3.Zero,
                Vector3.Zero,
                acquisitionDistance ?? handTransform.Origin.DistanceTo(targetOrigin));
    }
}
