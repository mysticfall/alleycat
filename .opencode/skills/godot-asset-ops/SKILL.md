---
name: godot-asset-ops
description: Use for all tasks related to creating, editing, or managing Godot scenes and resource assets.
---

# Godot Scene and Resource Operations

Use this skill to modify Godot scenes/resources through engine APIs, not by hand-editing serialised files.

## Why This Approach

- Manual edits to `.tscn`/`.tres` are fragile.
- API-driven edits preserve node ownership and resource integrity.

## Quick Workflow

1. Clarify target assets and expected outcome.
2. Use Context7 for uncertain Godot API details.
3. Write a task-specific GDScript under `@game/temp`.
4. Execute the script with `godot-mono` (always pass `--xr-mode off`).
5. Verify results.
6. Remove the temporary script unless asked to keep it.

## Temporary Script Rule

Always create a dedicated script before making scene/resource changes.

- Path pattern: `@game/temp/asset_ops_<short-task-name>.gd` (which resolves to
  `temp/asset_ops_<short-task-name>.gd`)
- Keep each script focused on one operation bundle.
- Do not patch serialised scene/resource text directly.

## SceneUtils (Shared Helper)

Use `@game/scripts/scene_utils.gd` (`SceneUtils`) when it helps reduce boilerplate.

Current helpers:

- **Scene helpers** — `load_scene`, `instantiate_scene`, `save_scene`, `require_node`
- **Wait helpers** — `wait_frames`, `wait_seconds`
- **Capture helpers** — `capture_screenshot`, `capture_viewport_screenshot`
- **Utility helpers** — `to_safe_file_component`, `fatal_error_and_quit`

For photobooth scene operations, use the `Photobooth` API on instantiated scenes:

- `add_camera_rig`, `get_camera_rig`, `remove_camera`
- `add_marker`, `get_marker`, `remove_marker`
- `capture_screenshots`

For per-camera captures, use `CameraRig.capture_screenshot`.

SceneUtils is fail-fast for setup and capture flows: on fatal errors it logs and quits with non-zero exit.

Use SceneUtils where useful, not only for visual probes. The same helpers are valid for general asset scripting
workflows (for example, scene instantiation, required node lookup, and controlled wait steps).

## Example Scenarios

### Scene/Resource Mutation

Use this when you need a safe and robust way to edit scenes and resources via Godot APIs.

```gdscript
extends SceneTree

func _init() -> void:
	var packed: PackedScene = load("res://assets/ui/splash_screen.tscn")
	if packed == null:
		push_error("Failed to load scene")
		quit(1)
		return

	var root := packed.instantiate()
	# Apply edits here.

	var out := PackedScene.new()
	if out.pack(root) != OK:
		push_error("Failed to pack scene")
		quit(1)
		return

	if ResourceSaver.save(out, "res://assets/ui/splash_screen.tscn") != OK:
		push_error("Failed to save scene")
		quit(1)
		return

	quit(0)
```

Use headless mode to run mutation scripts (for example `@game/temp/asset_ops_<task>.gd`):

```bash
godot-mono -d -s --headless --xr-mode off --path game "temp/asset_ops_<task>.gd"
```

## Inherited Scene Limitations

### Editing Children Of Inherited Sub-Scenes

When a scene inherits from a base scene (via `instance=ExtResource(...)`), adding nodes under an inherited sub-scene's
**internal** nodes (for example adding a child to a skeleton inside an imported `.blend` character) requires
Godot-specific serialisation features:

- `[editable path="Subject/Character"]` to mark the sub-scene as expanded for editing.
- `parent_id_path=PackedInt32Array(...)` to reference the correct parent by instance ID.

These features are difficult to produce correctly by hand-editing `.tscn` text. **Do not attempt manual `.tscn`
text editing for inherited scene modifications.** Instead:

1. Prefer the GDScript API approach (load, instantiate, modify, pack, save).
2. If the GDScript approach fails (for example because C# scripts cannot load in headless mode), **escalate to the
   user** and ask them to make the scene edit in the Godot editor.
3. Only hand-edit `.tscn` text for simple, non-inherited node additions where the parent is the scene root or a
   node owned by the scene root.

## Custom Type Script Metadata

When assigning a C# script to a node in a scene, if that script defines a custom node type, you must also set the
`_custom_type_script` metadata on the node to preserve custom type tracking in the serialised scene file.

Desired `.tscn` output format:

```
script = ExtResource("1_4of4h")
metadata/_custom_type_script = "uid://dv076impllmml"
```

### GDScript Pattern

After assigning the script to a node:

```gdscript
var script: Script = load("res://path/to/script.cs")
node.set_script(script)

# Get the UID and set the metadata
var script_uid: String = script.get_uid()
node.set_meta("_custom_type_script", script_uid)
```

## Verification Checklist

- Script exits with code `0`.
- Expected assets or screenshots are written to intended paths.
- Updated scenes/resources reload successfully.
- Requested nodes/properties/resources are present.

## Output Expectations

Report:

- Temporary script path used in `@game/temp` (or equivalent project-relative path).
- Assets changed (if any).
- What was validated.
- Follow-up action needed from the user (if any).
