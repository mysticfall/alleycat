---
id: SCN-002
title: Spatial Minimap
---

# Spatial Minimap

## Requirement

Deliver a runtime minimap built from scene-authored markers: `MiniMapMarker` nodes are discovered by scene-group
membership, and a standalone `MiniMapRenderer` service composes and rasterises an image of a requested area on demand,
with an always-visible adaptive world-coordinate grid and optional per-request marker style overrides, plus editor
preview and authoring tooling.

## Goal

Give content creators and future tooling legible top-down layout context with absolute world coordinates. The map
exists to make future movement targets — for example `walk_to(x, y)` coordinates — readable, not to filter knowledge.
Creators compose arbitrary legends from shape primitives; anything in the scene appears on the map by hosting a marker,
with no character-specific or category-specific machinery.

## User Requirements

1. **UR-1 — Compose:** Creators build map entities from rectangle, circle, triangle and label primitives with authored
   fill colours and sizes, without writing SVG and without a fixed semantic category set: any colour and shape
   combination is legal.
2. **UR-2 — Participate:** Any scene entity appears on maps by hosting a marker node parented into the scene.
   Characters receive no special handling and require no character-side changes; markers are added and removed at
   runtime by normal scene-tree lifecycle.
3. **UR-3 — Request:** A caller requests a map of an area by centre coordinate, optionally with a maximum inclusion
   distance. The returned image reflects markers' current positions and headings, auto-scales to the included markers
   with a visible margin, and excludes markers beyond the distance.
4. **UR-4 — Coordinates:** Every image carries an always-visible coordinate grid labelled in absolute world X/Z metres,
   with +X right and +Z down. The map never rotates: an entity's heading shows as rotating marker geometry, so a
   triangle parented under a character renders that character's heading for free.
5. **UR-5 — Labels:** A label shows either authored text or the canonical `FullId` identity automatically resolved from
   the nearest identifiable ancestor, so a marker hosted under a character is labelled by that character's identity
   without configuration.
6. **UR-6 — Deterministic Output:** The same scene state and settings always produce the same self-contained SVG and
   image. Text renders identically in editor and runtime with no external fonts, scripts or network dependencies.
7. **UR-7 — Editor Tooling:** Authors preview the whole authored map on opening the tool, pan by dragging and zoom in
   and out on top of the automatic scale, and see authored footprints as 3D gizmos — all through the same composition
   the runtime uses.
8. **UR-8 — Emphasise:** A requester emphasises specific markers per render request — recolouring a subject
   character's marker, for example, or relabelling it — without editing the scene. Overrides live in the request
   only: renders without them are unchanged, and the renderer gains no character or identity API beyond the `FullId`
   it already resolves.

## Technical Requirements

### Namespace and Type Placement

1. **TR-1:** All new public minimap types live in namespace `AlleyCat.Scene.MiniMap` under `game/src/Scene/MiniMap/`.
   No new minimap types are added to the root `AlleyCat.Scene` namespace.

### Markers and Discovery

2. **TR-2:** `MiniMapMarker` is a `Node3D` representing one minimap entity. It is editor-creatable (`[Tool]`,
   `[GlobalClass]`, exported authoring properties) and owns an explicitly authored, ordered collection of
   `MiniMapPrimitive`s plus an authored draw-order ordinal — an exported integer, default 0 — that orders markers for
   painting (TR-12). Duplicate references in the collection are authoring errors. An empty collection is legal and
   renders no fragments.
3. **TR-3:** `MiniMapMarker` self-registers into a dedicated Godot scene group, exposing the group name as a shared
   constant (pattern: `IHearing.GroupName`). Registration is idempotent in `_EnterTree()` and deregistration happens
   in `_ExitTree()` — this hook pair, not `_Ready()`, is what makes membership track tree presence, including a marker
   that exits and re-enters the tree, for example when reparented: entering the tree registers the marker, exiting
   deregisters it. There is no renderer-side registration API and no authoring-time object or character list. Marker
   participation is authored by the scene creator parenting markers wherever they belong (pattern: the `Actors` group
   for characters); character templates require no changes, and there is no inheritance-based attachment or
   demo-character subclass.
