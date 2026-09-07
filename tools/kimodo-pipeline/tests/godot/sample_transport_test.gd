extends SceneTree

const ROOT := ^"Rig:Root"
const HIPS := ^"Rig:Hips"
const EPSILON := 0.0001
const FIXTURE_DIRECTORY := "res://assets/testing/kimodo_pipeline_transport"
var passed := 0
var failed := 0
var transport: Script

func _init() -> void:
	transport = _load_helper("sample_transport.gd")
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(FIXTURE_DIRECTORY)) != OK:
		push_error("Could not create sample-transport test fixture directory")
		quit(1)
		return
	_test_loop_lengths()
	_test_transition()
	_test_track_semantics()
	_test_straight_accumulation()
	_test_pivot_and_arc_accumulation()
	_test_rejections()
	_remove_fixture_directory()
	print("ANIM-002 transport tests passed: %d" % passed)
	quit(1 if failed > 0 else 0)

func _test_loop_lengths() -> void:
	for sample_count in [33, 40]:
		var evidence := _evidence(sample_count, true)
		var animation := _importer_fixture(sample_count, true, Vector3(0.0, 0.0, -float(sample_count - 1) / 30.0), 0.0)
		animation = _normalise_round_trip("loop_%d" % sample_count, animation, evidence)
		_expect(is_equal_approx(animation.length, float(sample_count - 1) / 30.0), "%d-sample loop period" % sample_count)
		var root := animation.find_track(ROOT, Animation.TYPE_POSITION_3D)
		_expect(animation.track_get_key_count(root) == sample_count, "%d-sample loop has no importer-only extra key" % sample_count)

func _test_transition() -> void:
	var evidence := _evidence(29, false)
	var animation := _importer_fixture(29, false, Vector3(0.0, 0.0, -0.45), 0.0)
	animation = _normalise_round_trip("transition", animation, evidence)
	_expect(is_equal_approx(animation.length, 28.0 / 30.0), "finite transition duration is unchanged")
	_expect(animation.loop_mode == Animation.LOOP_NONE, "finite transition remains non-looping")
	for track in animation.get_track_count():
		_expect(not animation.track_get_interpolation_loop_wrap(track), "finite transition has no loop wrapping")
		_expect(is_equal_approx(animation.track_get_key_time(track, animation.track_get_key_count(track) - 1), animation.length), "finite transition retains its authored endpoint")

func _test_track_semantics() -> void:
	var evidence := _evidence(5, false)
	var animation := _semantic_importer_fixture()
	var expected := animation.duplicate(true) as Animation
	_expect(transport.normalise_imported(animation, evidence, false).is_empty(), "representative track fixture normalises")
	_expect(transport.normalise_imported(expected, evidence, false).is_empty(), "semantic baseline normalises")
	var path := FIXTURE_DIRECTORY.path_join("semantic_transition.res")
	_expect(ResourceSaver.save(animation, path) == OK, "semantic fixture saves")
	var loaded := ResourceLoader.load(path, "Animation", ResourceLoader.CACHE_MODE_REPLACE) as Animation
	_expect(_track_semantics_equal(expected, loaded), "all representative track semantics survive save/reload")
	var disabled := loaded.duplicate(true) as Animation
	disabled.track_set_enabled(0, not disabled.track_is_enabled(0))
	_expect(not _track_semantics_equal(expected, disabled), "enabled-state corruption is detected")
	var value_track := _find_track_type(loaded, Animation.TYPE_VALUE)
	var update_corrupt := loaded.duplicate(true) as Animation
	update_corrupt.value_track_set_update_mode(value_track, Animation.UPDATE_CONTINUOUS)
	_expect(not _track_semantics_equal(expected, update_corrupt), "value update-mode corruption is detected")

