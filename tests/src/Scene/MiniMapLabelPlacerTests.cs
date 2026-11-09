using AlleyCat.Scene.MiniMap;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Scene;

/// <summary>
/// Unit coverage for the pure TR-20 placement engine: the fixed, bounded candidate list in order, base-first
/// placement, the three placement checks — no intersection with already-placed label boxes, none with other
/// included markers' shape boxes (own marker's shapes exempt), fully inside the image — priority
/// (override-matched) labels placing first and never omitted, and last-resort omission with the informational
/// diagnostic only after both clearance sets are exhausted.
/// </summary>
public sealed class MiniMapLabelPlacerTests
{
    private const float Step = 32f;

    private const int Depth = 4;

    private static readonly Vector2I _image = new(512, 512);

    private static MiniMapLabelPlacementRequest Request(
        string path,
        Rect2 box,
        bool priority = false,
        int drawOrder = 0,
        string? markerPath = null,
        int collectionIndex = 0) =>
        new(path, markerPath ?? path, priority, drawOrder, markerPath ?? path, collectionIndex, box);

    /// <summary>The fixed, bounded, ordered candidate list: base, vertical stack above, then the k-major diagonal fan (TR-20).</summary>
    [Fact]
    public void CandidateOffsets_FollowTheFixedBoundedListOrder()
    {
        Vector2[] offsets = [.. MiniMapLabelPlacer.CandidateOffsets(Step, Depth)];

        Assert.Equal(21, offsets.Length);
        Assert.Equal(
        [
            new Vector2(0, 0),
            new Vector2(0, -Step),
            new Vector2(0, -2 * Step),
            new Vector2(0, -3 * Step),
            new Vector2(0, -4 * Step),
            // k = 1 fan: up-right, up-left, down-right, down-left.
            new Vector2(Step, -Step),
            new Vector2(-Step, -Step),
            new Vector2(Step, Step),
            new Vector2(-Step, Step),
            // k = 2 fan.
            new Vector2(2 * Step, -2 * Step),
            new Vector2(-2 * Step, -2 * Step),
            new Vector2(2 * Step, 2 * Step),
            new Vector2(-2 * Step, 2 * Step),
            // k = 3 fan.
            new Vector2(3 * Step, -3 * Step),
            new Vector2(-3 * Step, -3 * Step),
            new Vector2(3 * Step, 3 * Step),
            new Vector2(-3 * Step, 3 * Step),
            // k = 4 fan.
            new Vector2(4 * Step, -4 * Step),
            new Vector2(-4 * Step, -4 * Step),
            new Vector2(4 * Step, 4 * Step),
            new Vector2(-4 * Step, 4 * Step),
        ], offsets);
    }

    /// <summary>Labels clear of every other box and inside the image place at their base position first (TR-20).</summary>
    [Fact]
    public void Place_ClearLabelsStayAtBase()
    {
        Rect2 west = new(50, 50, 40, 20);
        Rect2 east = new(300, 300, 40, 20);

        IReadOnlyDictionary<MiniMapLabelPlacementRequest, MiniMapLabelPlacement> outcomes = MiniMapLabelPlacer.Place(
            [Request("West/Caption", west), Request("East/Caption", east)], [], _image, Step, Depth);

        Assert.Equal(new Vector2(0, 0), Assert.IsType<MiniMapLabelPlacement.Placed>(outcomes[Request("West/Caption", west)]).Offset);
        Assert.Equal(new Vector2(0, 0), Assert.IsType<MiniMapLabelPlacement.Placed>(outcomes[Request("East/Caption", east)]).Offset);
    }

    /// <summary>A colliding co-located pair displaces the later label one stack step up — the first step alone separates it (TR-20 defaults).</summary>
    [Fact]
    public void Place_CollidingPairDisplacesUpTheStack()
    {
        Rect2 box = new(236, 246, 40, 20);

        IReadOnlyDictionary<MiniMapLabelPlacementRequest, MiniMapLabelPlacement> outcomes = MiniMapLabelPlacer.Place(
            [Request("First/Caption", box), Request("Second/Caption", box)], [], _image, Step, Depth);

        Assert.Equal(new Vector2(0, 0), Assert.IsType<MiniMapLabelPlacement.Placed>(outcomes[Request("First/Caption", box)]).Offset);
        Assert.Equal(new Vector2(0, -Step), Assert.IsType<MiniMapLabelPlacement.Placed>(outcomes[Request("Second/Caption", box)]).Offset);
    }