4. **TR-4:** The renderer discovers markers by live scene-tree group query at render time. It must not use
   `ISceneContext` (SCN-001) or any character API, and must not special-case characters: every map entity is an
   ordinary marker wherever its creator parented it. No central authoring root type exists.

### Element Model and Primitives

5. **TR-5:** `IMiniMapElement` is implemented by `MiniMapMarker` and `MiniMapPrimitive`; its API returns an SVG fragment
   representing the element. Fragments are well-formed, self-contained SVG (paths, groups, fills) containing no
   `<text>` element and no reference to external fonts, resources, scripts or network resources. The marker's fragment
   composes its primitives' fragments in collection order.
6. **TR-6:** The initial `MiniMapPrimitive` subtype set is rectangle, circle, triangle and label:
   - Rectangle: positive authored width and depth, centred on the primitive's local origin.
   - Circle: positive authored radius; under non-uniform positive scale it may project as an ellipse.
   - Triangle: positive authored dimensions with its apex pointing along the marker's local forward (−Z), so heading
     arrows come free from the marker's world transform when parented under a rotating character.
   - Label: defined by TR-7.

   Every primitive exposes an authored fill colour with a neutral documented default. There is no built-in semantic
   category set and no built-in palette: legend colours and shapes are fully authored per primitive.
7. **TR-7:** Labels come in two flavours on one primitive type: (a) a configured text property; (b) automatic identity
   text resolved at render time from the nearest ancestor node implementing CORE-009 `IIdentifiable` — walking up the
   scene tree from the marker; the marker node itself is not consulted — displayed as the canonical `FullId`. Labels
   carry an authored local offset from the marker origin and a configurable text size with documented defaults. There
   is no automatic placement, collision avoidance or truncation. CORE-009 permits non-ASCII local IDs — for example
   Korean — that the bundled Inter font does not cover: such identity labels legitimately exercise TR-15's
   unsupported-glyph diagnostic and must not be "fixed" by transliteration or normalisation, which CORE-009 forbids.
   Identity resolution requires an instantiated `IIdentifiable` ancestor. Ancestor scripts that do not run in the
   editor — for example the production character — therefore genuinely provide none in the editor preview. In an
   editor-tool context — tool scripts running under the editor, discriminated by `Engine.IsEditorHint()` — such a
   label must not report the runtime `MissingIdentity` error; it reports the informational
   `IdentityResolvesAtRuntime` preview diagnostic (TR-16), which names the label and its marker by scope-relative 3D
   node path, names the nearest ancestor node bearing an attached script — by node name and/or scope-relative path —
   when one exists, since that is where identity will resolve at runtime, and explains in plain author-facing wording
   that the ancestor's script does not run in the editor so the identity appears in runtime renders. The label is
   still omitted from that render and the render continues; the implementation must not simulate identity by reading
   authored `Id` properties without a live `IIdentifiable`, which would invent semantics outside CORE-009.
8. **TR-8:** Primitive geometry is authored in local X/Z. Composition applies the marker's (and any intermediate
   chain's) world transform, then projects to world X/Z: translation in Y is ignored; yaw and non-uniform scale are
   supported, with negative (mirroring) scale projecting mirrored geometry; tilt is projected rather than rejected
   because markers legitimately ride on moving characters. Non-finite values are the only rejected transform class,
   matching TR-16.

### Renderer Service

9. **TR-9:** `MiniMapRenderer` is a service resolved through CORE-004 dependency injection, with no static or singleton
   accessor. A render request supplies a required world centre coordinate and optional per-request settings, each
   overriding a documented service default: maximum marker inclusion distance (sensible default), output pixel
   dimensions, bounding margin, grid text settings, the TR-14 grid minimum pixel spacing and the TR-19 marker style
   overrides (`MarkerOverrides`, default none). The TR-10 minimum world extent is the one exception — a documented
   fixed service guard, not a per-request setting. Tuning values stay configurable; their structure is fixed by
   this specification.
