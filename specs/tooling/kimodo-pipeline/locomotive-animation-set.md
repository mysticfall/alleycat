---
title: Kimodo Locomotive Animation-Set Pipeline
---

# Kimodo Locomotive Animation-Set Pipeline

## Requirement

Define the manually invoked Kimodo workflow that creates, validates, and publishes selected variant-scoped locomotion
and idle package sets for any supported character without becoming a gameplay-policy or production-content oracle.

## Goal

Technical artists can deliver selected compatible locomotive and idle variants for any supported character with
truthful motion and per-foot planted-interval metadata, idle and locomotion clips bound to the same calibrated
per-character retarget setup, preserved source assets, transactional publication, explicit cross-character package
reuse, and clear validation failures.

## User Requirements

1. Artists can bootstrap a character-present authoring workspace from a supplied retarget setup and audit generation
   themselves.
2. Supplied setups and source animations remain unmodified. Invalid inputs produce actionable diagnostics rather than
   in-place repair.
3. Failed or interrupted stages preserve prior published content and leave no partial package.
4. The selected variant publishes the complete ANIM-002 role set and, for each clip, its own duration, endpoint
   displacement and yaw, a native-key-rate root trajectory table with an error bound, phase events, stitch mappings,
   and per-foot planted intervals.
5. The current transaction and runtime configuration publish one selected variant per animation set. Alternative
   compatible fixture variants can be tested independently but do not enable additional production publication or live
   switching.
6. Artists can republish metadata from unchanged validated animation packages when all affected bindings remain valid.
7. Missing, stale, or mismatched motion or planted-interval data is reported truthfully. Tooling does not fabricate
   intervals or conclude content inadequacy.
8. Every published locomotion and idle clip states when each of both feet is planted, matching the clip's role and
   motion, including rolling about the ball of the foot and clips whose retargeted feet float or sink slightly.
   Authored ground slides and low shuffles are known pipeline artefacts: they count as swing, are not pinned, and the
   user judges them in-game.
9. Source animation is not regenerated or changed merely to conceal a runtime implementation defect.
10. A clean clone can import and load published packages without regeneration, repackaging, or local cache state.
11. Artists can rely on stable repository source assets and per-character authored inputs for reproducible bootstrap,
    workspace composition, extraction, and idle package workflows.
12. Artists can distinguish protected authoring inputs from generated, processed, and runtime outputs, which are not
    substitutes for those inputs.
13. Each repository source asset and each per-character authored input has an explicit lifecycle contract so it is not
    deleted or replaced by assumption; missing required inputs produce clear failures.
14. Published idle clips animate with the character setup's calibration intact: an idle bake that bypasses or deviates
    from the approved calibrated setup is refused before publication instead of appearing mis-oriented in-game.
15. A published package remains usable by another supported character's runtime where that reuse is declared: it plays
    without silent breakage, and animation tracks for bones the consuming skeleton lacks are known to no-op safely.
16. Adding a supported character requires only that character's own inputs and baseline approval; it does not modify
    another character's inputs, baselines, or published packages.

## Technical Requirements

### Tool and Source Boundary

1. Kimodo owns its manually invoked commands, implementation, configuration, Godot scripts, tests, and operational
   documentation under `tools/kimodo-pipeline/`. [The operational README][readme] defines supported invocation and
   destructive boundaries.
2. Authoring bootstrap and idle staging consume a complete user-supplied retarget setup as-is. They may create a
   variant workspace or a staged idle copy from a copy of it, but never alter calibrated helpers, constraints, poses,
   overrides, or source files.
3. Artists remain responsible for prompt authoring, generation, and visual audit. The pipeline does not automate content
   acceptance.
4. Source selection and bounded seam repair preserve anatomy, contact safety, hierarchy, recurrence, intended direction,
   and one-time root ownership. Repair may correct a candidate locally but must not mutate the protected source.

### Repository Source Asset Contract

The exact baseline digests belong to the linked tool implementation rather than this prose contract.

