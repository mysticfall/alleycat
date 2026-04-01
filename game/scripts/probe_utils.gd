class_name ProbeUtils
extends RefCounted

const DEFAULT_OUTPUT_PARENT_RELATIVE_DIR := "temp"
const JPG_QUALITY := 0.85
const PHOTOBOOTH_PATH := "res://assets/testing/photobooth/photobooth.tscn"


class AabbAccumulator:
	var has_bounds: bool = false
	var bounds: AABB = AABB()


## Helper object for 3D probe composition and capture.
##
## Usage:
## - create with `ProbeUtils.setup_photobooth(tree)`
## - call `load(subject_path)` to place a subject scene under `SubjectAnchor`
## - optionally move/projection-tune camera
## - call `capture(file_name)` to save a JPG from the camera viewport
class Photobooth:
	extends RefCounted

	const PORTRAIT_SIZE := Vector2i(540, 960)
	const LANDSCAPE_SIZE := Vector2i(960, 540)

	var _tree: SceneTree
	var _photobooth_root: Node3D
	var _camera: Camera3D
	var _subject_anchor: Node3D
	var _subject: Node3D
	var _capture_size: Vector2i = PORTRAIT_SIZE


	func _initialise(tree: SceneTree, photobooth_root: Node3D, camera: Camera3D, subject_anchor: Node3D) -> Photobooth:
		_tree = tree
		_photobooth_root = photobooth_root
		_camera = camera
		_subject_anchor = subject_anchor
		_capture_size = PORTRAIT_SIZE
		return self


	func dispose() -> void:
		if _photobooth_root != null and is_instance_valid(_photobooth_root):
			_photobooth_root.queue_free()

		_tree = null
		_photobooth_root = null
		_camera = null
		_subject_anchor = null
		_subject = null


	func get_root() -> Node3D:
		return _photobooth_root


	func get_camera() -> Camera3D:
		return _camera


	func get_subject() -> Node3D:
		return _subject


	func load(subject_path: String) -> Node3D:
		if _subject_anchor == null:
			ProbeUtils._fatal_error_and_quit("Photobooth: subject anchor is null while loading subject from: %s" % subject_path)
			return null

		var subject_node: Node = ProbeUtils.instantiate_scene(subject_path)
		if subject_node == null:
			return null

		var subject_3d: Node3D = subject_node as Node3D
		if subject_3d == null:
			ProbeUtils._fatal_error_and_quit("Photobooth: expected Node3D subject scene at '%s', but got '%s'" % [subject_path, subject_node.get_class()])
			return null

		if _subject != null and is_instance_valid(_subject):
			_subject.queue_free()

		_subject_anchor.add_child(subject_3d)
		_subject = subject_3d
		return _subject


	func use_portrait() -> void:
		_capture_size = PORTRAIT_SIZE


	func use_landscape() -> void:
		_capture_size = LANDSCAPE_SIZE


	func position_camera(position: Vector3, look_at: Variant = null) -> void:
		var target_point: Vector3
		if look_at == null:
			target_point = _resolve_subject_focus_point()
		else:
			if not (look_at is Vector3):
				ProbeUtils._fatal_error_and_quit("Photobooth: expected 'look_at' to be Vector3 or null, got: %s" % typeof(look_at))
				return
			target_point = look_at

		ProbeUtils.position_camera(_camera, position, target_point)


	func set_perspective(fov: float = 45.0) -> void:
		ProbeUtils.set_camera_perspective(_camera, fov)


	func set_orthographic(size: float = 2.0) -> void:
		ProbeUtils.set_camera_orthographic(_camera, size)


	func capture(file_name: String) -> String:
		if _camera == null:
			ProbeUtils._fatal_error_and_quit("Photobooth: camera is null while capturing screenshot: %s" % file_name)
			return ""

		var capture_viewport: Viewport = _camera.get_viewport()
		if capture_viewport == null:
			ProbeUtils._fatal_error_and_quit("Photobooth: camera viewport is null while capturing screenshot: %s" % file_name)
			return ""

		_ensure_capture_size(capture_viewport)
		return await ProbeUtils._capture_viewport_to_jpg(_tree, capture_viewport, file_name)


	func _ensure_capture_size(viewport: Viewport) -> void:
		if viewport is Window:
			var window_viewport: Window = viewport as Window
			window_viewport.size = _capture_size
			return

		if viewport is SubViewport:
			var subviewport: SubViewport = viewport as SubViewport
			subviewport.size = _capture_size
			return

		ProbeUtils._fatal_error_and_quit("Photobooth: unsupported viewport type '%s' while resizing capture frame" % viewport.get_class())


	func _resolve_subject_focus_point() -> Vector3:
		if _subject == null:
			ProbeUtils._fatal_error_and_quit("Photobooth: no subject loaded while resolving camera look-at point")
			return Vector3.ZERO

		var accumulator := AabbAccumulator.new()
		_accumulate_world_aabb(_subject, accumulator)
		if accumulator.has_bounds:
			return accumulator.bounds.get_center()

		return _subject.global_position


	func _accumulate_world_aabb(node: Node, accumulator: AabbAccumulator) -> void:
		var visual_node: VisualInstance3D = node as VisualInstance3D
		if visual_node != null:
			var local_bounds: AABB = visual_node.get_aabb()
			if local_bounds.has_surface():
				var world_bounds: AABB = visual_node.global_transform * local_bounds
				if accumulator.has_bounds:
					accumulator.bounds = accumulator.bounds.merge(world_bounds)
				else:
					accumulator.bounds = world_bounds
					accumulator.has_bounds = true

		for child: Node in node.get_children():
			_accumulate_world_aabb(child, accumulator)


