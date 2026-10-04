"""Read-only numerical inventory of complete Segment-1 precision candidates."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

import bpy

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.compatibility import ensure_registered
from kimodo_pipeline.contracts import ROLE_REGISTRY
from kimodo_pipeline.discovery import REFERENCE_ACTION_NAME
from kimodo_pipeline.loop_seam import symmetric_window
from kimodo_pipeline.materialise import (
    _anatomical_character_scale,
    _candidate_quality_metrics,
    _foot_geometry,
    _repair_periodic_pose_rows,
    _rig,
    _segments,
)
from kimodo_pipeline.precision_step import (
    PRECISION_CALIBRATION_BASELINE_ACTION,
    PrecisionStepLimits,
    assess_precision_semantic_events,
    select_precision_candidate,
)
from kimodo_pipeline.selection import quality_evidence
from kimodo_pipeline.source_repair import (
    MAXIMUM_SOURCE_ROTATION_RADIANS,
    MAXIMUM_SOURCE_TRANSLATION_SCALE,
    PROVENANCE_PROPERTY,
    _precision_anatomy_contact_check,
    _precision_foot_order_from_rows,
    _precision_leg_silhouette_from_rows,
    _sample_window,
    _semantic_source_candidates,
    source_window_content_signature,
)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--blend", required=True)
    parser.add_argument("--report", required=True)
    options = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    ensure_registered()
    bpy.ops.wm.open_mainfile(filepath=str(Path(options.blend).resolve()), load_ui=False, use_scripts=False)
    rig, _, basis = _rig()
    scale = _anatomical_character_scale(rig)
    segment = next(row for row in _segments() if row["index"] == 1)
    start, end = int(segment["start_frame"]), int(segment["end_frame"])
    frames = list(range(start, end + 1))
    reference = bpy.data.actions.get(REFERENCE_ACTION_NAME)
    actions = _semantic_source_candidates(rig, segment, (reference,) if reference is not None else ())
    role = next(role for role in ROLE_REGISTRY if role.code == "sr")
    rows = []
    quality_metrics = {}
    admitted = []
    for action in actions:
        if action.get(PROVENANCE_PROPERTY):
            continue
        signature = source_window_content_signature(action, start, end)
        row = {
            "source_action": action.name,
            "source_window_content_signature": signature,
            "source_range": [start, end],
        }
        try:
            geometry = _foot_geometry(rig, action, start, end, basis)
            event_samples = [
                (frame, geometry["Left"][frame].velocity[0], geometry["Left"][frame].speed,
                 geometry["Right"][frame].velocity[0], geometry["Right"][frame].speed)
                for frame in frames
            ]
            trajectory = [
                (geometry["Left"][frame].lateral_position + geometry["Right"][frame].lateral_position) * .5
                for frame in frames
            ]
            row["precision_semantic_events"] = assess_precision_semantic_events(event_samples, trajectory, scale)
            if not row["precision_semantic_events"]["accepted"]:
                row["rejection_stage"] = "complete_precision_event"
                row["rejection_reason"] = row["precision_semantic_events"]["rejection_reason"]
                rows.append(row)
                continue
            _, original, _, _ = _sample_window(rig, action, frames, role, basis)
            pre = _precision_foot_order_from_rows(rig, original, frames, scale, basis, "immutable_pre_repair")
            row["pre_repair_foot_order"] = pre
            silhouette_pre = _precision_leg_silhouette_from_rows(rig, original, frames, scale, basis, "immutable_pre_repair")
            row["pre_repair_leg_silhouette"] = silhouette_pre
            if not silhouette_pre["accepted"]:
                row["rejection_stage"] = "immutable_pre_repair_leg_silhouette"
                row["rejection_reason"] = silhouette_pre["rejection_reason"]
                rows.append(row)
                continue
            if not pre["accepted"]:
                row["rejection_stage"] = "immutable_pre_repair_foot_order"
                row["rejection_reason"] = pre["rejection_reason"]
                rows.append(row)
                continue
            repaired, assessment = _repair_periodic_pose_rows(original, scale)
            post = _precision_foot_order_from_rows(rig, repaired, frames, scale, basis, "repaired_output")
            row["post_repair_foot_order"] = post
            silhouette_post = _precision_leg_silhouette_from_rows(rig, repaired, frames, scale, basis, "repaired_output")
            row["post_repair_leg_silhouette"] = silhouette_post
            row["repair_cost"] = assessment["repair_cost"]
            failures = dict(assessment["failures"])
            translation_limit = MAXIMUM_SOURCE_TRANSLATION_SCALE * scale
            if assessment["maximum_translation_correction"] > translation_limit:
                failures["source_translation_correction"] = {
                    "value": assessment["maximum_translation_correction"], "threshold": translation_limit,
                }
            if assessment["maximum_rotation_correction_rad"] > MAXIMUM_SOURCE_ROTATION_RADIANS:
                failures["source_rotation_correction"] = {
                    "value": assessment["maximum_rotation_correction_rad"],
                    "threshold": MAXIMUM_SOURCE_ROTATION_RADIANS,
                }
            opening, closing = symmetric_window(len(frames))
            row["anatomy_contact"] = _precision_anatomy_contact_check(
                original, repaired, scale, basis, range(opening, len(frames) - closing), assessment,
            )
            row["repair_failures"] = failures
            if not silhouette_post["accepted"]:
                row["rejection_stage"] = "repaired_output_leg_silhouette"
                row["rejection_reason"] = silhouette_post["rejection_reason"]
            elif not post["accepted"]:
                row["rejection_stage"] = "repaired_output_foot_order"
                row["rejection_reason"] = post["rejection_reason"]
            elif failures:
                row["rejection_stage"] = "bounded_repair"
                row["rejection_reason"] = str(failures)
            else:
                quality_metrics[signature] = _candidate_quality_metrics(rig, action, start, end, basis)
                admitted.append(row)
        except Exception as error:
            row["rejection_stage"] = "semantic_or_safety_admission"
            row["rejection_reason"] = str(error)
        rows.append(row)

    quality = quality_evidence(quality_metrics, "precision_lateral") if quality_metrics else {}
    eligible = []
    for row in admitted:
        row["quality"] = quality[row["source_window_content_signature"]]
        if row["quality"]["accepted"]:
            eligible.append(row)
        else:
            row["rejection_stage"] = "generic_quality"
            row["rejection_reason"] = "; ".join(row["quality"]["reasons"])
    baseline = next((row for row in eligible if row["source_action"] == PRECISION_CALIBRATION_BASELINE_ACTION), None)
    calibration = None
    if baseline is not None:
        measured = baseline["quality"]["metrics"]["planted_foot_slip"]
        margin = PrecisionStepLimits().calibration_sliding_margin_scale
        calibration = {
            "baseline_action": PRECISION_CALIBRATION_BASELINE_ACTION,
            "measured_normalised_planted_foot_slide": measured,
            "fixed_margin": margin,
            "precision_sliding_maximum": measured + margin,
        }
        for row in tuple(eligible):
            row["precision_sliding_calibration"] = {**calibration, "is_baseline": row is baseline}
            if row is not baseline and row["quality"]["metrics"]["planted_foot_slip"] > calibration["precision_sliding_maximum"]:
                eligible.remove(row)
                row["rejection_stage"] = "calibrated_precision_sliding"
                row["rejection_reason"] = "normal precision candidate planted-foot slide exceeds calibrated precision sliding maximum"
    selected = None
    if eligible:
        decision = select_precision_candidate([
            {
                "phase_compatible": True,
                "foot_order_accepted": True,
                "quality_accepted": True,
                "repair_accepted": True,
                "repair_cost": row["repair_cost"],
                "source_window_content_signature": row["source_window_content_signature"],
            }
            for row in eligible
        ])
        selected = next(
            row for row in eligible
            if row["source_window_content_signature"] == decision["source_window_content_signature"]
        )["source_action"]
    report = {
        "contract": "read_only_precision_candidate_inventory_complete_event_v3",
        "blend": str(Path(options.blend).resolve()),
        "anatomical_scale": scale,
        "normalised_crossover_threshold": 0.03,
        "eligible_candidates": [row["source_action"] for row in eligible],
        "selected_source": selected,
        "precision_sliding_calibration": calibration,
        "candidates": rows,
    }
    path = Path(options.report).resolve()
    path.write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")
    print("KIMODO_PRECISION_CANDIDATE_ANALYSIS=" + json.dumps(report, sort_keys=True))


main()
