---
id: ANIM-002
title: Locomotive Animation Set
---

# Locomotive Animation Set

## Requirement

Define ANIM-002's locomotive schema, contact-permission ownership, content-independent validation boundary, and
source-to-runtime delivery contract.

## Goal

NPC and player locomotion can consume the same authorised contact schema while implementation and test validation remain
reliable when volatile authored animation content changes.

## User Requirements

1. Locomotion preserves the intent of valid authored contacts without allowing invalid evidence to enable contact lock.
2. NPC and player locomotion use the same contact truth while retaining independent movement policies.
3. Finite exits preserve their declared endpoint semantics without a false cyclic acquire at the endpoint.
4. Technical artists can deliver a complete locomotive package through a repeatable, non-visual validation process.
5. Intentional straight locomotion is not rejected solely because an otherwise valid candidate has bounded orthogonal
   Hips drift; genuinely reversing travel remains rejected.
6. Technical artists can correct runtime publication metadata without changing already validated animation or contact
   source data when the retained package bytes and provenance remain valid.

## Technical Requirements

1. **Permission Ownership:** ANIM-002 solely owns contact permission. Variant, IK, NAV, CTRL, and tooling documents
   must not duplicate, override, infer, or relax it.
2. **Package-Declared Role And Capability Schema:** Each processed action declares `runtime_capability`, and each
   runtime catalogue clip declares the corresponding `capability`. Each package also declares its role inventory,
   mappings, boundary kinds, loop intent, phase events, stitch relationships, and role-foot contact applicability. The
   generic catalogue parser derives those values from package data and must not contain a parser-global `sad`,
   `loc_sad`, current-role, identity, order, count, or fixed-inventory oracle. Gameplay consumers declare the
   capabilities they require and reject a package that does not provide them. The normative vocabulary and validation
   rules are defined in **Runtime Capability Contract** below. `Acquire`, `Support`, and `Release`, anatomical
   left/right feet, and finite/cyclic clocks remain schema vocabulary.
3. **Content-Independent Test Boundary:** The following volatile core assets may be read only for readability,
   structural binding, path derivation, and immutability:
   - `game/assets/characters/animations/source/locomotion/locomotion.blend`;
   - `game/assets/characters/animations/source/locomotion/locomotion_template.blend`; and
   - `game/assets/characters/reference/female/animations/source/kimodo_retarget_setup.blend`.

   Tests must not use those assets as an oracle for authored actions, frame ranges, poses, Root deltas, contacts,
   known hashes, variants, or current content inventory. An opaque before/after comparison may establish immutability,
   but cannot assert an expected authored hash. The `sad` variant and `loc_sad_*` clip identities and timings are
   authored content, not test oracles. Every motion or content expectation must originate in a deterministic synthetic
   fixture declaration owned by the test. Production scenes may be loaded only for unavoidable narrow composition
   wiring; behavioural integration tests use bare scenes or fixture-owned packages to avoid animation coupling and
   backend activation.
4. **Directional Phase Lineage:** Directional phase evidence is an immutable, bounded semantic seed for contact
   refinement; it is not contact evidence. Publication keeps these distinct, versioned provenance contracts:
   `anim002_source_directional_phase_events_v1`, `anim002_event_derivation_v1`,
   `anim002_post_repair_directional_phase_events_v2`, the displacement/neighbourhood contract
   `anim002_post_repair_phase_seed_displacement_v1`, and
   `anim002_selected_loop_repair_provenance_v2`. Their digests and cross-links must agree.
5. **Directional Transform Semantics:** Mirroring changes anatomical identity only. Reversal maps time,
   `Release` to `Acquire` and `Acquire` to `Release`, and finite `start` to `end` and `end` to `start`. Combined
   transforms compose in a canonical deterministic order. A cyclic endpoint is a duplicated conceptual sample; event
   canonicalisation and physical detection operate on the one-fewer unique runtime samples.
6. **Contact Refinement:** Evaluated foot motion relative to Root, Hips, and shin refines `Acquire`, `Support`, and
   `Release` only inside sectors authorised by topology, role, anatomical foot, and directional seed. The exact
   transition role owns finite polarity: idle-to-travel is `Release`; travel-to-idle is `Acquire`. A boundary-censored
   finite lifecycle may validly omit the endpoint outside the clip or authorised sector; the producer must not invent
   it.