func _semantic_importer_fixture() -> Animation:
	var animation := _importer_fixture(5, false, Vector3(0.0, 0.0, -0.2), 0.1)
	var specs := [
		[Animation.TYPE_SCALE_3D, ^"Rig:Hips", Vector3.ONE, Vector3(1.1, 0.9, 1.0)],
		[Animation.TYPE_BLEND_SHAPE, ^"Mesh:Smile", 0.0, 0.8],
		[Animation.TYPE_VALUE, ^"Node:visible", true, false],
		[Animation.TYPE_METHOD, ^"Node", {"method": &"probe", "args": [1]}, {"method": &"probe", "args": [2]}],
		[Animation.TYPE_BEZIER, ^"Node:value", 0.0, 1.0],
		[Animation.TYPE_AUDIO, ^"Node", {"stream": null, "start_offset": 0.0, "end_offset": 0.0}, {"stream": null, "start_offset": 0.0, "end_offset": 0.0}],
		[Animation.TYPE_ANIMATION, ^"Player", &"RESET", &"next"],
	]
	for spec in specs:
		var track := animation.add_track(spec[0])
		animation.track_set_path(track, spec[1])
		animation.track_set_enabled(track, track % 2 == 0)
		animation.track_set_interpolation_type(track, Animation.INTERPOLATION_NEAREST if spec[0] in [Animation.TYPE_METHOD, Animation.TYPE_AUDIO, Animation.TYPE_ANIMATION] else Animation.INTERPOLATION_LINEAR)
		animation.track_set_interpolation_loop_wrap(track, true)
		_insert_semantic_key(animation, track, spec[0], 0.0, spec[2], 0.75)
		_insert_semantic_key(animation, track, spec[0], 1.0 / 30.0, spec[2], 0.75)
		_insert_semantic_key(animation, track, spec[0], 5.0 / 30.0, spec[3], 0.5)
	animation.value_track_set_update_mode(_find_track_type(animation, Animation.TYPE_VALUE), Animation.UPDATE_DISCRETE)
	return animation

func _insert_semantic_key(animation: Animation, track: int, type: Animation.TrackType, time: float, value: Variant, transition: float) -> void:
	match type:
		Animation.TYPE_BEZIER: animation.bezier_track_insert_key(track, time, float(value), Vector2(-0.01, 0.0), Vector2(0.01, 0.0))
		Animation.TYPE_AUDIO: animation.audio_track_insert_key(track, time, value.get("stream"), float(value["start_offset"]), float(value["end_offset"]))
		Animation.TYPE_ANIMATION: animation.animation_track_insert_key(track, time, value)
		_: animation.track_insert_key(track, time, value, transition)

func _find_track_type(animation: Animation, type: Animation.TrackType) -> int:
	for track in animation.get_track_count():
		if animation.track_get_type(track) == type: return track
	return -1

func _track_semantics_equal(expected: Animation, actual: Animation) -> bool:
	if expected.get_track_count() != actual.get_track_count(): return false
	for track in expected.get_track_count():
		if expected.track_get_type(track) != actual.track_get_type(track): return false
		if expected.track_get_path(track) != actual.track_get_path(track): return false
		if expected.track_is_enabled(track) != actual.track_is_enabled(track): return false
		if expected.track_get_interpolation_type(track) != actual.track_get_interpolation_type(track): return false
		if expected.track_get_interpolation_loop_wrap(track) != actual.track_get_interpolation_loop_wrap(track): return false
		if expected.track_get_type(track) == Animation.TYPE_VALUE and expected.value_track_get_update_mode(track) != actual.value_track_get_update_mode(track): return false
		if expected.track_get_key_count(track) != actual.track_get_key_count(track): return false
		for key in expected.track_get_key_count(track):
			if not is_equal_approx(expected.track_get_key_time(track, key), actual.track_get_key_time(track, key)): return false
			if not is_equal_approx(expected.track_get_key_transition(track, key), actual.track_get_key_transition(track, key)): return false
			if expected.track_get_key_value(track, key) != actual.track_get_key_value(track, key): return false
	return true

