extends SceneTree

const SCENE_PATH := "res://tests/navigation_controls/navigation_controls_visual.tscn"
const FORWARD_EPSILON := 0.001

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var packed := load(SCENE_PATH) as PackedScene
	if packed == null:
		push_error("Failed to load %s" % SCENE_PATH)
		quit(1)
		return

	var scene := packed.instantiate() as Photobooth
	root.add_child(scene)
	await process_frame
	await process_frame
	for rig: CameraRig in scene.camera_rigs.values():
		rig.viewport.own_world_3d = false
		rig.viewport.world_3d = scene.get_world_3d()
		rig.camera.global_transform = rig.global_transform
		rig.camera.current = true

	var marker := scene.get_node("Subject/DestinationMarker") as Node3D
	var marker_disc := scene.get_node("Subject/DestinationMarker/MarkerDisc") as MeshInstance3D
	var forward_indicator := scene.get_node("Subject/DestinationMarker/ForwardIndicator") as Node3D
	var preview_material := marker.get_meta("preview_material") as Material
	var pressed_material := marker.get_meta("pressed_material") as Material
	var root_camera := scene.get_node("Cameras/RootCaptureCamera") as Camera3D
	root_camera.current = true

	_verify_forward_indicator_convention(marker, forward_indicator)

	marker.visible = true
	marker.global_position = Vector3(0.75, 0.04, 0.0)
	marker.global_basis = Basis.looking_at(Vector3.FORWARD, Vector3.UP)
	marker_disc.material_override = preview_material
	_verify_marker_forward(marker, Vector3.FORWARD)
	await SceneUtils.capture_screenshot(self, "navigation_controls_preview_marker.jpg")

	marker.global_position = Vector3(-0.75, 0.04, 0.0)
	marker.global_basis = Basis.looking_at(Vector3.RIGHT, Vector3.UP)
	marker_disc.material_override = pressed_material
	_verify_marker_forward(marker, Vector3.RIGHT)
	await SceneUtils.capture_screenshot(self, "navigation_controls_pressed_marker_facing_right.jpg")

	marker.visible = false
	await SceneUtils.capture_screenshot(self, "navigation_controls_marker_hidden_while_moving.jpg")

	scene.queue_free()
	await process_frame
	quit(0)

func _verify_forward_indicator_convention(marker: Node3D, forward_indicator: Node3D) -> void:
	if forward_indicator.position.z >= 0.0:
		push_error("ForwardIndicator must sit on the marker's local -Z axis, matching Godot forward.")
		quit(1)
		return

	marker.global_basis = Basis.looking_at(Vector3.FORWARD, Vector3.UP)
	_verify_marker_forward(marker, Vector3.FORWARD)

func _verify_marker_forward(marker: Node3D, expected_forward: Vector3) -> void:
	var actual_forward := -marker.global_transform.basis.z.normalized()
	if actual_forward.distance_to(expected_forward.normalized()) > FORWARD_EPSILON:
		push_error("Marker forward mismatch. Expected %s but got %s" % [expected_forward, actual_forward])
		quit(1)