7. **Physical-First Cyclic Admission:** Within the authorised sector and fixed polarity, cyclic refinement first chooses
   the strongest coherent physical transition basin. Seed proximity ranks candidates only inside that basin. A basin
   outside the sector, with the wrong polarity, without separable support/swing states, or with genuine ambiguity is
   refused. Persistence-aware transition quality, basin uncertainty, confidence components, and the `0.8` minimum
   confidence are versioned implementation contracts. Absolute world slide is diagnostic and uncertainty evidence only;
   it cannot admit or veto contact.
8. **Applicability Policy:** Contact applicability is declared per role-foot by phase-contact policy schema v3 as
   `required`, `optional`, or `fallback_only`. The current generated package yields 20 applicable paths, comprising 16
   required and four optional paths, plus 24 explicit fallback paths. These counts and current identities describe the
   present generated result, not an evergreen test oracle. Precision steps, pivots, and undeclared opposite feet use
   ordinary fallback.
9. **Contact Evidence Schema:** Contact evidence source provenance uses schema v5 and phase-contact policy schema v3.
   Together they carry all input contracts and digests, lifecycle coverage, confidence components, alternatives,
   fallback matrix, mapping and configuration identities/digests, package identity and generation, and carrier
   references. Published lifecycle rows retain the immutable `AuthoredPerFootContactSampleV1` fields. A
   `DerivedPerPhysicsContactSampleV1` may add observation fields but cannot mutate evidence or grant permission. Old,
   stale, incomplete, refused, mismatched, or cross-carrier data fails closed.
10. **Runtime Publication:** The runtime publisher selects evidence only from the current catalogue role, clip,
    anatomical foot, phase, package identity, and generation. It supports cyclic wrap, finite partial lifecycles,
    explicit fallback, and complete package replacement. It must not infer semantics from suffix, name, declaration
    order, or inventory count. Replacement revokes all authority from the previous package and generation.
11. **Provider Boundary:** A side-specific provider consumes ANIM-002 permission and its immutable role-foot sample.
   It must not duplicate, infer, or override permission. The IK-003 bounded hand-off is non-contact, and generic order
   remains `sync → provider → hip reconciliation (when present) → lower-leg consumers`. `CharacterIK` alone resolves
   the generic provider and actuates its selected target, gate, and influence.
12. **Finite Clocks:** A cyclic clock wraps in `[0, 1)`; a finite non-looping clock clamps in `[0, 1]` and retains phase
   `1` at and after its endpoint. Lifecycle selection follows the explicit package declaration and finite polarity, not
   a suffix convention. Exact production identities, durations, and mappings require a ratified content specification.
13. **Consumer Boundaries:** ANIM-002 owns package schema, contact permission, authored evidence, and declared
   finite-exit metadata. NAV-001 owns stopping and durable outcomes; CTRL-001 owns live travel-release occurrence;
   IK-003 owns anchor lifecycle and bounded-correction feasibility. Catalogue metadata cannot prove a live CTRL event,
   and lifecycle evidence cannot permit a CTRL graph hop.
14. **Straight-Directional Candidate Correction:** The normative [Kimodo candidate-selection contract][kimodo-seam]
   permits only a candidate-local, role-relative, bounded orthogonal correction to evaluated-Hips path drift for a
   straight directional role. It preserves signed intended progress and re-evaluates the candidate under the unchanged
   path and heading gate. This is not a threshold relaxation: direct hierarchy, anatomy, contact, topology, and repair
   budgets remain mandatory, and genuinely reversing travel remains rejected.

### Runtime Capability Contract

1. **Schema Versions:** The processed index remains schema version 2 and requires `runtime_capability` on every
   processed action; that field is additive in the implemented version-2 index contract. The runtime catalogue is
   schema version 3 and requires `capability` on every clip. The catalogue version was bumped from 2 because per-clip
   resource-path fields were removed in favour of `(library resource, animation identity)` resolution; stale
   version-2 clip-path catalogues fail closed with a republish diagnostic.
