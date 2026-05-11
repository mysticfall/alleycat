extends SceneTree

const MIRROR_ROOM_SCENE_PATH := "res://assets/testing/mirror_room/mirror_room.tscn"
const OUTPUT_ROOT := "body-004-mirror-room-eye-follow"

const HORIZONTAL_BLEND_PARAM := "parameters/EyesHorizontalLookBlend/blend_amount"
const VERTICAL_BLEND_PARAM := "parameters/EyesVerticalLookBlend/blend_amount"
const HORIZONTAL_SEEK_PARAM := "parameters/EyesHorizontalLookSeek/seek_request"
const VERTICAL_SEEK_PARAM := "parameters/EyesVerticalLookSeek/seek_request"
const BLINK_REQUEST_PARAM := "parameters/EyesBlinkOneShot/request"

const PLAYER_VIEWPOINT_PATH := ^"Actors/Player/Female_export/GeneralSkeleton/Head/Viewpoint"
const FEMALE_EYES_PATH := ^"Actors/Female/EyesBehaviour"
const FEMALE_ANIMATION_TREE_PATH := ^"Actors/Female/AnimationTree"

const ONE_SHOT_REQUEST_ABORT := 2
const BLINK_CAPTURE_DELAY_SECONDS := 0.08
const BLINK_DURATION_SECONDS := 0.6
const PARAMETER_TOLERANCE := 0.025

