extends RefCounted
## Restructure before the model enters the tree, keeping authored node references on the visible rig.
func install(actor: Node, model_name: String, player_role: bool) -> void:
	var model := actor.get_node(model_name)
	if model.has_node("SOMA/Native/GeneralSkeleton"):
		return
	var target := model.get_node("GeneralSkeleton") as Skeleton3D
	var references := []
	for node in [actor] + actor.find_children("*", "", true, false):
		for prop in node.get_property_list():
			if prop.type != TYPE_NODE_PATH or not (prop.usage & PROPERTY_USAGE_STORAGE):
				continue
			var path: NodePath = node.get(prop.name)
			if path.is_empty() or path.is_absolute() or path.get_subname_count():
				continue
			var dest: Node = node.get_node_or_null(path)
			if dest:
				references.append([node, prop.name, dest])
	var source: Skeleton3D = load("res://data/motionbricks/source-rest/%s.tscn" % model_name.to_lower()).instantiate()
	source.name = "SOMA"
	model.add_child(source)
	source.reset_bone_poses()
	source.modifier_callback_mode_process = Skeleton3D.MODIFIER_CALLBACK_MODE_PROCESS_PHYSICS
	var native := RetargetModifier3D.new()
	native.name = "Native"
	native.enable = 3 # Local position and rotation; do not transfer scale.
	var humanoid := SkeletonProfileHumanoid.new()
	native.profile = humanoid
	if player_role:
		var body := SkeletonProfile.new()
		for i in humanoid.bone_size:
			var name_: String = humanoid.get_bone_name(i)
			if not (name_.ends_with("Distal") or name_.ends_with("Proximal") or name_.ends_with("Intermediate") or name_.ends_with("Metacarpal")):
				body.bone_size += 1
				body.set_bone_name(body.bone_size - 1, name_)
		native.profile = body
	native.active = false
	source.add_child(native)
	var original_owner := target.owner
	target.owner = null
	target.reparent(native, false)
	target.owner = original_owner
	for binding in references:
		binding[0].set(binding[1], binding[0].get_path_to(binding[2]))
	var player := actor.get_node("AnimationPlayer") as AnimationPlayer
	for lib_name in player.get_animation_library_list():
		var old := player.get_animation_library(lib_name)
		var library := AnimationLibrary.new()
		for clip in old.get_animation_list():
			var animation := old.get_animation(clip)
			var changed := false
			for i in animation.get_track_count():
				var path := str(animation.track_get_path(i))
				if path.begins_with("GeneralSkeleton/") or path.begins_with("GeneralSkeleton:"):
					if not changed:
						animation = animation.duplicate()
						changed = true
					animation.track_set_path(i, NodePath("SOMA/Native/" + path))
			library.add_animation(clip, animation)
		player.remove_animation_library(lib_name)
		player.add_animation_library(lib_name, library)
	var installer := actor.get_node("PlayerCharacterInstaller" if player_role else "NPCCharacterInstaller")
	installer.set("TargetSkeletonPath", actor.get_path_to(target))
