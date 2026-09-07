"""Assert the disposable synthetic arc source is role-semantic before materialisation."""
from __future__ import annotations

import json
from pathlib import Path
import sys

import bpy

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.arc_semantics import arc_curve_evidence
from kimodo_pipeline.compatibility import ensure_registered
from kimodo_pipeline.materialise import (
    _evaluated_body_relative, _evaluated_hips_root_motion, _rig, _segments,
    _select_adaptive_loop_repair, _source_walk_role,
)
from kimodo_pipeline.contracts import ROLE_REGISTRY
from kimodo_pipeline.selection import release_events, role_window
from kimodo_pipeline.materialise import _foot_geometry
from kimodo_pipeline.source_repair import _select_arc_candidate, arc_body_support_for_action


ACTION = "kimodo_multi_002_arc"
SOURCE_ACTIONS = {"ar_l": ACTION, "ar_r": ACTION}


def main() -> None:
    ensure_registered()
    rig, _, basis = _rig()
    action = bpy.data.actions[ACTION]
    segment = next(item for item in _segments() if item["index"] == 5)
    frames = list(range(segment["start_frame"], segment["end_frame"] + 1))
    root = _evaluated_hips_root_motion(rig, action, frames, basis, "native")
    _, headings = _evaluated_body_relative(rig, action, frames, False, basis, True)
    scale = (rig.pose.bones["LeftLeg"].head - rig.pose.bones["LeftFoot"].head).length
    evidence = arc_curve_evidence(
        "ar_l",
        [(root[0][index], root[2][index]) for index in range(len(frames))],
        headings,
        scale,
    )
    geometry = _foot_geometry(rig, action, segment["start_frame"], segment["end_frame"], basis)
    left_releases = release_events(segment, geometry, "Left", "ar_l")
    right_action = bpy.data.actions[SOURCE_ACTIONS["ar_r"]]
    right_geometry = _foot_geometry(rig, right_action, segment["start_frame"], segment["end_frame"], basis)
    right_releases = release_events(segment, right_geometry, "Right", "ar_r")
    source_role_selection = {}
    for source_role in ("ar_l", "ar_r"):
        source_action = bpy.data.actions[SOURCE_ACTIONS[source_role]]
        try:
            _, selected_range, selected = _select_arc_candidate(
                rig, [source_action], source_role, segment, basis,
            )
            source_role_selection[source_role] = {
                "accepted": True,
                "action": source_action.name,
                "range": selected_range,
                "repair_cost": selected["candidates"][0]["repair"]["repair_cost"],
            }
        except Exception as error:
            source_role_selection[source_role] = {
                "accepted": False,
                "reason": str(error),
            }
    output_role_repair = {}
    roles = {role.code: role for role in ROLE_REGISTRY}
    for role_code in ("ar_l", "ar_r", "al_l", "al_r"):
        role = roles[role_code]
        source_role = _source_walk_role(role)
        source_action = bpy.data.actions[SOURCE_ACTIONS[source_role]]
        source_geometry = _foot_geometry(
            rig, source_action, segment["start_frame"], segment["end_frame"], basis,
        )
        source_start, source_end = role_window(segment, source_role, (), source_geometry)
        accepted, repair = _select_adaptive_loop_repair(
            rig, role, source_action, source_start, source_end, basis,
        )
        try:
            body_support = arc_body_support_for_action(
                rig, source_action, source_start, source_end, role, basis,
            )
        except Exception as error:
            body_support = {"accepted": False, "reason": str(error)}
        output_role_repair[role_code] = {
            "accepted": accepted,
            "source_role": source_role,
            "source_action": source_action.name,
            "source_range": [source_start, source_end],
            "repair_cost": repair.get("repair_cost"),
            "repair_reason": repair.get("reason"),
            "body_support": {"accepted": body_support.get("accepted", True), "reason": body_support.get("reason")},
        }
    print("KIMODO_SYNTHETIC_ARC_SOURCE_REPAIR=" + json.dumps(source_role_selection, sort_keys=True))
    print("KIMODO_SYNTHETIC_ARC_OUTPUT_REPAIR=" + json.dumps(output_role_repair, sort_keys=True))
    assert evidence["accepted"], evidence
    assert root[0][-1] < -scale * .05, evidence
    assert root[2][-1] > scale * .10, evidence
    assert headings[-1] - headings[0] < -.10, evidence
    assert left_releases, left_releases
    assert right_releases, right_releases
    assert all(row["accepted"] for row in source_role_selection.values()), source_role_selection
    assert all(row["accepted"] and row["body_support"].get("accepted", True) for row in output_role_repair.values()), output_role_repair
    print("KIMODO_SYNTHETIC_ARC_FIXTURE=" + json.dumps({
        "contract": "root_local_forward_right_clockwise_arc_v1",
        "root_endpoint": [root[0][-1], root[2][-1]],
        "endpoint_yaw": headings[-1] - headings[0],
        "curve": evidence,
        "left_releases": left_releases,
        "right_releases": right_releases,
    }, sort_keys=True))


main()
