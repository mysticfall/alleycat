extends SceneTree

const MIRROR_ROOM_SCENE_PATH := "res://assets/testing/mirror_room/mirror_room.tscn"
const OUTPUT_ROOT := "body-004-mirror-room-eye-follow"

const HORIZONTAL_BLEND_PARAM := "parameters/EyesHorizontalLookBlend/blend_amount"
const VERTICAL_BLEND_PARAM := "parameters/EyesVerticalLookBlend/blend_amount"
const HORIZONTAL_SEEK_PARAM := "parameters/EyesHorizontalLookSeek/seek_request"
const VERTICAL_SEEK_PARAM := "parameters/EyesVerticalLookSeek/seek_request"
const BLINK_REQUEST_PARAM := "parameters/EyesBlinkOneShot/request"

const ONE_SHOT_REQUEST_ABORT := 2
const BLINK_CAPTURE_DELAY_SECONDS := 0.16
const BLINK_DURATION_SECONDS := 0.6
const PARAMETER_TOLERANCE := 0.025

const SCENARIOS := [
	{"name": "01_open_neutral", "local_target": Vector3(0.0, 0.0, -1.0), "horizontal_seek": 0.5, "vertical_seek": 0.5, "blink": false},
	{"name": "02_blink_mid_closed", "local_target": Vector3(0.0, 0.0, -1.0), "horizontal_seek": 0.5, "vertical_seek": 0.5, "blink": true},
]


func _init() -> void:
	await _run()


func _run() -> void:
	if DisplayServer.get_name() == "headless":
		SceneUtils.fatal_error_and_quit("BODY-004 mirror-room eye-follow visual gate must not run headless")
		return

	var mirror_room: Node3D = SceneUtils.instantiate_scene(MIRROR_ROOM_SCENE_PATH) as Node3D
	if mirror_room == null:
		SceneUtils.fatal_error_and_quit("BODY-004 mirror-room eye-follow visual gate failed to load %s" % MIRROR_ROOM_SCENE_PATH)
		return

	root.add_child(mirror_room)
	await SceneUtils.wait_frames(self, 12)
	_hide_capture_occluders(mirror_room)

	var eye_subjects: Array[Dictionary] = _find_eye_subjects(mirror_room)
	if eye_subjects.is_empty():
		SceneUtils.fatal_error_and_quit("BODY-004 mirror-room eye-follow visual gate failed to discover any generated-character eye subjects")
		return

	for subject_index: int in eye_subjects.size():
		var subject: Dictionary = eye_subjects[subject_index] as Dictionary
		await _capture_subject(mirror_room, subject)

	print("BODY004_MIRROR_ROOM_EYE_FOLLOW_VISUAL_GATE_PASS artefact_root=game/temp/%s subjects=%d" % [OUTPUT_ROOT, eye_subjects.size()])
	quit(0)


func _hide_capture_occluders(mirror_room: Node) -> void:
	var items: Node3D = mirror_room.get_node_or_null(^"Items") as Node3D
	if items != null:
		items.visible = false


func _find_eye_subjects(mirror_room: Node) -> Array[Dictionary]:
	var subjects: Array[Dictionary] = []
	var candidate_nodes: Array[Node] = mirror_room.find_children("*", "Node", true, false)
	for candidate: Node in candidate_nodes:
		if not _has_property(candidate, "AnimationTree") or not _has_property(candidate, "EyeOrigin"):
			continue

		var animation_tree: AnimationTree = candidate.get("AnimationTree") as AnimationTree
		var eye_origin: Node3D = candidate.get("EyeOrigin") as Node3D
		if animation_tree == null or eye_origin == null:
			continue

		subjects.append({
			"label": _resolve_subject_label(mirror_room, candidate),
			"eyes_behaviour": candidate,
			"animation_tree": animation_tree,
			"eye_origin": eye_origin,
		})

	return subjects


func _has_property(node: Node, property_name: StringName) -> bool:
	for property: Dictionary in node.get_property_list():
		if property.get("name", StringName()) == property_name:
			return true

	return false


func _resolve_subject_label(mirror_room: Node, eyes_behaviour: Node) -> String:
	var actors: Node = mirror_room.get_node_or_null(^"Actors")
	if actors != null:
		var current: Node = eyes_behaviour
		while current != null and current.get_parent() != actors:
			current = current.get_parent()
		if current != null and current.get_parent() == actors:
			return SceneUtils.to_safe_file_component(str(current.name))

	return SceneUtils.to_safe_file_component(str(mirror_room.get_path_to(eyes_behaviour))).left(80)


func _capture_subject(mirror_room: Node3D, subject: Dictionary) -> void:
	var label: String = subject["label"] as String
	var eyes_behaviour: Node = subject["eyes_behaviour"] as Node
	var animation_tree: AnimationTree = subject["animation_tree"] as AnimationTree
	var eye_origin: Node3D = subject["eye_origin"] as Node3D
	if eyes_behaviour == null or animation_tree == null or eye_origin == null:
		SceneUtils.fatal_error_and_quit("BODY-004 mirror-room eye-follow visual gate found incomplete subject '%s'" % label)
		return

	var look_target := Marker3D.new()
	look_target.name = "%sBlinkVisualLookTarget" % label
	look_target.top_level = true
	mirror_room.add_child(look_target)

	_configure_eye_behaviour(eyes_behaviour, look_target)
	var camera := _create_face_camera(mirror_room, eye_origin, label)
	await SceneUtils.wait_frames(self, 4)

	_validate_camera(camera, eye_origin)
	await SceneUtils.capture_screenshot(self, "%s/framing/%s_face_camera.jpg" % [OUTPUT_ROOT, label])

	for scenario_index: int in SCENARIOS.size():
		var scenario: Dictionary = SCENARIOS[scenario_index] as Dictionary
		await _apply_scenario(look_target, eye_origin, eyes_behaviour, animation_tree, scenario)
		_assert_scenario_parameters(animation_tree, scenario)
		await SceneUtils.capture_screenshot(self, "%s/scenarios/%s_%s.jpg" % [OUTPUT_ROOT, label, scenario["name"]])

	camera.queue_free()
	look_target.queue_free()


