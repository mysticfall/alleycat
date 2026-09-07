#!/usr/bin/env python3
"""Read-only, shape-based candidate audit for RIG-002's one-helper topology."""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import bpy
TOOLS_DIR = Path(__file__).resolve().parents[1]
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))
SCRIPT_DIR = Path(__file__).resolve().parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

import forearm_twist_weights as distribution
from audit_forearm_twist import (
    EPSILON,
    SIDES,
    _coordinates,
    _evaluate_anatomical_bend,
    _require_single,
    _ring_measurements,
    _sha256,
    _side_frame,
    _skin_vertices,
    _triangle_indicators,
    _weights,
)
from forearm_twist_bridge import (
    RUNTIME_TWIST_WEIGHT,
    SCHEMA_VERSION as MANIFEST_SCHEMA_VERSION,
    evaluate_candidate_eligibility,
    ProvenanceError,
)


ANGLES = (45.0, 90.0, 135.0, 180.0)
# Declared axial candidate set (see /tmp/opencode/rig-002-twist-only-axial-candidates.md,
# declared before execution per the RIG-002 candidate ledger): helper_fraction stays
# pinned to the shipped profile's documented 0.5 ownership setting; candidates vary the
# axial transition boundaries (proximal_t, distal_t) and the connectivity smoothing
# passes. The unchanged shipped profile stays in the set as the in-run control, and the
# unsmoothed extremes anchor the smoothing axis.
STAGE5_PROFILE_PARAMETERS = (
    (0.10, 1.10, 24),  # control: shipped baseline
    (0.10, 1.10, 12),
    (0.10, 1.10, 60),
    (0.05, 1.10, 24),
    (0.10, 1.05, 24),
    (0.10, 1.15, 24),
    (0.05, 1.05, 36),
    (0.10, 1.05, 12),
)
PROFILES = tuple(
    distribution.ForearmTwistWeightProfile(
        f"topology-linear-p{round(proximal * 100):02d}-d{round(distal * 100):02d}-s{passes:02d}",
        0.5,
        proximal,
        distal,
        passes,
    )
    for proximal, distal, passes in STAGE5_PROFILE_PARAMETERS
)
BASELINE_CANDIDATE = "pre_authoring_source"


