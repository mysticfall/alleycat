using AlleyCat.Body;
using AlleyCat.Component;
using AlleyCat.Interaction;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Interaction;

/// <summary>
/// Unit coverage for the INTR-001 grabbable holder trait and grab-point component contract.
/// </summary>
public sealed class GrabbableTraitTests
{
    /// <summary>
    /// The default holder trait query delegates to owned grab points in holder-defined order.
    /// </summary>
    [Fact]
    public void GetGrabPoint_DelegatesThroughGrabPointsInHolderOrder()
    {
        var calls = new List<string>();
        var first = new FakeGrabPoint("first", calls, null);
        var second = new FakeGrabPoint("second", calls, CreateCandidate);
        IGrabbable holder = new FakeGrabbable(new NonGrabComponent(), first, second);

        GrabPointCandidate? candidate = holder.GetGrabPoint(LimbSide.Left, Transform3D.Identity);

        Assert.NotNull(candidate);
        Assert.Same(second, candidate.Source);
        Assert.Equal(["first", "second"], calls);
    }

    /// <summary>
    /// The default holder trait query returns null when no owned grab point yields an eligible candidate.
    /// </summary>
    [Fact]
    public void GetGrabPoint_NoEligibleGrabPoint_ReturnsNull()
    {
        var calls = new List<string>();
        IGrabbable holder = new FakeGrabbable(
            new FakeGrabPoint("first", calls, null),
            new FakeGrabPoint("second", calls, null));

        GrabPointCandidate? candidate = holder.GetGrabPoint(LimbSide.Right, Transform3D.Identity);

        Assert.Null(candidate);
        Assert.Equal(["first", "second"], calls);
    }

    /// <summary>
    /// Multiple grab-point components are supported, with the closest eligible component selected deterministically.
    /// </summary>
    [Fact]
    public void GetGrabPoint_MultipleEligibleGrabPoints_ReturnsClosestCandidate()
    {
        var calls = new List<string>();
        var farther = new FakeGrabPoint("farther", calls, source => CreateCandidate(source, new Vector3(5.0f, 0.0f, 0.0f)));
        var closer = new FakeGrabPoint("closer", calls, source => CreateCandidate(source, new Vector3(2.0f, 0.0f, 0.0f)));
        IGrabbable holder = new FakeGrabbable(farther, new NonGrabComponent(), closer);

        GrabPointCandidate? candidate = holder.GetGrabPoint(LimbSide.Left, Transform3D.Identity);

        Assert.NotNull(candidate);
        Assert.Same(closer, candidate.Source);
        Assert.Equal(["farther", "closer"], calls);
    }

    /// <summary>
    /// Equal-distance grab-point candidates retain holder order as the deterministic tie-breaker.
    /// </summary>
    [Fact]
    public void GetGrabPoint_EqualDistanceCandidates_UsesHolderOrderTieBreaker()
    {
        var calls = new List<string>();
        var first = new FakeGrabPoint("first", calls, source => CreateCandidate(source, new Vector3(1.0f, 0.0f, 0.0f)));
        var second = new FakeGrabPoint("second", calls, source => CreateCandidate(source, new Vector3(-1.0f, 0.0f, 0.0f)));
        IGrabbable holder = new FakeGrabbable(first, new NonGrabComponent(), second);

        GrabPointCandidate? candidate = holder.GetGrabPoint(LimbSide.Left, Transform3D.Identity);

        Assert.NotNull(candidate);
        Assert.Same(first, candidate.Source);
        Assert.Equal(["first", "second"], calls);
    }

    /// <summary>
    /// Grab-point candidates carry the component reference that produced them for execution-time ownership checks.
    /// </summary>
    [Fact]
    public void GrabPointCandidate_CarriesSourceComponentReference()
    {
        var source = new FakeGrabPoint("source", [], CreateCandidate);

        var candidate = new GrabPointCandidate(source, Transform3D.Identity, NullAnimationForPlainUnitTestHost());

        Assert.Same(source, candidate.Source);
        Assert.Null(candidate.Animation);
    }

    /// <summary>
    /// INTR-001 contracts remain compatible with CORE-003 holder and component query constraints.
    /// </summary>
    [Fact]
    public void Contracts_AreCompatibleWithComponentSystem()
    {
        Assert.True(typeof(IComponentHolder).IsAssignableFrom(typeof(IGrabbable)));
        Assert.True(typeof(IComponent).IsAssignableFrom(typeof(IGrabPoint)));
    }

    private static GrabPointCandidate CreateCandidate(IGrabPoint source) =>
        CreateCandidate(source, Vector3.Zero);

    private static GrabPointCandidate CreateCandidate(IGrabPoint source, Vector3 handTargetOrigin) =>
        new(source, TransformAt(handTargetOrigin), NullAnimationForPlainUnitTestHost());

    private static Transform3D TransformAt(Vector3 origin) => new(Basis.Identity, origin);

    /// <summary>
    /// Godot resources cannot be constructed safely in the plain dotnet test host, so unit coverage only verifies that
    /// the non-nullable animation property participates in the candidate contract without instantiating the runtime type.
    /// Runtime-backed tests should use a real <see cref="Godot.Animation" /> resource for value round-trip coverage.
    /// </summary>
    private static Godot.Animation NullAnimationForPlainUnitTestHost() => null!;

    private sealed class FakeGrabbable(params IComponent[] components) : IGrabbable
    {
        public IReadOnlyList<IComponent> Components { get; } = components;

        public bool Grab(GrabPointCandidate grabPoint) => Components.Contains(grabPoint.Source);
    }

    private sealed class FakeGrabPoint(
        string name,
        List<string> calls,
        Func<IGrabPoint, GrabPointCandidate?>? candidateFactory) : IGrabPoint
    {
        public GrabPointCandidate? GetGrabPoint(LimbSide handSide, Transform3D handTransform)
        {
            calls.Add(name);

            return candidateFactory?.Invoke(this);
        }
    }

    private sealed class NonGrabComponent : IComponent
    {
    }
}