2. **Opaque Identifiers:** Role and identity are opaque, unique package identifiers. Producers and consumers must not
   derive direction, transition intent, anatomical side or phase lane, reversal, behaviour, or capability from role or
   identity text, suffix, declaration order, a fixed inventory or count, `sad`, `loc_sad`, or an asset filename.
   Filenames may correspond to identities for structural resource binding only; that correspondence grants no semantic
   meaning.
3. **Capability Vocabulary:** `StandingLocomotionCapability` is the following closed 22-value semantic vocabulary.
   Left/right in a lane value identifies the declared anatomical phase lane, not a role-name convention.

   | Capability | Required Meaning |
   | --- | --- |
   | `cyclic_forward_primary` | Primary sustained cyclic forward travel. |
   | `cyclic_backward_primary` | Primary sustained cyclic backward travel. |
   | `cyclic_lateral_left_left_lane` | Sustained cyclic leftward travel using the left phase lane. |
   | `cyclic_lateral_left_right_lane` | Sustained cyclic leftward travel using the right phase lane. |
   | `cyclic_lateral_right_left_lane` | Sustained cyclic rightward travel using the left phase lane. |
   | `cyclic_lateral_right_right_lane` | Sustained cyclic rightward travel using the right phase lane. |
   | `cyclic_arc_left_left_lane` | Sustained cyclic left-turning arc travel using the left phase lane. |
   | `cyclic_arc_left_right_lane` | Sustained cyclic left-turning arc travel using the right phase lane. |
   | `cyclic_arc_right_left_lane` | Sustained cyclic right-turning arc travel using the left phase lane. |
   | `cyclic_arc_right_right_lane` | Sustained cyclic right-turning arc travel using the right phase lane. |
   | `confined_side_step_left` | Confined left side-step manoeuvre. |
   | `confined_side_step_right` | Confined right side-step manoeuvre. |
   | `confined_pivot_left` | Confined left pivot manoeuvre. |
   | `confined_pivot_right` | Confined right pivot manoeuvre. |
   | `entry_forward_left` | Finite idle-to-forward-travel entry owned by the left phase lane. |
   | `entry_forward_right` | Finite idle-to-forward-travel entry owned by the right phase lane. |
   | `exit_forward_left` | Finite forward-travel-to-idle exit owned by the left phase lane. |
   | `exit_forward_right` | Finite forward-travel-to-idle exit owned by the right phase lane. |
   | `entry_backward_left` | Finite idle-to-backward-travel entry owned by the left phase lane. |
   | `entry_backward_right` | Finite idle-to-backward-travel entry owned by the right phase lane. |
   | `exit_backward_left` | Finite backward-travel-to-idle exit owned by the left phase lane. |
   | `exit_backward_right` | Finite backward-travel-to-idle exit owned by the right phase lane. |

4. **Compatibility And Refusal:** A capability is required, recognised, and unique within each package that uses
   one-to-one capability dispatch. Cyclic travel capabilities require looping cyclic-travel declarations and applicable
   phase metadata. Confined manoeuvre capabilities require looping confined-manoeuvre declarations with phase and stitch
   metadata marked inapplicable. Entry and exit capabilities require finite, non-looping transition declarations, one
   locomotion-facing phase event on the declared side, and boundary ownership at `end` for entry or `start` for exit.
   The capability's direction, manoeuvre, entry, or exit meaning must agree with any declared locomotion state. Every
   stitch must reference declared roles and phase events and preserve event kind, anatomical side, and source and
   destination boundary ownership. Missing, unknown, duplicate, or incompatible declarations fail closed.
5. **Consumer Selection:** Player locomotion, NPC locomotion, and runtime installers select clips by capability and use
   package-declared phase and stitch metadata for lane, boundary, and transition dispatch. They must not recover these
   semantics from role or identity strings. A consumer fails closed when its required capability set is unavailable.

## Full Catalogue Delivery Contract

### User Requirements

1. The delivered package provides the declared travel, manoeuvre, transition, and idle interfaces without exposing
   source markers, Root resets, loop discontinuities, crossed precision steps, or arbitrary trajectory replacement.
2. Cyclic and finite roles retain their declared phase, anatomy, contact, support, carriage, path, heading, and
   recurrence semantics.
3. Navigation and locomotion playtests load and run the delivered packages without animation-resource error spam; a
   terminal package error surfaces once as a clear diagnostic instead of recurring every frame or physics tick.
