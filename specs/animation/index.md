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
   [ANIM-002](002-locomotive-animation-set/index.md), with idle supplied by its separate owner. Prefer our Kimodo
    content where available; verify actual availability and never copy or ship evaluation-only upstream samples.
   Normal player/NPC installation uses the same complete feminine source set without source activation switches.
2. Generated transitions and walking are displayed on the actual MakeHuman character, not merely a source skeleton.
3. Locomotion variants express styles such as crawling or limping through the same generic speed-stage structure,
   with physical reference speed and phase-local motion distinguished rather than walking-specific assumptions.

## Technical Requirements

1. [ANIM-002](002-locomotive-animation-set/index.md) is normative for required motion roles, generic one-or-more
   speed stages, physical reference/local velocity metadata and dynamic consecutive source-window sampling.
   Normalised speed is relative to the selected variant; target placement combines physical metadata, intent and
    horizon. A fixed-only entry/exit catalogue is insufficient. Do not introduce walking-specific gait modes or
    prescribe a particular migration schema here.
     ANIM-002 TR9 requires a variant-local authoring reference posture at each authored role's boundaries within one
    continuous multi-role source take. This is not an external idle dependency or a runtime idle/phase-reset barrier;
    actual idle/action transition targets remain externally owned. Boundary agreement alone does not prove suitable
     interior motion, phase, loopability or absence of carryover. Source creation is external and artist-owned.
     Its normative tooling dependency covers acceptable source/reference inputs, mandatory reviewed-action admission,
     calibrated export, technical sanitising, traceable role slicing and source-set publication, not source-generation
     machinery. Generation-runner receipts are not required for a human-authored source. Preserve essential integrity
     and safe publication without extra misplaced-constraint tests as delivery prerequisites.
2. The game owns variant, state, role, virtual source playback/expected source foot phase and target selection under
   ANIM-002's lifecycle. MotionBricks connects actual realised context to supplied targets; generated duration and
   displayed feet do not own intended phase. Idle/action owners provide compatible target windows outside ANIM-002.
   Current SOMA windows contain four consecutive frames at 30 Hz. Preserve source order, timing and local motion;
   target-displacement intent does not authorise generated-playback time-warping or procedural locomotion.
3. Implement the necessary SOMA runtime helpers alongside inference: pose preparation, generated-output decoding,
   boundary-frame handling, endpoint treatment, playback/chaining, and context recording follow the upstream contract.
   Raw ONNX output is not equivalent to the upstream runtime's processed motion. Endpoint processing does not
   authorise collision bypass or actor teleportation.
   Convert canonical source poses to model BONES frames before inference processing and back after endpoint processing,
   preserving joint positions. Named source-authoritative frame offsets come from Kimodo's
   `SOMASkeleton77.global_rot_offsets`, not a substitute USD rest basis.
4. Establish source mappings/rest poses, target MakeHuman proportions, coordinate frames, units, and root conversion.
   The existing mapping at `game/assets/characters/reference/skeleton_profiles/bone_map_makehuman.tres` is a starting
   point, not proof. Native Godot retargeting is preferred but remains conditional on actual capability and rig tests.
   Distinguish silhouette-normalised imported skeleton rests from the physical skin presentation retained by inverse
   binds. Preserve the evaluated calibrated target's physical posture, using consistent presentation frames for forward
   retargeting and realised-pose inversion; rest-relative rotation agreement alone does not establish fidelity.
   Preserve calibrated constraints and explicit missing-channel approximations: unobservable channels and constrained
   twist do not have a unique exact inverse. Keep source proportions for SOMA feedback rather than target bone lengths.
   The skin-bind mapping is limited to the existing rig with meshes in the skeleton's coordinate frame;
   missing or incompatible binds and unsupported mesh transforms must fail explicitly, not imply general rig support.
   Convert canonical world roots through the actual skeleton global transform in both directions, including actor yaw;
   skin presentation frames and source-proportioned child transforms are distinct from that world/skeleton conversion.
   The calibrated reference Female with same-space meshes is the supported rig, not a promise of arbitrary-rig support.
5. Record the poses actually displayed after collision-realised root movement and applicable character modifiers,
   convert them back to SOMA, and sample four context frames at 30 Hz from timestamped history. Only actual
   `SkeletonUpdated` observations after installed modifiers under normal physics scheduling feed runtime history.
   Do not substitute manual diagnostic scheduling, unplayed predictions, unmodified source poses, or stale pre-modifier
    poses for realised context. NAV-001 owns NPC sequence-completion, failure/idle, replacement and shared worker
    lifetime; CTRL-001 owns player input, release/replacement and XR integration under those worker guarantees;
   this feedback contract does not imply endpoint convergence or authorise a fallback animation.
