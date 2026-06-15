extends SceneTree

const TEST_SCENE_PATH := "res://tests/testing/mirror_room/mirror_room_orientation_visual_gate.tscn"
const OUTPUT_ROOT := "mirror-room-orientation-regression"

const PLAYER_PATH := ^"Actors/Player"
const PLAYER_RIG_CONTAINER_PATH := ^"Actors/Player/Female"
const PLAYER_SKELETON_PATH := ^"Actors/Player/Female/GeneralSkeleton"
const PLAYER_VIEWPOINT_PATH := ^"Actors/Player/Female/GeneralSkeleton/Head/Viewpoint"
const ALLY_PATH := ^"Actors/Female"
const ALLY_RIG_CONTAINER_PATH := ^"Actors/Female/Female"
const ALLY_SKELETON_PATH := ^"Actors/Female/Female/GeneralSkeleton"
const ALLY_VIEWPOINT_PATH := ^"Actors/Female/Female/GeneralSkeleton/Head/Viewpoint"

const SCENE_FACE_DOT_THRESHOLD := 0.85
const CAMERA_TARGET_HEIGHT := 1.25


func _init() -> void:
	await _run()


func _run() -> void:
	if DisplayServer.get_name() == "headless":
		SceneUtils.fatal_error_and_quit("Mirror-room orientation visual gate must not run headless")
		return

	DisplayServer.window_set_size(Vector2i(1280, 720))

	var mirror_room: Node3D = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Node3D
	if mirror_room == null:
		SceneUtils.fatal_error_and_quit("Mirror-room orientation visual gate failed to load %s" % TEST_SCENE_PATH)
		return

	_disable_unrelated_external_services(mirror_room)
	root.add_child(mirror_room)
	await SceneUtils.wait_frames(self, 12)

	var player: Node3D = SceneUtils.require_node(mirror_room, PLAYER_PATH) as Node3D
	var player_rig_container: Node3D = SceneUtils.require_node(mirror_room, PLAYER_RIG_CONTAINER_PATH) as Node3D
	var player_skeleton: Skeleton3D = SceneUtils.require_node(mirror_room, PLAYER_SKELETON_PATH) as Skeleton3D
	var player_viewpoint: Marker3D = SceneUtils.require_node(mirror_room, PLAYER_VIEWPOINT_PATH) as Marker3D
	var ally: Node3D = SceneUtils.require_node(mirror_room, ALLY_PATH) as Node3D
	var ally_rig_container: Node3D = SceneUtils.require_node(mirror_room, ALLY_RIG_CONTAINER_PATH) as Node3D
	var ally_skeleton: Skeleton3D = SceneUtils.require_node(mirror_room, ALLY_SKELETON_PATH) as Skeleton3D
	var ally_viewpoint: Marker3D = SceneUtils.require_node(mirror_room, ALLY_VIEWPOINT_PATH) as Marker3D
	if player == null or player_rig_container == null or player_skeleton == null or player_viewpoint == null or ally == null or ally_rig_container == null or ally_skeleton == null or ally_viewpoint == null:
		SceneUtils.fatal_error_and_quit("Mirror-room orientation visual gate failed to resolve actor rig containers")
		return

	var player_expected_front_world := Vector3.FORWARD
	var ally_expected_front_world := _flatten_y(player_viewpoint.global_position - ally_viewpoint.global_position).normalized()
	_assert_actor_face_direction("player", player_skeleton, player_viewpoint, player_expected_front_world)
	_assert_actor_face_direction("ally", ally_skeleton, ally_viewpoint, ally_expected_front_world)
	var player_mesh_forward_world := _assert_rig_face_origin("player", player, player_rig_container, player_expected_front_world)
	var ally_mesh_forward_world := _assert_rig_face_origin("ally", ally, ally_rig_container, ally_expected_front_world)
	_print_actor_direction("player", player, player_rig_container, player_viewpoint, player_expected_front_world, player_mesh_forward_world)
	_print_actor_direction("ally", ally, ally_rig_container, ally_viewpoint, ally_expected_front_world, ally_mesh_forward_world)

	_add_orientation_markers(mirror_room, player, player_expected_front_world)
	_add_capture_lighting(mirror_room, player.global_position)
	await SceneUtils.wait_frames(self, 4)

	var target := player.global_position + Vector3(0.0, CAMERA_TARGET_HEIGHT, 0.0)
	var cameras := {
		"01_front_expected_face": _create_camera(mirror_room, "OrientationFrontCamera", target + Vector3(0.0, 0.05, -1.35), target, 48.0),
		"02_back_expected_back": _create_camera(mirror_room, "OrientationBackCamera", target + Vector3(0.0, 0.05, 1.35), target, 48.0),
		"03_front_right_context": _create_camera(mirror_room, "OrientationFrontRightCamera", target + Vector3(1.1, 0.15, -1.45), target, 54.0),
		"04_top_direction_markers": _create_camera(mirror_room, "OrientationTopCamera", player.global_position + Vector3(0.0, 4.2, 0.02), player.global_position, 35.0),
	}

	for camera_name: String in cameras.keys():
		var camera: Camera3D = cameras[camera_name] as Camera3D
		camera.make_current()
		await SceneUtils.wait_frames(self, 2)
		await SceneUtils.capture_screenshot(self, "%s/%s.jpg" % [OUTPUT_ROOT, camera_name])

	print("MIRROR_ROOM_ORIENTATION_VISUAL_GATE_PASS artefact_root=temp/%s player_expected_front=(%.4f, %.4f, %.4f)" % [OUTPUT_ROOT, player_expected_front_world.x, player_expected_front_world.y, player_expected_front_world.z])
	quit(0)


