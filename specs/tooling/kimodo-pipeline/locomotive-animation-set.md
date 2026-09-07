---
title: Kimodo Locomotive Animation-Set Pipeline
---

# Kimodo Locomotive Animation-Set Pipeline

## Requirement

Define the Kimodo pipeline that bootstraps ANIM-002 authoring workspaces, materialises packages, and produces
reviewable evidence without becoming a locomotion-policy or authored-content oracle.

## Goal

Technical artists can turn a complete user-supplied retarget setup into a character-visible workspace and processed
package, while deterministic fixture declarations provide all motion/content expectations for automated validation.

## Tool Ownership And Usage Boundary

Kimodo owns the manually invoked bootstrap, materialisation, publication, and validation workflow under
`tools/kimodo-pipeline/`. Commands, configuration, libraries, external GDScript, and tests are colocated with the tool.
[The operational README][readme] is normative for repository-root invocation, prerequisites, destructive boundaries,
and focused verification. It supplements, rather than replaces, this specification's delivery contracts.

### Executable Command Inventory

Run all commands from the repository root. The supported Python entry points are:

- `commands/retarget_animations.py` — Blender-hosted retargeting; invoke through Blender, not `python3`.
- `commands/generate_contact_evidence.py` — creates contact evidence in a caller-supplied disposable processed stage.
- `commands/prepare_validated_publication_manifest.py` — creates a two-package manifest from validated stages without
  changing production roots.
- `commands/publish_validated_packages.py` — **destructive publication command**; publishes only a validated manifest
  through the journalled transaction.

The external Godot commands are `godot/commands/configure_kimodo_import.gd`,
`godot/commands/package_kimodo_runtime.gd`, and `godot/commands/validate_kimodo_runtime_package.gd`. Invoke each as
`godot-mono --path game --script "$PWD/tools/kimodo-pipeline/godot/commands/<script>.gd" -- <arguments>`. User
arguments (`--descriptor=<descriptor.json>` or `--package=<locomotion|idle>`) must follow that `--` separator: the
scripts read `OS.get_cmdline_user_args()`, so arguments before the separator are ignored and the package silently
defaults to `locomotion`. The absolute tool-owned script path preserves `game` as `res://`; external tooling has no
`.gd.uid` sidecar.

## User Requirements

1. Technical artists can bootstrap and audit a character-present locomotion workspace without rebuilding generation
   infrastructure manually.
2. A supplied retarget setup remains unmodified; invalid inputs receive actionable diagnostics rather than in-place
   repair or recalibration.
3. Failed or interrupted stages leave no partial artefacts and preserve the prior state.
4. Artists author prompts and audit Kimodo generation themselves; no stage automates generation or its acceptance.
5. Artists can review package provenance and evidence without an unreliable content-coupled test suite blocking work.
6. Artists can republish corrected runtime metadata from unchanged validated package bytes without rerunning destructive
   or provenance-changing animation stages.
7. A fresh clone of the repository imports and loads the published runtime packages without any regeneration,
   repackaging, or republishing step.

## Technical Requirements

### Content-Independent Test Boundary

1. The following volatile core assets may be read only for readability, structural binding, path derivation, and
   immutability:
   - `game/assets/characters/animations/source/locomotion/locomotion.blend`;
   - `game/assets/characters/animations/source/locomotion/locomotion_template.blend`; and
   - `game/assets/characters/reference/female/animations/source/kimodo_retarget_setup.blend`.
2. Tests must not derive an expected authored action, frame range, pose, Root delta, contact, known hash, variant, or
   current content inventory from those files. An opaque before/after comparison may establish immutability, but cannot
   assert an expected authored hash. The `sad` variant and `loc_sad_*` identities and timings remain authored content,
   not test oracles. All motion and content expectations originate in deterministic synthetic fixture declarations.
3. Production scenes may be loaded only for unavoidable narrow composition wiring. Behavioural integration tests must
   prefer dedicated bare scenes or fixture-owned packages so animation coupling and backend activation do not affect
   their result.
4. ANIM-002's role and lifecycle vocabulary is an allowed schema contract. It does not author a production action,
    contact, timing, or package inventory.

### Tool Boundary

1. Kimodo owns its manually invoked commands, command-owned implementation, configuration, tests, and fixture helpers.
2. Kimodo owns externally invoked Godot scripts for its workflow but does not own a Godot project root. Godot runs with
   `game` as `res://` and the externally owned scripts preserve that project context.
