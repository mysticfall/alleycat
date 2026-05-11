extends SceneTree

const TEST_SCENE_PATH := "res://tests/body/eyes/eyes_visual_test.tscn"
const ANIMATION_TREE_SCENE_PATH := "res://assets/characters/reference/female/animation_tree.tscn"
const EYES_BEHAVIOUR_SCRIPT_PATH := "res://src/Body/Eyes/EyesBehaviour.cs"
const OUTPUT_ROOT := "BODY-004/eyes_visual"

const HORIZONTAL_BLEND_PARAM := "parameters/EyesHorizontalLookBlend/blend_amount"
const VERTICAL_BLEND_PARAM := "parameters/EyesVerticalLookBlend/blend_amount"
const HORIZONTAL_SEEK_PARAM := "parameters/EyesHorizontalLookSeek/seek_request"
const VERTICAL_SEEK_PARAM := "parameters/EyesVerticalLookSeek/seek_request"
const BLINK_REQUEST_PARAM := "parameters/EyesBlinkOneShot/request"
const BLINK_RUNTIME_DURATION_SECONDS := 0.3
const MAX_HORIZONTAL_ANGLE_RADIANS := deg_to_rad(35.0)
const MAX_VERTICAL_ANGLE_RADIANS := deg_to_rad(25.0)
const STRONG_HORIZONTAL_ANGLE_RADIANS := deg_to_rad(70.0)
const PARAMETER_TOLERANCE := 0.025
const ONE_SHOT_REQUEST_ABORT := 2

const SCENARIOS := [
	{"name": "01_neutral", "local_target": Vector3(0.0, 0.0, -1.0), "blink": false, "horizontal_seek": 0.5, "vertical_seek": 0.5},
	{"name": "02_look_left", "local_target": Vector3(-tan(STRONG_HORIZONTAL_ANGLE_RADIANS), 0.0, -1.0), "blink": false, "horizontal_seek": 1.0, "vertical_seek": 0.5},
	{"name": "03_look_right", "local_target": Vector3(tan(STRONG_HORIZONTAL_ANGLE_RADIANS), 0.0, -1.0), "blink": false, "horizontal_seek": 0.0, "vertical_seek": 0.5},
	{"name": "04_up", "local_target": Vector3(0.0, tan(MAX_VERTICAL_ANGLE_RADIANS), -1.0), "blink": false, "horizontal_seek": 0.5, "vertical_seek": 0.0},
	{"name": "05_down", "local_target": Vector3(0.0, -tan(MAX_VERTICAL_ANGLE_RADIANS), -1.0), "blink": false, "horizontal_seek": 0.5, "vertical_seek": 1.0},
	{"name": "06_blink_closed", "local_target": Vector3(0.0, 0.0, -1.0), "blink": true, "horizontal_seek": 0.5, "vertical_seek": 0.5},
]


func _init() -> void:
	await _run()


func _run() -> void:
	if DisplayServer.get_name() == "headless":
		SceneUtils.fatal_error_and_quit("BODY-004 eyes runner must not run headless because screenshots would be invalid")
		return

	var photobooth: Photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("BODY-004 eyes runner: failed to load test scene: %s" % TEST_SCENE_PATH)
		return

	root.add_child(photobooth)
	await SceneUtils.wait_frames(self, 2)
	_validate_camera_rig(photobooth)

	var female: Node3D = SceneUtils.require_node(photobooth, ^"Subject/Female") as Node3D
	var viewpoint: Node3D = SceneUtils.require_node(female, ^"Female_export/GeneralSkeleton/Head/Viewpoint") as Node3D
	if female == null or viewpoint == null:
		SceneUtils.fatal_error_and_quit("BODY-004 eyes runner: failed to resolve female subject or viewpoint")
		return

	var animation_tree: AnimationTree = _attach_animation_tree(female)
	if animation_tree == null:
		return

	var look_target: Marker3D = Marker3D.new()
	look_target.name = "EyesLookTarget"
	photobooth.add_child(look_target)

	var eyes_behaviour: Node = _attach_eyes_behaviour(animation_tree, viewpoint, look_target)
	if eyes_behaviour == null:
		return

	var marker: DebugMarker = photobooth.add_marker("EyesLookTargetMarker", "Eyes Target")
	marker.scale = Vector3.ONE * 3.0
	marker.visible = true
	_set_target_from_local(viewpoint, look_target, marker, Vector3(0.0, 0.0, -1.0))
	_validate_target_marker_orientation(viewpoint, look_target)
	await SceneUtils.wait_frames(self, 4)
	await photobooth.get_camera_rig("CameraRig").capture_screenshot("%s/framing/face_camera_target_marker.jpg" % OUTPUT_ROOT)
	marker.visible = false

	for scenario_index: int in SCENARIOS.size():
		var scenario: Dictionary = SCENARIOS[scenario_index] as Dictionary
		_apply_scenario(viewpoint, look_target, marker, animation_tree, eyes_behaviour, scenario)
		if scenario["blink"] as bool:
			await SceneUtils.wait_seconds(self, BLINK_RUNTIME_DURATION_SECONDS * 0.5)
		else:
			await SceneUtils.wait_frames(self, 8)
			await SceneUtils.wait_seconds(self, 0.05)
		_assert_scenario_parameters(animation_tree, scenario)
		await photobooth.capture_screenshots("%s/scenarios/%s.jpg" % [OUTPUT_ROOT, scenario["name"]])

	print("BODY004_EYES_VISUAL_GATE_PASS artefact_root=%s" % OUTPUT_ROOT)
	quit(0)


