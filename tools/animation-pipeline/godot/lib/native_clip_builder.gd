extends RefCounted

# Builds an ANIM-003 native idle clip from Blender-evaluated armature samples
# (`anim003_native_clip_samples_v1`, written by `kimodo_pipeline/native_clip_sampling.py`).
#
# Blender samples are armature-space bone matrices of the evaluated reference rig (Z-up). The imported Godot
# skeleton differs from the Blender rig in path-independent ways that are all recovered from the imported character:
# - the glTF axis change, Blender (x, y, z) → Godot (x, z, -y);
# - an optional uniform scale (a calibrated setup may carry a scaled build of the character), fitted from the rest
#   joints and refused unless every joint agrees;
# - the importer's rest fixer, which rewrites each bone's local axes. A skin bind pose is the inverse of the bone's
#   Godot global transform while the mesh sits in the Blender rest, so `offset = convert(blender_rest)^-1 * bind^-1`
#   is that bone's constant frame change.
# Every sampled global pose is converted with its offset and made parent-relative on the Godot hierarchy. A skeleton
# bone the rig lacks (such as a forearm twist bone) holds its rest pose, as it does during playback. Tracks bind to
# `%<skeleton name>:<bone>`; rotations are always written, positions only for bones whose local translation leaves
# its rest (so each character keeps its own bone lengths), and positions are divided by `Skeleton3D.motion_scale`,
# because the engine multiplies position tracks by it during playback (ANIM-003 authoring item 4).

const SAMPLES_SCHEMA := "anim003_native_clip_samples_v1"
# Local translations that differ from the bone's rest translation by less than this (metres) at every frame are
# not exported as position tracks, so bone lengths stay owned by each character's own skeleton.
const POSITION_TOLERANCE := 1e-4
const SCALE_TOLERANCE := 1e-4
const OFFSET_TRANSLATION_TOLERANCE := 1e-3

# Blender armature space → Godot skeleton space: (x, y, z) → (x, z, -y).
const AXIS := Basis(Vector3(1, 0, 0), Vector3(0, 0, -1), Vector3(0, 1, 0))


static func load_samples(path: String) -> Dictionary:
	if not FileAccess.file_exists(path):
		return {"error": "Sample file is missing: %s" % path}
	var value: Variant = JSON.parse_string(FileAccess.get_file_as_string(path))
	if not value is Dictionary:
		return {"error": "Sample file is not a JSON object: %s" % path}
	var samples := value as Dictionary
	if String(samples.get("schema", "")) != SAMPLES_SCHEMA:
		return {"error": "Sample file schema must be '%s': %s" % [SAMPLES_SCHEMA, path]}
	for field in ["bones", "frames", "fps"]:
		if not samples.has(field):
			return {"error": "Sample file lacks '%s': %s" % [field, path]}
	if (samples["frames"] as Array).size() < 2:
		return {"error": "Sample file needs at least two frames: %s" % path}
	return {"samples": samples}


static func load_skeleton(scene_path: String) -> Dictionary:
	var scene := ResourceLoader.load(scene_path, "PackedScene") as PackedScene
	if scene == null:
		return {"error": "Character scene is unavailable: %s" % scene_path}
	var root := scene.instantiate()
	var skeletons := root.find_children("*", "Skeleton3D", true, false)
	if skeletons.size() != 1:
		root.free()
		return {"error": "Character scene must contain exactly one Skeleton3D: %s" % scene_path}
	var skeleton := skeletons[0] as Skeleton3D
	var binds := {}
	for mesh in root.find_children("*", "MeshInstance3D", true, false):
		var skin := (mesh as MeshInstance3D).skin
		if skin == null:
			continue
		for index in skin.get_bind_count():
			var name := String(skin.get_bind_name(index))
			if not binds.has(name):
				binds[name] = skin.get_bind_pose(index)
	var names: Array[String] = []
	var parents: Array[int] = []
	var rests: Array[Transform3D] = []
	for bone in skeleton.get_bone_count():
		names.append(skeleton.get_bone_name(bone))
		parents.append(skeleton.get_bone_parent(bone))
		rests.append(skeleton.get_bone_rest(bone))
	var result := {
		"name": String(skeleton.name), "motion_scale": skeleton.motion_scale, "names": names,
		"parents": parents, "rests": rests, "binds": binds,
	}
	root.free()
	return {"skeleton": result}


