extends CanvasLayer

@onready var _voice: Node = get_node_or_null("../AIVoice")
@onready var _dialogue_input: TextEdit = $PanelContainer/MarginContainer/VBoxContainer/DialogueInput
@onready var _speak_button: Button = $PanelContainer/MarginContainer/VBoxContainer/ButtonRow/SpeakButton
@onready var _status_label: Label = $PanelContainer/MarginContainer/VBoxContainer/StatusLabel


func _ready() -> void:
	_speak_button.pressed.connect(_on_speak_requested)
	_status_label.text = "Enter dialogue text and press Speak or Ctrl+Enter."

	if _voice != null and _voice.has_signal("SpeechFailed"):
		_voice.connect("SpeechFailed", _on_speech_failed)


func _unhandled_input(event: InputEvent) -> void:
	if not (event is InputEventKey):
		return

	if not event.pressed or event.echo:
		return

	if not event.ctrl_pressed:
		return

	if event.keycode != KEY_ENTER and event.keycode != KEY_KP_ENTER:
		return

	get_viewport().set_input_as_handled()
	_on_speak_requested()


func _on_speak_requested() -> void:
	if _voice == null:
		push_error("Voice test control: AIVoice node was not found.")
		_status_label.text = "Voice node missing."
		return

	var dialogue: String = _dialogue_input.text.strip_edges()
	if dialogue.is_empty():
		_status_label.text = "Enter dialogue before speaking."
		return

	_status_label.text = "Speaking: %s" % dialogue

	_voice.call("Speak", dialogue)


func _on_speech_failed(error: String) -> void:
	_status_label.text = "Speech failed: %s" % error
