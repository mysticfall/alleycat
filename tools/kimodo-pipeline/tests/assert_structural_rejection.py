"""Prove reopen validation rejects directional, carriage, and seam corruption."""
from __future__ import annotations

import sys
from pathlib import Path

import bpy

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.contracts import ROLE_REGISTRY, action_name
from kimodo_pipeline.materialise import (
    DEFAULT_TOLERANCES,
    MaterialisationError,
    ROOT as ROOT_PATH,
    _action_fcurves,
    _rig,
    _evaluated_world_samples,
    _validate,
)
from mathutils import Vector


def _curves(action: object) -> dict[tuple[str, int], object]:
    return {(curve.data_path, curve.array_index): curve for curve in _action_fcurves(action)}


def _expect_rejected(variant: str, expected: str) -> None:
    sources = {action.name for action in bpy.data.actions if action.name.startswith("kimodo_multi_")}
    non_pivot_roles = tuple(role for role in ROLE_REGISTRY if role.code not in {"pr45", "pl45"})
    try:
        _validate(variant, sources, non_pivot_roles, False, DEFAULT_TOLERANCES, {})
    except MaterialisationError as error:
        assert expected in str(error), error
        return
    raise AssertionError(f"structural validation accepted {expected}")


def _corrupt_evaluated_hips(
    action: object,
    rig: object,
    rig_basis: object,
    frame: int,
    amount: float,
    vertical: bool,
    longitudinal: bool = False,
) -> tuple[object, float]:
    """Alter a keyed Hips channel only after proving it moves evaluated geometry."""
    end = int(action.frame_range[1])
    baseline = _evaluated_world_samples(rig, action, end)[frame - 1][1]
    world_up = (rig.matrix_world.to_3x3() @ rig_basis @ Vector((0.0, 1.0, 0.0))).normalized()
    world_forward = (rig.matrix_world.to_3x3() @ rig_basis @ Vector((0.0, 0.0, 1.0))).normalized()
    for curve in _action_fcurves(action):
        if not curve.data_path.startswith('pose.bones["Hips"].location'):
            continue
        point = next((item for item in curve.keyframe_points if int(item.co[0]) == frame), None)
        if point is None:
            continue
        original = float(point.co[1])
        for sign in (1.0, -1.0):
            point.co[1] = original + sign * amount
            changed = _evaluated_world_samples(rig, action, end)[frame - 1][1] - baseline
            vertical_change = abs(changed.dot(world_up))
            horizontal_change = (changed - world_up * changed.dot(world_up)).length
            longitudinal_change = abs(changed.dot(world_forward))
            if (
                (vertical and vertical_change > amount * 0.5)
                or (longitudinal and longitudinal_change > amount * 0.5)
                or (not vertical and not longitudinal and horizontal_change > amount * 0.5)
            ):
                return point, original
        point.co[1] = original
    kind = "vertical" if vertical else "longitudinal" if longitudinal else "horizontal"
    raise AssertionError(f"could not corrupt evaluated {kind} Hips geometry for {action.name}")


def main() -> None:
    variant = sys.argv[sys.argv.index("--variant") + 1]
    sources = {action.name for action in bpy.data.actions if action.name.startswith("kimodo_multi_")}
    # This is the pass-before assertion: the untouched reopened catalogue must
    # validate before every independent corruption scenario below.
    non_pivot_roles = tuple(role for role in ROLE_REGISTRY if role.code not in {"pr45", "pl45"})
    _validate(variant, sources, non_pivot_roles, False, DEFAULT_TOLERANCES, {})
    rig, _, rig_basis = _rig()

    role = next(role for role in ROLE_REGISTRY if role.code == "wf_r")
    action = bpy.data.actions[action_name(variant, role)]
    curves = _curves(action)

    hips_endpoint = curves[('pose.bones["Hips"].location', 0)].keyframe_points[-1]
    hips_original = float(hips_endpoint.co.y)
    hips_endpoint.co.y = hips_original + .01
    _expect_rejected(variant, "generated recurrence failed")
    hips_endpoint.co.y = hips_original

    chest_endpoint = curves[('pose.bones["Chest"].rotation_euler', 0)].keyframe_points[-1]
    chest_original = float(chest_endpoint.co.y)
    chest_endpoint.co.y = chest_original + .1
    _expect_rejected(variant, "generated recurrence failed")
    chest_endpoint.co.y = chest_original

    # A CYCLES modifier cannot conceal an unclosed keyed endpoint.
    assert any(modifier.type == "CYCLES" for modifier in curves[('pose.bones["Hips"].location', 0)].modifiers)
    hips_endpoint.co.y = hips_original + .01
    _expect_rejected(variant, "generated recurrence failed")
    hips_endpoint.co.y = hips_original

    root_forward = curves[(f"{ROOT_PATH}.location", 2)]
    root_modifier = next(modifier for modifier in root_forward.modifiers if modifier.type == "CYCLES")
    original_mode = root_modifier.mode_after
    root_modifier.mode_after = "REPEAT"
    _expect_rejected(variant, "cycle extension fidelity failed")
    root_modifier.mode_after = original_mode

    root_x = curves[(f"{ROOT_PATH}.location", 0)]
    root_x.keyframe_points[-1].co[1] = 0.01
    endpoint = list(action["anim002_endpoint_displacement"])
    endpoint[0] = 0.01
    action["anim002_endpoint_displacement"] = endpoint
    # Validation is evaluated in the rig basis, so the rejection must describe
    # forbidden lateral motion rather than assume Blender's local X axis.
    _expect_rejected(variant, "forbidden evaluated Root lateral motion")
    root_x.keyframe_points[-1].co[1] = 0.0
    endpoint[0] = 0.0
    action["anim002_endpoint_displacement"] = endpoint

    # These alter real keyed Hips channels and first prove that evaluated world
    # geometry moved.  They reject the frame-24 carriage failure that the old
    # raw-channel/root-space mix admitted for both a loop and an exit.
    for role_code in ("wf_r", "f2i_r"):
        role = next(item for item in ROLE_REGISTRY if item.code == role_code)
        carriage_action = bpy.data.actions[action_name(variant, role)]
        middle = min(24, int(carriage_action.frame_range[1]) - 1)
        point, original = _corrupt_evaluated_hips(carriage_action, rig, rig_basis, middle, 1.0, False)
        _expect_rejected(variant, "excessive evaluated Hips-relative-to-Root")
        point.co[1] = original

    # A vertical impulse is independently rejected; it cannot be hidden by a
    # horizontal Root trajectory or an endpoint seam.
    transition = bpy.data.actions[action_name(variant, next(item for item in ROLE_REGISTRY if item.code == "f2i_r"))]
    transition_middle = min(24, int(transition.frame_range[1]) - 1)
    point, original = _corrupt_evaluated_hips(transition, rig, rig_basis, transition_middle, 1.0, True)
    _expect_rejected(variant, "unreasonable evaluated vertical Hips motion")
    point.co[1] = original

    # A gait-scale mid-clip forward excursion that returns before the endpoint
    # must trip the recovery check, rather than being concealed by the seam.
    point, original = _corrupt_evaluated_hips(transition, rig, rig_basis, transition_middle, 0.12, False, longitudinal=True)
    _expect_rejected(variant, "late recovery of accumulated Hips-vs-Root drift")
    point.co[1] = original


main()
