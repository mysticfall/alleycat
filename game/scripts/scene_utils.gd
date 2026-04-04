class_name SceneUtils
extends RefCounted

const DEFAULT_OUTPUT_PARENT_RELATIVE_DIR := "temp"
const JPG_QUALITY := 0.85

## Load a scene resource from `scene_path` and fail fast if unavailable.
static func load_scene(scene_path: String) -> PackedScene:
	var loaded_resource: Resource = load(scene_path)
	if loaded_resource == null:
		fatal_error_and_quit("SceneUtils: failed to load scene at path: %s" % scene_path)
		return null

	var packed_scene: PackedScene = loaded_resource as PackedScene
	if packed_scene == null:
		fatal_error_and_quit("SceneUtils: expected PackedScene at path, but got '%s': %s" % [loaded_resource.get_class(), scene_path])
		return null

	return packed_scene

## Instantiate a `PackedScene` from `scene_path` and fail fast on error.
static func instantiate_scene(scene_path: String, inherit: bool = false, root_name: String = "") -> Node:
	var packed_scene: PackedScene = load_scene(scene_path)
	if packed_scene == null:
		fatal_error_and_quit("SceneUtils: failed to load scene at path: %s" % scene_path)
		return null

	var instance: Node

	if inherit:
		var scene := PackedScene.new()

		scene._bundled = {
			"names": [root_name],
			"variants": [packed_scene],
			"node_count": 1,
			"nodes": [-1, -1, 2147483647, 0, -1, 0, 0],
			"conn_count": 0,
			"conns": [],
			"node_paths": [],
			"editable_instances": [],
			"base_scene": 0,
			"version": 3
		}

		instance = scene.instantiate(PackedScene.GEN_EDIT_STATE_MAIN_INHERITED)
	else:
		instance = packed_scene.instantiate()

	if instance == null:
		fatal_error_and_quit("SceneUtils: Failed to instantiate base scene: %s" % scene_path)
		return

	return instance

## Save `scene_root` to `output_path` and fail fast on invalid input or save errors.
static func save_scene(scene_root: Node, output_path: String) -> Error:
	if scene_root == null:
		fatal_error_and_quit("SceneUtils: scene root is null while saving scene to: %s" % output_path)
		return ERR_INVALID_PARAMETER

	if output_path.is_empty():
		fatal_error_and_quit("SceneUtils: output path is empty while saving scene root '%s'" % scene_root.name)
		return ERR_INVALID_PARAMETER

	var packed_scene: PackedScene = PackedScene.new()
	var pack_result: Error = packed_scene.pack(scene_root)
	if pack_result != OK:
		fatal_error_and_quit("SceneUtils: failed to pack scene root '%s' for '%s' (error %s)" % [scene_root.name, output_path, pack_result])
		return pack_result

	var save_result: Error = ResourceSaver.save(packed_scene, output_path)
	if save_result != OK:
		fatal_error_and_quit("SceneUtils: failed to save scene root '%s' to '%s' (error %s)" % [scene_root.name, output_path, save_result])
		return save_result

	print("SceneUtils: scene saved successfully to: %s" % output_path)

	return OK

## Resolve and return a required child node, or fail fast if not found.
static func require_node(root: Node, node_path: NodePath) -> Node:
	if root == null:
		fatal_error_and_quit("SceneUtils: root node is null while resolving node path: %s" % node_path)
		return null

	if not root.has_node(node_path):
		fatal_error_and_quit("SceneUtils: required node path '%s' not found under root '%s'" % [node_path, root.name])
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

## Capture the root viewport to a JPG file and print the generated absolute path.
##
## The output directory is resolved from `--output-dir` / `--output-dir=...`.
## If not provided, defaults to `temp` at repository root.
static func capture_screenshot(tree: SceneTree, file_name: String) -> void:
	if tree == null:
		fatal_error_and_quit("SceneUtils: SceneTree is null while capturing root screenshot: %s" % file_name)
		return

	var root_viewport: Window = tree.root
	if root_viewport == null:
		fatal_error_and_quit("SceneUtils: SceneTree root viewport is null while capturing screenshot: %s" % file_name)
		return

	await _capture_viewport_to_jpg(tree, root_viewport, file_name)

## Capture a specific viewport to a JPG file.
static func capture_viewport_screenshot(tree: SceneTree, viewport: Viewport, file_name: String) -> String:
	return await _capture_viewport_to_jpg(tree, viewport, file_name)

static func _capture_viewport_to_jpg(tree: SceneTree, viewport: Viewport, file_name: String) -> String:
	var output_dir: String = _resolve_output_dir()
	var output_file_path: String = output_dir.path_join(file_name)

	if tree == null:
		fatal_error_and_quit("SceneUtils: SceneTree is null while capturing screenshot to: %s" % output_file_path)
		return ""

	if viewport == null:
		fatal_error_and_quit("SceneUtils: viewport is null while capturing screenshot: %s" % output_file_path)
		return ""

	var ensure_dir_result: Error = _ensure_parent_dir(output_file_path)
	if ensure_dir_result != OK:
		fatal_error_and_quit("SceneUtils: failed to create screenshot output directory for '%s' (error %s)" % [output_file_path, ensure_dir_result])
		return ""

	if DisplayServer.get_name() == "headless":
		await wait_frames(tree, 2)
	else:
		await RenderingServer.frame_post_draw

	var viewport_texture: ViewportTexture = viewport.get_texture()
	if viewport_texture == null:
		fatal_error_and_quit("SceneUtils: viewport texture is null while capturing screenshot: %s" % output_file_path)
		return ""

	var image: Image = viewport_texture.get_image()
	if image == null:
		fatal_error_and_quit("SceneUtils: failed to read image from viewport for: %s" % output_file_path)
		return ""

	var save_result: Error = image.save_jpg(output_file_path, JPG_QUALITY)
	if save_result != OK:
		fatal_error_and_quit("SceneUtils: failed to save screenshot '%s' as JPG (error %s)" % [output_file_path, save_result])
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

## Convert `value` into a filename-safe component by replacing unsupported characters with `_`.
## Leading/trailing whitespace is removed from the result.
static func to_safe_file_component(value: String) -> String:
	if value.is_empty():
		return ""

	var allowed_characters := "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_"
	var output := ""
	for index in value.length():
		var character: String = value.substr(index, 1)
		if allowed_characters.contains(character):
			output += character
			continue

		output += "_"

	return output.strip_edges()

## Log `message` as a fatal error and terminate the running SceneTree with exit code `1`.
static func fatal_error_and_quit(message: String) -> void:
	push_error(message)
	printerr(message)

	var main_loop: MainLoop = Engine.get_main_loop()
	var tree: SceneTree = main_loop as SceneTree
	if tree == null:
		push_error("SceneUtils: failed to resolve SceneTree while handling fatal error")
		return

	tree.quit(1)
