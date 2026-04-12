---
name: godot-ui
description: Additional guidelines for creating Godot UI screens and controls.
---

# Godot UI Guidelines

Use this skill for UI tasks in this repository.

## Full-screen UI

For any UI intended to fill the player's view:

- Size must be **1152x648**.
- Background must be **transparent** (no background colour).

## Theme

All `Control` nodes must use `@game/assets/ui/themes/default.tres` as their theme. Follow the GDScript workflow in
[godot-asset-ops](godot-asset-ops/SKILL.md) to apply the theme by setting the `theme` property on each Control node:

```gdscript
extends SceneTree

func _init() -> void:
    var packed := load("res://assets/ui/some_screen.tscn") as PackedScene
    var root := packed.instantiate() as Control
    root.theme = load("res://assets/ui/themes/default.tres")

    # Apply to all child Control nodes.
    for child in root.get_children():
        if child is Control:
            child.theme = root.theme

    # Save.
    var out := PackedScene.new()
    out.pack(root)
    ResourceSaver.save(out, "res://assets/ui/some_screen.tscn")
    quit(0)
```