4. Character locomotion and idle behaviour are unchanged by package delivery: mounted playback namespaces, authored
   contacts, and movement behaviour are identical on a clean clone without regenerating any animation asset.
5. Publishing another locomotion or idle variant can never overwrite, corrupt, or alias the delivered variant's
   package or its metadata.

### Technical Requirements

1. **Package Interface:** A locomotive package supplies role-qualified bindings, explicit capabilities, boundary kinds,
   loop intent, Root response data, applicable event mappings, stitch relationships, role-foot contact applicability,
   provenance, and a separate idle interface. Package declarations, rather than a fixed global role list, define its
   inventory.
2. **Source Repair And Reconstruction:** Required source selection, bounded repair, Root reconstruction,
   role-family validation, one-time Root/Hips motion ownership, and rejection of arbitrary linear, counter-animated,
   or source-mutating repair remain mandatory. The normative implementation contract is
   [Candidate Selection And Seam Repair][kimodo-seam]. Direct validation covers evaluated evidence, semantic Segment-0
   boundaries, phase-compatible recurrence, and walk, strafe, arc, pivot, precision-step, and finite-transition
   policies using deterministic synthetic fixtures.
3. **Bootstrap And Package Delivery:** Delivery consumes each complete user-supplied retarget setup as-is and generates
     the character-visible authoring workspace and flattened processed package through the normative
     [Kimodo pipeline specification][kimodo-pipeline]. It preserves source and materialiser ownership while performing
     materialisation, runtime-library packaging, Root-track and loop validation, truthful selection and baked-Root
     provenance, and descriptor-bound contact-evidence publication. `kimodo_materialisation_carrier_binding_v1` binds
     the source-repair report to the immutable precursor path/hash. Materialisation works from a copy and binds the
     precursor, final staged carrier, selection evidence, per-Action provenance, and retarget index without claiming
     precursor/final byte identity. The descriptor declares `contact_evidence.input`, `runtime_path`,
     `package_identity`, and `generation`, with hash bindings for its retained and runtime artefacts. The idle
     descriptor explicitly declares contact-free semantics and contains no contact artefacts. Prepared locomotion
     evidence must retain explicit descriptor-bound provenance at a non-disposable transaction-retained location; it
     must never use `game/tests`, `game/temp`, or test-results paths. Reference Female is the initial canonical bake
     target; other characters use Godot skeleton-profile retargeting until a future evidence-driven direct-bake
     extension is ratified.
4. **Character-Owned, Variant-Scoped Package Layout:** Every runtime package is owned by exactly one character and one
   variant. The package root is `[character asset root]/animations/runtime/<set>/<variant>`, where `<set>` is
   `locomotion` or `idle`; the delivered Reference Female `sad` variant is published at
   `game/assets/characters/reference/female/animations/runtime/locomotion/sad` and
   `game/assets/characters/reference/female/animations/runtime/idle/sad`. The locomotion package contains exactly
   `locomotion_<variant>.tres` (the package `AnimationLibrary`), `locomotion_<variant>_catalogue.json`, and
   `authored_per_foot_contacts_v1.json`. The idle package contains exactly `idle_<variant>.tres` and
   `idle_<variant>_catalogue.json`. No runtime animation package lives under a character-agnostic global tree.
5. **Embedded-Only Runtime Representation:** A runtime animation has exactly one published representation: an
   animation embedded in its package `AnimationLibrary`. Publication emits no standalone clip resources and no clip
   directory. Runtime resolution is `(library resource, animation identity)`: consumers load the package library and
   retrieve the declared animation identity. Clip file paths and serialised `SubResource` IDs are not contracts, and
   the runtime catalogue schema carries no animation file-path fields. The library, catalogue, and contact evidence of
   one package are co-located files selected together; mixed-package bindings fail closed. AnimationPlayer
   mounted-library slot names `locomotion` and `idle` are gameplay namespaces, not filenames: they are stable and
   independent of library filenames, character, and variant.
6. **Coherent Package Selection And Cross-Character Sharing:** Runtime consumers select the library, catalogue, and
   contact evidence of one character/variant package as a unit and reject any binding that mixes packages.
   Cross-character consumption is explicit, deliberate, and validated: the male reference template consumes the
   female-authored package through validated skeleton-profile retargeting. Provenance retains female authorship, and a
   shared package is never duplicated into male-owned paths or relabelled as male-authored.
