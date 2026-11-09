---
id: AI-013
title: Visual AI Context
---

# Visual AI Context

## Requirement

AgenticMind sessions must gain live visual context by consuming the SCN-002 minimap renderer and the VISION-001
on-demand vision capture under one configuration key, `AI:VisualContextMode`: in the default `tools` mode the model
calls two zero-argument tools (a self-centred minimap and a first-person view capture); in `auto` mode the same two
tools remain callable and both images are additionally delivered automatically with every logical request through a
runtime-fabricated tool exchange; `off` disables all of it. Generated images are model-visible for a bounded ephemeral
lifetime — retained images are disposed to a textual placeholder at the next fresh scheduling turn, and automatically
delivered images never outlive the request cycle that created them — and, when AI-011 transcript logging is enabled,
written as sibling image files beside the turn page.

## Goal

Let players observe NPCs that reason about where they are and what they currently see: an NPC can read a top-down map
of its surroundings with itself highlighted, inspect a fresh capture from its own eyes, or receive both automatically
every turn. The images are live context, not memory — characters move, so pixels never linger past the action chain
that created them. The two consumed capabilities stay unchanged: the renderer remains character-agnostic (SCN-002
TR-4) and the vision rig keeps its one-shot capture contract (VISION-001 TR-53–TR-62). This spec delivers the AI
plumbing that SCN-002's Out Of Scope deferred.

## User Requirements

1. The user configures `AI:VisualContextMode` through the `AI` section of the shipped `AlleyCat.yaml`, with
   `user://AlleyCat.yaml` overrides under CORE-006's merge precedence. It accepts `tools`, `auto`, or `off`, defaults
   to `tools`, applies for the whole session, and a misconfigured nonblank value fails locally with a diagnostic
   naming the key and the permitted values.
2. In `tools` mode, an NPC can on demand request a top-down minimap — always centred on the NPC's current position,
   with the NPC's own marker highlighted — and a fresh still of what the NPC currently sees through its own eyes.
   Both arrive in its reasoning context without the NPC choosing where to look from or how far to map.
3. In `auto` mode, the NPC receives both images automatically with every logical request, immediately before its
   current scene status, while the two visual tools remain available for its own on-demand calls, which behave
   exactly as in `tools` mode.
4. In `off` mode, the NPC has no visual tools, no images are captured or generated, and no image files are written.
5. Visual requests never break the NPC: a character with no vision rig, a failed capture, or an unavailable renderer
   yields a contained error tool result in `tools` mode, and in `auto` mode the failed image is omitted with a
   warning while the turn proceeds.
6. Images are fresh, never stale: image content never survives beyond the action chain that created it. After the
   NPC's next fresh scheduling turn, an image from its own tool call is gone from its context — replaced by a short
   textual record of what was captured and when, while the call that produced the image remains as a truthful action
   record — and an automatically delivered image is gone without a trace, never appearing in any later request. An
   NPC that needs current visuals asks again.
7. With AI-011 transcript logging enabled, a developer finds every generated image as a file beside the relevant turn
   page — minimap PNG, view-capture JPEG — relatively linked and listed per cycle; with logging disabled, no image
   files are written anywhere.

## Technical Requirements

### Mode Configuration

1. `AIOptions` gains the optional setting `AI:VisualContextMode`, bound by the AI subsystem from the core-provided
   `IConfiguration` under CORE-006, whose sourcing, merge precedence, and subsystem ownership rules are normative
   here. The shipped `res://AlleyCat.yaml` documents the setting through comments only, with no value set, so the
   `tools` default applies unless a user override configures it. The binding, normalisation, and validation pattern
   of the reasoning-effort settings (AI-002 TR-26–TR-28) applies: values are trimmed and parsed case-insensitively;
   the accepted values are `tools`, `auto`, and `off`; unset or blank means `tools`; and an unrecognised nonblank
   value is rejected locally, before any provider request, with a diagnostic naming the configuration key and the
   permitted values. Unlike the reasoning settings, the mode is read once at AgenticMind session start (AI-002 TR-1)
   and stays fixed for that session.

   Authoring note: authored prompt stacks that reference the visual tools (AI-003 shared instructions) mismatch `off`
   mode, where the tools do not exist. This is a documentation concern for prompt authors, not a runtime
   requirement: the runtime neither rewrites nor validates instruction text against the active mode.

### Tools Mode

