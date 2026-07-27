extends SceneTree

# Extracts the imported standing actions into portable clips and packages them
# as a deterministic, catalogue-neutral AnimationLibrary and JSON catalogue.
# The source .blend must have completed its explicit Godot import pass first.

const INDEX_PATH := "res://assets/characters/reference/female/animations/processed/mixamo/index.json"
const SIDECAR_PATH := "res://assets/characters/reference/female/animations/processed/mixamo/locomotion_standing.blend.import"
const CLIP_DIR := "res://assets/characters/reference/female/animations/locomotion/clips"
const LIBRARY_PATH := "res://assets/characters/reference/female/animations/locomotion/standing_locomotion_library.tres"
const CATALOGUE_PATH := "res://assets/characters/reference/female/animations/locomotion/standing_locomotion_catalogue.json"
const EXPECTED_COUNT := 9
const INDEX_SCHEMA_VERSION := 2
const METRICS_SCHEMA_VERSION := 2
const TARGET_PREFIX := "%GeneralSkeleton"
const ACCEPTED_PREFIXES: Array[String] = ["%GeneralSkeleton", "GeneralSkeleton", "Female/GeneralSkeleton"]
const REQUIRED_BONES: Array[String] = ["Root", "Hips", "LeftFoot", "RightFoot", "LeftToes", "RightToes"]
const HEADING_AXIS := Vector3.UP
const ROOT_HEADING_EPSILON := 0.001
const HIPS_HEADING_EPSILON := 0.001
const ROLE_BY_ACTION := {
	"mixamo_c9ccf750_b96c_11e4_a802_0aaa78deedf9": "Idle",
	"mixamo_c9ccf814_b96c_11e4_a802_0aaa78deedf9": "ForwardWalk",
	"mixamo_c9ccf998_b96c_11e4_a802_0aaa78deedf9": "BackwardWalk",
	"mixamo_c9ccf8d5_b96c_11e4_a802_0aaa78deedf9": "WalkArcLeft",
	"derived_mirror_c9ccf8d5_b96c_11e4_a802_0aaa78deedf9": "WalkArcRight",
	"mixamo_c9c9d9d6_b96c_11e4_a802_0aaa78deedf9": "SideStepLeft",
	"mixamo_c9c9db9e_b96c_11e4_a802_0aaa78deedf9": "SideStepRight",
	"mixamo_c9ceef5f_b96c_11e4_a802_0aaa78deedf9": "TurnInPlaceLeft90",
	"mixamo_c9cef01d_b96c_11e4_a802_0aaa78deedf9": "TurnInPlaceRight90",
}
static var GRAPH_CLIP_MAPS := {
	"female": {
		"Idle": _role("mixamo_c9ccf750_b96c_11e4_a802_0aaa78deedf9", "female_ordinary", "female", "Idle", false, "Female ordinary idle."),
		"ForwardWalk": _role("mixamo_c9ccf814_b96c_11e4_a802_0aaa78deedf9", "female_ordinary", "female", "ForwardWalk", false, "Female ordinary forward walk."),
		"BackwardWalk": _role("mixamo_c9ccf998_b96c_11e4_a802_0aaa78deedf9", "female_ordinary", "female", "BackwardWalk", false, "Female ordinary bounded rear correction."),
		"SideStepLeft": _role("mixamo_c9c9d9d6_b96c_11e4_a802_0aaa78deedf9", "ordinary_unarmed", "neutral", "SideStepLeft", false, "Natural ordinary left lateral correction."),
		"SideStepRight": _role("mixamo_c9c9db9e_b96c_11e4_a802_0aaa78deedf9", "ordinary_unarmed", "neutral", "SideStepRight", false, "Natural ordinary right lateral correction."),
		"WalkArcLeft": _role("mixamo_c9ccf8d5_b96c_11e4_a802_0aaa78deedf9", "female_ordinary", "female", "WalkArcLeft", false, "Female ordinary left walking arc."),
		"WalkArcRight": _role("derived_mirror_c9ccf8d5_b96c_11e4_a802_0aaa78deedf9", "female_ordinary", "female", "WalkArcRight", false, "Derived mirror of vetted natural female left arc."),
		"TurnInPlaceLeft90": _role("mixamo_c9ceef5f_b96c_11e4_a802_0aaa78deedf9", "ordinary_unarmed", "neutral", "TurnInPlaceLeft90", false, "Ordinary unarmed left 90-degree pivot."),
		"TurnInPlaceRight90": _role("mixamo_c9cef01d_b96c_11e4_a802_0aaa78deedf9", "ordinary_unarmed", "neutral", "TurnInPlaceRight90", false, "Ordinary unarmed right 90-degree pivot."),
	},
	"male": {
		"Idle": _role("mixamo_c9ccf750_b96c_11e4_a802_0aaa78deedf9", "female_ordinary", "female", "Idle", true, "Shared selected standing library; replace only with an approved male role map revision."),
		"ForwardWalk": _role("mixamo_c9ccf814_b96c_11e4_a802_0aaa78deedf9", "female_ordinary", "female", "ForwardWalk", true, "Shared selected standing library; replace only with an approved male role map revision."),
		"BackwardWalk": _role("mixamo_c9ccf998_b96c_11e4_a802_0aaa78deedf9", "female_ordinary", "female", "BackwardWalk", true, "Shared selected standing library; replace only with an approved male role map revision."),
		"SideStepLeft": _role("mixamo_c9c9d9d6_b96c_11e4_a802_0aaa78deedf9", "ordinary_unarmed", "neutral", "SideStepLeft", true, "Shared selected standing library; replace only with an approved male role map revision."),
		"SideStepRight": _role("mixamo_c9c9db9e_b96c_11e4_a802_0aaa78deedf9", "ordinary_unarmed", "neutral", "SideStepRight", true, "Shared selected standing library; replace only with an approved male role map revision."),
		"WalkArcLeft": _role("mixamo_c9ccf8d5_b96c_11e4_a802_0aaa78deedf9", "female_ordinary", "female", "WalkArcLeft", true, "Shared selected standing library; replace only with an approved male role map revision."),
		"WalkArcRight": _role("derived_mirror_c9ccf8d5_b96c_11e4_a802_0aaa78deedf9", "female_ordinary", "female", "WalkArcRight", true, "Shared selected standing library; replace only with an approved male role map revision."),
		"TurnInPlaceLeft90": _role("mixamo_c9ceef5f_b96c_11e4_a802_0aaa78deedf9", "ordinary_unarmed", "neutral", "TurnInPlaceLeft90", true, "Shared selected standing library; replace only with an approved male role map revision."),
		"TurnInPlaceRight90": _role("mixamo_c9cef01d_b96c_11e4_a802_0aaa78deedf9", "ordinary_unarmed", "neutral", "TurnInPlaceRight90", true, "Shared selected standing library; replace only with an approved male role map revision."),
	},
}

