extends SceneTree

const SCENE_PATH := "res://tests/navigation_steering/navigation_steering_visual.tscn"
const DIRECTION_EPSILON := 0.001
const PRECISION_DISTANCE := 0.50
const OLD_TOLERANCE_RADIUS := 1.0
const CORNER_START := Vector3(-0.6, 0.0, 0.0)
const CORNER_WAYPOINT := Vector3(-0.6, 0.0, -1.2)
const CORNER_DESTINATION := Vector3(0.2, 0.0, 0.0)


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	var booth := SceneUtils.instantiate_scene(SCENE_PATH) as Photobooth
	if booth == null:
		SceneUtils.fatal_error_and_quit("Failed to load navigation steering photobooth")
		return
	root.add_child(booth)
	await SceneUtils.wait_frames(self, 2)

	var camera_rig := booth.get_camera_rig("OverviewCamera")
	camera_rig.viewport.own_world_3d = false
	camera_rig.viewport.world_3d = booth.get_world_3d()
	camera_rig.camera.global_transform = camera_rig.global_transform
	camera_rig.camera.current = true

	if not _verify_directional_sanity(booth):
		quit(1)
		return
	if not _verify_camera_and_markers(booth, camera_rig):
		quit(1)
		return

	_apply_long_initial(booth)
	await SceneUtils.wait_frames(self, 2)
	await camera_rig.capture_screenshot("framing/camera_marker_sanity.jpg")
	if _has_argument("--framing-only"):
		await _cleanup(booth)
		quit(0)
		return

	await camera_rig.capture_screenshot("scenarios/long_initial.jpg")
	_apply_long_mid(booth)
	await SceneUtils.wait_frames(self, 2)
	await camera_rig.capture_screenshot("scenarios/long_mid_progressive_yaw.jpg")

	_apply_corner_approach(booth)
	await SceneUtils.wait_frames(self, 2)
	await camera_rig.capture_screenshot("scenarios/interior_corner_transition.jpg")

	_apply_short_lateral(booth, Vector3.ZERO)
	await SceneUtils.wait_frames(self, 2)
	await camera_rig.capture_screenshot("scenarios/short_lateral_start.jpg")
	_apply_short_lateral(booth, Vector3(0.9, 0.0, 0.0))
	await SceneUtils.wait_frames(self, 2)
	await camera_rig.capture_screenshot("scenarios/short_lateral_end.jpg")

	_apply_terminal_turn(booth, Vector3.RIGHT)
	await SceneUtils.wait_frames(self, 2)
	await camera_rig.capture_screenshot("scenarios/terminal_before_yaw.jpg")
	_apply_terminal_turn(booth, Vector3.FORWARD)
	await SceneUtils.wait_frames(self, 2)
	await camera_rig.capture_screenshot("scenarios/terminal_after_completion.jpg")

	# These checkpoints are deliberately staged for objective expected-state readability. Runtime linkage is supplied
	# by the matching DirectTransformNavigation C# integration tests, not by this screenshot runner.
	_configure_evidence_camera(camera_rig, Vector3(0.25, 0.0, 0.0), 3.4)
	_apply_precision_evidence(booth, Vector3.ZERO, "EXPECTED STATE — 0.50 m LATERAL START\nLOCAL -Z FACING CUE MUST REMAIN CYAN/FORWARD")
	await SceneUtils.wait_frames(self, 2)
	await camera_rig.capture_screenshot("framing/precision_evidence_camera.jpg")
	await camera_rig.capture_screenshot("precision/sub_metre_050_start_expected_state.jpg")
	_apply_precision_evidence(booth, Vector3(PRECISION_DISTANCE, 0.0, 0.0), "EXPECTED STATE — 0.50 m LATERAL END\nAT DESTINATION; CYAN LOCAL -Z FACING UNCHANGED")
	await SceneUtils.wait_frames(self, 2)
	await camera_rig.capture_screenshot("precision/sub_metre_050_end_expected_state.jpg")

	_configure_evidence_camera(camera_rig, Vector3(-0.2, 0.0, -0.55), 4.2)
	_apply_corner_evidence(booth, CORNER_START, "EXPECTED STATE — NEAR SIDE\nDIRECT DESTINATION = 0.80 m (< OLD 1.0 m), ROUTE STILL ACTIVE")
	await SceneUtils.wait_frames(self, 2)
	await camera_rig.capture_screenshot("framing/close_corner_evidence_camera.jpg")
	await camera_rig.capture_screenshot("precision/close_corner_01_near_side_expected_state.jpg")
	_apply_corner_evidence(booth, CORNER_WAYPOINT, "EXPECTED STATE — INTERIOR WAYPOINT\nTRAVERSED CORNER; DID NOT STOP ON NEAR SIDE")
	await SceneUtils.wait_frames(self, 2)
	await camera_rig.capture_screenshot("precision/close_corner_02_interior_expected_state.jpg")
	_apply_corner_evidence(booth, CORNER_DESTINATION, "EXPECTED STATE — TERMINAL PRECISION\nREMAINING ROUTE CONSUMED; ACTOR AT DESTINATION")
	await SceneUtils.wait_frames(self, 2)
	await camera_rig.capture_screenshot("precision/close_corner_03_terminal_expected_state.jpg")

	await _cleanup(booth)
	quit(0)


