---
title: Leg-Feet IK Test Setup Contract
---

# Leg-Feet IK Test Setup Contract

## Requirement

Define the rig-neutral two-rig fixture and evidence topology for IK-003 validation.

## Goal

Make generic provider resolution, future provider refusal, and leg-solve evidence comparable across player and NPC
rigs.

## User Requirements

1. Reviewers can inspect equivalent lower-limb contact scenarios on both reference rigs.
2. Review evidence makes provider selection, a refused future provider, or a solver defect understandable without
   guessing.
3. Gate B evidence prevents a plausible ordinary fallback solve from being mistaken for accepted contact evidence.

## Technical Requirements

1. The fixture topology contains one player rig and one NPC rig with equivalent scenario inputs and independent rig
   adapters. It has no shared production controller requirement.
2. Each scenario captures inputs, provider applicability, selected value-only resolution, pass/fail result, and blocking
   consequence for pre-IK composition and post-IK final-output validation. It records target, gate, influence,
   pole/two-bone output, and final applicable output. The stage order is
   `sync → provider → hip reconciliation (when present) → lower-leg consumers`. `CharacterIK` performs generic
   provider resolution and sole actuation.
3. Gate A scenarios cover terminal zero influence, explicit refusal, first-applicable finite composite selection, side
   isolation, and degenerate-pole fallback for both rigs.
4. Gate A is accepted as a synthetic, isolated two-rig native provider-pipeline technical control. It proves valid
   evidence-to-provider-to-solve flow without production navigation, player control, terrain, or pelvis integration.
