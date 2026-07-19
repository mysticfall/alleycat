---
id: ANIM-003
title: Standing Locomotion Catalogue
---

# Standing Locomotion Catalogue

## Requirement

Define the authoritative curated standing-locomotion collection, its source provenance, its reproducible ANIM-001
processed outputs, and its reusable Godot `AnimationLibrary` packaging.

## Goal

Technical artists and gameplay implementers can review, regenerate, import, and reuse one portable collection of 46
standing clips without depending on a particular locomotion runtime.

## User Requirements

1. Contributors can review why each source clip is included and trace it to stable Mixamo source metadata.
2. The catalogue provides ordinary standing idle, forward and backward movement, lateral movement, moving arcs, starts,
   stops, speed variation, and turn-in-place coverage.
3. The complete collection is available as individually loadable Godot animations and as one reusable
   `AnimationLibrary`.
4. Regenerating or reimporting the catalogue does not silently omit clips, retain removed clips, or depend on a
   contributor's local source location.
5. Catalogue completeness is not presented as proof that walking-to-idle transitions or subjective animation quality
   are natural in a consuming runtime.

## Technical Requirements

### Ownership and Normative Inputs

1. ANIM-003 owns the concrete 46-row standing selection, its review notes, `locomotion_standing.blend`, the associated
   metrics and processed-index entries, Godot extraction settings, the 46 extracted clips, and the standing catalogue
   library and package manifest.
2. [ANIM-001](../001-animation-source-pipeline/index.md) is a normative dependency for the source-manifest and selection
   schemas, action naming, retargeting, root reconstruction, metrics schema, portable processed-index format, and
   MakeHuman Godot import handoff. ANIM-003 must apply those contracts and must not duplicate or weaken them.
3. ANIM-003 defines the authoritative 46-clip baseline through the committed source manifest, selection CSV,
   processed artefacts, counts, invariants, and curation provenance specified on this page.
4. The stable source inputs are:
   - `game/assets/characters/reference/female/animations/source/mixamo/manifest.csv`;
   - `game/assets/characters/reference/female/animations/source/mixamo/selection.csv`.
5. The selection CSV is the reviewable source of truth for inclusion, classification, tags, gender, group, root-motion
   policy, and curation notes. Its 46 rows are all enabled, use category `locomotion`, use group
   `locomotion_standing`, and join uniquely by `motion_id` to the source manifest.
6. Source-manifest `file` values remain portable paths relative to the configured source root, such as
   `download/<motion_id>.fbx`. No local download root or other source-machine path is part of this specification.

### Curated Collection

1. The collection contains exactly:
   - 4 `StandingIdle` clips;
   - 36 `StandingLocomotion` clips;
   - 6 `TurnInPlace` clips.
2. The selected tags and notes must retain coverage of:
   - neutral, female, and look-around standing idles;
   - forward walk and run at varied speeds, forward starts, walk/run stops, and a stop-start clip;
   - backward walking and running;
   - left/right walk strafes, run strafes, strafe starts and stops, relaxed strafes, and sidesteps;
   - left/right walking and running arcs;
   - left/right turn-in-place coverage at 90 and 180 degrees, plus the selected 45-degree-left and ordinary-right
     variants.
3. The five preview-screened lateral additions at the baseline are normative selection provenance:

   | Motion ID | Selection | Curation Rationale |
   | --- | --- | --- |
   | `c9c9d9d6-b96c-11e4-a802-0aaa78deedf9` | Walk Strafe Left | Clean upright lateral walk with relaxed arms; low-speed left variety. |
   | `c9c9db9e-b96c-11e4-a802-0aaa78deedf9` | Walk Strafe Right | Clean upright lateral walk with relaxed arms; low-speed right variety. |
   | `c9c9829c-b96c-11e4-a802-0aaa78deedf9` | Left Strafe Walk | Clear side travel, relaxed posture, and no weapon or combat pose. |
   | `c9c985b7-b96c-11e4-a802-0aaa78deedf9` | Right Strafe Walk | Clear side travel, relaxed posture, and no weapon or combat pose. |
   | `c9c7ff20-b96c-11e4-a802-0aaa78deedf9` | Side Step | Generic unarmed side-to-side adjustment with relaxed arms. |