7. **Variant-Scoped Metadata And Single-Variant Boundary:** Every processed index, metrics artefact, publication
   descriptor, and provenance binding is variant-scoped: its path and declared identity include the character, set,
   and variant it belongs to, and publishing one variant can never overwrite or alias another variant's metadata. No
   variant-agnostic package-wide metadata slot exists. Until multi-variant publication is ratified as a contract
   amendment, publication tooling must reject publishing a second variant for the same character and set.
8. **Atomic Publication:** The processed package, runtime library/catalogue/contact evidence, and provenance of both
   packages form one logically atomic, journalled, all-or-rollback transaction across all package roots. Both prepared
   packages are preflighted and reload-validated before either production root is mutated. Backups restore the exact
   immediate-pretransaction bytes of every root after staging, validation, first-root, second-root, reload, or cleanup
   failure; success removes staging, backups, and the journal. This is not a claim of OS-level atomic multi-directory
   rename.
9. **Publication Frame-Math Agreement:** Catalogue durations, loop intent, and the embedded library animations are
   produced from one frame-math source, so a declared duration and its embedded animation cannot drift apart by a
   frame or rounding step. Duration and loop validation compares catalogue declarations against the animation
   retrieved from the library and fails closed on any mismatch.
10. **Clean-Clone Reproducibility:** A fresh clone that imports the project loads every published runtime package
    completely without regenerating, re-packaging, or republishing anything. Publication output and its import
    configuration must not depend on pre-existing UID registration, local import caches, or editor state. Validation
    proves this from an isolated cold-import project or cache.
11. **Package-Owned Stitch Intent:** Each processed package retains
    `provenance/stitch-release-intent-v1.json` as its content-owned declaration of role-to-role boundary-stitch edges,
    covering both release and acquire event kinds across entry and exit boundaries. Content owners declare the opaque
    role edges; generic tooling strictly validates and transports them without inventing current role identifiers or
    treating current content as an oracle. The idle package declares an empty edge list. Missing, malformed, duplicate,
    self-referential, unknown-role, or event-incompatible intent fails closed.
12. **Runtime And Consumer Completion:** Runtime package validation and consumer coverage prove role-aware binding,
     reachability, malformed-data rejection, Root-track and loop metadata handling, response and boundary metadata,
     discrete mapping validity, phase/contact compatibility, and generic contact-evidence self-consistency. For a
     declared evidence package, validation checks the published path, package identity and generation, processed-index
     role/identity/variant mapping, catalogue-to-library identity bijection, and diagnostic provenance before a
     provider consumes the evidence. Validation retrieves the actual animation from the package library and validates
     duration, looping, skeleton tracks, Root responses, capabilities, and contact evidence against it. Missing
     required declarations and missing, malformed, stale, or mismatched declared evidence fail closed.
     The generic runtime-catalogue schema fails closed for package-derived `boundary_kind`, root `contact_evidence`,
     `phase_events.duration_seconds`, evidence metadata, timeline and event ownership, contact declarations, and stitch
     mappings. The parser derives inventory, counts, roles, mappings, boundary kinds, and stitch relationships from the
     package. Gameplay consumers declare required capabilities; neither parser nor validation hard-codes current roles,
     identities, order, count, variant, or hash. A fresh package is preflighted against generic consumers before
     publication; a legacy production `stitch_mappings: inapplicable` value is not a general package contract. These
     requirements do not transfer NAV scheduling or CTRL policy to ANIM-002.
13. **Bounded Failure Reporting:** A terminal package error — a package that is missing, malformed, stale, or failed
     validation — is reported and latched once per installation attempt; the failing package is not re-read,
     re-parsed, or re-validated on later frames or physics ticks while it remains unchanged. Retries remain legitimate
     only for genuinely incomplete installer wiring, such as waiting for character, animation, or IK installers or
     required nodes that have not yet been provided; once an installation attempt reaches package validation and fails,
     the failure is terminal for that attempt. A new explicit installation attempt, such as a scene reload or manual
     reinstall, starts a fresh attempt. Degraded playback follows the existing one-shot catalogue-failure latch
     behaviour of player locomotion.