func _assert_rig_face_origin(actor_name: String, actor_root: Node3D, rig_container: Node3D, expected_face_world: Vector3) -> Vector3:
	# The reference rig's avatar face is skeleton-local +Z because the Female rig container carries
	# the import yaw offset. Resolve that semantic direction into world space and compare against
	# independent mirror-room markers instead of using the actor's current transform as the oracle.
	var rig_avatar_face_world := (rig_container.global_transform.basis * Vector3.BACK).normalized()
	var dot := _flatten_y(rig_avatar_face_world).normalized().dot(_flatten_y(expected_face_world).normalized())
	if dot < SCENE_FACE_DOT_THRESHOLD:
		SceneUtils.fatal_error_and_quit("Mirror-room orientation visual gate %s rig face mismatch: rig_avatar_face_world=%s expected_face_world=%s dot=%.6f" % [actor_name, rig_avatar_face_world, expected_face_world, dot])
		return Vector3.ZERO

	return rig_avatar_face_world


func _assert_actor_face_direction(actor_name: String, skeleton: Skeleton3D, viewpoint: Marker3D, expected_face_world: Vector3) -> void:
	var viewpoint_forward_world := (-viewpoint.global_transform.basis.z).normalized()
	var face_protrusion_world := _compute_head_to_viewpoint_direction(skeleton, viewpoint)
	var expected_flat := _flatten_y(expected_face_world).normalized()
	var forward_dot := _flatten_y(viewpoint_forward_world).normalized().dot(expected_flat)
	if forward_dot < SCENE_FACE_DOT_THRESHOLD:
		SceneUtils.fatal_error_and_quit("Mirror-room orientation visual gate %s viewpoint/camera forward points backwards: viewpoint_forward=%s expected=%s dot=%.6f" % [actor_name, viewpoint_forward_world, expected_face_world, forward_dot])
	var protrusion_dot := face_protrusion_world.dot(expected_flat)
	if protrusion_dot < 0.85:
		SceneUtils.fatal_error_and_quit("Mirror-room orientation visual gate %s face/chest cue is on rear side: head_to_viewpoint=%s expected=%s dot=%.6f" % [actor_name, face_protrusion_world, expected_face_world, protrusion_dot])


