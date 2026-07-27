extends SceneTree

const TEST_SCENE_PATH := "res://tests/navigation/turn_authoring_visual.tscn"
const ACTOR_SCENE_PATH := "res://assets/characters/templates/reference_female/reference_female_base.tscn"
const GRAPH_PATH := "res://assets/characters/templates/animation/animation_tree_root_npc.tres"
const LIBRARY_PATH := "res://assets/characters/reference/female/animations/locomotion/standing_locomotion_library.tres"
const CATALOGUE_PATH := "res://assets/characters/reference/female/animations/locomotion/standing_locomotion_catalogue.json"
const OUTPUT_ROOT := "NAV-001/turn_authoring"
const ROOT_PATH := ^"%GeneralSkeleton:Root"
const SAMPLE_COUNT := 40

const SCENARIOS := [
	{"slug": "walk_arc_left", "role": "WalkArcLeft", "point": &"WalkArcLeft", "blend": Vector2(-1.0, 1.0), "moving": true},
	{"slug": "walk_arc_right", "role": "WalkArcRight", "point": &"WalkArcRight", "blend": Vector2(1.0, 1.0), "moving": true},
	{"slug": "turn_in_place_left_90", "role": "TurnInPlaceLeft90", "point": &"TurnInPlaceLeft90", "blend": Vector2(-1.0, 0.0), "moving": false},
	{"slug": "turn_in_place_right_90", "role": "TurnInPlaceRight90", "point": &"TurnInPlaceRight90", "blend": Vector2(1.0, 0.0), "moving": false},
]

var _photobooth: Photobooth
var _actor: Node3D
var _scenario_visuals: Node3D
var _library: AnimationLibrary
var _locomotion: AnimationNodeBlendSpace2D
var _catalogue_map: Dictionary


func _init() -> void:
	await _run()


func _run() -> void:
	if DisplayServer.get_name() == "headless":
		SceneUtils.fatal_error_and_quit("NAV-001 authoring trajectory screenshots require a renderer")
		return
	_photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if _photobooth == null:
		SceneUtils.fatal_error_and_quit("Failed to instantiate NAV-001 turn-authoring photobooth")
		return
	root.add_child(_photobooth)
	await SceneUtils.wait_frames(self, 3)
	_resolve_authoring_data()
	_prepare_frozen_actor()
	_add_world_markers()
	_configure_and_verify_camera()
	await SceneUtils.wait_frames(self, 2)
	await _photobooth.get_camera_rig("TopTrajectoryCamera").capture_screenshot("%s/framing/top_camera_markers.jpg" % OUTPUT_ROOT)

	for scenario: Dictionary in SCENARIOS:
		_draw_scenario(scenario)
		await SceneUtils.wait_frames(self, 2)
		await _photobooth.get_camera_rig("TopTrajectoryCamera").capture_screenshot(
			"%s/scenarios/%s.jpg" % [OUTPUT_ROOT, scenario.slug]
		)

	print("TURN_AUTHORING_VISUAL_PASS graph=%s actor_yaw=%.6f artefact_root=%s" % [GRAPH_PATH, _actor.rotation.y, OUTPUT_ROOT])
	_photobooth.free()
	quit(0)


func _resolve_authoring_data() -> void:
	_library = load(LIBRARY_PATH) as AnimationLibrary
	var graph := load(GRAPH_PATH) as AnimationNodeBlendTree
	var states := graph.get_node(&"States") as AnimationNodeStateMachine
	var walking := states.get_node(&"Walking") as AnimationNodeBlendTree
	_locomotion = walking.get_node(&"Locomotion") as AnimationNodeBlendSpace2D
	var catalogue_file := FileAccess.open(CATALOGUE_PATH, FileAccess.READ)
	var catalogue: Dictionary = JSON.parse_string(catalogue_file.get_as_text())
	for entry: Dictionary in catalogue.role_maps.reference_female:
		_catalogue_map[entry.graph_role] = entry
	if _library == null or _locomotion == null or _catalogue_map.is_empty():
		SceneUtils.fatal_error_and_quit("Could not resolve the graph, catalogue map, or imported animation library")


func _prepare_frozen_actor() -> void:
	var actor_scene := SceneUtils.load_scene(ACTOR_SCENE_PATH)
	_actor = actor_scene.instantiate() as Node3D
	_actor.name = "FrozenReferenceActor"
	_photobooth.get_node(^"Subject").add_child(_actor)
	var locomotion := _actor.get_node_or_null(^"Locomotion")
	if locomotion != null:
		locomotion.set_physics_process(false)
	var animation_tree := _actor.get_node(^"AnimationTree") as AnimationTree
	animation_tree.active = false
	animation_tree.set_process(false)
	animation_tree.set_physics_process(false)
	for modifier in _actor.find_children("*", "SkeletonModifier3D", true, false):
		modifier.active = false
	_scenario_visuals = Node3D.new()
	_scenario_visuals.name = "ImportedAuthoringTrajectory"
	_photobooth.add_child(_scenario_visuals)
	print("TURN_AUTHORING_FREEZE actor_position=%s actor_yaw=%.6f locomotion_physics=false animation_tree=false conversion=not_called" % [_actor.global_position, _actor.rotation.y])


