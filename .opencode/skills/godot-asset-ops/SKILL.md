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

Current helper intent:

- Scene load/instantiate helpers
- Required-node lookup helper
- Time/frame wait helpers
- Screenshot capture helper (JPG)
- Output directory resolution for captures

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

const ProbeUtils = preload("res://scripts/probe_utils.gd")

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
