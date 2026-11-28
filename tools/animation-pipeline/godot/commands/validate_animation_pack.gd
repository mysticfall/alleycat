extends SceneTree

# Headless ANIM-003 animation pack validator. Runs the game's single pack validation (AnimationPackLoader) through
# AnimationPackValidationCommand and never modifies authored files. Requires the game's C# assembly to be built.
#
# Usage (user arguments after `--`):
#   --pack=<res:// pack.json> [--pack=...] [--content-root=<res:// content-pack root>]
#   [--template=<res:// character template scene>]... [--priority=<int>] [--require-compatible]
#
# Exit status: 0 valid, 1 invalid (or incompatible with --require-compatible), 2 bad arguments.

const COMMAND := "res://src/Animations/AnimationPackValidationCommand.cs"


func _init() -> void:
	var script := load(COMMAND) as Script
	if script == null:
		printerr("ANIMATION_PACK_VALIDATION_ERROR: %s is unavailable; build the game's C# project first." % COMMAND)
		quit(2)
		return
	var command: Object = script.new()
	var status: int = command.Run(PackedStringArray(OS.get_cmdline_user_args()))
	quit(status)
