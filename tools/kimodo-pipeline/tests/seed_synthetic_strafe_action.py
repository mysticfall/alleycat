"""Seed a deterministic, hierarchy-solved strafe source in a disposable blend."""
from __future__ import annotations

import math
from pathlib import Path
import sys
from typing import Any

import bpy
from mathutils import Matrix, Vector

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.compatibility import ensure_registered
from kimodo_pipeline.discovery import REFERENCE_ACTION_NAME
from kimodo_pipeline.materialise import (
    EULER_OUTPUT_MODE,
    ROOT as ROOT_PATH,
    _activate_action,
    _channels_from_armature_rows,
    _motion_bone_names,
    _rig,
    _segments,
)


ACTION_NAME = "kimodo_multi_synthetic_strafe"
INELIGIBLE_ACTION_NAME = "kimodo_multi_ineligible_strafe_repair"
PERIOD = 16


def gait_positions(phase: int) -> tuple[float, float]:
    """Return non-static semantic-lateral Left/Right foot offsets."""
    # Cross the centre line decisively rather than lingering near it. This
    # preserves an auditable left release while avoiding a synthetic collapsed
    # leg pose around the crossing samples.
    left = (-.15, -.12, .06, .12, .18, .20, .20, .20, .06, -.12, -.18, -.20, -.20, -.20, -.18, -.16)
    right = (0.0,) * PERIOD
    return left[phase], right[phase]


def add_curve(bag: Any, path: str, index: int, frames: list[int], values: list[float]) -> None:
    curve = bag.fcurves.new(data_path=path, index=index)
    curve.keyframe_points.add(len(frames))
    for point, frame, value in zip(curve.keyframe_points, frames, values):
        point.co = frame, value
        point.interpolation = "LINEAR"


def main() -> None:
    blend = Path(sys.argv[sys.argv.index("--") + 1]).resolve()
    ensure_registered()
    bpy.ops.wm.open_mainfile(filepath=str(blend), load_ui=False, use_scripts=False)
    rig, _, rig_basis = _rig()
    segment = next(row for row in _segments() if row["index"] == 4)
    frames = list(range(segment["start_frame"], segment["end_frame"] + 1))
    if len(frames) < PERIOD * 2 + 1:
        raise AssertionError("strafe source segment is too short for two evidenced cycles")

    reference = bpy.data.actions[REFERENCE_ACTION_NAME]
    _activate_action(rig, reference)
    bpy.context.scene.frame_set(1)
    bpy.context.view_layer.update()
    root_matrix = rig.pose.bones["Root"].matrix.copy()
    names = _motion_bone_names(rig)
    base = {name: rig.pose.bones[name].matrix.copy() for name in names}
    base_hips = base["Hips"].translation.copy()
    world_up = (rig_basis @ Vector((0.0, 1.0, 0.0))).normalized()
    forward = (rig_basis @ Vector((0.0, 0.0, 1.0))).normalized()
    # This matches _foot_geometry's signed semantic lateral axis.
    lateral = forward.cross(world_up).normalized()

    desired_rows = []
    for frame in frames:
        elapsed = frame - frames[0]
        phase = elapsed % PERIOD
        # Candidate-local orthogonal drift deliberately exceeds the unchanged
        # straight-travel gate in one subwindow while remaining bounded and
        # returning to zero at each recurrence endpoint.
        orthogonal_drift = .045 * math.sin(2.0 * math.pi * phase / PERIOD)
        carriage = lateral * (.03 * elapsed) + forward * orthogonal_drift
        row = {name: matrix.copy() for name, matrix in base.items()}
        for matrix in row.values():
            matrix.translation += carriage
        hips = base_hips + carriage
        left, right = gait_positions(elapsed % PERIOD)
        for name, offset in (("LeftFoot", left), ("RightFoot", right)):
            base_offset = base[name].translation - base_hips
            base_offset -= lateral * base_offset.dot(lateral)
            target = hips + base_offset + lateral * offset
            delta = target - row[name].translation
            prefix = name.removesuffix("Foot")
            for leg_name in (f"{prefix}Leg", f"{prefix}Shin", name):
                row[leg_name].translation += delta
        # A small bounded non-periodic chest drift requires genuine symmetric
        # seam construction while remaining far inside unchanged repair budgets.
        row["Chest"].translation += forward * (.00008 * elapsed)
        desired_rows.append(row)

    channels = _channels_from_armature_rows(
        rig, [root_matrix.copy() for _ in frames], desired_rows,
    )
    existing = bpy.data.actions.get(ACTION_NAME)
    if existing is not None:
        bpy.data.actions.remove(existing, do_unlink=True)
    action = bpy.data.actions.new(ACTION_NAME)
    action.use_fake_user = True
    action["anim002_synthetic_fixture"] = "deterministic_hierarchy_strafe_v1"
    action["anim002_period_frames"] = PERIOD
    action["anim002_expected_left_release_phase"] = 3
    action["anim002_expected_right_release_phase"] = 11
    slot = action.slots.new(rig.id_type, rig.name)
    bag = action.layers.new("SYNTHETIC_STRAFE").strips.new(type="KEYFRAME").channelbag(slot, ensure=True)
    for (path, index), values in sorted(channels.items()):
        add_curve(bag, path, index, frames, values)
    for index in range(3):
        add_curve(bag, f"{ROOT_PATH}.location", index, frames, [0.0] * len(frames))
        add_curve(bag, f"{ROOT_PATH}.rotation_euler", index, frames, [0.0] * len(frames))
    existing = bpy.data.actions.get(INELIGIBLE_ACTION_NAME)
    if existing is not None:
        bpy.data.actions.remove(existing, do_unlink=True)
    ineligible = action.copy()
    ineligible.name = INELIGIBLE_ACTION_NAME
    ineligible.use_fake_user = True
    ineligible["anim002_source_roles"] = '["wf_r"]'
    ineligible["anim002_source_content_repair"] = "stale provenance"
    rig.pose.bones["Root"].rotation_mode = EULER_OUTPUT_MODE
    bpy.ops.wm.save_as_mainfile(filepath=str(blend), check_existing=False)


main()
