"""Numerical acceptance oracle for native sr and time-reversed sl."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

import bpy

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.contracts import ROLE_REGISTRY, action_name
from kimodo_pipeline.lateral_basis import anatomical_lateral_basis
from kimodo_pipeline.materialise import (
    ROOT as ROOT_PATH,
    _activate_action,
    _anatomical_character_scale,
    _curves,
    _loop_hierarchy_seam_metrics,
    _rig,
    _validate_loop_hierarchy_seam,
)
from kimodo_pipeline.precision_step import PrecisionStepLimits, assess_precision_foot_order
from kimodo_pipeline.source_repair import precision_leg_silhouette_evidence


def _samples(rig, action, end: int):
    _activate_action(rig, action)
    rows = []
    for frame in range(1, end + 1):
        bpy.context.scene.frame_set(frame)
        root_inverse = rig.pose.bones["Root"].matrix.inverted_safe()
        rows.append({name: root_inverse @ rig.pose.bones[name].matrix.copy() for name in (
            "Hips", "Chest", "LeftLeg", "LeftShin", "LeftFoot",
            "RightLeg", "RightShin", "RightFoot",
        )})
    return rows


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--variant", required=True)
    parser.add_argument("--report")
    options = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    rig, _, basis = _rig()
    scale = _anatomical_character_scale(rig)
    lateral_basis = anatomical_lateral_basis(rig, basis)
    declarations = {role.code: role for role in ROLE_REGISTRY}
    actions = {code: bpy.data.actions[action_name(options.variant, declarations[code])] for code in ("sr", "sl")}
    report = {"contract": "precision_step_output_temporal_reversal_oracle_v2", "anatomical_scale": scale, "roles": {}}
    sampled = {}
    for code, action in actions.items():
        end = int(action.frame_range[1])
        assert end == 40
        curves = _curves(action)
        root = [[float(curves[(f'{ROOT_PATH}.location', axis)].evaluate(frame)) for frame in range(1, end + 1)] for axis in range(3)]
        yaw = [[float(curves[(f'{ROOT_PATH}.rotation_euler', axis)].evaluate(frame)) for frame in range(1, end + 1)] for axis in range(3)]
        expected_sign = -1.0 if code == "sr" else 1.0
        assert root[0][-1] * expected_sign > scale * 0.08
        assert max(abs(value) for value in root[1] + root[2]) <= 1e-7
        assert max(abs(value) for axis in yaw for value in axis) <= 1e-7
        seam = _validate_loop_hierarchy_seam(rig, action, declarations[code])
        sampled[code] = _samples(rig, action, end)
        foot_order = assess_precision_foot_order(
            code,
            [
                (
                    frame,
                    float(row["LeftFoot"].translation.dot(lateral_basis.axis)),
                    float(row["RightFoot"].translation.dot(lateral_basis.axis)),
                )
                for frame, row in enumerate(sampled[code], 1)
            ],
            scale,
            stage="repaired_output",
            basis_evidence=lateral_basis.evidence,
        )
        assert foot_order["accepted"], foot_order
        leg_silhouette = precision_leg_silhouette_evidence(
            rig, action, 1, end, basis, "repaired_output", code,
        )
        assert leg_silhouette["accepted"], leg_silhouette
        trace = [
            float((row["LeftFoot"].translation - row["RightFoot"].translation).dot(lateral_basis.axis))
            for row in sampled[code]
        ]
        # The output gate must cover every authored frame, not only its worst
        # frame recorded by the source-repair provenance.
        assert foot_order["responsible_sample"] in range(1, end + 1)
        assert abs(min(trace) - foot_order["minimum_signed_separation"]) <= 1e-8
        limb_ranges = {}
        minimum_knee_bend = float("inf")
        contacts = {}
        for side in ("Left", "Right"):
            upper = [(row[f"{side}Shin"].translation - row[f"{side}Leg"].translation).length for row in sampled[code]]
            lower = [(row[f"{side}Foot"].translation - row[f"{side}Shin"].translation).length for row in sampled[code]]
            limb_ranges[side] = max(max(upper) - min(upper), max(lower) - min(lower))
            for row in sampled[code]:
                hip, knee, ankle = (row[f"{side}{suffix}"].translation for suffix in ("Leg", "Shin", "Foot"))
                axis = ankle - hip
                bend = knee - hip - axis * ((knee - hip).dot(axis) / max(axis.length_squared, 1e-12))
                minimum_knee_bend = min(minimum_knee_bend, bend.length)
            heights = [(row[f"{side}Foot"].translation - row["Hips"].translation).z for row in sampled[code]]
            ground = min(heights)
            contacts[side] = [index for index, height in enumerate(heights) if height <= ground + scale * 0.05]
        # The source rig contains small authored joint translations, so this is
        # a severe stretch guard rather than an assumption of rigid channels.
        assert max(limb_ranges.values()) <= scale * 0.10, limb_ranges
        assert minimum_knee_bend >= scale * 0.01
        report["roles"][code] = {
            "samples": end,
            "period": end - 1,
            "root_endpoint_lateral": root[0][-1],
            "maximum_root_forward": max(abs(value) for value in root[2]),
            "maximum_root_vertical": max(abs(value) for value in root[1]),
            "maximum_root_yaw": max(abs(value) for axis in yaw for value in axis),
            "maximum_limb_length_range": max(limb_ranges.values()),
            "minimum_knee_bend": minimum_knee_bend,
            "contact_topology": contacts,
            "complete_event_closure": seam["precision_complete_event_closure"],
            "cycle_extension_fidelity": seam["cycle_extension_fidelity"],
            "precision_foot_order": foot_order,
            "precision_leg_silhouette": leg_silhouette,
            "signed_foot_separation_trace": trace,
            "minimum_signed_foot_separation": min(trace),
            "maximum_scale_normalised_crossover_depth": foot_order["maximum_scale_normalised_crossover_depth"],
        }

    transform_error = height_error = 0.0
    for right, left in zip(reversed(sampled["sr"]), sampled["sl"]):
        for name in ("Hips", "Chest", "LeftLeg", "LeftShin", "LeftFoot", "RightLeg", "RightShin", "RightFoot"):
            transform_error = max(transform_error, max(
                abs(right[name][row][column] - left[name][row][column])
                for row in range(4) for column in range(4)
            ))
        for side in ("Left", "Right"):
            source_height = (right[f"{side}Foot"].translation - right["Hips"].translation).z
            target_height = (left[f"{side}Foot"].translation - left["Hips"].translation).z
            height_error = max(height_error, abs(source_height - target_height))
    endpoint_sum = report["roles"]["sr"]["root_endpoint_lateral"] + report["roles"]["sl"]["root_endpoint_lateral"]
    sample_count = report["roles"]["sr"]["samples"]
    assert abs(endpoint_sum) <= scale * 1e-5
    assert transform_error <= 5e-4, transform_error
    assert height_error <= scale * 5e-4, height_error
    separation_reversal_error = max(
        abs(right - left)
        for right, left in zip(
            reversed(report["roles"]["sr"]["signed_foot_separation_trace"]),
            report["roles"]["sl"]["signed_foot_separation_trace"],
        )
    )
    assert separation_reversal_error <= scale * 5e-4, separation_reversal_error
    silhouette_reversal_error = abs(
        report["roles"]["sr"]["precision_leg_silhouette"]["minimum_signed_separation"]
        - report["roles"]["sl"]["precision_leg_silhouette"]["minimum_signed_separation"]
    )
    assert silhouette_reversal_error <= scale * 5e-4, silhouette_reversal_error
    rejected_material_crossovers = {}
    for code in ("sr", "sl"):
        trace = report["roles"][code]["signed_foot_separation_trace"]
        responsible_frame = min(range(1, len(trace) + 1), key=lambda frame: (trace[frame - 1], frame))
        # Exercise a material inversion at an evaluated output frame. This is
        # deliberately a generic synthetic observation, not a source exception.
        crossover = assess_precision_foot_order(
            code,
            [(frame, -scale * 0.031, 0.0) if frame == responsible_frame else (frame, trace[frame - 1], 0.0)
             for frame in range(1, len(trace) + 1)],
            scale,
            stage="repaired_output",
            basis_evidence=lateral_basis.evidence,
            limits=PrecisionStepLimits(maximum_crossover_depth_scale=0.03),
        )
        assert not crossover["accepted"], crossover
        rejected_material_crossovers[code] = {
            "injected_output_frame": responsible_frame,
            "maximum_scale_normalised_crossover_depth": crossover["maximum_scale_normalised_crossover_depth"],
            "rejection_reason": crossover["rejection_reason"],
        }
    contact_difference = 0
    for source_side, target_side in (("Left", "Left"), ("Right", "Right")):
        source_contacts = set(report["roles"]["sr"]["contact_topology"][source_side])
        target_contacts = {sample_count - frame + 1 for frame in report["roles"]["sl"]["contact_topology"][target_side]}
        contact_difference = max(contact_difference, len(source_contacts ^ target_contacts))
    report["temporal_reversal"] = {
        "derivation": "sl is the same-anatomy temporal reversal of sr",
        "root_endpoint_sum": endpoint_sum,
        "maximum_root_relative_transform_error": transform_error,
        "maximum_relative_foot_height_error": height_error,
        "maximum_contact_topology_temporal_difference_samples": contact_difference,
        # Contact is explicitly undetermined in this materialiser. Foot-height
        # trace equality above is the authoritative temporal check; this
        # quantised near-ground set remains diagnostic only.
        "contact_topology_same_anatomy_reversed_diagnostic": True,
        "maximum_signed_foot_separation_trace_error": separation_reversal_error,
        "minimum_signed_leg_silhouette_reversal_error": silhouette_reversal_error,
        "signed_foot_separation_traces_temporally_equivalent": True,
        "material_crossovers_rejected_for_roles": rejected_material_crossovers,
    }
    if options.report:
        Path(options.report).write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")
    print("KIMODO_PRECISION_STEP_OUTPUT=" + json.dumps(report, sort_keys=True))


main()
