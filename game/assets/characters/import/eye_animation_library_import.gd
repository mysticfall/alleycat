@tool
extends EditorScenePostImport

const CharacterEyeAnimationImport := preload("res://assets/characters/import/character_eye_animation_import.gd")


func _post_import(scene: Node) -> Node:
	CharacterEyeAnimationImport.new().apply(scene)
	return scene
