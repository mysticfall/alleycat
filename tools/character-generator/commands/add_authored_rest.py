"""One-time migration of an open main or collider blend; invoke with Blender -b."""

import sys
from pathlib import Path

import bpy

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from character_generator.authored_rest import add_authored_rest

path = Path(bpy.data.filepath)
if not path.is_file():
    raise ValueError("Expected a saved character blend")
rig, = (obj for obj in bpy.context.scene.objects if obj.type == "ARMATURE")
add_authored_rest(rig)
bpy.context.preferences.filepaths.save_version = 0
bpy.ops.wm.save_as_mainfile(filepath=str(path), compress=True)
