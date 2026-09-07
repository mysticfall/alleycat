extends SceneTree

const TEST_SCENE_PATH := "res://tests/ik/locomotion_contact_lock_prototype.tscn"
const FEMALE_RIG_PATH := "res://assets/characters/reference/female/reference_female.blend"
const MALE_RIG_PATH := "res://assets/characters/reference/male/reference_male.blend"
const OUTPUT_ROOT := "IK-003/stage5_contact_lock_prototype/stage5_review_final"
const SNAPSHOT_PATH := "res://temp/IK-003/stage5_contact_lock_prototype/visual_snapshots.json"
const FRAMING_CAMERAS := ["FrontCamera", "RightCamera", "LeftCamera", "BackCamera", "BottomCamera"]
# Material scenarios use the labelled FrontCamera so both direct rigs and the complete snapshot-derived label remain visible.
const SCENARIO_CAMERAS := ["FrontCamera"]
const RIG_X := [-0.32, 0.32]

var _photobooth: Photobooth
var _evidence_root: Node3D
var _scenario_label: Label3D
var _rig_markers: Array[Dictionary] = []
var _scenarios: Array = []


func _init() -> void:
	await _run()


func _run() -> void:
	_scenarios = _load_snapshot_scenarios()
	if _scenarios.is_empty():
		SceneUtils.fatal_error_and_quit("Stage 5 runner: exact prototype snapshot export is missing or invalid; refusing independent visual simulation")
		return

	_photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if _photobooth == null:
		SceneUtils.fatal_error_and_quit("Stage 5 runner: failed to load photobooth scene")
		return

	root.add_child(_photobooth)
	_evidence_root = Node3D.new()
	_evidence_root.name = "PrototypeSnapshotDrivenEvidence"
	_photobooth.add_child(_evidence_root)
	_add_direct_reference_rigs()
	_create_snapshot_bound_visuals()
	await SceneUtils.wait_frames(self, 4)
	_configure_lower_body_cameras()
	if not _validate_directional_semantics():
		SceneUtils.fatal_error_and_quit("Stage 5 runner: direct-rig forward marker is not world +Z; screenshots would be misleading")
		return

	_apply_scenario(_scenarios[0])
	await SceneUtils.wait_frames(self, 2)
	await _capture_framing_pass()
	for scenario: Dictionary in _scenarios:
		_apply_scenario(scenario)
		await SceneUtils.wait_frames(self, 3)
		for camera_name: String in SCENARIO_CAMERAS:
			var camera: CameraRig = _photobooth.get_camera_rig(camera_name)
			await camera.capture_screenshot("%s/scenarios/%s_%s.jpg" % [OUTPUT_ROOT, scenario["slug"], camera_name.to_snake_case()])

	print("Stage 5 captures exported to res://temp/%s; visual review remains pending." % OUTPUT_ROOT)
	quit(0)


func _load_snapshot_scenarios() -> Array:
	var text := FileAccess.get_file_as_string(SNAPSHOT_PATH)
	var parsed: Variant = JSON.parse_string(text)
	if not parsed is Dictionary:
		return []
	var document: Dictionary = parsed
	var provenance: Variant = document.get("provenance")
	var scenarios: Variant = document.get("scenarios")
	if not provenance is Dictionary or not scenarios is Array:
		return []
	if provenance.get("visualReviewStatus") != "Pending":
		return []
	for scenario: Variant in scenarios:
		if not scenario is Dictionary or not scenario.has("anchors") or not scenario.has("disposition"):
			return []
	return scenarios


func _add_direct_reference_rigs() -> void:
	var female: Node3D = (load(FEMALE_RIG_PATH) as PackedScene).instantiate() as Node3D
	var male: Node3D = (load(MALE_RIG_PATH) as PackedScene).instantiate() as Node3D
	if female == null or male == null:
		SceneUtils.fatal_error_and_quit("Stage 5 runner: direct Female or Male reference rig is unavailable")
		return

	female.name = "FemaleDirectReferenceRig"
	male.name = "MaleDirectReferenceRig"
	female.position = Vector3(RIG_X[0], 0.0, 0.0)
	male.position = Vector3(RIG_X[1], 0.0, 0.0)
	female.scale = Vector3(0.78, 0.78, 0.78)
	male.scale = Vector3(0.78, 0.78, 0.78)
	_photobooth.get_node(^"Subject").add_child(female)
	_photobooth.get_node(^"Subject").add_child(male)


func _create_snapshot_bound_visuals() -> void:
	_create_overlay()
	for rig_x: float in RIG_X:
		var rig_marker: Dictionary = {
			"rig_x": rig_x,
			"left": _create_foot_visual(rig_x, "Left"),
			"right": _create_foot_visual(rig_x, "Right"),
			"request": _create_vector_visual(Color(0.12, 0.48, 1.0)),
			"output": _create_vector_visual(Color(0.1, 0.95, 0.95)),
			"residual": _create_vector_visual(Color(1.0, 0.12, 0.08)),
			"request_yaw": _create_yaw_visual(Color(0.12, 0.48, 1.0)),
			"output_yaw": _create_yaw_visual(Color(0.1, 0.95, 0.95)),
			"residual_yaw": _create_yaw_visual(Color(1.0, 0.12, 0.08)),
		}
		_rig_markers.append(rig_marker)


