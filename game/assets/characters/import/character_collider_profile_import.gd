@tool
extends RefCounted

const BODY_COLLIDER_PROFILE_SCRIPT_PATH := "res://src/Rigging/Physics/BodyColliderProfile.cs"
const BODY_COLLIDER_PROFILE_PATH := "body_collider_profile.tres"


func apply(source_file_path: String) -> void:
	if source_file_path.is_empty():
		push_warning("Character collider import skipped because the imported source file path could not be determined.")
		return

	if source_file_path.ends_with(".colliders.blend"):
		return

	if not source_file_path.ends_with(".blend"):
		push_warning("Character collider import skipped for non-Blender character source '%s'." % source_file_path)
		return

	var collider_scene_path := _derive_collider_scene_path(source_file_path)
	if not FileAccess.file_exists(collider_scene_path):
		push_warning("Character collider import skipped because collider source '%s' does not exist yet." % collider_scene_path)
		return

	if not FileAccess.file_exists(collider_scene_path + ".import"):
		push_warning("Character collider import skipped because Godot has not imported collider source '%s' yet." % collider_scene_path)
		return

	if not ResourceLoader.exists(collider_scene_path, "PackedScene"):
		push_warning("Character collider import skipped because imported collider scene '%s' is not available to ResourceLoader yet." % collider_scene_path)
		return

	var wrapper_path := _derive_wrapper_scene_path(source_file_path)
	var profile_path := _derive_profile_path(source_file_path)
	var wrapper_saved := _save_collider_wrapper(collider_scene_path, wrapper_path)
	if not wrapper_saved:
		return

	_save_body_collider_profile(wrapper_path, profile_path)


func _derive_collider_scene_path(source_file_path: String) -> String:
	var base_path := source_file_path.trim_suffix(".blend")
	return base_path + ".colliders.blend"


func _derive_wrapper_scene_path(source_file_path: String) -> String:
	var directory := source_file_path.get_base_dir()
	var stem := source_file_path.get_file().trim_suffix(".blend")
	return "%s/%s_colliders.tscn" % [directory, stem]


func _derive_profile_path(source_file_path: String) -> String:
	return "%s/%s" % [source_file_path.get_base_dir(), BODY_COLLIDER_PROFILE_PATH]


func _save_collider_wrapper(collider_scene_path: String, wrapper_path: String) -> bool:
	if _existing_wrapper_matches(collider_scene_path, wrapper_path):
		return true

	# Packing an instantiated imported scene inlines the collider content in Godot
	# 4.x, so the wrapper scene is emitted as a minimal inherited scene that keeps
	# the imported collider PackedScene as its root instance.
	var file := FileAccess.open(wrapper_path, FileAccess.WRITE)
	if file == null:
		push_error("Character collider import failed to open wrapper '%s' for writing: %s" % [wrapper_path, FileAccess.get_open_error()])
		return false

	file.store_string(_build_wrapper_scene_text(collider_scene_path, wrapper_path))
	file.close()

	var reloaded_wrapper := ResourceLoader.load(wrapper_path, "PackedScene", ResourceLoader.CACHE_MODE_REPLACE) as PackedScene
	if reloaded_wrapper == null:
		push_error("Character collider import saved wrapper '%s' but ResourceLoader could not reload it." % wrapper_path)
		return false

	return true


func _existing_wrapper_matches(collider_scene_path: String, wrapper_path: String) -> bool:
	if not FileAccess.file_exists(wrapper_path):
		return false

	var wrapper_scene := ResourceLoader.load(wrapper_path, "PackedScene", ResourceLoader.CACHE_MODE_REPLACE) as PackedScene
	if wrapper_scene == null:
		return false

	var wrapper_text := FileAccess.get_file_as_string(wrapper_path)
	return wrapper_text.contains("path=\"%s\"" % _escape_godot_string(collider_scene_path))


func _build_wrapper_scene_text(collider_scene_path: String, wrapper_path: String) -> String:
	var scene_header := "[gd_scene format=3"
	var wrapper_uid := _resource_uid_text(wrapper_path)
	if not wrapper_uid.is_empty():
		scene_header += " uid=\"%s\"" % wrapper_uid
	scene_header += "]"

	var collider_uid := _resource_uid_text(collider_scene_path)
	var ext_resource := "[ext_resource type=\"PackedScene\""
	if not collider_uid.is_empty():
		ext_resource += " uid=\"%s\"" % collider_uid
	ext_resource += " path=\"%s\" id=\"1_collider\"]" % _escape_godot_string(collider_scene_path)

	var node_name := _escape_godot_string(wrapper_path.get_file().trim_suffix(".tscn"))
	return "%s\n\n%s\n\n[node name=\"%s\" instance=ExtResource(\"1_collider\")]\n" % [scene_header, ext_resource, node_name]


func _resource_uid_text(resource_path: String) -> String:
	var uid := ResourceLoader.get_resource_uid(resource_path)
	return "" if uid == -1 else ResourceUID.id_to_text(uid)


func _escape_godot_string(value: String) -> String:
	return value.replace("\\", "\\\\").replace("\"", "\\\"")


func _save_body_collider_profile(wrapper_path: String, profile_path: String) -> void:
	var wrapper_scene := ResourceLoader.load(wrapper_path, "PackedScene", ResourceLoader.CACHE_MODE_REPLACE) as PackedScene
	if wrapper_scene == null:
		push_error("Character collider import failed to load wrapper '%s' for BodyColliderProfile generation." % wrapper_path)
		return

	var profile_script := ResourceLoader.load(BODY_COLLIDER_PROFILE_SCRIPT_PATH, "Script") as Script
	if profile_script == null:
		push_error("Character collider import failed to load BodyColliderProfile script '%s'." % BODY_COLLIDER_PROFILE_SCRIPT_PATH)
		return

	var profile := ResourceLoader.load(profile_path, "Resource", ResourceLoader.CACHE_MODE_REPLACE) as Resource
	if profile != null and profile.get_script() == profile_script:
		var existing_source_scene := profile.get("SourceScene") as PackedScene
		if existing_source_scene != null and existing_source_scene.resource_path == wrapper_path:
			return

	if profile == null or profile.get_script() != profile_script:
		profile = profile_script.new() as Resource
		if profile == null:
			push_error("Character collider import failed to instantiate BodyColliderProfile from '%s'." % BODY_COLLIDER_PROFILE_SCRIPT_PATH)
			return

	profile.set("SourceScene", wrapper_scene)
	var save_result := ResourceSaver.save(profile, profile_path)
	if save_result != OK:
		push_error("Character collider import failed to save BodyColliderProfile '%s': %s" % [profile_path, save_result])
		return

	var reloaded_profile := ResourceLoader.load(profile_path, "Resource", ResourceLoader.CACHE_MODE_REPLACE) as Resource
	if reloaded_profile == null:
		push_error("Character collider import saved BodyColliderProfile '%s' but ResourceLoader could not reload it." % profile_path)
		return

	var reloaded_source_scene := reloaded_profile.get("SourceScene") as PackedScene
	if reloaded_source_scene == null or reloaded_source_scene.resource_path != wrapper_path:
		push_error("Character collider import saved BodyColliderProfile '%s' but its SourceScene did not reload as '%s'." % [profile_path, wrapper_path])
