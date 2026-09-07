extends SceneTree

const DEFAULT_BONE_MAP := "res://assets/characters/reference/skeleton_profiles/bone_map_makehuman.tres"
const DEFAULT_SKELETON_PATH := "PATH:Female/Skeleton3D"
const DEFAULT_SILHOUETTE_FILTER := [&"Head", &"Neck", &"UpperChest", &"Chest", &"Spine", &"Hips", &"RightFoot", &"LeftFoot"]
const DEFAULT_CHARACTER := "female"
const DEFAULT_VARIANT := "sad"
var errors: Array[String] = []
var runtime_capability: Script

func _init() -> void:
	runtime_capability = _load_helper("runtime_capability.gd")
	var descriptor := _descriptor()
	if not descriptor.is_empty():
		_configure(descriptor)
	quit(1 if not errors.is_empty() else 0)

func _descriptor() -> Dictionary:
	var path := _argument("--descriptor=")
	if not path.is_empty():
		return _json(path)
	var package_name := _argument("--package=")
	if package_name.is_empty(): package_name = "locomotion"
	var variant := _argument("--variant=")
	if variant.is_empty(): variant = DEFAULT_VARIANT
	var character := _argument("--character=")
	if character.is_empty(): character = DEFAULT_CHARACTER
	var set_directory := "res://assets/characters/reference/%s/animations/processed/%s" % [character, package_name]
	var index := set_directory.path_join(variant).path_join("processed-index.json")
	return {
		"name": package_name, "character": character, "variant": variant, "index": index,
		"blend": set_directory.path_join("%s_%s.blend" % [package_name, variant]),
		"root": "res://assets/characters/reference/%s/animations/runtime/%s/%s" % [character, package_name, variant],
	}

