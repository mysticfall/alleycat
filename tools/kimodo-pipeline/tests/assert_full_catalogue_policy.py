"""Reopen and assert every synthetic catalogue Root/cycle policy."""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import sys

import bpy

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.arc_semantics import arc_curve_evidence
from kimodo_pipeline.contracts import ROLE_REGISTRY, action_name, pivot_root_yaw_sign, role_projection_policy
from kimodo_pipeline.materialise import (
    ROOT as ROOT_PATH,
    _anatomical_character_scale,
    _candidate_quality_metrics,
    _curves,
    _rig,
    _validate_loop_hierarchy_seam,
)
from kimodo_pipeline.selection import quality_evidence
from kimodo_pipeline.source_repair import evaluated_action_signature


def maximum(values: list[float]) -> float:
    return max((abs(value) for value in values), default=0.0)


def close(actual: float, expected: float, tolerance: float, label: str) -> None:
    assert abs(actual - expected) <= tolerance, (label, actual, expected, tolerance)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--variant", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    options = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    evidence = json.loads(options.evidence.read_text(encoding="utf-8"))
    rig, _, rig_basis = _rig()
    character_scale = _anatomical_character_scale(rig)
    endpoints = {}
    traces = {}
    report = {}
    expected_sources = {
        "walk": "kimodo_multi_001_straight",
        "strafe": "kimodo_multi_synthetic_strafe",
        "precision_lateral": "kimodo_multi_000_step",
        "arc": "kimodo_multi_002_arc",
        "pivot": "kimodo_multi_003_pivot",
    }
    for role in ROLE_REGISTRY:
        code = role.code
        action = bpy.data.actions[action_name(options.variant, role)]
        curves = _curves(action)
        end = int(action.frame_range[1])
        frames = list(range(1, end + 1))
        root = [[float(curves[(f"{ROOT_PATH}.location", axis)].evaluate(frame)) for frame in frames] for axis in range(3)]
        yaw = [float(curves[(f"{ROOT_PATH}.rotation_euler", 1)].evaluate(frame)) for frame in frames]
        endpoint = (root[0][-1], root[1][-1], root[2][-1], yaw[-1])
        endpoints[code] = endpoint
        traces[code] = (root, yaw)
        policy = role_projection_policy(role)
        assert evidence["roles"][code]["root_projection"]["family"] == policy.family
        synthetic_fixture = any(
            row["selected"] in expected_sources.values()
            or row["selected"] == "kimodo_multi_004_transition"
            for row in evidence["roles"].values()
        )
        if synthetic_fixture and policy.family in expected_sources:
            selected = evidence["roles"][code]["selected"]
            if policy.family == "pivot":
                assert selected == expected_sources[policy.family] or selected.startswith("kimodo_multi_repair_")
            else:
                assert selected == expected_sources[policy.family], code
        elif synthetic_fixture and policy.family == "walk_transition":
            expected = "kimodo_multi_004_transition" if code.startswith(("i2f_", "b2i_")) else "kimodo_multi_001_straight"
            assert evidence["roles"][code]["selected"] == expected, (
                code, evidence["roles"][code]["selected"], expected
            )
        if policy.family == "walk":
            assert maximum(root[0]) <= 1e-8 and maximum(root[1]) <= 1e-8 and maximum(yaw) <= 1e-8
            assert (1.0 if code.startswith("wf_") else -1.0) * root[2][-1] > .1
        elif policy.family == "strafe":
            assert maximum(root[1]) <= 1e-8 and maximum(root[2]) <= 1e-8 and maximum(yaw) <= 1e-8
            assert (-1.0 if code.startswith("xr_") else 1.0) * root[0][-1] > .1
        elif policy.family == "precision_lateral":
            assert maximum(root[1]) <= 1e-8 and maximum(root[2]) <= 1e-8 and maximum(yaw) <= 1e-8
            signed_endpoint = (-1.0 if code == "sr" else 1.0) * root[0][-1]
            assert signed_endpoint > .05
            if synthetic_fixture:
                assert signed_endpoint < .2
        elif policy.family == "arc":
            assert maximum(root[1]) <= 1e-8, code
            assert abs(root[2][-1]) > .1, (code, root[2][-1])
            assert max(root[0]) - min(root[0]) > .02, code
            assert max(yaw) - min(yaw) > .1, code
            arc = arc_curve_evidence(code, list(zip(root[0], root[2])), yaw, character_scale)
            assert arc["accepted"], (code, arc)
        elif policy.family == "pivot":
            assert max(maximum(axis) for axis in root) <= 1e-8
            close(yaw[-1], pivot_root_yaw_sign(code) * math.pi / 4.0, 1e-5, code)
        else:
            assert not role.is_loop
            assert maximum(root[0]) <= 1e-8 and maximum(root[1]) <= 1e-8 and maximum(yaw) <= 1e-8
            sign = 1.0 if code.startswith(("i2f_", "f2i_")) else -1.0
            assert sign * root[2][-1] > .05, (code, root[2][-1])

        if role.is_loop:
            _validate_loop_hierarchy_seam(rig, action, role)
            period = end - 1
            for cycle in (1, 2, 3):
                frame = 1 + cycle * period
                for axis in range(3):
                    close(float(curves[(f"{ROOT_PATH}.location", axis)].evaluate(frame)), cycle * endpoint[axis], 2e-5, f"{code} cycle {cycle} axis {axis}")
                close(float(curves[(f"{ROOT_PATH}.rotation_euler", 1)].evaluate(frame)), cycle * endpoint[3], 2e-5, f"{code} cycle {cycle} yaw")
            for curve in curves.values():
                modifier = next(item for item in curve.modifiers if item.type == "CYCLES")
                expected_mode = "REPEAT_OFFSET" if curve.data_path.startswith(ROOT_PATH) and (
                    curve.data_path.endswith(".location") or curve.array_index == 1 and maximum(yaw) > 1e-8
                ) else "REPEAT"
                assert modifier.mode_after == expected_mode, (code, curve.data_path, curve.array_index)
        else:
            assert all(not curve.modifiers for curve in curves.values()), code
        # Re-evaluate every published output directly.  These are safety
        # limits over the actual armature hierarchy, rather than source
        # selection metadata or a renderer-dependent visual proxy.
        anatomy_contact = quality_evidence(
            {action.name: _candidate_quality_metrics(rig, action, 1, end, rig_basis)},
            policy.family,
        )[action.name]
        assert anatomy_contact["accepted"], (code, anatomy_contact)
        report[code] = {
            "family": policy.family,
            "endpoint": endpoint,
            "frames": end,
            "direct_anatomy_contact": anatomy_contact,
        }

    for first, second in (("wf_r", "wf_l"), ("wb_r", "wb_l")):
        for index in range(4): close(endpoints[first][index], endpoints[second][index], 2e-5, f"{first}/{second}")
    close(endpoints["wf_r"][2], -endpoints["wb_r"][2], 2e-5, "walk reversal")
    for first, second in (("xr_l", "xl_l"), ("xr_r", "xl_r"), ("sr", "sl")):
        close(endpoints[first][0], -endpoints[second][0], 2e-5, f"{first}/{second}")
    for first, second in (("ar_l", "al_r"), ("ar_r", "al_l")):
        first_root, first_yaw = traces[first]
        second_root, second_yaw = traces[second]
        assert max(abs(a + b) for a, b in zip(first_root[0], second_root[0])) <= 2e-5
        assert max(abs(a - b) for a, b in zip(first_root[2], second_root[2])) <= 2e-5
        assert max(abs(a + b) for a, b in zip(first_yaw, second_yaw)) <= 2e-5
    close(endpoints["pr45"][3], -endpoints["pl45"][3], 2e-5, "pivot mirror")
    assert abs(endpoints["sr"][0]) < abs(endpoints["xr_l"][0]) * .5
    for first, second in (
        ("wf_r", "wf_l"), ("wb_r", "wb_l"), ("xr_l", "xl_l"),
        ("xr_r", "xl_r"), ("sr", "sl"), ("ar_l", "al_r"),
        ("ar_r", "al_l"), ("pr45", "pl45"), ("i2f_r", "i2f_l"),
        ("f2i_r", "f2i_l"), ("i2b_r", "i2b_l"), ("b2i_r", "b2i_l"),
    ):
        first_action = bpy.data.actions[action_name(options.variant, next(role for role in ROLE_REGISTRY if role.code == first))]
        second_action = bpy.data.actions[action_name(options.variant, next(role for role in ROLE_REGISTRY if role.code == second))]
        assert evaluated_action_signature(first_action) != evaluated_action_signature(second_action), (
            "role-specific output actions must not share identical evaluated channel content",
            first,
            second,
        )
    print("KIMODO_FULL_CATALOGUE_POLICY=" + json.dumps(report, sort_keys=True))


main()
