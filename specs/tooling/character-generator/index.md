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
- Generated characters import with the eye animation support required by runtime eye and face systems.
- When Godot has not created import sidecars yet, users receive a clear rerun workflow instead of fabricated data.
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
  `import_script/path="res://assets/characters/import/eye_animation_library_import.gd"` so Godot post-import adds the
  required `eyes` `AnimationLibrary` for runtime validation.
- Root import metadata updates must apply only to the main `.blend.import` sidecar, never to the
  `<stem>.colliders.blend.import` sidecar.
- Collider `.blend.import` sidecars must not receive the eye animation import script by default.
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
- Godot scene assembly is performed by downstream Scene Installer System (CORE-005) workflows that:
  - create or refresh the visual/import root node used as the scene root;
  - delegate skeleton, animation, collider, and gameplay setup to module installers;
  - keep reusable topology visible in template scenes/assets for inspection and testing.

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
- Preserving and applying the eye animation import-script sidecar contract for generated character `.blend` outputs.

## Out Of Scope
- Creating new character presets or modifying existing ones.
- Manual character sculpting or mesh editing.
- Animation creation or keyframe editing outside of baking.
- Direct Godot scene generation or installer execution from this Blender generation script.
- Creating first-import Godot UID, remap, dependency, or imported `.scn` sidecar data before Godot has imported assets.
- User interface for configuration (strictly JSON-driven).
- Support for other character generation systems beyond MPFB/MakeHuman.
- Real-time viewport rendering or interactive feedback during generation.
- Defining character-specific gameplay attributes or abilities.

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
  - [ ] Generated characters import with the required `eyes` animation library for runtime eye and face systems.
  - [ ] Missing sidecars produce a clear instruction to run Godot import and rerun the helper or generator.
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
  - [ ] The main sidecar contains the `eye_animation_library_import.gd` import-script path without dropping unrelated
        import metadata.
  - [ ] Collider sidecar updates do not receive main character root metadata.
  - [ ] Collider sidecar updates do not receive the eye animation import-script path unless explicitly required.
  - [ ] Existing main `Skeleton3D` node entries, or the generated-name fallback when absent, and the collider
        `Skeleton3D` entry contain the reference MakeHuman bone map and silhouette settings.
  - [ ] Missing sidecars are reported without writing synthetic Godot remap, UID, dependency, or `.scn` data.
  - [ ] Godot installer-backed scene generation is handled by CORE-005 workflows, not this Blender script.

## References
- Source script: `tools/generate_character.py`
- Wrapper script: `tools/generate_character.sh`
- Test configuration: `game/assets/characters/test/Female.character.json`
- Collider generation: `tools/generate_body_colliders.py`
- Portable character contract: @specs/character/001-character-skeleton/index.md
- Character root import contract: @specs/character/002-character-root/index.md
- Scene Installer System: @specs/core/005-scene-installer-system/index.md
