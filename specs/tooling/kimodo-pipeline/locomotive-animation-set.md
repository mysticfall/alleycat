---
title: Kimodo Motion Sources
---

# Kimodo Motion Sources

## Requirement

Preserve Kimodo authoring inputs and provide truthful source-selection and conversion evidence for the
[shared animation contract](../../animation/index.md). Game-owned source roles and dynamic sampling are defined by
[ANIM-002](../../animation/002-locomotive-animation-set/index.md), not by MotionBricks itself.

This page governs tooling/source protection and the required ANIM-002 source properties, reviewed-action ingestion and
publication contract for artists, implementers and reviewers. Normal CTRL/NAV share the reviewed feminine eight-role
source set; that adoption does not complete broader variant/default admission or runtime quality acceptance.

## Goal

Artists and implementers can identify usable locomotion/idle sources, their canonical skeleton and provenance, and any
missing inputs without confusing generated packages with protected source assets or with proof of runtime suitability.

## User Requirements

1. Supplied setups and source animations remain unmodified; invalid or missing inputs produce actionable diagnostics.
2. Artists own external source creation, template duplication/adjustment where used, prompt authoring, mandatory motion
   review and setup approval. A reviewed Blender action can be ingested without a pipeline generation runner.
   Source motion is not regenerated merely to conceal a runtime defect.
3. Supply the shared eight-role locomotion source and externally owned idle from our Kimodo content where available.
   Verify availability rather than assuming it, and never substitute evaluation-only upstream sample motions.
4. Failed processing/publication preserves prior content and leaves no partial package. A delivered output is loadable
   from a clean checkout with documented prerequisites, without relying on an undocumented local cache.
5. Another character's inputs and approved baselines are not changed to add or validate a character.
6. Artists own each variant's local source reference posture without supplying a matching external runtime idle asset.
   Source acceptance considers the full motion, not just matching boundary poses or prompt wording.

## Technical Requirements

### Tool and Source Boundary

1. Kimodo owns its commands, implementation, configuration, external Godot scripts, tests, and operational
   [README](../../../tools/kimodo-pipeline/README.md) under `tools/kimodo-pipeline/`; follow
   [Developer Tool Layout](../index.md). The README describes executable tooling, not a separate runtime motion
   contract.
2. Bootstrap and idle staging use verified copies of the supplied calibrated setup. They never modify protected
   helpers, constraints, poses, overrides, or source files. Source selection and local processing preserve anatomy,
   hierarchy, intended direction, contact safety, and one-time root ownership.
3. Source suitability and conversion to MotionBricks target windows require explicit evidence under the shared
   animation contract. A packaged idle/walk clip or Blender preview alone does not prove SOMA conversion,
   actual-rig retargeting, or realised-context feedback.

### Repository Source Asset Contract

1. **KIMODO-SRC-001:** `game/assets/characters/animations/source/kimodo_source.blend` is the repository-fixed
   clean Kimodo rig, containing `Kimodo_Source` without actions, meshes, or materials. Preserve it byte-for-byte.
   `bootstrap_paths.py` owns its path; `bootstrap_workspace.py` owns its approved baseline. Replacement requires
   deliberate baseline re-approval and coordinated path, validation, and test updates.
2. **KIMODO-SRC-002:** `game/assets/characters/animations/source/locomotion/locomotion_template.blend` is the
   character-neutral bootstrap template with pose references, root waypoints, and neutral reference actions.
   Preserve it byte-for-byte. Its path and baseline have the same owners as the canonical rig; replacement requires
   baseline re-approval and coordinated composition, validation, and test updates.
3. **KIMODO-SRC-003:** `game/assets/characters/animations/source/locomotion/locomotion.blend` is the read-only
   monolithic bootstrap/extraction input, owned by `bootstrap_extraction.py`. Never save over it. Retirement requires
   an approved replacement for extraction and every reproducibility/provenance dependency; runtime replacement alone
   does not authorise source deletion.
4. **KIMODO-SRC-004:** Workspaces, processed carriers, runtime packages, metadata, and reports are derivatives, not
   source replacements. Preserve truthful lineage, including transient staging carriers, without representing those
   carriers as durable reproducible inputs. Reproducibility depends on protected sources, approved baselines, and
   retained processed/published outputs.
5. **KIMODO-SRC-005:** Required missing inputs fail clearly. Checks cover path ownership, structure, baseline or
   before/after byte integrity, provenance, and replacement/retirement gates, separately from behavioural evidence.
