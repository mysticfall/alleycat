"""Read-only verification for the complete Segment-2 pivot pair."""
from __future__ import annotations

import math
import sys
from pathlib import Path

import bpy
from mathutils import Matrix, Vector

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.contracts import action_name
from kimodo_pipeline.compatibility import ensure_registered
from kimodo_pipeline.discovery import _segments
from kimodo_pipeline.materialise import ROOT as ROOT_PATH, _action_fcurves, _activate_action, _foot_geometry, _global_z_yaw, _rig


def main() -> None:
    variant = sys.argv[sys.argv.index("--variant") + 1]
    ensure_registered()
    rig, _, basis = _rig()
    segment = {item["index"]: item for item in _segments()}[2]
    assert (segment["start_frame"], segment["end_frame"]) == (161, 210)
    # Fixed project convention viewed from +Z: clockwise/right is negative;
    # counter-clockwise/left is positive. Root local-Y has the same sign.
    for code, side, sign in (("pr45", "Right", -1.0), ("pl45", "Left", 1.0)):
        action = bpy.data.actions[action_name(variant, next(role for role in __import__("kimodo_pipeline.contracts", fromlist=["ROLE_REGISTRY"]).ROLE_REGISTRY if role.code == code))]
        end = int(action.frame_range[1])
        assert end == segment["end_frame"] - segment["start_frame"] + 1
        curves = {(curve.data_path, curve.array_index): curve for curve in _action_fcurves(action)}
        for index in range(3):
            curve = curves[(f"{ROOT_PATH}.location", index)]
            assert max(abs(curve.evaluate(frame)) for frame in range(1, end + 1)) <= 1e-5
        assert rig.pose.bones["Root"].rotation_mode == "ZYX"
        yaw = curves[(f"{ROOT_PATH}.rotation_euler", 1)]
        for index in (0, 2):
            curve = curves[(f"{ROOT_PATH}.rotation_euler", index)]
            assert max(abs(curve.evaluate(frame)) for frame in range(1, end + 1)) <= 1e-5
        endpoint = sign * math.pi / 4.0
        raw = [float(point.co.y) for point in yaw.keyframe_points]
        assert len(raw) == end
        assert all(-1e-5 <= value * sign <= math.pi / 4.0 + 1e-5 for value in raw), (code, raw)
        assert all((following - preceding) * sign >= -1e-5 for preceding, following in zip(raw, raw[1:])), (code, raw)
        for frame in range(1, end + 1):
            value = float(yaw.evaluate(frame))
            assert -1e-5 <= value * sign <= math.pi / 4.0 + 1e-5, (code, frame, value)
            assert math.isclose(value, raw[frame - 1], abs_tol=1e-5), (code, frame, value, raw[frame - 1])
        for frame in range(end, 1 + 2 * (end - 1) + 1):
            cycle, source_index = divmod(frame - 1, end - 1)
            expected_theta = cycle * endpoint + (0.0 if source_index == 0 else raw[source_index])
            value = float(yaw.evaluate(frame))
            assert math.pi / 4.0 - 1e-5 <= value * sign <= math.pi / 2.0 + 1e-5, (code, frame, value)
            assert math.isclose(value, expected_theta, abs_tol=1e-5), (code, frame, value, expected_theta)
        for curve in curves.values():
            modifier = next(modifier for modifier in curve.modifiers if modifier.type == "CYCLES")
            expected = "REPEAT_OFFSET" if curve is yaw or curve.data_path.endswith('pose.bones["Root"].location') else "REPEAT"
            assert modifier.mode_before == expected and modifier.mode_after == expected
        geometry = _foot_geometry(rig, action, 1, end, basis)
        other = "Left" if side == "Right" else "Right"
        assert abs(geometry[side][2].speed) > abs(geometry[other][2].speed)
        _activate_action(rig, action)
        bpy.context.scene.frame_set(1)
        root_rest = rig.pose.bones["Root"].matrix.copy()
        opening = {bone: (rig.matrix_world @ rig.pose.bones[bone].matrix).copy() for bone in ("Root", "Hips", "Chest")}
        world_up = (rig.matrix_world.to_3x3() @ rig.data.bones["Root"].matrix_local.to_3x3() @ Vector((0.0, 1.0, 0.0))).normalized()
        opening_facing = opening["Hips"].to_3x3() @ Vector((0.0, 0.0, 1.0))
        for frame in (1, max(2, end // 2), end, 1 + (end - 1) + max(1, (end - 1) // 2), 1 + 2 * (end - 1)):
            bpy.context.scene.frame_set(frame)
            actual = root_rest.inverted_safe() @ rig.pose.bones["Root"].matrix
            expected = Matrix.Rotation(float(yaw.evaluate(frame)), 4, "Y")
            assert max(abs(actual[row][column] - expected[row][column]) for row in range(4) for column in range(4)) <= 1e-5
            headings = {
                bone: _global_z_yaw((rig.matrix_world @ rig.pose.bones[bone].matrix) @ opening[bone].inverted_safe())
                for bone in opening
            }
            current_facing = (rig.matrix_world @ rig.pose.bones["Hips"].matrix).to_3x3() @ Vector((0.0, 0.0, 1.0))
            first = (opening_facing - world_up * opening_facing.dot(world_up)).normalized()
            current = (current_facing - world_up * current_facing.dot(world_up)).normalized()
            facing_heading = math.atan2(world_up.dot(first.cross(current)), first.dot(current))
            assert headings["Root"] * sign >= -1e-4, (code, frame, headings)
            assert headings["Hips"] * sign >= -1e-4, (code, frame, headings)
            assert headings["Chest"] * sign >= -1e-4, (code, frame, headings)
            assert facing_heading * sign >= -1e-4, (code, frame, facing_heading)
            assert abs(headings["Root"] - headings["Hips"]) <= .02, (code, frame, headings)


main()