6. Verify code/model/content licensing and document local model setup without committing bulky models or secrets.
   Preserve source assets and calibrated authoring inputs; missing content is an explicit delivery issue, not permission
   to use evaluation-only samples or claim source readiness.
    Runtime content must not depend on diagnostic fixtures under `game/temp`. Current runtime reads `poses.json`,
    `model-frames.json`, the external-idle `catalogue.json` and shared `reviewed-feminine/set.json`/angular metadata
    under `game/data/motionbricks/`. All eight movement roles bind to `landing-ablation-01.json`; no prototype movement
    catalogue is a fallback. Quality-admission metadata does not turn normal source loading into an opt-in mode.
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

- ANIM-002 conditioning sources/lifecycle, externally supplied idle/action targets, and runtime motion helpers.
- Source and licence verification, actual-rig conversion, realised-pose feedback, and validation.

## Out Of Scope

- A general action/idle subsystem, content-pack catalogue implementation and terrain/contact adaptation. ANIM-002's
  required roles, generic stages, source metadata and external-target boundary remain mandatory.
- Player policy beyond CTRL-001's initial slice, broad hardware support, and multi-NPC execution.
- Wrist/finger fidelity refinement for this playable slice; preserve existing mappings and explicit approximations,
  without claiming complete hand fidelity or weakening gross-body posture and realised-feedback validation.

## Acceptance Criteria

### User Requirement Acceptance

1. **ANIM-UAC-001 (UR1):** Required locomotion and external idle/action sources and licences are identified; their
   availability is verified rather than inferred from upstream sample files, following ANIM-002 acceptance.
2. **ANIM-UAC-002 (UR2–3):** Actual MakeHuman temporal evidence shows generated walking and entry into idle; the
   variant structure does not require a walking-specific mode to represent future styles.

### Technical Requirement Acceptance

1. **ANIM-TAC-001 (TR1–2):** Apply ANIM-002 metadata/sampling/lifecycle acceptance, including physical reference speed,
    dynamic four-frame windows and external idle compatibility. Verify game-owned phase separately from actual context.
     Apply [ANIM-002-TAC-004](002-locomotive-animation-set/index.md#technical-requirement-acceptance) for external
     source/reference properties, reviewed-action ingestion, essential integrity,
     sanitising/slicing fidelity and whole-window human review. Do not substitute source endpoints or human artistic
     approval for runtime idle targets, independent implementation review or actual-rig temporal evidence.
2. **ANIM-TAC-002 (TR3–5):** Automated conversion/context checks and actual-rig evidence show correctly processed
   generation, root/frame conversion, playback, and subsequent generation conditioned on realised MakeHuman poses.
   A standalone inference probe or matching bone names cannot satisfy this criterion.
   Compare displayed physical landmarks and rigid presentation orientations against independently evaluated calibrated
   target poses, not expected values derived from the retargeter's own rotation equation. Check that realised feedback
    observes target-pose changes in the same physical frames, retaining declared approximations for ambiguous channels.
    Check world/skeleton conversion under actor yaw and skin-presentation covariance independently, not only rest-delta
    arithmetic. Normal-scene traces must connect submitted context to preceding actual `SkeletonUpdated` observations.
   Numerical fidelity does not replace actual-rig temporal evidence or the user's motion-suitability judgement.
3. **ANIM-TAC-003 (TR6–7):** Source/licence/setup records and independent review confirm protected inputs, complete
    temporal evidence, runtime loading without diagnostic fixtures, reproducible export prerequisites, and absence of
     corrective foot-IK rescue. NAV-001 and CTRL-001 own their respective playable evaluation and diagnostics.

## Implementation Status

Normal CTRL/NAV share the full eight-role feminine source set, dynamic four-frame sampling and actual post-modifier
feedback. CTRL uses source-bound lift/recovery conditioning and source-guided travel; NAV uses native path/handoff and
finite terminal-facing primary-landmark plans. External idle, calibrated Female conversion and generated-root ownership
remain separate boundaries. See [ANIM-002](002-locomotive-animation-set/index.md#implementation-status),
[CTRL-001](../ctrl/001-locomotion/index.md#implementation-status) and
[NAV-001](../nav/001-npc-navigation/index.md#implementation-status) for current source bindings and acceptance gaps.
The normal-source migration is technically validated, pending combined independent review; no default-entrypoint native
quality or headset acceptance is claimed. Source stepping/foot-sliding mitigation is accepted with initial twitch
deferred. Raw repetitions, generated gait/sliding/contact, masculine/default content and previews, native-shutdown and
hand-origin risks remain open. Broader ANIM delivery remains deferred-with-risk; plan steps 5/6 are not complete.

## References

- [Project Specifications](../index.md)
- [ANIM-002: Locomotive Conditioning Sources](002-locomotive-animation-set/index.md)
- [NAV-001: NPC Navigation](../nav/001-npc-navigation/index.md)
- [Kimodo Motion Sources](../tooling/kimodo-pipeline/locomotive-animation-set.md)
- [Player Character Control](../ctrl/index.md)
- [CTRL-001: Player Locomotion](../ctrl/001-locomotion/index.md)
- Upstream `/home/mysticfall/workspace/motionbricks/docs/`: `runtime_api.md`, `onnx_inference.md`, `soma_model.md`,
  and `scene_preview.md` define reference runtime semantics, not local implementation status.