var _errors: Array[String] = []
var _source_roots: Array[Node] = []
var _heading_neutralised_actions: Array[String] = []

func _init() -> void:
	var index := _load_json(INDEX_PATH)
	var motions := _standing_motions(index)
	var canonical_clip_uids := _load_canonical_clip_uids(motions)
	if _has_errors():
		_finish()
		return
	_ensure_dir(CLIP_DIR)
	var source := _load_source_library(motions)
	if _has_errors():
		_finish()
		return

	var library := AnimationLibrary.new()
	var clips: Array[Dictionary] = []
	for motion in motions:
		var action := String(motion["action"])
		var imported: Animation = source.get_animation(action)
		if imported == null:
			_fail("Imported scene is missing action: %s" % action)
			continue
		if imported.loop_mode != _expected_loop_mode(motion):
			_fail("Imported source loop policy mismatch: %s" % action)
			continue
		_validate_required_tracks(action, imported)
		var output := _copy_animation(action, imported)
		_validate_packaged_root_tracks(action, output)
		_validate_cyclic_root_track_wrapping(action, motion, output)
		var clip_path := "%s/%s.res" % [CLIP_DIR, action]
		var save_error := ResourceSaver.save(output, clip_path)
		if save_error != OK:
			_fail("Failed to save %s: %s" % [clip_path, error_string(save_error)])
			continue
		if canonical_clip_uids.has(action):
			var uid_error := ResourceSaver.set_uid(clip_path, canonical_clip_uids[action])
			if uid_error != OK:
				_fail("Failed to preserve canonical UID for %s: %s" % [clip_path, error_string(uid_error)])
				continue
		var clip := ResourceLoader.load(clip_path, "Animation", ResourceLoader.CACHE_MODE_REPLACE) as Animation
		if clip == null:
			_fail("Extracted clip failed to reload: %s" % clip_path)
			continue
		var add_error := library.add_animation(action, clip)
		if add_error != OK:
			_fail("Failed to add %s to library: %s" % [action, error_string(add_error)])
			continue
		clips.append(_catalogue_entry(motion, clip_path, clip))

	if clips.size() != EXPECTED_COUNT:
		_fail("Expected %d packaged clips, found %d" % [EXPECTED_COUNT, clips.size()])
	if not _has_errors():
		_remove_stale_clips(clips)
	library.resource_name = "standing_locomotion_library"
	if library.get_animation_list() != _actions_from_motions(motions):
		_fail("AnimationLibrary keys do not correspond to processed actions")
	if not _has_errors():
		var save_error := ResourceSaver.save(library, LIBRARY_PATH)
		if save_error != OK:
			_fail("Failed to save library: %s" % error_string(save_error))
	var catalogue := {
		"catalogue_schema_version": 2,
		"source_index": INDEX_PATH,
		"source_index_schema_version": INDEX_SCHEMA_VERSION,
		"metrics_schema_version": METRICS_SCHEMA_VERSION,
		"animation_library": LIBRARY_PATH,
		"clip_count": clips.size(),
		"clips": clips,
		"role_maps": _role_maps(),
	}
	if not _has_errors():
		_save_json(CATALOGUE_PATH, catalogue)
		print("Generated %d standing locomotion catalogue clips; neutralised Hips heading in %d clips: %s" % [clips.size(), _heading_neutralised_actions.size(), ",".join(_heading_neutralised_actions)])
	_finish()