const SCENARIOS := [
	{"name": "01_neutral_current_player_viewpoint", "local_target": Vector3(0.0, 0.0, -1.0), "horizontal_seek": 0.5, "vertical_seek": 0.5, "blink": false},
	{"name": "02_player_viewpoint_character_left", "local_target": Vector3(-2.75, 0.0, -1.0), "horizontal_seek": 1.0, "vertical_seek": 0.5, "blink": false},
	{"name": "03_player_viewpoint_character_right", "local_target": Vector3(2.75, 0.0, -1.0), "horizontal_seek": 0.0, "vertical_seek": 0.5, "blink": false},
	{"name": "04_blink_closed_neutral", "local_target": Vector3(0.0, 0.0, -1.0), "horizontal_seek": 0.5, "vertical_seek": 0.5, "blink": true},
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
	await SceneUtils.wait_frames(self, 4)

	var player_viewpoint: Node3D = SceneUtils.require_node(mirror_room, PLAYER_VIEWPOINT_PATH) as Node3D
	var eyes_behaviour: Node = SceneUtils.require_node(mirror_room, FEMALE_EYES_PATH)
	var animation_tree: AnimationTree = SceneUtils.require_node(mirror_room, FEMALE_ANIMATION_TREE_PATH) as AnimationTree
	var eye_origin: Node3D = eyes_behaviour.get("EyeOrigin") as Node3D
	if player_viewpoint == null or eyes_behaviour == null or animation_tree == null or eye_origin == null:
		SceneUtils.fatal_error_and_quit("BODY-004 mirror-room eye-follow visual gate failed to resolve required eye nodes")
		return

	_configure_eye_behaviour(eyes_behaviour, player_viewpoint)
	var camera := _create_face_camera(mirror_room, eye_origin)
	await SceneUtils.wait_frames(self, 4)

	_validate_camera(camera, eye_origin)
	await SceneUtils.capture_screenshot(self, "%s/framing/00_face_camera.jpg" % OUTPUT_ROOT)

	for scenario_index: int in SCENARIOS.size():
		var scenario: Dictionary = SCENARIOS[scenario_index] as Dictionary
		await _apply_scenario(player_viewpoint, eye_origin, eyes_behaviour, animation_tree, scenario)
		_assert_scenario_parameters(animation_tree, scenario)
		await SceneUtils.capture_screenshot(self, "%s/scenarios/%s.jpg" % [OUTPUT_ROOT, scenario["name"]])

	print("BODY004_MIRROR_ROOM_EYE_FOLLOW_VISUAL_GATE_PASS artefact_root=temp/%s" % OUTPUT_ROOT)
	quit(0)


func _configure_eye_behaviour(eyes_behaviour: Node, player_viewpoint: Node3D) -> void:
	eyes_behaviour.set("LookTarget", player_viewpoint)
	eyes_behaviour.set("LookSmoothingTime", 0.0)
	eyes_behaviour.set("MinimumBlinkInterval", 99.0)
	eyes_behaviour.set("MaximumBlinkInterval", 99.0)
	eyes_behaviour.set("BlinkDuration", BLINK_DURATION_SECONDS)
	player_viewpoint.top_level = true


func _create_face_camera(parent: Node, eye_origin: Node3D) -> Camera3D:
	var camera := Camera3D.new()
	camera.name = "BODY004MirrorRoomEyeFollowFaceCamera"
	camera.projection = Camera3D.PROJECTION_PERSPECTIVE
	camera.fov = 9.0
	camera.near = 0.01
	camera.far = 10.0
	parent.add_child(camera)

	var target_position: Vector3 = eye_origin.global_position + Vector3(0.0, 0.005, 0.0)
	var camera_position: Vector3 = eye_origin.global_transform * Vector3(0.0, 0.0, -0.52)
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


func _apply_scenario(player_viewpoint: Node3D, eye_origin: Node3D, eyes_behaviour: Node, animation_tree: AnimationTree, scenario: Dictionary) -> void:
	var local_target: Vector3 = scenario["local_target"] as Vector3
	player_viewpoint.global_position = eye_origin.global_transform * local_target
	if local_target.z >= 0.0:
		SceneUtils.fatal_error_and_quit("BODY-004 mirror-room eye-follow visual gate scenario %s target is not in front of the face" % scenario["name"])
		return

	animation_tree.set(HORIZONTAL_BLEND_PARAM, 1.0)
	animation_tree.set(VERTICAL_BLEND_PARAM, 1.0)
	if scenario["blink"] as bool:
		eyes_behaviour.call("TriggerBlink")
		await SceneUtils.wait_seconds(self, BLINK_CAPTURE_DELAY_SECONDS)
	else:
		animation_tree.set(BLINK_REQUEST_PARAM, ONE_SHOT_REQUEST_ABORT)
		await SceneUtils.wait_frames(self, 6)


func _assert_scenario_parameters(animation_tree: AnimationTree, scenario: Dictionary) -> void:
	var horizontal_blend: float = animation_tree.get(HORIZONTAL_BLEND_PARAM)
	var vertical_blend: float = animation_tree.get(VERTICAL_BLEND_PARAM)
	var horizontal_seek: float = animation_tree.get(HORIZONTAL_SEEK_PARAM)
	var vertical_seek: float = animation_tree.get(VERTICAL_SEEK_PARAM)

	if absf(horizontal_blend - 1.0) > PARAMETER_TOLERANCE or absf(vertical_blend - 1.0) > PARAMETER_TOLERANCE:
		SceneUtils.fatal_error_and_quit("BODY-004 mirror-room eye-follow visual gate %s look blends disabled h=%.4f v=%.4f" % [scenario["name"], horizontal_blend, vertical_blend])
		return
	if absf(horizontal_seek - (scenario["horizontal_seek"] as float)) > PARAMETER_TOLERANCE:
		SceneUtils.fatal_error_and_quit("BODY-004 mirror-room eye-follow visual gate %s horizontal seek %.4f did not match %.4f" % [scenario["name"], horizontal_seek, scenario["horizontal_seek"]])
		return
	if absf(vertical_seek - (scenario["vertical_seek"] as float)) > PARAMETER_TOLERANCE:
		SceneUtils.fatal_error_and_quit("BODY-004 mirror-room eye-follow visual gate %s vertical seek %.4f did not match %.4f" % [scenario["name"], vertical_seek, scenario["vertical_seek"]])
		return

	print(
		"BODY004_MIRROR_ROOM_EYE_SCENARIO name=%s horizontal_seek=%.4f vertical_seek=%.4f" % [
			scenario["name"],
			horizontal_seek,
			vertical_seek,
		]
	)
