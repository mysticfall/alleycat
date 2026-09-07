# Character Generator

## Requirement
Generate MPFB/MakeHuman character presets as Blender files with rigging, collider generation, and action processing.
Generated source assets must provide stable naming and collider outputs for downstream portable character assembly.

## Goal
Generate MPFB/MakeHuman character presets as Blender files with automated rigging for AlleyCat VR.
The generator produces source assets that downstream Godot scene assembly can bind through the portable character
contract. Godot scene installation and refresh workflows use the Scene Installer System (CORE-005) to materialise
runtime/editor-visible nodes such as animation trees, attachments, hand anchors, and physical rigs.

## User Requirements
- Artists and developers can generate character Blender files from JSON configuration presets.
- Generated characters include proper rigging with MPFB-generated armature, collision meshes, and baked actions.
- Generated source assets use the configured character name so downstream Godot scenes do not require `Female` names.
- Generated source assets can be consumed by portable Godot scene assembly without manual mesh or rig renaming.
- The tool rejects unsafe file paths to prevent security issues.
- Externalised textures are saved alongside the output file for portability.
- HDRI environment configuration is applied to the generated scene.
- Non-retargeted actions are appended locally to the character.
- Rigify source animation blends expose their intended clips as persistent multi-frame actions and can be shared by
  generated characters with different output names, so opening or appending the source does not present the rig as only
  an unattached single-frame A-pose.
- Rigify retargeting accepts the current source action names and reports malformed, single-frame, or static/rest-pose
  clips before they can become generated character animations.
- Collider generation follows the body mesh and maintains proper orientation.
- Existing Godot import sidecars keep the MakeHuman retarget metadata needed for generated skeleton imports.
- Existing main Godot import sidecars keep or regain the `CharacterBody3D` root import contract required by CHAR-002.
- Generated characters import through a modular Godot post-import pipeline that assembles deterministic companion
  assets.
- Generated characters keep the eye animation support required by runtime eye and face systems.
- Generated characters with ready collider imports receive refreshed body collider wrapper/profile assets automatically.
- When Godot has not created import sidecars yet, users receive a clear rerun workflow instead of fabricated data.
- Contributors can manually install the generic MPFB forearm-twist source assets before ordinary character
  regeneration.
- Regenerated characters carry the RIG-002 forearm-twist helper bones — one per side, with no second helper
  bone — and hand attachment bindings remain intact.
- In an ordinarily regenerated character, selecting the left middle-finger group in Blender Edit Mode does not select
  right-hand body-mesh vertices, including after removing the right-hand and right-finger groups.
- Ordinary regeneration preserves ambiguous bilateral source ownership on both physical sides without moving finger
  or unrelated mass; the approved female finger correction is not a blanket wrong-side reassignment rule.
- Ordinary regeneration retains pre-existing zero-weight Blender group memberships in the saved character, except
  eligible, nonprotected same-side twist-helper zeros promoted to positive through conserved axial authoring and
  independently attributed opposite-hand finger zero ghosts removed from their groups. Every other original zero key,
  including protected and out-of-domain memberships, remains physical; a calculated zero creates no selectable group.
- Configuration uses a simple JSON schema with `preset`, `name`, `outputFile`, and `amimations` fields.

## Technical Requirements
- Accept a JSON configuration file with exactly these fields:
  - `preset`: Character preset identifier
  - `name`: Output name for the character
  - `outputFile`: Path for the generated Blender file (relative to @game)
  - `amimations`: (Note: intentionally misspelled as specified) Animation configuration
- Resolve all paths relative to the `@game` directory root.
- Reject absolute paths and paths containing parent traversal (`..`) for security.
- Generate the base character file at the specified outputFile location.
- Externalise textures by saving them beside the output file.
- Configure the project HDRI environment for proper lighting.
- Append non-retargeted actions as local Blender actions.
- Auto-detect Rigify source blends by preferring `{character_name}.rigify`, then accepting a single unique
  `*.rigify` source object for shared reference animation files.
