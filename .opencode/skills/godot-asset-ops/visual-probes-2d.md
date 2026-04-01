# Visual Probes: 2D Scenes and UI

Use this flow when you need screenshot artefacts for objective visual verification of 2D content.

## Example: Splash Screen Fade-In

This probe loads the splash scene, captures an initial frame, waits three seconds, then captures the faded-in frame.

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

## Run Command

Run visual probe scripts **without** `--headless` to enable the renderer:

```bash
godot-mono -d -s --xr-mode off --path game "temp/<probe_script>.gd"
```

## Screenshot Output

`ProbeUtils.capture_screenshot(...)` writes JPG files under:

- `--output-dir <path>` / `--output-dir=<path>`, when provided
- otherwise `@game/temp` (resolves to `temp/` under the Godot project root)

Each successful capture prints:

- `Saved a screenshot: <absolute-path>`