3. Physical Kimodo tool, test, configuration, and asset names omit `anim002`. The compatibility-sensitive provenance
   identifiers listed in **Directional Seed Provenance** intentionally retain that token; they are not physical names.

### Package Paths And Descriptor Semantics

1. Processed packages are rooted at `[character asset root]/animations/processed/locomotion` and `.../idle`. Every
   processed index, metrics artefact, publication descriptor, and provenance artefact within a processed root is
   variant-scoped: its path and declared identity include the character, set, and variant it belongs to, so publishing
   one variant can never overwrite or alias another variant's artefacts. Variant-agnostic package-wide metadata slots
   are prohibited.
2. Runtime packages are rooted at `[character asset root]/animations/runtime/<set>/<variant>`. For the delivered
   Reference Female `sad` variant the roots are
   `game/assets/characters/reference/female/animations/runtime/locomotion/sad` and
   `game/assets/characters/reference/female/animations/runtime/idle/sad`. The locomotion package contains
   `locomotion_<variant>.tres`, `locomotion_<variant>_catalogue.json`, and `authored_per_foot_contacts_v1.json`; the
   idle package contains `idle_<variant>.tres` and `idle_<variant>_catalogue.json`.
3. Both packages retain a durable, variant-scoped runtime package descriptor declaring its character, set, variant,
   package identity, and generation. The locomotion descriptor requires contact evidence, its input/runtime path,
   package identity, generation, and hash bindings. The idle descriptor explicitly declares contact-free semantics,
   binds only package artefacts, and contains no contact evidence or contact artefacts.
4. Until multi-variant publication is ratified, manifest preparation and preflight reject a second variant for the
   same character and set before any production mutation.

### Embedded-Only Runtime Packaging

1. The runtime packager publishes animations only as embedded members of the package `AnimationLibrary`. It must not
   emit standalone runtime clip resources, stage or publish a clip directory, or transport clip file paths. The staged
   and published catalogue schema contains no animation file-path fields.
2. Processed authoring import sidecars must disable animation externalisation: `save_to_file` externalisation is
   disabled globally and for every action. Preflight rejects an externalising or contradictory import configuration,
   global or per-action, before staging, so publication tooling cannot recreate standalone clips.