10. **TR-10:** A marker is included if and only if the X/Z-plane distance from the requested centre to the marker's
    world origin is at most the inclusion distance. The viewport is the axis-aligned bounding box of all included
    markers' projected footprints, unioned with the requested centre, expanded by the per-request margin on every side,
    then widened along one axis to the image aspect ratio under a single uniform pixels-per-metre scale — never
    distortion. A label's world anchor — its authored offset position under the marker — counts as footprint extent, so
    included label content is never projected out of view. A documented fixed minimum world extent (TR-9) keeps single
    or collinear marker sets legible, and markers contributing no geometry contribute no extent.
11. **TR-11:** Capture runs on the Godot main thread in a single pass: live group membership, transforms and identity
    text are read once into an immutable snapshot before composition, so one result never mixes states. Markers freed
    or queued for deletion are excluded. Later requests capture anew; no live-pose caching.
12. **TR-12:** Deterministic ordering: paint background, then the grid with its labels, then markers. Markers paint in
    ascending authored draw-order ordinal, tie-broken by scene node path; a marker's primitives paint in authored
    collection order. Composition must not depend on scene-group iteration order. Later markers may cover earlier
    ones: overlap is creator-controlled through ordinals.
13. **TR-13:** A successful render returns the self-contained SVG text and a rasterised image at exactly the requested
    dimensions, rasterised in-process. Identical snapshot state and settings produce byte-identical SVG — invariant
    numeric formatting, escaped strings, no timestamps or random identifiers — and identical decoded raster pixels;
    PNG compression bytes need not be identical. Rendering performs no file writes and mutates no scene state.
    Requests supplying no `MarkerOverrides` (TR-19) render byte-identically to the same request without the override
    feature.

### Grid

14. **TR-14:** An adaptive coordinate grid in absolute world X/Z metres is always drawn. Grid lines sit at integer
    multiples of an adaptive step from the `1, 2, 5 × 10^n` metre sequence, anchored at world zero with negative
    coordinates included. The step is the smallest that meets the minimum pixel spacing — a per-request setting with a
    documented default (TR-9). Grid lines carry absolute metre value labels along the image edges via the TR-15 text
    pipeline. The map is never rotated or re-oriented; +X is right and +Z is down.

### Text Pipeline

15. **TR-15:** All rendered text — labels and grid numbers — is converted to filled glyph outline paths from the
    bundled Inter font via the carried-over in-process pipeline. The SVG never depends on SVG text support, external
    fonts or external conversion processes. Unsupported glyphs produce a diagnostic, never silently empty text. All
    user-supplied strings are escaped for their SVG/XML context. Editor and runtime produce identical fragments for
    the same scene state.

### Error and Edge Contracts

16. **TR-16:** Edge behaviour is defined, not accidental:
    - Zero markers in range: the render succeeds with background and grid over a documented default extent around the
      requested centre, plus a zero-markers diagnostic. It is not a failure.
    - Empty or whitespace-only configured label text: renders no glyphs; legal and not an error; no placeholder text
      is invented.
    - Identity label with no instantiated `IIdentifiable` ancestor: that label is omitted for that render and the
      render continues. At runtime this is the `MissingIdentity` error diagnostic, naming the marker by
      scope-relative node path; in an editor-tool context (TR-7) the informational `IdentityResolvesAtRuntime`
      diagnostic applies instead, and `MissingIdentity` is never reported there.
    - Authored degenerate or invalid sizes (zero or negative width, depth, radius or triangle dimensions) and
      non-finite values: the render fails with a diagnostic naming the marker node path and primitive — fail loudly at
      authoring time rather than silently omitting.
    - Transient degenerate projection — a footprint collapsing to zero area from a live pose, for example a pitched
      marker: the affected primitive is skipped for that render with a diagnostic; the render continues.
    - Unused marker override key: a `MarkerOverrides` (TR-19) key matching no discovered marker — group membership,
      considered before the distance filter — is reported as the informational `UnusedOverride` diagnostic and the
      render continues. A key matching a distance-excluded marker is not unused.
    - Null marker override key or entry: a null key or a null override entry in `MarkerOverrides` (TR-19) fails the
      render loudly with a diagnostic.

    All minimap diagnostics — references and message paths alike — name markers and primitives by node path
    relative to the renderer's scope root (the edited scene root for the editor preview), never by absolute tree
    paths that traverse host UI hierarchies such as the editor's own Control tree; example reference:
    `alice/MinimapMarker/Label`.