4. Selection notes must remain in the CSV and survive into processed provenance where the ANIM-001 schema carries
   them. The table above summarises, rather than replaces, those normative rows.
5. Ordinary reusable locomotion is preferred over combat, weapon, boxing, guard, action-pose, crouched, prone,
   injured, stylised, or object-carrying motion. Such candidates are excluded; if retained for comparative review
   outside this selection, they must be disabled or explicitly down-ranked and the reason recorded.
6. Curation notes provide reviewable quality provenance: weapon-biased turn and movement candidates were replaced by
   ordinary or unarmed clips, while candidates that lacked mapped source bones or failed the Godot generation round
   trip were not selected.

### Processed Artefacts and Invariants

1. ANIM-001 processing produces these standing-catalogue artefacts:
   - `game/assets/characters/reference/female/animations/processed/mixamo/locomotion_standing.blend`;
   - `game/assets/characters/reference/female/animations/processed/mixamo/locomotion_standing.blend.import`;
   - `game/assets/characters/reference/female/animations/processed/mixamo/index.json`;
   - 46 files at
     `game/assets/characters/reference/female/animations/processed/mixamo/metrics/<action>.metrics.json`.
2. The `.blend` contains one coherent `locomotion_standing` group with one target armature and exactly 46 persistent,
   uniquely named actions. Each action follows `mixamo_<motion_id>` with hyphens replaced by underscores.
3. The baseline processed index and metrics have these cross-consistent invariants:
   - processed-index schema version 2 and metrics schema version 2;
   - 46 successful motions, 46 unique actions, and 46 metrics files;
   - one category, `locomotion`, and one group, `locomotion_standing`;
   - 2,612 total metric samples at 30 fps;
   - `root_source` equal to `reconstructed_root` and `root_created=true` for every clip;
   - 106.66666668653485 seconds when summing the 46 imported animation lengths, reported as 106.67 seconds for
     human-readable summaries.
4. `index.json`, metrics files, source references, `.blend` references, and generated package references must use
   repository-relative paths or `res://` paths. Absolute paths, temporary paths, and source-machine locations are
   forbidden.
5. No `locomotion_crouch` motion, action, metrics entry, extracted clip, library entry, or package-manifest entry may be
   emitted as part of this collection. `locomotion_crouch.blend` is not an ANIM-003 output and must not become an orphan
   referenced by standing-catalogue generation.

### Godot Import and Extraction

1. `locomotion_standing.blend` must use the ANIM-001 MakeHuman bone map and complete silhouette-rest-fixer handoff,
   including the normative filter defined there.
2. Godot imports the `.blend` as a `PackedScene` with animation import enabled at 30 fps. The imported scene must expose
   one `AnimationPlayer`, one `AnimationLibrary`, one `Skeleton3D`, and all 46 selected actions.
3. Before import, the minimal, idempotent import configurator must write one top-level contained-animation entry for
   each of the 46 actions, including the five preview-screened lateral additions. Every entry must set:
   - `save_to_file/enabled` to `true`;
   - `save_to_file/path` to the literal expected path
     `res://assets/characters/reference/female/animations/locomotion/clips/<action>.res`;
   - `save_to_file/fallback_path` to that identical literal expected path.
   The configurator must remove stale top-level action entries and must produce no duplicate action or resource path.
4. After Godot import, Godot 4.7 may canonicalise a top-level `save_to_file/path` value to `uid://...`. A canonical UID
   is valid only when all of these conditions hold:
   - Godot resolves it to exactly the expected clip resource for that action;
   - `save_to_file/fallback_path` remains the exact expected literal `res://` path;
   - `save_to_file/enabled` remains `true`;
   - all 46 unique action-to-resource correspondences remain exact;
   - the UID is resolved, non-duplicated, and does not conceal an empty, missing, or mismatched fallback.
5. Importer-generated slice-default metadata is canonical opaque import state. Its volume and default values are not
   stale and do not require stripping or normalisation. Validation must ignore this metadata except when it introduces
   an extra top-level action or an enabled slice that changes extraction semantics.
6. No post-import repair may replace a valid canonical UID merely to normalise or simplify the sidecar. Repeated
   unchanged imports must produce equivalent canonical post-import action, UID resolution, fallback, enablement, and
   extraction state.