- If a source blend contains multiple `*.rigify` objects and none match `{character_name}.rigify`, fail with a clear
  ambiguity diagnostic instead of appending Rigify actions as non-retargeted actions.
- Rigify source `.blend` files must persist source actions through fake users or muted/locked NLA references on the
  Rigify armature, rather than relying on an active action only.
- Rigify source action names may either use the legacy `-noexp` suffix or the final exported clip name. The generator
  must strip `-noexp` when present and safely handle suffixless source names without name collisions during baking.
- Rigify source actions and baked Rigify actions must be validated as multi-frame, keyed, and non-static before saving
  output. Static/rest-pose-only actions must fail with diagnostics naming the offending action.
- Retarget and bake Rigify actions from the source onto the MPFB-generated armature.
- After baking, remove Rigify source objects/actions; retain only persisted local/baked actions on the armature.
- Generate a sibling collider file using `<stem>.colliders.blend` via `tools/generate_body_colliders.py`.
- The collider generation process uses the generated `.body` mesh as input.
- Reset armature/body pose state before collider mesh generation.
- Strip animation actions and linked libraries from collider output.
- Recalculate collider normals facing outward using `bpy.ops.mesh.normals_make_consistent(inside=False)`.
- Provide a wrapper script `tools/generate_character.sh` that honours the `BLENDER_BIN` environment variable.
- The test configuration is located at `game/assets/characters/test/Female.character.json`.
- Generated Blender assets must use the configured character name for exported mesh names and must not require shared
  reference Rigify animation sources to be renamed per generated character.
- Generated collider assets must remain paired with the generated character output by filename stem.
- After writing the main and collider `.blend` files, update existing `.blend.import` sidecars derived from
  `outputFile` and `<stem>.colliders.blend`.
- Sidecar updates must preserve `[remap]`, `[deps]`, UIDs, imported `.scn` paths, animation subresources, and unrelated
  importer parameters.
- Sidecar updates must support both empty `_subresources={}` and populated `_subresources` dictionaries.
- The main import sidecar must set `nodes/root_type="CharacterBody3D"`, `nodes/root_name` to the configured character
  name, and `nodes/root_script` to the UID-backed `res://src/Character/Character.cs` reference required by CHAR-002.
- The main import sidecar must preserve or apply
  `import_script/path="res://assets/characters/import/character_import.gd"` so Godot runs the modular character
  post-import pipeline.
- `character_import.gd` orchestrates focused post-import modules and must preserve existing eye animation library
  generation through `character_eye_animation_import.gd`.
- When `<stem>.colliders.blend` has an imported scene ready, the post-import pipeline must create or refresh:
  - `<stem>_colliders.tscn` as the character collider wrapper scene;
  - `body_collider_profile.tres` as the body collider profile resource for the generated character.
- Collider wrapper/profile asset generation must be deterministic and idempotent across repeated imports.
- Blender/Python generation and sidecar update tooling must not directly serialise Godot scenes/resources or fabricate
  Godot UID, remap, dependency, import, or imported-scene metadata.
- Root import metadata updates must apply only to the main `.blend.import` sidecar, never to the
  `<stem>.colliders.blend.import` sidecar.
- Collider `.blend.import` sidecars must not receive the character post-import script by default.
- The main import sidecar must detect existing `_subresources.nodes` entries ending in `/Skeleton3D` and apply
  MakeHuman retarget metadata to those actual imported skeleton paths.
- If the main import sidecar has no existing skeleton node entry, it must fall back to
  `PATH:<configured character name>/Skeleton3D` as the intended generated-name path.
- Existing legacy main skeleton paths, such as `PATH:Female/Skeleton3D`, must be updated in place rather than replaced
  by an unused configured-name entry.
- The collider import sidecar must apply MakeHuman retarget metadata to `PATH:Ragdoll/Skeleton3D`.
- Required retarget keys are `retarget/bone_map`, `retarget/rest_fixer/fix_silhouette/enable`, and
  `retarget/rest_fixer/fix_silhouette/filter`, matching the reference female import sidecars.
