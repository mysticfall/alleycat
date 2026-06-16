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
- Collider generation follows the body mesh and maintains proper orientation.
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
- Auto-detect Rigify source blends matching `{character_name}.rigify` pattern.
- Retarget and bake Rigify actions from the source onto the MPFB-generated armature.
- After baking, remove Rigify source objects/actions; retain only persisted local/baked actions on the armature.
- Generate a sibling collider file using `<stem>.colliders.blend` via `tools/generate_body_colliders.py`.
- The collider generation process uses the generated `.body` mesh as input.
- Reset armature/body pose state before collider mesh generation.
- Strip animation actions and linked libraries from collider output.
- Recalculate collider normals facing outward using `bpy.ops.mesh.normals_make_consistent(inside=False)`.
- Provide a wrapper script `tools/generate_character.sh` that honours the `BLENDER_BIN` environment variable.
- The test configuration is located at `game/assets/characters/test/Female.character.json`.
- Generated Blender assets must use the configured character name for Rigify source detection and exported mesh names.
- Generated collider assets must remain paired with the generated character output by filename stem.
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

## Out Of Scope
- Creating new character presets or modifying existing ones.
- Manual character sculpting or mesh editing.
- Animation creation or keyframe editing outside of baking.
- Direct Godot scene generation or installer execution from this Blender generation script.
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
  - [ ] Unsafe paths (absolute or containing `..`) in the configuration are rejected with an error.
  - [ ] The wrapper script correctly uses the `BLENDER_BIN` environment variable when set.
  - [ ] Generated assets use configured character names instead of requiring `Female` source names.
  - [ ] Generated assets can be consumed by downstream portable scene assembly without mesh or rig renaming.
- Technical Requirements:
  - [ ] Configuration schema validation requires exactly preset, name, outputFile, and amimations fields.
  - [ ] All file paths are resolved relative to the @game directory.
  - [ ] Rigify source auto-detected by `{character_name}.rigify` pattern match.
  - [ ] Rigify source objects/actions removed; generated armature retains only persisted local/baked actions.
  - [ ] Collider generation follows the prescribed workflow via the dedicated script.
  - [ ] Armature/body pose state is reset before collider mesh generation.
  - [ ] Collider output strips animation actions and linked libraries.
  - [ ] Mesh normals on colliders are recalculated with outward consistency.
  - [ ] Generated Blender and collider output filenames remain paired by stem for downstream scene assembly.
  - [ ] Godot installer-backed scene generation is handled by CORE-005 workflows, not this Blender script.

## References
- Source script: `tools/generate_character.py`
- Wrapper script: `tools/generate_character.sh`
- Test configuration: `game/assets/characters/test/Female.character.json`
- Collider generation: `tools/generate_body_colliders.py`
- Portable character contract: @specs/character/001-character-skeleton/index.md
- Scene Installer System: @specs/core/005-scene-installer-system/index.md