func _apply_long_initial(booth: Photobooth) -> void:
	_show_route(booth, "LongRoute")
	_set_actor(booth, Vector3.ZERO, Vector3.FORWARD)
	_set_markers(booth, Vector3.ZERO, Vector3(3.0, 0.0, 0.0), false)


func _apply_long_mid(booth: Photobooth) -> void:
	_show_route(booth, "LongRoute")
	_set_actor(booth, Vector3(1.25, 0.0, 0.0), (Vector3.RIGHT + Vector3.FORWARD).normalized())
	_set_markers(booth, Vector3.ZERO, Vector3(3.0, 0.0, 0.0), false)


func _apply_corner_approach(booth: Photobooth) -> void:
	_show_route(booth, "CornerRoute")
	_set_actor(booth, Vector3(1.45, 0.0, 0.0), (Vector3.RIGHT + Vector3.FORWARD).normalized())
	_set_markers(booth, Vector3.ZERO, Vector3(2.0, 0.0, -2.2), true)


func _apply_short_lateral(booth: Photobooth, actor_position: Vector3) -> void:
	_show_route(booth, "ShortRoute")
	_set_actor(booth, actor_position, Vector3.FORWARD)
	_set_markers(booth, Vector3.ZERO, Vector3(0.9, 0.0, 0.0), false)


func _apply_terminal_turn(booth: Photobooth, actor_facing: Vector3) -> void:
	_hide_precision_evidence(booth)
	_show_route(booth, "TerminalRoute")
	_set_actor(booth, Vector3(3.0, 0.0, 0.0), actor_facing)
	_set_markers(booth, Vector3.ZERO, Vector3(3.0, 0.0, 0.0), false)


func _apply_precision_evidence(booth: Photobooth, actor_position: Vector3, checkpoint_text: String) -> void:
	_show_route(booth, "")
	var evidence := _get_or_create_evidence(booth)
	evidence.visible = true
	_set_evidence_route(evidence, [Vector3.ZERO, Vector3(PRECISION_DISTANCE, 0.0, 0.0)])
	_set_tolerance_reference(evidence, Vector3(PRECISION_DISTANCE, 0.0, 0.0))
	_set_evidence_points(evidence, Vector3.ZERO, Vector3(PRECISION_DISTANCE, 0.0, 0.0), Vector3.ZERO, false)
	_set_evidence_label(evidence, checkpoint_text, Vector3(0.25, 1.3, -0.15))
	_set_actor(booth, actor_position, Vector3.FORWARD)
	_prepare_evidence_actor(booth)
	_set_markers(booth, Vector3.ZERO, Vector3(PRECISION_DISTANCE, 0.0, 0.0), false)


func _apply_corner_evidence(booth: Photobooth, actor_position: Vector3, checkpoint_text: String) -> void:
	_show_route(booth, "")
	var evidence := _get_or_create_evidence(booth)
	evidence.visible = true
	_set_evidence_route(evidence, [CORNER_START, CORNER_WAYPOINT, CORNER_DESTINATION])
	_set_tolerance_reference(evidence, CORNER_DESTINATION)
	_set_evidence_points(evidence, CORNER_START, CORNER_DESTINATION, CORNER_WAYPOINT, true)
	_set_evidence_label(evidence, checkpoint_text, Vector3(-0.2, 1.4, -0.55))
	_set_actor(booth, actor_position, Vector3.FORWARD)
	_prepare_evidence_actor(booth)
	_set_markers(booth, CORNER_START, CORNER_DESTINATION, true)
	booth.get_marker("CornerMarker").position = CORNER_WAYPOINT + Vector3(0.0, 0.05, 0.0)


