---
id: ANIM-001
title: Animation Source Pipeline
---

# Animation Source Pipeline

## Requirement

Define the proven Mixamo animation pipeline for catalogue acquisition, source inspection, curated processing,
target-rig retargeting, root reconstruction, metrics generation, and reusable processed outputs.

## Goal

Technical artists can acquire and inspect Mixamo clips, curate them through stable CSV metadata, and reproducibly
generate target-rig animation sources without manual per-clip Blender editing or leaking machine-local source paths.
Future ANIM-003 work will own concrete standing-locomotion content and Godot packaging built from these outputs.

## User Requirements

1. Technical artists can acquire Mixamo animations and reconcile downloader-run metadata into a stable, portable
   processing manifest.
2. Contributors can inspect downloaded clips through consistent static previews before curating them.
3. Technical artists can curate Mixamo animations through reviewable selection metadata without changing the tools.
4. Contributors can regenerate selected target-rig animation groups and metrics without manual per-clip Blender edits.
5. Locomotion clips provide reconstructed target `Root` motion whose class-specific direction and progress agree with
   visible body motion and graph-role semantics, while child compensation prevents visible double motion.
6. Batch processing supports narrowed, interrupted, and resumed work.
7. Processed outputs do not expose local Mixamo source or temporary paths and provide reusable inputs for later Godot
   content packaging.
8. Standing-locomotion consumers receive source actions with an unambiguous, pose-derived loop intent and continuous
   repeated Root motion, so only clips whose retained pose permits repetition repeat naturally.
9. When a vetted natural source has no approved native counterpart, content specifications can use a reproducible,
   traceable derived mirror without changing the source pipeline's Root or clip-contract guarantees.

## Technical Requirements

### Pipeline and Content Ownership

1. ANIM-001 owns the working Mixamo acquisition, preview, retargeting, root-processing, batch-processing, metrics, and
   processed-index contracts defined here.
2. ANIM-001 owns reusable output schemas, including grouped target-rig `.blend` files, per-action metrics sidecars, and
   the processed index. Output locations remain configurable at processing time.
3. Future ANIM-003 owns the concrete standing-locomotion selection rows, the generated
   `locomotion_standing.blend`, extracted per-clip `.res` resources, and the corresponding Godot animation library.
4. ANIM-001 does not require a particular runtime consumer.
5. ANIM-001 defines the Blender source naming convention for loop intent. ANIM-003 normatively applies that convention
   when importing and packaging the concrete standing-locomotion catalogue.
6. ANIM-001 provides the reusable derived-mirror processing and provenance contract. A content specification decides
   whether a source is eligible and whether a native matched pair is available.

### Source Manifests

1. The stable source manifest header is exactly:
   `motion_id,name,description,type,file`.
2. The curated selection header is exactly:
   `motion_id,enabled,category,motion_class,tags,gender,group,create_root_motion,notes`.
3. `motion_id` joins selection rows to source rows. After active motion and group filters are applied, each remaining
   enabled row requires a non-empty `group`, a matching source-manifest row, and a corresponding source file.
4. `enabled` and `create_root_motion` use the boolean values accepted by `process_mixamo_animations.py`; `tags` is a
   semicolon-separated list.
5. In the stable processing manifest, `file` identifies the downloaded FBX with a portable relative value or basename,
   independently of the machine-local source directory. The source directory is supplied through `--source-dir` or
   `--mixamo-root` at processing time and is not persisted in processed artefacts.
6. `process_mixamo_animations.py` rejects incorrect headers, empty required identifiers, duplicate source-manifest
   `motion_id` values, and invalid booleans. For enabled rows remaining after active motion and group filters, it also
   rejects missing groups, dangling source-manifest references, and missing selected FBX files. Duplicate selection
   `motion_id` values are not rejected by the historical implementation.

### Mixamo Acquisition Metadata

1. `tools/download_mixamo.py` enumerates Mixamo motion metadata and downloads FBX files to configurable `--out-dir`,
   naming each file `<motion_id>.fbx`.
2. The downloader writes a downloader-run `manifest.csv` under `--out-dir`, catalogue metadata to configurable `--csv`,
   and resumable completion/failure state to `--state`. Its manifest `file` values may include the configured
   `--out-dir`; before processing, this run metadata must be reconciled into the stable source manifest so each `file`
   value is a portable relative path or basename.