func _test_straight_accumulation() -> void:
	for signed_distance in [-0.9286, 0.9286]:
		var animation := _importer_fixture(33, true, Vector3(0.0, 0.0, signed_distance), 0.0)
		_expect(transport.normalise_imported(animation, _evidence(33, true), true).is_empty(), "straight fixture normalises")
		var endpoint := _root_position(animation, animation.length)
		var third := _accumulated(animation, animation.length * 3.0)
		_expect(third.position.distance_to(Vector2(endpoint.x, endpoint.z) * 3.0) < EPSILON, "WF/WB signed Root accumulates for three cycles")
		var before: Vector2 = _accumulated(animation, animation.length - 0.0001)["position"]
		var after: Vector2 = _accumulated(animation, animation.length + 1.0 / 30.0)["position"]
		_expect(after.distance_to(before) > 0.0001 and signf(after.y - before.y) == signf(signed_distance), "wrap has no reset, pause, slide, or duplicate delta")
		var hips := animation.find_track(HIPS, Animation.TYPE_POSITION_3D)
		var opening := animation.position_track_interpolate(hips, 0.0)
		var wrapped := animation.position_track_interpolate(hips, 1.0 / 30.0)
		_expect(opening.distance_to(wrapped) > 0.0001, "one sample after wrap advances periodic pose")

func _test_pivot_and_arc_accumulation() -> void:
	var pivot := _importer_fixture(33, true, Vector3.ZERO, PI / 4.0)
	_expect(transport.normalise_imported(pivot, _evidence(33, true), true).is_empty(), "pivot fixture normalises")
	_expect(absf(_accumulated(pivot, pivot.length * 3.0).yaw - 3.0 * PI / 4.0) < EPSILON, "pivot yaw accumulates for three cycles")
	var arc := _importer_fixture(33, true, Vector3(1.0, 0.0, 0.0), 2.0 * PI / 3.0)
	_expect(transport.normalise_imported(arc, _evidence(33, true), true).is_empty(), "arc fixture normalises")
	var accumulated := _accumulated(arc, arc.length * 3.0)
	_expect(accumulated.position.distance_to(Vector2.ZERO) < EPSILON and absf(accumulated.yaw - TAU) < EPSILON, "arc composes in local SE(2), not repeated world chords")

func _test_rejections() -> void:
	var missing := _evidence(33, true); missing.erase("period_frames")
	_expect(not transport.validate(missing, true).is_empty(), "missing period is rejected")
	var corrupt := _evidence(33, true); corrupt["period_frames"] = 33
	_expect(not transport.validate(corrupt, true).is_empty(), "off-by-one period is rejected")
	var imported := _importer_fixture(33, true, Vector3(0.0, 0.0, -1.0), 0.0); imported.length += 1.0 / 30.0
	_expect(not transport.normalise_imported(imported, _evidence(33, true), true).is_empty(), "extra imported sample is rejected")
	var malformed := _evidence(33, true); malformed["sample_count"] = 33.0
	_expect(not transport.validate(malformed, true).is_empty(), "fractional/coercible integral transport field is rejected")
	malformed = _evidence(33, true); malformed["fps"] = "30"
	_expect(not transport.validate(malformed, true).is_empty(), "string fps is rejected")
	malformed = _evidence(33, true); malformed["fps"] = true
	_expect(not transport.validate(malformed, true).is_empty(), "boolean fps is rejected")
	malformed = _evidence(33, true); malformed["duration_seconds"] = INF
	_expect(not transport.validate(malformed, true).is_empty(), "non-finite duration is rejected")

func _normalise_round_trip(name: String, animation: Animation, evidence: Dictionary) -> Animation:
	var path := FIXTURE_DIRECTORY.path_join("%s.res" % name)
	_expect(ResourceSaver.save(animation, path) == OK, "%s fixture saves through Godot API" % name)
	var loaded := ResourceLoader.load(path, "Animation", ResourceLoader.CACHE_MODE_REPLACE) as Animation
	var error: String = transport.normalise_imported(loaded, evidence, bool(evidence["boundary"] == "loop"))
	_expect(error.is_empty(), "%s fixture normalises: %s" % [name, error])
	_expect(ResourceSaver.save(loaded, path) == OK, "%s normalised fixture saves" % name)
	return ResourceLoader.load(path, "Animation", ResourceLoader.CACHE_MODE_REPLACE) as Animation