2. In `tools` mode the model-facing inventory gains exactly two zero-argument tools — working names `minimap` and
   `capture_view`; the exact names stay tunable while the zero-argument shape and the contracts of TR-3–TR-6 are
   fixed. Both bind the subject character at AgenticMind composition: the model supplies no identity, position,
   distance, or framing arguments, mirroring the trusted-binding rule of `read_lore` (AI-002 TR-23). Tool validation
   and typed binding occur only at AgenticMind composition (AI-002 TR-13); the common runtime gains no feature
   services.
3. The minimap tool renders through the SCN-002 `MiniMapRenderer` service (CORE-004 resolution): the centre is always
   the subject character's live world position at call time; the inclusion distance is the renderer's documented
   service default (SCN-002 TR-9), never model-settable; framing is the SCN-002 TR-10 auto-scale viewport; and the
   request carries `MarkerOverrides` (SCN-002 TR-19) keyed by the subject's resolved `FullId`, with a fixed documented
   gold highlight colour and label text `*You*` — literal asterisks, replacing the text of both label flavours. A
   subject hosting no discoverable marker makes the override key match nothing: the render still succeeds, apart
   from SCN-002's informational `UnusedOverride` diagnostic. SCN-002's edge contracts apply unchanged, including
   zero-markers-in-range success. The tool returns the renderer's raster image; the SVG document is not
   model-facing. There are deliberately no centre, radius, or size arguments: the model cannot know distances to
   important objects before reading a map. The tool is read-only — no Mind observations and no world effects.
4. The view-capture tool resolves the subject's own vision rig through `IHasVision` and takes exactly one fresh frame
   through `IVision.CaptureImage()` (VISION-001 TR-54), whose bounded freshness, no-polling, idle-cost, and
   side-effect contracts (VISION-001 TR-53–TR-62) apply unchanged. The tool returns the captured image and is
   read-only in the same sense as TR-3.
5. Tool failure — no vision rig configured (`IVision.Camera` null), a throwing capture, an unavailable renderer
   service, or a renderer failure — produces a contained error tool result: a textual result describing the failure,
   accepted and retained like any tool exchange, with no observation, watermark, scheduling, or session effect. A
   successful result carries the generated image as image content at its exchange position (TR-10); the existing
   textual result channel remains available alongside it.
6. Model-facing descriptions of the two tools follow AI-002 TR-15–TR-16 accuracy: each states its zero-argument
   shape, that it is bound to the calling NPC (a self-centred map, an own-eyes view), what it returns, that failures
   return an error result rather than an image, and that image content is ephemeral — later context shows a textual
   placeholder, so current visuals require a fresh call. No description may claim distance or framing arguments,
   promise persistent images, or imply observation or world effects.

### Auto Mode

7. In `auto` mode both tools of TR-2 stay model-visible and callable, and a model-initiated call to either behaves
   exactly as in `tools` mode: the capture contracts of TR-3 and TR-4 apply unchanged, the accepted exchange is
   retained like any real exchange, with its image content under the TR-9 disposal rule, and the result's image is
   presented under TR-10's retained-exchange rule. Only `off` mode removes the tools from the model-facing inventory
   (TR-13).
8. At every logical request materialisation (AI-002 TR-2, TR-4) in `auto` mode, both images are captured under the
   TR-3 and TR-4 contracts — a minimap centred on the subject with the same gold `*You*` override, a view from the
   subject's own rig — and the runtime fabricates from the successful captures exactly one synthetic visual-context
   exchange per logical request: an assistant message carrying one `minimap` and/or `capture_view` function call —
   the real tool names of TR-2 with zero-argument arguments — followed by the corresponding tool-result messages,
   placed immediately before that request's fresh current-scene message. Fabricated call IDs draw from the synthetic
   call-ID namespace (AI-002 TR-18) and never collide with a real tool call ID or a `scene_update` ID. The
   fabricated exchange is a per-request suffix presentation only: it is never appended to the retained accepted
   transcript; exact transport retries and that cycle's recovery rematerialisations reuse it — generation-tagged,
   with the captures of the materialising cycle — and the next materialisation rebuilds it from fresh captures, so
   an earlier cycle's fabricated exchange or images never appear in a later request. Each failed capture is omitted
   — its fabricated call is not composed — with a logged warning, and the turn proceeds; a cycle in which no image
   captured successfully presents no fabricated exchange.

### Ephemeral Image Lifetime