3. The downloader accepts a bearer token from `--bearer`, `MIXAMO_BEARER_TOKEN`, or `--bearer-file`; a bearer file
   overrides the direct option or environment value. Documentation and generated artefacts must not embed token values.
4. Catalogue metadata fields are `id`, `type`, `name`, `description`, `category`, `character_type`, `motion_id`,
   `source`, `thumbnail`, `thumbnail_animated`, `motions`, `matching_file`, and `file_exists`.
5. Catalogue metadata supports candidate discovery, comparison with curated rows, category and character-type review,
   thumbnail inspection, and downloaded-file matching. `matching_file` remains downloader-run metadata and is not a
   runtime or processed-output dependency.
6. Downloads use temporary partial files, reject empty results, replace completed destinations, retain retryable state,
   and can continue after per-motion failures.

### Preview Generation

1. `tools/generate_mixamo_previews.py` batch renders downloaded Mixamo FBX files from configurable `--source-dir` and
   supports repeated `--file` filters.
2. Each preview is a `.png` beside its source `.fbx`. Existing previews are skipped unless `--force` is supplied.
3. A preview is a vertically stacked four-frame panel sampled at the start, one-third, two-thirds, and end of the clip.
   Width, height, separator pixels, render sample count, and background colour are configurable.
4. Camera bounds account for the full clip, with bounded dense sampling for long clips. The preview uses a neutral
   material and an orthographic camera.
5. Blender is resolved from `BLENDER` or `PATH`. Per-file render failures are reported and cause a non-zero result.
6. Preview paths are processing-time inspection aids only and are not persisted in processed indexes or runtime assets.

### Retargeting and Root Reconstruction

1. `tools/retarget_mixamo_animation.py` retargets one selected FBX directly from the Mixamo preset to the configured
   target-rig preset, bakes one target-rig action, removes source data, and retains the action persistently.
2. Normal processing canonicalises the unparented target `Root` rest bone at the origin, with its tail along Blender
   `+Y`, and reconstructs a sanitised `Root` track in Blender planar `X/Y` with `Z` up.
3. **Normative Heading Terminology:** A **visible/body heading** is the clockwise-positive planar heading observed from
   the evaluated body and Root tail. A **canonical Blender Root Euler-Z** is the right-handed `Root` Euler-Z channel.
   Because the canonical Root tail is `+Y`, a visible heading delta maps as
   `root_euler_z = -visible_heading_delta`. This is a representation contract, not a change to role semantics.
4. Heading validation must measure the evaluated physical Root forward/tail vector in the Blender plane and derive
   visible/body heading from that vector. It must not infer visible direction or signed turn solely from a raw
   Euler-Z channel or `matrix_basis` channel.
5. Root reconstruction compensates direct `Root` children, including `pelvis`, so non-Root evaluated poses remain
   equivalent within the historical tool tolerances and no double motion is introduced.
6. Selection `category`, `motion_class`, and `tags` are the primary root-policy source. Track analysis only reports
   disagreement or static-motion outliers; it does not replace curated metadata.
7. Policies serialise exactly as:
   - `Moving` for translational locomotion;
   - `Turn-In-Place` for clips tagged `turn` and `in_place`;
   - `Stationary` for other clips.
8. Moving clips use `StraightMoving` by default and `CurvedMoving` for `arc` metadata. Straight semantic directions map
   to the Blender root plane as forward `-Y`, backward `+Y`, left `+X`, and right `-X`.
9. Source `Root` tracks are unreliable inputs to reconstruction. Curated metadata validates class and semantics, but
   cannot be the sole author of reconstructed yaw; source-informed visible-pose measurement and validation remain
   required.
10. Turn-in-place reconstruction keeps `Root` translation exactly at `(0, 0, 0)` throughout. Only signed yaw changes,
    and its direction and progress must agree with the visible body turn and the selected turn-role semantics. For the
    named right pivot, visible/body heading is clockwise-positive and `root_euler_z` is negative; the left pivot uses
    the opposite signs.
11. Straight travel and side-step reconstruction supplies graph-role planar translation and a deliberately fixed
   canonical heading when source yaw is unreliable. It must have no unintended yaw rotation or role contradiction;
   generic alignment between raw-source Root yaw and body heading is not required.
