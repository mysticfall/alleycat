"""Build a disposable control-rig authoring blend for the ANIM-003 rig-path export test.

Run inside Blender: ``blender --background --factory-startup --python this.py -- <reference.blend> <output.blend>``.

The reference armature is appended from the character blend. A control rig is a copy of it (standing in for a
Rigify rig built over the reference skeleton), and every reference bone follows its control bone through a world-space
Copy Transforms constraint, so the reference deform bones are what the export samples. One action on the control rig
rotates ``upperarm_l`` about its local X axis by ``AMPLITUDE_DEGREES`` at the middle frame and back, and sways the
pelvis. A second action keys the same span without motion. Nothing is written except the output blend.
"""
from __future__ import annotations

import math
import sys

import bpy

REFERENCE_OBJECT = "Female"
CONTROL_RIG = "Controls"
ACTION = "rig_probe_sway"
STILL_ACTION = "rig_probe_still"
FIRST, MIDDLE, LAST = 1, 16, 31
AMPLITUDE_DEGREES = 30.0


def main() -> None:
    reference_blend, output = sys.argv[sys.argv.index("--") + 1:]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    with bpy.data.libraries.load(reference_blend, link=False) as (available, wanted):
        wanted.objects = [REFERENCE_OBJECT]
    reference = wanted.objects[0]
    bpy.context.scene.collection.objects.link(reference)
    bpy.context.scene.render.fps = 30
    reference.animation_data_clear()
    for bone in reference.pose.bones:
        for constraint in list(bone.constraints):
            bone.constraints.remove(constraint)
    control = reference.copy()
    control.data = reference.data.copy()
    control.name = CONTROL_RIG
    bpy.context.scene.collection.objects.link(control)
    for bone in reference.pose.bones:
        constraint = bone.constraints.new("COPY_TRANSFORMS")
        constraint.target = control
        constraint.subtarget = bone.name
        constraint.owner_space = constraint.target_space = "WORLD"
    action = bpy.data.actions.new(ACTION)
    action.use_fake_user = True
    control.animation_data_create().action = action
    arm = control.pose.bones["upperarm_l"]
    pelvis = control.pose.bones["pelvis"]
    arm.rotation_mode = pelvis.rotation_mode = "QUATERNION"
    for frame, fraction in ((FIRST, 0.0), (MIDDLE, 1.0), (LAST, 0.0)):
        half = math.radians(AMPLITUDE_DEGREES * fraction) / 2.0
        arm.rotation_quaternion = (math.cos(half), math.sin(half), 0.0, 0.0)
        arm.keyframe_insert("rotation_quaternion", frame=frame)
        pelvis.location = (0.02 * fraction, 0.0, 0.0)
        pelvis.keyframe_insert("location", frame=frame)
    # Keys spanning the same duration that never move: the export must refuse it as frozen.
    still = bpy.data.actions.new(STILL_ACTION)
    still.use_fake_user = True
    control.animation_data.action = still
    arm.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
    for frame in (FIRST, LAST):
        arm.keyframe_insert("rotation_quaternion", frame=frame)
    control.animation_data.action = None
    bpy.ops.wm.save_as_mainfile(filepath=output, compress=False)
    print("CONTROL_RIG_FIXTURE=" + output)


main()
