using AlleyCat.Core;
using AlleyCat.Interaction;
using AlleyCat.Interaction.Hands;
using AlleyCat.Rigging;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Interaction.Hands;

/// <summary>
/// Plain-host unit coverage for deterministic BODY-001/INTR-002 hand grab selection over paths that precede
/// candidate content validation. Nearest-candidate ranking, discovery-order tie-breaking, range filtering,
/// and mixed held/available eligibility all admit candidates through the shared instance-exact reference
/// validation (<see cref="GrabPointCandidate.TryGetValidatedReference" />), which requires a real Godot
/// <see cref="Animation" /> resource, so that coverage is Godot-running in
/// <c>AlleyCat.IntegrationTests.Interaction.Hands.HandGrabCandidateSelectorIntegrationTests</c>.
/// </summary>
public sealed class HandGrabCandidateSelectorTests
{
    /// <summary>
    /// Verifies a held grabbable is excluded from selection (INTR-001 TR18; INTR-002 TR3) before any candidate
    /// content work, so a scene holding every discoverable grabbable yields no selection. Mixed held/available
    /// ranking with real validated content is covered by the integration regressions: the plain dotnet host
    /// cannot construct the <see cref="Animation" /> resources the shared content validation requires.
    /// </summary>
    [Fact]
    public void Select_AllGrabbablesHeld_ReturnsNull()
    {
        FakeGrabbable held = new()
        {
            IsGrabbed = true,
        };

        HandGrabSelection? selection = HandGrabCandidateSelector.Select(
            [held],
            LimbSide.Left,
            Transform3D.Identity,
            0.3f);

        Assert.Null(selection);
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

    private sealed class FakeGrabbable : IGrabbable
    {
        public IReadOnlyList<IComponent> Components => [];

        public GrabbableMobility Mobility => GrabbableMobility.Movable;

        public bool IsGrabbed
        {
            get; set;
        }

        public bool Grab(GrabPointCandidate grabPoint) => false;
    }
}