1. **KIMODO-SRC-001:** `game/assets/characters/animations/source/kimodo_source.blend` is the repository-fixed
   canonical clean Kimodo rig used by bootstrap and workspace composition. It contains the clean `Kimodo_Source` rig
   without actions, meshes, or materials. `bootstrap_paths.py` owns its path, and `bootstrap_workspace.py` owns and
   enforces its approved baseline digest. Preserve it byte-for-byte. Replacement requires deliberate baseline
   re-approval and coordinated path, validation, and test updates.
2. **KIMODO-SRC-002:** `game/assets/characters/animations/source/locomotion/locomotion_template.blend` is the
   repository-fixed, character-neutral locomotion template containing pose references, root waypoints, and neutral
   reference actions, including the default locomotion bootstrap idle pose reference (KIMODO-IDLE-001).
   `bootstrap_paths.py` owns its path, and `bootstrap_workspace.py` owns and enforces its approved baseline digest.
   Preserve it byte-for-byte. Replacement requires baseline re-approval and coordinated composition, validation, and
   test updates.
3. **KIMODO-SRC-003:** `game/assets/characters/animations/source/locomotion/locomotion.blend` is the read-only
   monolithic locomotion bootstrap and extraction input. It contains the source actions and semantic segments together
   with character and template content. `bootstrap_extraction.py` owns it and must never save over it. It is not a
   runtime package. Retirement requires an approved replacement for bootstrap extraction and every reproducibility and
   provenance dependency.
4. **KIMODO-SRC-004:** Generated workspaces, processed authoring carriers, runtime packages, metadata, and reports are
   derivatives. Their content or apparent similarity does not make them replacements for repository source assets or
   evidence that a source asset can be retired. Reproducibility is defined over durable artefacts — repository source
   assets, per-character authored inputs, approved baselines, and retained processed and published outputs — so
   provenance that names a transient staging carrier (for example a materialised multi-role blend) records direct
   lineage truthfully without implying that the carrier is durable reproducible state.
5. **KIMODO-SRC-005:** Repository bootstrap and default paths fail clearly when a required input is missing.
   Validation and tests cover path ownership, expected source structure, baseline or before/after byte integrity,
   provenance, and migration gates. Structural and read-only source checks remain separate from behavioural fixtures
   and production-content delivery oracles.
6. **KIMODO-SRC-006:** Each supported character owns two character-scoped authored input families beneath its
   character asset root: the calibrated retarget setup at
   `[character asset root]/animations/source/kimodo_retarget_setup.blend` and the animated idle sources at
   `[character asset root]/animations/source/idle/<variant>.blend`. They are protected authoring inputs, not
   repository-fixed assets and not derivatives. `bootstrap_paths.py` owns their path derivation, and the pipeline must
   never modify them. Their baseline approval is per character (KIMODO-CHAR-002).

### Character Setup and Calibration Contract

1. **KIMODO-SET-001:** The per-character calibrated retarget setup is the single calibration reuse unit. Every
   locomotion workspace and every idle package is staged from a verified copy of that character's setup, so every
   published clip carries the same calibration guarantee.
2. **KIMODO-SET-002:** Every bake binds to the character's approved setup baseline through a setup-parity gate: the
   staged setup copy's byte hash matches the approved baseline, and a constraint/helper fingerprint covering helper
   bone basis and roll state plus constraint mute state matches the approved setup. Any deviation refuses the bake
   before publication. The fingerprint names the calibration-relevant state so calibration drift is diagnosed as
   calibration drift, not opaque byte drift.
3. **KIMODO-SET-003:** The calibration state a baseline protects is explicit. For the calibrated reference setup,
   `thigh_l/_r_RET` helpers carry 35° axial rolls with their Locked Track constraints muted, and `lowerarm_l/_r_RET`
   helpers carry 50° rolls; these authored helper states define correct limb orientation. Another character's approved
   setup bytes are that character's calibration record. Limb-roll miscalibration preserves root position and planted
   intervals, so motion and interval gates cannot detect it; the setup-parity gate is the control that does.

### Idle Package Flow Contract

1. **KIMODO-IDLE-001:** Idle has two distinct roles. Role A is the animated idle loop source that becomes an idle
   package (KIMODO-IDLE-002). Role B is the locomotion bootstrap single-pose reference `pose_ref_idle`, sourced by
   default from the neutral locomotion template's pose references, preserving the reproducibility of published
   locomotion packages; an animated idle loop cannot satisfy single-frame admission. A future variant may declare an
   idle-derived pose reference as an explicit extension; it is not the default and is not required for delivery.