func _standing_motions(index: Dictionary) -> Array[Dictionary]:
	if int(index.get("schema_version", -1)) != INDEX_SCHEMA_VERSION:
		_fail("Processed index schema must be %d" % INDEX_SCHEMA_VERSION)
	if int(index.get("metrics_schema_version", -1)) != METRICS_SCHEMA_VERSION:
		_fail("Processed index metrics schema must be %d" % METRICS_SCHEMA_VERSION)
	var source = index.get("motions", {})
	if not (source is Dictionary):
		_fail("Processed index motions is not a Dictionary")
		return []
	var motions: Array[Dictionary] = []
	var seen := {}
	for motion_id in source:
		var motion = source[motion_id]
		if not (motion is Dictionary):
			continue
		if String(motion.get("status", "")) != "success":
			continue
		if String(motion.get("group", "")) != "locomotion_standing":
			_fail("Successful processed motion belongs to another group: %s" % motion_id)
			continue
		var action := String(motion.get("action", ""))
		if action.is_empty() or seen.has(action):
			_fail("Empty or duplicate processed action: %s" % action)
			continue
		seen[action] = true
		motions.append(motion)
	motions.sort_custom(func(a: Dictionary, b: Dictionary) -> bool: return String(a["action"]) < String(b["action"]))
	if motions.size() != EXPECTED_COUNT:
		_fail("Expected %d successful standing motions, found %d" % [EXPECTED_COUNT, motions.size()])
	return motions