### Editor Tooling

17. **TR-17:** An editor addon provides a preview that opens showing the whole authored coverage of the edited scene
    (every marker, inclusion distance effectively unbounded), then drag-to-pan and zoom in/out controls layered on the
    automatic scale, plus authoring-time 3D footprint gizmos for every primitive. The preview composes through the
    production renderer and element pipeline — only the viewport driver inputs differ — so editor and runtime render
    the same fragments for the same scene state. The preview supplies no TR-19 marker overrides — it exposes no
    override input — so overrides are a runtime-request concern only. The preview surfaces diagnostics in its
    status, in the TR-16
    scope-relative path form: an identity label whose ancestor script does not run in the editor appears as the
    informational TR-7 `IdentityResolvesAtRuntime` notice, phrased so authors understand the omission is expected —
    the label will show its identity in play mode — while genuine errors are still surfaced as errors. The tool must
    not start gameplay, move nodes, write generated assets on refresh, or present a stale image after a failed
    refresh, and it releases editor-only resources on close or scene switch.

### Delivery

18. **TR-18:** Deliver a representative demonstration scene and concise instructions covering marker composition,
    primitive transforms, both label flavours, renderer requests, distance filtering, auto-scale with margin, the
    grid, and the editor workflow. The scene exercises every primitive type, both label kinds, a live `ICharacter`
    carrying a marker that translates and rotates, at least one included and one excluded marker, and at least two
    distinct scales.

### Per-Request Marker Style Overrides

19. **TR-19:** A render request may carry `MarkerOverrides` — per-request restyling of specific discovered markers
    that never mutates scene state and adds no character or identity API beyond the `FullId` the renderer already
    resolves (TR-7, TR-9).
    - **Shape:** a read-only collection mapping the exact resolved `FullId` string (CORE-009) to an override record
      carrying an optional `Colour` and an optional `LabelText`. Both unset is a legal, documented inert no-op; an
      empty collection (the default) means no overrides.
    - **Matching:** a marker matches when its resolved `FullId` exactly equals a key, case-sensitively (CORE-009
      canonical exactness). Only markers with an instantiated `IIdentifiable` ancestor can match; markers without
      identity never match, and every matching marker in the render is styled. Overrides apply to every render
      entry that accepts settings — automatic centre-based, explicit-viewport and coverage renders; the editor
      preview passes none and is unchanged (TR-17).
    - **Colour:** when set, replaces the fill colour of every primitive of matching markers — shapes and label
      glyphs alike — so one knob recolours the whole marker.
    - **LabelText:** when set, replaces the rendered text of every label primitive of matching markers regardless
      of flavour; flavour determines the default text, and the override wins. Empty string is legal and renders no
      glyphs. Glyph diagnostics such as TR-15 unsupported glyphs apply to the overridden text.
    - **Extent and ordering:** overrides never alter geometry, footprint extent, inclusion, draw order, framing or
      the grid.
    - **UnusedOverride:** an override key matching no discovered marker — group membership, considered before the
      distance filter — is reported as the informational `UnusedOverride` diagnostic (TR-16) and the render
      continues. A key matching an out-of-range, distance-excluded marker is not unused.
    - **Validation:** a null key or a null override entry fails the render loudly with a diagnostic, consistent
      with TR-9 settings validation.
    - **Determinism:** override application is a per-marker lookup with no iteration-order effects, and requests
      without overrides render byte-identically to the same request without the feature (TR-13).

