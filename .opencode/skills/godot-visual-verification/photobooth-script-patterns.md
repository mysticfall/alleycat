# Photobooth Script Patterns

Use these patterns to create and run photobooth-based verification scenes.

## Pattern 1: Create Or Update A Photobooth Test Scene

```gdscript
extends SceneTree

const BASE_SCENE := "res://assets/characters/reference/female/photobooth/full_body_5_cams.tscn"
const OUTPUT_SCENE := "res://tests/characters/example/example_pose_test.tscn"


func _init() -> void:
	# Set inherit=true to create a new inherited scene from BASE_SCENE.
	var photobooth: Photobooth = SceneUtils.instantiate_scene(BASE_SCENE, true) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("Failed to instantiate photobooth base scene")
		return

	# Optional: add/update markers.
	var head_marker: DebugMarker = photobooth.add_marker("HeadTarget", "Head")
	head_marker.position = Vector3(0.0, 1.6, 0.2)

	# Optional: add/update cameras.
	var front_camera: CameraRig = photobooth.get_camera_rig("FrontCamera")
	front_camera.image_size = Vector2i(512, 768)

	SceneUtils.save_scene(photobooth, OUTPUT_SCENE)
	photobooth.free()
	quit(0)
```

## Pattern 2: Verify Camera/Marker Framing

```gdscript
extends SceneTree

const TEST_SCENE := "res://tests/characters/example/example_pose_test.tscn"


func _init() -> void:
	var photobooth: Photobooth = SceneUtils.instantiate_scene(TEST_SCENE) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("Failed to instantiate test scene")
		return

	root.add_child(photobooth)
	await SceneUtils.wait_frames(self, 2)

	# Show only markers relevant to this framing pass.
	photobooth.get_marker("HeadTarget").visible = true

	# Capture per camera to verify framing before feature checks.
	await photobooth.get_camera_rig("FrontCamera").capture_screenshot("framing/front_camera.jpg")
	await photobooth.get_camera_rig("LeftCamera").capture_screenshot("framing/left_camera.jpg")
	await photobooth.get_camera_rig("RightCamera").capture_screenshot("framing/right_camera.jpg")

	quit(0)
```

## Pattern 3: Feature Scenario Capture Runner

```gdscript
extends SceneTree

const TEST_SCENE := "res://tests/characters/example/example_pose_test.tscn"


func _init() -> void:
	var photobooth: Photobooth = SceneUtils.instantiate_scene(TEST_SCENE) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("Failed to instantiate test scene")
		return

	root.add_child(photobooth)
	await SceneUtils.wait_frames(self, 2)

	# Scenario A.
	_apply_state_a(photobooth)
	await SceneUtils.wait_frames(self, 2)
	await photobooth.capture_screenshots("example_pose/state_a")

	# Scenario B.
	_apply_state_b(photobooth)
	await SceneUtils.wait_frames(self, 2)
	await photobooth.capture_screenshots("example_pose/state_b")

	quit(0)


func _apply_state_a(_photobooth: Photobooth) -> void:
	pass


func _apply_state_b(_photobooth: Photobooth) -> void:
	pass
```

## Naming Rule

Always keep test scene and runner names aligned:

- `example_pose_test.tscn`
- `example_pose_test.gd`

Store both in the same `@game/tests/<feature>/...` directory.

## Review Loop

For each runner execution:

1. Confirm the runner was executed **without `--headless`** and expected files are generated.
2. Visually inspect representative screenshots using the `read` tool to confirm expected behaviour.
   File existence alone is not sufficient evidence.
3. Compare screenshots across scenarios: distinct scenarios must produce visually distinct results.
4. Iterate scene setup/feature implementation until visual output is correct.
5. Then add or update the corresponding C# integration test.