- Missing sidecars must be reported with instructions to run Godot import and rerun the helper or generator.
  Tooling must not fabricate Godot UID or imported scene metadata.
- Root role installer scene/profile assignment remains a separate Scene Installer System (CORE-005) workflow unless a
  dedicated role-scene workflow handles it.
- Godot role scene assembly is performed by downstream Scene Installer System (CORE-005) workflows that:
  - create or refresh the visual/import root node used as the scene root;
  - delegate skeleton, animation, collider, and gameplay setup to module installers;
  - keep reusable topology visible in template scenes/assets for inspection and testing.
- Forearm-twist source rig generation follows [RIG-002: Forearm Twist](../../rigging/002-forearm-twist/index.md):
  generic source assets under `tools/mpfb/` target Blender 5.2, MPFB 2.0.17, and schema `110`; contributors install
  them manually, then use ordinary regeneration without direct generated-`.blend` edits or an automatic installation
  script.
- Helper-chain emission follows RIG-002: regenerated characters carry `LeftForearmTwist` and `RightForearmTwist`
  under the matching lower arms, with each matching hand re-parented to its twist helper, completing the per-side
  chain `LowerArm → ForearmTwist → Hand`. The generator emits no second helper bone. Helper rest placement is
  deterministic from the installed generic sources and wrist joint geometry, never hand-tuned per character.
- Twist helpers are excluded from `SkeletonProfileHumanoid`, BoneMap, and retarget and animation mapping, and
  remain deform-only.
- Immediately after MPFB `export_copy`, before cleanup or writing groups, snapshot each export mesh vertex's complete
  physical group membership map, including present zero-weight keys; keep this separate from the positive
  post-axial reference used for ownership and import validation. Before mirrored cleanup, protect complete physical
  group assignments on both sides of each ambiguous source-present bilateral row. Suffix, zero or small weight, and
  position do not prove exporter contamination. Cleanup may remove an independently identified exporter-introduced
  counterpart, but cannot transfer a positive source-present bilateral assignment without approved, independently
  justified row-specific attribution. If none is evidenced, perform no speculative positive transfer. Do not require
  a general per-vertex provenance system or invent source-to-export vertex correspondence; an uncorrelated bilateral
  row remains protected rather than inferred from position or suffix.
- The forearm-twist weight pipeline runs that safe cleanup, recensuses eligibility on the resulting input, and
  constructs axial lower-arm/twist-helper/hand ownership (including eligible rows with no original helper weight),
  snapshotting the completed axial distribution as the immutable ownership and import-validation reference.
  Exclude protected bilateral rows from axial authoring unless an independently justified, approved narrow
  attribution resolves them. Preserve their original full physical membership maps through output. Axial
  authoring may promote an existing zero twist-helper membership to positive only on an eligible, nonprotected
  same-side row, within the conserved lower-arm/helper/hand pool; verify axial construction conservation. All
  other pre-existing zero memberships persist in the saved `.blend` unless the row-specific finger-ghost
  exception below applies. Logical zero influence in a calculation must not create a new physical membership.
  The authored pool and protected physical channels are preserved through output. Finger, unrelated,
  opposite-side and out-of-domain influences remain protected at the applicable stage.
  `helper_fraction = 0.5` is the initial ownership setting, independently tunable from runtime `TwistWeight`
  within RIG-002's bounded visual review, not a fixed release value. Generation evidence does not certify runtime
  deformation quality or define visual thresholds.
- For the known right-hand source point (vertex 1945) in the project-owned female custom preset, remove its unintended
  positive `middle_01_l` assignment directly in that source. This bounded correction does not authorise moving other
  source-present bilateral assignments. During ordinary generation, remove a physical zero-weight opposite-hand
  finger membership before writing groups only when independent physical and anatomical evidence attributes that
  particular export row's membership as an exporter-introduced ghost. Suffix, zero magnitude, or position alone does
  not suffice. If no such ghost is established, generate normally without speculative deletion. Preserve the corrected
  same-side total and unrelated group assignments; do not edit generated `.blend` files by hand. The corrected female
  export has no zero finger memberships and does not itself evidence this conditional cleanup. This does not classify
  other bilateral source weights or prescribe global reassignment.