2. **KIMODO-IDLE-002:** Per-character animated idle sources live at
   `[character asset root]/animations/source/idle/<variant>.blend`. Each is a staged copy of that character's
   calibrated retarget setup with the animated idle action (Kimodo-space) appended. Inert staging rigs (`Kimodo_IK`
   and foot markers) are not part of idle sources.
3. **KIMODO-IDLE-003:** Idle packages flow without a locomotion workspace: a staged, setup-parity-verified copy of
    the character's setup with the appended idle action → visual-constraint bake → package → validated publication at
    `[character asset root]/animations/runtime/idle/<variant>`, publishing the artefacts Technical Requirement 7
    defines. The appended action must drive the source rig during evaluation, including correct slot binding for
    slotted actions; unresolved or ambiguous binding is refused rather than guessed. The bake retains the source's
    frame-varying motion over the admitted range, not merely keys and duration for a frozen pose. Generated provenance
    records the per-character idle source path and action.

### Character-Generic Pipeline and Cross-Character Reuse Contract

1. **KIMODO-CHAR-001:** The retarget processor is character-generic. The target rig name, character asset root, and
   root lateral-location axis convention are explicit per-character parameters with no single-character default; rig
   validation checks the resolved target rig; processed indexes record `target_skeleton` derived from the actual
   target rig.
2. **KIMODO-CHAR-002:** Gate-1 baseline approval is per character. Onboarding a new character setup adds that
   character's own baseline entry; an existing character's baseline is never edited to admit another character.
3. **KIMODO-CHAR-003:** Cross-character package reuse (Technical Requirement 10) is validated, not accidental:
   validation proves that animation tracks for bones absent from the consuming skeleton (for example `breast_l/r`
   when a male reference character consumes female-authored packages) no-op safely, and that the remaining tracks bind
   the consuming skeleton. Runtime templates binding another character's published libraries are the established
   consumption pattern this contract validates.
4. **KIMODO-CHAR-004:** Script-generated setup transplantation is an optional, undelivered future fallback for a
    character without a hand-calibrated setup, conditional on rejection of direct reuse and explicit authorisation.
    No setup-transplant command is currently provided or required for delivery. If authorised and implemented, its
    candidate must pass per-character baseline approval, the setup-parity gate, and artist visual audit before use as
    an approved input. Direct cross-character reuse remains the default path. The reference male directly consumes
    the female-authored `sad` idle and locomotion packages
    through its existing runtime template wiring; no male-authored setup or package publication is required for this
    accepted reuse. Artist acceptance covers interactive Blender review, not in-game/VR validation or equivalence to
    runtime motion scaling, procedural twist, or controller behaviour. Each future variant requires its own reuse
    validation and artist audit; this acceptance does not prevalidate it, remove character onboarding, or authorise
    future fallback implementation.

### Variant and Package Contract

5. Every processed index, metric, descriptor, provenance record, runtime library, catalogue, and planted-interval file
   is scoped by character, set, and the selected variant. No variant-neutral writable metadata slot may obscure that
   identity.
6. The publication manifest, transaction, and runtime configuration own exactly one selected variant per character and
   animation set. They reject a different additional variant before publication changes durable content.
7. Runtime roots follow `[character asset root]/animations/runtime/<set>/<variant>`. Locomotion publishes
   `locomotion_<variant>.tres`, `locomotion_<variant>_catalogue.json`, and per-foot planted-interval data
   (`per_foot_planted_intervals_v1.json`); idle publishes `idle_<variant>.tres` and `idle_<variant>_catalogue.json`,
   whose `per_foot_planted_intervals` field holds the idle planted intervals.
8. Runtime animations are embedded in the package `AnimationLibrary` and resolved by animation identity. Publication
   emits no standalone clip directory or runtime clip-path contract.
9. A manifest stages complete locomotion and idle packages, validates both, and publishes them through a journalled
   all-or-rollback transaction. The durable roots are not mutated until preflight and reload validation succeed.