12. Walk-arc reconstruction supplies planar translation and signed yaw whose direction and progress agree with the
    visible turn and graph-role semantics. Its clockwise-positive visible/body heading maps to negative canonical
    Blender Root Euler-Z under the heading terminology above. It uses source-informed measurement and validation, not
    blind adoption of source Root values.
13. Stationary processing suppresses planar movement while retaining a stable initial yaw.
14. Reconstruction records policy, subtype, path and yaw changes, source signals, straight-motion decisions,
    suppressed lateral deviation, and metadata-disagreement flags in `root_reconstruction` diagnostics.
15. Sanitised output has no target `Root` vertical drift or local-X tilt beyond the historical checks, uses linear keys,
    and is validated after save. Final thresholds remain implementation tuning values.
16. `--skip-root-reconstruction` is an exceptional inspection option that preserves raw baked root tracks.
    `--create-root-motion` is only valid with that debug path and may synthesise planar root movement from pelvis
    displacement; neither option defines normal production output.
17. Obsolete delta-only comparison or evidence helpers from prior failed heading fixes must be removed or replaced.
    Replacement helpers must validate evaluated physical Root-tail heading, retain visual evidence, and must not
    reinstate raw Euler-Z or `matrix_basis` as proof of visible/body direction.

### Source Loop Intent

1. The processor determines per-clip loop eligibility by comparing evaluated start and end poses for every retained
   target bone other than `Root`. Root translation and Root yaw are excluded from this comparison.
2. The analysis uses versioned, configurable pose-comparison thresholds. It records the analysis version, thresholds,
   per-bone maxima, offending bones, raw eligibility result, effective loop intent, and any override with its provenance
   in the metrics sidecar and processed index.
3. Effective loop intent derives from the persisted eligibility result. An override is permitted only when explicitly
   recorded with its provenance; it must not silently replace the analysis result.
4. Every selected standing action uses its persisted effective intent. A content role that must remain held, including
   an in-place pivot, must select a loop-eligible action; an ineligible action cannot satisfy that role. A loop-eligible
   action has the Blender action-name suffix `-loop` before Godot import; an ineligible action does not. The suffix is
   an internal Blender/import marker only.
5. The `-loop` marker must be stripped before any external action selection, catalogue key, package-manifest field, or
   runtime reference is emitted. It is not part of the externally selectable animation identity.
6. ANIM-003 normatively consumes the persisted loop intent for its Godot `Animation` resource loop mode, regenerated
   resource preservation, and runtime graph consumption boundary; it does not independently classify clips.
7. For a loop-eligible action, repeated-cycle Root translation and yaw deltas must remain continuous at the wrap. This
   does not require moving Root transforms to return to their initial values. A held in-place pivot retains zero Root
   translation and continuous role-consistent yaw through every wrap.

### Derived Mirror Contract

1. Native matched source pairs are preferred. A derived mirror is permitted only when the content specification vets the
   source as natural and records why no approved native counterpart is used.
2. The derivation reflects every animated transform across the sagittal plane. It reverses Root planar `X` translation
   and Root yaw sign, swaps left/right bone tracks, and swaps left/right metric and foot-contact channels.
3. The processor recomputes all derived metrics from the reflected action. It must not relabel, copy, or sign-edit the
   source metrics as a substitute for recomputation.
4. A derived action has a distinct identity, `derived_mirror_<source_motion_id>`, and must not be represented as a
   native source action. Its processed-index and package provenance records identify the source motion and action,
   derivation type, canonical reflection recipe, SHA-256 source-artifact hash, and SHA-256 canonical-recipe hash.
5. Derived processing preserves the normal Root reconstruction invariants, including planar Root motion, no vertical
   drift or local-X tilt beyond the historical checks, child-pose compensation, and post-save validation. It also
   preserves the source loop intent; reflection cannot make an ineligible clip cyclic.
6. Runtime consumers receive a fully processed derived action. They must not mirror an action, metrics, contacts, Root
   motion, or yaw at runtime.

### Batch Processing and Reusable Outputs

1. `tools/process_mixamo_animations.py` processes enabled selection rows and supports `--motion-id`, `--group`,
   `--force`, `--dry-run`, and the exceptional root-reconstruction debug option.
2. A dry run validates inputs and prints planned actions, groups, root-motion flags, and source basenames without
   writing processed outputs.
