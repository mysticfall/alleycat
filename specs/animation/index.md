---
id: ANIM
title: Animation
---

# Animation

## Requirement

Define the shared motion-content and pose-conversion contract for the
[NPC navigation](../nav/001-npc-navigation/index.md) and
[player locomotion](../ctrl/001-locomotion/index.md).

## Goal

Give implementers, content authors, and reviewers one contract for pose-conditioned motion without encoding gait
special cases or treating source availability and retargeting as already proven.

## User Requirements

1. Locomotion uses dynamically sampled role/variant sources under
   [ANIM-002](002-locomotive-animation-set/index.md). Idle and locomotion variants are selected through the
   [ANIM-003](003-animation-catalogue/index.md) animation catalogue. Prefer our Kimodo content where available;
   verify actual availability and never copy or ship evaluation-only upstream samples. Player and NPC installation
   resolve their locomotion set through the catalogue, without source activation switches. The default is the
   complete feminine set.
2. Generated transitions and walking are displayed on the actual MakeHuman character, not merely a source skeleton.
   The visual reference is Godot's native retargeted body with F6-aligned hands, judged in native playback. Player
   fingers that are actually observed (authored hand poses or optical tracking) stay with their owner; generated
   motion does not override them, and does not hold the body once the player returns to tracked idle.
3. Locomotion variants express styles such as crawling or limping through the same generic speed-stage structure,
   with physical reference speed and phase-local motion distinguished rather than walking-specific assumptions.

## Technical Requirements

1. [ANIM-002](002-locomotive-animation-set/index.md) is normative for required motion roles, generic one-or-more speed
   stages, physical reference/local velocity metadata and dynamic consecutive source-window sampling. Normalised speed
   is relative to the selected variant; target placement combines physical metadata, intent and horizon. A fixed-only
   entry/exit catalogue is insufficient. Do not introduce walking-specific gait modes or prescribe a particular
   migration schema here. ANIM-002 TR9 requires a variant-local authoring reference posture at each authored role's
   boundaries within one continuous multi-role source take. It is not a dependency on a runtime idle, and it is not an
   idle or phase-reset barrier. ANIM-003 owns idle transition targets, and action targets remain externally owned.
   Boundary agreement alone does not prove suitable interior motion, phase, loopability or absence of carryover. Source
   creation is external and artist-owned. Its normative tooling dependency covers acceptable source/reference inputs,
   mandatory reviewed-action admission, canonical export, technical sanitising, traceable role slicing and source-set
   publication, not source-generation machinery. Generation-runner receipts are not required for a human-authored
   source. Preserve essential integrity and safe publication without extra misplaced-constraint tests as delivery
   prerequisites.
