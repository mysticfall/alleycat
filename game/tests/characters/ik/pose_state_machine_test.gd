extends SceneTree

const TEST_SCENE_PATH := "res://tests/characters/ik/pose_state_machine_test.tscn"
const OUTPUT_ROOT := "char001/pose_state_machine"

const REQUIRED_CAMERAS := [
	"FrontCamera",
	"RightCamera",
	"LeftCamera",
	"BackCamera",
	"TopCamera",
]


func _init() -> void:
	await _run()


func _run() -> void:
	var photobooth: Photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("IK-004 runner: failed to instantiate test scene: %s" % TEST_SCENE_PATH)
		return

	root.add_child(photobooth)
	await SceneUtils.wait_frames(self, 2)

	var driver: Node = SceneUtils.require_node(photobooth, ^"PoseStateMachineDriver")
	var scenarios_root: Node3D = SceneUtils.require_node(photobooth, ^"Markers/PoseStateMachine/Scenarios") as Node3D
	var rest_marker: Node3D = SceneUtils.require_node(photobooth, ^"Markers/PoseStateMachine/RestViewpoint") as Node3D

	if driver == null or scenarios_root == null or rest_marker == null:
		SceneUtils.fatal_error_and_quit("IK-004 runner: required driver/marker nodes are missing")
		return

	if _has_user_arg("--validate-only"):
		if not _validate_required_scenarios(driver):
			SceneUtils.fatal_error_and_quit("IK-004 runner: validation failed for required scenarios")
			return

		print("IK-004 runner: validate-only mode completed successfully")
		quit(0)
		return

	await _capture_framing_pass(photobooth, scenarios_root, rest_marker)
	await _capture_scenarios(photobooth, driver, scenarios_root)

	quit(0)


func _capture_framing_pass(photobooth: Photobooth, scenarios_root: Node3D, rest_marker: Node3D) -> void:
	rest_marker.visible = true
	for child: Node in scenarios_root.get_children():
		var marker: Node3D = child as Node3D
		if marker != null:
			marker.visible = true

	await SceneUtils.wait_frames(self, 2)

	for camera_name: String in REQUIRED_CAMERAS:
		var camera_rig: CameraRig = photobooth.get_camera_rig(camera_name)
		await camera_rig.capture_screenshot("%s/framing/%s_markers.jpg" % [OUTPUT_ROOT, _to_slug(camera_name)])

	rest_marker.visible = false
	for child: Node in scenarios_root.get_children():
		var marker: Node3D = child as Node3D
		if marker != null:
			marker.visible = false


func _capture_scenarios(photobooth: Photobooth, driver: Node, scenarios_root: Node3D) -> void:
	var selected_scenarios: PackedStringArray = _resolve_selected_scenarios(driver)
	if selected_scenarios.is_empty():
		SceneUtils.fatal_error_and_quit("IK-004 runner: no scenarios selected")
		return

	for scenario_index: int in selected_scenarios.size():
		var scenario_name: String = selected_scenarios[scenario_index]
		var scenario_node: Node3D = scenarios_root.get_node_or_null(NodePath(scenario_name)) as Node3D
		if scenario_node == null:
			SceneUtils.fatal_error_and_quit("IK-004 runner: scenario marker '%s' is missing" % scenario_name)
			return

		scenario_node.visible = true

		var applied: bool = bool(driver.call("ApplyScenario", StringName(scenario_name)))
		if not applied:
			SceneUtils.fatal_error_and_quit("IK-004 runner: failed to apply scenario '%s'" % scenario_name)
			return

		await SceneUtils.wait_frames(self, 6)
		await SceneUtils.wait_seconds(self, 0.05)

		var state_id: StringName = StringName(driver.call("GetCurrentStateId"))
		var file_name: String = "%s/poses/%02d_%s__%s.jpg" % [
			OUTPUT_ROOT,
			scenario_index + 1,
			_to_slug(scenario_name),
			_to_slug(state_id)
		]
		await photobooth.capture_screenshots(file_name)

		scenario_node.visible = false


func _resolve_selected_scenarios(driver: Node) -> PackedStringArray:
	var all_scenarios: PackedStringArray = _extract_scenario_names(driver)

	if all_scenarios.is_empty():
		return PackedStringArray()

	var requested: String = _get_user_arg_value("--scenarios")
	if requested.is_empty():
		return all_scenarios

	var requested_scenarios: PackedStringArray = requested.split(",", false)
	var selected := PackedStringArray()
	for scenario_name_raw: String in requested_scenarios:
		var scenario_name: String = scenario_name_raw.strip_edges()
		if scenario_name.is_empty():
			continue

		if not all_scenarios.has(scenario_name):
			SceneUtils.fatal_error_and_quit("IK-004 runner: requested scenario '%s' is not defined" % scenario_name)
			return PackedStringArray()

		selected.append(scenario_name)

	return selected


func _validate_required_scenarios(driver: Node) -> bool:
	var scenario_names: PackedStringArray = _extract_scenario_names(driver)
	return scenario_names.has("Standing") and scenario_names.has("CrouchMidway") and scenario_names.has("CrouchFull")


func _extract_scenario_names(driver: Node) -> PackedStringArray:
	var names := PackedStringArray()
	var raw_names: Variant = driver.call("GetScenarioNames")

	if raw_names is PackedStringArray:
		for scenario_name: String in raw_names:
			names.append(scenario_name)
		return names

	if raw_names is Array:
		for scenario_value: Variant in raw_names:
			names.append(str(scenario_value))

	return names


func _to_slug(value: Variant) -> String:
	return SceneUtils.to_safe_file_component(str(value).to_lower())


func _has_user_arg(expected_arg: String) -> bool:
	if expected_arg.is_empty():
		return false

	for arg: String in OS.get_cmdline_user_args():
		if arg == expected_arg:
			return true

	return false


func _get_user_arg_value(prefix: String) -> String:
	if prefix.is_empty():
		return ""

	for arg: String in OS.get_cmdline_user_args():
		if arg.begins_with("%s=" % prefix):
			return arg.trim_prefix("%s=" % prefix)

	return ""