3. Native external action identities are `mixamo_<motion_id>` with hyphens replaced by underscores. Derived mirrors use
   the distinct identity defined by the derived-mirror contract. An action with persisted loop intent appends the
   internal `-loop` suffix under the source-loop contract; the suffix is not external.
4. Each configured output directory contains:
   - `<group>.blend` files, each retaining one shared target armature and multiple persistent actions;
   - `metrics/<action>.metrics.json` sidecars;
   - `index.json`, updated after each successfully processed item.
5. Temporary single-clip outputs are merged into group files. Metrics are validated before and after their final move.
   A successfully indexed item can be skipped on a later run only when the expected group and metrics outputs exist;
   metrics schema and tags match; action, group, group output path, metrics output path, category, motion class, gender,
   and create-root-motion flag match; the processor version matches; and status is successful. Source identity,
   manifest metadata beyond these fields, and selection notes are not current-entry invalidation checks.
6. An unfiltered forced run resets outputs represented by that selection before rebuilding them. Unforced or filtered
   runs do not perform this full-run reset; filtered runs preserve unrelated groups and index entries.
7. The processed index uses its implemented `schema_version`, `processor_version`, `metrics_schema_version`,
   `selection`, and `motions` structure. Each native motion entry records source identity and portable source basename,
   action, group, group `.blend`, metrics path, selection metadata, persisted loop-intent analysis, status, processor
   version, and processing time. A derived entry additionally records the derived identity and the derivation provenance
   required by the derived-mirror contract.
8. Processed metrics and indexes must not contain the supplied local Mixamo source directory, selected absolute FBX
   path, or temporary processing paths.

### Motion Metrics

1. Metrics schema version 2 includes `schema_version`, `action`, `manifest`, `root_source`, `root_created`, `fps`,
   `frame_range`, `sample_count`, `tracked_bones`, `bone_names`, `coordinate_space`, `head_height_norm`,
   `clip_head_height_norm`, `foot_contact`, `feature_schema`, `clip`, `samples`, and the enriched `selection` metadata.
   Normal reconstructed outputs also include `root_reconstruction`.
2. The coordinate-space value is `root_relative_blender_z_up_y_forward`. Tracked bones include `Root`, `pelvis`,
   `head`, both feet and balls, and both hands.
3. Each sample includes root, pelvis, head, foot, and hand positions; root-relative joint positions; sample motion
   features; root velocity; root-local planar velocity; root yaw delta and angular velocity; future trajectory samples;
   and left/right foot contact.
4. Future trajectory offsets are 0.2, 0.5, and 1.0 seconds. Sample motion features cover root, hips, left foot, and
   right foot positions, deltas, and velocities.
5. Contact uses the `low_slow` method. Clip-normalised head height uses `clip_minmax`.
6. Root-source values are:
   - `reconstructed_root` for normal reconstructed and sanitised output, with `root_created=true`;
   - `root_static` for a static source or output root;
   - `raw_root`, `pelvis_planar`, or legacy `root` only for the explicit debug path.
7. Normal processing rejects root sources other than `reconstructed_root`. A locomotion row with `root_static` fails
   validation.

### Godot Import Handoff

1. Group `.blend` outputs provide reusable target-rig actions for later Godot content packaging; ANIM-001 does not
   claim to create or refresh Godot `.res` clips or animation libraries.
2. When a future content spec imports a processed MakeHuman/MPFB reference-rig `.blend`, it must use the MakeHuman bone
   map resource at `res://assets/characters/reference/skeleton_profiles/bone_map_makehuman.tres` with UID
   `uid://db42k2j8v05ku`.
3. The reusable Godot import settings are:
   ```gdscript
   "retarget/bone_map": Resource(
       "uid://db42k2j8v05ku",
       "res://assets/characters/reference/skeleton_profiles/bone_map_makehuman.tres"
   ),
   "retarget/rest_fixer/fix_silhouette/enable": true,
   ```