func _create_overlay() -> void:
	_scenario_label = Label3D.new()
	_scenario_label.position = Vector3(0.0, 0.78, 0.22)
	_scenario_label.font_size = 56
	_scenario_label.pixel_size = 0.0016
	_scenario_label.outline_size = 8
	_scenario_label.modulate = Color.WHITE
	_scenario_label.outline_modulate = Color(0.01, 0.02, 0.05)
	_scenario_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	_scenario_label.fixed_size = true
	_scenario_label.no_depth_test = true
	_evidence_root.add_child(_scenario_label)


func _create_foot_visual(rig_x: float, foot: String) -> Dictionary:
	var anchor := _make_mesh(CylinderMesh.new(), Color(0.1, 0.95, 0.25))
	var anchor_mesh: CylinderMesh = anchor.mesh as CylinderMesh
	anchor_mesh.top_radius = 0.038
	anchor_mesh.bottom_radius = 0.038
	anchor_mesh.height = 0.012
	var contact := _make_mesh(SphereMesh.new(), Color(1.0, 0.75, 0.08))
	contact.scale = Vector3(0.045, 0.045, 0.045)
	var target := _make_mesh(BoxMesh.new(), Color(0.1, 0.95, 0.95))
	target.scale = Vector3(0.055, 0.018, 0.055)
	_evidence_root.add_child(anchor)
	_evidence_root.add_child(contact)
	_evidence_root.add_child(target)
	return {"rig_x": rig_x, "foot": foot, "anchor": anchor, "contact": contact, "target": target}


func _create_vector_visual(colour: Color) -> Dictionary:
	var shaft := _make_mesh(CylinderMesh.new(), colour)
	var shaft_mesh: CylinderMesh = shaft.mesh as CylinderMesh
	shaft_mesh.top_radius = 0.009
	shaft_mesh.bottom_radius = 0.009
	shaft_mesh.height = 1.0
	var tip := _make_mesh(SphereMesh.new(), colour)
	tip.scale = Vector3(0.026, 0.026, 0.026)
	_evidence_root.add_child(shaft)
	_evidence_root.add_child(tip)
	return {"shaft": shaft, "tip": tip}


func _create_yaw_visual(colour: Color) -> Dictionary:
	var ring := _make_mesh(TorusMesh.new(), colour)
	var ring_mesh: TorusMesh = ring.mesh as TorusMesh
	ring_mesh.inner_radius = 0.075
	ring_mesh.outer_radius = 0.087
	var direction := _make_mesh(BoxMesh.new(), colour)
	direction.scale = Vector3(0.07, 0.012, 0.018)
	_evidence_root.add_child(ring)
	_evidence_root.add_child(direction)
	return {"ring": ring, "direction": direction}


func _make_mesh(mesh: Mesh, colour: Color) -> MeshInstance3D:
	var marker := MeshInstance3D.new()
	marker.mesh = mesh
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	material.albedo_color = colour
	material.emission_enabled = true
	material.emission = colour
	marker.material_override = material
	return marker


func _configure_lower_body_cameras() -> void:
	# Direct imports face world +Z. Swapping the axial rigs preserves semantic front/back labels.
	var front_camera: CameraRig = _photobooth.get_camera_rig("FrontCamera")
	var back_camera: CameraRig = _photobooth.get_camera_rig("BackCamera")
	var front_transform: Transform3D = front_camera.transform
	front_camera.transform = back_camera.transform
	back_camera.transform = front_transform
	for camera_name: String in FRAMING_CAMERAS:
		var camera: CameraRig = _photobooth.get_camera_rig(camera_name)
		camera.set("image_size", Vector2(1024, 768))
		camera.set("orthogonal_scale", 1.05)
		if camera_name != "BottomCamera":
			var camera_transform: Transform3D = camera.transform
			camera_transform.origin.y = 0.45
			camera.transform = camera_transform


func _validate_directional_semantics() -> bool:
	var forward_marker := Marker3D.new()
	_evidence_root.add_child(forward_marker)
	forward_marker.position = Vector3(0.0, 0.04, 0.30)
	forward_marker.look_at(forward_marker.global_position + Vector3.BACK, Vector3.UP)
	var marker_forward: Vector3 = -forward_marker.global_transform.basis.z.normalized()
	var valid := marker_forward.dot(Vector3.BACK) > 0.99 and forward_marker.global_position.z > 0.0
	forward_marker.queue_free()
	return valid


func _capture_framing_pass() -> void:
	for camera_name: String in FRAMING_CAMERAS:
		var camera: CameraRig = _photobooth.get_camera_rig(camera_name)
		await camera.capture_screenshot("%s/framing/%s.jpg" % [OUTPUT_ROOT, camera_name.to_snake_case()])