func _configure(descriptor: Dictionary) -> void:
	var index := _json(String(descriptor.get("index", "")))
	var actions: Array = index.get("actions", [])
	if actions.is_empty():
		_fail("Processed index has no actions: %s" % descriptor.get("index", ""))
		return
	for value in actions:
		if not value is Dictionary or not _has_current_provenance(value as Dictionary):
			if value is Dictionary:
				var capability_error: String = runtime_capability.validate_processed_action(value as Dictionary)
				if not capability_error.is_empty():
					_fail("%s: %s" % [capability_error, (value as Dictionary).get("action", "")])
					return
			_fail("Processed index contains stale or incomplete ANIM-002 event/repair provenance")
			return
	var blend := String(descriptor.get("blend", ""))
	if blend.is_empty():
		_fail("Package descriptor has no processed blend")
		return
	var sidecar := blend + ".import"
	var config := ConfigFile.new()
	if config.load(sidecar) != OK:
		_fail("Missing Godot import sidecar (run initial Godot import first): %s" % sidecar)
		return
	var bone_map_path := String(descriptor.get("bone_map", DEFAULT_BONE_MAP))
	var bone_map := load(bone_map_path) as BoneMap
	if bone_map == null:
		_fail("Package descriptor bone map is unavailable: %s" % bone_map_path)
		return
	var skeleton_path := String(descriptor.get("skeleton_import_path", DEFAULT_SKELETON_PATH))
	var filter := _silhouette_filter(descriptor.get("silhouette_filter", DEFAULT_SILHOUETTE_FILTER))
	if filter.is_empty():
		_fail("Package descriptor silhouette filter is invalid")
		return
	var subresources: Dictionary = config.get_value("params", "_subresources", {})
	var nodes: Dictionary = subresources.get("nodes", {})
	nodes[skeleton_path] = {
		"retarget/bone_map": bone_map,
		"retarget/rest_fixer/fix_silhouette/enable": true,
		"retarget/rest_fixer/fix_silhouette/filter": filter,
		"retarget/rest_fixer/normalize_position_tracks": true,
	}
	var animations := subresources.get("animations", {}) as Dictionary
	var declared := {}
	var alias := {}
	for entry_value in actions:
		if not entry_value is Dictionary:
			_fail("Processed index contains an invalid action entry")
			continue
		var entry := entry_value as Dictionary
		var action := String(entry.get("action", ""))
		var identity := String(entry.get("external_identity", ""))
		if action.is_empty() or identity.is_empty():
			_fail("Processed action identity is incomplete")
			continue
		declared[action] = Animation.LOOP_LINEAR if bool(entry.get("is_loop", false)) else Animation.LOOP_NONE
		# Godot strips "-loop"/"_loop" name tokens when exposing imported
		# animations, so a sidecar block may be keyed by either convention.
		for suffix in ["-loop", "_loop"]:
			var stripped := action.trim_suffix(suffix)
			if stripped != action:
				alias[stripped] = action
	# Canonicalise every existing per-action block — including stale blocks the
	# sidecar already carries under either key convention — onto its declared
	# action with externalisation stripped; blocks for undeclared actions are
	# removed so no residual externalisation can survive.
	var canonical := {}
	for key in animations.keys():
		var action := String(key)
		if not declared.has(action):
			action = String(alias.get(action, ""))
		if action.is_empty(): continue
		var block := (animations[key] as Dictionary).duplicate(true)
		_scrub_externalisation(block)
		canonical[action] = block
	for action in declared:
		var block: Dictionary = (canonical.get(action) as Dictionary).duplicate(true) if canonical.has(action) else {}
		_scrub_externalisation(block)
		block["save_to_file/enabled"] = false
		block["save_to_file/keep_custom_tracks"] = false
		block["settings/loop_mode"] = int(declared[action])
		block["slices/amount"] = 0
		canonical[action] = block
	# The scene importer has no supported global externalisation switch; disable
	# any stale params-level key defensively so no configuration path can
	# externalise a standalone clip.
	for key in config.get_section_keys("params"):
		if String(key).ends_with("save_to_file/enabled"):
			config.set_value("params", key, false)
	subresources["nodes"] = nodes
	subresources["animations"] = canonical
	config.set_value("params", "animation/remove_immutable_tracks", false)
	config.set_value("params", "_subresources", subresources)
	if config.save(sidecar) != OK:
		_fail("Could not save import sidecar: %s" % sidecar)
		return
	_verify_saved(sidecar, actions, skeleton_path, bone_map_path, filter)

func _verify_saved(sidecar: String, actions: Array, skeleton_path: String, bone_map_path: String, filter: Array) -> void:
	var config := ConfigFile.new()
	if config.load(sidecar) != OK:
		_fail("Could not reload import sidecar: %s" % sidecar)
		return
	if config.get_value("params", "animation/remove_immutable_tracks", true) != false:
		_fail("Immutable baked animation tracks are not preserved: %s" % sidecar)
	for key in config.get_section_keys("params"):
		if String(key).ends_with("save_to_file/enabled") and config.get_value("params", key) == true:
			_fail("Import sidecar retains a global externalisation switch: %s" % sidecar)
	var subresources: Dictionary = config.get_value("params", "_subresources", {})
	var nodes: Dictionary = subresources.get("nodes", {})
	var animations: Dictionary = subresources.get("animations", {})
	var settings = nodes.get(skeleton_path)
	if not settings is Dictionary:
		_fail("Import sidecar skeleton settings are absent: %s" % sidecar)
		return
	var bone_map := (settings as Dictionary).get("retarget/bone_map") as BoneMap
	if bone_map == null or bone_map.resource_path != bone_map_path:
		_fail("Import sidecar retarget binding is invalid: %s" % sidecar)
	if (settings as Dictionary).get("retarget/rest_fixer/fix_silhouette/enable") != true or (settings as Dictionary).get("retarget/rest_fixer/fix_silhouette/filter") != filter:
		_fail("Import sidecar silhouette settings are invalid: %s" % sidecar)
	if (settings as Dictionary).get("retarget/rest_fixer/normalize_position_tracks", false) != true:
		_fail("Import sidecar has disabled position-track normalisation: %s" % sidecar)
	if animations.size() != actions.size():
		_fail("Import sidecar action set is invalid: %s" % sidecar)
	for action in animations.keys():
		var block = animations[action]
		if not block is Dictionary or _externalisation_enabled(block as Dictionary):
			_fail("Import must retain embedded animation for %s" % action)
	for entry_value in actions:
		if not entry_value is Dictionary: continue
		var entry := entry_value as Dictionary
		var action := String(entry.get("action", ""))
		var action_settings = animations.get(action)
		if not action_settings is Dictionary or (action_settings as Dictionary).get("save_to_file/enabled") != false:
			_fail("Import must retain embedded animation for %s" % action)
		elif int((action_settings as Dictionary).get("settings/loop_mode", -1)) != (Animation.LOOP_LINEAR if bool(entry.get("is_loop", false)) else Animation.LOOP_NONE):
			_fail("Import loop intent is invalid for %s" % action)

