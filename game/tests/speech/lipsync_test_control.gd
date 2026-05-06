extends CanvasLayer

const SPEECH_CLIP: AudioStreamWAV = preload("res://assets/audio/samples/sample-voice.wav")

@onready var _lip_sync_player: Node = get_node_or_null("../LipSyncPlayer")
@onready var _play_button: Button = $MarginContainer/PlayButton

var _manual_play_request_count: int = 0
var _last_requested_speech_clip: AudioStreamWAV = null

func _ready() -> void:
	_play_button.text = "Play / Restart Speech"
	_play_button.pressed.connect(_on_play_button_pressed)


func _on_play_button_pressed() -> void:
	if _lip_sync_player == null:
		push_error("Lip-sync test control: LipSyncPlayer node was not found.")
		return

	_manual_play_request_count += 1
	_last_requested_speech_clip = SPEECH_CLIP
	_lip_sync_player.call("Play", SPEECH_CLIP)


func trigger_test_play() -> void:
	_on_play_button_pressed()


func get_manual_play_request_count() -> int:
	return _manual_play_request_count


func has_requested_speech_clip() -> bool:
	return _last_requested_speech_clip != null
