@tool
extends EditorScenePostImport

const CharacterEyeAnimationImport := preload("res://assets/characters/import/character_eye_animation_import.gd")
const CharacterColliderProfileImport := preload("res://assets/characters/import/character_collider_profile_import.gd")


func _post_import(scene: Node) -> Node:
	CharacterEyeAnimationImport.new().apply(scene)
	CharacterColliderProfileImport.new().apply(_get_import_source_file())
	return scene


func _get_import_source_file() -> String:
	if has_method("get_source_file"):
		return call("get_source_file") as String

	push_warning("Character import could not call EditorScenePostImport.get_source_file(); collider profile generation is skipped.")
	return ""
