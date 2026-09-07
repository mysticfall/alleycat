"""Add an invalid pose-bone F-curve to a dynamically selected source Action."""
from __future__ import annotations

import sys

import bpy


def main() -> None:
    blend = sys.argv[sys.argv.index("--") + 1]
    bpy.ops.wm.open_mainfile(filepath=blend, load_ui=False, use_scripts=False)
    action = next(
        action
        for action in sorted(bpy.data.actions, key=lambda item: item.name)
        if action.name.startswith("kimodo_multi_")
    )
    fcurves = getattr(action, "fcurves", None)
    if fcurves is not None:
        fcurves.new('pose.bones["__kimodo_invalid_pose_target__"].location', index=0)
    else:
        channelbag = next(
            channelbag
            for layer in action.layers
            for strip in layer.strips
            for channelbag in strip.channelbags
        )
        channelbag.fcurves.new('pose.bones["__kimodo_invalid_pose_target__"].location', index=0)
    bpy.ops.wm.save_as_mainfile(filepath=blend, check_existing=False)


main()