## In Scope

- Marker, primitive, element and renderer contracts under `AlleyCat.Scene.MiniMap`, including group discovery,
  distance filtering, auto-scale with margin, deterministic ordering, the adaptive grid and per-request marker
  style overrides.
- Self-contained text-to-glyph-path rendering and in-process rasterisation.
- Editor preview (whole-coverage open, pan, zoom) and footprint gizmos using production composition.
- Demonstration scene, authoring and API instructions, and automated/visual validation.

## Out Of Scope

- AI plumbing and tools, including `walk_to`: the grid only makes future coordinates legible.
- Fog-of-war, exploration memory and visibility or occlusion filtering.
- Multi-storey mapping and floor selection.
- Runtime player-facing minimap UI.
- New interaction-state subsystems.
- Automatic label placement or collision handling, and built-in rendered legends or category palettes: creators author
  their own.
- Editor-preview override affordances: the preview passes no overrides (TR-17).
- Per-primitive override granularity, overlay annotation elements, and path-keyed overrides for non-identifiable
  markers: TR-19 keys are resolved `FullId` strings and restyle whole markers.

## Acceptance Criteria

### User Requirement Acceptance

- **UA-1 (UR-1, UR-5, UR-6):** Independently inspect actual rendered demonstration images. Verify every primitive type
  is visible with its authored colours, both label flavours are legible, identity labels show canonical `FullId`
  values, and SVG text is glyph paths with no external dependencies. Capture success alone is not evidence.
- **UA-2 (UR-2):** Observe markers added and removed at runtime through scene-tree lifecycle: they appear and
  disappear on subsequent renders without renderer API calls or character-side changes.
- **UA-3 (UR-3):** Request renders around different centres and inclusion distances. Verify excluded markers are
  absent and included markers present, and that scale visibly adapts to marker spread with a margin. Use temporal
  action/result observations, not static endpoints alone.
- **UA-4 (UR-4):** Observe a character carrying a marker translating and rotating: the heading triangle rotates with
  the character while map orientation, grid anchoring and axis convention stay fixed. Verify the grid is present with
  legible absolute metre labels at more than one scale.
- **UA-5 (UR-7):** In the editor, record an action/result sequence of opening the preview on whole authored coverage,
  panning by dragging, and zooming in and out, producing visibly distinct images. Verify gizmos reflect the authored
  primitives and editor and runtime fragments match for the same scene state.
- **UA-6 (UR-8):** From a requester's seat, request a render emphasising a chosen character — for example a gold
  fill and/or a "You" label — and observe only that marker restyled: same geometry, positions, draw order and grid,
  with later plain requests showing the marker in its authored style again.

### Technical Requirement Acceptance

- **TA-1 (TR-1 – TR-4):** Tests verify namespace placement, `_EnterTree()` group self-registration with `_ExitTree()`
  deregistration including tree re-entry, live per-render group discovery, rejection of duplicate primitive
  references, and that the renderer references neither `ISceneContext` nor character APIs and contains no central-root
  or character special-casing types.
- **TA-2 (TR-5 – TR-8):** Tests verify fragment self-containment (no `<text>`, no external references), rectangle and
  circle geometry including ellipse projection under non-uniform scale, triangle apex direction under marker yaw,
  authored-offset labels, and transform/projection behaviour including ignored Y translation.
- **TA-3 (TR-9 – TR-11):** Tests verify DI-only renderer resolution, distance-filter boundary inclusion, the
  bounding-box, centre-union, margin and minimum-extent viewport rules, uniform scale with aspect fit, main-thread
  single-pass capture, and exclusion of freed or queued-for-deletion markers.