14. **Metadata-Only Publication:** When existing processed locomotion and idle packages and retained locomotion contact
    evidence are already valid, a supported repair may stage byte-identical copies under `game/temp`, add or update only
    package-owned publication intent, and regenerate Godot runtime libraries, catalogues, and integrity
    descriptors. It must publish locomotion and idle through the validated atomic publisher and must not run source
    repair, materialisation, retargeting, or contact generation. Full provenance regeneration is required instead when a
    processed animation, processed index, metrics, contact evidence, source/action provenance, retarget binding, or
    materialisation carrier changes or fails validation.
15. **Required Non-Visual Evidence:** Source, selection, repair, materialisation, reopen, package, corruption, runtime,
    and consumer evidence remain mandatory. Direct automated checks cover anatomy, contact topology and safety, support,
    hierarchy closure, native cadence, semantic boundaries, Root-relative path, heading, arc carriage/body support,
    pivot accumulation, seam closure, and repeat-cycle composition. This evidence assures technical properties, not
    subjective player-perceived naturalness.
16. **Mirror Invariant:** Every spatially mirrored evaluated hierarchy datum applies both the armature-space spatial
    transform and bilateral bone identity in the output-role frame before Root-relative path, seam, contact, anatomy,
    cadence, or body-support assessment. Applying only one is invalid.

### Acceptance Criteria

#### User Requirement Acceptance

1. Fixture-owned packages demonstrate declared role, phase, path, heading, lead-side, loop, transition, and idle
   behaviour without claiming that any current production clip has those properties.
2. Fixture-owned motion demonstrates safe anatomy, contact, support, carriage, seam, and recurrence semantics.
3. Fixture-owned motion demonstrates that bounded orthogonal Hips drift alone does not reject intentional straight
   locomotion, while genuinely reversing travel remains unavailable.
4. A metadata-only repair demonstrates byte-identical processed animation artefacts while restoring declared runtime
   stitch behaviour for locomotion and preserving contact-free, stitch-free idle behaviour.
5. A navigation or locomotion playtest composition loads a delivered package and runs without animation-resource error
   repetition; an injected terminal package failure produces exactly one clear diagnostic per installation attempt and
   no per-frame or per-physics-tick recurrence while the package is unchanged.
6. A clean clone of the repository imports and plays character locomotion and idle identically to the working tree,
   without regenerating or republishing any animation package.
7. A second-variant publication attempt is refused without touching the delivered variant's package or metadata.

#### Technical Requirement Acceptance

1. Contract review verifies that ANIM-002 is the only contact-permission authority and that tooling, IK-003, NAV-001,
   and CTRL-001 do not duplicate, infer, override, or relax permission.
2. Schema tests validate package-declared role/event vocabulary, immutable authored and derived schemas, and rejection
   of permission fields or mutations in authored evidence.
3. Synthetic contact fixtures cover required, optional, and fallback-only role-foot paths; directional lineage and
   deterministic mirror/reverse composition; unique cyclic samples; physical-first basin selection; finite polarity and
   partial lifecycle selection; the `0.8` confidence gate; package replacement; and ordinary non-contact fallback. They
   prove that out-of-sector, wrong-polarity, non-separable, ambiguous, stale, incomplete, and cross-carrier evidence
   fails closed, while absolute world slide remains diagnostic only. No assertion derives an expected action, range,
   pose, Root delta, contact, known hash, variant, identity, timing, or inventory from a volatile production asset.
4. Fixture-owned provider and Gate A/B tests verify ordering, `CharacterIK`-only resolution and actuation, refusal
   semantics, and that downstream composition, solve, or output cannot cure an earlier failed validation. Gate B stays
   disposable fixture evidence and cannot authorise production content, policy, scene wiring, or integration.