6. **KIMODO-SRC-006:** Per-character authored inputs are protected, not derivatives: the calibrated setup at
   `[character asset root]/animations/source/kimodo_retarget_setup.blend` and animated idle sources at
   `[character asset root]/animations/source/idle/<variant>.blend`. `bootstrap_paths.py` owns path derivation;
   approval is per character. The pipeline never modifies these inputs.

### Character Setup and Calibration Contract

This contract governs Blender-side Kimodo baking and idle staging only. It does not define runtime presentation,
preview authority or published runtime data; the [shared animation contract](../../animation/index.md) TR4 owns
presentation.

1. **KIMODO-SET-001:** The approved per-character setup remains the calibration reuse unit for Kimodo baking.
   Locomotion workspaces and idle staging use a verified copy of it.
2. **KIMODO-SET-002:** Every bake verifies setup parity before publication: approved setup-byte hash and a
   constraint/helper fingerprint covering basis, roll, and constraint mute state. Refuse deviations as calibration
   failures rather than opaque byte drift.
3. **KIMODO-SET-003:** The reference calibration includes `thigh_l/_r_RET` 35° axial rolls with Locked Track
   constraints muted and `lowerarm_l/_r_RET` 50° rolls. Each other character's approved setup records its own
   calibration; matching root motion cannot prove limb calibration.
4. Idle staging must bind the appended animated source action, including slotted actions, and retain frame-varying
   motion through the admitted range. A bootstrap single-pose reference is not an animated idle source, and keys
   spanning a duration do not prove retained motion. The neutral template remains the bootstrap reference default.
5. Character-generic processing uses explicit target rig, asset root, and root-axis parameters; validate the resolved
   rig and record the actual target skeleton. Approvals are per character and do not alter another baseline.
6. Cross-character reuse is explicit and validated: prove remaining tracks bind and absent-bone tracks no-op safely.
   Preserve truthful authorship and retarget lineage. Source reuse does not prevalidate future variants or Godot motion.
   Automated setup transplantation remains unauthorised unless separately approved after direct reuse is rejected.

### Source Production Contract

1. **KIMODO-PROD-001:** Source creation is external and artist-owned; the repository delivers reviewed-action ingestion,
   canonical export and source-set publication, not source-generation machinery or an executable template recipe.
   Retain inspectable authored Blender sources and reference/authoring inputs. Record source, variant/posture,
   local reference identity, stages, roles, timing, constraints, root/facing trajectories and
   available prompts/seed/generation settings. Do not invent generator provenance for user-authored actions or promise
   bit-identical stochastic inference. Prefer short literal subject/action/direction prompts without style
   embellishments; constraints carry spatial requirements. The reference
   is local to the variant, not a runtime idle dependency, and may be arms-down or another authored posture.
2. **KIMODO-PROD-002:** An acceptable complete variant has one continuous take containing all eight ANIM-002 roles, not
   separate per-role takes. Reviewed subsets may support representative implementation without claiming completeness.
   Constrain each role's start and end to the same variant reference using whole-pose placement: preserve child-pose
   configuration while the root follows that boundary's declared world position and facing. Travel and facing remain
   separate; ground Root and Hips/pelvis height are not interchangeable. Do not substitute mirrors or reverse playback.
3. **KIMODO-PROD-003:** Artist-owned source production declares multiple intended cycles per role, acquisition/settling
   allowances, middle usable ranges and representative-speed intervals before source creation. Preserve the predeclared
   middle-cycle extraction policy; acquisition and settling remain outside retained ranges.
   Never automatically select favourable windows or silently shift failed ranges. A deliberate human range revision
   creates a new source/review revision, not a retroactive pass.
   Retain full chronology, role boundaries and provenance. Review covers the declared ranges before ingestion.
4. **KIMODO-PROD-004:** External authoring preserves declared constraints and source protections; unconstrained output
   must not be represented as satisfying constrained authoring. No repository generation runner, fixed extension or
   extra misplaced-constraint tests are delivery prerequisites. Human review decides artistic suitability, not new
   aesthetic constraint gates. Essential processing checks reject nonfinite data, unsupported skeleton/coordinates/rate,
   invalid ranges, mismatched source identity and incomplete role coverage when claiming a complete variant.
   Foot-joint targets are not sole-contact
   assertions. Preserve authored sources and setups; approval does not waive data integrity.