func _configure_and_verify_camera() -> void:
	var camera := _photobooth.get_camera_rig("TopTrajectoryCamera")
	if camera == null or camera.global_position.y < 5.0:
		SceneUtils.fatal_error_and_quit("TopTrajectoryCamera is not above the imported root path")
		return
	camera.orthogonal_scale = 7.0
	camera.image_size = Vector2i(1200, 900)
	var camera_forward := -camera.global_basis.z.normalized()
	if camera_forward.dot(Vector3.DOWN) < 0.99:
		SceneUtils.fatal_error_and_quit("TopTrajectoryCamera is not trajectory-readable/downward-facing")
		return
	print("TURN_AUTHORING_CAMERA_SANITY position=%s forward=%s projection=orthogonal scale=%.2f world_forward=-Z imported_root_forward=+Z" % [camera.global_position, camera_forward, camera.orthogonal_scale])


func _add_world_markers() -> void:
	var markers := _photobooth.get_node(^"Markers") as Node3D
	_add_arrow(markers, Vector3.ZERO, Vector3.RIGHT * 1.25, Color.WHITE, 0.04)
	_add_arrow(markers, Vector3.ZERO, Vector3.LEFT * 1.25, Color.WHITE, 0.04)
	_add_arrow(markers, Vector3.ZERO, Vector3.FORWARD * 1.25, Color(0.25, 1.0, 0.3), 0.055)
	_add_arrow(markers, Vector3.ZERO, Vector3.BACK * 1.25, Color(1.0, 0.2, 0.9), 0.055)
	_add_label(markers, "+X", Vector3(1.45, 0.08, 0.0), Color.WHITE, 42)
	_add_label(markers, "-X", Vector3(-1.45, 0.08, 0.0), Color.WHITE, 42)
	_add_label(markers, "ACTOR FORWARD -Z", Vector3(0.0, 0.08, -1.55), Color(0.25, 1.0, 0.3), 36)
	_add_label(markers, "AUTHORED ROOT FORWARD +Z", Vector3(0.0, 0.08, 1.55), Color(1.0, 0.2, 0.9), 36)