## Recommended way to create a photobooth helper for 3D probe scripts.
##
## The photobooth scene is added to the scene tree root. Load a subject with
## `photobooth.load(subject_path)` before capturing.
static func setup_photobooth(tree: SceneTree) -> Photobooth:
	if tree == null:
		_fatal_error_and_quit("ProbeUtils: SceneTree is null while setting up photobooth")
		return null

	var photobooth_node: Node = instantiate_scene(PHOTOBOOTH_PATH)
	if photobooth_node == null:
		return null

	var photobooth_root: Node3D = photobooth_node as Node3D
	if photobooth_root == null:
		_fatal_error_and_quit("ProbeUtils: expected Node3D photobooth root, but got '%s'" % photobooth_node.get_class())
		return null

	tree.root.add_child(photobooth_root)

	var anchor_node: Node = require_node(photobooth_root, ^"SubjectAnchor")
	if anchor_node == null:
		return null

	var subject_anchor: Node3D = anchor_node as Node3D
	if subject_anchor == null:
		_fatal_error_and_quit("ProbeUtils: expected Node3D subject anchor, but got '%s'" % anchor_node.get_class())
		return null

	var camera_node: Node = require_node(photobooth_root, ^"Camera3D")
	if camera_node == null:
		return null

	var camera: Camera3D = camera_node as Camera3D
	if camera == null:
		_fatal_error_and_quit("ProbeUtils: expected Camera3D in photobooth, but got '%s'" % camera_node.get_class())
		return null

	var photobooth := Photobooth.new()
	return photobooth._initialise(tree, photobooth_root, camera, subject_anchor)


## Load a scene resource from `scene_path` and fail fast if unavailable.
static func load_scene(scene_path: String) -> PackedScene:
	var loaded_resource: Resource = load(scene_path)
	if loaded_resource == null:
		_fatal_error_and_quit("ProbeUtils: failed to load scene at path: %s" % scene_path)
		return null

	var packed_scene: PackedScene = loaded_resource as PackedScene
	if packed_scene == null:
		_fatal_error_and_quit("ProbeUtils: expected PackedScene at path, but got '%s': %s" % [loaded_resource.get_class(), scene_path])
		return null

	return packed_scene


## Instantiate a `PackedScene` from `scene_path` and fail fast on error.
static func instantiate_scene(scene_path: String) -> Node:
	var packed_scene: PackedScene = load_scene(scene_path)
	if packed_scene == null:
		return null

	var root_node: Node = packed_scene.instantiate()
	if root_node == null:
		_fatal_error_and_quit("ProbeUtils: failed to instantiate scene: %s" % scene_path)
		return null

	return root_node


## Resolve and return a required child node, or fail fast if not found.
static func require_node(root: Node, node_path: NodePath) -> Node:
	if root == null:
		_fatal_error_and_quit("ProbeUtils: root node is null while resolving node path: %s" % node_path)
		return null

	if not root.has_node(node_path):
		_fatal_error_and_quit("ProbeUtils: required node path '%s' not found under root '%s'" % [node_path, root.name])
		return null

	return root.get_node(node_path)


## Wait for `frame_count` process frames.
static func wait_frames(tree: SceneTree, frame_count: int = 1) -> void:
	var clamped_frame_count: int = maxi(frame_count, 0)
	for _frame_index in clamped_frame_count:
		await tree.process_frame


## Wait for `seconds` using a SceneTree timer.
static func wait_seconds(tree: SceneTree, seconds: float) -> void:
	var wait_duration_seconds: float = maxf(seconds, 0.0)
	if is_zero_approx(wait_duration_seconds):
		return

	await tree.create_timer(wait_duration_seconds).timeout


## Position a camera and orient it toward a target point.
## Uses `look_at_from_position` so it works even when the node is not in the tree.
## Automatically selects an up-vector that avoids colinearity with the view direction.
static func position_camera(camera: Camera3D, position: Vector3, target: Vector3) -> void:
	if camera == null:
		_fatal_error_and_quit("ProbeUtils: camera is null in position_camera")
		return

	var view_direction := (target - position).normalized()
	var up := Vector3.UP if absf(view_direction.dot(Vector3.UP)) < 0.99 else Vector3.FORWARD
	camera.look_at_from_position(position, target, up)


