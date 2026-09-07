extends SceneTree

const FIXTURE_SCENE_PATH := "res://assets/testing/reference_player_fixture/reference_player_fixture.tscn"
const PLAYER_ROLE_SCENE_PATH := "res://assets/characters/reference/ally_player.tscn"
const NPC_ROLE_SCENE_PATH := "res://assets/characters/reference/ally_npc.tscn"
const NPC_INSTALLER_PATH := "NPCCharacterInstaller"
const PLAYER_INSTALLER_PATH := "PlayerCharacterInstaller"
const STAGE_PREFIX := "ALLEYCAT_BONE_TRANSCRIPT_STAGE:"


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	print(STAGE_PREFIX + "fixture-load-start")
	var fixture_scene := load(FIXTURE_SCENE_PATH) as PackedScene
	if fixture_scene == null:
		_fail("Failed to load minimal reference-player fixture.")
		return
	print(STAGE_PREFIX + "fixture-load-complete")

	var fixture := fixture_scene.instantiate()
	var actors := fixture.get_node_or_null("Actors") as Node
	if actors == null:
		fixture.free()
		_fail("Minimal reference-player fixture did not contain its Actors root.")
		return
	print(STAGE_PREFIX + "fixture-instantiated")

	_remove_authored_role(actors, "Player")
	_remove_authored_role(actors, "Ally")
	root.add_child(fixture)
	await process_frame
	print(STAGE_PREFIX + "fixture-attached")

	var player_scene := load(PLAYER_ROLE_SCENE_PATH) as PackedScene
	if player_scene == null:
		_fail("Failed to load player role scene.")
		return
	var player := player_scene.instantiate()
	player.set("Id", "reference_player")
	_isolate_player_attachment_context(player)
	print(STAGE_PREFIX + "player-role-instantiated")
	actors.add_child(player)
	print(STAGE_PREFIX + "player-role-attached")

	var npc_scene := load(NPC_ROLE_SCENE_PATH) as PackedScene
	if npc_scene == null:
		_fail("Failed to load NPC role scene.")
		return
	var npc := npc_scene.instantiate()
	npc.set("Id", "ally")
	if not _isolate_npc_attachment_context(npc):
		npc.free()
		return
	print(STAGE_PREFIX + "npc-role-instantiated")
	actors.add_child(npc)
	print(STAGE_PREFIX + "npc-role-attached")
	await process_frame
	print(STAGE_PREFIX + "roles-ready")
	await physics_frame
	print(STAGE_PREFIX + "initial-physics-frame-1")
	await physics_frame
	print(STAGE_PREFIX + "initial-physics-frame-2")

	fixture.queue_free()
	await process_frame
	print(STAGE_PREFIX + "complete")
	quit(0)


func _isolate_player_attachment_context(player: Node) -> void:
	# The transcript validates bone attachment only. The role template's speech transcriber monitors the
	# microphone automatically once attached, and a live microphone stream at exit pollutes the asserted engine
	# transcript with unrelated audio teardown (PulseAudio disconnect errors under the default driver, leaked
	# microphone playback instances under the Dummy driver). Speech input is therefore disabled in the
	# attachment-only template before the role is attached.
	var installer := player.get_node_or_null(PLAYER_INSTALLER_PATH)
	if installer == null:
		_fail("Reference player role did not contain its character installer.")
		return

	var template_scene := installer.get("Template") as PackedScene
	if template_scene == null:
		_fail("Reference player character installer did not contain its role template.")
		return

	var template := template_scene.instantiate()
	var transcriber := template.get_node_or_null("OpenAITranscriber")
	if transcriber == null:
		template.free()
		return

	transcriber.set("Enabled", false)
	var attachment_only_template := PackedScene.new()
	var pack_result := attachment_only_template.pack(template)
	template.free()
	if pack_result != OK:
		_fail("Failed to create the attachment-only player role template: %s." % error_string(pack_result))
		return

	installer.set("Template", attachment_only_template)


func _remove_authored_role(actors: Node, role_name: String) -> void:
	var authored_role := actors.get_node_or_null(role_name)
	if authored_role == null:
		_fail("Minimal reference-player fixture did not contain authored role '%s'." % role_name)
		return
	actors.remove_child(authored_role)
	authored_role.free()


func _isolate_npc_attachment_context(npc: Node) -> bool:
	var installer := npc.get_node_or_null(NPC_INSTALLER_PATH)
	if installer == null:
		_fail("Reference NPC role did not contain its character installer.")
		return false

	var template_scene := installer.get("Template") as PackedScene
	if template_scene == null:
		_fail("Reference NPC character installer did not contain its role template.")
		return false

	var template := template_scene.instantiate()
	var mind := template.get_node_or_null("Mind")
	if mind == null:
		template.free()
		_fail("Reference NPC role template did not contain its AgenticMind node.")
		return false

	var character_ik := template.get_node_or_null("CharacterIK")
	if character_ik == null:
		template.free()
		_fail("Reference NPC role template did not contain its character IK controller.")
		return false

	# The transcript only validates bone-attachment setup. Keep the role's normal installer and all attachment
	# context, but remove its unrelated production mind subtree. CharacterIK's sole Mind dependency is intentionally
	# cleared first so the attachment-only template has no dangling authored NodePath reference.
	character_ik.set("HeadTargetIntentProvider", null)
	template.remove_child(mind)
	mind.free()
	var attachment_only_template := PackedScene.new()
	var pack_result := attachment_only_template.pack(template)
	template.free()
	if pack_result != OK:
		_fail("Failed to create the attachment-only NPC role template: %s." % error_string(pack_result))
		return false

	installer.set("Template", attachment_only_template)
	return true


func _fail(message: String) -> void:
	push_error(message)
	quit(1)