    /// <summary>A candidate must lie fully inside the image: a base box poking past the right edge walks to the first inside fan candidate.</summary>
    [Fact]
    public void Place_BoxMustLieFullyInsideTheImage()
    {
        Rect2 edge = new(490, 100, 40, 20);

        IReadOnlyDictionary<MiniMapLabelPlacementRequest, MiniMapLabelPlacement> outcomes = MiniMapLabelPlacer.Place(
            [Request("Edge/Caption", edge)], [], _image, Step, Depth);

        // The stack keeps the box past the right edge; the first fan candidate that pulls it inside is up-left.
        Assert.Equal(new Vector2(-Step, -Step), Assert.IsType<MiniMapLabelPlacement.Placed>(outcomes[Request("Edge/Caption", edge)]).Offset);
    }

    /// <summary>
    /// A base box intersecting another included marker's shape box is not placeable: the label walks the candidate
    /// list to the first position clear of the shape ink — the stack steps that still intersect are skipped, not
    /// accepted (TR-20 shape clearance).
    /// </summary>
    [Fact]
    public void Place_LabelUnderAnotherMarkersShapeDisplacesClearOfIt()
    {
        // The label's base box sits fully inside a neighbouring marker's shape box; every stack step through
        // k = 3 still intersects it, and the k = 4 step lifts the box's end above the shape's top edge.
        Rect2 label = new(236, 246, 40, 20);
        Rect2 shape = new(200, 150, 120, 130);

        IReadOnlyDictionary<MiniMapLabelPlacementRequest, MiniMapLabelPlacement> outcomes = MiniMapLabelPlacer.Place(
            [Request("Milestone/Caption", label)], [new MiniMapShapeInkBox("Planter", shape)], _image, Step, Depth);

        Assert.Equal(new Vector2(0, -4 * Step), Assert.IsType<MiniMapLabelPlacement.Placed>(outcomes[Request("Milestone/Caption", label)]).Offset);
    }

    /// <summary>
    /// The own-marker exemption: a label whose base box intersects only its own marker's shape box stays at its
    /// authored anchor — the anchor is designed against the marker's own shapes (TR-20 shape clearance).
    /// </summary>
    [Fact]
    public void Place_OwnMarkerShapeBoxesAreExempt()
    {
        Rect2 label = new(236, 246, 40, 20);
        Rect2 ownShape = new(200, 200, 120, 120);

        IReadOnlyDictionary<MiniMapLabelPlacementRequest, MiniMapLabelPlacement> outcomes = MiniMapLabelPlacer.Place(
            [Request("Crate/Caption", label, markerPath: "Crate")],
            [new MiniMapShapeInkBox("Crate", ownShape)],
            _image,
            Step,
            Depth);

        Assert.Equal(new Vector2(0, 0), Assert.IsType<MiniMapLabelPlacement.Placed>(outcomes[Request("Crate/Caption", label, markerPath: "Crate")]).Offset);
    }

    /// <summary>
    /// Shape clearance is one of three checks, not a replacement for the label check: a candidate blocked by
    /// already-placed labels keeps walking even when later candidates clear the labels — the first candidate
    /// clearing both sets wins (TR-20).
    /// </summary>
    [Fact]
    public void Place_CandidateMustClearBothLabelsAndOtherMarkersShapes()
    {
        // The plain label's base box intersects the placed early label; the first stack step clears the label but
        // lands in the neighbour's shape ink, so it is skipped and the second stack step — clear of both — wins.
        Rect2 basePosition = new(236, 246, 40, 20);
        Rect2 earlyLabel = new(230, 240, 50, 24);
        Rect2 neighbourShape = new(180, 210, 140, 30);

        IReadOnlyDictionary<MiniMapLabelPlacementRequest, MiniMapLabelPlacement> outcomes = MiniMapLabelPlacer.Place(
            [
                Request("Early/Caption", earlyLabel, drawOrder: 0),
                Request("Late/Caption", basePosition, drawOrder: 1),
            ],
            [new MiniMapShapeInkBox("Planter", neighbourShape)],
            _image,
            Step,
            Depth);

        Assert.Equal(new Vector2(0, 0), Assert.IsType<MiniMapLabelPlacement.Placed>(outcomes[Request("Early/Caption", earlyLabel, drawOrder: 0)]).Offset);
        Assert.Equal(new Vector2(0, -2 * Step), Assert.IsType<MiniMapLabelPlacement.Placed>(outcomes[Request("Late/Caption", basePosition, drawOrder: 1)]).Offset);
    }