5. **KIMODO-PROD-005:** Provide a maintained command to ingest an arbitrary approved Blender source/action with explicit
   role/review metadata, independent of generation-runner receipts. Evaluate original canonical Kimodo/SOMA poses
   read-only, without a MakeHuman round-trip or saving/rebaking source files, and validate the source against the
   published canonical skeleton identity (joint names, parents and rest), not handmade calibration rows. Use a small
   versioned format with generic variants containing one or more speed stages. Retain all chronological samples and
   provenance. Slicing may use
   ranges/sample offsets over the full take; separate clips are not required. Preserve consecutive four-frame 30 Hz
   windows, local motion and placement displacement/facing, without sampling across unrelated role boundaries.
   Document and verify each technical sanitising operation's pose/trajectory fidelity; normalisation/cleanup must not
   repair gait, plant feet, invent steps, perform runtime seam stitching or teleport endpoints. Optional source-boundary
   processing follows the cycle policy below, not mandatory sanitising. Escalate intended-motion changes.
   Validate physical representative reference velocity/speed separately from local velocity/angular progression,
   travel separately from facing and intended phase separately from observed evidence. Preserve uneven motion; do not
   fabricate positive reference translation speed for stationary turns. Identify a candidate complete cycle supported by
   actual source motion, manually or with reused deterministic tools; no particular algorithm is mandatory. Freeze
   source bounds, period, endpoint-duplicate convention and accumulated travel/facing transform before a raw
   repeated-sampling trial. New experimental cycle metadata wholly inside an approved role retains its approval bounds
   and whole chronology; it does not mutate the source, relabel rejected motion or admit runtime-loop quality.
   Exact pose/derivative equality or certified recurrence is not a prerequisite: first test without boundary processing.
   Measure boundary discrepancies diagnostically and apply
   [ANIM-002 TR4/8](../../animation/002-locomotive-animation-set/index.md#technical-requirements) for wrap-straddling
   four-frame sampling, cumulative placement and repeated actual-rig generated evidence before accepting cyclic use.
   MotionBricks interpolation may tolerate discrepancies; it does not guarantee suitability. Source-boundary processing
   is only an optional, explicitly documented and human-reviewed fallback when discrepancies visibly harm generated
   motion, not automatic gait/output repair. Undeclared non-cyclic content retains truthful exhaustion, never silent
   wrapping.
6. **KIMODO-PROD-006:** Require human approval bound to source hash, action identity and reviewed ranges. Review the
   full relevant window on canonical source and reference-character preview where applicable: reference, acquisition,
   movement interiors, transitions and settling. Boundary agreement, prompt wording and transport checks alone cannot
   admit motion. Retain rejected takes/reports; failed or unreviewed motion cannot become admitted content. Publish
   content and review/provenance metadata atomically per destination, preserving prior valid outputs on failure.
   Record hashes, action/frame/rate identities, units, tool identities and available generation provenance. Human
   artistic approval is distinct from independent implementation safety review and actual-rig runtime proof.
   Reference-character previews follow the shared animation contract's visual authority: Godot native playback, or
   a Blender preview independently shown to match it on skinned landmarks.
   Retain feminine and masculine defaults, feminine first, as styles rather than sex-wide claims. A reviewed subset
   may support representative implementation; final default acceptance needs both actual sources and reference-character
   previews. Missing complete content does not authorise generation or make a representative result complete.

### Output and Validation Boundary

1. The [shared animation contract](../../animation/index.md) and
   [ANIM-002](../../animation/002-locomotive-animation-set/index.md) own runtime role/variant/stage and dynamic-window
   needs. Normal CTRL/NAV load the same current full-source export through `reviewed-feminine/set.json`, without
   source activation switches or a prototype movement fallback. No planted-interval catalogue, stitch table or
   clip-seam repair is required.
2. Retained outputs identify character, source, variant, canonical skeleton, coordinates, units, and provenance
   truthfully.
   Generated data is tool-owned, not hand-edited to conceal mismatches. Unknown or contradictory bindings fail clearly.
3. Stage and validate output before durable mutation; publication is all-or-rollback. Metadata-only changes require
   retained animation, identity, and integrity bindings to remain valid. No partial or stale publication is accepted.
4. Verify selected real content and consumer loading as well as synthetic contract fixtures. Report gaps as gaps,
   not proof of content inadequacy or motion quality. Measure processing costs diagnostically without new numerical
   gates.
5. Do not redesign the broader pipeline as a prerequisite to the actual-rig walk-to-idle demonstration. Record which
   source extraction/conversion path works and which tooling remains unadapted; never label an unexecuted path ready.
6. The minimal MotionBricks export path lives under `tools/kimodo-pipeline/`: `export_motionbricks.py` reads protected
   setup and external-idle inputs without saving or rebaking them and verifies before/after hashes. It publishes the
   canonical skeleton identity, external-idle samples and provenance; it neither exports nor requires handmade
   calibration rows (helper, `LOOK` or `targetRest`), and publication validation does not require them.
   Moving/turn roles use the separate reviewed-source ingestion under KIMODO-PROD-005;
   `export_motionbricks_frames.py` exports named `SOMASkeleton77.global_rot_offsets` from the approved Kimodo
   installation.
   It does not export evaluation motions or replace source-authoritative offsets with USD rests.
7. Export `poses.json` and `model-frames.json` under `game/data/motionbricks/`. `catalogue.json` supplies external idle;
   all eight normal movement roles use `reviewed-feminine/set.json`, binding `landing-ablation-01.json` and its angular
   sidecar. The durable approved source/authoring/descriptor/provenance prefix is
   `game/assets/characters/animations/source/locomotion/locomotion_feminine_landing_ablation_01`.
   Preserve full 1–1200 at 30 Hz chronology and source/action/slot identity. Validate bundle/set/angular metadata
   together before coordinated replacement with consumers stopped and rollback bytes available; per-file atomic
   publication is not multi-file hot publication. Current ranges/cues are bound by
   [ANIM-002](../../animation/002-locomotive-animation-set/index.md#implementation-status) and the
   [source-data guide](../../../game/data/motionbricks/reviewed-feminine/README.md), not duplicated nominal angles.
   Runtime must not depend on diagnostic JSON; independent validation oracles may retain separate fixtures.
8. Document Blender, the approved Kimodo Python environment, source inputs and export commands in the operational
    [MotionBricks export instructions](../../../tools/kimodo-pipeline/README.md#motionbricks-exports).
    Link the separately provisioned [SOMA FP32 model setup](../../../game/models/motionbricks/README.md) and
    [NVIDIA CUDA/cuDNN runtime prerequisites](../../../game/src/Motion/README.md#launch), rather than bundling
    model weights. ANIM TR6 owns ignored root-project weights and worktree sharing; NAV-001 TR2 owns runtime path
    selection and host-qualified launch requirements. Provisioning evidence must distinguish a reused local setup
    from clean-checkout reproducibility.

## In Scope

- Source discovery and preservation, canonical source processing, provenance, licensing checks through
  the shared animation contract, safe output publication, and real consumer validation.
- Existing bootstrap/input lifecycle safeguards and per-character calibration/reuse boundaries.
- Externally authored source/reference inputs, continuous multi-role source requirements and essential integrity checks.
- Reviewed-action ingestion, technical sanitising, role slicing, versioned metadata and failure-safe publication.

## Out Of Scope

- Runtime routing, player movement, corrective foot planting, general action catalogues, and broad pipeline redesign.
- Unattended content acceptance, new animation generation to mask runtime faults, and unapproved setup transplantation.

These exclusions do not defer required idle/walk source verification, conversion, loading, or source protection.

## Acceptance Criteria

### User Requirement Acceptance

1. **KIMODO-UAC-001 (UR1–3):** Reports identify actual idle/walk inputs and missing inputs, preserve source bytes and
   artist approval boundaries, and distinguish source readiness from runtime motion evidence. The maintained
   reviewed-action command ingests externally artist-authored sources without a pipeline generation receipt.
2. **KIMODO-UAC-002 (UR4–5):** Failure checks preserve prior outputs and unrelated character inputs; a delivered output
    loads from a clean checkout using documented prerequisites.
3. **KIMODO-UAC-003 (UR6):** Source/authoring audit proves local reference ownership without an external idle
   dependency.
   Whole-window source/preview evidence and human review assess interior motion independently of boundary agreement.

### Technical Requirement Acceptance

1. **KIMODO-TAC-001:** Tool/source-boundary and source-lifecycle checks verify ownership, protected inputs, provenance,
   baseline integrity, missing-input failures, and coordinated replacement/retirement gates (KIMODO-SRC-001–006).
2. **KIMODO-TAC-002:** Where Kimodo baking/reuse is used, checks verify calibration parity (KIMODO-SET-001–003), actual
   idle motion, explicit character parameters, isolated approvals, and absent-bone reuse safety.
3. **KIMODO-TAC-003:** Output checks verify truthful bindings, rollback, clean loading, real selected-source conversion,
    and the absence of a legacy catalogue prerequisite. The recorded supported path and gaps match executed evidence.
    For MotionBricks, verify read-only export/input integrity, named frame provenance, catalogue/action-window loading
    from runtime data paths and documented external prerequisites. Verify that published data and reviewed-source
    ingestion neither contain nor require helper, `LOOK` or `targetRest` calibration rows while retaining canonical
    skeleton identity, samples, external idle, model frames and provenance. Reused exports or an existing import
    cache alone do not satisfy clean-checkout acceptance.
4. **KIMODO-TAC-004 (KIMODO-PROD-001–006, UR2/4/6):** Verify externally authored source/reference provenance,
   whole-pose boundary placement, multiple intended cycles and predeclared extraction policy, without requiring
   repository source-generation machinery. Test essential identity, format/range and claimed-complete coverage failures,
   not extra misplaced-constraint fixtures. Demonstrate ingestion of a reviewed Blender action without a generation
   receipt, binding approval to hash/action/ranges; explicit range
   revisions need new review. Verify read-only full ordered export, documented sanitising fidelity, traceable slices,
   physical/phase metadata and ordinary/boundary sampling, including truthful non-cyclic handling. Audit complete-cycle
   identification, frozen bounds/period/endpoint convention and experimental-versus-admitted metadata. Raw-cycle trials
   require no exact-seam gate; boundary measurements remain diagnostic. Apply ANIM-002-TAC-001–003 for repeated-wrap
   sampling/placement and actual-rig evidence. Fallback source-boundary processing needs visible generated-harm
   evidence, explicit changes and human review, not mandatory sanitising. Verify atomic content/review publication
   preserves valid outputs on failure and refuses failed/unreviewed sources. Full source
   and reference-character preview evidence under the shared visual authority distinguishes human suitability,
   technical integrity and actual-rig runtime proof.
   A complete variant requires all eight roles; final default acceptance requires both defaults and their previews,
   not merely a representative subset. Independent implementation review remains required.

Technical acceptance 1 covers Tool and Source Boundary and Repository Source Asset Contract; acceptance 2 covers
Character Setup and Calibration Contract; acceptance 3 covers Output and Validation Boundary; acceptance 4 covers
Source Production Contract. NAV-001 owns final playable evidence; source and gameplay suitability remain human-owned.

## Implementation Status

The maintained reviewed-action ingestion and set publisher deliver the complete feminine
`source_feminine_landing_ablation_01` action, with protected durable source/authoring copies, descriptor, approval and
provenance receipts. `reviewed-feminine/set.json` binds every role to `landing-ablation-01.json`; the angular sidecar
uses actual exported headings/FK measurements and current primary/recovery landmarks, including authored reversal.
Source ranges remain non-cyclic and raw repetitions `EXPERIMENTAL_NOT_ADMITTED`; these are quality-admission labels,
not feature switches. No source poses are repaired or regenerated by normal runtime loading.

The [adoption report](../../../game/temp/motionbricks/anim002-regenerated-source-adoption-01/REPORT.md) records
read-only full export, complete eight-role publication, coordinated set/angular replacement and normal consumer checks.
Runtime JSON is self-contained; durable Blender inputs retain host-specific texture references, not a proved portable
texture archive. The source-set migration's technical checks pass; they do not cover removing helper, `LOOK` or
`targetRest` calibration rows from the exporter, publication or ingestion under Output 6 and KIMODO-PROD-005, which
remains unverified. Combined independent review remains pending, and no multi-file
atomic hot-publication, default-entrypoint native-motion, performance or artistic quality certification is claimed.
Human acceptance covers source stepping/foot-sliding mitigation with initial twitch deferred. Raw repetitions,
generated gait/contact/sliding, headset quality, masculine/default content and previews, and known shutdown/hand-origin
risks remain open. Plan steps 5/6 and broader ANIM-002 acceptance remain deferred-with-risk. No new take, retry,
model-call budget, source processing or tuning is authorised by this documentation revision.

## References

- [Project Specifications](../../index.md)
- [Developer Tool Layout](../index.md)
- [Shared Animation Contract](../../animation/index.md)
- [NAV-001: NPC Navigation](../../nav/001-npc-navigation/index.md)
- [Operational README](../../../tools/kimodo-pipeline/README.md)
