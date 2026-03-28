---
name: godot-asset-ops
description: Manipulate Godot scenes and resources safely through executable GDScript automation. Use this whenever a request involves creating or editing .tscn/.scn/.tres/.res assets, adding or removing nodes, changing node properties, wiring resources, bulk scene refactors, or any task where direct text edits of Godot assets would be brittle. Prefer this skill even when the user asks for "just update the scene file", because Godot API-driven changes are safer and more reliable.
---

# Godot Scene and Resource Operations

Use this skill to perform scene/resource modifications by running Godot API code, not by hand-editing serialised scene
text.

## Why this approach

- Godot serialised files are easy to corrupt with manual edits.
- API-based edits keep node ownership and resource structure valid.
- The workflow is repeatable for similar tasks.

## Workflow

1. Clarify the exact asset targets and intended outcome.
2. Use Context7 to confirm any uncertain Godot API details.
3. Write a temporary GDScript runner under `@.opencode/temp` for the concrete task.
4. Execute the script with Godot in headless mode.
5. Verify results (load/save success, expected nodes/resources present).
6. Remove the temporary script when finished unless the user asks to keep it.

## Temporary script requirement

Always write a task-specific GDScript file to `@.opencode/temp` before making scene/resource changes.

- Path pattern: `@.opencode/temp/asset_ops_<short-task-name>.gd`
- Do not patch `.tscn`/`.tres` directly when this skill applies.
- Keep each script focused on one operation bundle.

If `@.opencode/temp` does not exist, create it first.

## Script template

Use a runner script shaped like this and adapt the body to the task:

```gdscript
extends SceneTree

func _init() -> void:
    var exit_code := 0

    # 1) Load target scene/resource.
    # 2) Apply edits (create nodes/resources, set properties, reparent, etc.).
    # 3) Save with ResourceSaver.save(...).
    # 4) Print a concise success/failure message.

    # Example skeleton:
    # var packed: PackedScene = load("res://assets/ui/splash_screen.tscn")
    # if packed == null:
    #     push_error("Failed to load scene")
    #     quit(1)
    #     return
    # var root := packed.instantiate()
    # ... mutate root/resources ...
    # var out := PackedScene.new()
    # var pack_result := out.pack(root)
    # if pack_result != OK:
    #     push_error("Pack failed: %s" % pack_result)
    #     quit(1)
    #     return
    # var save_result := ResourceSaver.save(out, "res://assets/ui/splash_screen.tscn")
    # if save_result != OK:
    #     push_error("Save failed: %s" % save_result)
    #     quit(1)
    #     return

    quit(exit_code)
```

Important implementation notes:

- When packing a scene, ensure nodes that must persist have correct `owner`.
- Check return codes (`OK`) from `pack()` and `ResourceSaver.save()`.
- Fail fast with clear error messages if loads/saves fail.

## Execution

Run the temporary script via Godot headless, using the project at `game/`.

Example command:

```bash
godot-mono -d -s --headless --xr-mode off --path game "path/to/temp/asset_ops_<task>.gd"
```

Use absolute script paths to avoid ambiguity.

## Scope of operations

Use this skill for operations such as:

- Creating new scenes from node hierarchies and saving them.
- Loading existing scenes, adding/removing/reparenting nodes.
- Updating node/resource properties that should persist to disk.
- Creating or editing `.tres`/`.res` assets (materials, themes, custom resources).
- Applying structured, repeatable bulk changes across assets.

## Verification checklist

- Script exits with code `0`.
- Expected files are written at intended paths.
- Modified scenes/resources can be loaded again successfully.
- Requested nodes/properties/resources are present after save.

## Output expectations

After execution, report:

- Which temporary script path was used in `@.opencode/temp`.
- Which assets were changed.
- What was validated.
- Any follow-up action the user should take (for example, opening the scene in editor).
