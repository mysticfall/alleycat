class_name SampleTransport
extends RefCounted

const CONTRACT := "anim002_sample_transport_v1"
const TIME_TOLERANCE := 0.00001

static func validate(evidence: Dictionary, looping: bool) -> String:
	var common := ["contract", "boundary", "fps", "frame_start", "frame_end", "sample_count", "duration_frames", "duration_seconds"]
	var loop_fields := ["period_frames", "runtime_sample_count", "conceptual_boundary_frame"]
	var expected_fields := common + (loop_fields if looping else [])
	if evidence.size() != expected_fields.size():
		return "transport evidence fields do not match its boundary kind"
	for field in expected_fields:
		if not evidence.has(field):
			return "transport evidence lacks %s" % field
	if evidence["contract"] != CONTRACT or evidence["boundary"] != ("loop" if looping else "transition"):
		return "transport identity or boundary is inconsistent"
	if typeof(evidence["fps"]) != TYPE_FLOAT or not _all_exact_ints(evidence, ["frame_start", "frame_end", "sample_count", "duration_frames"]):
		return "transport numeric fields have invalid types"
	if typeof(evidence["duration_seconds"]) != TYPE_FLOAT:
		return "transport duration has invalid type"
	if looping and not _all_exact_ints(evidence, loop_fields):
		return "loop transport fields have invalid types"
	var fps := float(evidence["fps"])
	var frame_start := int(evidence["frame_start"])
	var frame_end := int(evidence["frame_end"])
	var sample_count := int(evidence["sample_count"])
	var duration_frames := int(evidence["duration_frames"])
	if not is_finite(fps) or fps <= 0.0 or frame_end <= frame_start:
		return "transport range or fps is invalid"
	if sample_count != frame_end - frame_start + 1 or duration_frames != sample_count - 1:
		return "transport sample count is inconsistent"
	if not is_equal_approx(float(evidence["duration_seconds"]), duration_frames / fps):
		return "transport duration is inconsistent"
	if looping and (int(evidence["period_frames"]) != duration_frames or int(evidence["runtime_sample_count"]) != duration_frames or int(evidence["conceptual_boundary_frame"]) != frame_end):
		return "loop period evidence is inconsistent"
	return ""

static func _all_exact_ints(evidence: Dictionary, fields: Array) -> bool:
	for field in fields:
		if typeof(evidence[field]) != TYPE_INT:
			return false
	return true

static func from_json(evidence: Dictionary, looping: bool) -> Dictionary:
	# Godot's JSON parser represents every JSON number as float. Convert only
	# lexically numeric, finite mathematical integers at the file-adapter boundary;
	# direct GDScript callers remain type-strict through validate().
	var output := evidence.duplicate(true)
	var fields := ["frame_start", "frame_end", "sample_count", "duration_frames"]
	if looping: fields.append_array(["period_frames", "runtime_sample_count", "conceptual_boundary_frame"])
	for field in fields:
		if output.has(field) and typeof(output[field]) == TYPE_FLOAT and is_finite(output[field]) and output[field] == floorf(output[field]):
			output[field] = int(output[field])
	return output

static func normalise_imported(animation: Animation, evidence: Dictionary, looping: bool) -> String:
	var error := validate(evidence, looping)
	if not error.is_empty():
		return error
	if animation.loop_mode != (Animation.LOOP_LINEAR if looping else Animation.LOOP_NONE):
		return "imported loop mode is inconsistent"
	var fps := float(evidence["fps"])
	var offset := 1.0 / fps
	var duration := float(evidence["duration_seconds"])
	if not is_equal_approx(animation.length, duration + offset):
		return "imported duration does not contain exactly one Blender frame-origin offset"
	var endpoint_keys := 0
	for track in animation.get_track_count():
		# Finite clips have no loop semantics. Importers may leave wrapping enabled
		# independently of Animation.loop_mode, so make the policy explicit.
		if not looping:
			animation.track_set_interpolation_loop_wrap(track, false)
		var key_count := animation.track_get_key_count(track)
		if key_count == 0:
			continue
		for key in key_count:
			var time := animation.track_get_key_time(track, key)
			if time > animation.length + TIME_TOLERANCE:
				return "imported track has a key after its declared endpoint"
			if is_equal_approx(time, animation.length):
				endpoint_keys += 1
		# Blender frames are one-based. Godot's Blender import also authors a t=0
		# pre-roll key, then places authored frame 1 at 1/fps. Discard that pre-roll
		# whenever frame 1 survived optimiser reduction; otherwise the t=0 key is
		# already the only representation of the constant opening value.
		var first_authored := animation.track_find_key(track, offset, Animation.FIND_MODE_APPROX)
		if first_authored >= 0 and animation.track_get_key_time(track, 0) <= TIME_TOLERANCE:
			animation.track_remove_key(track, 0)
		for key in animation.track_get_key_count(track):
			var original_time := animation.track_get_key_time(track, key)
			if original_time > TIME_TOLERANCE:
				animation.track_set_key_time(track, key, original_time - offset)
		for key in animation.track_get_key_count(track):
			if animation.track_get_key_time(track, key) > duration + TIME_TOLERANCE:
				return "normalised track retains a key after the finite endpoint"
	if endpoint_keys == 0:
		return "imported animation lacks endpoint sample evidence"
	animation.length = duration
	return ""