9. Generated image content is ephemeral in both modes:
   - Retained exchanges — a `tools`-mode tool result, or a model-initiated call under auto mode's TR-7: image
     content within the retained tool-result exchange is presented as an image at its exchange position in every
     request of the action chain that created it — continuations, exact transport retries, and recovery
     rematerialisations included. At the materialisation of the next fresh scheduling turn — a new logical request
     from the AI-002 TR-7 ladder, not a continuation or rematerialisation of the current chain — the image content
     is disposed: the pixels are replaced by a textual placeholder recording the image kind (minimap or view), its
     area or centre, and the game time of capture. The exchange, its tool call, and its arguments remain retained
     forever, keeping the action record truthful; the tool-result message remains, carrying the placeholder, so
     exchange unit integrity (AI-002 TR-20) is preserved — the placeholder replaces image content in place and
     removes no part of the exchange. This disposal is a deliberate, scoped exception to AI-002 TR-17's no-disposal
     rule, amended there.
   - Fabricated auto-mode delivery: the TR-8 fabricated exchange never enters the retained transcript, so no
     disposal rule is engaged — an automatically delivered image is model-visible only within the request cycle
     that created it and disappears without a placeholder at the next materialisation.
   - Rationale: spatial and visual state goes stale as characters move; lingering pixels would mislead the model.
10. Endpoint-aware transport: the pinned semantic is that a generated image is model-visible as an image at its
    exchange's or message's position in every request within the TR-9 lifetime, on every shipped protocol path;
    silently degrading to text-only is not acceptable. The provider endpoint kind — the effective chat client kind,
    Responses or Chat Completions, resolved under the `AI:ChatClientKind` binding (AI-002 TR-35–TR-36: a configured
    nonblank value overrides the provider resource property; unset or blank falls back to it) — must reach the
    visual-context presentation composition, and each path preserves the semantic by construction:
    - Retained exchanges (TR-9): a successful tool result presents its image through one exchange-adjacent user
      message — factual capture-record text plus the image parts — placed after the tool exchange, identically on
      both endpoint kinds.
    - Fabricated auto-mode exchange on Responses-capable endpoints (the provider default): each fabricated tool
      result of TR-8 carries its image in-result — result content of the TR-11 instructional text for its kind plus
      the image part — so the image rides inside the function-call output; no adjacent user message exists on this
      path.
    - Fabricated auto-mode exchange on Chat Completions and compatible backends: each fabricated tool result of
      TR-8 is text-only — the TR-11 instructional text for its kind — and each image rides an adjacent user message
      — that image's instructional text plus the image parts — placed after the fabricated exchange and before the
      current-scene message.
    Tests assert the semantic on both endpoint paths, not one fixed wire shape.

### Auto-Mode Instructional Texts

11. The text accompanying a live auto-mode image — inside a fabricated tool result on the Responses path, or as the
    label of its adjacent fallback user message — is a brief instruction, not a capture record: it explains what the
    image represents and tells the agent to treat it as a visual cue for the current scene. It must not carry
    `DescribeCapture()`-style capture facts — no coordinates, radii, or game-time prose. The normative texts, one
    per kind, are exactly:
    - Minimap: "A top-down minimap of your current surroundings, centred on your own position with your marker
      highlighted. Consider it a visual cue for where things stand around you in the current scene."
    - View: "This is what you currently see through your own eyes at this moment. Consider it a visual cue for the
      current scene."
    The tools-mode disposal placeholder of TR-9 keeps its factual capture record, unchanged.

### Transcript Images