func _load_canonical_clip_uids(motions: Array[Dictionary]) -> Dictionary:
	var config := ConfigFile.new()
	var load_error := config.load(SIDECAR_PATH)
	if load_error != OK:
		_fail("Failed to load import sidecar UIDs: %s" % error_string(load_error))
		return {}
	var subresources = config.get_value("params", "_subresources", {})
	var animations = subresources.get("animations", {}) if subresources is Dictionary else null
	if not (animations is Dictionary):
		_fail("Standing import sidecar has no animation settings")
		return {}
	var result := {}
	for motion in motions:
		var action := String(motion["action"])
		var settings = animations.get(action, {})
		if not (settings is Dictionary):
			_fail("Standing import sidecar is missing animation settings: %s" % action)
			continue
		var expected_path := "%s/%s.res" % [CLIP_DIR, action]
		var primary_path := String(settings.get("save_to_file/path", ""))
		if primary_path == expected_path:
			continue
		if not primary_path.begins_with("uid://"):
			_fail("Standing import sidecar has an unsupported primary path: %s" % action)
			continue
		var uid := ResourceUID.text_to_id(primary_path)
		if uid == ResourceUID.INVALID_ID:
			_fail("Standing import sidecar has an invalid canonical UID: %s" % action)
			continue
		result[action] = uid
	return result

func _load_source_library(motions: Array[Dictionary]) -> AnimationLibrary:
	var blend_paths := {}
	for motion in motions:
		blend_paths[_to_res_path(String(motion.get("group_blend", "")))] = true
	if blend_paths.size() != 1:
		_fail("Standing motions must reference exactly one group .blend")
		return null
	var blend_path := String(blend_paths.keys()[0])
	var packed := ResourceLoader.load(blend_path, "PackedScene", ResourceLoader.CACHE_MODE_REPLACE) as PackedScene
	if packed == null:
		_fail("Failed to load imported standing scene: %s" % blend_path)
		return null
	var scene_root := packed.instantiate()
	_source_roots.append(scene_root)
	var players := scene_root.find_children("*", "AnimationPlayer", true, false)
	var skeletons := scene_root.find_children("*", "Skeleton3D", true, false)
	if players.size() != 1 or skeletons.size() != 1:
		_fail("Imported standing scene must contain one AnimationPlayer and one Skeleton3D")
		return null
	var player := players[0] as AnimationPlayer
	var names := player.get_animation_library_list()
	if names.size() != 1:
		_fail("Imported standing scene must contain one AnimationLibrary")
		return null
	return player.get_animation_library(names[0])

func _copy_animation(action: String, source: Animation) -> Animation:
	var output := Animation.new()
	output.resource_name = action
	output.length = source.length
	output.loop_mode = source.loop_mode
	output.step = source.step
	for source_track in source.get_track_count():
		source.copy_track(source_track, output)
		var target_track := output.get_track_count() - 1
		var path := String(source.track_get_path(source_track))
		var normalised := _normalise_path(path)
		if normalised != path:
			output.track_set_path(target_track, NodePath(normalised))
		_validate_track_copy(action, source, source_track, output, target_track)
	_neutralise_hips_heading(action, output)
	return output

func _expected_loop_mode(motion: Dictionary) -> Animation.LoopMode:
	var intent = motion.get("loop_intent", {})
	if not (intent is Dictionary) or not (intent as Dictionary).get("effective_loop_intent") is bool:
		_fail("Processed action has no persisted ANIM-001 effective loop intent: %s" % motion.get("action", ""))
		return Animation.LOOP_NONE
	return Animation.LOOP_LINEAR if bool((intent as Dictionary)["effective_loop_intent"]) else Animation.LOOP_NONE