- Whenever bone insertion shifts the bone order, the regeneration pipeline refreshes stored `BoneAttachment3D`
  indices so `bone_idx` and `bone_name` agree at load (the RIG-002 bone-binding guard); the check tool and
  integration test prove the contract.

## In Scope
- JSON configuration parsing and validation.
- Blender background processing for character generation.
- Rigify rig detection, retargeting, and baking.
- Collider mesh generation and post-processing.
- Texture externalisation and HDRI configuration.
- Path safety validation (relative to @game, no absolute/parent traversal).
- Wrapper script execution environment handling.
- Ensuring generated source assets provide names and collider outputs required by portable scene assembly.
- Preserving and applying Godot import retarget metadata sidecars for generated character `.blend` outputs.
- Preserving and applying the CHAR-002 main character root import metadata for generated character `.blend` outputs.
- Preserving and applying the modular character import-script sidecar contract for generated character `.blend` outputs.
- Godot post-import generation of eye animation libraries and collider wrapper/profile companion assets.
- Generic MPFB forearm-twist source assets under `tools/mpfb/`, their manual installation, and ordinary regeneration.
- Twist-helper chain emission, exclusions, axial authoring and reference snapshot, and bone-binding index
  refresh for RIG-002.

## Out Of Scope
- Creating new character presets or modifying existing ones beyond the known female custom-source weight correction
  above.
- Manual character sculpting or mesh editing.
- Animation creation or keyframe editing outside of baking.
- Direct Godot scene/resource serialisation or installer execution from this Blender generation script.
- Creating first-import Godot UID, remap, dependency, or imported `.scn` sidecar data before Godot has imported assets.
- Patching root role installer scenes or assigning generated body collider profiles to role installer roots during the
  first modular post-import asset generation pass.
- User interface for configuration (strictly JSON-driven).
- Support for other character generation systems beyond MPFB/MakeHuman.
- Real-time viewport rendering or interactive feedback during generation.
- Defining character-specific gameplay attributes or abilities.
- MPFB source assets beyond the generic forearm-twist sources required by RIG-002.

## Acceptance Criteria
- User Requirements:
  - [ ] Running the generate script with config produces a valid Blender file at outputFile location.
  - [ ] The generated file contains a properly rigged character with MPFB-generated armature.
  - [ ] Collider mesh is generated as a sibling file with proper normals orientation.
  - [ ] Textures used by the character are externalised and saved beside the output file.
  - [ ] HDRI environment is configured in the generated scene.
  - [ ] Non-retargeted actions from the source are present in the output file.
  - [ ] Rigify source animations open or append as discoverable multi-frame clips rather than only an A-pose object,
        including when a generated character uses a shared reference `*.rigify` source object with a different name.
  - [ ] Rigify baking with current suffixless action names produces generated character animation clips or clear
        validation errors for malformed source data.
  - [ ] Unsafe paths (absolute or containing `..`) in the configuration are rejected with an error.
  - [ ] The wrapper script correctly uses the `BLENDER_BIN` environment variable when set.
  - [ ] Generated assets use configured character names instead of requiring `Female` source names.
  - [ ] Generated assets can be consumed by downstream portable scene assembly without mesh or rig renaming.
  - [ ] Existing Godot import sidecars keep or receive the MakeHuman retarget metadata after generation.
  - [ ] Existing main Godot import sidecars keep or receive the CHAR-002 character root metadata after generation.
  - [ ] Generated characters import through the modular character post-import pipeline, not an eye-only import script.
  - [ ] Generated characters import with the required `eyes` animation library for runtime eye and face systems.
  - [ ] When the matching collider import is ready, generated characters receive refreshed collider wrapper/profile
        assets without a manually invoked Godot script.
  - [ ] Root role installer scene/profile assignment remains handled by the role scene workflow, not by the first
        post-import asset generation pass.
  - [ ] Missing sidecars produce a clear instruction to run Godot import and rerun the helper or generator.
  - [ ] Regenerated reference characters contain the single forearm-twist helper per side with the re-parented
        hand chain and intact `BoneAttachment3D` bindings.
  - [ ] In the ordinarily generated female `.blend`, with all body-mesh vertices visible, selecting `middle_01_l`
        in Blender Edit Mode selects no right-hand vertices, both before and after deleting right-hand and
        right-finger groups; the left middle finger remains selectable.
  - [ ] Ordinary female/male regeneration preserves ambiguous bilateral source physical channels on both sides,
        without moving finger or unrelated ownership or weakening the RIG-002 twist visual gates.
  - [ ] Saved and reopened Blender characters retain pre-existing zero-weight group memberships outside eligible
        same-side helper promotion and independently attributed opposite-hand finger-ghost removal; calculated zeros
        introduce no selectable memberships.
  - [ ] Forearm-twist generation uses the RIG-002 manual generic-asset and ordinary-regeneration workflow.