    /// <summary>
    /// A priority label honours shape clearance like any other label — it places at its authored anchor only when
    /// that anchor is clear of other labels and of neighbouring shape ink alike, and otherwise displaces; it is
    /// still never omitted, falling back to the base position when the list is exhausted (TR-20).
    /// </summary>
    [Fact]
    public void Place_PriorityLabelDisplacesAroundShapeInkAndIsNeverOmitted()
    {
        // Anchor inside a neighbour's shape: the priority label displaces to the first clear candidate instead of
        // sitting on the shape ink.
        Rect2 label = new(236, 246, 40, 20);
        Rect2 neighbourShape = new(200, 150, 120, 130);

        IReadOnlyDictionary<MiniMapLabelPlacementRequest, MiniMapLabelPlacement> outcomes = MiniMapLabelPlacer.Place(
            [Request("Subject/Identity", label, priority: true)],
            [new MiniMapShapeInkBox("Planter", neighbourShape)],
            _image,
            Step,
            Depth);

        Assert.Equal(new Vector2(0, -4 * Step), Assert.IsType<MiniMapLabelPlacement.Placed>(outcomes[Request("Subject/Identity", label, priority: true)]).Offset);

        // A full-image shape box blocks every displaced candidate: the priority label still places, at its base.
        Rect2 fullImage = new(0, 0, 512, 512);
        IReadOnlyDictionary<MiniMapLabelPlacementRequest, MiniMapLabelPlacement> exhausted = MiniMapLabelPlacer.Place(
            [Request("Subject/Identity", fullImage, priority: true)],
            [new MiniMapShapeInkBox("Planter", fullImage)],
            _image,
            Step,
            Depth);

        MiniMapLabelPlacement.Placed fallback = Assert.IsType<MiniMapLabelPlacement.Placed>(exhausted[Request("Subject/Identity", fullImage, priority: true)]);
        Assert.Equal(new Vector2(0, 0), fallback.Offset);
    }

    /// <summary>
    /// Priority (override-matched) labels place first regardless of input and draw order, and are never omitted;
    /// plain labels then displace around them in draw order with node-path tie-break and collection order.
    /// </summary>
    [Fact]
    public void Place_PriorityFirstThenDrawOrderNodePathAndCollectionIndex()
    {
        Rect2 box = new(236, 246, 40, 20);
        MiniMapLabelPlacementRequest plainB = Request("B/Caption", box, drawOrder: 1);
        MiniMapLabelPlacementRequest plainA = Request("A/Caption", box, drawOrder: 0);
        MiniMapLabelPlacementRequest priority = Request("P/Caption", box, priority: true, drawOrder: 5);
        MiniMapLabelPlacementRequest sameMarkerFirst = Request("M/One", box, markerPath: "M", drawOrder: 2, collectionIndex: 0);
        MiniMapLabelPlacementRequest sameMarkerSecond = Request("M/Two", box, markerPath: "M", drawOrder: 2, collectionIndex: 1);

        IReadOnlyDictionary<MiniMapLabelPlacementRequest, MiniMapLabelPlacement> outcomes = MiniMapLabelPlacer.Place(
            [plainB, sameMarkerSecond, plainA, sameMarkerFirst, priority], [], _image, Step, Depth);

        // Priority walked first despite the highest draw order: it owns the base position.
        Assert.Equal(new Vector2(0, 0), Assert.IsType<MiniMapLabelPlacement.Placed>(outcomes[priority]).Offset);
        // Then A (draw order 0) before B (draw order 1): A takes the first stack step, B the second.
        Assert.Equal(new Vector2(0, -Step), Assert.IsType<MiniMapLabelPlacement.Placed>(outcomes[plainA]).Offset);
        Assert.Equal(new Vector2(0, -2 * Step), Assert.IsType<MiniMapLabelPlacement.Placed>(outcomes[plainB]).Offset);
        // Same marker, authored collection order: index 0 places before index 1 among the remaining candidates.
        Assert.Equal(new Vector2(0, -3 * Step), Assert.IsType<MiniMapLabelPlacement.Placed>(outcomes[sameMarkerFirst]).Offset);
        Assert.Equal(new Vector2(0, -4 * Step), Assert.IsType<MiniMapLabelPlacement.Placed>(outcomes[sameMarkerSecond]).Offset);
    }