5. Gate B is accepted only as disposable, test-only, isolated two-rig fixture evidence. It covers the matrix and
   retained records in [Gate B Accepted (Test Only)](#gate-b-accepted-test-only); it does not authorise production.
6. Each Gate B case retains source evidence, pre-IK decision, selected provider, native solver snapshot, post-IK
   decision, and blocking consequence. Metrics are validation evidence only, never steering input.
7. The fixtures do not define production policy or scheduling. They do not add production navigation, providers,
   scenes, `CharacterIK` contracts, ANIM schema, ANIM/NAV/CTRL integration, or production delivery.

## Deferred and Unaccepted Requirements

The following original IK-003 fixture contracts remain mandatory for later delivery. Gate A and the test-only Gate B
matrix do not replace, cancel, or accept them. No current fixture, suite, or playtest result claims their delivery.

### User Requirements

1. Inherited-camera visual validation makes knee bend-plane and foot-correction behaviour observable.
2. The required pose coverage makes ordinary geometry, foot-placement and rotation variation, hips-offset geometry, and
   degenerate-pole fallback observable and reproducible.

### Technical Requirements

1. **Required Test Artefacts:** The verification scene
   `game/tests/ik/leg_feet_ik_test.tscn` and runner `game/tests/ik/leg_feet_ik_test.gd` remain required. The scene must
   inherit `game/assets/testing/photobooth/templates/lower_body_5_cams.tscn`.
2. **Hips Override Harness:** The verification scene must contain a `BoneAttachment3D` bound to the hips bone. It must
   apply an authored explicit position override while animation does not drive that transform, including at least one
   non-neutral hips-offset scenario.
3. **Required Pose Coverage:** The fixture must reproducibly cover neutral standing, forward foot placement,
   outward/inward foot rotation during fallback, the `BoneAttachment3D` hips override, and a degenerate geometry that
   deterministically triggers fallback. Marker values remain tunable.
4. **Non-Visual Validation:** A C# integration test must load the same verification scene and verify pole-direction
   continuity under continuous foot rotation, solve-to-target consumption, read-only foot targets, stable hips-override
   behaviour, and deterministic degenerate-geometry response.

### Acceptance Criteria

1. Visual review verifies both deferred user requirements through the inherited cameras and every required pose.
2. Artefact review verifies Deferred Technical Requirement 1 and the required scene/runner/template topology.
3. Fixture review verifies Deferred Technical Requirements 2–3, including the `BoneAttachment3D` hips override without
   animation and the complete pose matrix.
4. The scene-loading C# integration test verifies Deferred Technical Requirement 4.

### Acceptance Traceability

- Deferred User Requirements 1–2 → Deferred Acceptance Criterion 1.
- Deferred Technical Requirement 1 → Deferred Acceptance Criterion 2.
- Deferred Technical Requirements 2–3 → Deferred Acceptance Criterion 3.
- Deferred Technical Requirement 4 → Deferred Acceptance Criterion 4.

The Gate A/Gate B fixture artefacts and criteria are not a named supersession mapping for these requirements; each
remains deferred and unaccepted until its stated acceptance criterion is met.

## In Scope

- Both-rig synthetic fixtures, scenario topology, ordered evidence capture, and Gate A/B validation.
- Visual and automated evidence for generic provider resolution and the subordinate leg solve.
- The accepted Gate B test-only fixture matrix, including native fallback-masking and post-IK coherence evidence.

## Out Of Scope

- Production gameplay tests, navigation, providers, scenes, `CharacterIK` contracts, ANIM schema, ANIM/NAV/CTRL
  integration, and delivery.
- Production terrain probing or adaptation, pelvis or body adjustment, motion warping,
  caps or feasibility policy, and provider-specific production policy or exact production
  scheduler ordering.

## Evidence Status

### Gate A Accepted

Gate A acceptance covers only a test-only native provider-pipeline technical control and independently reviewed visual
evidence from a minimal eight-bone Player/NPC role fixture. It uses real `Skeleton3D` and `TwoBoneIK3D` execution on
physics frames, with fresh `ModificationProcessed` evidence. It verifies all of the following:

- End bone to target error is at most `0.006 m`; signal-time anchor to target error is at most `0.0001 m`.
- Player/NPC role isolation, per-side fallback and isolation, and generic provider ordering are preserved.
- During the accepted run identified below, native execution emitted no `p_bone = -1` or `bone_idx = -1` engine
  errors.
- The photobooth inherits `lower_body_5_cams`, retains visible live overlays, and its all-camera baseline/Gate A
  comparison plus pole-forward cue were independently accepted.

The committed scene, runner, and automated-test contracts supporting reproducibility include:

- `game/tests/ik/contact_lock_native_pipeline_photobooth.tscn`
- `integration-tests/src/IK/ContactLock/ContactLockLegSolveFixtureIntegrationTests.cs`
- `integration-tests/src/IK/ContactLock/ContactLockNativePipelinePhotoboothIntegrationTests.cs`

The following ignored `game/temp` path identifies the historical accepted visual capture run; it is not a durable
tracked evidence location:
`game/temp/IK-003/stage5_native_provider_two_bone_gate_a/2026-08-22/18-54-24-native-signal-gate/`.

Reproducibility is supported by committed scene, runner, and test contracts and, where available, retained command
and transcript provenance. It does not depend on durable image artefacts. The non-headless transcript was emitted
during that accepted run; where retained, it supplies engine-output provenance without becoming a tracked artefact.

The accepted comparison retains its historical baseline caveat: the baseline validates the fixture's framing and
ordinary fallback only. It does not establish production contact behaviour.

Broad-suite results are not Gate A/B acceptance or production-delivery validation. They do not change the independently
scoped Gate A acceptance.

### Gate B Accepted (Test Only)

Gate B independently passed only as disposable, test-only native-fixture evidence. It is not approval for production
contact policy, production scene wiring, NAV/CTRL/ANIM integration, terrain or pelvis intervention, release-metadata
contact inference, or any production delivery. Gate A remains accepted and unchanged.

For every Player/NPC × left/right role-foot combination, the matrix derives rejection from genuine fixture data for each
of the following classes:

| Stage | Required Class |
| --- | --- |
| Pre-IK evidence | Missing; stale-invalid (stale or invalid); contradictory; fallback-masking |
| Post-IK output | Missing; stale; wrong-foot; non-finite; anchor-incoherent |

`Contradictory` means mutually incompatible fixture evidence for the same role, foot, and generation only. Evidence
for different roles, feet, or generations is not contradictory.

These fixture classes are not the production provider-refusal definition and do not narrow it. Production refusal uses
the complete [ANIM-002 refusal set](../../animation/002-locomotive-animation-set/index.md#canonical-contact-evidence).

`Fallback-masking` proves that invalid contact evidence cannot be promoted by a valid ordinary fallback solve. Its only
blocking artefact is a failed contact-publication/pre-IK record despite that valid ordinary fallback solve. This is a
fixture record, not a production release policy.

Every post-IK class is injected and rejected at the `ModificationProcessed` signal boundary. Its record preserves the
separate native `TwoBoneIK3D` snapshot and provenance, so native-solver success cannot promote an incoherent output.

For every matrix case, the retained record contains:

1. Source evidence, including role, foot, generation, and the injected class.
2. The pre-IK decision and selected provider, including any refusal.
3. A native solver snapshot of the target and `TwoBoneIK3D` result at signal time.
4. The post-IK decision for the required output class.
5. The blocking consequence: a pre-IK failure remains rejected despite fallback success, or a post-IK failure prevents
   Gate B from supporting production integration.

No downstream solve, snapshot, or validator may promote a failed pre-IK contact-publication record. A post-IK failure
also cannot be bypassed by an overwrite. This no-bypass status applies despite a valid ordinary fallback solve or a
native-solver success.

The reproducibility records are the fixture scene and runner,
`game/tests/ik/contact_lock_native_pipeline_photobooth.tscn` and
`game/tests/ik/contact_lock_native_pipeline_photobooth.gd`, plus
`integration-tests/src/IK/ContactLock/ContactLockLegSolveFixtureIntegrationTests.cs` and
`integration-tests/src/IK/ContactLock/ContactLockNativePipelinePhotoboothIntegrationTests.cs`. The independently
accepted visual run is precisely
`game/temp/IK-003/gate_b_contact_refusal_native_fallback/2026-08-22/21-53-57-fixture-only/`. Its screenshots and
`provenance_manifest.json` are ignored, ephemeral run artefacts, not durable accepted artefacts.

The approved Phase 1 amendment in [ANIM-002](../../animation/002-locomotive-animation-set/index.md) and
[IK-003](index.md) separately authorises a limited NPC vertical slice using explicitly authored ANIM tracks, never
release-phase metadata, as contact evidence. It does not change this fixture's scope or make Gate B production evidence.
Terrain, pelvis, caps, feasibility, scene wiring, NAV/CTRL/ANIM policy integration, and delivery remain outside Gate B.

## Acceptance Criteria

### User Requirement Acceptance

1. Review artefacts show equivalent ordinary-provider, fallback, and refused-future-provider scenarios for player and
   NPC rigs.
2. A reviewer can distinguish refusal, selected resolution, and lower-limb solver result from captured artefacts.
3. A reviewer can distinguish a valid ordinary fallback solve from accepted contact evidence in every Gate B
   fallback-masking case.

### Technical Requirement Acceptance

1. Fixture checks prove both-rig parity, adapter isolation, per-foot isolation, and the complete evidence chain for
   every scenario.
2. Fixture checks prove terminal zero-influence behaviour, explicit-refusal fallback, finite ordered composite
   selection, value-only providers, and `CharacterIK`-only target/gate/influence actuation.
3. Accepted Gate A evidence proves the test-only native provider pipeline uses the stated eight-bone roles, native
   physics/`ModificationProcessed` evidence, error limits, provider ordering, role/side isolation, and clean
   bone-index transcript.
4. Accepted Gate B fixture evidence covers the Player/NPC × left/right matrix: data-derived missing, stale-invalid,
   narrowly-defined contradictory, and fallback-masking pre-IK rejection; and signal-boundary missing, stale,
   wrong-foot, non-finite, and anchor-incoherent post-IK rejection with native provenance preserved.
5. Every Gate B matrix record retains source evidence, pre-IK decision, selected provider, native solver snapshot,
   post-IK decision, and blocking consequence. A pre-IK failure remains rejected despite an ordinary fallback solve;
   a post-IK failure cannot support production integration or be bypassed by an overwrite.
6. Fixture checks prove the generic modifier order and idempotent ordering repair without production scene wiring.
7. Acceptance rejects a gate result that lacks required input/evidence population, pass/fail result, or blocking
   consequence.
8. Review confirms Gate B defines no current production lock policy, production navigation, provider, scene, schema,
   ANIM/NAV/CTRL integration, terrain or pelvis intervention, release-metadata contact inference, or delivery.
9. Review confirms the Phase 1 production amendment uses explicitly authored ANIM tracks, never release-phase metadata,
   as contact evidence, and does not expand this fixture's scope.

### Acceptance Traceability

- User Requirements 1–2 → User Requirement Acceptance 1–2.
- User Requirement 3 → User Requirement Acceptance 3.
- Technical Requirement 1 → Technical Requirement Acceptance 1.
- Technical Requirement 2 → Technical Requirement Acceptance 2, 5, 7.
- Technical Requirement 3 → Technical Requirement Acceptance 2, 6.
- Technical Requirement 4 → Technical Requirement Acceptance 3.
- Technical Requirement 5 → Technical Requirement Acceptance 4–5.
- Technical Requirement 6 → Technical Requirement Acceptance 3, 5, 7.
- Technical Requirement 7 → Technical Requirement Acceptance 8–9.

## References

- [IK-003 Overview](index.md)
- [Leg-Feet IK Contract](leg-feet-ik-contract.md)
- [ANIM-002: Locomotive Animation Set](../../animation/002-locomotive-animation-set/index.md)