# Returns {"animation": Animation, "report": Dictionary} or {"error": String}.
static func build(samples: Dictionary, skeleton: Dictionary, bone_map: BoneMap, clip_name: String, loop: bool) -> Dictionary:
	var blender_index := {}
	var blender_bones := samples["bones"] as Array
	for index in blender_bones.size():
		blender_index[String((blender_bones[index] as Dictionary)["name"])] = index
	var names: Array[String] = skeleton["names"]
	var parents: Array[int] = skeleton["parents"]
	var binds: Dictionary = skeleton["binds"]
	# Godot bone → Blender bone index, through the importer's bone map for renamed bones.
	var sources: Array[int] = []
	var bind_globals: Array[Transform3D] = []
	var unmapped: Array[String] = []
	for bone in names.size():
		var source_name := names[bone]
		if bone_map != null:
			var mapped := String(bone_map.get_skeleton_bone_name(StringName(names[bone])))
			if not mapped.is_empty():
				source_name = mapped
		if not blender_index.has(source_name) or not binds.has(names[bone]):
			sources.append(-1)
			bind_globals.append(Transform3D.IDENTITY)
			unmapped.append(names[bone])
			continue
		sources.append(int(blender_index[source_name]))
		bind_globals.append((binds[names[bone]] as Transform3D).affine_inverse())
	# A sampled rig may be a uniformly scaled build of the imported character (normalised position tracks transfer
	# across scale, as the importer's own normalisation does). Fit that scale from the rest joints and refuse
	# anything that is not a uniform scale of the same rig.
	var numerator := 0.0
	var denominator := 0.0
	for bone in names.size():
		if sources[bone] < 0:
			continue
		var rest_origin := _convert(_matrix((blender_bones[sources[bone]] as Dictionary)["rest"]), 1.0).origin
		numerator += rest_origin.dot(bind_globals[bone].origin)
		denominator += rest_origin.length_squared()
	if denominator <= 0.0:
		return {"error": "No sampled bone maps onto the imported skeleton."}
	var scale := numerator / denominator
	var offsets: Array[Transform3D] = []
	var maximum_offset_translation := 0.0
	for bone in names.size():
		if sources[bone] < 0:
			offsets.append(Transform3D.IDENTITY)
			continue
		var offset := _convert(_matrix((blender_bones[sources[bone]] as Dictionary)["rest"]), scale).affine_inverse() * bind_globals[bone]
		maximum_offset_translation = maxf(maximum_offset_translation, offset.origin.length())
		offsets.append(Transform3D(offset.basis.orthonormalized(), Vector3.ZERO))
	if maximum_offset_translation > OFFSET_TRANSLATION_TOLERANCE:
		return {"error": "The sampled rig rest is not a uniform scale (%.5f) of the imported skeleton: joints disagree by up to %.4f m." % [scale, maximum_offset_translation]}
	var frames := samples["frames"] as Array
	var fps := float(samples["fps"])
	var motion_scale := float(skeleton["motion_scale"])
	var rests: Array[Transform3D] = skeleton["rests"]
	var locals := []
	for bone in names.size():
		locals.append([])
	for frame in frames:
		var globals: Array[Transform3D] = []
		globals.resize(names.size())
		# A skeleton bone the rig lacks (for example a forearm twist bone between a sampled forearm and hand) holds
		# its rest pose relative to its parent, as it does during playback before any later modifier runs.
		for bone in names.size():
			var parent := parents[bone]
			if sources[bone] >= 0:
				globals[bone] = _convert(_matrix((frame as Array)[sources[bone]]), scale) * offsets[bone]
			else:
				globals[bone] = rests[bone] if parent < 0 else globals[parent] * rests[bone]
		for bone in names.size():
			if sources[bone] < 0:
				continue
			var parent := parents[bone]
			(locals[bone] as Array).append(globals[bone] if parent < 0 else globals[parent].affine_inverse() * globals[bone])
	var animation := Animation.new()
	animation.resource_name = clip_name
	animation.length = float(frames.size() - 1) / fps
	animation.loop_mode = Animation.LOOP_LINEAR if loop else Animation.LOOP_NONE
	var tracked: Array[String] = []
	var positioned: Array[String] = []
	var skeleton_name := String(skeleton["name"])
	for bone in names.size():
		if sources[bone] < 0:
			continue
		var path := NodePath("%%%s:%s" % [skeleton_name, names[bone]])
		var poses := locals[bone] as Array
		var moves := false
		var scales := false
		for pose in poses:
			var local := pose as Transform3D
			moves = moves or local.origin.distance_to(rests[bone].origin) > POSITION_TOLERANCE
			scales = scales or (local.basis.get_scale() - Vector3.ONE).length() > SCALE_TOLERANCE
		if moves:
			var position_track := _track(animation, Animation.TYPE_POSITION_3D, path, loop)
			for index in poses.size():
				animation.position_track_insert_key(position_track, index / fps, (poses[index] as Transform3D).origin / motion_scale)
			positioned.append(names[bone])
		var rotation_track := _track(animation, Animation.TYPE_ROTATION_3D, path, loop)
		var previous := Quaternion.IDENTITY
		for index in poses.size():
			var rotation := (poses[index] as Transform3D).basis.get_rotation_quaternion().normalized()
			if index > 0 and previous.dot(rotation) < 0.0:
				rotation = -rotation
			animation.rotation_track_insert_key(rotation_track, index / fps, rotation)
			previous = rotation
		if scales:
			var scale_track := _track(animation, Animation.TYPE_SCALE_3D, path, loop)
			for index in poses.size():
				animation.scale_track_insert_key(scale_track, index / fps, (poses[index] as Transform3D).basis.get_scale())
		tracked.append(names[bone])
	return {
		"animation": animation,
		"sources": sources,
		"scale": scale,
		"report": {
			"skeleton": skeleton_name, "motion_scale": motion_scale, "frames": frames.size(), "fps": fps,
			"length_seconds": animation.length, "loop": loop, "tracked_bones": tracked,
			"position_tracks": positioned, "untracked_skeleton_bones": unmapped,
			"maximum_rest_offset_translation_m": maximum_offset_translation, "source_to_skeleton_scale": scale,
		},
	}


static func _track(animation: Animation, type: Animation.TrackType, path: NodePath, loop: bool) -> int:
	var track := animation.add_track(type)
	animation.track_set_path(track, path)
	animation.track_set_interpolation_type(track, Animation.INTERPOLATION_LINEAR)
	animation.track_set_interpolation_loop_wrap(track, loop)
	return track


# Blender row-major 4x4 armature-space matrix → Transform3D in Blender space.
static func _matrix(rows: Array) -> Transform3D:
	var r0 := rows[0] as Array
	var r1 := rows[1] as Array
	var r2 := rows[2] as Array
	var basis := Basis(
		Vector3(float(r0[0]), float(r1[0]), float(r2[0])),
		Vector3(float(r0[1]), float(r1[1]), float(r2[1])),
		Vector3(float(r0[2]), float(r1[2]), float(r2[2])))
	return Transform3D(basis, Vector3(float(r0[3]), float(r1[3]), float(r2[3])))


static func _convert(transform: Transform3D, scale: float) -> Transform3D:
	return Transform3D(AXIS * transform.basis * AXIS.inverse(), AXIS * transform.origin * scale)