func _get_or_create_evidence(booth: Photobooth) -> Node3D:
	var existing := booth.get_node_or_null("Subject/PrecisionEvidence") as Node3D
	if existing != null:
		return existing

	var evidence := Node3D.new()
	evidence.name = "PrecisionEvidence"
	booth.get_node("Subject").add_child(evidence)

	var route := Node3D.new()
	route.name = "Route"
	evidence.add_child(route)

	var tolerance := MeshInstance3D.new()
	tolerance.name = "OldToleranceReference"
	var ring := TorusMesh.new()
	ring.inner_radius = OLD_TOLERANCE_RADIUS - 0.025
	ring.outer_radius = OLD_TOLERANCE_RADIUS + 0.025
	ring.rings = 64
	ring.ring_segments = 8
	tolerance.mesh = ring
	tolerance.material_override = _make_material(Color(1.0, 0.2, 0.2, 1.0), true)
	evidence.add_child(tolerance)

	var tolerance_label := Label3D.new()
	tolerance_label.name = "OldToleranceLabel"
	tolerance_label.text = "OLD 1.0 m COMPLETION TOLERANCE"
	tolerance_label.font_size = 24
	tolerance_label.outline_size = 8
	tolerance_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	evidence.add_child(tolerance_label)

	var checkpoint_label := Label3D.new()
	checkpoint_label.name = "CheckpointLabel"
	checkpoint_label.font_size = 26
	checkpoint_label.outline_size = 10
	checkpoint_label.modulate = Color(1.0, 1.0, 1.0, 1.0)
	checkpoint_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	evidence.add_child(checkpoint_label)
	for point_name in ["StartPoint", "DestinationPoint", "WaypointPoint"]:
		var point := MeshInstance3D.new()
		point.name = point_name
		var sphere := SphereMesh.new()
		sphere.radius = 0.07
		sphere.height = 0.14
		point.mesh = sphere
		evidence.add_child(point)
		var point_label := Label3D.new()
		point_label.name = point_name + "Label"
		point_label.font_size = 24
		point_label.outline_size = 8
		point_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
		evidence.add_child(point_label)
	return evidence


func _set_evidence_route(evidence: Node3D, points: Array[Vector3]) -> void:
	var route := evidence.get_node("Route") as Node3D
	for child in route.get_children():
		child.queue_free()
	for index in range(points.size() - 1):
		var start := points[index]
		var finish := points[index + 1]
		var delta := finish - start
		var segment := MeshInstance3D.new()
		var box := BoxMesh.new()
		box.size = Vector3(0.09, 0.025, delta.length())
		segment.mesh = box
		segment.material_override = _make_material(Color(0.95, 0.55, 0.08, 1.0), true)
		segment.position = (start + finish) * 0.5 + Vector3(0.0, 0.025, 0.0)
		segment.basis = Basis.looking_at(delta.normalized(), Vector3.UP)
		route.add_child(segment)


func _set_tolerance_reference(evidence: Node3D, centre: Vector3) -> void:
	var tolerance := evidence.get_node("OldToleranceReference") as MeshInstance3D
	tolerance.position = centre + Vector3(0.0, 0.045, 0.0)
	var label := evidence.get_node("OldToleranceLabel") as Label3D
	label.position = centre + Vector3(0.0, 0.15, OLD_TOLERANCE_RADIUS * 0.72)


func _set_evidence_points(evidence: Node3D, start: Vector3, destination: Vector3, waypoint: Vector3, show_waypoint: bool) -> void:
	var point_data := [
		["StartPoint", start, "START", Color(0.25, 0.65, 1.0, 1.0)],
		["DestinationPoint", destination, "DESTINATION", Color(0.3, 1.0, 0.4, 1.0)],
		["WaypointPoint", waypoint, "INTERIOR WAYPOINT", Color(1.0, 0.65, 0.1, 1.0)],
	]
	for entry in point_data:
		var point := evidence.get_node(entry[0]) as MeshInstance3D
		point.position = entry[1] + Vector3(0.0, 0.09, 0.0)
		point.material_override = _make_material(entry[3], true)
		point.visible = entry[0] != "WaypointPoint" or show_waypoint
		var label := evidence.get_node(entry[0] + "Label") as Label3D
		label.text = entry[2]
		label.position = entry[1] + Vector3(0.0, 0.26, 0.0)
		label.modulate = entry[3]
		label.visible = point.visible


func _set_evidence_label(evidence: Node3D, text: String, position: Vector3) -> void:
	var label := evidence.get_node("CheckpointLabel") as Label3D
	label.text = text
	label.position = position


func _make_material(colour: Color, unshaded: bool) -> StandardMaterial3D:
	var material := StandardMaterial3D.new()
	material.albedo_color = colour
	if unshaded:
		material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	return material