5. Bootstrap, extraction, workspace, materialisation, reopen, flattened-path migration, runtime library, package,
   corruption, and consumer checks verify Technical Requirements 3 and 8 without mutating protected source or published
   assets. Synthetic declared-fixture tests cover successful evidence staging; missing, malformed, or mismatched
   evidence with no write; stale-evidence removal; validator consumption; and provider consumption. They cover normal
   commit; staging and validation rollback; first- and second-root rollback; stale cleanup; provenance survivability;
   and cleanup rollback. They verify exact immediate-pretransaction bytes for every package root and no journal,
   staging, or backup remnants after successful commit or recoverable rollback. Carrier-binding fixtures prove immutable
   precursor/report hashes and explicit precursor-to-final-to-selection/action-to-retarget-index lineage without a
   byte-identity claim. Generic schema fixtures cover valid, malformed, unknown, duplicate, and non-finite declarations,
   alongside generic capability preflight. Capability fixtures exercise catalogue schema version 3 while requiring
   processed-action `runtime_capability` and runtime-clip `capability`; they reject missing, unknown, duplicate, and
   state-, boundary-, phase-, or stitch-incompatible values. Production assets are read only within the boundary in
    Technical Requirement 3. Descriptor checks prove hash-bound locomotion contact evidence and explicitly
    contact-free idle publication with no contact declaration or artefact; both modes fail closed before mutation.
6. Synthetic source-selection and adaptive-repair fixtures cover source-name, source-order, and generation-order
   independence; bounded repairs; Root ownership; evaluated-hierarchy cadence; direct safety; and each required role
   family. They retain source and published-asset immutability and verify that durable `windows` and
   `immutable_interior` provenance describe the selected adaptive width rather than the default repair width.
7. Direct synthetic output assertions cover the full non-visual evidence in Technical Requirement 15, including native
   and spatially mirrored paths. They reject Root/body or bilateral-bone output-role-frame mismatches.
8. Synthetic declared candidate fixtures prove that a raw path outside the unchanged gate is admitted only after a
   bounded candidate-local orthogonal correction satisfies that gate, without changing signed intended progress. They
   also prove deterministic action-name, source-order, and mirror invariance, and rejection of genuinely reversing
   travel. These fixtures do not introduce visual verification or an authored-production-content test oracle.
9. Synthetic package and consumer fixtures arbitrarily rename and reorder opaque roles and identities while preserving
   capability, phase, and stitch declarations. Player, NPC, and installer dispatch remains unchanged, and tests prove
    that no suffix, order, fixed count, `sad`, `loc_sad`, or filename supplies behaviour or capability.
10. Publication tests reject missing and malformed package-owned stitch intent, prove generic transport with synthetic
     opaque roles, and prove empty idle intent. A metadata-only production repair verifies byte-identical processed
     animation artefacts before and after atomic locomotion-and-idle publication, valid descriptors, the declared
     locomotion mappings, and semantically unchanged idle output.
11. Package-layout checks prove the delivered locomotion and idle packages live at their character-owned,
     variant-scoped roots with exactly the declared files, and that no character-agnostic global runtime animation
     tree remains. Variant-scoping checks prove processed indexes, metrics, descriptors, and provenance carry the
     character/set/variant identity and that a second-variant publication attempt for the same character and set is
     rejected before any production mutation (Technical Requirements 4 and 7).
12. Embedded-representation checks prove consumers resolve every animation through `(library resource, animation
     identity)`: no standalone clip resources or clip directories are published or required, catalogue and contact
     evidence schemas carry no animation file-path fields, and identity bijection holds across catalogue, library, and
     contact evidence. AnimationPlayer mounted-library slots `locomotion` and `idle`, and the library-qualified
     animation names consumers use, remain stable across publication (Technical Requirement 5).
13. Sharing checks prove the male reference template consumes the female-authored package through validated
     retargeting, that provenance retains female authorship, and that no male-owned duplicate or relabelled package
     exists (Technical Requirement 6).
14. Failure-reporting checks prove a synthetic invalid package produces exactly one latched diagnostic per
     installation attempt with no per-frame or per-physics-tick re-read, re-parse, or re-validation of the unchanged
     package, while a composition whose installer wiring is genuinely incomplete still installs once the missing
     installers or nodes are provided (Technical Requirement 13).
15. Frame-math checks prove published catalogue durations and loop intent match the embedded library animations
     exactly, and a package whose declared durations drift from its embedded animations fails validation closed
     (Technical Requirement 9). Clean-clone checks prove an isolated cold-import project loads both published packages
     completely without regeneration, repackaging, or pre-existing UID or import-cache state (Technical Requirement 10).

