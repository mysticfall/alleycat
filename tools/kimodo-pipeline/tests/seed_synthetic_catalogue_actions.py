"""Seed deterministic non-strafe source families for full catalogue tests."""
from __future__ import annotations

import math
import json
from pathlib import Path
import sys
from typing import Any, Callable

import bpy
from mathutils import Matrix, Vector

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.compatibility import ensure_registered
from kimodo_pipeline.discovery import REFERENCE_ACTION_NAME
from kimodo_pipeline.materialise import (
    EULER_OUTPUT_MODE, ROOT as ROOT_PATH, _activate_action,
    _channels_from_armature_rows, _curves, _motion_bone_names, _rig, _segments,
)
from kimodo_pipeline.source_repair import ROLE_RESTRICTION_PROPERTY

PERIOD = 32


def gait_positions(phase: int) -> tuple[float, float]:
    if phase < 4: return -.10 + .05 * phase, 0.0
    if phase < 8: return .10, 0.0
    if phase < 12: return .10, .035 * (phase - 8)
    if phase < 16: return .10, .14
    if phase < 20: return .10 - .05 * (phase - 16), .14
    if phase < 24: return -.10, .14
    if phase < 28: return -.10, .14 - .035 * (phase - 24)
    return -.10, 0.0


def move_leg_chain(row: dict[str, Matrix], foot_name: str, target: Vector) -> None:
    delta = target - row[foot_name].translation
    prefix = foot_name.removesuffix("Foot")
    for name in (f"{prefix}Leg", f"{prefix}Shin", foot_name):
        row[name].translation += delta


def add_curve(bag: Any, path: str, index: int, frames: list[int], values: list[float]) -> None:
    curve = bag.fcurves.new(data_path=path, index=index)
    curve.keyframe_points.add(len(frames))
    for point, frame, value in zip(curve.keyframe_points, frames, values):
        point.co = frame, value
        point.interpolation = "LINEAR"


def create_action(rig: Any, name: str, frames: list[int], root: Matrix, desired_rows: list[dict[str, Matrix]], family: str) -> None:
    channels = _channels_from_armature_rows(rig, [root.copy() for _ in frames], desired_rows)
    existing = bpy.data.actions.get(name)
    if existing is not None: bpy.data.actions.remove(existing, do_unlink=True)
    action = bpy.data.actions.new(name)
    action.use_fake_user = True
    action["anim002_synthetic_fixture"] = f"deterministic_{family}_v1"
    slot = action.slots.new(rig.id_type, rig.name)
    bag = action.layers.new(f"SYNTHETIC_{family.upper()}").strips.new(type="KEYFRAME").channelbag(slot, ensure=True)
    for (path, index), values in sorted(channels.items()): add_curve(bag, path, index, frames, values)
    for index in range(3):
        add_curve(bag, f"{ROOT_PATH}.location", index, frames, [0.0] * len(frames))
        add_curve(bag, f"{ROOT_PATH}.rotation_euler", index, frames, [0.0] * len(frames))


def transformed_rows(frames: list[int], root: Matrix, base: dict[str, Matrix], transfer_at: Callable[[int, float], Matrix], forward: Vector, lateral: Vector, gait: bool = True) -> list[dict[str, Matrix]]:
    root_inverse = root.inverted_safe()
    base_relative = {name: root_inverse @ matrix for name, matrix in base.items()}
    base_hips = base["Hips"].translation
    rows = []
    for index, _frame in enumerate(frames):
        progress = index / (len(frames) - 1)
        generated = transfer_at(index, progress)
        row = {name: generated @ relative for name, relative in base_relative.items()}
        if gait:
            left, right = gait_positions((index + 7) % PERIOD)
            hips = row["Hips"].translation
            delta = generated @ root_inverse
            moved_forward = (delta.to_3x3() @ forward).normalized()
            moved_lateral = (delta.to_3x3() @ lateral).normalized()
            for name, longitudinal in (("LeftFoot", left), ("RightFoot", right)):
                base_offset = base[name].translation - base_hips
                target = row[name].translation + moved_forward * (longitudinal - base_offset.dot(forward))
                move_leg_chain(row, name, target)
        rows.append(row)
    return rows