func _compute_head_to_viewpoint_direction(skeleton: Skeleton3D, viewpoint: Node3D) -> Vector3:
	var head_bone_index := skeleton.find_bone("Head")
	if head_bone_index < 0:
		SceneUtils.fatal_error_and_quit("Mirror-room orientation visual gate expected a Head bone")
		return Vector3.ZERO
	var head_rest_world := skeleton.global_transform * skeleton.get_bone_global_rest(head_bone_index).origin
	var head_to_viewpoint := _flatten_y(viewpoint.global_position - head_rest_world)
	if head_to_viewpoint.length_squared() <= 0.0001:
		SceneUtils.fatal_error_and_quit("Mirror-room orientation visual gate expected the viewpoint to be offset from the head toward the face")
		return Vector3.ZERO
	return head_to_viewpoint.normalized()


func _print_actor_direction(actor_name: String, actor_root: Node3D, rig_container: Node3D, viewpoint: Marker3D, expected_face_world: Vector3, mesh_face_world: Vector3) -> void:
	var viewpoint_forward_world := (-viewpoint.global_transform.basis.z).normalized()
	print("MIRROR_ROOM_ORIENTATION_DIRECTION actor=%s root_origin=(%.4f, %.4f, %.4f) rig_local_basis_z=(%.4f, %.4f, %.4f) expected_face=(%.4f, %.4f, %.4f) mesh_face=(%.4f, %.4f, %.4f) viewpoint_forward=(%.4f, %.4f, %.4f)" % [
		actor_name,
		actor_root.global_position.x,
		actor_root.global_position.y,
		actor_root.global_position.z,
		rig_container.transform.basis.z.x,
		rig_container.transform.basis.z.y,
		rig_container.transform.basis.z.z,
		expected_face_world.x,
		expected_face_world.y,
		expected_face_world.z,
		mesh_face_world.x,
		mesh_face_world.y,
		mesh_face_world.z,
		viewpoint_forward_world.x,
		viewpoint_forward_world.y,
		viewpoint_forward_world.z,
	])


func _flatten_y(value: Vector3) -> Vector3:
	return Vector3(value.x, 0.0, value.z)


func _add_orientation_markers(parent: Node, actor_root: Node3D, forward_world: Vector3) -> void:
	var marker_height := Vector3(0.0, 1.15, 0.0)
	var actor_origin := actor_root.global_position
	_add_sphere_marker(parent, "GreenForwardMarker", actor_origin + marker_height + forward_world * 0.85, Color(0.0, 1.0, 0.0), 0.11)
	_add_sphere_marker(parent, "RedBackMarker", actor_origin + marker_height - forward_world * 0.85, Color(1.0, 0.05, 0.02), 0.11)


func _add_sphere_marker(parent: Node, marker_name: String, position: Vector3, colour: Color, radius: float) -> void:
	var sphere := MeshInstance3D.new()
	sphere.name = marker_name
	var mesh := SphereMesh.new()
	mesh.radius = radius
	mesh.height = radius * 2.0
	sphere.mesh = mesh
	sphere.material_override = _make_unshaded_material(colour)
	parent.add_child(sphere)
	sphere.global_position = position


func _make_unshaded_material(colour: Color) -> StandardMaterial3D:
	var material := StandardMaterial3D.new()
	material.albedo_color = colour
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	return material


func _add_capture_lighting(parent: Node, focus: Vector3) -> void:
	var light := OmniLight3D.new()
	light.name = "OrientationCaptureLight"
	light.light_energy = 3.0
	light.omni_range = 6.0
	parent.add_child(light)
	light.global_position = focus + Vector3(0.0, 2.2, -1.6)


func _create_camera(parent: Node, camera_name: String, position: Vector3, target: Vector3, fov: float) -> Camera3D:
	var camera := Camera3D.new()
	camera.name = camera_name
	camera.projection = Camera3D.PROJECTION_PERSPECTIVE
	camera.fov = fov
	camera.near = 0.01
	camera.far = 20.0
	parent.add_child(camera)
	camera.global_position = position
	camera.look_at(target, Vector3.UP)
	return camera


func _disable_unrelated_external_services(mirror_room: Node) -> void:
	var lip_sync_player := mirror_room.get_node_or_null(^"Actors/AIConversation/AlleyVoice/LipSyncPlayer")
	if lip_sync_player != null:
		lip_sync_player.get_parent().remove_child(lip_sync_player)
		lip_sync_player.free()