    /// <summary>
    /// Last resort: a plain label exhausting the bounded candidate list against the label and shape clearance
    /// checks alike is omitted with the informational LabelOverlaps diagnostic naming its label and marker and
    /// describing both exhausted clearance sets, while a priority label under the same crowding still places at
    /// its base position (TR-20).
    /// </summary>
    [Fact]
    public void Place_ExhaustionOmitsPlainLabelsAndNeverOmitsPriorityLabels()
    {
        // A full-image box: every displaced candidate leaves the image, so only the base position can ever place.
        Rect2 fullImage = new(0, 0, 512, 512);

        IReadOnlyDictionary<MiniMapLabelPlacementRequest, MiniMapLabelPlacement> outcomes = MiniMapLabelPlacer.Place(
            [
                Request("Crowd/Plain", fullImage, markerPath: "Crowd", collectionIndex: 1),
                Request("Crowd/Subject", fullImage, priority: true, markerPath: "Crowd", collectionIndex: 0),
            ],
            [],
            _image,
            Step,
            Depth);

        MiniMapLabelPlacement.Placed subject = Assert.IsType<MiniMapLabelPlacement.Placed>(outcomes[Request("Crowd/Subject", fullImage, priority: true, markerPath: "Crowd", collectionIndex: 0)]);
        Assert.Equal(new Vector2(0, 0), subject.Offset);
        MiniMapLabelPlacement.Omitted omitted = Assert.IsType<MiniMapLabelPlacement.Omitted>(outcomes[Request("Crowd/Plain", fullImage, markerPath: "Crowd", collectionIndex: 1)]);
        Assert.Equal("LabelOverlaps", omitted.Code);
        Assert.Contains("Crowd/Plain", omitted.Message, StringComparison.Ordinal);
        Assert.Contains("Crowd", omitted.Message, StringComparison.Ordinal);
        Assert.Contains("markers' shapes", omitted.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The omission diagnostic fires only after displacement is exhausted against both clearance checks: a label
    /// whose every label-clear candidate also lands inside neighbouring shape ink — and vice versa — keeps
    /// walking the whole bounded list before the omission is legitimate (TR-16/TR-20).
    /// </summary>
    [Fact]
    public void Place_OmissionFiresOnlyAfterBothClearanceSetsAreExhausted()
    {
        // The stack and up-fan candidates are covered by a shape box wide enough to catch every reachable x; the
        // down-fan candidates leave the image for a base anchored near its foot: no candidate clears both sets, so
        // the plain label omits.
        Rect2 edgeLabel = new(236, 490, 40, 20);
        Rect2 wideShape = new(100, 320, 310, 190);

        IReadOnlyDictionary<MiniMapLabelPlacementRequest, MiniMapLabelPlacement> outcomes = MiniMapLabelPlacer.Place(
            [Request("Hemmed/Caption", edgeLabel)], [new MiniMapShapeInkBox("Planter", wideShape)], _image, Step, Depth);

        MiniMapLabelPlacement.Omitted omitted = Assert.IsType<MiniMapLabelPlacement.Omitted>(outcomes[Request("Hemmed/Caption", edgeLabel)]);
        Assert.Equal("LabelOverlaps", omitted.Code);

        // The same crowding with the shape box belonging to the label's own marker changes the verdict: the stack
        // candidates stay clear (own shapes exempt), so the label places instead of omitting.
        IReadOnlyDictionary<MiniMapLabelPlacementRequest, MiniMapLabelPlacement> exempt = MiniMapLabelPlacer.Place(
            [Request("Hemmed/Caption", edgeLabel, markerPath: "Hemmed")],
            [new MiniMapShapeInkBox("Hemmed", wideShape)],
            _image,
            Step,
            Depth);

        MiniMapLabelPlacement.Placed exemptOutcome =
            Assert.IsType<MiniMapLabelPlacement.Placed>(exempt[Request("Hemmed/Caption", edgeLabel, markerPath: "Hemmed")]);
        Assert.Equal(new Vector2(0, 0), exemptOutcome.Offset);
    }

    /// <summary>Placement is a pure function of its inputs: identical requests place identically (TR-20 determinism).</summary>
    [Fact]
    public void Place_IsDeterministicAcrossRepeatedCalls()
    {
        Rect2 box = new(236, 246, 40, 20);
        MiniMapLabelPlacementRequest[] requests = [Request("A/Caption", box), Request("B/Caption", box), Request("C/Caption", box, priority: true)];
        MiniMapShapeInkBox[] shapes = [new("Planter", new Rect2(200, 200, 120, 120))];

        IReadOnlyDictionary<MiniMapLabelPlacementRequest, MiniMapLabelPlacement> first = MiniMapLabelPlacer.Place(requests, shapes, _image, Step, Depth);
        IReadOnlyDictionary<MiniMapLabelPlacementRequest, MiniMapLabelPlacement> second = MiniMapLabelPlacer.Place(requests, shapes, _image, Step, Depth);

        Assert.Equal(
            first.Select(entry => (entry.Key.PrimitivePath, entry.Value)),
            second.Select(entry => (entry.Key.PrimitivePath, entry.Value)));
    }
}
