---
id: ANIM-003
title: Standing Locomotion Catalogue
---

# Standing Locomotion Catalogue

## Requirement

Define the authoritative minimal standing-locomotion library, its portable Godot package, and the response data used by
its consumers.

## Goal

Players and NPCs have a small, ordinary, unarmed standing library that supports idle, forward and backward walking,
smooth walking direction changes, short local correction, and deliberate bilateral in-place pivots.

## User Requirements

1. The shipped standing library contains exactly nine clips: idle, ordinary forward and backward walks, left and right
   walk arcs, short left and right sidesteps, and ordinary unarmed left and right 90-degree in-place turns.
2. Walking direction changes remain smooth through the forward and walk-arc clips. Short local rear or lateral
   correction uses a rear/lateral diagonal blend; a distant destination behind the character turns, then walks forward.
3. Every selected clip has an ordinary upright, unarmed standing pose. Sad or hunched posture, raised-arm or combat
   poses, running, starts, stops, weapon motion, and unused alternatives are excluded. The former 45-degree crouched
   clip remains excluded.
4. Both in-place turns are deliberate, ordinary unarmed looped pivots. They turn in opposing directions through
   90 degrees with Root held exactly at `(0, 0, 0)`, remain continuous at every Root-motion loop wrap, and are not
   moving direction changes.
5. Consumers can load the clips individually or from one reusable `AnimationLibrary`, and can trace each selection to
   portable source provenance.
6. The selected clips supplied to `ILocomotion` make their graph role legible: pivots visibly turn without travel,
   straight walks and side steps travel without unintended turning, and walk arcs visibly turn while travelling.

## Technical Requirements

### Ownership And Selection

1. ANIM-003 owns the nine enabled `locomotion_standing` selection rows, their curation notes, processed outputs,
   extraction settings, package, role maps, and response profile.
2. [ANIM-001](../001-animation-source-pipeline/index.md) is the normative dependency for manifest and selection
   schemas, action naming, retargeting, root reconstruction, metrics, and portable processed-index contracts.
3. The selection CSV is the reviewable inclusion source of truth. Each enabled row joins uniquely to the source
   manifest by `motion_id`, uses category `locomotion` and group `locomotion_standing`, and records its role.
4. Only ordinary, upright, unarmed standing motion is eligible. The approved backward-walk role is permitted; running,
   starts, stops, combat, weapon, boxing, guard, crouched, prone, injured, stylised, sad, hunched, raised-arm, and
   unused alternatives are excluded from the enabled selection. The former 45-degree crouched clip remains excluded.
5. Native matched pairs are preferred for opposing directional roles. A derived mirror is allowed only for a vetted
   natural eligible source when no approved native counterpart exists; it is processed under the ANIM-001
   derived-mirror contract, never mirrored at runtime, and records its exception rationale.
6. A rejected sad, hunched, crouched, combat, weapon, raised-arm, running, or stylised source must never be mirrored.
   Rejected source-role rows are replaced rather than retained as native or derived fallbacks.

### Shipping Library And Role Map

1. The collection contains exactly these roles, each mapped to one clip: `Idle`, `ForwardWalk`, `BackwardWalk`,
   `WalkArcLeft`, `WalkArcRight`, `SideStepLeft`, `SideStepRight`, `TurnInPlaceLeft90`, and `TurnInPlaceRight90`.
2. `TurnInPlaceLeft90` uses source `c9ceef5f-b96c-11e4-a802-0aaa78deedf9`; `TurnInPlaceRight90` uses source
   `c9cef01d-b96c-11e4-a802-0aaa78deedf9`. Each is an ordinary, unarmed deliberate pivot with Root held exactly at
   `(0, 0, 0)` and source-informed, opposing signed 90-degree yaw. Its yaw direction and progress agree with the
   visible body turn and role semantics. Both pivot actions must be ANIM-001 loop-eligible and remain continuous at
   every loop wrap.
   `TurnInPlaceRight90` has clockwise-positive visible/body heading and negative canonical Blender Root Euler-Z;
   `TurnInPlaceLeft90` has the opposite signs.
3. The bilateral native 90-degree pivots are retained. No derived pivot substitute, synthetic yaw, fallback binding, or
   additional in-place-turn role is permitted.
4. Female and male graphs share one `AnimationLibrary`. Each graph has a deterministic role map containing the nine
   roles, library key, motion family, clip gender, graph role, and temporary-selection metadata when applicable.
5. Role selection may use only the nine library keys. It must not select excluded movement classes or infer a missing
   role by runtime mirroring an opposite clip.

### Import, Retarget, And Root Motion

1. ANIM-001 processing produces `locomotion_standing.blend`, its `.import` sidecar, `index.json`, and one metrics file
   per selected action under the existing processed Mixamo paths.
