# Spatial Minimap Demo (SCN-002)

Demonstration scene for the redesigned spatial minimap: one unified marker model discovered by scene-group
membership, composed by a standalone renderer service. Nothing here is minimap-specific game code — every entity on
the map is an ordinary `MiniMapMarker` parented wherever the scene author put it.

## Run The Demo

```bash
godot-mono --path game res://tests/spatial_minimap/demo.tscn
```

The driver resolves `MiniMapRenderer` through CORE-004 DI, renders four requests, saves each SVG/PNG pair under
`game/temp/minimap-redesign/`, mirrors the latest image on the demo overlay, prints a `MINIMAP_DEMO_RENDER` line per
request, and quits. It also runs clean headless (`--headless --xr-mode off`).

## What The Scene Shows

- **Every primitive type** — rectangle walls (`Walls`), circle basins (`Basin`), heading triangles (`Beacon`,
  `Alice`), and both label flavours (`Crate` configured text; `AliceMarker`/`MinsuMarker` identity `FullId`).
- **A live `ICharacter` carrying a marker** — `Alice` is an ordinary bare `Character` node; `AliceMarker` is an
  authored child. No character subclass, no character-side changes, no registration API. The driver translates and
  rotates the character between renders: the heading triangle rotates with her, the identity label follows, and the
  map orientation stays fixed.
- **Distance filtering** — `FarBeacon` at `(60, 40)` appears in the whole-coverage render and is excluded from the
  8 m close-up request.
- **Auto-scale with margin** — the close-up viewport is the bounding box of the included markers plus the requested
  centre, grown by the margin and widened to the image aspect; compare its scale against the whole-coverage render.
- **Adaptive grid at two scales** — 5 m steps on the whole-coverage render, 2 m steps on the close-ups, anchored at
  world zero with negative metre labels.
- **Per-request marker style overrides (TR-19)** — the fourth render repeats the moved close-up request with
  `MarkerOverrides { "char:alice": gold + "You" }`: Alice's whole marker — heading triangle and label glyphs —
  renders gold with the label "You", while every other marker, the geometry, the framing, the draw order and the
  grid are those of the plain request. The styling lives in the request only; the scene is never edited, and the
  three earlier plain renders are unaffected.
- **Unsupported-glyph diagnostic (TR-7/TR-15)** — `Minsu` carries the Korean `FullId` `char:민수`, which bundled
  Inter does not cover: the render succeeds, the label is omitted, and an `UnsupportedGlyph` diagnostic is reported.
  This is legitimate CORE-009 identity and must not be transliterated.
- **Editor note** — in the editor preview, identity labels hosted under non-`[Tool]` ancestors (the production
  `Character`) report `MissingIdentity`, because those scripts do not instantiate in the editor. The same scene at
  runtime resolves them. This is the TR-16 identity edge contract working as specified, not a wiring error.

## Authoring

- Add a `MiniMapMarker` (Node3D) wherever the entity lives — under a character, a prop, or the scene root. It
  self-registers into the `minimap_markers` group when it enters the scene tree (`_EnterTree`).
- Populate its ordered `Primitives` array with `MiniMapRectangle` / `MiniMapCircle` / `MiniMapTriangle` /
  `MiniMapLabel` nodes (typically children; their authored local positions are their offsets from the marker origin).
  Duplicate references fail the render loudly.
- Set `DrawOrder` to control paint order (ascending, tie-broken by node path); later markers cover earlier ones.
- Geometry is authored in local X/Z: yaw, tilt, non-uniform scale and negative (mirroring) scale all project; only
  non-finite transforms are rejected. Triangle apex points along local −Z, so a triangle under a rotating character
  shows its heading.
- Labels: `Flavour = Configured` uses the `Text` property; `Flavour = Identity` displays the canonical `FullId` of
  the nearest `IIdentifiable` ancestor of the marker (resolved at capture time). Text size is in image pixels, so
  labels stay legible at every zoom.

