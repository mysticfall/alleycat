using Godot;

namespace AlleyCat.Scene.MiniMap;

/// <summary>
/// One marker label's TR-20 placement request: the axis-aligned pixel bounding box of its rendered glyph paths
/// at the authored base position, plus the ordering keys and priority class the placement rule needs.
/// </summary>
/// <param name="PrimitivePath">Label primitive node path, scope-relative (TR-16); unique within a render.</param>
/// <param name="MarkerPath">Owning marker node path, scope-relative (TR-16).</param>
/// <param name="Priority">True when the owning marker matched a TR-19 override: the label places first and is
/// never omitted.</param>
/// <param name="DrawOrder">Authored draw-order ordinal of the owning marker (TR-12).</param>
/// <param name="MarkerNodePath">Owning marker node path used as the TR-12 ordinal tie-break key.</param>
/// <param name="CollectionIndex">Index of the label's primitive in the marker's authored collection order.</param>
/// <param name="BaseBox">The label's glyph-path bounding box at its authored anchor, in image pixels.</param>
internal sealed record MiniMapLabelPlacementRequest(
    string PrimitivePath,
    string MarkerPath,
    bool Priority,
    int DrawOrder,
    string MarkerNodePath,
    int CollectionIndex,
    Rect2 BaseBox);

/// <summary>
/// TR-20 placement outcome for one marker label: a displacement offset from the base position, or an omission
/// whose diagnostic the composition reports.
/// </summary>
internal abstract record MiniMapLabelPlacement
{
    /// <summary>The label renders at its base position plus this offset; <c>Vector2.Zero</c> is the base itself.</summary>
    /// <param name="Offset">Displacement offset in image pixels.</param>
    internal sealed record Placed(Vector2 Offset) : MiniMapLabelPlacement;

    /// <summary>The label is omitted for this render; the carried code and message form its diagnostic.</summary>
    /// <param name="Code">Diagnostic code (<c>LabelOverlaps</c>, or <c>UnsupportedGlyph</c> for text the chain rejects).</param>
    /// <param name="Message">Diagnostic message.</param>
    internal sealed record Omitted(string Code, string Message) : MiniMapLabelPlacement;
}

/// <summary>
/// One included marker's projected shape-ink boxes for TR-20 shape clearance: the axis-aligned pixel bounding
/// box of each projected shape primitive's footprint — the same footprint boxes that feed TR-10's viewport
/// extent — keyed by the owning marker's scope-relative path so a label can exempt its own marker's shapes.
/// </summary>
/// <param name="MarkerPath">Owning marker node path, scope-relative (TR-16).</param>
/// <param name="Box">Axis-aligned pixel bounding box of one projected shape primitive.</param>
internal sealed record MiniMapShapeInkBox(string MarkerPath, Rect2 Box);

/// <summary>
/// Deterministic single-pass TR-20 label placement (SCN-002 UR-9/TR-20): a pure function of the participant
/// requests, the shape-ink boxes, the image bounds and the service tuning constants — no scene access, no
/// engine services — so identical inputs always produce identical placements. Labels place in the rule's order
/// at the first candidate position whose box intersects no already-placed marker label's box, intersects no
/// other included marker's shape box (the label's own marker's shapes are exempt), and lies fully inside the
/// placement bounds — the plot area, which excludes the grid-label gutters; a priority label that exhausts the list places at its base position regardless, a plain label is
/// omitted.
/// </summary>
internal static class MiniMapLabelPlacer
{
    /// <summary>
    /// The fixed, bounded, ordered candidate list (TR-20): the base position; the vertical stack above it,
    /// <c>(0, −k·S)</c> for k = 1…D; then the diagonal fan, for k = 1…D the four offsets <c>(±k·S, ±k·S)</c> in
    /// the fixed order up-right, up-left, down-right, down-left. Offsets are in image pixels: +x right, +y down.
    /// </summary>
    /// <param name="step">Candidate step S in image pixels.</param>
    /// <param name="depth">Stack/fan depth D.</param>
    /// <returns>Candidate offsets in list order.</returns>
    internal static IEnumerable<Vector2> CandidateOffsets(float step, int depth)
    {
        yield return Vector2.Zero;
        for (int k = 1; k <= depth; k++)
        {
            yield return new Vector2(0, -k * step);
        }

        for (int k = 1; k <= depth; k++)
        {
            float spread = k * step;
            yield return new Vector2(spread, -spread);
            yield return new Vector2(-spread, -spread);
            yield return new Vector2(spread, spread);
            yield return new Vector2(-spread, spread);
        }
    }