2. The `.blend` contains one target armature and exactly nine persistent, uniquely named actions. Native action names
   use `mixamo_<motion_id>` with hyphens replaced by underscores. Derived action names use the ANIM-001
   derived identity.
3. Import uses the ANIM-001 MakeHuman bone map, silhouette-rest-fixer handoff, and class-specific root-reconstruction
   contract. Every selected clip retains `Root` motion. Pivots retain only role-consistent signed yaw at Root
   `(0, 0, 0)`; straight walks and side steps retain role-consistent planar translation with deliberately fixed
   canonical heading and no unintended yaw; and walk arcs retain role-consistent planar translation and signed yaw.
   Source Root tracks are unreliable and are not blindly adopted; metadata validates semantics and direction but cannot
   be the only yaw author. Under ANIM-001's normative heading terminology, clockwise-positive visible/body heading is
   represented by negative right-handed canonical Blender Root Euler-Z because the canonical Root tail is `+Y`:
   `root_euler_z = -visible_heading_delta`. This representation contract applies to pivots and arcs only; it does not
   alter the deliberate straight/side-step policy or permit a runtime/Godot workaround.
4. The generated import and catalogue consume each selected action's persisted ANIM-001 effective loop intent,
   including the required looped pivots. Actions with that intent use the Blender `-loop` suffix before import and
   import as looping `Animation` resources; other actions do not and import as non-looping resources. The suffix is
   stripped at the external boundary and never appears in selection, library, package, role-map, or runtime references.
5. Every extracted clip provides exactly one position track and one rotation track targeting `GeneralSkeleton:Root`.
   Every consuming runtime `AnimationTree` resolves that exact root-motion track on its unique-name
   `GeneralSkeleton` binding. No alternate Root target, duplicate root-motion target, or per-clip track path is
   permitted.
6. Before import, the idempotent configurator writes exactly nine enabled top-level contained-animation entries. Each
   has the expected literal `res://assets/characters/reference/female/animations/locomotion/clips/<action>.res` primary
   and fallback paths; stale or duplicate entries are removed.
7. Godot may canonicalise a primary sidecar path to `uid://` only when it resolves to the expected clip and retains the
   enabled extraction and literal fallback path. Durable package references remain literal portable `res://` paths.
8. Packaging may normalise the accepted skeleton-track prefix and must deterministically neutralise duplicated Hips
   heading while preserving Root translation and yaw, Hips swing, translation, scale, timing, interpolation,
   non-heading pose, and full-body appearance. Runtime correction, warping, or synthetic root motion is prohibited.

### Package And Response Profile

1. The durable package is `standing_locomotion_library.tres` and `standing_locomotion_catalogue.json` at the existing
   locomotion paths. The library contains exactly nine extracted resources keyed by processed action name.
2. The catalogue records each clip's portable source and processed provenance, key, resource path, role, metrics path,
   persisted loop intent and analysis provenance, and root-motion metadata. A derived clip additionally records its
   distinct derived identity, source action and motion, reflection recipe, and source-artifact and recipe SHA-256
   hashes. Library keys, clip basenames, actions, and package entries are one-to-one.
3. The compact character-aware response profile derives deterministically from the role maps and selected metrics. It
   records signed forward, backward, lateral, walk-arc, and bilateral 90-degree pivot response with normalised-cycle
   timing provenance. It supports rear/lateral diagonal blending only from the selected backward and lateral roles and
   does not invent running, start/stop, combat, or other excluded response.
4. [CTRL-001](../../ctrl/001-locomotion/index.md) owns graph topology and root-motion consumption.
   [NAV-001](../../nav/001-npc-navigation/index.md) owns route planning. Both consume this role-map limitation and
   response profile; neither may add an animation role or substitute direct transform motion for it.

### Validation

1. Validation fails unless the selection rows, manifest joins, processed index, metrics, `.blend` actions, import
   metadata, extracted clips, package entries, library keys, role maps, and response profile agree on exactly nine
   unique clips and the required role map.
2. Validation rejects duplicate or empty motion IDs, actions, keys, resource paths, and roles; excluded classes;
   rejected source-role rows, missing source joins, stale outputs, runtime mirroring, synthetic yaw, and an unapproved
   in-place-turn role. It accepts only ANIM-001-compliant derived mirrors of vetted natural sources.
3. Import validation confirms ANIM-001 retarget settings, required Root and skeletal tracks, exactly one position and
   one rotation track at `GeneralSkeleton:Root`, nine enabled extraction entries, loadable clip resources, loadable
   library, per-clip loop modes matching persisted ANIM-001 effective intent, portable paths, and deterministic
   repeated imports. It accepts canonical sidecar UIDs only under the import contract above and proves looped
   Root-motion continuity at every wrap.