## Requesting Renders

```csharp
MiniMapRenderer renderer = Game.Instance.GetService<MiniMapRenderer>()
    ?? throw new InvalidOperationException("MiniMapRenderer is not registered.");

// Whole authored coverage, unbounded inclusion.
MiniMapRenderResult overview = renderer.RenderCoverage(new MiniMapRenderSettings(Width: 1280, Height: 800));

// Around a centre with an inclusion distance; markers beyond it are excluded.
MiniMapRenderResult local = renderer.Render(
    new Vector2(character.GlobalPosition.X, character.GlobalPosition.Z),
    new MiniMapRenderSettings(MaximumDistance: 8f, Width: 1280, Height: 800));

// Caller-owned outputs; rendering itself performs no file writes or scene mutation.
File.WriteAllText("map.svg", local.Svg);
_ = local.Image.SavePng("map.png");
```

Per-request settings (all optional, overriding documented service defaults): `MaximumDistance` (default 100 m,
`float.PositiveInfinity` includes everything), `Width`/`Height` (default 1024×1024), `MarginMetres` (default 2),
`GridTextSizePixels` (default 16) and `GridMinimumSpacingPixels` (default 64). The 8 m minimum world extent is a
documented fixed service guard, not a setting. Failed renders return `Status = Failed` with diagnostics naming the
marker node path and primitive; successful renders may still carry non-fatal diagnostics.

### Per-Request Marker Style Overrides (TR-19)

Any render request that accepts settings can emphasise specific characters at render time — no scene edits, no
character or identity API beyond the `FullId` the renderer already resolves:

```csharp
MiniMapRenderSettings emphasise = new(
    MaximumDistance: 8f,
    Width: 1280,
    Height: 800,
    MarkerOverrides: new Dictionary<string, MiniMapMarkerOverride>
    {
        ["char:alice"] = new(Colour: new Color("d4af37"), LabelText: "You"),
    });
MiniMapRenderResult emphasised = renderer.Render(centre, emphasise);
```

- **Matching** — a marker matches when its resolved `FullId` exactly equals a key (case-sensitive). Only markers
  with an instantiated `IIdentifiable` ancestor can match; every matching marker in the render is styled, so
  identities shared by several markers restyle all of them. Overrides apply to the automatic centre-based,
  explicit-viewport and coverage renders; the editor preview passes none.
- **Colour** — replaces the fill of every primitive of matching markers, shapes and label glyphs alike. **LabelText**
  — replaces the rendered text of every label of matching markers regardless of flavour; empty string is legal and
  renders no glyphs. Both unset is an inert no-op; an empty collection (the default) means no overrides.
- **Non-effects** — overrides never alter geometry, footprint extent, inclusion, draw order, framing or the grid,
  and requests without them render exactly as before.
- **Diagnostics** — a key matching no discovered marker (group membership, considered before the distance filter)
  reports the informational `UnusedOverride` diagnostic and the render continues; a key matching a
  distance-excluded marker is not unused. A null key or null entry fails the render loudly.

## Editor Workflow

The `spatial_minimap` addon adds a **Spatial Minimap** bottom panel. Opening a scene renders its whole authored
coverage through the production renderer (`MiniMapPreviewDriver` constructs the same `MiniMapRenderer` scoped to the
edited scene — only the viewport driver inputs differ). Drag the preview to pan; use **Zoom In/Zoom Out** on top of
the automatic scale; **Whole Coverage** resets. Edits mark the preview stale and it auto-refreshes after a short
debounce; a failed refresh clears the image and shows the diagnostic instead of a stale frame. Authoring-time
footprint gizmos draw every primitive's world outline.

Evidence capture mode (used for review artefacts): run the editor with `MINIMAP_EDITOR_EVIDENCE=<tag>`; the plugin
opens the demo scene, captures editor-window screenshots of the open/pan/zoom-in/zoom-out states under
`game/temp/<tag>/`, and quits.