    /// <summary>
    /// Places every request in the TR-20 order: priority labels first, then ascending draw-order ordinal
    /// tie-broken by marker node path, then authored collection order among one marker's own labels. Within that
    /// order each label takes the first candidate that clears all three checks — no intersection with an
    /// already-placed marker label's box, none with another included marker's shape box (the label's own
    /// marker's shapes are exempt, since the authored anchor is designed against them), and fully inside the
    /// image; a priority label that exhausts the list still places at its base position, a plain label is
    /// omitted with <c>LabelOverlaps</c>.
    /// </summary>
    /// <param name="requests">All participating label placement requests, in any order.</param>
    /// <param name="shapeInkBoxes">Projected shape-footprint pixel boxes of every included marker, keyed by the
    /// owning marker path; grid lines and grid labels never participate (TR-14).</param>
    /// <param name="imageSize">Output image size in pixels; a box must lie fully inside it.</param>
    /// <param name="step">Candidate step S in image pixels.</param>
    /// <param name="depth">Stack/fan depth D.</param>
    /// <returns>Placement outcome per request.</returns>
    internal static IReadOnlyDictionary<MiniMapLabelPlacementRequest, MiniMapLabelPlacement> Place(
        IEnumerable<MiniMapLabelPlacementRequest> requests,
        IReadOnlyList<MiniMapShapeInkBox> shapeInkBoxes,
        Vector2I imageSize,
        float step,
        int depth)
        => Place(requests, shapeInkBoxes, new Rect2(Vector2.Zero, imageSize), step, depth);

    /// <summary>
    /// Places every request as <see cref="Place(IEnumerable{MiniMapLabelPlacementRequest}, IReadOnlyList{MiniMapShapeInkBox}, Vector2I, float, int)" />
    /// does, with an explicit placement rectangle: the renderer passes the plot area so marker labels never enter the
    /// grid-label gutters.
    /// </summary>
    /// <param name="requests">All participating label placement requests, in any order.</param>
    /// <param name="shapeInkBoxes">Projected shape-footprint pixel boxes of every included marker.</param>
    /// <param name="bounds">Placement rectangle in image pixels; a box must lie fully inside it.</param>
    /// <param name="step">Candidate step S in image pixels.</param>
    /// <param name="depth">Stack/fan depth D.</param>
    /// <returns>Placement outcome per request.</returns>
    internal static IReadOnlyDictionary<MiniMapLabelPlacementRequest, MiniMapLabelPlacement> Place(
        IEnumerable<MiniMapLabelPlacementRequest> requests,
        IReadOnlyList<MiniMapShapeInkBox> shapeInkBoxes,
        Rect2 bounds,
        float step,
        int depth)
    {
        List<MiniMapLabelPlacementRequest> ordered = [.. requests
            .OrderBy(request => request.Priority ? 0 : 1)
            .ThenBy(request => request.DrawOrder)
            .ThenBy(request => request.MarkerNodePath, StringComparer.Ordinal)
            .ThenBy(request => request.CollectionIndex)];

        Dictionary<MiniMapLabelPlacementRequest, MiniMapLabelPlacement> outcomes = [];
        List<Rect2> placedBoxes = [];
        foreach (MiniMapLabelPlacementRequest request in ordered)
        {
            MiniMapLabelPlacement? outcome = null;
            foreach (Vector2 offset in CandidateOffsets(step, depth))
            {
                Rect2 candidate = new(request.BaseBox.Position + offset, request.BaseBox.Size);
                if (IsInside(candidate, bounds)
                    && placedBoxes.All(box => !box.Intersects(candidate))
                    && shapeInkBoxes.All(ink => ink.MarkerPath == request.MarkerPath || !ink.Box.Intersects(candidate)))
                {
                    placedBoxes.Add(candidate);
                    outcome = new MiniMapLabelPlacement.Placed(offset);
                    break;
                }
            }

            if (outcome is null)
            {
                if (request.Priority)
                {
                    // An overridden label is never omitted (TR-20): it places at its base position, where only
                    // other overridden labels can already sit; later labels displace around it.
                    placedBoxes.Add(request.BaseBox);
                    outcome = new MiniMapLabelPlacement.Placed(Vector2.Zero);
                }
                else
                {
                    outcome = new MiniMapLabelPlacement.Omitted("LabelOverlaps",
                        $"Marker label '{request.PrimitivePath}' on marker '{request.MarkerPath}' found no position clear of the "
                            + $"other placed labels and the other markers' shapes inside the image within the bounded displacement "
                            + "candidates; omitted for this render. Extreme crowding is normal transient scene state.");
                }
            }

            outcomes[request] = outcome;
        }

        return outcomes;
    }

    private static bool IsInside(Rect2 box, Rect2 bounds) =>
        box.Position.X >= bounds.Position.X && box.Position.Y >= bounds.Position.Y
            && box.End.X <= bounds.End.X && box.End.Y <= bounds.End.Y;
}