func _scrub_externalisation(block: Dictionary) -> void:
	for key in block.keys():
		var text := String(key)
		if text.ends_with("save_to_file/enabled") or text.ends_with("save_to_file/keep_custom_tracks"):
			block[key] = false
		elif text.ends_with("save_to_file/path") or text.ends_with("save_to_file/fallback_path"):
			block[key] = ""

func _externalisation_enabled(block: Dictionary) -> bool:
	for key in block.keys():
		if String(key).ends_with("save_to_file/enabled") and block[key] == true:
			return true
	return false

func _has_current_provenance(entry: Dictionary) -> bool:
	var capability_error: String = runtime_capability.validate_processed_action(entry)
	if not capability_error.is_empty(): return false
	var capability := String(entry["runtime_capability"])
	if not runtime_capability.requires_directional_event_provenance(capability): return true
	var source = entry.get("source_directional_event_evidence")
	var derivation = entry.get("event_derivation_evidence")
	var output = entry.get("post_repair_event_evidence")
	if not source is Dictionary or String((source as Dictionary).get("contract", "")) != "anim002_source_directional_phase_events_v1": return false
	if not derivation is Dictionary or String((derivation as Dictionary).get("contract", "")) != "anim002_event_derivation_v1": return false
	if not output is Dictionary or String((output as Dictionary).get("contract", "")) != "anim002_post_repair_directional_phase_events_v2" or not (output as Dictionary).get("refusals", []).is_empty(): return false
	if runtime_capability.requires_loop_repair(capability):
		var repair = entry.get("loop_repair_evidence")
		return repair is Dictionary and String((repair as Dictionary).get("contract", "")) == "anim002_selected_loop_repair_provenance_v2" and not String((repair as Dictionary).get("blend_profile", "")).is_empty()
	return true

func _silhouette_filter(value: Variant) -> Array:
	if not value is Array: return []
	var output: Array = []
	for item in value:
		var name := String(item)
		if name.is_empty(): return []
		output.append(StringName(name))
	return output

func _argument(prefix: String) -> String:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with(prefix): return argument.trim_prefix(prefix)
	return ""

func _load_helper(filename: String) -> Script:
	var command_path: String = get_script().resource_path
	var helper_path: String = command_path.get_base_dir().get_base_dir().path_join("lib").path_join(filename).simplify_path()
	return load(helper_path) as Script

func _json(path: String) -> Dictionary:
	if path.is_empty() or not FileAccess.file_exists(path):
		_fail("JSON file is missing: %s" % path)
		return {}
	var value = JSON.parse_string(FileAccess.get_file_as_string(path))
	if value is Dictionary: return value
	_fail("JSON object is invalid: %s" % path)
	return {}

func _fail(message: String) -> void:
	push_error(message)
	errors.append(message)
