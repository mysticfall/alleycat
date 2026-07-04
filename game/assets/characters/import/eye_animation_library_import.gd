@tool
extends EditorScenePostImport

const EYES_LIBRARY_NAME := &"eyes"
const EYE_ANIMATION_CONTRACT_PLACEHOLDER_NAME := "EyeAnimationContractPlaceholder"
const BLINK_ANIMATION_NAME := &"Eyes Blink"
const HORIZONTAL_LOOK_ANIMATION_NAME := &"Eyes Right Left"
const VERTICAL_LOOK_ANIMATION_NAME := &"Eyes Up Down"

const BLINK_BLEND_SHAPES := [&"eyeBlinkLeft", &"eyeBlinkRight"]
const REQUIRED_EYE_BLEND_SHAPES := [
	&"eyeBlinkLeft",
	&"eyeBlinkRight",
	&"eyeLookInRight",
	&"eyeLookInLeft",
	&"eyeLookUpRight",
	&"eyeLookDownRight",
]
const HORIZONTAL_LOOK_BLEND_SHAPES := {
	# Matches the legacy normalised 0.0 -> left, 0.5 -> neutral, 1.0 -> right
	# timeline used by the AnimationTree time-seek controller.
	&"eyeLookInRight": [0.0, 0.0, 1.0],
	&"eyeLookInLeft": [1.0, 0.0, 0.0],
	&"eyeLookOutRight": [1.0, 0.0, 0.0],
	&"eyeLookOutLeft": [0.0, 0.0, 1.0],
}
const VERTICAL_LOOK_BLEND_SHAPES := {
	# Matches the legacy normalised 0.0 -> up, 0.5 -> neutral, 1.0 -> down
	# timeline used by the AnimationTree time-seek controller.
	&"eyeLookUpRight": [1.0, 0.0, 0.0],
	&"eyeLookUpLeft": [1.0, 0.0, 0.0],
	&"eyeLookDownRight": [0.0, 0.0, 1.0],
	&"eyeLookDownLeft": [0.0, 0.0, 1.0],
}


func _post_import(scene: Node) -> Node:
	var animation_player := _ensure_animation_player(scene)

	var skeleton := _find_first_node_of_type(scene, "Skeleton3D") as Skeleton3D
	var eye_tracks: Array[Dictionary] = []
	if skeleton == null or skeleton.get_parent() == null:
		push_warning("Eye animation import created an empty '%s' AnimationLibrary because the imported scene has no Skeleton3D with a model root parent." % EYES_LIBRARY_NAME)
	else:
		var model_root := skeleton.get_parent()
		# Runtime installation rebases the AnimationPlayer to this same model root so
		# generated blend-shape tracks are resolvable both in imported/inherited scenes
		# and after installer materialisation.
		animation_player.root_node = animation_player.get_path_to(model_root)

		eye_tracks = _collect_eye_tracks(model_root)
		if eye_tracks.is_empty():
			_create_eye_animation_contract_placeholder(scene, model_root)
			eye_tracks = _collect_eye_tracks(model_root)
			push_warning("Eye animation import created placeholder eye blend-shape tracks for '%s' because no relevant eye blend shapes were found below '%s'." % [EYES_LIBRARY_NAME, model_root.name])

	var library := AnimationLibrary.new()
	library.add_animation(BLINK_ANIMATION_NAME, _build_blink_animation(eye_tracks))
	library.add_animation(HORIZONTAL_LOOK_ANIMATION_NAME, _build_look_animation(HORIZONTAL_LOOK_ANIMATION_NAME, eye_tracks, HORIZONTAL_LOOK_BLEND_SHAPES))
	library.add_animation(VERTICAL_LOOK_ANIMATION_NAME, _build_look_animation(VERTICAL_LOOK_ANIMATION_NAME, eye_tracks, VERTICAL_LOOK_BLEND_SHAPES))

	if animation_player.has_animation_library(EYES_LIBRARY_NAME):
		animation_player.remove_animation_library(EYES_LIBRARY_NAME)

	var result := animation_player.add_animation_library(EYES_LIBRARY_NAME, library)
	if result != OK:
		push_error("Eye animation import failed to add AnimationLibrary '%s': %s" % [EYES_LIBRARY_NAME, result])

	return scene


func _create_eye_animation_contract_placeholder(scene: Node, model_root: Node) -> void:
	var existing_placeholder := model_root.get_node_or_null(EYE_ANIMATION_CONTRACT_PLACEHOLDER_NAME)
	if existing_placeholder != null:
		existing_placeholder.queue_free()
		model_root.remove_child(existing_placeholder)

	var placeholder := MeshInstance3D.new()
	placeholder.name = EYE_ANIMATION_CONTRACT_PLACEHOLDER_NAME
	placeholder.visible = false
	placeholder.mesh = _create_eye_animation_contract_mesh()
	model_root.add_child(placeholder)
	placeholder.owner = scene