4. Silhouette fixing is required because the Godot profile expects a T-pose while the reference MakeHuman/MPFB rig
   uses an A-pose. The compatibility filter is:
   ```gdscript
   "retarget/rest_fixer/fix_silhouette/filter": [
       &"Head",
       &"Neck",
       &"UpperChest",
       &"Chest",
       &"Spine",
       &"Hips",
       &"RightThumbMetacarpal",
       &"RightThumbProximal",
       &"RightThumbDistal",
       &"RightIndexProximal",
       &"RightIndexIntermediate",
       &"RightIndexDistal",
       &"RightMiddleProximal",
       &"RightMiddleIntermediate",
       &"RightMiddleDistal",
       &"RightRingProximal",
       &"RightRingIntermediate",
       &"RightRingDistal",
       &"RightLittleProximal",
       &"RightLittleIntermediate",
       &"RightLittleDistal",
       &"LeftThumbMetacarpal",
       &"LeftThumbProximal",
       &"LeftThumbDistal",
       &"LeftIndexProximal",
       &"LeftIndexIntermediate",
       &"LeftIndexDistal",
       &"LeftMiddleProximal",
       &"LeftMiddleIntermediate",
       &"LeftMiddleDistal",
       &"LeftRingProximal",
       &"LeftRingIntermediate",
       &"LeftRingDistal",
       &"LeftLittleProximal",
       &"LeftLittleIntermediate",
       &"LeftLittleDistal",
       &"RightFoot",
       &"LeftFoot"
   ]
   ```
   This filter remains mandatory unless a skeleton-profile specification replaces it.
5. Concrete contained-animation `save_to_file/*` settings, extracted clip paths, refresh behaviour, and animation
   library assembly belong to ANIM-003.

## In Scope

- Mixamo acquisition metadata, resumable downloads, and bearer-token handling.
- Stable Mixamo source and curated selection CSV schemas.
- Four-frame source preview generation.
- Direct Mixamo-to-target-rig retargeting and metadata-led root reconstruction.
- Class-specific Root reconstruction and validation for turn-in-place, straight/side-step, and walk-arc motion.
- Filtered, dry-run, interruptible, and resumable batch processing.
- Reusable grouped `.blend`, metrics-sidecar, and processed-index schemas.
- Reproducible derived-mirror processing, metrics recomputation, and provenance for content-approved sources.
- The reusable Godot retarget-import handoff boundary.

## Out Of Scope

- Concrete standing-locomotion selections and subjective clip-selection choices; future ANIM-003 owns them.
- `locomotion_standing.blend`, extracted per-clip `.res` resources, their refresh settings and paths, and the concrete
  animation library; future ANIM-003 owns these outputs.
- Runtime locomotion, motion matching, pose search, blend policy, state machines, and navigation integration.
- Non-Mixamo acquisition or retargeting providers and any multi-provider abstraction.
- Complete Godot import automation.
- Final preview, reconstruction, contact, or quality tuning values, provided the required processing, metrics,
  generated outputs, validation, and visual evidence remain delivered.
- Character-rig topology or MakeHuman bone-map changes.
- No ownership boundary or out-of-scope item excludes processing, metrics, generated assets, validation, regeneration,
  or visual evidence needed to deliver a selected locomotion input.

## Acceptance Criteria

### User Requirement Acceptance

1. `download_mixamo.py` can resume acquisition and writes `<motion_id>.fbx`, a downloader-run manifest, catalogue CSV,
   and state without embedding bearer-token values in documented or processed artefacts; reconciliation produces the
   stable processing manifest with portable relative `file` values or basenames before processing.
2. `generate_mixamo_previews.py` renders all or filtered downloaded FBX files as four-frame PNG panels and reports
   per-file failures.
3. A contributor can curate processing through the documented selection CSV and use a dry run to review filtered work.
4. A selected batch produces reusable grouped target-rig actions, metrics, and an index without manual per-clip Blender
   edits or persisted local source and temporary paths.
5. Moving, turn-in-place, and stationary examples follow selection metadata; reconstruction preserves non-Root poses
   through child compensation and reports source-track disagreements without replacing policy. Representative pivot,
   straight/side-step, and arc captures evidence visible direction and progress against their graph-role semantics.
6. An interrupted run can continue by skipping outputs that the historical current-entry checks still validate.
7. Loop eligibility is visibly derived from retained non-Root target-bone start/end poses; Root translation and yaw do
   not make a clip loop-eligible or ineligible, any override is traceable, and looped Root-motion deltas remain
   continuous at each wrap. A held pivot is accepted only from a loop-eligible action.
8. No acceptance step requires a concrete standing-locomotion selection, Godot `.res` clip, or animation library.