## Switch a camera to perspective projection.
static func set_camera_perspective(camera: Camera3D, fov: float = 45.0) -> void:
	if camera == null:
		_fatal_error_and_quit("ProbeUtils: camera is null in set_camera_perspective")
		return

	camera.projection = Camera3D.PROJECTION_PERSPECTIVE
	camera.fov = fov


## Switch a camera to orthographic projection.
## `size` controls the visible height in world units (width follows from the viewport aspect ratio).
static func set_camera_orthographic(camera: Camera3D, size: float = 2.0) -> void:
	if camera == null:
		_fatal_error_and_quit("ProbeUtils: camera is null in set_camera_orthographic")
		return

	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.size = size


## Capture the root viewport to a JPG file and print the generated absolute path.
##
## The output directory is resolved from `--output-dir` / `--output-dir=...`.
## If not provided, defaults to `temp` at repository root.
static func capture_screenshot(tree: SceneTree, file_name: String) -> void:
	if tree == null:
		_fatal_error_and_quit("ProbeUtils: SceneTree is null while capturing root screenshot: %s" % file_name)
		return

	var root_viewport: Window = tree.root
	if root_viewport == null:
		_fatal_error_and_quit("ProbeUtils: SceneTree root viewport is null while capturing screenshot: %s" % file_name)
		return

	await _capture_viewport_to_jpg(tree, root_viewport, file_name)


static func _capture_viewport_to_jpg(tree: SceneTree, viewport: Viewport, file_name: String) -> String:
	var output_dir: String = _resolve_output_dir()
	var output_file_path: String = output_dir.path_join(file_name)

	if tree == null:
		_fatal_error_and_quit("ProbeUtils: SceneTree is null while capturing screenshot to: %s" % output_file_path)
		return ""

	if viewport == null:
		_fatal_error_and_quit("ProbeUtils: viewport is null while capturing screenshot: %s" % output_file_path)
		return ""

	var ensure_dir_result: Error = _ensure_parent_dir(output_file_path)
	if ensure_dir_result != OK:
		_fatal_error_and_quit("ProbeUtils: failed to create screenshot output directory for '%s' (error %s)" % [output_file_path, ensure_dir_result])
		return ""

	if DisplayServer.get_name() == "headless":
		await wait_frames(tree, 2)
	else:
		await RenderingServer.frame_post_draw

	var viewport_texture: ViewportTexture = viewport.get_texture()
	if viewport_texture == null:
		_fatal_error_and_quit("ProbeUtils: viewport texture is null while capturing screenshot: %s" % output_file_path)
		return ""

	var image: Image = viewport_texture.get_image()
	if image == null:
		_fatal_error_and_quit("ProbeUtils: failed to read image from viewport for: %s" % output_file_path)
		return ""

	var save_result: Error = image.save_jpg(output_file_path, JPG_QUALITY)
	if save_result != OK:
		_fatal_error_and_quit("ProbeUtils: failed to save screenshot '%s' as JPG (error %s)" % [output_file_path, save_result])
		return ""

	var absolute_output_file_path: String = ProjectSettings.globalize_path(output_file_path)
	print("Saved a screenshot: %s" % absolute_output_file_path)
	return absolute_output_file_path


static func _ensure_parent_dir(output_file_path: String) -> Error:
	var parent_directory: String = output_file_path.get_base_dir()
	if parent_directory.is_empty():
		return OK

	var absolute_parent_directory: String = ProjectSettings.globalize_path(parent_directory)
	return DirAccess.make_dir_recursive_absolute(absolute_parent_directory)


static func _resolve_output_dir() -> String:
	var user_args: PackedStringArray = OS.get_cmdline_user_args()
	var output_dir_from_user_args: String = _find_output_dir_arg(user_args)
	if not output_dir_from_user_args.is_empty():
		return output_dir_from_user_args

	var all_args: PackedStringArray = OS.get_cmdline_args()
	var output_dir_from_all_args: String = _find_output_dir_arg(all_args)
	if not output_dir_from_all_args.is_empty():
		return output_dir_from_all_args

	var game_root_absolute: String = ProjectSettings.globalize_path("res://")
	var repo_root_absolute: String = game_root_absolute.simplify_path()

	return repo_root_absolute.path_join(DEFAULT_OUTPUT_PARENT_RELATIVE_DIR)


static func _find_output_dir_arg(args: PackedStringArray) -> String:
	for index in args.size():
		var argument: String = args[index]
		if argument == "--output-dir" and index + 1 < args.size():
			return args[index + 1]
		if argument.begins_with("--output-dir="):
			return argument.trim_prefix("--output-dir=")

	return ""


static func _fatal_error_and_quit(message: String) -> void:
	push_error(message)
	printerr(message)

	var main_loop: MainLoop = Engine.get_main_loop()
	var tree: SceneTree = main_loop as SceneTree
	if tree == null:
		push_error("ProbeUtils: failed to resolve SceneTree while handling fatal error")
		return

	tree.quit(1)
