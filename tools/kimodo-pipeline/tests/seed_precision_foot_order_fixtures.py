"""Seed identity-independent Segment-1 foot-order fixtures in a disposable blend."""
from __future__ import annotations

import argparse
from pathlib import Path
import sys

import bpy
from mathutils import Matrix, Vector

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.compatibility import ensure_registered
from kimodo_pipeline.discovery import REFERENCE_ACTION_NAME
from kimodo_pipeline.lateral_basis import anatomical_lateral_basis
from kimodo_pipeline.materialise import (
    ROOT as ROOT_PATH,
    _activate_action,
    _channels_from_armature_rows,
    _motion_bone_names,
    _rig,
    _segments,
)


def _add_curve(bag, path: str, index: int, frames: list[int], values: list[float]) -> None:
    curve = bag.fcurves.new(data_path=path, index=index)
    curve.keyframe_points.add(len(frames))
    for point, frame, value in zip(curve.keyframe_points, frames, values):
        point.co = frame, value
        point.interpolation = "LINEAR"


def _create_crossing_action(rig, basis, name: str, frames: list[int], offset: int = 0) -> None:
    _activate_action(rig, bpy.data.actions[REFERENCE_ACTION_NAME])
    bpy.context.scene.frame_set(1)
    bpy.context.view_layer.update()
    root = rig.pose.bones["Root"].matrix.copy()
    base = {bone: rig.pose.bones[bone].matrix.copy() for bone in _motion_bone_names(rig)}
    lateral = anatomical_lateral_basis(rig, basis).axis
    rows = []
    for index, _frame in enumerate(frames):
        progress = index / (len(frames) - 1)
        generated = root @ Matrix.Translation(Vector((-.12 * (3.0 * progress ** 2 - 2.0 * progress ** 3), 0.0, 0.0)))
        row = {bone: generated @ root.inverted_safe() @ matrix for bone, matrix in base.items()}
        centre = (row["LeftFoot"].translation + row["RightFoot"].translation) * 0.5
        # Both identities materially invert anatomy over the complete segment;
        # the local frame offset changes provenance only, not the decision.
        row["LeftFoot"].translation = centre - lateral * .08
        row["RightFoot"].translation = centre + lateral * .08
        rows.append(row)
    channels = _channels_from_armature_rows(rig, [root.copy() for _ in frames], rows)
    action = bpy.data.actions.new(name)
    action.use_fake_user = True
    action["anim002_synthetic_fixture"] = "material_precision_crossover_v1"
    slot = action.slots.new(rig.id_type, rig.name)
    bag = action.layers.new("PRECISION_CROSSOVER").strips.new(type="KEYFRAME").channelbag(slot, ensure=True)
    shifted = [frame + offset for frame in frames]
    for (path, index), values in sorted(channels.items()):
        _add_curve(bag, path, index, shifted, values)
    for index in range(3):
        _add_curve(bag, f"{ROOT_PATH}.location", index, shifted, [0.0] * len(frames))
        _add_curve(bag, f"{ROOT_PATH}.rotation_euler", index, shifted, [0.0] * len(frames))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("blend")
    parser.add_argument("--mode", choices=("all_crossing",), required=True)
    options = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    blend = Path(options.blend).resolve()
    ensure_registered()
    bpy.ops.wm.open_mainfile(filepath=str(blend), load_ui=False, use_scripts=False)
    rig, _, basis = _rig()
    segment = next(row for row in _segments() if row["index"] == 1)
    frames = list(range(segment["start_frame"], segment["end_frame"] + 1))
    for action in list(bpy.data.actions):
        if action.name.startswith("kimodo_multi_"):
            bpy.data.actions.remove(action, do_unlink=True)
    _create_crossing_action(rig, basis, "kimodo_multi_crossing_alpha", frames)
    _create_crossing_action(rig, basis, "kimodo_multi_crossing_beta", frames)
    bpy.ops.wm.save_as_mainfile(filepath=str(blend), check_existing=False)


main()
