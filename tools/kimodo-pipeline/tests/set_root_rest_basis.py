"""Rotate the Kimodo Source Root rest basis in the currently opened disposable blend."""
from __future__ import annotations

import math
import sys

import bpy
from mathutils import Matrix


def main() -> None:
    output = sys.argv[sys.argv.index("--") + 1]
    rig = next(
        obj
        for obj in bpy.data.objects
        if obj.type == "ARMATURE" and obj.name.replace("_", " ").lower() == "kimodo source"
    )
    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    root = rig.data.edit_bones["Root"]
    root.matrix = Matrix.Translation(root.head) @ Matrix.Rotation(math.pi / 2, 4, "Z")
    bpy.ops.object.mode_set(mode="OBJECT")
    bpy.ops.wm.save_as_mainfile(filepath=output, check_existing=False)


main()