7. Extraction produces exactly 46 binary `Animation` resources at
   `game/assets/characters/reference/female/animations/locomotion/clips/<action>.res`. Each resource is loadable,
   retains the imported animation keys, and has `resource_name=<action>`.
8. Extraction and packaging are deterministic. When the `.blend`, selection, index, metrics, or import metadata
   changes, all affected clips, paths, catalogue entries, and library references refresh in one run; stale extracted
   clips and stale library entries are removed.
9. Packaging may normalise an accepted imported skeleton track prefix to `GeneralSkeleton`, but must otherwise pass
   through imported track types, key counts, key times, and key values. It must not reconstruct poses or roots, apply
   grounding, or add runtime compensation.

### Reusable Godot Package

1. The durable library path is
   `game/assets/characters/reference/female/animations/locomotion/standing_locomotion_library.tres`. It is an
   `AnimationLibrary` containing exactly the 46 extracted resources, keyed by their processed action names.
2. The durable package-manifest path is
   `game/assets/characters/reference/female/animations/locomotion/standing_locomotion_catalogue.json`. It records the
   source index, schema versions, library path, clip count, and one entry per clip with its key, resource path,
   `motion_id`, action, group, group `.blend`, metrics path, motion class, category, tags, source-manifest metadata,
   imported length, track count, fps, frame range, sample count, root source, and root-created flag.
3. `mixamo_locomotion_library.tres` and `mixamo_locomotion_manifest.json` are non-normative runtime-oriented names.
   Restored or regenerated assets must use the catalogue-neutral paths above.
4. Library keys, extracted clip basenames, processed actions, metrics actions, and package-manifest keys correspond
   one-to-one. Ordering is deterministic by action key.
5. Durable package-manifest clip fields and externally documented clip paths must remain literal portable `res://`
   paths. A library `.tres` may contain normal Godot UID metadata alongside resource paths, but UIDs must not replace
   its path-resolvable resource contract. The durable catalogue JSON must never substitute `uid://` values for its
   literal `res://` paths.
6. The package exposes the complete collection without motion-matching, player-control, navigation, scoring,
   transition, or `AnimationTree` coupling. Runtime query structures, scoring, transitions, `AnimationTree` topology,
   and navigation integration belong to runtime and navigation consumer specifications.

### Validation Contract

1. A catalogue validation pass must fail on disagreement among selection rows, source-manifest joins, processed-index
   motions, metrics files, `.blend` actions, import metadata, extracted clips, package-manifest entries, library keys,
   or the required 4/36/6 coverage split.
2. Validation must reject duplicate or empty `motion_id` values, actions, clip keys, resource paths, and library keys.
3. The exact collection count is 46 at every post-selection stage, and all five laterals must be present.
4. Git LFS validation must confirm that `.blend` and `.res` paths are LFS-tracked, their Git objects are valid LFS
   pointers, and their working-tree files are materialised payloads rather than pointer text before Blender or Godot
   validation.
5. Godot validation must import and instantiate `locomotion_standing.blend`, load every extracted `.res` as an
   `Animation`, load the library as an `AnimationLibrary`, and resolve every library entry to the corresponding clip.
6. Pre-import validation must confirm the ANIM-001 retarget settings and exactly 46 top-level action entries with
   enabled extraction, literal expected `save_to_file/path` values, identical literal fallback paths, and no stale or
   duplicate actions.
7. Post-import validation must accept either the unchanged expected literal primary path or a canonical `uid://`
   primary path that Godot resolves to that exact resource. It must reject unresolved, mismatched, or duplicate UIDs;
   disabled extraction; empty or mismatched fallbacks; missing or extra top-level actions; and enabled slices that alter
   extraction semantics. Opaque slice-default metadata otherwise remains unchecked.
8. Import validation must also confirm required `Root`, `Hips`, foot, and toe tracks and pass-through key preservation.
   Repeated unchanged imports must have equivalent canonical post-import outputs without UID-rewriting repair.
9. Portability validation must scan CSV, index, metrics, package manifest, and text resources for absolute paths,
   temporary paths, UID substitution for durable literal clip paths, and other source-machine leakage. Canonical
   top-level import-sidecar UIDs are allowed only under the post-import contract above.
10. Validation must confirm that no crouch group or orphan crouch output is referenced by the standing collection.

### Known Quality Gaps