func _configure_eye_behaviour(eyes_behaviour: Node, look_target: Node3D) -> void:
	eyes_behaviour.set("LookTarget", look_target)
	eyes_behaviour.set("LookSmoothingTime", 0.0)
	eyes_behaviour.set("MinimumBlinkInterval", 99.0)
	eyes_behaviour.set("MaximumBlinkInterval", 99.0)
	eyes_behaviour.set("BlinkDuration", BLINK_DURATION_SECONDS)
	eyes_behaviour.set("SaccadeAmplitude", 0.0)


func _create_face_camera(parent: Node, eye_origin: Node3D, label: String) -> Camera3D:
	var camera := Camera3D.new()
	camera.name = "BODY004MirrorRoomEyeFollowFaceCamera_%s" % label
	camera.projection = Camera3D.PROJECTION_PERSPECTIVE
	camera.fov = 18.0
	camera.near = 0.01
	camera.far = 10.0
	parent.add_child(camera)

	var target_position: Vector3 = eye_origin.global_position + Vector3(0.0, -0.025, 0.0)
	var camera_position: Vector3 = eye_origin.global_transform * Vector3(0.0, 0.0, -0.65)
	camera.global_position = camera_position
	camera.look_at(target_position, Vector3.UP)
	camera.make_current()
	return camera


func _validate_camera(camera: Camera3D, eye_origin: Node3D) -> void:
	var eye_to_camera_local: Vector3 = eye_origin.global_transform.affine_inverse() * camera.global_position
	if eye_to_camera_local.z >= 0.0:
		SceneUtils.fatal_error_and_quit("BODY-004 mirror-room eye-follow visual gate camera is not in front of the female face")
		return

	print(
		"BODY004_MIRROR_ROOM_EYE_CAMERA local_position=(%.4f, %.4f, %.4f) fov=%.2f" % [
			eye_to_camera_local.x,
			eye_to_camera_local.y,
			eye_to_camera_local.z,
			camera.fov,
		]
	)


func _apply_scenario(look_target: Node3D, eye_origin: Node3D, eyes_behaviour: Node, animation_tree: AnimationTree, scenario: Dictionary) -> void:
	var local_target: Vector3 = scenario["local_target"] as Vector3
	look_target.global_position = eye_origin.global_transform * local_target
	if local_target.z >= 0.0:
		SceneUtils.fatal_error_and_quit("BODY-004 mirror-room eye-follow visual gate scenario %s target is not in front of the face" % scenario["name"])
		return

	animation_tree.set(HORIZONTAL_BLEND_PARAM, 1.0)
	animation_tree.set(VERTICAL_BLEND_PARAM, 1.0)
	animation_tree.set(HORIZONTAL_SEEK_PARAM, scenario["horizontal_seek"] as float)
	animation_tree.set(VERTICAL_SEEK_PARAM, scenario["vertical_seek"] as float)
	if scenario["blink"] as bool:
		eyes_behaviour.call("TriggerBlink")
		await SceneUtils.wait_seconds(self, BLINK_CAPTURE_DELAY_SECONDS)
	else:
		animation_tree.set(BLINK_REQUEST_PARAM, ONE_SHOT_REQUEST_ABORT)
		await SceneUtils.wait_frames(self, 6)

	animation_tree.set(HORIZONTAL_SEEK_PARAM, scenario["horizontal_seek"] as float)
	animation_tree.set(VERTICAL_SEEK_PARAM, scenario["vertical_seek"] as float)


func _assert_scenario_parameters(animation_tree: AnimationTree, scenario: Dictionary) -> void:
	var horizontal_blend: float = animation_tree.get(HORIZONTAL_BLEND_PARAM)
	var vertical_blend: float = animation_tree.get(VERTICAL_BLEND_PARAM)
	var horizontal_seek: float = animation_tree.get(HORIZONTAL_SEEK_PARAM)
	var vertical_seek: float = animation_tree.get(VERTICAL_SEEK_PARAM)

	if absf(horizontal_blend - 1.0) > PARAMETER_TOLERANCE or absf(vertical_blend - 1.0) > PARAMETER_TOLERANCE:
		SceneUtils.fatal_error_and_quit("BODY-004 mirror-room eye-follow visual gate %s look blends disabled h=%.4f v=%.4f" % [scenario["name"], horizontal_blend, vertical_blend])
		return
	if not (scenario["blink"] as bool) and absf(horizontal_seek - (scenario["horizontal_seek"] as float)) > PARAMETER_TOLERANCE:
		SceneUtils.fatal_error_and_quit("BODY-004 mirror-room eye-follow visual gate %s horizontal seek %.4f did not match %.4f" % [scenario["name"], horizontal_seek, scenario["horizontal_seek"]])
		return
	if not (scenario["blink"] as bool) and absf(vertical_seek - (scenario["vertical_seek"] as float)) > PARAMETER_TOLERANCE:
		SceneUtils.fatal_error_and_quit("BODY-004 mirror-room eye-follow visual gate %s vertical seek %.4f did not match %.4f" % [scenario["name"], vertical_seek, scenario["vertical_seek"]])
		return

	print(
		"BODY004_MIRROR_ROOM_EYE_SCENARIO name=%s horizontal_seek=%.4f vertical_seek=%.4f" % [
			scenario["name"],
			horizontal_seek,
			vertical_seek,
		]
	)
