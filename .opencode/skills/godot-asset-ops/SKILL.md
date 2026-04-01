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
- **Photobooth setup** — `setup_photobooth` (loads scene, attaches subject, returns setup object)
- **Camera helpers** — `position_camera`, `set_camera_perspective`, `set_camera_orthographic`
- **Wait helpers** — `wait_frames`, `wait_seconds`
- **Capture** — `capture_screenshot` (JPG)

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

### Visual Verification of 2D Scenes

Use this when you need screenshot artefacts for objective visual verification during development.

This probe loads the splash scene, captures an initial frame, waits three seconds, then captures the faded-in frame:

```gdscript
extends SceneTree

func _init() -> void:
	await run_probe()


func run_probe() -> void:
	var splash_root: Node = ProbeUtils.instantiate_scene("res://assets/ui/splash_screen.tscn")

	root.add_child(splash_root)

	await ProbeUtils.capture_screenshot(self, "ui_001_splash_init.jpg")

	await ProbeUtils.wait_seconds(self, 3.0)

	await ProbeUtils.capture_screenshot(self, "ui_001_splash_fade_in.jpg")

	quit(0)
```

Run visual probe scripts without the `--headless` flag:

```bash
godot-mono -d -s --xr-mode off --path game "scripts/visual_probes/ui_001_splash_visual_probe.gd"
```

### Visual Verification of 3D Scenes

Use this when you need screenshot artefacts of 3D content — characters, props, poses, IK setups, etc.

The photobooth (`@game/assets/testing/photobooth/photobooth.tscn`) provides a studio HDRI environment,
a floor, a camera, and a `SubjectAnchor` for placing the subject.

`setup_photobooth` handles all boilerplate: it loads the photobooth, instantiates the subject, parents it to the
anchor, and returns a `PhotoboothSetup` with `.camera` and `.subject` references.

Uses the reference female character by default; pass a custom `subject_path` for any scene
(characters, props, effects, etc.).

#### Multi-angle IK Verification Example

This simulates an IK elbow check with orthographic captures from three angles:

```gdscript
extends SceneTree

## Approximate right-elbow height on the T-posed reference character.
const ELBOW_HEIGHT := 1.15
## Orthographic visible height — tight framing around the arm region.
const ORTHO_SIZE := 0.8


func _init() -> void:
	await run_probe()


func run_probe() -> void:
	var setup := ProbeUtils.setup_photobooth(self)

	# Apply IK pose changes to setup.subject here.

	ProbeUtils.set_camera_orthographic(setup.camera, ORTHO_SIZE)

	# Front view
	ProbeUtils.position_camera(setup.camera, Vector3(0, ELBOW_HEIGHT, 2), Vector3(0, ELBOW_HEIGHT, 0))
	await ProbeUtils.wait_frames(self, 3)
	await ProbeUtils.capture_screenshot(self, "ik_elbow_front.jpg")

	# Side view
	ProbeUtils.position_camera(setup.camera, Vector3(2, ELBOW_HEIGHT, 0), Vector3(0, ELBOW_HEIGHT, 0))
	await ProbeUtils.wait_frames(self, 3)
	await ProbeUtils.capture_screenshot(self, "ik_elbow_side.jpg")

	# Top-down view
	ProbeUtils.position_camera(setup.camera, Vector3(0, ELBOW_HEIGHT + 2, 0), Vector3(0, ELBOW_HEIGHT, 0))
	await ProbeUtils.wait_frames(self, 3)
	await ProbeUtils.capture_screenshot(self, "ik_elbow_top.jpg")

	quit(0)
```

#### Using a Custom Subject

Pass a `subject_path` to `setup_photobooth` for any scene — characters, props, effects, etc.:

```gdscript
# A different character
var setup := ProbeUtils.setup_photobooth(self, "res://assets/characters/my_character.tscn")

# A prop or any other 3D scene
var setup := ProbeUtils.setup_photobooth(self, "res://assets/props/table.tscn")
```

## Running Visual Probes

All visual probe scripts must run **without** `--headless` to enable the renderer:

```bash
godot-mono -d -s --xr-mode off --path game "temp/<probe_script>.gd"
```

`ProbeUtils.capture_screenshot(...)` writes JPG files under:

- `--output-dir <path>` / `--output-dir=<path>`, when provided
- otherwise `@game/temp` (resolves to `temp/` under the Godot project root)

Each successful capture prints:

- `Saved a screenshot: <absolute-path>`

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