def main() -> None:
    blend = Path(sys.argv[sys.argv.index("--") + 1]).resolve()
    ensure_registered()
    bpy.ops.wm.open_mainfile(filepath=str(blend), load_ui=False, use_scripts=False)
    for action in list(bpy.data.actions):
        raw_roles = action.get(ROLE_RESTRICTION_PROPERTY)
        if raw_roles is not None and "pr45" not in json.loads(raw_roles):
            bpy.data.actions.remove(action, do_unlink=True)
    rig, _, basis = _rig()
    segments = {row["index"]: row for row in _segments()}
    _activate_action(rig, bpy.data.actions[REFERENCE_ACTION_NAME])
    bpy.context.scene.frame_set(1); bpy.context.view_layer.update()
    root = rig.pose.bones["Root"].matrix.copy()
    base = {name: rig.pose.bones[name].matrix.copy() for name in _motion_bone_names(rig)}
    up = (basis @ Vector((0.0, 1.0, 0.0))).normalized()
    forward = (basis @ Vector((0.0, 0.0, 1.0))).normalized()
    lateral = forward.cross(up).normalized()
    base_hips = base["Hips"].translation

    frames = list(range(segments[0]["start_frame"], segments[0]["end_frame"] + 1))
    straight_rows = []
    for index, _frame in enumerate(frames):
        progress = index / (len(frames) - 1)
        # This complementary source closes into idle but starts in motion. It
        # therefore owns motion-to-idle windows while the transition source
        # below owns idle-to-motion windows without relying on Action identity.
        carriage = forward * (3.0 * (2.0 * progress - progress * progress))
        row = {name: matrix.copy() for name, matrix in base.items()}
        for matrix in row.values(): matrix.translation += carriage
        hips = base_hips + carriage
        left, right = gait_positions(0 if index < 24 else (index - 24) % PERIOD)
        for name, longitudinal in (("LeftFoot", left), ("RightFoot", right)):
            base_offset = base[name].translation - base_hips
            target = row[name].translation + forward * (longitudinal - base_offset.dot(forward))
            move_leg_chain(row, name, target)
        row["Chest"].translation += lateral * (.00008 * index)
        straight_rows.append(row)
    create_action(rig, "kimodo_multi_001_straight", frames, root, straight_rows, "straight")

    transition_rows = []
    for index, _frame in enumerate(frames):
        progress = index / (len(frames) - 1)
        # Accelerate away from the idle-facing opening. The source deliberately
        # has no idle-facing closing endpoint, so native idle-to-motion roles
        # use it while motion-to-idle roles use the straight source's closing
        # gait event. Reverse derivations preserve the corresponding mapping.
        carriage = forward * (1.19 * progress * progress)
        row = {name: matrix.copy() for name, matrix in base.items()}
        for matrix in row.values(): matrix.translation += carriage
        hips = base_hips + carriage
        left = 0.0
        if index < 29:
            right = -.08
        elif index == 29:
            right = .01
        elif index < 61:
            right = .08
        elif index == 61:
            right = 0.0
        elif index < 93:
            right = -.08
        elif index == 93:
            right = .01
        else:
            right = .08
        for name, longitudinal in (("LeftFoot", left), ("RightFoot", right)):
            base_offset = base[name].translation - base_hips
            target = row[name].translation + forward * (longitudinal - base_offset.dot(forward))
            move_leg_chain(row, name, target)
        # Deliberately finite: this source is valid for transitions but exceeds
        # unchanged periodic seam budgets, so it cannot alias a WF/WB loop.
        row["Chest"].translation += lateral * (.004 * index)
        transition_rows.append(row)
    create_action(rig, "kimodo_multi_004_transition", frames, root, transition_rows, "transition")

    frames = list(range(segments[1]["start_frame"], segments[1]["end_frame"] + 1))
    step_rows = transformed_rows(
        frames, root, base,
        lambda _i, p: root @ Matrix.Translation(Vector((-.05 * (3*min(p / .4, 1.0)**2 - 2*min(p / .4, 1.0)**3), 0.0, 0.0))),
        forward, lateral, gait=False,
    )
    for index, row in enumerate(step_rows):
        progress = index / (len(step_rows) - 1)
        lift = math.sin(math.pi * progress) ** 2
        for name in ("LeftLeg", "LeftShin", "LeftFoot"):
            row[name].translation += up * (.035 * lift)
            # A complete, non-crossing native release: the left chain moves
            # rightward relative to Hips then returns to planted support.  The
            # common generated translation above alone is deliberately not
            # enough to satisfy the evaluated contact/release contract.
            release = .08 * math.sin(math.pi * min(progress / .5, 1.0))
            row[name].translation += lateral * release
        for name in ("RightLeg", "RightShin", "RightFoot"):
            row[name].translation += up * (.025 * lift)
    create_action(rig, "kimodo_multi_000_step", frames, root, step_rows, "precision_step")

    # Two gait cycles make both releases explicit. The shallow turn keeps each
    # source loop candidate within the direct repair correction budget while
    # retaining clearly curved Root-local forward/right clockwise motion.
    frames = list(range(segments[5]["start_frame"], segments[5]["start_frame"] + 65))
    arc_radius = .5
    arc_radians_per_frame = .025
    arc_rows = []
    for index, _frame in enumerate(frames):
        theta = arc_radians_per_frame * index
        carriage = basis @ Vector((
            -arc_radius * (1.0 - math.cos(theta)),
            0.0,
            arc_radius * math.sin(theta),
        ))
        body_turn = root @ Matrix.Rotation(-theta, 4, "Y") @ root.inverted_safe()
        turn_about_hips = Matrix.Translation(base_hips) @ body_turn @ Matrix.Translation(-base_hips)
        row = {name: turn_about_hips @ matrix for name, matrix in base.items()}
        for matrix in row.values():
            matrix.translation += carriage
        phase = 2.0 * math.pi * (index + 4) / PERIOD
        tangent = basis @ Vector((
            -arc_radius * arc_radians_per_frame * math.sin(theta),
            0.0,
            arc_radius * arc_radians_per_frame * math.cos(theta),
        ))
        tangent.normalize()
        for name, longitudinal in (("LeftFoot", .04 * math.sin(phase)), ("RightFoot", -.04 * math.sin(phase))):
            relative = row[name].translation - row["Hips"].translation
            target = row[name].translation + tangent * (longitudinal - relative.dot(tangent))
            move_leg_chain(row, name, target)
        arc_rows.append(row)
    create_action(rig, "kimodo_multi_002_arc", frames, root, arc_rows, "arc")

    frames = list(range(segments[2]["start_frame"], segments[2]["end_frame"] + 1))
    pivot_rows = transformed_rows(
        frames, root, base,
        lambda _i, p: root @ Matrix.Rotation(-math.pi*p/4.0, 4, "Y"),
        forward, lateral, gait=False,
    )
    for index, row in enumerate(pivot_rows):
        progress = index / (len(pivot_rows) - 1)
        hips = row["Hips"].translation
        delta = (root @ Matrix.Rotation(-math.pi*progress/4.0, 4, "Y")) @ root.inverted_safe()
        moved_forward = (delta.to_3x3() @ forward).normalized()
        moved_lateral = (delta.to_3x3() @ lateral).normalized()
        for name, longitudinal in (
            ("LeftFoot", .06 * math.sin(2.0 * math.pi * progress)),
            ("RightFoot", -.04 * math.sin(2.0 * math.pi * progress)),
        ):
            base_offset = base[name].translation - base_hips
            target = row[name].translation + moved_forward * (longitudinal - base_offset.dot(forward))
            move_leg_chain(row, name, target)
    create_action(rig, "kimodo_multi_003_pivot", frames, root, pivot_rows, "pivot")

    confined_frames = list(range(segments[1]["start_frame"], segments[1]["end_frame"] + 1))
    chest_path = 'pose.bones["Chest"].location'
    for action in (item for item in bpy.data.actions if item.name.startswith("kimodo_multi_") and item.name != "kimodo_multi_000_step"):
        curves = _curves(action)
        curve = curves.get((chest_path, 0))
        if curve is None:
            bag = action.layers[0].strips[0].channelbags[0]
            curve = bag.fcurves.new(data_path=chest_path, index=0)
        origin = float(curve.evaluate(confined_frames[0]))
        for index, frame in enumerate(confined_frames):
            point = next((item for item in curve.keyframe_points if abs(float(item.co.x) - frame) <= 1e-6), None)
            value = origin + .01 * index
            if point is None:
                point = curve.keyframe_points.insert(frame, value, options={"FAST"})
            else:
                point.co.y = value
            point.interpolation = "LINEAR"
        curve.update()
    # The fixture's synthetic source catalogue is isolated in the disposable
    # copy. The retained role-scoped pivot repair is required by its dedicated
    # full-transform and repeat-composition contract.
    for action in list(bpy.data.actions):
        raw_roles = action.get(ROLE_RESTRICTION_PROPERTY)
        preserves_repaired_pivot = raw_roles is not None and "pr45" in json.loads(raw_roles)
        if (
            action.name.startswith("kimodo_multi_")
            and "anim002_synthetic_fixture" not in action
            and not preserves_repaired_pivot
        ):
            bpy.data.actions.remove(action, do_unlink=True)
    rig.pose.bones["Root"].rotation_mode = EULER_OUTPUT_MODE
    bpy.ops.wm.save_as_mainfile(filepath=str(blend), check_existing=False)


main()
