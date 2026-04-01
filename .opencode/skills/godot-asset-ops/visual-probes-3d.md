# Visual Probes: 3D Photobooth

Use this flow when you need screenshot artefacts of 3D content (characters, props, poses, IK setups, etc.).

The photobooth (`@game/assets/testing/photobooth/photobooth.tscn`) provides a studio HDRI environment,
a floor, a camera, and a `SubjectAnchor` for placing the subject.

Create it via `ProbeUtils.setup_photobooth(tree)`, then load a subject explicitly with `photobooth.load(subject_path)`.

## Inline Example: Portrait and Landscape Capture

```gdscript
extends SceneTree

const SUBJECT_PATH := "res://assets/characters/reference/reference_female.tscn"
const PORTRAIT_CAMERA_POSITION := Vector3(0.0, 1.45, 2.1)
const LANDSCAPE_CAMERA_POSITION := Vector3(1.9, 1.25, 1.9)


func _init() -> void:
	await run_probe()


func run_probe() -> void:
	var photobooth: ProbeUtils.Photobooth = ProbeUtils.setup_photobooth(self)
	if photobooth == null:
		return

	var subject: Node3D = photobooth.load(SUBJECT_PATH)
	if subject == null:
		return

	# Let transforms settle after attaching the subject.
	await ProbeUtils.wait_frames(self, 1)

	# Optional subject edits before capture.
	subject.rotation_degrees = Vector3(0.0, 18.0, 0.0)

	# Portrait capture using automatic look-at (subject AABB centre).
	photobooth.use_portrait()
	photobooth.set_perspective(38.0)
	photobooth.position_camera(PORTRAIT_CAMERA_POSITION)
	await ProbeUtils.wait_frames(self, 3)
	await photobooth.capture("3d_001_portrait.jpg")

	# Landscape capture with explicit look-at.
	photobooth.use_landscape()
	photobooth.position_camera(LANDSCAPE_CAMERA_POSITION, subject.global_position + Vector3(0.0, 1.0, 0.0))
	await ProbeUtils.wait_frames(self, 3)
	await photobooth.capture("3d_001_landscape.jpg")

	photobooth.dispose()
	quit(0)
```

## Run Command

Run visual probe scripts **without** `--headless` to enable the renderer:

```bash
godot-mono -d -s --xr-mode off --path game "temp/<probe_script>.gd"
```

## Screenshot Output

`ProbeUtils.capture_screenshot(...)` and `Photobooth.capture(...)` write JPG files under:

- `--output-dir <path>` / `--output-dir=<path>`, when provided
- otherwise `@game/temp` (resolves to `temp/` under the Godot project root)

Each successful capture prints:

- `Saved a screenshot: <absolute-path>`