### Technical Requirement Acceptance

1. The historical manifest and selection readers accept the exact documented headers and reject malformed headers,
   invalid booleans, and duplicate source-manifest `motion_id` values. For enabled rows remaining after active motion
   and group filters, they reject dangling source-manifest references, missing groups, and missing selected FBX files;
   duplicate selection `motion_id` values are not required to be rejected.
2. Downloader checks confirm configurable outputs, resumable completion/failure state, non-empty temporary-file
   replacement, catalogue fields, bearer precedence, and `<motion_id>.fbx` naming.
3. Preview checks confirm source-directory and repeated-file selection, four sampled frames, configurable rendering,
   existing-output skipping, forced regeneration, and non-zero failure reporting.
4. Single-clip retarget checks confirm direct preset-based baking, persistent deterministic action naming, source-data
   cleanup, root canonicalisation, child-pose compensation, finite tolerances, and fresh-file root persistence.
5. `check_mixamo_root_policy.py` confirms that source Root tracks are not blindly adopted and that metadata is not the
   sole yaw author. It validates visible/body heading from the evaluated physical Root tail rather than raw Euler-Z or
   `matrix_basis`, and applies `root_euler_z = -visible_heading_delta` for the canonical `+Y` Root tail. It proves the
   named right pivot has clockwise-positive visible/body heading and negative canonical Root Euler-Z, with the left
   pivot proving the opposite signs. It also proves representative left/right arcs use the same representation while
   retaining role-consistent travel. Pivots remain at Root `(0, 0, 0)` with only role-consistent signed yaw; straight
   and side-step roles retain their deliberate canonical-heading/no-unintended-yaw policy; and arcs use
   role-consistent planar translation and source-informed signed-yaw direction and progress. Obsolete delta-only
   comparison or evidence helpers are removed or replaced. Representative pivot, straight/side-step, and arc captures
   are retained as validation evidence.
6. Batch checks confirm enabled-row processing, motion and group filters, write-free dry runs, reset on an unfiltered
   forced run but no full-run reset without force or with active filters, and per-item index updates. Current-entry
   skipping checks output existence; metrics schema and tags; action; group; group and metrics output paths; category;
   motion class; gender; create-root-motion flag; processor version; and successful status.
7. Metrics validation confirms schema version 2, required top-level and sample fields, selection metadata, normal
   `reconstructed_root` provenance, rejection of static locomotion, and absence of absolute source paths.
8. Generated group files contain one shared target armature and persistent actions; generated index entries resolve the
   expected group and metrics outputs without depending on temporary files, and processed source references use
   portable basenames rather than downloader-run or machine-local paths.
9. Ownership review confirms ANIM-001 does not require `locomotion_standing.blend`, extracted `.res` clips, a concrete
   animation library, a runtime consumer, complete Godot import automation, or a provider-extensible redesign.
10. Source-loop inspection confirms each action's evaluated non-Root start/end pose comparison, Root translation/yaw
    exclusion, versioned thresholds, maxima, offending bones, raw result, effective intent, and override provenance in
    both metrics and index entries. It confirms that only persisted loop-intent actions use the internal Blender `-loop`
    suffix and that the marker is absent from processed external selection and catalogue identities. Repeated-cycle
    Root-motion checks confirm continuous translation and yaw deltas at loop wraps, including zero-translation,
    role-consistent-yaw held pivots.
11. Derived-mirror inspection confirms sagittal reflection, Root `X` and yaw sign reversal, left/right bone, metric,
    and contact swaps, recomputed metrics, distinct derived identity, and complete source, recipe, and SHA-256
    provenance. It also confirms preserved Root invariants and preserved loop intent.
12. Focused root-policy tests and regeneration from the same selected inputs reproduce the class-specific Root policy,
    evaluated-tail heading evidence, metrics, generated outputs, and representative validation evidence without manual
    per-clip repair.

## References

- [ANIM: Animation](../index.md)
- [CHAR-001: Character Skeleton Profile](../../character/001-character-skeleton/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- `tools/download_mixamo.py`
- `tools/generate_mixamo_previews.py`
- `tools/retarget_mixamo_animation.py`
- `tools/process_mixamo_animations.py`
- `tools/mixamo_root_policy.py`
- `tools/check_mixamo_root_policy.py`