4. Class-specific Root-motion validation replaces delta-only proof. It derives visible/body heading from the evaluated
   physical Root forward/tail vector, not a raw Euler-Z or `matrix_basis` channel, and applies ANIM-001's canonical
   `+Y`-tail mapping, `root_euler_z = -visible_heading_delta`. It confirms `TurnInPlaceRight90` has clockwise-positive
   visible/body heading and negative canonical Root Euler-Z, `TurnInPlaceLeft90` has opposite signs, and representative
   opposite arcs meet the same mapping while travelling. Pivots have Root `(0, 0, 0)` throughout with only signed,
   role-consistent yaw; forward, backward, and lateral roles retain role-consistent planar translation, their deliberate
   canonical heading, and no unintended yaw; and arcs retain role-consistent planar translation plus source-informed
   signed-yaw direction and progress. Obsolete delta-only comparison or evidence helpers from prior failed fixes are
   removed or replaced. Validation also confirms preserved Hips and Root data after packaging. Representative pivot,
   straight/side-step, and arc captures are retained as visual evidence. Response-profile validation confirms complete
   nine-role coverage, deterministic derivation, signed response, and timing provenance. Independent visual review
   confirms upright, unarmed poses and each role's intended motion, including any derived travel role.

## In Scope

- The nine-clip selection, provenance, processing, import, extraction, packaging, role maps, response profile, and
  validation contracts, generated assets, and visual evidence required to ship it.
- Smooth walking direction changes through walk arcs, brief lateral corrections through sidesteps, and bilateral
  deliberate 90-degree in-place pivots.

## Out Of Scope

- Additional standing clips and unrelated locomotion collections, including run, start/stop, combat, weapon,
  crouch, prone, seated, and interaction motion.
- Consumer-specific blend thresholds, route scoring weights, and playtest tuning.
- Full motion matching, motion warping, player-specific snap turning, and navigation-owned root-motion application.
- No out-of-scope item excludes required processing, metrics, generated assets, validation, regeneration, or visual
  evidence for the nine selected clips.

## Acceptance Criteria

### User Requirement Acceptance

1. Catalogue review shows exactly the nine named roles and no excluded clip class.
2. Playback review shows an animated idle, ordinary forward and backward walking, smooth left and right walking arcs,
   and brief left and right lateral corrections.
3. Independent playback review shows both ordinary unarmed looped pivots turn 90 degrees in opposing directions with
   Root fixed at `(0, 0, 0)` and no Root-motion discontinuity at a wrap; straight walks and side steps do not
   unintentionally turn; and moving direction changes use walk arcs rather than pivots. Each clip's repeat behaviour
   matches its persisted pose-derived loop intent.
4. A contributor can trace every selected clip from selection row and source manifest through processed output,
   extracted resource, package entry, library key, and graph role without a local path, including source-pair rationale
   and, for a derived mirror, its distinct identity, source, recipe, and SHA-256 hashes.

### Technical Requirement Acceptance

1. Automated checks prove nine unique manifest-joined selection rows, actions, metrics, extraction entries, clips,
   package entries, library keys, role-map entries, and response-profile entries, with no stale output.
2. Import and package checks prove the ANIM-001 retarget and class-specific Root-motion contract, including unreliable
   source Root handling, metadata as semantic validation rather than sole yaw author, per-clip loop modes matching
   persisted loop intent and analysis provenance, the exact `GeneralSkeleton:Root` position and rotation tracks,
   loadability, portable paths, continuous loop-wrap Root motion, and deterministic Hips heading neutralisation without
   runtime compensation. Focused checks derive heading from the evaluated physical Root tail;
   prove the named right pivot's clockwise-positive visible/body heading maps to negative canonical Root Euler-Z, the
   left pivot's opposite signs, and representative opposite arcs; and reject raw Euler-Z, `matrix_basis`, or obsolete
   delta-only helpers as visible-heading evidence.
3. Role-map checks prove each consumer graph maps only the nine roles from the shared library, including both required
   stationary pivot roles, with no fallback or runtime-generated counterpart.
4. Response-profile checks prove deterministic character-role resolution, signed bilateral pivot response, and
    normalised-cycle timing for forward, backward, lateral, walk-arc, and pivot roles only. Visual checks reject sad,
    hunched, crouched, raised-arm, combat, weapon, running, and stylised poses, including as mirror sources.
5. Regeneration checks reproduce all nine generated actions, metrics, extracted clips, package entries, role maps, and
   representative pivot, straight/side-step, and arc validation captures without manual per-clip repair.

## References

- [ANIM: Animation](../index.md)
- [ANIM-001: Animation Source Pipeline](../001-animation-source-pipeline/index.md)
- [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md)
- [NAV-001: NPC Navigation](../../nav/001-npc-navigation/index.md)