func _importer_fixture(sample_count: int, looping: bool, endpoint: Vector3, endpoint_yaw: float) -> Animation:
	var fps := 30.0
	var duration := float(sample_count - 1) / fps
	var animation := Animation.new()
	animation.length = duration + 1.0 / fps
	animation.step = 1.0 / fps
	animation.loop_mode = Animation.LOOP_LINEAR if looping else Animation.LOOP_NONE
	var root_position := animation.add_track(Animation.TYPE_POSITION_3D)
	animation.track_set_path(root_position, ROOT)
	var root_rotation := animation.add_track(Animation.TYPE_ROTATION_3D)
	animation.track_set_path(root_rotation, ROOT)
	var hips := animation.add_track(Animation.TYPE_POSITION_3D)
	animation.track_set_path(hips, HIPS)
	for track in [root_position, root_rotation, hips]: animation.track_set_interpolation_loop_wrap(track, looping)
	animation.position_track_insert_key(root_position, 0.0, Vector3.ZERO)
	animation.rotation_track_insert_key(root_rotation, 0.0, Quaternion.IDENTITY)
	animation.position_track_insert_key(hips, 0.0, Vector3.ZERO)
	for frame in range(1, sample_count + 1):
		var phase := float(frame - 1) / float(sample_count - 1)
		var time := float(frame) / fps
		animation.position_track_insert_key(root_position, time, endpoint * phase)
		animation.rotation_track_insert_key(root_rotation, time, Quaternion(Vector3.UP, endpoint_yaw * phase))
		animation.position_track_insert_key(hips, time, Vector3(sin(phase * TAU), 0.0, 0.0))
	return animation

func _evidence(sample_count: int, looping: bool) -> Dictionary:
	var evidence := {"contract": "anim002_sample_transport_v1", "boundary": "loop" if looping else "transition", "fps": 30.0, "frame_start": 1, "frame_end": sample_count, "sample_count": sample_count, "duration_frames": sample_count - 1, "duration_seconds": float(sample_count - 1) / 30.0}
	if looping:
		evidence.merge({"period_frames": sample_count - 1, "runtime_sample_count": sample_count - 1, "conceptual_boundary_frame": sample_count})
	return evidence

func _root_position(animation: Animation, time: float) -> Vector3:
	return animation.position_track_interpolate(animation.find_track(ROOT, Animation.TYPE_POSITION_3D), time)

func _root_yaw(animation: Animation, time: float) -> float:
	return animation.rotation_track_interpolate(animation.find_track(ROOT, Animation.TYPE_ROTATION_3D), time).get_euler().y

func _accumulated(animation: Animation, time: float) -> Dictionary:
	var cycles := int(floor(time / animation.length))
	var phase := fposmod(time, animation.length)
	if is_equal_approx(phase, 0.0) and time > 0.0:
		phase = animation.length
		cycles -= 1
	var endpoint := _root_position(animation, animation.length)
	var endpoint_planar := Vector2(endpoint.x, endpoint.z)
	var endpoint_yaw := _root_yaw(animation, animation.length)
	var position := Vector2.ZERO
	var yaw := 0.0
	for cycle in cycles:
		position += endpoint_planar.rotated(yaw)
		yaw += endpoint_yaw
	var local := _root_position(animation, phase)
	position += Vector2(local.x, local.z).rotated(yaw)
	yaw += _root_yaw(animation, phase)
	return {"position": position, "yaw": yaw}

func _expect(condition: bool, message: String) -> void:
	if not condition:
		push_error(message)
		failed += 1
		return
	passed += 1

func _load_helper(filename: String) -> Script:
	var test_path: String = get_script().resource_path
	var helper_path: String = test_path.get_base_dir().get_base_dir().get_base_dir().path_join("godot/lib").path_join(filename).simplify_path()
	return load(helper_path) as Script

func _remove_fixture_directory() -> void:
	var directory := DirAccess.open(FIXTURE_DIRECTORY)
	if directory == null:
		return
	for file in directory.get_files():
		directory.remove(file)
	DirAccess.remove_absolute(ProjectSettings.globalize_path(FIXTURE_DIRECTORY))