func _apply_scenario(scenario: Dictionary) -> void:
	_scenario_label.text = "%s\n%s | %s" % [_short_title(scenario["title"]), _short_disposition(scenario["disposition"]), _short_evidence(scenario)]
	for index: int in _rig_markers.size():
		_apply_rig_snapshot(_rig_markers[index], scenario)


func _short_title(title: String) -> String:
	match title:
		"Missing Contact Evidence":
			return "Missing Evidence"
		"Empty Contact Evidence":
			return "Empty Evidence"
		"Deliberately Excessive":
			return "Excessive Request"
		_:
			return title


func _short_disposition(disposition: String) -> String:
	return "Deferred" if disposition == "DeferredRecoveryRequired" else disposition


func _short_evidence(scenario: Dictionary) -> String:
	if scenario["reason"] == "MissingContactEvidence":
		return "Missing"
	if scenario["reason"] == "EmptyContactEvidence":
		return "Empty"
	if scenario["reason"] == "FeasibilityUnproven":
		return "No Proof"
	return scenario["evidenceState"]


func _apply_rig_snapshot(marker_set: Dictionary, scenario: Dictionary) -> void:
	var anchors: Dictionary = scenario["anchors"]
	var active_contacts: Array = scenario["activeContacts"]
	for foot_key: String in ["left", "right"]:
		var visual: Dictionary = marker_set[foot_key]
		var foot_name: String = visual["foot"]
		var is_anchored := anchors.has(foot_name)
		var anchor: MeshInstance3D = visual["anchor"]
		var contact: MeshInstance3D = visual["contact"]
		var target: MeshInstance3D = visual["target"]
		if is_anchored:
			var point := _snapshot_point(anchors[foot_name], marker_set["rig_x"])
			anchor.position = point
			contact.position = point + Vector3(0.0, 0.04, 0.0)
			target.position = point + Vector3(0.0, 0.018, 0.0)
			anchor.visible = true
			contact.visible = active_contacts.has(foot_name)
			target.visible = scenario["reason"] == null
		else:
			anchor.visible = false
			contact.visible = false
			target.visible = false

	var yaw_origin := Vector3(marker_set["rig_x"], 0.055, -0.04)
	# A pivot is intentionally centred on the authoritative active anchor; yaw remains centred on the rig origin.
	if scenario["title"] == "Permitted Pivot" and anchors.has("Left"):
		yaw_origin = _snapshot_point(anchors["Left"], marker_set["rig_x"]) + Vector3(0.0, 0.025, 0.0)
	_apply_vector(marker_set["request"], yaw_origin, _array_to_vector(scenario["requestTranslation"]), Vector3(0.0, 0.018, 0.0))
	_apply_vector(marker_set["output"], yaw_origin, _array_to_vector(scenario["outputTranslation"]), Vector3(0.0, 0.030, 0.0))
	_apply_vector(marker_set["residual"], yaw_origin, _array_to_vector(scenario["residualTranslation"]), Vector3(0.0, 0.042, 0.0))
	_apply_yaw(marker_set["request_yaw"], yaw_origin, scenario["requestYaw"], 0.018)
	_apply_yaw(marker_set["output_yaw"], yaw_origin, scenario["outputYaw"], 0.035)
	_apply_yaw(marker_set["residual_yaw"], yaw_origin, scenario["residualYaw"], 0.052)


func _snapshot_point(serialised: Array, rig_x: float) -> Vector3:
	return Vector3(rig_x + serialised[0], serialised[1], serialised[2])


func _array_to_vector(serialised: Array) -> Vector3:
	return Vector3(serialised[0], serialised[1], serialised[2])


func _apply_vector(visual: Dictionary, origin: Vector3, value: Vector3, height: Vector3) -> void:
	var visible: bool = value.length() > 0.0001
	var shaft: MeshInstance3D = visual["shaft"]
	var tip: MeshInstance3D = visual["tip"]
	shaft.visible = visible
	tip.visible = visible
	if not visible:
		return
	var display_vector: Vector3 = value.normalized() * maxf(value.length() * 8.0, 0.09)
	shaft.position = origin + height + display_vector * 0.5
	shaft.scale = Vector3(1.0, display_vector.length(), 1.0)
	shaft.look_at(shaft.global_position + display_vector, Vector3.UP)
	tip.position = origin + height + display_vector


func _apply_yaw(visual: Dictionary, origin: Vector3, radians: float, height: float) -> void:
	var visible: bool = absf(radians) > 0.0001
	var ring: MeshInstance3D = visual["ring"]
	var direction: MeshInstance3D = visual["direction"]
	ring.visible = visible
	direction.visible = visible
	if not visible:
		return
	ring.position = origin + Vector3(0.0, height, 0.0)
	direction.position = ring.position + Vector3(0.09, 0.0, 0.0)
	direction.rotation.y = radians