func _neutralise_hips_heading(action: String, animation: Animation) -> void:
	var root_track := animation.find_track(^"%GeneralSkeleton:Root", Animation.TYPE_ROTATION_3D)
	var hips_track := animation.find_track(^"%GeneralSkeleton:Hips", Animation.TYPE_ROTATION_3D)
	if root_track < 0 or hips_track < 0 or animation.track_get_key_count(hips_track) < 2:
		return
	var key_count := animation.track_get_key_count(hips_track)
	var first_time := animation.track_get_key_time(hips_track, 0)
	var root_start := animation.rotation_track_interpolate(root_track, first_time).normalized()
	var hips_start := (animation.track_get_key_value(hips_track, 0) as Quaternion).normalized()
	var root_headings: Array[float] = []
	var hips_headings: Array[float] = []
	var previous_root_heading := 0.0
	var previous_hips_heading := 0.0
	for key_index in key_count:
		var key_time := animation.track_get_key_time(hips_track, key_index)
		var root_rotation := animation.rotation_track_interpolate(root_track, key_time).normalized()
		var hips_rotation := (animation.track_get_key_value(hips_track, key_index) as Quaternion).normalized()
		var root_heading := _signed_heading(root_rotation * root_start.inverse())
		var hips_heading := _signed_heading(hips_rotation * hips_start.inverse())
		if key_index > 0:
			root_heading = _unwrap_heading(root_heading, previous_root_heading)
			hips_heading = _unwrap_heading(hips_heading, previous_hips_heading)
		root_headings.append(root_heading)
		hips_headings.append(hips_heading)
		previous_root_heading = root_heading
		previous_hips_heading = hips_heading
	var root_total := root_headings[-1]
	var hips_total := hips_headings[-1]
	if absf(root_total) <= ROOT_HEADING_EPSILON or absf(hips_total) <= HIPS_HEADING_EPSILON:
		return
	_heading_neutralised_actions.append(action)

	# Root is the sole planar-heading authority. The imported Hips track is a direct Root child and
	# contains the source body's accumulated heading after Root reconstruction. Remove only that
	# parent-frame Y-axis twist, following Root's unwrapped progress, while retaining Hips swing,
	# translation, scale, key timing, and interpolation. The reference rig's 180-degree container
	# conjugates this same vertical axis and therefore does not invert or otherwise alter the correction.
	for key_index in key_count:
		var progress := clampf(root_headings[key_index] / root_total, 0.0, 1.0)
		var correction := hips_total * progress
		var hips_rotation := (animation.track_get_key_value(hips_track, key_index) as Quaternion).normalized()
		var corrected := (Quaternion(HEADING_AXIS, -correction) * hips_rotation).normalized()
		animation.track_set_key_value(hips_track, key_index, corrected)

	var corrected_finish := (animation.track_get_key_value(hips_track, key_count - 1) as Quaternion).normalized()
	var residual_heading := _signed_heading(corrected_finish * hips_start.inverse())
	if absf(residual_heading) > HIPS_HEADING_EPSILON:
		_fail("%s Hips heading neutralisation left %.9f radians" % [action, residual_heading])

func _signed_heading(rotation: Quaternion) -> float:
	var vector := Vector3(rotation.x, rotation.y, rotation.z)
	var projected := HEADING_AXIS * vector.dot(HEADING_AXIS)
	var twist := Quaternion(projected.x, projected.y, projected.z, rotation.w)
	var magnitude := sqrt((twist.x * twist.x) + (twist.y * twist.y) + (twist.z * twist.z) + (twist.w * twist.w))
	if magnitude <= 0.000001:
		return 0.0
	twist = Quaternion(twist.x / magnitude, twist.y / magnitude, twist.z / magnitude, twist.w / magnitude)
	return wrapf(2.0 * atan2(Vector3(twist.x, twist.y, twist.z).dot(HEADING_AXIS), twist.w), -PI, PI)

func _unwrap_heading(value: float, previous: float) -> float:
	return previous + wrapf(value - previous, -PI, PI)

func _normalise_path(path: String) -> String:
	var separator := path.rfind(":")
	if separator <= 0:
		return path
	var prefix := path.substr(0, separator)
	# The shared AnimationTree resolves root motion through the runtime owner's unique
	# skeleton name. Keep every packaged bone track on that same contract.
	if prefix == "GeneralSkeleton" or prefix == "Female/GeneralSkeleton":
		return TARGET_PREFIX + path.substr(separator)
	return path

