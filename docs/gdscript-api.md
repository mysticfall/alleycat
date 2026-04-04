# GDScript API Reference

Source Root: `game/scripts`

## Class CameraRig (`photobooth/camera_rig.gd`)
- Extends: `Node3D`
- Summary: Provides a configurable capture camera for visual scene testing in the photobooth workflow.

### Constants
- `DEFAULT_IMAGE_SIZE`

### Properties
- `projection: Camera3D.ProjectionType (@export)` — Camera projection mode used by the rig.
- `image_size: Vector2 (@export)` — Output image size used by the viewport.
- `orthogonal_scale: float (@export_range)` — Orthographic camera size, shown only when the projection is orthographic.
- `fov: float (@export_range)` — Perspective camera field of view, shown only when the projection is perspective.

### Methods
- `capture_screenshot(file_name: String) -> String` — Capture a screenshot to a JPG file and print the generated absolute path.

## Class DebugMarker (`photobooth/debug_marker.gd`)
- Extends: `Node3D`
- Summary: Represents a 3D marker used by Photobooth for visual verification during scene checks.

### Properties
- `label_text: String (@export)` — Optional text shown on the marker label; when empty, the label is hidden.

## Class Photobooth (`photobooth/photobooth.gd`)
- Extends: `Node3D`
- Summary: Enables visual scene testing by capturing screenshots from pre-configured [CameraRig] setups.

### Constants
- `CAMERA_RIG_SCENE_PATH`
- `MARKER_SCENE_PATH`

### Methods
- `add_camera_rig(camera_name: String) -> CameraRig` — Instantiates a [CameraRig], adds it under `Cameras`, and returns it.
- `add_marker(marker_name: String, label_text: String = "") -> DebugMarker` — Instantiates a debug marker, adds it under `Markers`, and returns it.
- `remove_marker(marker_name: String) -> void` — Removes the marker registered under [param marker_name] and frees it.
- `get_marker(marker_name: String) -> DebugMarker` — Returns the marker registered under [param marker_name], or quits if it is missing.
- `get_camera_rig(camera_name: String) -> CameraRig` — Returns the camera rig registered under [param camera_name], or quits if it is missing.
- `remove_camera(camera_name: String) -> void` — Removes the camera rig registered under [param camera_name] and frees it.
- `capture_screenshots(file_name: String) -> Dictionary` — Captures screenshots from all visible camera rigs and returns output paths keyed by camera name.

## Class SceneUtils (`scene_utils.gd`)
- Extends: `RefCounted`

### Constants
- `DEFAULT_OUTPUT_PARENT_RELATIVE_DIR`
- `JPG_QUALITY`

### Methods
- `static load_scene(scene_path: String) -> PackedScene` — Load a scene resource from `scene_path` and fail fast if unavailable.
- `static instantiate_scene(scene_path: String, inherit: bool = false, root_name: String = "") -> Node` — Instantiate a `PackedScene` from `scene_path` and fail fast on error.
- `static save_scene(scene_root: Node, output_path: String) -> Error` — Save `scene_root` to `output_path` and fail fast on invalid input or save errors.
- `static require_node(root: Node, node_path: NodePath) -> Node` — Resolve and return a required child node, or fail fast if not found.
- `static wait_frames(tree: SceneTree, frame_count: int = 1) -> void` — Wait for `frame_count` process frames.
- `static wait_seconds(tree: SceneTree, seconds: float) -> void` — Wait for `seconds` using a SceneTree timer.
- `static capture_screenshot(tree: SceneTree, file_name: String) -> void` — Capture the root viewport to a JPG file and print the generated absolute path. The output directory is resolved from `--output-dir` / `--ou…
- `static capture_viewport_screenshot(tree: SceneTree, viewport: Viewport, file_name: String) -> String` — Capture a specific viewport to a JPG file.
- `static to_safe_file_component(value: String) -> String` — Convert `value` into a filename-safe component by replacing unsupported characters with `_`. Leading/trailing whitespace is removed from th…
- `static fatal_error_and_quit(message: String) -> void` — Log `message` as a fatal error and terminate the running SceneTree with exit code `1`.
