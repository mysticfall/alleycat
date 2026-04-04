## Represents a 3D marker used by Photobooth for visual verification during scene checks.
@tool
class_name DebugMarker
extends Node3D

## Optional text shown on the marker label; when empty, the label is hidden.
@export var label_text: String = ""

func _ready() -> void:
	var label: Label3D = $Label3D

	if label_text == null or label_text.is_empty():
		label.visible = false

	label.text = label_text