- **TA-4 (TR-12, TR-13):** Tests verify deterministic ordering independent of group iteration order (repeated renders
  of a stable scene produce byte-identical SVG; ordinal changes reorder predictably), exact output dimensions,
  escaping and invariant formatting.
- **TA-5 (TR-14, TR-15):** Tests verify world-zero-anchored adaptive steps at more than one scale including negative
  coordinates, minimum pixel spacing, grid labels, and unsupported-glyph diagnostics.
- **TA-6 (TR-16):** Tests verify each edge contract behaves exactly as specified: zero-marker success with
  diagnostic, empty label text, authored degenerate failure, and transient projection skip. For an identity label
  with no instantiated `IIdentifiable` ancestor, tests verify the runtime `MissingIdentity` error naming the marker
  by scope-relative node path, and — in an editor-tool context, exercised through a test seam rather than by
  launching the Godot editor — the informational `IdentityResolvesAtRuntime` variant naming the label, its marker
  and the nearest scripted ancestor by scope-relative node path, with the label still omitted and the render
  continuing. Tests also verify that every diagnostic names markers and primitives by scope-relative node path,
  never by an absolute path traversing a host UI hierarchy.
- **TA-7 (TR-17):** Editor verification shows production-path composition with editor/runtime fragment equality —
  checked on markers whose identifiable ancestry instantiates in the tool, while identity resolution under a character
  is verified at runtime through the demonstration scene — plus status surfacing of the TR-7 identity boundary as
  the informational `IdentityResolvesAtRuntime` notice phrased as expected behaviour, no gameplay side effects,
  visible failure and stale-image handling, and cleanup on close or scene switch. Diagnostics never enter SVG
  bytes, so the informational variant cannot affect fragment parity.
- **TA-8 (TR-18 and all):** Run formatting verification, the warning-as-error solution build, unit tests and affected
  Godot integration suites under repository skills; independently review code–spec consistency in both directions
  with evidence paths.
- **TA-9 (TR-9, TR-13, TR-16, TR-19):** Tests verify per-request overrides end to end: a colour-only override
  restyles exactly the matching markers' fills and nothing else changes; a label-text override replaces the
  rendered text of both label flavours; renders without overrides stay byte-identical (TR-13); an entry with both
  `Colour` and `LabelText` unset is an inert no-op; `UnusedOverride` fires for a key matching no discovered marker
  and does not fire when the only match is distance-excluded; markers sharing one `FullId` are all styled; null
  keys and null entries are rejected loudly; and extent, inclusion, draw order, framing and grid are unchanged
  under overrides.

## References

Normative dependencies (their existing contracts are preserved):

- [CORE-009: Identifiable Identity](../../core/009-identifiable-identity/index.md) — canonical `FullId` for identity
  labels and TR-19 override matching.
- [CORE-004: Global Service Resolution](../../core/004-global-service-resolution/index.md) — renderer service
  resolution.

Related, explicitly not consumed:

- [SCN-001: Scene Context API](../001-scene-context-api/index.md) — marker discovery is independent of scene-context
  membership; the renderer must not use `ISceneContext`.

Supporting references:

- [TEST-002: Visual Verification Scope](../../testing/002-visual-verification-scope/index.md) — evidence boundaries.
- [CHAR-002: Character Root](../../character/002-character-root/index.md) — `ICharacter` used by the demonstration
  scene only.
- [Project Specifications](../../index.md) — entry point for implementers and reviewers.
- `game/src/Scene/MiniMap/` — implementation home created by this feature.
- `game/assets/fonts/Inter/Inter_18pt-Medium.ttf` — bundled font consumed by the text pipeline.
- [Godot SVG Import](https://docs.godotengine.org/en/stable/tutorials/assets_pipeline/importing_images.html) — SVG
  text is unsupported; rationale for TR-15.
