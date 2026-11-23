extends SceneTree

# The roles install the reference female through the production rig role installer and its template-subtree, body
# parts, and dynamic physical rig installers only. Their templates clear MotionModel, so no MotionBricks runtime is
# mounted, and they carry no navigation, mind, speech, or voice wiring.
const FIXTURE_SCENE_PATH := "res://tests/ik/bone_attachment_fixture/bone_attachment_fixture.tscn"
const PLAYER_ROLE_SCENE_PATH := "res://tests/ik/bone_attachment_fixture/bone_attachment_player.tscn"
const NPC_ROLE_SCENE_PATH := "res://tests/ik/bone_attachment_fixture/bone_attachment_npc.tscn"
const STAGE_PREFIX := "ALLEYCAT_BONE_TRANSCRIPT_STAGE:"
const SKELETON_PATH := ^"Female/GeneralSkeleton"
const AUTHORED_ATTACHMENTS: Array[String] = ["Head", "RightHand", "LeftHand", "Neck"]


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	print(STAGE_PREFIX + "fixture-load-start")
	var fixture_scene := load(FIXTURE_SCENE_PATH) as PackedScene
	if fixture_scene == null:
		_fail("Failed to load the bone attachment fixture.")
		return
	print(STAGE_PREFIX + "fixture-load-complete")

	var fixture := fixture_scene.instantiate()
	var actors := fixture.get_node_or_null("Actors") as Node
	if actors == null:
		fixture.free()
		_fail("The bone attachment fixture did not contain its Actors root.")
		return
	print(STAGE_PREFIX + "fixture-instantiated")

	root.add_child(fixture)
	await process_frame
	print(STAGE_PREFIX + "fixture-attached")

	var player := _instantiate_role(PLAYER_ROLE_SCENE_PATH)
	if player == null:
		return
	print(STAGE_PREFIX + "player-role-instantiated")
	actors.add_child(player)
	print(STAGE_PREFIX + "player-role-attached")

	var npc := _instantiate_role(NPC_ROLE_SCENE_PATH)
	if npc == null:
		return
	print(STAGE_PREFIX + "npc-role-instantiated")
	actors.add_child(npc)
	print(STAGE_PREFIX + "npc-role-attached")
	await process_frame
	if not _verify_installed_attachments(player) or not _verify_installed_attachments(npc):
		return
	print(STAGE_PREFIX + "roles-ready")
	await physics_frame
	print(STAGE_PREFIX + "initial-physics-frame-1")
	await physics_frame
	print(STAGE_PREFIX + "initial-physics-frame-2")

	fixture.queue_free()
	await process_frame
	print(STAGE_PREFIX + "complete")
	quit(0)


func _instantiate_role(scene_path: String) -> Node:
	var scene := load(scene_path) as PackedScene
	if scene == null:
		_fail("Failed to load role scene '%s'." % scene_path)
		return null
	return scene.instantiate()


# Guards against a vacuous transcript: each role must have received the template's bone attachments and dynamic
# physical rig through its installer, on the imported skeleton rather than a MotionBricks retarget mount.
func _verify_installed_attachments(role: Node) -> bool:
	var skeleton := role.get_node_or_null(SKELETON_PATH) as Skeleton3D
	if skeleton == null:
		_fail("Role '%s' has no skeleton at '%s'." % [role.name, SKELETON_PATH])
		return false
	if skeleton.get_parent() is RetargetModifier3D:
		_fail("Role '%s' unexpectedly mounted a MotionBricks retarget rig." % role.name)
		return false
	for attachment_name in AUTHORED_ATTACHMENTS:
		var attachment := skeleton.get_node_or_null(NodePath(attachment_name)) as BoneAttachment3D
		if attachment == null:
			_fail("Role '%s' did not receive bone attachment '%s'." % [role.name, attachment_name])
			return false
		if attachment.get_bone_idx() < 0:
			_fail("Role '%s' bone attachment '%s' has no resolved bone." % [role.name, attachment_name])
			return false
	if skeleton.get_node_or_null(^"DynamicPhysicalRig") == null:
		_fail("Role '%s' did not receive its dynamic physical rig." % role.name)
		return false
	return true


func _fail(message: String) -> void:
	push_error(message)
	quit(1)
