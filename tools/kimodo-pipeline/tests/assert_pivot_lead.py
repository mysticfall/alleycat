"""Read-only anatomical lead proof for the complete Segment-2 pivot pair.

The metric deliberately uses evaluated bilateral foot geometry in armature
space.  It never interprets camera screen side or diagnostic guide colours.
"""
from __future__ import annotations

import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Quaternion, Vector

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.compatibility import ensure_registered
from kimodo_pipeline.contracts import ROLE_REGISTRY, action_name
from kimodo_pipeline.materialise import _activate_action, _rig


def _samples(rig, action, frames: list[int]) -> dict[int, dict[str, float]]:
    """Measure Root-relative bilateral swing displacement in evaluated geometry."""
    animation = rig.animation_data_create()
    previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
    try:
        _activate_action(rig, action)
        rows = {}
        for frame in frames:
            bpy.context.scene.frame_set(frame)
            evaluated = rig.evaluated_get(bpy.context.evaluated_depsgraph_get())
            # Blender armature/global Z is the stable Kimodo vertical axis. The
            # metric is intentionally unrelated to the render camera axes.
            # Root is intentionally newly yawed. Hips-relative anatomy is the
            # source-derived stepping oracle, so it is not contaminated by the
            # promoted whole-character heading.
            hips = evaluated.pose.bones["Hips"].matrix
            left = hips.inverted_safe() @ evaluated.pose.bones["LeftFoot"].matrix.translation
            right = hips.inverted_safe() @ evaluated.pose.bones["RightFoot"].matrix.translation
            rows[frame] = {
                "Left": [float(value) for value in left],
                "Right": [float(value) for value in right],
            }
        opening = rows[min(frames)]
        for row in rows.values():
            row["LeftSwing"] = (Vector(row["Left"]) - Vector(opening["Left"])).length
            row["RightSwing"] = (Vector(row["Right"]) - Vector(opening["Right"])).length
            row["left_minus_right_swing"] = row["LeftSwing"] - row["RightSwing"]
        return rows
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)
        bpy.context.view_layer.update()


def _leading_side(rows: dict[int, dict[str, float]], label: str) -> str:
    """Return source-evidenced anatomical lead in Hips-relative geometry."""
    # The interval begins at the rest pose and reaches the first meaningful
    # mid-unit sample. This is an evaluated displacement, not a screen-side
    # label or an instantaneous frame whose support side can later alternate.
    difference = rows[25]["left_minus_right_swing"]
    if abs(difference) <= 1e-4:
        raise AssertionError(
            f"{label} has no source-evidenced anatomical lead through frame 25: "
            f"left-minus-right swing={rows[25]['left_minus_right_swing']:.6f}"
        )
    return "Left" if difference > 0.0 else "Right"


def _heading(matrix) -> float:
    quaternion = matrix.to_3x3().to_quaternion().normalized()
    twist = Quaternion((quaternion.w, 0.0, 0.0, quaternion.z))
    twist.normalize()
    return math.degrees(2.0 * math.atan2(twist.z, twist.w))


def _assert_actual_body_heading(rig, output, sign: float) -> None:
    """Root, Hips, and Chest must share physical heading in every cycle."""
    _activate_action(rig, output)
    bpy.context.scene.frame_set(1)
    opening = {bone: _heading(rig.pose.bones[bone].matrix) for bone in ("Root", "Hips", "Chest")}
    # Natural source gait can perturb a single-frame anatomical twist. Check
    # the source-paced unit landmarks and their repeat, rather than mistaking
    # that local motion for a whole-character counter-yaw.
    for frame in (25, 50, 75, 99):
        bpy.context.scene.frame_set(frame)
        headings = {bone: _heading(rig.pose.bones[bone].matrix) - opening[bone] for bone in opening}
        assert headings["Root"] * sign >= -0.01, (output.name, frame, headings)
        for bone in ("Hips", "Chest"):
            assert headings[bone] * sign >= -0.01, (output.name, frame, headings)
            assert abs(headings[bone] - headings["Root"]) <= 5.0, (output.name, frame, headings)


def _assert_source_paced_lead_and_lift(rig, source, output, source_side: str, output_side: str) -> dict[str, int]:
    """Ensure the projection did not retime immutable lead or foot-lift peaks."""
    def trajectory(action, side, frames):
        _activate_action(rig, action)
        values = []
        for frame in frames:
            bpy.context.scene.frame_set(frame)
            hips_inverse = rig.pose.bones["Hips"].matrix.inverted_safe()
            foot = hips_inverse @ rig.pose.bones[f"{side}Foot"].matrix.translation
            values.append(Vector(foot))
        origin = values[0]
        swing = [(value - origin).length for value in values]
        lift = [value.z - origin.z for value in values]
        return max(range(len(values)), key=lambda index: swing[index]), max(range(len(values)), key=lambda index: lift[index])

    source_peaks = trajectory(source, source_side, list(range(161, 211)))
    output_peaks = trajectory(output, output_side, list(range(1, 51)))
    assert source_peaks == output_peaks, (output.name, source_peaks, output_peaks)
    return {"lead_peak_offset_frames": output_peaks[0], "lift_peak_offset_frames": output_peaks[1]}


def main() -> None:
    variant = sys.argv[sys.argv.index("--variant") + 1]
    ensure_registered()
    rig, _, _ = _rig()
    roles = {role.code: role for role in ROLE_REGISTRY}
    source = bpy.data.actions[sys.argv[sys.argv.index("--source") + 1]]
    source_rows = _samples(rig, source, [161, 162, 185, 203, 210])
    # Segment 2 is native right-leading. Its output-equivalent samples are
    # source 161/162/185, with frame 162 as initial release and 185 mid-unit.
    print("KIMODO_PIVOT_SOURCE_LEAD_JSON=" + json.dumps(source_rows, sort_keys=True))
    source_lead = _leading_side({25: source_rows[185]}, source.name)
    report = {"source": source_rows, "outputs": {}}
    for code, side in (("pr45", source_lead), ("pl45", "Left" if source_lead == "Right" else "Right")):
        action = bpy.data.actions[action_name(variant, roles[code])]
        rows = _samples(rig, action, [1, 2, 25, 43, 50, 99])
        assert _leading_side(rows, action.name) == side, (action.name, source_lead, side)
        report["outputs"][code] = {
            "samples": rows,
            "source_phase": _assert_source_paced_lead_and_lift(rig, source, action, source_lead, side),
        }
    print("KIMODO_PIVOT_LEAD_JSON=" + json.dumps(report, sort_keys=True))


main()