10. Cross-character package use is explicit and validated (KIMODO-CHAR-003): a package authored for one character is
    consumed by another character's runtime only through declared, validated consumption whose absent-bone no-op is
    proven. Provenance retains truthful authorship and retarget lineage.

### Motion and Planted-Interval Data Publication

11. Publication copies each explicit ANIM-002 capability and validates the complete 22-capability inventory. It does
    not infer behaviour, phase lane, direction, anatomy, or operation from role/clip names, file names, order, counts,
    hashes, or `sad` conventions.
12. Each clip publishes exactly the variant-owned metadata that
    [ANIM-002 Technical Requirements 5–8](../../animation/002-locomotive-animation-set/index.md#motion-metadata)
    require: duration, endpoint root displacement and yaw, the root trajectory table with its key rate and declared
    error bound, phase events, stitch mappings, and per-foot planted intervals, in the declared coordinates and units.
13. The root trajectory table samples each clip's cumulative planar root position and yaw at its native key rate.
    Values are in source units; publication applies no rig's `motion_scale`, which consumers apply under the
    [ANIM-002 scale rule](../../animation/002-locomotive-animation-set/index.md#motion-metadata). The declared error
    bound holds against the embedded root track, so consumers can predict slice displacement and yaw within it,
    including moves below complete entry/exit transitions. Published metadata does not impose exact distance, rate,
    or clip completion on runtime consumers.
    - **Ball joints:** ball-joint data is not published, because it is rig-specific bind-time runtime data under
      [NAV-001 Technical Requirement 7](../../nav/001-npc-navigation/index.md#arrival-planner).
14. For every locomotion and idle clip, the pipeline emits per-foot planted and swing intervals over source time for
    **both** anatomical feet, covering the whole clip. Intervals are derived from the clip's declared role (entry,
    cycle, exit, side-step, pivot, arc, idle) and each one is checked against the clip. Idle intervals are written to
    the idle catalogue's `per_foot_planted_intervals` field.
15. A clip whose intervals are incomplete or fail verification is refused for publication and reported; it is never
    published with a gap or a guessed interval. Derivation implements the weight-bearing planted definition of
    [ANIM-002 Technical Requirement 8](../../animation/002-locomotive-animation-set/index.md#motion-metadata): a ball
    moving along the ground faster than the policy tolerance is swing, so authored slides and low shuffles are not
    planted.
    - **Signals:** the ball joint (the toe-bone rest-pose origin) height relative to that foot's own low ball-height
      baseline within the clip, so a constant float or sink left by retargeting does not affect the result, and the
      ball's planar ground speed. Derivation never uses the foot bone origin's speed or height.
    - **Role structure:** each role declares its expected step structure: the number and order of planted and swing
      phases per foot, the leading foot, and the start and end state. For example, a forward cycle is one full cycle,
      `i2f_l` starts from idle and ends with its left foot, the
      [leading foot](../../animation/002-locomotive-animation-set/index.md#runtime-capability-contract), lifting
      into the walk cycle after the right foot's first step, a stop ends with both feet planted, idle is planted
      throughout, and side-steps and pivots have a known step structure. Derivation heuristically fits this structure
      to the signals.
    - **Policy:** per-role expectations, signal tolerances, and heuristics are tunable policy data, not spec constants.
      Detection accuracy must be reasonable, not perfect. A clip whose signals cannot be fitted to its role's structure
      with adequate confidence is refused and reported.
    - **Boundary evidence:** each interval boundary records its cause, either height or speed; a speed-caused boundary
      also records the ground-level ball slide. Boundaries come only from signal crossings: the role-structure fit
      selects among those crossings and never creates a boundary.
    - **Derivation report:** derivation writes a durable `per_foot_planted_interval_derivation_report_v1.json` holding
      each boundary's cause and slide. It is retained in the processed package's
      `processed/<set>/<variant>/provenance/` folder next to the retained interval copy, is deterministic, contains no
      paths or timestamps, and is bound to the interval document by `intervals_sha256`. The runtime does not consume
      it.
    - **Independent verification:** checks independent of the classifier that placed the boundaries verify each
      planted interval: the ball descends into contact before its start, rises or leaves by the speed criterion after
      its end, and its planar drift inside the interval stays within the policy limit. Over-drift, no descent, no
      lift, and an unfittable structure are refusal causes.
16. The pinned point is the ball joint defined by [IK-003 Foot
    Planting](../../ik/003-leg-feet-ik/index.md#foot-planting).
17. Planted-interval data declares its schema version and derivation provenance. Producers, validators, and runtime
    loaders reject unknown or mismatched versions.
18. Published metadata describes motion only. It grants nothing and authorises no runtime movement or decision.
19. Each selected-package report derives its clip and per-foot inventory from that package and reports coverage,
    check results, and provenance. A data gap is reported as a data gap; it does not by itself establish missing
    clips or a need to generate animation.
20. Metadata-only publication is allowed only when retained animation, motion and planted-interval data, package
    identity, generation, and all integrity bindings remain valid. Otherwise the affected derived metadata is
    deliberately regenerated and validated from preserved source inputs.
21. Generated runtime catalogues, descriptors, planted intervals, and reports are pipeline-owned and are not
    hand-edited.

### Validation and Performance

22. Package validation checks complete capabilities, package/variant identity, finite data, frames and units, embedded
    library correspondence, root-trajectory and planted-interval provenance, both-foot coverage, hashes/generations,
    consumer compatibility, and clean-clone loading. Runtime-library SHA-256 bindings cover deterministic
    `ResourceSaver` text after excluding only the path-owned top-level resource UID annotation that Godot adds during
    import; embedded subresource identities and all animation data remain hash-covered.
23. Behavioural fixtures may use deterministic synthetic packages, but real selected-content checks are required for
    delivery behaviour. Production names, hashes, timings, distances, counts, and one variant's metadata are not
    universal fixture authority.
24. Runtime-facing data is precomputed and bounded. Consumers do not parse package data or search the animation library
    every frame.
25. Representative measurement records publication cost and relevant runtime data size/selection work using existing
    facilities. No unratified exact performance threshold is introduced by this specification.
26. Structural and numerical checks are preferred. Screenshots are optional diagnostic aids, not routine proof or a new
    fixture prerequisite. Pipeline validation does not claim subjective movement naturalness.

## In Scope

- Authoring bootstrap, source-safe selection and repair, materialisation, package validation, and publication.
- Idle package authoring from per-character idle sources through the calibrated retarget setup, including the
  setup-parity gate and idle-source lifecycle.
- Repository source-asset roles, ownership, immutability, provenance, replacement, and retirement contracts,
  including character-scoped authored inputs.
- Character-generic processing, character onboarding with approved inputs, per-character baseline approval, and
  cross-character package reuse validation.
- Complete selected-variant ANIM-002 capability, motion, and planted-interval metadata, including schema,
  derivation, provenance, publication-refusal, and runtime-loader validation contracts.
- Single-selected-variant publication, additional-variant refusal, transactional rollback, clean-clone loading, and
  selected-content package checks.

## Out Of Scope

- Player input, NPC navigation/arrival, runtime foot planting, IK actuation, and obstacle planning. This exclusion does
  not remove package handoff or consumer schema validation from scope.
- Additional or simultaneous production-variant publication, live variant switching, unattended generation, and
  subjective naturalness approval.
- Creating or regenerating source animation to repair gameplay implementation defects.
- Implementing the optional setup-transplant fallback without rejection of direct reuse and explicit authorisation
  (KIMODO-CHAR-004). This exclusion does not defer character onboarding, approved-baseline enforcement, every-bake
  calibration checks, or per-variant reuse validation.
- Hand-calibrating or visually approving a character's retarget setup: calibration approval and visual audit remain
  artist-owned decisions.
- Idle-derived locomotion pose references beyond the declared extension point; the neutral-template default is the
  delivery contract.
- Broader pipeline redesign unrelated to source preservation.

## Acceptance Criteria

### User Requirement Acceptance

1. **KIMODO-UAC-001:** An artist bootstraps and audits a variant workspace without modifying the supplied setup or
   source animation; invalid input produces an actionable diagnostic.
2. **KIMODO-UAC-002:** Failed staging, validation, or publication preserves the prior package and leaves no partial
   transaction artefacts.
3. **KIMODO-UAC-003:** One selected full-role variant publishes with its own metadata. Attempting to publish a different
   additional variant is refused without changing the selected package; fixture testing remains variant-neutral.
4. **KIMODO-UAC-004:** A clean-clone check loads the published package without regeneration or pre-existing cache state.
5. **KIMODO-UAC-005:** Reports expose missing or failed planted-interval data without fabricating intervals,
   successful locomotion, or content inadequacy.
6. **KIMODO-UAC-006:** Every real selected clip publishes both-foot planted intervals that match the clip, including
   rolling about the ball of the foot and clips whose retargeted feet float or sink slightly. Authored ground slides
   and low shuffles, known pipeline artefacts, are published as swing, so they are not pinned; the user judges them
   in-game.
7. **KIMODO-UAC-007:** From a clean checkout, an artist can identify each repository source asset's and each
   per-character input's workflow role and lifecycle, reproduce a supported source-dependent workflow (bootstrap,
   workspace composition, extraction, idle publication), distinguish inputs from derivatives, and receive an
   actionable missing-input failure instead of silent substitution.
8. **KIMODO-UAC-008:** An idle package publishes through the setup-calibrated flow with provenance naming its
   per-character idle source, and the user accepts its in-game appearance; a bake whose staged setup fails the
   setup-parity gate is refused before publication rather than shipping mis-oriented limbs.
9. **KIMODO-UAC-009:** A package authored for one supported character plays on another supported character's runtime
   through declared reuse, without silent breakage; tracks for bones absent from the consuming skeleton demonstrably
   no-op.

### Technical Requirement Acceptance

1. **KIMODO-TAC-001:** Package tests verify all 22 capabilities, variant-scoped metadata and provenance,
   embedded-library resolution, coherent package binding, and rejection of inferred, mixed, stale, or contradictory
   declarations.
2. **KIMODO-TAC-002:** Transaction tests cover staged validation, reload, normal commit, every rollback point, metadata-
   only publication, and refusal of a different additional variant before durable mutation.
3. **KIMODO-TAC-003:** Planted-interval checks verify both-foot, whole-clip coverage for every locomotion and idle clip;
   role-aware derivation from ball height relative to each foot's own low ball-height baseline and planar ground
   speed, with ground slides above the policy tolerance classified as swing; a recorded height or speed cause for every
   boundary and the recorded slide at every speed-caused boundary; production and retention of the derivation report,
   bound by `intervals_sha256`, in the package's provenance folder; start and end verification independent of the
   classifier; refusal of incomplete clips, with refusal tests demonstrating over-drift, no descent, no lift, and an
   unfittable role structure; native-key-rate root trajectory tables in source units, within their declared error
   bound against the embedded root track; and rejection of unknown or mismatched schema versions across producer,
   validator, and runtime loader.
4. **KIMODO-TAC-004:** A selected-package report derives its inventory, reports coverage, check results, and
   provenance, and does not convert a data gap into a content-quality conclusion. Alternative compatible
   fixture packages are tested independently and do not imply additional production publication.
5. **KIMODO-TAC-005:** Validation proves protected source immutability, clean-clone loading, bounded precomputed data,
   no per-frame consumer parsing/library search, and representative performance measurement.
6. **KIMODO-TAC-006:** A real selected package passes package and consumer checks; synthetic fixtures alone do not prove
   delivered-content behaviour or subjective naturalness. Short-motion checks distinguish missing metadata from missing
   source content without requiring new animation generation.
7. **KIMODO-TAC-007:** Source-contract checks bind the canonical rig and neutral template to their repository-fixed
   paths and approved baselines, prove the monolithic locomotion input remains read-only, bind each character's setup
   and idle sources to their per-character baselines, and verify generated provenance names the per-character idle
   source and repository sources where required.
8. **KIMODO-TAC-008:** Tests cover clear missing-input failures and the coordinated replacement or retirement gates for
   all repository source assets and character-scoped authored inputs. Structural and read-only checks are classified
   separately from synthetic behavioural fixtures and real selected-package delivery checks.
9. **KIMODO-TAC-009:** Setup-parity tests prove every bake binds to the character's approved setup baseline — byte
    hash plus constraint/helper fingerprint parity covering helper basis and roll state and constraint mute state —
    and refuse a deviating or uncalibrated staged setup before publication, including the documented reference
    calibration state (`thigh_l/_r_RET` 35° rolls with muted Locked Tracks, `lowerarm_l/_r_RET` 50° rolls).
    Idle-bake regression checks prove that an appended slotted action drives the source rig even when its slot
    identifier differs from the rig name, and that the baked clip retains frame-varying motion across the admitted
    range; constant-valued keys spanning that range do not establish motion retention.
10. **KIMODO-TAC-010:** Character-generic and reuse tests prove the parameterised target rig name, character asset
    root, and root lateral-location axis with no single-character default; `target_skeleton` recorded from the actual
    target rig; per-character baseline approval that leaves existing characters' baselines untouched; declared
    cross-character consumption whose absent-bone tracks are validated to no-op (for example `breast_l/r` on male).
    Candidate-output checks apply only if the optional KIMODO-CHAR-004 fallback is authorised and implemented; they
    must prove that its output remains unapproved until baseline approval, setup parity, and artist visual audit pass.

### Acceptance Traceability

- User Requirements 1–3 → KIMODO-UAC-001–002.
- User Requirements 4–5 → KIMODO-UAC-003.
- User Requirements 6–7 → KIMODO-UAC-005 and KIMODO-TAC-002–003.
- User Requirement 8 → KIMODO-UAC-006 and KIMODO-TAC-003.
- User Requirement 9 → KIMODO-UAC-001 and KIMODO-TAC-005, KIMODO-TAC-007.
- User Requirement 10 → KIMODO-UAC-004.
- User Requirements 11–13 → KIMODO-UAC-007 and KIMODO-TAC-007–008.
- User Requirement 14 → KIMODO-UAC-008 and KIMODO-TAC-009.
- User Requirements 15–16 → KIMODO-UAC-009 and KIMODO-TAC-010.
- KIMODO-SRC-001–006 → KIMODO-UAC-007 and KIMODO-TAC-007–008.
- KIMODO-SET-001–003 → KIMODO-UAC-008 and KIMODO-TAC-009.
- KIMODO-IDLE-001–003 → KIMODO-UAC-007–008 and KIMODO-TAC-007, KIMODO-TAC-009.
- KIMODO-CHAR-001–004 → KIMODO-UAC-009 and KIMODO-TAC-010; fallback candidate-output checks are conditional on
  authorised implementation, not current delivery.
- Technical Requirements 1–4 → KIMODO-TAC-005–006.
- Technical Requirements 5–10 → KIMODO-TAC-001–002, KIMODO-TAC-005.
- Technical Requirements 11–18 → KIMODO-TAC-001, KIMODO-TAC-003, KIMODO-TAC-006.
- Technical Requirement 19 → KIMODO-TAC-004.
- Technical Requirements 20–21 → KIMODO-TAC-002–003.
- Technical Requirements 22–26 → KIMODO-TAC-001, KIMODO-TAC-005–006.

## References

- [Project Specifications Index](../../index.md)
- [Developer Tool Layout](../index.md)
- [ANIM-002: Locomotive Animation Set](../../animation/002-locomotive-animation-set/index.md)
- [IK-003: Leg and Feet IK System](../../ik/003-leg-feet-ik/index.md)
- [Operational README][readme]
- [Bootstrap Path Ownership][bootstrap-paths]
- [Bootstrap Baseline Ownership][bootstrap-workspace]
- [Monolithic Bootstrap Extraction][bootstrap-extraction]
- [Retarget Processor][blender-retargeting]
- [Idle Export Defaults][retargeting]

[readme]: ../../../tools/kimodo-pipeline/README.md
[bootstrap-paths]: ../../../tools/kimodo-pipeline/kimodo_pipeline/bootstrap_paths.py
[bootstrap-workspace]: ../../../tools/kimodo-pipeline/kimodo_pipeline/bootstrap_workspace.py
[bootstrap-extraction]: ../../../tools/kimodo-pipeline/kimodo_pipeline/bootstrap_extraction.py
[blender-retargeting]: ../../../tools/kimodo-pipeline/kimodo_pipeline/blender_retargeting.py
[retargeting]: ../../../tools/kimodo-pipeline/kimodo_pipeline/retargeting.py