1. Catalogue completeness does not prove walking-to-idle synchronisation or naturalness.
2. Crouch content is absent and outside this standing collection.
3. Subjective animation quality, transition feel, and naturalness require evidence and playtesting by each consumer.
4. ANIM-003 does not own runtime transition solutions for these gaps.

## In Scope

- The concrete 46-clip standing selection and its reviewable source provenance.
- ANIM-001 generation of the standing `.blend`, metrics, and portable processed index.
- Godot import metadata, deterministic extraction, reusable library packaging, and catalogue package metadata.
- Cross-layer count, coverage, portability, LFS, import, loadability, and correspondence validation.

## Out Of Scope

- Crouch, prone, seated, interaction, combat, weapon, boxing, guard, and bespoke animation collections.
- Runtime motion matching, query data, scoring, transition policy, state machines, `AnimationTree` topology, player
  locomotion, and navigation integration.
- Claims of natural transition quality without consumer playtest evidence.
- Final tuning of consumer-specific transition thresholds, blend durations, or scoring weights.

## Acceptance Criteria

### User Requirement Acceptance

1. A contributor can trace every enabled selection row through the stable source manifest, retained selection notes,
   processed outputs, extracted clip, package manifest, and library key without a local source path.
2. Catalogue review confirms exactly 4 standing idles, 36 standing locomotion clips, and 6 turn-in-place clips with the
   stated directional, speed, start/stop, lateral, arc, and turn coverage.
3. The five named preview-screened laterals are present with their rationale, while unsuitable combat, weapon, boxing,
   guard, crouch, and other non-ordinary candidates are absent or explicitly disabled outside the selection.
4. Godot consumers can load all 46 clips individually and enumerate the same complete collection from one reusable
   `AnimationLibrary` without adopting a runtime motion-matching or navigation architecture.
5. Catalogue documentation states that walking-to-idle synchronisation, naturalness, crouch coverage, and subjective
   quality remain consumer validation work rather than solved catalogue outcomes.

### Technical Requirement Acceptance

1. Automated cross-consistency checks join 46 unique selection rows to the source manifest and confirm 46 unique
   processed actions, metrics files, `.blend` actions, contained-import entries, extracted resources, package entries,
   and library keys.
2. The processed index and every metric validate as schema version 2, one coherent `locomotion_standing` group,
   `reconstructed_root`, `root_created=true`, 2,612 total samples, and the inspected 106.66666668653485-second imported
   duration total.
3. The pre-import configuration uses the normative ANIM-001 MakeHuman bone map and silhouette-rest-fixer settings and
   writes exactly 46 enabled top-level actions with literal expected primary and identical fallback `res://` paths,
   without stale or duplicate actions.
4. Post-import inspection accepts a Godot 4.7 canonical `uid://` primary path only when it resolves to the exact
   expected clip, retains the exact literal fallback and enabled extraction, and maintains all 46 unique
   action-to-resource correspondences. Opaque slice defaults are ignored unless an enabled slice changes extraction.
5. Repeated unchanged imports produce equivalent canonical post-import outputs without rewriting valid UIDs;
   regeneration after a `.blend` change refreshes affected clips, removes stale clips and keys, and maintains
   one-to-one action, metrics, clip, package-manifest, and library correspondence.
6. LFS checks prove valid tracked pointers and materialised `.blend` and `.res` payloads; Godot then imports the source
   and loads every clip and the library successfully.
7. Portability checks find no source-machine or temporary path leakage. Durable catalogue JSON and documented clip
   fields use literal `res://` paths; library resources remain path-resolvable even when normal UID metadata is present.
8. The generated collection contains no crouch action, metrics entry, clip, package entry, library key, or orphan
   crouch reference.
9. Package inspection confirms pass-through animation data apart from allowed skeleton-prefix normalisation and no
   dependency on runtime query, scoring, transition, `AnimationTree`, player-locomotion, or navigation structures.
10. Restored or regenerated assets use `standing_locomotion_library.tres` and
   `standing_locomotion_catalogue.json`; the non-normative runtime-oriented filenames are not part of the durable
   contract.

## References

- [ANIM: Animation](../index.md)
- [ANIM-001: Animation Source Pipeline](../001-animation-source-pipeline/index.md)
- `tools/process_mixamo_animations.py`
