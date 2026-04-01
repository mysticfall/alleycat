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
4. Execute the script with `godot-mono`.
5. Verify results.
6. Remove the temporary script unless asked to keep it.

## Temporary Script Rule

Always create a dedicated script before making scene/resource changes.

- Path pattern: `@game/temp/asset_ops_<short-task-name>.gd` (which resolves to
  `temp/asset_ops_<short-task-name>.gd`)
- Keep each script focused on one operation bundle.
- Do not patch serialised scene/resource text directly.

## ProbeUtils (Shared Helper)

Use `@game/scripts/probe_utils.gd` (`ProbeUtils`) when it helps reduce boilerplate.

Current helpers:

- **Scene helpers** — `load_scene`, `instantiate_scene`, `require_node`
- **Photobooth setup** — `setup_photobooth` (returns a `Photobooth` helper; call `load(subject_path)` to attach a
  subject)
- **Photobooth camera/capture** — `use_portrait`, `use_landscape`, `position_camera`, `set_perspective`,
  `set_orthographic`, `capture`
- **Camera helpers** — `position_camera`, `set_camera_perspective`, `set_camera_orthographic` (for non-photobooth use)
- **Wait helpers** — `wait_frames`, `wait_seconds`
- **Capture** — `capture_screenshot` (JPG from root viewport, mostly for 2D/UI probes)

ProbeUtils is fail-fast for probe flows: on fatal errors it logs and quits with non-zero exit.

Use ProbeUtils where useful, not only for visual probes. The same helpers are valid for general asset scripting
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

### Visual Probe Guides

Use a visual probe when you need objective screenshot evidence of runtime visuals (for example, layout state or
animation pose) instead of relying on textual assertions alone.

A visual probe is a small `SceneTree` script that loads the target scene, waits for the required state,
captures one or more JPG artefacts, then exits. Use the guide matching your rendering context:

- 2D scenes and UI capture: `@.opencode/skills/godot-asset-ops/visual-probes-2d.md`
- 3D photobooth capture: `@.opencode/skills/godot-asset-ops/visual-probes-3d.md`

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