12. When `Diagnostics:AI:EnableSessionTranscriptLogging` is enabled, every image generated under this spec — in both
    modes — is written exactly once as a sibling file beside the turn page of the cycle that generated it (minimap
    PNG, view JPEG), relatively linked from the turn page at the position of the exchange or message that carried it
    and listed per cycle; a disposed placeholder renders as text and writes no file. When the toggle is disabled,
    this feature writes no image files anywhere: images exist only within provider requests. AI-011 TR-26–TR-28
    (added by this spec's amendment) are the normative transcript contract; containment follows AI-011 TR-7.

### Off Mode

13. In `off` mode none of this spec's machinery is active: the two tools are absent from the model-facing inventory,
    no captures or renders are requested, no fabricated visual-context exchange exists, and no image files are
    written. SCN-002 and VISION-001 remain available to every other caller unchanged.

## In Scope

- The `AI:VisualContextMode` configuration key: binding, validation, shipped documentation, and the session-fixed
  lifetime.
- Tools mode: the two zero-argument, subject-bound visual tools with the fixed gold `*You*` minimap override and
  contained error results.
- Auto mode: the fabricated per-request visual-context exchange — capture, composition, placement, per-cycle reuse,
  rebuild, failure omission, and instructional label texts — with both tools remaining model-visible.
- The ephemeral image lifetime: placeholder disposal for retained images as a scoped exception to AI-002 TR-17
  (amended), and per-request non-retention for fabricated exchanges.
- Endpoint-aware image transport: in-result images on Responses-capable endpoints and the documented adjacent
  user-message fallback on Chat Completions and compatible backends.
- Transcript sibling images under the AI-011 toggle, through AI-011's amended TR-26–TR-28.
- `off`-mode inactivity.

## Out Of Scope

- Movement tools such as `walk_to`: the minimap makes coordinates legible for a future movement revision (SCN-002
  Goal).
- Configurable highlight colour or label text: the gold `*You*` override is fixed (TR-3).
- Model-supplied minimap framing — centre, radius, or size arguments: rejected by design (TR-3).
- Per-mind mode overrides, hot reload, and in-game mode switching; the mode is session-fixed (TR-1).
- Per-request image size, format, or downscaling controls, and auto-mode frequency tuning or token-budget controls;
  the renderer's and vision rig's documented defaults apply unchanged.
- Fog-of-war, exploration memory, and visibility or occlusion filtering of minimap content (also SCN-002 Out Of
  Scope).
- Transcript compaction (AI-002's recorded future revision), and mode-aware prompt-stack authoring guidance beyond
  the TR-1 authoring note.
- Live-model studies of visual reasoning quality: acceptance verifies requests, containment, and transcripts
  deterministically, not provider-side reasoning outcomes.

## Acceptance Criteria

### User Requirements

1. Configuration acceptance shows the three values binding case-insensitively after trimming, the default `tools`
   applying when unset or blank, user overrides taking CORE-006 precedence, a misconfigured nonblank value failing
   locally with a diagnostic naming `AI:VisualContextMode` and its permitted values, and the mode staying fixed for
   a session (UR-1).
2. Tools-mode acceptance shows a model call to each tool returning a successful result whose provider request
   carries the image at the exchange position: the minimap centred on the calling NPC with its marker gold and
   labelled `*You*` under auto-scale framing, the view a fresh capture from its rig; failure paths return contained
   error results while the session continues normally (UR-2, UR-5).
3. Auto-mode acceptance shows both tools model-visible and callable with model-initiated calls behaving exactly as
   in `tools` mode, every logical request carrying exactly one fabricated visual-context exchange with the fresh
   images presented immediately before the current-scene message, and a failed capture omitted with a warning while
   the turn proceeds (UR-3, UR-5).
4. Off-mode acceptance shows no visual tools, no captures, no fabricated visual-context exchange, and no image files
   (UR-4).
5. Lifetime acceptance shows images present through their creating action chain — continuations, retries, and
   recovery — then, for images from the NPC's own tool calls, a textual placeholder carrying kind, area or centre,
   and capture game time in requests after the next fresh scheduling turn, with the original tool call and
   arguments retained, and automatically delivered images never appearing in any later request (UR-6).
6. Transcript acceptance shows, with logging enabled, sibling minimap PNGs and view JPEGs beside the generating turn
   pages with relative links and per-cycle listing, and, with logging disabled, no image files written anywhere
   (UR-7).

### Technical Requirements

1. Configuration tests verify value parsing, default, precedence, the rejection diagnostic, and the session-fixed
   read (TR-1).
2. Inventory tests verify the two tools appear in `tools` and `auto` modes with zero-argument schemas, bind at
   AgenticMind composition only, and are absent — with unknown-tool rejection — only in `off` mode (TR-2, TR-7,
   TR-13; AI-002 TR-13).
3. Minimap tool tests verify the centre is the subject's live world position, the inclusion distance is the
   renderer's documented default, the override is keyed by the subject's `FullId` with the gold colour and `*You*`
   label replacing both label flavours, framing follows SCN-002 TR-10, a markerless subject still succeeds through
   the `UnusedOverride` diagnostic path, and no observations or world effects occur (TR-3).
4. Vision tool tests verify one fresh `CaptureImage()` frame from the subject's own rig with VISION-001's bounded,
   side-effect-free capture contract intact (TR-4).
5. Containment tests verify every failure path of TR-5 yields a contained error tool result with no observation,
   watermark, scheduling, or session effect.
6. Description tests verify the TR-6 accuracy claims against the shipped descriptions.
7. Auto-mode tests verify per-materialisation capture, exactly one fabricated exchange — real tool names,
   zero-argument arguments, results for the successful captures — placed immediately before the current-scene
   message, fabricated call IDs drawn from the synthetic namespace with no collision against a real tool call ID or
   a `scene_update` ID, suffix-only non-retention with reuse across that cycle's retries and recovery
   rematerialisations, rebuild at the next materialisation, no earlier-cycle image in any later request, and
   omission of failed captures with a cycle where nothing captured presenting no exchange (TR-8; AI-002 TR-18).
8. Lifetime tests verify the disposal boundary is exactly the next fresh scheduling turn — a continuation of the
   chain does not dispose — the placeholder's recorded kind, area or centre, and game time, retained tool calls and
   arguments, no orphaned or duplicated parts, and fabricated auto-mode exchanges never entering retained-transcript
   state (TR-9; AI-002 TR-17 as amended).
9. Captured-request tests verify both endpoint paths of TR-10: on a Responses-kind client the fabricated tool result
   carries its image in-result on the wire, and on a Chat-Completions-kind client the fabricated result is
   text-only with each image riding its adjacent user message; retained tool exchanges keep the exchange-adjacent
   user-message presentation on both paths; the transport path follows the effective kind resolved under
   `AI:ChatClientKind` — a configured override and the resource-property fallback each selecting their path — and
   the image is model-visible at its position within the TR-9 lifetime on every path (TR-10; AI-002 TR-35–TR-36).
10. Text tests verify the TR-11 instructional texts accompany every live auto-mode image — fabricated results on the
    Responses path and fallback carriers alike — with no capture-fact content, and that the tools-mode disposal
    placeholder keeps its factual capture record (TR-11).
11. Transcript tests verify sibling PNG and JPEG files, relative links at the carrying position, per-cycle listing,
    exactly-once writes, placeholder-only cycles writing no file, and no image files on the disabled path (TR-12;
    AI-011 TR-26–TR-28 as amended).
12. Existing AI-002 and AI-011 suites pass with the amendments; assertions sensitive to the model-visible inventory
    are updated against the amended AI-002 TR-13 rather than weakened.
13. Formatting verification, the warning-as-error solution build, unit tests, and affected Godot integration suites
    pass under repository skills; an independent review checks code–spec consistency in both directions.

## References

Normative dependencies:

- [AI-002: Agent Runtime](../002-agent-runtime/index.md) — amended by this spec: request shape with the auto-mode
  fabricated visual-context exchange slot (TR-2), the mode-dependent tool inventory (TR-13), description accuracy
  (TR-15–TR-16), the scoped image-disposal exception (TR-17), and the synthetic call-ID namespace covering
  fabricated visual calls (TR-18). Consumed unchanged: the session lifecycle (TR-1), materialisation (TR-4), the
  scheduling ladder (TR-7), the synthetic-delivery precedent (TR-18), and the chat client kind binding (TR-35–TR-36).
- [AI-011: Mind Session Transcripts](../011-mind-session-transcripts/index.md) — amended by this spec with the
  transcript-image contracts (TR-26–TR-28); the toggle, storage layout, and containment are consumed unchanged.
- [SCN-002: Spatial Minimap](../../scene/002-spatial-minimap/index.md) — consumed unchanged: the character-agnostic
  renderer (TR-4), per-request settings with the documented service default (TR-9), auto-scale framing (TR-10),
  edge contracts (TR-16), and `MarkerOverrides` (TR-19). Its deferred AI plumbing is delivered here.
- [VISION-001: Eyes](../../vision/001-eyes/index.md) — consumed unchanged: `IVision.Camera` and `CaptureImage()` with
  bounded, side-effect-free one-shot capture (TR-53–TR-62).
- [CORE-006: Microsoft Configuration Integration](../../core/006-microsoft-configuration-integration/index.md) and
  [CORE-004: Global Service Resolution](../../core/004-global-service-resolution/index.md) — configuration binding
  and renderer service resolution.

Implementation surfaces:

- `game/src/Mind/AI/AgenticMind.cs` — composition point for session tools, mode binding, and the auto-mode
  fabricated-exchange composition.
- `game/src/Mind/AI/Provider/AIOptions.cs` and `game/AlleyCat.yaml` — the configuration key and its documented
  default, following the `AI:ReasoningEffort` pattern.
- `game/src/Mind/AI/Tool/AgentToolResult.cs`, `game/src/Mind/AI/AgentSessionRunner.cs`, and
  `game/src/Mind/AI/MindSessionTranscriptRecorder.cs` — the image-carrying result, request replay with placeholder
  disposal, and sibling image writes extended by this feature.
- `game/src/Scene/MiniMap/MiniMapRenderer.cs` and `game/src/Vision/EyesBehaviour.cs` — consumed services.
