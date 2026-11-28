extends RefCounted

# Compares two native clips on one skeleton by effective local pose (a bone without a track holds its rest) and by
# forward-kinematics global joint positions, sampled at the given rate over the shorter clip. Positions are reported
# in metres (track values multiplied by the skeleton's motion scale, as the engine plays them).


static func compare(candidate: Animation, reference: Animation, skeleton: Dictionary, fps: float) -> Dictionary:
	var names: Array[String] = skeleton["names"]
	var parents: Array[int] = skeleton["parents"]
	var rests: Array[Transform3D] = skeleton["rests"]
	var motion_scale := float(skeleton["motion_scale"])
	var skeleton_name := String(skeleton["name"])
	var length := minf(candidate.length, reference.length)
	var count := int(floor(length * fps + 1e-6)) + 1
	var per_bone := {}
	for bone in names.size():
		per_bone[names[bone]] = {"max_rotation_deg": 0.0, "mean_rotation_deg": 0.0, "max_local_position_m": 0.0, "max_global_position_m": 0.0}
	var worst_rotation := {"bone": "", "deg": 0.0, "time": 0.0}
	var worst_global := {"bone": "", "m": 0.0, "time": 0.0}
	for sample in count:
		var time := minf(sample / fps, length)
		var candidate_globals := _globals(candidate, time, names, parents, rests, motion_scale, skeleton_name)
		var reference_globals := _globals(reference, time, names, parents, rests, motion_scale, skeleton_name)
		for bone in names.size():
			var a := _local(candidate, time, bone, names, rests, motion_scale, skeleton_name)
			var b := _local(reference, time, bone, names, rests, motion_scale, skeleton_name)
			var angle := rad_to_deg(a.basis.get_rotation_quaternion().angle_to(b.basis.get_rotation_quaternion()))
			var entry := per_bone[names[bone]] as Dictionary
			entry["max_rotation_deg"] = maxf(float(entry["max_rotation_deg"]), angle)
			entry["mean_rotation_deg"] = float(entry["mean_rotation_deg"]) + angle / count
			entry["max_local_position_m"] = maxf(float(entry["max_local_position_m"]), a.origin.distance_to(b.origin))
			var global_error := candidate_globals[bone].origin.distance_to(reference_globals[bone].origin)
			entry["max_global_position_m"] = maxf(float(entry["max_global_position_m"]), global_error)
			if angle > float(worst_rotation["deg"]):
				worst_rotation = {"bone": names[bone], "deg": angle, "time": time}
			if global_error > float(worst_global["m"]):
				worst_global = {"bone": names[bone], "m": global_error, "time": time}
	var mean_rotation := 0.0
	for bone in names:
		mean_rotation += float((per_bone[bone] as Dictionary)["mean_rotation_deg"]) / names.size()
	return {
		"samples": count, "fps": fps, "candidate_length": candidate.length, "reference_length": reference.length,
		"candidate_loop_mode": candidate.loop_mode, "reference_loop_mode": reference.loop_mode,
		"worst_rotation": worst_rotation, "worst_global_position": worst_global,
		"mean_rotation_deg_over_bones": mean_rotation, "per_bone": per_bone,
	}


static func _local(animation: Animation, time: float, bone: int, names: Array[String], rests: Array[Transform3D], motion_scale: float, skeleton_name: String) -> Transform3D:
	var path := NodePath("%%%s:%s" % [skeleton_name, names[bone]])
	var rest := rests[bone]
	var origin := rest.origin
	var rotation := rest.basis.get_rotation_quaternion()
	var scale := rest.basis.get_scale()
	var position_track := animation.find_track(path, Animation.TYPE_POSITION_3D)
	if position_track >= 0:
		origin = animation.position_track_interpolate(position_track, time) * motion_scale
	var rotation_track := animation.find_track(path, Animation.TYPE_ROTATION_3D)
	if rotation_track >= 0:
		rotation = animation.rotation_track_interpolate(rotation_track, time)
	var scale_track := animation.find_track(path, Animation.TYPE_SCALE_3D)
	if scale_track >= 0:
		scale = animation.scale_track_interpolate(scale_track, time)
	return Transform3D(Basis(rotation).scaled(scale), origin)


static func _globals(animation: Animation, time: float, names: Array[String], parents: Array[int], rests: Array[Transform3D], motion_scale: float, skeleton_name: String) -> Array[Transform3D]:
	var globals: Array[Transform3D] = []
	globals.resize(names.size())
	for bone in names.size():
		var local := _local(animation, time, bone, names, rests, motion_scale, skeleton_name)
		globals[bone] = local if parents[bone] < 0 else globals[parents[bone]] * local
	return globals


# Measures how far a clip's forward-kinematics joint positions on the skeleton are from the Blender-evaluated joint
# positions it was sampled from (converted to Godot axes and the fitted rig scale). This is the preview-parity
# measurement: the Blender samples are exactly what the author previewed.
static func compare_to_samples(animation: Animation, samples: Dictionary, skeleton: Dictionary, sources: Array[int], scale: float) -> Dictionary:
	var names: Array[String] = skeleton["names"]
	var parents: Array[int] = skeleton["parents"]
	var rests: Array[Transform3D] = skeleton["rests"]
	var motion_scale := float(skeleton["motion_scale"])
	var skeleton_name := String(skeleton["name"])
	var frames := samples["frames"] as Array
	var fps := float(samples["fps"])
	var axis := Basis(Vector3(1, 0, 0), Vector3(0, 0, -1), Vector3(0, 1, 0))
	var worst := {"bone": "", "m": 0.0, "frame": 0}
	var total := 0.0
	var count := 0
	for frame in frames.size():
		var time := minf(frame / fps, animation.length)
		var globals := _globals(animation, time, names, parents, rests, motion_scale, skeleton_name)
		for bone in names.size():
			if sources[bone] < 0:
				continue
			var rows := (frames[frame] as Array)[sources[bone]] as Array
			var expected := axis * Vector3(float(rows[0][3]), float(rows[1][3]), float(rows[2][3])) * scale
			var error := globals[bone].origin.distance_to(expected)
			total += error
			count += 1
			if error > float(worst["m"]):
				worst = {"bone": names[bone], "m": error, "frame": frame}
	return {"worst_joint_position": worst, "mean_joint_position_m": total / maxi(count, 1), "samples": frames.size()}