3. Runtime resolution follows the normative `(library resource, animation identity)` contract in
   [ANIM-002](../../animation/002-locomotive-animation-set/index.md#full-catalogue-delivery-contract). Packager output
   must not depend on serialised `SubResource` IDs or pre-existing UID registration, and a saved library must reload
   without external clip dependencies.
4. Publication output is clean-clone reproducible: a fresh clone that imports the project loads every published
   package without regeneration. Validation proves this from an isolated cold-import project or cache.
5. AnimationPlayer mounted-library slot names `locomotion` and `idle` are gameplay namespaces owned by consumer
   wiring; packaging must not derive, rename, or invalidate them from library filenames or variant names.

### Validated Publication Manifest

1. A publication manifest has schema version 1, exactly two named package plans, and optional declared legacy roots.
   Each plan supplies `name`, `mode`, `processed_stage`, `runtime_stage`, `processed_root`, `runtime_root`,
   `preflight_descriptor`, and `final_descriptor`.
2. `locomotion` uses `contact-evidenced-locomotion`; `idle` uses `contact-free-idle`. A mode/name mismatch, missing or
   unknown mode, malformed descriptor, missing stage input, incompatible contact declaration, or failed preflight
   refuses publication before production mutation.
3. The publisher validates both disposable stages and both descriptors, then journals replacement and rollback across
   both processed and runtime roots. Any staging, validation, replacement, reload, or cleanup failure restores the
   immediate pre-transaction bytes and fails closed. Success removes transaction staging, backups, and journal data.
4. Prepare manifests and evidence only in disposable staging locations. Use the publisher only after staged validation
   and explicit review of its roots; it is the production-safe path and must not be used as a standalone partial-package
   publication mechanism.
5. Each processed stage must contain its content-owned `provenance/stitch-release-intent-v1.json`. Generic manifest
   preparation strictly validates and transports this declaration; it must not invent edges from opaque current role
   identifiers. Missing or malformed declarations fail before descriptors are written. Locomotion content declares its
   intended boundary-stitch edges, covering both release and acquire event kinds across entry and exit boundaries,
   while idle declares an empty edge list.
6. A metadata-only publication may copy the current processed locomotion and idle roots and required retained contact
   evidence byte-for-byte into `game/temp`, then configure/import/package and atomically republish runtime metadata and
   both package roots. It must not invoke repair, materialisation, retargeting, or contact generation. Both packages
   participate even when idle semantics do not change. Full provenance regeneration is mandatory if processed animation,
   index, metrics, contact evidence, source/action provenance, retarget binding, or carrier bytes change or fail
   validation.
7. Every runtime-packager invocation must supply exactly one explicit disposable `--stage-root`. Before removing,
   creating, or replacing any directory, the packager rejects a missing, empty, malformed, or unsafe stage root. A
   valid stage root is a non-symbolic-link `res://temp` descendant ending in `runtime/<set>/<variant>` for the
   declared package; it must differ from the descriptor's durable root. Packaging only prepares and validates this
   disposable runtime stage. The journalled publisher is the only command permitted to replace durable processed or
   runtime roots.

### Runtime Capability Publication

1. Each processed action declares `runtime_capability`, and publication copies it to the runtime catalogue clip's
   required `capability` field. The processed index retains its implemented schema version 2 with `runtime_capability`
   as an additive required field; the runtime catalogue is schema version 3, bumped from version 2 because per-clip
   resource-path fields were removed for embedded-only packaging.
2. The closed vocabulary, semantic meanings, compatibility rules, and fail-closed behaviour are the normative
   [ANIM-002 Runtime Capability Contract][runtime-capabilities]. The pipeline must reject a missing or unknown
   capability. Publication and runtime preflight must reject duplicate capabilities where one-to-one dispatch is
   required and reject capabilities incompatible with declared state, boundary, loop, phase, or stitch semantics.
3. Role and identity remain opaque, unique package identifiers. No pipeline, validator, player or NPC consumer, or
   runtime installer may derive direction, transition intent, anatomical side or phase lane, reversal, behaviour, or
   capability from identifier text, suffix, declaration order, a fixed inventory or count, `sad`, `loc_sad`, or an
   asset filename.
4. Player, NPC, and installer selection uses capabilities and package-declared phase and stitch metadata. Renaming or
   reordering opaque roles and identities must not alter dispatch. Missing required consumer capabilities fail closed.
5. Capability validation remains package-generic. Synthetic declared fixtures are the behavioural oracle; current role
   strings, identities, asset filenames, variants, and package inventory are not.

### Contact Evidence Packaging

1. The pipeline owns bootstrap, materialisation, and evidence recording; ANIM-002 exclusively owns permission.
2. **Descriptor-Bound Evidence:** The locomotion descriptor declares `contact_evidence` with `input`, `runtime_path`,
   `package_identity`, and `generation`, and fails closed when it is absent. The idle descriptor explicitly declares
   contact-free semantics and carries no contact declaration or contact artefact. A locomotion declaration validates
   the explicit upstream artefact before staging; it is not a request to create evidence.
3. **Explicit Evidence Only:** Contact evidence source provenance uses schema v5 and phase-contact policy schema v3. A
    published lifecycle record uses the canonical `AuthoredPerFootContactSampleV1` field names:
    `schema_version`, `role`, `foot`, `lifecycle`, `timing`, `validity`, `confidence`, `provenance`, `clip`, `variant`,
    `reference`, `identity`, and `generation`. The `clip` field is the animation identity within the package's embedded
    library, never a clip file path. A live role, clip, phase, foot, identity, and generation select explicit
    `Acquire`, half-open `Support [start, end)`, and `Release` data. The pipeline must not infer a contact from omitted
    data, a clip name, a pose, release metadata, a solver or terrain result, or downstream behaviour. Declared data are
    preserved through publication; per-physics fields are derived later by ANIM-002 and cannot alter the source record.
4. **Generic Self-Consistency Validation:** Schema v5 carries complete lineage and input digests, lifecycle coverage,
   confidence components, candidate and rejected alternatives, the role-foot fallback matrix, mapping and configuration
   identities/digests, package identity/generation, and carrier references. A declared artefact is valid only when its
   package identity/generation, processed-index role/identity/variant mapping, catalogue clip bijection, and diagnostic
   provenance agree. The published evidence path, descriptor declaration, catalogue declaration, and evidence identities
   must agree after reload. Package validation rejects every member of the
   [ANIM-002 Refusal Set](../../animation/002-locomotive-animation-set/index.md#canonical-contact-evidence), including
   missing, malformed, stale, and mismatched evidence. It fails closed for package-derived `boundary_kind`, root
   `contact_evidence`, `phase_events.duration_seconds`, evidence metadata, timeline and event ownership, contact
   declarations, and stitch mappings. The producer and parser derive inventory, counts, roles, mappings, boundary kinds,
   and stitch relationships from supplied package data, not a hard-coded current inventory, variant, count, role name,
   or hash. Gameplay consumers declare required capabilities instead of relying on parser-global role assumptions.
   Release timing is scheduling/provenance only and cannot admit contact. A legacy production
   `stitch_mappings: inapplicable` value is not a contract for a fresh package.
5. **Transactional Publication And Consumption:** The pipeline stages validated declared evidence with the package
     library and catalogue. Prepared evidence is retained at an explicit descriptor-bound, non-disposable location
     within the transaction; it must never be under `game/tests`, `game/temp`, or a test-results path. Processed
     package, runtime library/catalogue, contact evidence, and provenance are published as one logically atomic,
     journalled, all-or-rollback transaction across both package roots. Both staged packages undergo preflight and
     reload validation before either production root is mutated; each fresh package is also preflighted against its
     generic consumers. Backups restore the exact immediate-pretransaction bytes of both roots after staging,
     validation, first-root, second-root, reload, or cleanup failure. Success removes the journal, staging, and
     backups. This does not claim OS-level atomic multi-directory rename. A failed validation or publication writes no
     partial package and removes stale prior evidence through replacement. The generic package validator consumes the
     descriptor-bound publication; the ANIM-002 provider consumes published evidence. The pipeline does not decide
     provider policy, navigation outcomes, or player input.
     Changing permission or contact semantics requires an ANIM-002 amendment, not a tooling metadata change.
6. **Directional Seed Provenance:** Directional phase selection is an immutable, bounded semantic seed, not contact
   evidence. The pipeline keeps `anim002_source_directional_phase_events_v1`, `anim002_event_derivation_v1`,
   `anim002_post_repair_directional_phase_events_v2`, the displacement/neighbourhood contract
   `anim002_post_repair_phase_seed_displacement_v1`, and
   `anim002_selected_loop_repair_provenance_v2` distinct and digest-linked. Old, incomplete, stale, mixed-generation,
   or cross-carrier lineage fails closed.
7. **Derivation And Sampling:** Mirror changes anatomical identity only. Reverse maps time, `Release` to `Acquire` and
   `Acquire` to `Release`, and finite `start` to `end` and `end` to `start`. Combined derivations use one canonical
   deterministic composition. A cyclic endpoint remains a duplicated conceptual sample, while event canonicalisation
   and physical detection use the one-fewer unique runtime samples.
8. **Relative-Motion Refinement:** Native-cadence evaluated foot motion relative to Root, Hips, and shin refines contact
   boundaries only inside topology-, role-, foot-, and seed-authorised sectors. Idle-to-travel roles own `Release`;
   travel-to-idle roles own `Acquire`. Boundary-censored partial finite lifecycles are valid and must not gain an absent
   acquire or release endpoint.
9. **Physical-First Basin Selection:** For cyclic paths, refinement first selects the strongest coherent physical
   transition basin inside the authorised sector and fixed polarity. Seed proximity ranks only within that basin.
   Out-of-sector, wrong-polarity, non-separable, or genuinely ambiguous basins refuse. Persistence-aware transition
   quality, basin uncertainty, confidence components, and minimum confidence `0.8` are versioned contracts. Absolute
   world slide contributes diagnostics and uncertainty only, never admission.
10. **Declared Applicability And Runtime Selection:** Policy schema v3 declares each role-foot path as `required`,
    `optional`, or `fallback_only`. The current generated result has 20 applicable paths, comprising 16 required and
    four optional paths, and 24 explicit fallback paths. Current counts and identities are informative, not evergreen
    test oracles. Precision steps, pivots, and undeclared opposite feet fall back. Runtime selection uses the current
    catalogue role, clip, foot, phase, package identity, and generation; supports cyclic wrap and finite partial
    lifecycles; and never infers from suffix, name, order, or count. Package replacement revokes prior authority.

### Locomotion Authoring Bootstrap

1. **Staged Authoring Artefacts:** The monolithic authoring blend
   `game/assets/characters/animations/source/locomotion/locomotion.blend` is replaced as authoring source of truth by:
   - **Canonical Kimodo Source** — `game/assets/characters/animations/source/kimodo_source.blend`: a clean shared
     `Kimodo_Source` rig with no character, `*_RET` helpers, constraints, or generated actions.
   - **Neutral Locomotion Template** —
     `game/assets/characters/animations/source/locomotion/locomotion_template.blend`: character-neutral Kimodo
     infrastructure with IK/waypoints, pose-reference structure, semantic segment ranges, and blank prompt fields; no
     character, retarget binding, or variant output.
   - **Character Retarget Setup** — `[character asset root]/animations/source/kimodo_retarget_setup.blend`: the complete
     user-supplied input with the target character, visible mesh, Kimodo source rig, calibrated `*_RET` helpers, target
     constraints, and library overrides. It is consumed as-is.
   - **Variant Authoring Workspace** —
     `[character asset root]/animations/source/locomotion/locomotion_[variant]_authoring.blend`: the generated,
     character-present generation and audit workspace.
    - **Processed Package** — `[character asset root]/animations/processed/locomotion/locomotion_[variant].blend`: the
      runtime-facing baked package.
2. **One-Time Extraction:** The pipeline performs one extraction from the monolithic blend into the canonical source,
   neutral template, and reference Female retarget setup. This is the only occasion on which the pipeline creates a
   retarget setup. Future character setups are complete user-provided files; no stage subsequently requires the
   monolithic blend.
3. **Bootstrap Inputs And Validation:** A bootstrap takes exactly a character setup blend, variant name, idle blend
   path, and idle action name. It validates the complete-setup contract; a rig-compatible, single-frame Kimodo-space
   idle action; and blank segment prompts.
4. **Workspace Generation:** Starting from the supplied setup, bootstrap merges template infrastructure without
   duplicating `Kimodo_Source`, copies the validated idle as `pose_ref_idle`, and creates rotated pose references under
   extracted-template conventions. Prompts stay blank for Kimodo UI entry, the character stays present for audit, and
   generation remains a human-audited Kimodo add-on action.
5. **Setup Immutability:** Every retarget setup is validate-only. No stage may create, alter, or recalibrate `*_RET`
   helpers, calibrated poses, target constraints, or library overrides. Rejections identify the unmet contract.
6. **Transactional Behaviour:** Every stage writes to a temporary location, validates structurally and after reload,
    then moves output to its final path. Package publication follows the two-root journalled transaction in
    **Transactional Publication And Consumption**. Failure or interruption leaves prior artefacts unchanged and no
    partial blend, package, index, or evidence output.
7. **Materialisation Carrier Binding:** `kimodo_materialisation_carrier_binding_v1` binds the repair report to the
   immutable precursor path and hash. Materialisation operates on a copy and records distinct precursor, final staged,
   selection-evidence, source-repair-report, and per-Action provenance carriers. The retarget index binds the final
   carrier and Action provenance digest. No stage claims precursor and final bytes are identical.
8. **Canonical Bake Target:** Reference Female is the initial canonical authoring and bake target. Other characters use
   Godot skeleton-profile retargeting of the shared Female bake. Character-specific direct bake is a future
   evidence-driven extension. The Female flattened-path migration and paired Godot consumer update are sequenced
   delivery phases, not exclusions.
9. **Structural Validation:** Generated blends are structurally validated after generation and reload for expected rigs
   and actions, exactly one `Kimodo_Source`, the character, single-frame `pose_ref_idle`, rotated pose references,
   blank prompts, and no drift from supplied bindings, constraints, or overrides.
10. **Direct Automated Staged Verification:** Materialise-and-reopen validation covers role semantics, anatomy, contact
   safety, path, heading, hierarchy closure, and native cadence. The full test suite remains a sequenced final
   automated phase. Both use deterministic synthetic expectations under the content-independent boundary and do not
   prove subjective player-perceived naturalness.

### Candidate Selection and Seam Repair

1. **Source-Agnostic Enumeration:** For every cyclic role, materialisation independently enumerates each
   phase-qualified adjacent recurrence window of the required anatomical foot. Source names, source order, generation
   order, and prior selection cannot establish quality. Phase-incompatible windows may be diagnostic but cannot admit.
2. **Selection Precedence:** Selection applies, without later cure: semantic/phase eligibility and direct
   anatomy/contact safety; deterministic cycle/body/support/path/heading quality; repaired-output smoothness,
   correction magnitude, two-ended balance, and evaluated-hierarchy native-cadence continuity; then deterministic
   content identity as final tie-break.
3. **Direct Safety And Diagnostics:** Fore-aft span, lateral width, crossover/separation, relative height, and peer
   outliers are diagnostic unless they directly prove unsafe anatomy for the role family. Direct limb, contact,
   support, torso/carriage, path, heading, and final-recurrence evidence remains blocking. A diagnostic cannot cure a
   failure or veto safe motion solely by differing from peers.
4. **Straight-Directional Candidate Correction:** For a straight directional role, raw evaluated-Hips orthogonal drift
   is candidate-local path drift, not by itself unstable or reversing heading. A candidate-local, role-relative,
   bounded orthogonal Hips correction may preserve signed intended progress, then re-evaluate the candidate against the
   unchanged path and heading gate. It is not a threshold relaxation: direct hierarchy, anatomy, contact, topology, and
   repair-budget requirements remain mandatory, and genuinely reversing travel remains rejected.
5. **Adaptive Two-Ended Repair:** Repair is bounded, duration-scaled, pose-local, and two-ended. It evaluates bounded,
   non-overlapping support widths and blend profiles, then deterministically selects safe output by continuity,
   correction magnitude, opening/closing balance, evaluated-hierarchy native cadence, support width, and profile name.
   The `evaluated_hierarchy_native_cadence_v1` provenance records generated-Root-relative pelvis, lower-leg, and foot
   landmarks at seam-adjacent native-cadence offsets; normalised per-landmark step, acceleration, jerk, and reversal
   evidence; anatomical scale; and native frame rate. Cadence ranks otherwise-safe options only. It sets no fitted
   rejection thresholds.

   Repair preserves an immutable interior exactly, reconstructs the real hierarchy with one-time Root ownership,
   provides C0/C1 closure, retains direct safety, and preserves repeated-cycle composition. Durable provenance fields
   `windows` and `immutable_interior` must describe the selected adaptive width and its resulting immutable interval,
   never the default repair width. Support widths, profiles, and safety budgets remain tunable within these invariants.
6. **Raw Mismatch:** Raw endpoint mismatch is diagnostic when bounded applied correction and final output prove closure,
   recurrence, anatomy, contact, and semantic continuity. Assessment measures actual correction rather than a blanket
   raw-mismatch threshold.
7. **Repaired Pivot Admission:** A repaired pivot may use repaired recurrence only with retained raw provenance and
   evidence of bounded correction, anatomy/contact preservation, zero Root translation, signed 45-degree unit
   accumulation, no reset, and correct three-cycle composition. Missing, stale, or incomplete provenance blocks it.
8. **Mirror Invariant:** Spatially mirrored hierarchy data applies both armature-space spatial transform and bilateral
   bone identity in the output-role frame before any path, seam, contact, anatomy, cadence, or body-support assessment.
   Applying only one is invalid.

## In Scope

- One-time extraction, bootstrap, workspace generation, structural validation, setup immutability, and transactional
  delivery.
- Source-agnostic selection, direct safety classification, adaptive repair, provenance, and direct evaluated-output
  validation for applicable role families.
- Flattened processed-package delivery, Reference Female migration, and paired Godot consumer update as sequenced
  phases.
- Embedded-only runtime packaging at character-owned, variant-scoped roots, including variant-scoped descriptors,
  metrics, and provenance.
- Descriptor-bound contact-evidence publication, validation, and ANIM-002 pre-IK hand-off.
- Journalled processed/runtime publication, retained evidence provenance, generic catalogue validation, and consumer
  preflight.
- Deterministic synthetic fixtures for all motion and content expectations, including non-visual failure cases.
- Kimodo's command, implementation, configuration, test, and README boundary.

## Out Of Scope

- Contact permission ownership, terrain probing, runtime IK, navigation, player control, and correction policy.
- Ratifying current authored production content or treating a `sad` variant, `loc_sad_*` identity, timing, or inventory
  as a test oracle.
- Creating, altering, or recalibrating retarget setups after one-time reference Female extraction.
- Automated/unattended generation or prompt authoring; artists enter prompts and audit generation.
- Character-specific direct bakes beyond Reference Female.
- Publishing additional variants beyond the delivered single variant per character and set; the second-variant
  rejection contract remains normative until multi-variant publication is ratified.
- Screenshot, photobooth, GLB, contact-sheet, manual visual review, and visual-capture verification.

These exclusions do not defer mandatory extraction, bootstrap, workspace, materialisation, repair, package, contact
publication, cross-package transaction, embedded-only runtime packaging, variant-scoped metadata, clean-clone
reproducibility, reopen, runtime catalogue, consumer, or non-visual delivery validation.

## Acceptance Criteria

### User Requirement Acceptance

1. An artist bootstraps a character-present workspace from the setup blend, variant name, idle blend path, and idle
   action name, then enters prompts through the Kimodo UI and audits generation.
2. Invalid setup or idle input returns actionable diagnostics without modifying supplied files; failed or interrupted
   stages leave prior artefacts intact and no partial output.
3. Package reviewers can trace synthetic fixture evidence and production structural provenance without treating current
   production animation as an expected-content oracle.
4. A cold-import check proves a fresh clone loads both published packages without any regeneration, repackaging, or
   republishing step.

### Technical Requirement Acceptance

1. Tests prove production core assets are read only for readability, structural binding, path derivation, and
   immutability. They prove no expected action, frame range, pose, Root delta, contact, known hash, variant, timing,
   identity, or inventory is derived from them.
2. Behavioural integration tests use bare scenes or fixture-owned packages. Any production-scene test demonstrates its
   narrow composition-wiring need and avoids animation-coupled backend activation.
3. Synthetic declared-fixture tests validate package-derived roles and capabilities; all five directional provenance
   contracts and digests; deterministic mirror/reverse composition; unique cyclic sampling; finite polarity and partial
   lifecycles; physical-first basin selection; the `0.8` confidence gate; declared applicability and fallback; runtime
   phase selection; and ANIM-002-only permission ownership. They refuse out-of-sector, wrong-polarity, non-separable,
   ambiguous, old, incomplete, stale, mismatched, and cross-carrier evidence. Package replacement removes stale
   authority. Generic schema fixtures cover valid, malformed, unknown, duplicate, and non-finite declarations for the
   boundary, root contact evidence, phase duration, evidence metadata, event ownership, contacts, and stitch mappings.
   Capability fixtures retain processed-index schema version 2 and exercise runtime-catalogue schema version 3 while
   requiring `runtime_capability` and `capability`. They use arbitrarily renamed and reordered opaque roles and
   identities, prove player, NPC, and installer dispatch from capability plus declared phase/stitch metadata, and
   reject missing, unknown, duplicate, or semantically incompatible capabilities.
4. Extraction and bootstrap checks prove the canonical source/template/setup contracts; exactly one `Kimodo_Source`;
   present character; pose references; blank prompts; reload validation; flattened path; and the sequenced Female
   migration and Godot consumer update.
5. Immutability and transactional checks prove no protected setup binding, helper, pose, constraint, override, source,
   or published asset changes, and no failed stage leaves partial output.
6. Synthetic declared candidate fixtures enumerate eligible and diagnostic windows and prove action-name, source-order,
   generation-order, and mirror independence; selection precedence; direct-safety blocking; and diagnostic-only proxy
   limits. They admit bounded candidate-local orthogonal Hips drift only after it satisfies the unchanged path gate,
   preserve signed intended progress, and reject a genuinely reversing path.
7. Synthetic repair fixtures exercise bounded duration-scaled options, C0/C1 closure, Root ownership, immutable
   interiors, anatomy/contact preservation, repeated-cycle composition, native cadence, raw-mismatch handling, pivots,
   and mirrored output frames.
8. An adaptive repair fixture selects a non-default safe width and verifies durable `windows` and
   `immutable_interior` provenance report that selected width and interval exactly, rather than default-width values.
9. Materialise-and-reopen, package, corruption, runtime, and consumer coverage remains required and uses fixture-owned
     expectations for role semantics, anatomy, contact safety, path, heading, hierarchy closure, cadence, carriage,
     pivot accumulation, seam closure, and repeat-cycle composition. Generic transaction and validator paths consume
     staged and published declared evidence, verifying reload, published path, identity, generation, package-index
     mapping, catalogue-to-library identity bijection, provenance, and provider consumption. Synthetic fixtures cover
     normal commit; staging and validation rollback; first- and second-root rollback; stale cleanup; provenance
     survivability; and cleanup rollback, with exact immediate-pretransaction bytes restored for both roots. Carrier
     tests bind immutable precursor and repair-report hashes to distinct final, selection, Action-provenance, and
     retarget-index carriers without claiming byte identity. They also preflight a fresh package against
     consumer-declared capabilities. This does not prove subjective naturalness.
10. Tooling checks confirm that Kimodo commands, implementation, configuration, tests, fixture helpers, and external
     GDScript are colocated under `tools/kimodo-pipeline/`; external scripts retain `game` as `res://`, and the README
     documents supported commands, destructive publication, staging, and required environment.
11. Publication checks cover both declared modes, descriptor semantics, manifest rejection, preflight refusal, normal
     commit, and all rollback points. They prove that locomotion retains hash-bound contact evidence and idle carries no
     contact declaration or contact artefact.
12. Package-intent checks reject missing and malformed declarations, transport synthetic opaque role edges, and preserve
     empty idle intent. Metadata-only repair evidence proves processed animation artefact hashes are unchanged across
     atomic publication and that only declared runtime metadata, runtime assets, and integrity descriptors change.
13. A synthetic runtime-packager regression proves missing, empty, malformed, and unsafe stage roots fail without
     changing the declared production root, while a valid explicit disposable stage ending in
     `runtime/<set>/<variant>` is prepared successfully.
14. Embedded-only packaging regressions prove staged and published packages contain the library and catalogue, plus
     contact evidence for locomotion, and no standalone clip resources or clip directory; the staged catalogue schema
     carries no animation file-path fields; and a saved library reloads without external clip dependencies.
15. Preflight regressions prove import configurations that enable `save_to_file` externalisation, globally or
     per-action, are rejected before staging, and that no publication path can recreate standalone clips.
16. Variant-scoping regressions prove every processed index, metrics artefact, descriptor, and provenance artefact
     carries its character/set/variant identity, and a second-variant publication attempt for the same character and
     set is refused before production mutation without overwriting first-variant metadata.
17. Cold-import checks prove an isolated fresh project imports and loads both published packages completely, with no
     dependence on pre-existing UID registration, local import caches, or editor state.

### Acceptance Traceability

- User Requirements 1–4 and 6 → User Requirement Acceptance 1–2 and Technical Requirement Acceptance 4–5, 11–12.
- User Requirement 5 → User Requirement Acceptance 3 and Technical Requirement Acceptance 1–3, 6–9.
- User Requirement 7 → User Requirement Acceptance 4 and Technical Requirement Acceptance 17.
- Content-Independent Test Boundary → Technical Requirement Acceptance 1–3.
- Tool Boundary → Technical Requirement Acceptance 10.
- Package Paths And Descriptor Semantics → Technical Requirement Acceptance 9, 11, 16.
- Embedded-Only Runtime Packaging → Technical Requirement Acceptance 14–17.
- Validated Publication Manifest → Technical Requirement Acceptance 9, 11–13, 16.
- Runtime Capability Publication → Technical Requirement Acceptance 3 and 9.
- Contact Evidence Packaging → Technical Requirement Acceptance 3, 9.
- Locomotion Authoring Bootstrap → Technical Requirement Acceptance 4–5, 9.
- Candidate Selection And Seam Repair → Technical Requirement Acceptance 6–9.

## References

- [Project Specifications Index](../../index.md)
- [Developer Tool Layout](../index.md)
- [ANIM-002: Locomotive Animation Set](../../animation/002-locomotive-animation-set/index.md)
- [Contact-Lock Coordinator Contract](../../ik/003-leg-feet-ik/contact-lock-coordinator-contract.md)
- [Operational README][readme]

[runtime-capabilities]: ../../animation/002-locomotive-animation-set/index.md#runtime-capability-contract
[readme]: ../../../tools/kimodo-pipeline/README.md
