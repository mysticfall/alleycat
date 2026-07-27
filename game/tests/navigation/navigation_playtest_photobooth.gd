extends Node3D

const NPC_SCENE := preload("res://assets/testing/navigation/navigation_test_npc.tscn")
const OUTPUT_ROOT := "NAV-001/turn_sign"
const TURN_PARAMETER := &"parameters/States/Walking/Locomotion/blend_position"

func _ready() -> void:
	if DisplayServer.get_name() == "headless":
		SceneUtils.fatal_error_and_quit("NAV-001 turn-sign screenshots require a renderer")
		return

	var playtest := get_node("NavigationPlaytest") as Node3D
	playtest.set_process(false)
	playtest.set_physics_process(false)
	for obstacle_name in ["NavigationObstacleTallBox", "NavigationObstacleWideBox", "NavigationObstacleCylinder"]:
		playtest.get_node(obstacle_name).visible = false

	var right_actor := playtest.get_node("NavigationTestNpc") as Node3D
	var left_actor := NPC_SCENE.instantiate() as Node3D
	left_actor.name = "LeftTurnActor"
	playtest.add_child(left_actor)
	right_actor.name = "RightTurnActor"
	right_actor.position = Vector3(-1.4, 0.0, 0.7)
	left_actor.position = Vector3(1.4, 0.0, 0.7)
	await SceneUtils.wait_frames(get_tree(), 4)

	_prepare_actor(right_actor, 1.0)
	_prepare_actor(left_actor, -1.0)
	_add_direction_marker(playtest, "RIGHT +X", Vector3(-1.4, 0.04, -1.2), Vector3.RIGHT, Color(0.15, 0.65, 1.0))
	_add_direction_marker(playtest, "LEFT -X", Vector3(1.4, 0.04, -1.2), Vector3.LEFT, Color(1.0, 0.4, 0.15))

	var camera := get_node("VerificationCamera") as Camera3D
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.size = 6.5
	camera.global_position = Vector3(0.0, 7.5, 0.0)
	camera.look_at(Vector3(0.0, 0.0, 0.0), Vector3.FORWARD)
	camera.make_current()
	await SceneUtils.wait_frames(get_tree(), 4)
	print("TURN_SIGN_CAMERA_SANITY top_y=%.2f right_target=+X left_target=-X" % camera.global_position.y)
	await SceneUtils.capture_screenshot(get_tree(), "%s/symmetric_turn_start.jpg" % OUTPUT_ROOT)

	var right_start := right_actor.global_position
	var left_start := left_actor.global_position
	var strongest_right_root_yaw := 0.0
	var strongest_left_root_yaw := 0.0
	for _frame in range(60):
		await get_tree().physics_frame
		var right_yaw := (right_actor.get_node("AnimationTree") as AnimationTree).get_root_motion_rotation().get_euler().y
		var left_yaw := (left_actor.get_node("AnimationTree") as AnimationTree).get_root_motion_rotation().get_euler().y
		if absf(right_yaw) > absf(strongest_right_root_yaw):
			strongest_right_root_yaw = right_yaw
		if absf(left_yaw) > absf(strongest_left_root_yaw):
			strongest_left_root_yaw = left_yaw
	var right_forward := -right_actor.global_basis.z.normalized()
	var left_forward := -left_actor.global_basis.z.normalized()
	var right_root_translation := right_actor.global_position - right_start
	var left_root_translation := left_actor.global_position - left_start
	var right_blend: Vector2 = right_actor.get_node("AnimationTree").get(TURN_PARAMETER)
	var left_blend: Vector2 = left_actor.get_node("AnimationTree").get(TURN_PARAMETER)
	_add_trajectory(playtest, right_start, right_actor.global_position, Color(0.15, 0.65, 1.0))
	_add_trajectory(playtest, left_start, left_actor.global_position, Color(1.0, 0.4, 0.15))
	print("TURN_SIGN_RESULT right_blend=%s right_root_translation=%s right_root_yaw=%.6f right_forward=%s left_blend=%s left_root_translation=%s left_root_yaw=%.6f left_forward=%s" % [right_blend, right_root_translation, strongest_right_root_yaw, right_forward, left_blend, left_root_translation, strongest_left_root_yaw, left_forward])
	if right_blend.x <= 0.0 or left_blend.x >= 0.0:
		SceneUtils.fatal_error_and_quit("Semantic turn commands selected the wrong graph side")
		return
	if strongest_right_root_yaw <= 0.0 or strongest_left_root_yaw >= 0.0:
		SceneUtils.fatal_error_and_quit("Imported runtime root yaw signs did not mirror the selected graph sides")
		return
	if right_root_translation.length() <= 0.01 or left_root_translation.length() <= 0.01:
		SceneUtils.fatal_error_and_quit("Production actors did not consume non-zero root translation")
		return
	if right_forward.x <= 0.05 or left_forward.x >= -0.05:
		SceneUtils.fatal_error_and_quit("Actor forwards did not rotate towards requested world +X/-X")
		return
	await SceneUtils.capture_screenshot(get_tree(), "%s/symmetric_turn_after_1s.jpg" % OUTPUT_ROOT)
	get_tree().quit(0)

func _prepare_actor(actor: Node3D, semantic_turn: float) -> void:
	var navigation := actor.get_node_or_null("Navigation")
	if navigation != null:
		navigation.set_physics_process(false)
	var locomotion := actor.get_node("Locomotion")
	locomotion.call("Move", Vector2.UP)
	locomotion.call("Rotate", Vector2(semantic_turn, 0.0))

func _add_direction_marker(parent: Node3D, text: String, origin: Vector3, direction: Vector3, color: Color) -> void:
	var mesh := ImmediateMesh.new()
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	material.albedo_color = color
	mesh.surface_begin(Mesh.PRIMITIVE_LINES, material)
	mesh.surface_add_vertex(origin)
	mesh.surface_add_vertex(origin + direction * 1.0)
	mesh.surface_add_vertex(origin + direction * 1.0)
	mesh.surface_add_vertex(origin + direction * 0.72 + Vector3(0.0, 0.0, 0.18))
	mesh.surface_add_vertex(origin + direction * 1.0)
	mesh.surface_add_vertex(origin + direction * 0.72 - Vector3(0.0, 0.0, 0.18))
	mesh.surface_end()
	var marker := MeshInstance3D.new()
	marker.mesh = mesh
	parent.add_child(marker)
	var label := Label3D.new()
	label.text = text
	label.font_size = 42
	label.modulate = color
	label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	label.position = origin + Vector3(0.0, 0.2, 0.3)
	parent.add_child(label)

func _add_trajectory(parent: Node3D, start: Vector3, finish: Vector3, color: Color) -> void:
	var mesh := ImmediateMesh.new()
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	material.albedo_color = color
	mesh.surface_begin(Mesh.PRIMITIVE_LINES, material)
	mesh.surface_add_vertex(start + Vector3.UP * 0.05)
	mesh.surface_add_vertex(finish + Vector3.UP * 0.05)
	mesh.surface_end()
	var trajectory := MeshInstance3D.new()
	trajectory.mesh = mesh
	parent.add_child(trajectory)