func _draw_scenario(scenario: Dictionary) -> void:
	for child in _scenario_visuals.get_children():
		child.free()
	var point_index := _locomotion.find_blend_point_by_name(scenario.point)
	if point_index < 0:
		SceneUtils.fatal_error_and_quit("Missing exact graph blend point %s" % scenario.point)
		return
	var graph_position := _locomotion.get_blend_point_position(point_index)
	var animation_node := _locomotion.get_blend_point_node(point_index) as AnimationNodeAnimation
	var graph_animation := String(animation_node.animation)
	var key := graph_animation.trim_prefix("locomotion/")
	var catalogue_entry: Dictionary = _catalogue_map.get(scenario.role, {})
	if catalogue_entry.is_empty():
		SceneUtils.fatal_error_and_quit("Missing catalogue entry for graph role %s" % scenario.role)
		return
	if key != catalogue_entry.library_key or graph_position != scenario.blend:
		SceneUtils.fatal_error_and_quit("Graph point/key does not match the female authoring catalogue for %s" % scenario.role)
		return
	var animation := _library.get_animation(key)
	if animation == null:
		SceneUtils.fatal_error_and_quit("Catalogue library key does not resolve to an imported animation: %s" % key)
		return
	var position_track := animation.find_track(ROOT_PATH, Animation.TYPE_POSITION_3D)
	var rotation_track := animation.find_track(ROOT_PATH, Animation.TYPE_ROTATION_3D)
	if position_track < 0 or rotation_track < 0:
		SceneUtils.fatal_error_and_quit("Imported root tracks are missing for %s" % key)
		return
	var start_position := animation.position_track_interpolate(position_track, 0.0)
	var start_rotation := animation.rotation_track_interpolate(rotation_track, 0.0)
	var points: PackedVector3Array = []
	var rotations: Array[Quaternion] = []
	for sample_index in range(SAMPLE_COUNT + 1):
		var time := animation.length * float(sample_index) / float(SAMPLE_COUNT)
		var imported_position := animation.position_track_interpolate(position_track, time) - start_position
		points.append(Vector3(imported_position.x, 0.065, imported_position.z))
		rotations.append(start_rotation.inverse() * animation.rotation_track_interpolate(rotation_track, time))
	_add_polyline(_scenario_visuals, points, Color(1.0, 0.78, 0.08), 0.045)
	for sample_index in [0, int(SAMPLE_COUNT * 0.5), SAMPLE_COUNT]:
		var root_forward: Vector3 = rotations[sample_index] * Vector3.BACK
		root_forward.y = 0.0
		_add_arrow(_scenario_visuals, points[sample_index], root_forward.normalized() * 0.55, Color(1.0, 0.2, 0.9), 0.035)
	var planar_delta := Vector2(points[-1].x - points[0].x, points[-1].z - points[0].z)
	var maximum_planar_excursion := 0.0
	for point in points:
		maximum_planar_excursion = maxf(maximum_planar_excursion, Vector2(point.x - points[0].x, point.z - points[0].z).length())
	var imported_yaw := rotations[-1].get_euler().y
	var imported_sign := "POSITIVE / RIGHT" if imported_yaw > 0.0 else "NEGATIVE / LEFT"
	var semantic := "MOVING TURN" if scenario.moving else "TURN IN PLACE"
	var blend_sign := "+1 RIGHT" if scenario.blend.x > 0.0 else "-1 LEFT"
	var title := "%s — %s\nGraph point: %s  blend=(%.1f, %.1f) [%s]\nAnimation key: %s\nImported root yaw: %+.1f° [%s]  max planar: %.3fm\nActor: FROZEN yaw=0°; no ToActorYawDelta/RotateY\ntemporary=%s  replacement=%s" % [
		semantic,
		String(scenario.role).to_upper(),
		String(scenario.point),
		graph_position.x,
		graph_position.y,
		blend_sign,
		key,
		rad_to_deg(imported_yaw),
		imported_sign,
		maximum_planar_excursion,
		str(catalogue_entry.temporary),
		catalogue_entry.replacement_note if not String(catalogue_entry.replacement_note).is_empty() else "none",
	]
	_add_label(_scenario_visuals, title, Vector3(0.0, 0.12, 2.85), Color.WHITE, 30)
	print("TURN_AUTHORING_SAMPLE role=%s semantic=%s point=%s blend=%s key=%s temporary=%s replacement=%s root_yaw=%+.6f max_planar=%.6f lateral_x=%+.6f actor_yaw=%.6f conversion=not_called" % [scenario.role, semantic, scenario.point, graph_position, key, catalogue_entry.temporary, catalogue_entry.replacement_note, imported_yaw, maximum_planar_excursion, planar_delta.x, _actor.rotation.y])
	if scenario.moving and maximum_planar_excursion <= 0.5:
		SceneUtils.fatal_error_and_quit("Moving-turn imported trajectory is not visibly translational")
	elif scenario.moving and planar_delta.x * -scenario.blend.x <= 0.05:
		SceneUtils.fatal_error_and_quit("Moving-turn imported trajectory does not curve towards the authored rig-local side")
	elif not scenario.moving and maximum_planar_excursion >= 0.2:
		SceneUtils.fatal_error_and_quit("Turn-in-place imported trajectory has excessive planar translation")
	if imported_yaw * scenario.blend.x <= 0.1:
		SceneUtils.fatal_error_and_quit("Imported root yaw sign contradicts the exact graph blend sign")
	if not is_zero_approx(_actor.rotation.y):
		SceneUtils.fatal_error_and_quit("Physical actor yaw changed during pre-conversion authoring sampling")


func _add_polyline(parent: Node3D, points: PackedVector3Array, color: Color, width: float) -> void:
	for index in range(points.size() - 1):
		_add_line_prism(parent, points[index], points[index + 1], color, width)


func _add_arrow(parent: Node3D, origin: Vector3, direction: Vector3, color: Color, width: float) -> void:
	var finish := origin + direction
	_add_line_prism(parent, origin, finish, color, width)
	var planar := Vector3(direction.x, 0.0, direction.z).normalized()
	var side := Vector3(-planar.z, 0.0, planar.x)
	_add_line_prism(parent, finish, finish - planar * 0.22 + side * 0.13, color, width)
	_add_line_prism(parent, finish, finish - planar * 0.22 - side * 0.13, color, width)


func _add_line_prism(parent: Node3D, start: Vector3, finish: Vector3, color: Color, width: float) -> void:
	var midpoint := (start + finish) * 0.5
	var length := start.distance_to(finish)
	if length <= 0.0001:
		return
	var mesh := BoxMesh.new()
	mesh.size = Vector3(width, width, length)
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	material.albedo_color = color
	mesh.material = material
	var instance := MeshInstance3D.new()
	instance.mesh = mesh
	instance.position = midpoint
	instance.look_at_from_position(midpoint, finish, Vector3.UP)
	parent.add_child(instance)


func _add_label(parent: Node3D, text: String, position: Vector3, color: Color, font_size: int) -> void:
	var label := Label3D.new()
	label.text = text
	label.position = position
	label.font_size = font_size
	label.pixel_size = 0.003
	label.modulate = color
	label.outline_size = 8
	label.no_depth_test = true
	label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	parent.add_child(label)
