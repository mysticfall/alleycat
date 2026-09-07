"""Destructively scale a disposable Kimodo rig and every source motion Action."""
from __future__ import annotations

import json
from pathlib import Path
import sys

import bpy

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.anatomical_scale import is_anatomical_motion_bone
from kimodo_pipeline.compatibility import ensure_registered
from kimodo_pipeline.discovery import _action_fcurves
from kimodo_pipeline.materialise import _anatomical_character_scale, _rig


def main() -> None:
    input_path = Path(sys.argv[sys.argv.index("--") + 1]).resolve()
    output_path = Path(sys.argv[sys.argv.index("--") + 2]).resolve()
    scale = float(sys.argv[sys.argv.index("--") + 3])
    ensure_registered()
    bpy.ops.wm.open_mainfile(filepath=str(input_path), load_ui=False, use_scripts=False)
    rig, _, _ = _rig()
    before_scale = _anatomical_character_scale(rig)

    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    for bone in rig.data.edit_bones:
        bone.head *= scale
        bone.tail *= scale
    root = rig.data.edit_bones["Root"]
    root.tail = root.head + (root.tail - root.head) * .23
    helper = next(
        bone for bone in rig.data.edit_bones
        if not is_anatomical_motion_bone(bone.name, rig.data.bones[bone.name].use_deform)
        and bone.name != "Root"
    )
    helper.tail = helper.head + (helper.tail - helper.head) * 3.7
    root_length = root.length
    helper_name, helper_length = helper.name, helper.length
    bpy.ops.object.mode_set(mode="OBJECT")

    # This fixture measures destructive scale invariance from raw capture
    # content. Published outputs and prior role-confined derivatives carry
    # provenance measured at their original scale and must not become peers.
    for action in list(bpy.data.actions):
        if action.name.startswith("loc_") or action.get("anim002_source_content_repair"):
            bpy.data.actions.remove(action)

    # Bone-local translations are dimensional; rotations and all timing remain
    # unchanged. This scales authored motion together with the rest hierarchy.
    for action in bpy.data.actions:
        for curve in _action_fcurves(action):
            if curve.data_path.startswith('pose.bones[') and curve.data_path.endswith(".location"):
                for point in curve.keyframe_points:
                    point.co.y *= scale
                    point.handle_left.y *= scale
                    point.handle_right.y *= scale
                curve.update()
        action.update_tag()

    rig.data.bones["Root"].envelope_distance = before_scale * scale * 4.1
    rig.data.bones[helper_name].envelope_distance = before_scale * scale * .013
    bpy.context.view_layer.update()
    after_scale = _anatomical_character_scale(rig)
    assert abs(after_scale / before_scale - scale) <= 1e-5
    bpy.ops.wm.save_as_mainfile(filepath=str(output_path), check_existing=False)
    print("KIMODO_PIVOT_SCALE_FIXTURE=" + json.dumps({
        "uniform_scale": scale,
        "anatomical_scale_before": before_scale,
        "anatomical_scale_after": after_scale,
        "root_rest_length": root_length,
        "root_display_length": rig.data.bones["Root"].envelope_distance,
        "helper": helper_name,
        "helper_rest_length": helper_length,
        "helper_display_length": rig.data.bones[helper_name].envelope_distance,
    }, sort_keys=True))


main()