2. The game owns variant, state, role, virtual source playback/expected source foot phase and target selection under
   ANIM-002's lifecycle. MotionBricks connects actual realised context to supplied targets; generated duration and
   displayed feet do not own intended phase. ANIM-003 provides idle target windows; action owners remain outside
   ANIM-002. Current SOMA windows contain four consecutive frames at 30 Hz. Preserve source order, timing and local
   motion. Target-displacement intent does not authorise time-warping generated playback, or procedural locomotion.
   The one exception is the [ANIM-003 TR18](003-animation-catalogue/index.md#transitions) settle easing. It covers
   only bridges into and out of catalogue idles, and never the CTRL-001 player settle into tracked idle, which TR3
   still governs.
3. Implement the necessary SOMA runtime helpers alongside inference: pose preparation, generated-output decoding,
   boundary-frame handling, endpoint treatment, playback/chaining, and context recording follow the upstream contract.
   Raw ONNX output is not equivalent to the upstream runtime's processed motion. Endpoint processing does not
    authorise collision bypass or actor teleportation. For player release and settle replacement under
    [CTRL-001 TR4–5](../ctrl/001-locomotion/index.md#technical-requirements), the existing post-inference endpoint
    pass may take a separate outgoing presentation start without changing inference context or destination. Do not
    add a procedural endpoint ramp to force the settle to idle.
   Convert canonical source poses to model BONES frames before inference processing and back after endpoint processing,
   preserving joint positions. Named source-authoritative frame offsets come from Kimodo's
   `SOMASkeleton77.global_rot_offsets`, not a substitute USD rest basis.
4. **Native Presentation:** present generated motion through Godot's `RetargetModifier3D`, not a custom
   retargeting rig. The supported rigs are the reference Female and Male imported as below, not arbitrary-rig support.
   - **Import:** each reference imports from its production `.blend` with the authored named rest `AuthoredT`
     (T-pose body, natural MakeHuman hands) loaded as the actual skeleton rest, the MakeHuman bone map at
     `game/assets/characters/reference/skeleton_profiles/bone_map_makehuman.tres`, silhouette correction off, no
     exclusion filter and modifiers unapplied. [Character-generator tooling](../tooling/character-generator/index.md)
     supplies the rest reproducibly. Rest-dependent consumers (IK, forearm twist, pole anchors, optical finger
     tracking, authored hand references, colliders, external clips) are rechecked against this rest; only what
     visibly fails is reauthored.
   - **Hierarchy:** each actor owns a hidden, non-rendered 77-joint SOMA source `Skeleton3D` that receives generated
     samples. Its `RetargetModifier3D` drives the visible target, which must be the modifier's direct child:
     `Actor/<Model>/SOMA/Native/GeneralSkeleton`. Resolve the target skeleton explicitly, not by search or name match.
   - **Source Writer:** write absolute canonical local samples to the hidden source skeleton, not samples composed
     with its corrected rig-specific rest. Playing the canonical rest sample means writing that sample; resetting the
     source to its own rest is not equivalent.
   - **Mapping:** use a semantic body map, non-thumb source links 2/3/4 and thumb links 1/2/3, and transfer local
     position and rotation without scale.
   - **Source Rest:** the source skeleton's rig-specific rest carries the F6 hand alignment: SOMA palm-relative
     finger directions, with the thumb swung to SOMA's rest thumb-to-index gap. Derive it deterministically from the
     rig's rest/skin binds and the F6 donor, resolving bones by name. Name, topology or bind-coverage mismatches fail
     explicitly. The target rest stays `AuthoredT`, so optical finger-tracking guards are not relaxed.
    - **Root and Scale:** the game owns actor yaw and applies planar travel demand once through the controller's
      `MoveAndCollide`, in actor units. Remove that travel from the source hips so the retargeter does not apply it
      twice. Player settling under [CTRL-001 TR3](../ctrl/001-locomotion/index.md#technical-requirements) generates
      local hip articulation in the release frame, not planar demand; preserve NPC and ordinary collision paths.
      Native motion scale concerns skeletal translation only, never actor displacement, and no legacy scale constant
      is applied on top of it. Convert canonical world roots through the actual skeleton global transform in both
      directions, including actor yaw.
     For stationary-turn cycles only, apply game-owned actor yaw once under ANIM-002's
     [actor-facing contract][stationary-turn-facing]; generated bones do not drive actor facing in those cycles.
   - **Ownership:** the player retargeter transfers the wrist but does not write hand descendants while actual
     fingers are observed ([INTR-003](../interaction/003-hands/index.md) authored poses or
     [XR-002](../xr/002-optical-hand-tracking/index.md) optical tracking); NPCs map digits. Tracked idle is an
     explicit ownership transition that stops native body writes; a source held on its last sample must not keep
     ownership. Likewise, the retargeter is inactive during [ANIM-003](003-animation-catalogue/index.md#playback-rig)
     native idle. A first-placed idle modifier is the only presented writer for clip-keyed bones. Idles are never
     retargeted at runtime. Existing IK, hand-tracking and optical-last modifier order is preserved.
   - **Visual Authority:** Godot native playback is the visual authority. A Blender preview is authoritative only
     where independently shown to match native playback on skinned landmarks. Calibrated Blender posture parity is
     not a requirement.
5. **Realised-Pose Feedback:** a realised-pose reader samples the final visible target pose at the target skeleton's
   `skeleton_updated`, after collision-realised root movement, IK, hand tracking and other installed modifiers. It
   converts that pose to SOMA channels, keeping source proportions rather than target bone lengths, and records
   timestamped history from which four context frames are sampled at 30 Hz. The inverse for each mapped rotation
   accounts for both the corrected source rest and the actual target rest frames and returns canonical SOMA
   coordinates. Channels the target cannot observe
   retain canonical SOMA local rest and inherit observed parent motion; this policy is declared because no unique
   exact inverse exists. Only actual observations after installed modifiers under normal physics scheduling feed
   runtime history. This native→SOMA conversion exists only to feed MotionBricks. It also converts the start frames
   of ANIM-003 idle clips into target windows, but those frames are targets and never enter realised history.
   Do not substitute manual diagnostic scheduling, unplayed predictions, source-skeleton samples, or stale
    pre-modifier poses for realised context. NAV-001 owns NPC sequence-completion, failure/idle, replacement and shared
    worker lifetime; CTRL-001 owns player input, release/replacement and XR integration under those worker guarantees;
   this feedback contract does not imply endpoint convergence or authorise a fallback animation.
6. Verify code/model/content licensing and document local model setup without committing bulky models or secrets.
   Preserve source assets and authoring inputs; missing content is an explicit delivery issue, not permission
   to use evaluation-only samples or claim source readiness.
    Runtime content must not depend on diagnostic fixtures under `game/temp`.
    - **MotionBricks data:** `game/assets/animations/motionbricks/` contains runtime `poses.json` (canonical skeleton
      identity and provenance), `model-frames.json` (named model frames) and `source-rest/` scenes. Calibration and
      reviewed set data carry no idle samples or frames.
    - **Catalogue packs:** [ANIM-003](003-animation-catalogue/index.md) packs reference idle clips and reviewed
      sets. The default `feminine` entry binds `assets/animations/packs/default/locomotion/feminine/set.json`, relative
      to `res://`. The set, adjacent `landing-ablation-01.json` (used by all eight roles) and angular metadata stay
      together.
    - **Authoring inputs:** animation-only inputs and review material live under `game/assets/animations/source/`.
      Its `.gdignore` excludes that subtree, not the runtime assets. Dedicated test assets stay with their fixtures.
    - **Loading:** no prototype movement catalogue is a fallback. Quality-admission metadata does not turn normal
      source loading into an opt-in mode.
    Read-only source extraction and frame export belong to
    [Kimodo tooling](../tooling/kimodo-pipeline/locomotive-animation-set.md).
    External means separately provisioned, gitignored weights at
    `game/models/motionbricks/onnx/mmm_v6_soma_fp32.onnx`, not a required path outside the project.
    The root project owns the payload; worktrees symlink its `onnx` directory through
    [OpenChamber setup](../../.openchamber/project.json). Never commit weights. The model remains separately licensed
    under the NVIDIA Open Model Agreement, not the adapted runtime software's licence. Follow
    [model setup](../../game/models/motionbricks/README.md); NAV-001 TR2 owns runtime path selection and launch
    requirements.
7. Validate conversion and feedback with focused automated checks and actual-rig temporal evidence. No clip-seam
   planting contract or new foot-IK rescue is required or permitted to conceal generated-motion limitations.

## In Scope

- ANIM-002 conditioning sources/lifecycle, ANIM-003 idle targets, external action targets, runtime motion helpers.
- Source and licence verification, actual-rig conversion, realised-pose feedback, and validation.

## Out Of Scope

- A general action subsystem and terrain/contact adaptation; [ANIM-003](003-animation-catalogue/index.md) owns idle
  and the animation catalogue. ANIM-002's required roles, generic stages, source metadata and idle/action target
  boundary remain mandatory.
- Player policy beyond CTRL-001's initial slice, broad hardware support, and multi-NPC execution.
- Wrist/finger fidelity refinement beyond the F6 source-rest alignment and TR4 ownership; do not claim complete hand
  fidelity or weaken gross-body posture and realised-feedback validation.

## Acceptance Criteria

### User Requirement Acceptance

1. **ANIM-UAC-001 (UR1):** Required locomotion, catalogue idle and action sources and licences are identified; their
   availability is verified rather than inferred from upstream sample files, following ANIM-002 acceptance.
2. **ANIM-UAC-002 (UR2–3):** Actual MakeHuman temporal evidence in Godot native playback shows generated walking and
   entry into idle on both reference characters, with F6-aligned NPC hands, observed player fingers left with their
   owner and tracked idle free of held generated posture. The variant structure does not require a walking-specific
   mode to represent future styles.

### Technical Requirement Acceptance

1. **ANIM-TAC-001 (TR1–2):** Apply ANIM-002 metadata/sampling/lifecycle acceptance, including physical reference speed,
    dynamic four-frame windows and ANIM-003 idle compatibility. Verify game-owned phase separately from actual context.
     Apply [ANIM-002-TAC-004](002-locomotive-animation-set/index.md#technical-requirement-acceptance) for external
     source/reference properties, reviewed-action ingestion, essential integrity,
     sanitising/slicing fidelity and whole-window human review. Do not substitute source endpoints or human artistic
     approval for runtime idle targets, independent implementation review or actual-rig temporal evidence.
2. **ANIM-TAC-002 (TR3–5):** Automated conversion/context checks and actual-rig evidence show correctly processed
   generation, root/frame conversion, playback, and subsequent generation conditioned on realised MakeHuman poses.
   A standalone inference probe or matching bone names cannot satisfy this criterion.
   Verify the import recipe (`AuthoredT` as actual rest, bone map, silhouette correction off, no filter, modifiers
   unapplied), the explicit `SOMA/Native/GeneralSkeleton` hierarchy, map/link/transfer settings, and deterministic
   name-resolved source-rest derivation with explicit mismatch failure. On both rigs, a matched canonical input
   written as absolute local samples must reproduce the F6 hand presentation, and the final mapped channels read
   back from the target must return in canonical coordinates. After a blocked planar step under actor yaw and a
   known perturbation of a final target bone, the next submitted context must contain the collision-realised root
    and the perturbed bone, not the requested pose; planar travel demand is applied once to the actor and absent
    from the source hips. Apply CTRL-TAC-002 for the player-settle local articulation and presentation-start checks.
    Check the declared unobservable-channel policy, player digit exclusion versus NPC digit mapping, and that
    tracked idle stops native body writes rather than holding the last source sample. Normal-scene traces must
    connect submitted context to preceding actual `skeleton_updated` observations of the final target.
   Numerical fidelity does not replace actual-rig temporal evidence or the user's motion-suitability judgement.
3. **ANIM-TAC-003 (TR6–7):** Source/licence/setup records and independent review confirm protected inputs, complete
   temporal evidence, runtime loading from the documented layout without authoring or diagnostic files, reproducible
   export prerequisites, and absence of corrective foot-IK rescue. Confirm `.gdignore` excludes only the authoring
   subtree within the animation asset layout. NAV-001 and CTRL-001 own their respective playable evaluation and
   diagnostics.

[stationary-turn-facing]: 002-locomotive-animation-set/index.md#actor-facing-during-stationary-turns

## Implementation Status

Normal CTRL/NAV share the full eight-role feminine source set, dynamic four-frame sampling and actual post-modifier
feedback. CTRL uses source-guided travel, and NAV uses native path/handoff and finite terminal-facing plans.
CTRL integration with the shared angular mapping and planned-yaw stationary facing in both controllers are in
progress. Generated-root ownership remains a separate boundary. Catalogue idles, native idle presentation and the
idle bridges are delivered under [ANIM-003](003-animation-catalogue/index.md#implementation-status), and CTRL/NAV
resolve their locomotion set through its locomotion view.
TR4–5 native presentation and final-target feedback are the target contract; production import and runtime migration
is in progress and not yet verified or reviewed. See
[ANIM-002](002-locomotive-animation-set/index.md#implementation-status),
[CTRL-001](../ctrl/001-locomotion/index.md#implementation-status) and
[NAV-001](../nav/001-npc-navigation/index.md#implementation-status) for current source bindings and acceptance gaps.
The normal-source migration is technically validated, pending combined independent review; no default-entrypoint native
quality or headset acceptance is claimed. Source stepping/foot-sliding mitigation is accepted with initial twitch
deferred. Raw repetitions, generated gait/sliding/contact, masculine/default content and previews, native-shutdown and
hand-origin risks remain open. Broader ANIM delivery remains deferred-with-risk; plan steps 5/6 are not complete.

## References

- [Project Specifications](../index.md)
- [ANIM-002: Locomotive Conditioning Sources](002-locomotive-animation-set/index.md)
- [ANIM-003: Animation Catalogue](003-animation-catalogue/index.md)
- [Animation Asset and Pack Guide](../../game/assets/animations/README.md)
- [NAV-001: NPC Navigation](../nav/001-npc-navigation/index.md)
- [Kimodo Motion Sources](../tooling/kimodo-pipeline/locomotive-animation-set.md)
- [Player Character Control](../ctrl/index.md)
- [CTRL-001: Player Locomotion](../ctrl/001-locomotion/index.md)
- Upstream `/home/mysticfall/workspace/motionbricks/docs/`: `runtime_api.md`, `onnx_inference.md`, `soma_model.md`,
  and `scene_preview.md` define reference runtime semantics, not local implementation status.