func _validate_track_copy(action: String, source: Animation, source_track: int, output: Animation, target_track: int) -> void:
	if source.track_get_type(source_track) != output.track_get_type(target_track):
		_fail("Track type changed for %s track %d" % [action, source_track])
	if source.track_is_enabled(source_track) != output.track_is_enabled(target_track):
		_fail("Track enabled state changed for %s track %d" % [action, source_track])
	if source.track_get_interpolation_type(source_track) != output.track_get_interpolation_type(target_track):
		_fail("Track interpolation changed for %s track %d" % [action, source_track])
	if source.track_get_interpolation_loop_wrap(source_track) != output.track_get_interpolation_loop_wrap(target_track):
		_fail("Track loop wrapping changed for %s track %d" % [action, source_track])
	var count := source.track_get_key_count(source_track)
	if output.track_get_key_count(target_track) != count:
		_fail("Track key count changed for %s track %d" % [action, source_track])
		return
	for key in count:
		if source.track_get_key_time(source_track, key) != output.track_get_key_time(target_track, key):
			_fail("Track key time changed for %s track %d key %d" % [action, source_track, key])
		if source.track_get_key_transition(source_track, key) != output.track_get_key_transition(target_track, key):
			_fail("Track key transition changed for %s track %d key %d" % [action, source_track, key])
		if source.track_get_key_value(source_track, key) != output.track_get_key_value(target_track, key):
			_fail("Track key value changed for %s track %d key %d" % [action, source_track, key])

func _validate_required_tracks(action: String, animation: Animation) -> void:
	var bones := {}
	var root_position := false
	var root_rotation := false
	var hips_position := false
	for track in animation.get_track_count():
		var path := String(animation.track_get_path(track))
		var separator := path.rfind(":")
		if separator <= 0:
			continue
		var prefix := path.substr(0, separator)
		var bone := path.substr(separator + 1)
		if not ACCEPTED_PREFIXES.has(prefix):
			_fail("%s has unsupported skeleton prefix in %s" % [action, path])
		bones[bone] = true
		var type := animation.track_get_type(track)
		root_position = root_position or (bone == "Root" and type == Animation.TYPE_POSITION_3D)
		root_rotation = root_rotation or (bone == "Root" and type == Animation.TYPE_ROTATION_3D)
		hips_position = hips_position or (bone == "Hips" and type == Animation.TYPE_POSITION_3D)
	for bone in REQUIRED_BONES:
		if not bones.has(bone):
			_fail("%s is missing required bone tracks for %s" % [action, bone])
	if not root_position or not root_rotation or not hips_position:
		_fail("%s is missing required Root/Hips transform tracks" % action)

func _validate_packaged_root_tracks(action: String, animation: Animation) -> void:
	var position_track := animation.find_track(^"%GeneralSkeleton:Root", Animation.TYPE_POSITION_3D)
	var rotation_track := animation.find_track(^"%GeneralSkeleton:Root", Animation.TYPE_ROTATION_3D)
	if position_track < 0 or rotation_track < 0:
		_fail("%s packaging did not retain canonical %%GeneralSkeleton:Root transform tracks" % action)
		return
	if animation.track_get_key_count(position_track) == 0 or animation.track_get_key_count(rotation_track) == 0:
		_fail("%s packaging produced an empty canonical Root transform track" % action)

func _validate_cyclic_root_track_wrapping(action: String, motion: Dictionary, animation: Animation) -> void:
	if _expected_loop_mode(motion) != Animation.LOOP_LINEAR:
		return
	for track_type in [Animation.TYPE_POSITION_3D, Animation.TYPE_ROTATION_3D]:
		var track := animation.find_track(^"%GeneralSkeleton:Root", track_type)
		if track < 0 or not animation.track_get_interpolation_loop_wrap(track):
			_fail("%s cyclic Root track must preserve imported loop wrapping for continuous root-motion deltas" % action)