func _configure_evidence_camera(camera_rig: CameraRig, focus: Vector3, distance: float) -> void:
	var camera := camera_rig.camera
	camera.global_position = focus + Vector3(distance * 0.65, distance, distance * 0.75)
	camera.look_at(focus, Vector3.UP)
	camera.fov = 42.0


func _prepare_evidence_actor(booth: Photobooth) -> void:
	var actor := booth.get_node("Subject/DirectionalActor") as Node3D
	actor.scale = Vector3.ONE * 0.55
	(actor.get_node("ForwardLabel") as Label3D).font_size = 30


func _hide_precision_evidence(booth: Photobooth) -> void:
	var evidence := booth.get_node_or_null("Subject/PrecisionEvidence") as Node3D
	if evidence != null:
		evidence.visible = false


func _set_actor(booth: Photobooth, actor_position: Vector3, facing: Vector3) -> void:
	var actor := booth.get_node("Subject/DirectionalActor") as Node3D
	actor.position = actor_position
	actor.basis = Basis.looking_at(facing.normalized(), Vector3.UP)
	_verify_resolved_forward(actor, facing)


func _show_route(booth: Photobooth, route_name: String) -> void:
	for child in (booth.get_node("Subject") as Node3D).get_children():
		if child.name.ends_with("Route"):
			(child as Node3D).visible = child.name == route_name


func _set_markers(booth: Photobooth, start: Vector3, destination: Vector3, show_corner: bool) -> void:
	var start_marker := booth.get_marker("StartMarker")
	start_marker.position = start + Vector3(0.0, 0.05, 0.0)
	start_marker.visible = true
	var destination_marker := booth.get_marker("DestinationMarker")
	destination_marker.position = destination + Vector3(0.0, 0.05, 0.0)
	destination_marker.visible = true
	var corner_marker := booth.get_marker("CornerMarker")
	corner_marker.position = Vector3(2.0, 0.05, 0.0)
	corner_marker.visible = show_corner


func _verify_directional_sanity(booth: Photobooth) -> bool:
	if Vector3.FORWARD.distance_to(Vector3(0.0, 0.0, -1.0)) > DIRECTION_EPSILON:
		push_error("Godot world-forward sanity failed: expected -Z")
		return false
	var actor := booth.get_node("Subject/DirectionalActor") as Node3D
	var cue := actor.get_node("ForwardCue") as Node3D
	if cue.position.z >= 0.0:
		push_error("Actor ForwardCue must sit on local -Z")
		return false
	actor.basis = Basis.looking_at(Vector3.FORWARD, Vector3.UP)
	if not _verify_resolved_forward(actor, Vector3.FORWARD):
		return false
	for path in [
		"Subject/LongRoute/TerminalFacing",
		"Subject/CornerRoute/OutgoingBearing",
		"Subject/ShortRoute/TerminalFacing",
		"Subject/TerminalRoute/TerminalFacing",
	]:
		var arrow := booth.get_node(path) as Node3D
		if (-arrow.global_basis.z).normalized().distance_to(Vector3.FORWARD) > DIRECTION_EPSILON:
			push_error("Directional reference arrow does not resolve to world -Z: %s" % path)
			return false
	print("DIRECTIONAL_SANITY_OK: world forward=-Z; actor cue local -Z; reference arrows resolve -Z")
	return true


func _verify_resolved_forward(actor: Node3D, expected: Vector3) -> bool:
	var actual := (-actor.global_basis.z).normalized()
	if actual.distance_to(expected.normalized()) > DIRECTION_EPSILON:
		push_error("Actor forward mismatch. Expected %s, got %s" % [expected, actual])
		return false
	return true


func _verify_camera_and_markers(booth: Photobooth, camera_rig: CameraRig) -> bool:
	var camera_forward := -camera_rig.global_basis.z.normalized()
	var towards_subject := camera_rig.global_position.direction_to(Vector3(1.4, 0.0, -0.6))
	if camera_forward.dot(towards_subject) < 0.99:
		push_error("Overview camera is not aimed at route centre")
		return false
	for marker_name in ["StartMarker", "CornerMarker", "DestinationMarker"]:
		var marker := booth.get_marker(marker_name)
		if camera_rig.global_position.distance_to(marker.global_position) > 20.0:
			push_error("Marker outside camera working range: %s" % marker_name)
			return false
	print("CAMERA_MARKER_SANITY_OK: overview covers route centre and all reference markers")
	return true


func _has_argument(argument: String) -> bool:
	return OS.get_cmdline_user_args().has(argument) or OS.get_cmdline_args().has(argument)


func _cleanup(booth: Photobooth) -> void:
	booth.queue_free()
	await SceneUtils.wait_frames(self, 3)
