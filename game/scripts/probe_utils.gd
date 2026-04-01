class_name ProbeUtils
extends RefCounted

const DEFAULT_OUTPUT_PARENT_RELATIVE_DIR := "temp"
const JPG_QUALITY := 0.85
const PHOTOBOOTH_PATH := "res://assets/testing/photobooth/photobooth.tscn"
const DEFAULT_SUBJECT_PATH := "res://assets/characters/reference/reference_female.tscn"


## Holds references to the key nodes produced by `setup_photobooth`.
class PhotoboothSetup:
	var photobooth: Node3D
	var camera: Camera3D
	var subject: Node


## Load the photobooth scene, attach a subject to the anchor point, and
## return a `PhotoboothSetup` with references to the photobooth root, camera,
## and subject nodes. The photobooth is added to the scene tree root.
##
## Uses the reference female character by default; pass a different
## `subject_path` to load any scene (characters, props, effects, etc.).
static func setup_photobooth(tree: SceneTree, subject_path: String = DEFAULT_SUBJECT_PATH) -> PhotoboothSetup:
	var photobooth: Node = instantiate_scene(PHOTOBOOTH_PATH)
	if photobooth == null:
		return null

	tree.root.add_child(photobooth)

	var anchor: Node = require_node(photobooth, ^"SubjectAnchor")
	if anchor == null:
		return null

	var subject: Node = instantiate_scene(subject_path)
	if subject == null:
		return null

	anchor.add_child(subject)

	var camera: Camera3D = require_node(photobooth, ^"Camera3D")
	if camera == null:
		return null

	var setup := PhotoboothSetup.new()
	setup.photobooth = photobooth
	setup.camera = camera
	setup.subject = subject
	return setup


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
## If not provided, defaults to `.opencode/temp` at repository root.
static func capture_screenshot(tree: SceneTree, file_name: String) -> void:
	var output_dir: String = _resolve_output_dir()
	var output_file_path: String = output_dir.path_join(file_name)

	if tree == null:
		_fatal_error_and_quit("ProbeUtils: SceneTree is null while capturing screenshot to: %s" % output_file_path)
		return

	var ensure_dir_result: Error = _ensure_parent_dir(output_file_path)
	if ensure_dir_result != OK:
		_fatal_error_and_quit("ProbeUtils: failed to create screenshot output directory for '%s' (error %s)" % [output_file_path, ensure_dir_result])
		return

	if DisplayServer.get_name() == "headless":
		await wait_frames(tree, 2)
	else:
		await RenderingServer.frame_post_draw

	var root_viewport: Window = tree.root
	if root_viewport == null:
		_fatal_error_and_quit("ProbeUtils: SceneTree root viewport is null while capturing screenshot: %s" % output_file_path)
		return

	var viewport_texture: ViewportTexture = root_viewport.get_texture()
	if viewport_texture == null:
		_fatal_error_and_quit("ProbeUtils: root viewport texture is null while capturing screenshot: %s" % output_file_path)
		return

	var image: Image = viewport_texture.get_image()
	if image == null:
		_fatal_error_and_quit("ProbeUtils: failed to read image from root viewport for: %s" % output_file_path)
		return

	var save_result: Error = image.save_jpg(output_file_path, JPG_QUALITY)
	if save_result != OK:
		_fatal_error_and_quit("ProbeUtils: failed to save screenshot '%s' as JPG (error %s)" % [output_file_path, save_result])
		return

	print("Saved a screenshot: %s" % ProjectSettings.globalize_path(output_file_path))


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