### Acceptance Traceability

- User Requirements 1–3 → User Requirement Acceptance 1–2 and Technical Requirement Acceptance 2–4.
- User Requirements 4–6 → Technical Requirement Acceptance 5–10.
- User Requirement 5 → User Requirement Acceptance 3 and Technical Requirement Acceptance 6–8.
- Technical Requirements 1–3 → Technical Requirement Acceptance 1–3.
- Technical Requirements 4–10 → Technical Requirement Acceptance 2–5.
- Technical Requirements 11–13 → Technical Requirement Acceptance 1, 3–4.
- Technical Requirement 14 → Technical Requirement Acceptance 8.
- Runtime Capability Contract → Technical Requirement Acceptance 5 and 9.
- Full Catalogue Delivery Contract User Requirements 1–2 → User Requirement Acceptance 1–2.
- Full Catalogue Delivery Contract User Requirements 3–5 → User Requirement Acceptance 5–7 and Technical Requirement
  Acceptance 11–15.
- Full Catalogue Delivery Contract Technical Requirements 3–10 → Technical Requirement Acceptance 5, 11–15.
- Full Catalogue Delivery Contract Technical Requirements 11–12 and 14–16 → Technical Requirement Acceptance 5–10.
- Full Catalogue Delivery Contract Technical Requirement 13 → Technical Requirement Acceptance 14.

## Canonical Contact Evidence

`ANIM-002 Contact Evidence Refusal Set` is the complete, closed provider-refusal set. A provider must refuse contact
publication for evidence that is missing or empty; unknown or malformed; stale; invalid or non-finite; contradictory
or incompatible; inferred; inapplicable-as-applicable; wrong-foot; or role- or identity-mismatched.

Refusal means only that contact is not admitted. Ordinary animation-synchronised or fallback motion may continue, but
it is not accepted contact evidence and cannot become contact acceptance.

## In Scope

- Contact-permission ownership, capability and role/event schemas, contact evidence, and provider boundaries.
- Full non-visual package, repair, materialisation, runtime, contact-publication, and consumer validation.
- Character-owned, variant-scoped, embedded-only runtime package delivery: package layout, `(library resource,
  animation identity)` resolution, coherent package selection, cross-character sharing, variant-scoped metadata, and
  clean-clone reproducibility.
- Bounded failure reporting for terminal package errors during runtime installation.
- Journalled cross-package publication, retained contact provenance, generic catalogue validation, and production
  preflight without a production-content oracle.
- Bootstrap, extraction, workspace, flattened processed-path, and package-delivery obligations through the Kimodo
  pipeline.
- Deterministic synthetic fixtures as the only motion and content oracle.

## Out Of Scope

- Ratifying current authored production content, including `sad` variants and `loc_sad_*` identities or timings.
- Publishing additional characters, sets, or variants beyond the delivered Reference Female `sad` packages; the
  single-variant rejection contract remains normative until multi-variant publication is ratified.
- Terrain adaptation, contact inference, gait synthesis, motion matching, movement-policy selection, and provider-
  specific production scheduling.
- NAV-001 stopping, correction-envelope, hard-boundary, confirmation, and durable-outcome policy.
- Screenshot, photobooth, GLB, contact-sheet, manual visual review, and visual-capture verification.

These exclusions do not defer mandatory bootstrap, extraction, workspace, repair, package, contact publication,
cross-package transaction, embedded-only runtime catalogue, bounded failure reporting, clean-clone reproducibility,
consumer, or non-visual delivery validation.

## References

- [Project Specifications Index](../../index.md)
- [Kimodo Locomotive Animation-Set Pipeline][kimodo-pipeline]
- [IK-003: Leg And Feet IK System](../../ik/003-leg-feet-ik/index.md)
- [Contact-Lock Coordinator Contract](../../ik/003-leg-feet-ik/contact-lock-coordinator-contract.md)
- [NAV-001: NPC Navigation](../../nav/001-npc-navigation/index.md)
- [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md)

[kimodo-pipeline]: ../../tooling/kimodo-pipeline/locomotive-animation-set.md
[kimodo-seam]: ../../tooling/kimodo-pipeline/locomotive-animation-set.md#candidate-selection-and-seam-repair