- Technical Requirements:
  - [ ] Configuration schema validation requires exactly preset, name, outputFile, and amimations fields.
  - [ ] All file paths are resolved relative to the @game directory.
  - [ ] Rigify source auto-detected by `{character_name}.rigify` pattern match or by a single unique fallback
        `*.rigify` object, with ambiguous unmatched sources rejected before direct action appending.
  - [ ] Rigify source blends persist source actions through fake users or muted/locked NLA references on the Rigify
        armature.
  - [ ] Rigify baking accepts both legacy `-noexp` and suffixless source action names without silently overwriting or
        discarding action data.
  - [ ] Rigify source and baked actions are rejected when they are single-frame, unkeyed, or static/rest-pose-only.
  - [ ] Rigify source objects/actions removed; generated armature retains only persisted local/baked actions.
  - [ ] Collider generation follows the prescribed workflow via the dedicated script.
  - [ ] Armature/body pose state is reset before collider mesh generation.
  - [ ] Collider output strips animation actions and linked libraries.
  - [ ] Mesh normals on colliders are recalculated with outward consistency.
  - [ ] Generated Blender and collider output filenames remain paired by stem for downstream scene assembly.
  - [ ] Sidecar paths are derived from `outputFile` as `.blend.import` and `<stem>.colliders.blend.import`.
  - [ ] Sidecar updates merge into empty and populated `_subresources` blocks without dropping unrelated import data.
  - [ ] The main sidecar contains `nodes/root_type="CharacterBody3D"`, `nodes/root_name` matching the configured
        character name, and the UID-backed `Character.cs` `nodes/root_script` reference.
  - [ ] The main sidecar contains the `character_import.gd` import-script path without dropping unrelated
        import metadata.
  - [ ] Collider sidecar updates do not receive main character root metadata.
  - [ ] Collider sidecar updates do not receive the character post-import script path unless explicitly required.
  - [ ] The modular post-import pipeline invokes the eye animation import module and preserves generated `eyes`
        `AnimationLibrary` output.
  - [ ] The modular post-import pipeline creates or refreshes `<stem>_colliders.tscn` and `body_collider_profile.tres`
        from the `<stem>.colliders.blend` convention only when collider import output is available.
  - [ ] Repeated imports refresh the generated wrapper/profile assets deterministically without requiring manual Godot
        script execution.
  - [ ] Existing main `Skeleton3D` node entries, or the generated-name fallback when absent, and the collider
        `Skeleton3D` entry contain the reference MakeHuman bone map and silhouette settings.
  - [ ] Missing sidecars are reported without writing synthetic Godot remap, UID, dependency, or `.scn` data.
  - [ ] Blender/Python tooling does not serialise Godot scenes/resources or fabricate Godot UID, remap, dependency,
        import, or imported-scene metadata.
  - [ ] Godot installer-backed role scene generation is handled by CORE-005 workflows, not this Blender script.
  - [ ] Generic forearm-twist sources target Blender 5.2, MPFB 2.0.17, and schema `110`; their installation is manual,
        and the configuration schema remains exactly four fields.
  - [ ] Regenerated reference characters carry exactly one twist helper per side under the matching lower arm,
        with each hand re-parented to its twist helper, completing the `LowerArm → ForearmTwist → Hand` chain and
        no second helper bone; a disposable-generation test proves the regenerated assets through the ordinary
        pipeline without direct generated-`.blend` edits.
  - [ ] Twist helpers are excluded from `SkeletonProfileHumanoid`, BoneMap, and retarget and animation mapping.
  - [ ] Ordinary generation and focused tests snapshot every export-stage physical membership, including zero keys,
        before cleanup or writing; bilateral protection precedes scoped cleanup. Only independently evidenced
        exporter-added counterparts or approved row-specific corrections can change positive wrong-side assignments.
        Unproven source-present bilateral rows retain their complete physical maps and are excluded from axial
        authoring. Check source-to-export correspondence where available; do not assert exact row equivalence where
        it cannot be established. With no evidenced exporter-added assignment, cleanup makes no speculative
        positive transfer. Recensus eligibility after cleanup and before axial authoring, including eligible
        zero-helper rows; capture the immutable positive axial reference after authoring. Verify axial construction
        conservation, with protected physical maps and the post-authoring pool unchanged through output. Unrelated,
        finger, opposite-side and out-of-domain influences remain protected at the applicable stage. Positive
        ownership/import validation uses the axial reference, not the export-stage physical snapshot. Validator
        coverage confirms equivalence to that authored reference, not adequacy of the input distribution or runtime
        visual quality. `helper_fraction = 0.5` is only the initial ownership setting; RIG-002's bounded visual
        approval selects the release setting independently of runtime `TwistWeight`.
  - [ ] Comparing export-stage physical maps with saved and reopened `.blend` group memberships confirms every
        pre-existing zero key persists except eligible nonprotected same-side twist-helper zeros promoted to positive
        with conserved axial pool, or independently attributed opposite-hand finger ghosts removed row by row.
        Calculated logical zeros do not create physical keys; protected and out-of-domain maps remain intact.
  - [ ] The project-owned female custom source has no positive `middle_01_l` assignment at right-hand vertex 1945;
        an ordinary regeneration leaves that right-hand vertex unassigned to `middle_01_l`, not merely at zero
        weight. A synthetic attributed ghost tests conditional removal, conservation of same-side total and unrelated
        group assignments, and no manual generated-`.blend` edits; it is not evidence of a ghost in the female export.
        With no attributable ghost, generation succeeds without deleting pre-existing zero memberships.
  - [ ] After generator bone insertion, every `BoneAttachment3D` in emitted or updated templates satisfies stored
        `bone_idx` ↔ `bone_name` agreement at load (binding guard), proved by the check tool and integration
        test.

## References
- Source script: `tools/generate_character.py`
- Wrapper script: `tools/generate_character.sh`
- Test configuration: `game/assets/characters/test/Female.character.json`
- Collider generation: `tools/generate_body_colliders.py`
- Godot character post-import pipeline: `game/assets/characters/import/character_import.gd`
- Eye animation post-import module: `game/assets/characters/import/character_eye_animation_import.gd`
- Collider profile post-import module: `game/assets/characters/import/character_collider_profile_import.gd`
- Import sidecar metadata updater: `tools/update_character_import_retarget_metadata.py`
- Forearm-twist weight pipeline: `tools/forearm_twist_weights.py`
- Bone-binding guard: `tools/character_template_bone_bindings.py` and `tools/check_character_template_bone_bindings.py`
- Portable character contract: @specs/character/001-character-skeleton/index.md
- Character root import contract: @specs/character/002-character-root/index.md
- Scene Installer System: @specs/core/005-scene-installer-system/index.md
- Forearm twist: @specs/rigging/002-forearm-twist/index.md