def arguments() -> argparse.Namespace:
    values = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--female", required=True, type=Path)
    parser.add_argument("--male", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    return parser.parse_args(values)


def candidate_weights(mesh: bpy.types.Object, armature: bpy.types.Object, profile: distribution.ForearmTwistWeightProfile) -> dict[str, list[dict[str, float]]]:
    original = [_weights(mesh, vertex.index) for vertex in mesh.data.vertices]
    polygons = [tuple(polygon.vertices) for polygon in mesh.data.polygons]
    result = {}
    for side_name, side in SIDES.items():
        frame = _side_frame(armature, side)
        projected = []
        for point in (mesh.matrix_world @ vertex.co for vertex in mesh.data.vertices):
            t, first, second = _coordinates(point, frame)
            radius = math.hypot(first, second)
            projected.append((t, radius))
        hand_bone = armature.data.bones[str(side["hand"])]
        finger_groups = frozenset(
            bone.name for bone in armature.data.bones
            if bone.use_deform and bone != hand_bone
            and any(ancestor == hand_bone for ancestor in bone.parent_recursive)
        )
        axial_side = distribution.AxialSide(
            str(side["lower"]), str(side["helper"]), str(side["hand"]), finger_groups
        )
        vertices = [distribution.ForearmVertex(0.0, 0.0, weights) for weights in original]
        authored = distribution.author_axial_weights(
            vertices, polygons, [axial_side], {str(side["lower"])[-2:]: projected}, profile
        )
        result[side_name] = [dict(row) for row in authored.reference]
    return result


def posed_matrices(armature: bpy.types.Object, side: dict[str, object], hand_degrees: float, helper_degrees: float):
    explicit = {str(side["hand"]): math.radians(hand_degrees), str(side["helper"]): math.radians(helper_degrees)}
    poses = {}

    def resolve(bone: bpy.types.Bone):
        if bone.name in poses:
            return poses[bone.name]
        rest = bone.matrix_local.copy()
        if bone.name in explicit:
            from mathutils import Matrix
            pose = rest @ Matrix.Rotation(explicit[bone.name], 4, "Y")
        elif bone.parent is not None:
            pose = resolve(bone.parent) @ bone.parent.matrix_local.inverted() @ rest
        else:
            pose = rest
        poses[bone.name] = pose
        return pose

    for bone in armature.data.bones:
        resolve(bone)
    return poses


def geometry_metrics(mesh, armature, side, source_weights, hand_degrees, helper_degrees):
    neutral = [mesh.matrix_world @ vertex.co for vertex in mesh.data.vertices]
    frame = _side_frame(armature, side)
    selected = [
        index for index, point in enumerate(neutral)
        if 0.05 <= _coordinates(point, frame)[0] <= 1.15
        and math.hypot(*_coordinates(point, frame)[1:]) <= distribution.FOREARM_RADIUS
    ]
    deformed = _skin_vertices(
        mesh,
        armature,
        source_weights,
        posed_matrices(armature, side, hand_degrees, helper_degrees),
    )
    rings = [ring for ring in _ring_measurements(neutral, deformed, selected, frame) if 0.10 <= float(ring["t"]) <= 1.10]
    triangles = _triangle_indicators(mesh, neutral, deformed, set(selected))
    widths = [float(ring["width_ratio"]) for ring in rings]
    areas = [float(ring["area_ratio"]) for ring in rings]
    continuities = [min(a, b) / max(a, b) if max(a, b) > EPSILON else 0.0 for a, b in zip(widths, widths[1:])]
    minimum_width_index = min(range(len(rings)), key=lambda index: widths[index])
    minimum_area_index = min(range(len(rings)), key=lambda index: areas[index])
    return {
        "minimum_width_ratio": widths[minimum_width_index],
        "minimum_width_t": float(rings[minimum_width_index]["t"]),
        "minimum_area_ratio": areas[minimum_area_index],
        "minimum_area_t": float(rings[minimum_area_index]["t"]),
        "minimum_adjacent_ring_continuity": min(continuities),
        "maximum_adjacent_width_change": max(abs(a - b) for a, b in zip(widths, widths[1:])),
        "maximum_centreline_drift_metres": max(float(ring["centreline_drift"]) for ring in rings),
        "foldover_candidate_count": int(triangles["foldover_candidate_count"]),
        "minimum_triangle_area_ratio": float(triangles["minimum_area_ratio"]),
    }


def bend_metrics(mesh, armature, side, source_weights, manifest_side):
    """Both anatomical-bend metric families over the frozen population.

    Evaluation happens at the shipped runtime twist weight only: candidates
    differ in mesh weight distribution, not in the runtime helper-rotation
    weight, so the runtime weight is the single valid evaluation point
    (RIG-002 TR10).
    """

    evaluation = _evaluate_anatomical_bend(mesh, armature, side, source_weights, manifest_side)
    return {
        "frame": evaluation["frame"],
        "population": evaluation["population"],
        "runtime_weight_gates": evaluation["runtime_weight_gates"],
        "weights": evaluation["weights"],
        "manifest_parity": evaluation["manifest_parity"],
    }


def audit_asset(path: Path) -> dict[str, object]:
    before = _sha256(path)
    manifest_path = path.with_suffix(".forearm_twist_manifest.json")
    if not manifest_path.is_file():
        raise ProvenanceError(
            f"Missing pre-helper neutral manifest '{manifest_path}'; candidates may not define a new population."
        )
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if manifest.get("schema_version") != MANIFEST_SCHEMA_VERSION:
        raise ProvenanceError(f"Unsupported neutral manifest schema at '{manifest_path}'.")
    bpy.ops.wm.open_mainfile(filepath=str(path.resolve()), load_ui=False)
    armature = _require_single((item for item in bpy.context.scene.objects if item.type == "ARMATURE"), "armature")
    mesh = _require_single(
        (
            item for item in bpy.context.scene.objects
            if item.type == "MESH" and item.name.endswith(".body")
            and any(modifier.type == "ARMATURE" and modifier.object == armature for modifier in item.modifiers)
        ),
        "body mesh",
    )
    original = [_weights(mesh, vertex.index) for vertex in mesh.data.vertices]
    candidates = {profile.name: candidate_weights(mesh, armature, profile) for profile in PROFILES}
    output = {"source_sha256": before, "sides": {}}
    for side_name, side in SIDES.items():
        manifest_mesh = next((item for item in manifest["meshes"] if item["name"] == mesh.name), None)
        if manifest_mesh is None or side_name not in manifest_mesh["sides"]:
            raise ProvenanceError(f"Missing fixed bridge membership for {mesh.name}/{side_name}.")
        manifest_side = manifest_mesh["sides"][side_name]
        side_output = {}
        for candidate_name, helper_fraction, side_weights in [
            (BASELINE_CANDIDATE, distribution.SELECTED_PROFILE.helper_fraction, original),
            *[(profile.name, profile.helper_fraction, candidates[profile.name][side_name]) for profile in PROFILES],
        ]:
            scenarios = {}
            # The roll-neutral state (no hand rotation, no helper rotation) is
            # evidenced per candidate as the identity anchor.  It changes no
            # gate: every axial aggregate is a min/max over scenarios and the
            # neutral state is the identity anchor with ratios near 1.0.
            scenarios["neutral"] = geometry_metrics(mesh, armature, side, side_weights, 0.0, 0.0)
            for angle in ANGLES:
                for sign in (-1.0, 1.0):
                    signed = angle * sign
                    # Every candidate ships behind the same runtime TwistWeight;
                    # only the mesh weight distribution differs between
                    # candidates, so the helper rotates by the runtime weight
                    # times the roll, never by the profile helper fraction.
                    scenarios[f"roll_{signed:+.0f}"] = geometry_metrics(
                        mesh, armature, side, side_weights, signed, signed * RUNTIME_TWIST_WEIGHT
                    )
            bends = bend_metrics(mesh, armature, side, side_weights, manifest_side)
            side_output[candidate_name] = {
                "helper_fraction": helper_fraction,
                "helper_fraction_semantics": "documented mesh ownership setting; the runtime helper rotation is always RUNTIME_TWIST_WEIGHT x angle",
                "scenarios": scenarios,
                "anatomical_bend": bends,
            }
        output["sides"][side_name] = side_output
    if _sha256(path) != before:
        raise AssertionError(f"Read-only candidate audit changed {path}")
    return output


def aggregate(result: dict[str, object]) -> dict[str, object]:
    names = [BASELINE_CANDIDATE, *(profile.name for profile in PROFILES)]
    summaries = {}
    baseline_width = min(
        scenario["minimum_width_ratio"]
        for asset in result["assets"].values()
        for side in asset["sides"].values()
        for scenario in side[BASELINE_CANDIDATE]["scenarios"].values()
    )
    baseline_area = min(
        scenario["minimum_area_ratio"]
        for asset in result["assets"].values()
        for side in asset["sides"].values()
        for scenario in side[BASELINE_CANDIDATE]["scenarios"].values()
    )
    for name in names:
        scenarios = [
            scenario
            for asset in result["assets"].values()
            for side in asset["sides"].values()
            for scenario in side[name]["scenarios"].values()
        ]
        worst_width = min(item["minimum_width_ratio"] for item in scenarios)
        worst_area = min(item["minimum_area_ratio"] for item in scenarios)
        foldovers = sum(item["foldover_candidate_count"] for item in scenarios)
        axial_eligible = worst_width >= baseline_width + 0.08 and worst_area >= baseline_area + 0.10 and foldovers == 0
        axial_reason = (
            "whole-domain worst width/area improved without foldovers"
            if axial_eligible
            else "failed whole-domain improvement or foldover gate"
        )
        failed_bend_gates = sorted(
            gate_name
            for asset in result["assets"].values()
            for side in asset["sides"].values()
            for gate_name in side[name]["anatomical_bend"]["runtime_weight_gates"]["failed"]
        )
        bend_gates = {
            "all_passed": not failed_bend_gates,
            "failed": failed_bend_gates,
        }
        eligibility = evaluate_candidate_eligibility(axial_eligible, axial_reason, bend_gates)
        summaries[name] = {
            "worst_width_ratio": worst_width,
            "worst_area_ratio": worst_area,
            "worst_adjacent_ring_continuity": min(item["minimum_adjacent_ring_continuity"] for item in scenarios),
            "maximum_adjacent_width_change": max(item["maximum_adjacent_width_change"] for item in scenarios),
            "maximum_centreline_drift_metres": max(item["maximum_centreline_drift_metres"] for item in scenarios),
            "foldover_candidate_count": foldovers,
            "axial_eligible": axial_eligible,
            "bend_gates_passed": bend_gates["all_passed"],
            "failed_bend_gates": failed_bend_gates,
            "decision": eligibility["decision"],
            "reasons": eligibility["reasons"],
        }
    return summaries


def _not_measurable(output_path: Path, reason: str, required_inputs: list[Path]) -> int:
    result = {
        "schema_version": 4,
        "producer": "tools/rigging/optimise_forearm_twist.py",
        "status": "not_measurable",
        "reason": reason,
        "missing_paths": [path.resolve().as_posix() for path in sorted(required_inputs)],
        "required_inputs": [path.resolve().as_posix() for path in sorted(required_inputs)],
        "evidence_path": output_path.resolve().as_posix(),
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(result, sort_keys=True))
    return 2


def main() -> int:
    values = arguments()
    required_manifests = [values.female.with_suffix(".forearm_twist_manifest.json"), values.male.with_suffix(".forearm_twist_manifest.json")]
    missing = [path for path in required_manifests if not path.is_file()]
    if missing:
        return _not_measurable(values.output, "missing_pre_helper_neutral_manifest", missing)
    candidate_evidence = [values.female.with_suffix(".forearm_twist_candidate_weights.json"), values.male.with_suffix(".forearm_twist_candidate_weights.json")]
    missing = [path for path in candidate_evidence if not path.is_file()]
    if missing:
        return _not_measurable(values.output, "missing_candidate_weight_evidence", missing)
    result = {
        "schema_version": 4,
        "producer": "tools/rigging/optimise_forearm_twist.py",
        "study": "RIG-002 twist-only axial candidate study: the declared candidate set varying axial transition boundaries and connectivity smoothing (RIG-002 TR4)",
        "declared_candidate_table": "/tmp/opencode/rig-002-twist-only-axial-candidates.md",
        "classification_rule": (
            "eligibility requires BOTH the unchanged axial material-improvement rule "
            "(+0.08 worst width, +0.10 worst area, zero foldover candidates versus the "
            "pre-authoring baseline) AND every declared anatomical-bend gate, both "
            f"evaluated at the shipped runtime twist weight {RUNTIME_TWIST_WEIGHT}"
        ),
        "angles_degrees": list(ANGLES),
        "roll_signs": [-1, 1],
        "axial_scenario_extension": "roll-neutral (0 degrees hand, 0 degrees helper) added as an evidenced identity anchor; no gate reads it differently",
        "runtime_twist_weight": RUNTIME_TWIST_WEIGHT,
        "assets": {
            "female": audit_asset(values.female),
            "male": audit_asset(values.male),
        },
    }
    result["candidate_summaries"] = aggregate(result)
    values.output.parent.mkdir(parents=True, exist_ok=True)
    values.output.write_text(json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"evidence": str(values.output), "candidates": len(PROFILES)}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
