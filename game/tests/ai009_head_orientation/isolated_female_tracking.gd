extends Node3D

# Dedicated fixture: instantiate ONLY the imported model, then select head/eye authoring from
# SceneState without instantiating either character template or running a role installer.
func _init() -> void:
	name = "IsolatedFemaleTracking"
	var source: PackedScene = load("res://assets/characters/templates/reference_female/reference_female_base.tscn")
	var state := source.get_state()
	var imported: Node = load("res://assets/characters/reference/female/reference_female.blend").instantiate()
	var model: Node3D = imported.get_node("Female")
	imported.remove_child(model)
	model.owner = null
	add_child(model)
	model.rotation.y = PI
	imported.free()
	var selected := ["Female/GeneralSkeleton/Head", "Female/GeneralSkeleton/Head/Viewpoint", "Female/GeneralSkeleton/Head/Eyes", "Female/GeneralSkeleton/Head/Eyes/RightEye", "Female/GeneralSkeleton/Head/Eyes/LeftEye", "Female/GeneralSkeleton/NeckSpineIK", "Female/GeneralSkeleton/HeadCopyRotation", "Female/GeneralSkeleton/NeckTwistDisperser", "Female/GeneralSkeleton/Neck", "Female/GeneralSkeleton/Neck/NeckPivot", "IKTargets", "IKTargets/Head", "IKTargets/HeadSolve"]
	for i in state.get_node_count():
		var path := String(state.get_node_path(i))
		if path.begins_with("./"):
			path = path.substr(2)
		if not path in selected:
			continue
		var instance := state.get_node_instance(i)
		var node: Node = instance.instantiate() if instance != null else ClassDB.instantiate(state.get_node_type(i))
		node.name = state.get_node_name(i)
		get_node(path.get_base_dir() if path.contains("/") else ".").add_child(node)
		for j in state.get_node_property_count(i):
			var property := state.get_node_property_name(i, j)
			if property != "unique_name_in_owner":
				node.set(property, state.get_node_property_value(i, j))
	for side in ["LeftHand", "RightHand"]:
		var body := AnimatableBody3D.new()
		body.name = side
		body.collision_layer = 0
		body.collision_mask = 0
		get_node("IKTargets").add_child(body)
	var skeleton: Skeleton3D = get_node("Female/GeneralSkeleton")
	skeleton.reset_bone_poses()
	# Declared constant upstream rest animation. Engine animation precedes modifiers; no
	# post-solve callback overwrites bones, and release exercises real influence decay.
	var neutral := Animation.new()
	neutral.length = 1.0
	neutral.loop_mode = Animation.LOOP_LINEAR
	for i in skeleton.get_bone_count():
		var track := neutral.add_track(Animation.TYPE_ROTATION_3D)
		neutral.track_set_path(track, NodePath("Female/GeneralSkeleton:" + skeleton.get_bone_name(i)))
		neutral.rotation_track_insert_key(track, 0.0, skeleton.get_bone_rest(i).basis.get_rotation_quaternion())
	var library := AnimationLibrary.new()
	library.add_animation("Neutral", neutral)
	var player := AnimationPlayer.new()
	player.name = "UpstreamNeutral"
	player.add_animation_library("", library)
	player.autoplay = "Neutral"
	add_child(player)