func _validate_camera_rig(photobooth: Photobooth) -> void:
	var camera_rig: CameraRig = photobooth.get_camera_rig("CameraRig")
	if camera_rig == null:
		SceneUtils.fatal_error_and_quit("BODY-004 eyes runner: missing face CameraRig")
		return

	if camera_rig.camera == null:
		SceneUtils.fatal_error_and_quit("BODY-004 eyes runner: face CameraRig has no camera")
		return

	print("BODY004_EYES_CAMERA rig=CameraRig camera=%s projection=%s" % [camera_rig.camera.name, camera_rig.camera.projection])


func _validate_target_marker_orientation(viewpoint: Node3D, look_target: Node3D) -> void:
	var local_target: Vector3 = viewpoint.global_transform.affine_inverse() * look_target.global_position
	if local_target.z >= 0.0:
		SceneUtils.fatal_error_and_quit("BODY-004 eyes runner: neutral look target is not in front of the face")
		return

	print("BODY004_EYES_MARKER neutral_local=(%.4f, %.4f, %.4f)" % [local_target.x, local_target.y, local_target.z])


func _assert_scenario_parameters(animation_tree: AnimationTree, scenario: Dictionary) -> void:
	var horizontal_seek: float = animation_tree.get(HORIZONTAL_SEEK_PARAM)
	var vertical_seek: float = animation_tree.get(VERTICAL_SEEK_PARAM)
	var expected_horizontal: float = scenario["horizontal_seek"] as float
	var expected_vertical: float = scenario["vertical_seek"] as float
	if not (scenario["blink"] as bool) and absf(horizontal_seek - expected_horizontal) > PARAMETER_TOLERANCE:
		SceneUtils.fatal_error_and_quit("BODY-004 eyes runner: %s horizontal seek %.4f did not match %.4f" % [scenario["name"], horizontal_seek, expected_horizontal])
		return
	if not (scenario["blink"] as bool) and absf(vertical_seek - expected_vertical) > PARAMETER_TOLERANCE:
		SceneUtils.fatal_error_and_quit("BODY-004 eyes runner: %s vertical seek %.4f did not match %.4f" % [scenario["name"], vertical_seek, expected_vertical])
		return
	print(
		"BODY004_EYES_SCENARIO name=%s horizontal_seek=%.4f vertical_seek=%.4f" % [
			scenario["name"],
			horizontal_seek,
			vertical_seek,
		]
	)


func _attach_animation_tree(female: Node3D) -> AnimationTree:
	var tree_scene: PackedScene = SceneUtils.load_scene(ANIMATION_TREE_SCENE_PATH)
	if tree_scene == null:
		return null

	var animation_tree: AnimationTree = tree_scene.instantiate() as AnimationTree
	if animation_tree == null:
		SceneUtils.fatal_error_and_quit("BODY-004 eyes runner: AnimationTree scene root must be AnimationTree")
		return null

	female.add_child(animation_tree)
	animation_tree.active = true
	return animation_tree


func _attach_eyes_behaviour(animation_tree: AnimationTree, viewpoint: Node3D, look_target: Node3D) -> Node:
	var eyes_script: Script = load(EYES_BEHAVIOUR_SCRIPT_PATH) as Script
	if eyes_script == null:
		SceneUtils.fatal_error_and_quit("BODY-004 eyes runner: failed to load EyesBehaviour script")
		return null

	var eyes_behaviour := Node.new()
	eyes_behaviour.name = "EyesBehaviour"
	eyes_behaviour.set_script(eyes_script)
	eyes_behaviour.set("AnimationTree", animation_tree)
	eyes_behaviour.set("EyeOrigin", viewpoint)
	eyes_behaviour.set("LookSmoothingTime", 0.0)
	eyes_behaviour.set("MinimumBlinkInterval", 99.0)
	eyes_behaviour.set("MaximumBlinkInterval", 99.0)
	eyes_behaviour.set("BlinkDuration", BLINK_RUNTIME_DURATION_SECONDS)
	eyes_behaviour.set("LookTarget", look_target)
	animation_tree.add_child(eyes_behaviour)
	return eyes_behaviour


func _apply_scenario(viewpoint: Node3D, look_target: Node3D, marker: Node3D, animation_tree: AnimationTree, eyes_behaviour: Node, scenario: Dictionary) -> void:
	eyes_behaviour.set_process(true)
	_set_target_from_local(viewpoint, look_target, marker, scenario["local_target"] as Vector3)
	if scenario["blink"] as bool:
		eyes_behaviour.call("TriggerBlink")
	else:
		animation_tree.set(BLINK_REQUEST_PARAM, ONE_SHOT_REQUEST_ABORT)

	# Keep these blends enabled for the static target screenshots even before the first process sample.
	animation_tree.set(HORIZONTAL_BLEND_PARAM, 1.0)
	animation_tree.set(VERTICAL_BLEND_PARAM, 1.0)


func _set_target_from_local(viewpoint: Node3D, look_target: Node3D, marker: Node3D, local_target: Vector3) -> void:
	var target_position: Vector3 = viewpoint.global_transform * local_target
	look_target.global_position = target_position
	marker.global_position = target_position