func _create_eye_animation_contract_mesh() -> ArrayMesh:
	var mesh := ArrayMesh.new()
	for blend_shape_name in REQUIRED_EYE_BLEND_SHAPES:
		mesh.add_blend_shape(blend_shape_name)

	var vertices := PackedVector3Array([
		Vector3.ZERO,
		Vector3.ZERO,
		Vector3.ZERO,
	])
	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = vertices

	var blend_shapes: Array[Array] = []
	for _blend_shape_name in REQUIRED_EYE_BLEND_SHAPES:
		var blend_shape_arrays := []
		blend_shape_arrays.resize(Mesh.ARRAY_MAX)
		blend_shape_arrays[Mesh.ARRAY_VERTEX] = vertices
		blend_shapes.append(blend_shape_arrays)

	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays, blend_shapes)
	return mesh


func _ensure_animation_player(scene: Node) -> AnimationPlayer:
	var animation_player := _find_first_node_of_type(scene, "AnimationPlayer") as AnimationPlayer
	if animation_player != null:
		return animation_player

	animation_player = AnimationPlayer.new()
	animation_player.name = "AnimationPlayer"
	scene.add_child(animation_player)
	animation_player.owner = scene
	push_warning("Eye animation import created missing AnimationPlayer on imported scene '%s'." % scene.name)
	return animation_player


func _collect_eye_tracks(model_root: Node) -> Array[Dictionary]:
	var tracks: Array[Dictionary] = []
	_collect_eye_tracks_recursive(model_root, model_root, tracks)
	return tracks


func _collect_eye_tracks_recursive(model_root: Node, node: Node, tracks: Array[Dictionary]) -> void:
	if node is MeshInstance3D and node.mesh != null:
		var mesh_instance := node as MeshInstance3D
		for blend_shape_index in mesh_instance.mesh.get_blend_shape_count():
			var blend_shape_name: StringName = mesh_instance.mesh.get_blend_shape_name(blend_shape_index)
			if _is_eye_blend_shape(blend_shape_name):
				tracks.append({
					"path": NodePath("%s:%s" % [model_root.get_path_to(mesh_instance), blend_shape_name]),
					"blend_shape": blend_shape_name,
				})

	for child in node.get_children():
		_collect_eye_tracks_recursive(model_root, child, tracks)


func _is_eye_blend_shape(blend_shape_name: StringName) -> bool:
	return blend_shape_name in BLINK_BLEND_SHAPES \
		or HORIZONTAL_LOOK_BLEND_SHAPES.has(blend_shape_name) \
		or VERTICAL_LOOK_BLEND_SHAPES.has(blend_shape_name)


func _build_blink_animation(eye_tracks: Array[Dictionary]) -> Animation:
	var animation := Animation.new()
	animation.resource_name = BLINK_ANIMATION_NAME
	animation.length = 0.3

	for eye_track in eye_tracks:
		if not eye_track["blend_shape"] in BLINK_BLEND_SHAPES:
			continue

		var track_index := _add_blend_shape_track(animation, eye_track["path"])
		animation.blend_shape_track_insert_key(track_index, 0.0, 0.0)
		animation.blend_shape_track_insert_key(track_index, 0.15, 1.0)
		animation.blend_shape_track_insert_key(track_index, 0.3, 0.0)

	return animation


func _build_look_animation(animation_name: StringName, eye_tracks: Array[Dictionary], shape_keys: Dictionary) -> Animation:
	var animation := Animation.new()
	animation.resource_name = animation_name
	animation.length = 1.0

	for eye_track in eye_tracks:
		var blend_shape_name: StringName = eye_track["blend_shape"]
		if not shape_keys.has(blend_shape_name):
			continue

		var keys: Array = shape_keys[blend_shape_name]
		var track_index := _add_blend_shape_track(animation, eye_track["path"])
		animation.blend_shape_track_insert_key(track_index, 0.0, keys[0])
		animation.blend_shape_track_insert_key(track_index, 0.5, keys[1])
		animation.blend_shape_track_insert_key(track_index, 1.0, keys[2])

	return animation


func _add_blend_shape_track(animation: Animation, track_path: NodePath) -> int:
	var track_index := animation.add_track(Animation.TYPE_BLEND_SHAPE)
	animation.track_set_path(track_index, track_path)
	animation.track_set_imported(track_index, true)
	return track_index


func _find_first_node_of_type(node: Node, class_name_to_find: String) -> Node:
	if node.is_class(class_name_to_find):
		return node

	for child in node.get_children():
		var match := _find_first_node_of_type(child, class_name_to_find)
		if match != null:
			return match

	return null