func _catalogue_entry(motion: Dictionary, clip_path: String, clip: Animation) -> Dictionary:
	var metrics_path := _to_res_path(String(motion.get("metrics", "")))
	var metrics := _load_json(metrics_path)
	if int(metrics.get("schema_version", -1)) != METRICS_SCHEMA_VERSION:
		_fail("Metrics schema mismatch: %s" % metrics_path)
	if String(metrics.get("action", "")) != String(motion["action"]):
		_fail("Metrics action mismatch: %s" % metrics_path)
	if String(metrics.get("root_source", "")) != "reconstructed_root" or not bool(metrics.get("root_created", false)):
		_fail("Metrics root invariant failed: %s" % metrics_path)
	var source_manifest: Dictionary = motion.get("manifest", {})
	return {
		"key": String(motion["action"]),
		"role": ROLE_BY_ACTION.get(String(motion["action"]), ""),
		"animation_resource": clip_path,
		"motion_id": String(motion.get("motion_id", "")),
		"action": String(motion["action"]),
		"group": String(motion.get("group", "")),
		"group_blend": _to_res_path(String(motion.get("group_blend", ""))),
		"metrics": metrics_path,
		"motion_class": String(motion.get("motion_class", "")),
		"category": String(motion.get("category", "")),
		"tags": motion.get("tags", []),
		"source_manifest": {
			"file": String(source_manifest.get("file", "")),
			"name": String(source_manifest.get("name", "")),
			"description": String(source_manifest.get("description", "")),
			"type": String(source_manifest.get("type", "")),
		},
		"length": clip.length,
		"track_count": clip.get_track_count(),
		"fps": int(metrics.get("fps", 0)),
		"frame_range": metrics.get("frame_range", []),
		"sample_count": int(metrics.get("sample_count", 0)),
		"root_source": String(metrics.get("root_source", "")),
		"root_created": bool(metrics.get("root_created", false)),
		"loop_intent": motion.get("loop_intent", {}),
		"derived_provenance": motion.get("derivation_provenance", {}),
	}

func _actions_from_motions(motions: Array[Dictionary]) -> Array[StringName]:
	var actions: Array[StringName] = []
	for motion in motions:
		actions.append(StringName(motion["action"]))
	return actions

static func _role(key: String, family: String, gender: String, role: String, temporary: bool, replacement_note: String) -> Dictionary:
	return {
		"library_key": key,
		"motion_family": family,
		"clip_gender": gender,
		"graph_role": role,
		"temporary": temporary,
		"replacement_note": replacement_note,
	}

static func _role_maps() -> Dictionary:
	var result := {}
	for character in GRAPH_CLIP_MAPS:
		var entries: Array[Dictionary] = []
		for role in GRAPH_CLIP_MAPS[character]:
			var entry: Dictionary = GRAPH_CLIP_MAPS[character][role].duplicate(true)
			entries.append(entry)
		result["reference_" + String(character)] = entries
	return result

func _remove_stale_clips(clips: Array[Dictionary]) -> void:
	var expected := {}
	for clip in clips:
		expected[String(clip["animation_resource"]).get_file()] = true
	var directory := DirAccess.open(CLIP_DIR)
	if directory == null:
		_fail("Failed to open dedicated clips directory")
		return
	for file_name in directory.get_files():
		if file_name.ends_with(".res") and not expected.has(file_name):
			var error := directory.remove(file_name)
			if error != OK:
				_fail("Failed to remove stale clip %s: %s" % [file_name, error_string(error)])

func _load_json(path: String) -> Dictionary:
	if path.is_empty() or not FileAccess.file_exists(path):
		_fail("Missing JSON file: %s" % path)
		return {}
	var parsed = JSON.parse_string(FileAccess.get_file_as_string(path))
	if not (parsed is Dictionary):
		_fail("Invalid JSON object: %s" % path)
		return {}
	return parsed

func _save_json(path: String, value: Dictionary) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		_fail("Failed to open catalogue for writing: %s" % path)
		return
	file.store_string(JSON.stringify(value, "  ", false) + "\n")

func _ensure_dir(path: String) -> void:
	var error := DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(path))
	if error != OK and error != ERR_ALREADY_EXISTS:
		_fail("Failed to create %s: %s" % [path, error_string(error)])

func _to_res_path(path: String) -> String:
	return "res://" + path.substr(5) if path.begins_with("game/") else path

func _fail(message: String) -> void:
	push_error(message)
	_errors.append(message)

func _has_errors() -> bool:
	return not _errors.is_empty()

func _finish() -> void:
	for message in _errors:
		printerr(message)
	for scene_root in _source_roots:
		scene_root.free()
	_source_roots.clear()
	quit(1 if _has_errors() else 0)
