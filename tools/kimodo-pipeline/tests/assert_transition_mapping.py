"""Blender-side assertions for source-derived forward and backward transition windows."""
from __future__ import annotations

import hashlib
import json
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import bpy

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from kimodo_pipeline.compatibility import ensure_registered
from kimodo_pipeline.contracts import ROLE_REGISTRY, action_name, transition_boundary_phase
from kimodo_pipeline.discovery import REFERENCE_ACTION_NAME, _action_fcurves, _segments
from kimodo_pipeline.materialise import (
    _activate_action, _candidate_quality_metrics, _foot_geometry, _rig,
    _source_walk_role, _source_window_content_signature,
)
from kimodo_pipeline.selection import (
    SelectionError,
    quality_evidence,
    release_event_evidence,
    release_events,
    role_window,
    score_candidate,
    select_candidate,
)
from kimodo_pipeline.source_repair import action_is_candidate_for_role


def _mirror_path(path: str) -> str:
    for left, right in (("Left", "Right"), ("Right", "Left"), ("_l", "_r"), ("_r", "_l")):
        if left in path:
            return path.replace(left, right)
    return path


def _value(path: str, index: int, value: float, derivation: str) -> float:
    if "mirror" in derivation and ((path.endswith(".location") and index == 0) or (path.endswith(".rotation_euler") and index in (1, 2))):
        return -value
    return value


def _json_value(value: Any) -> Any:
    """Return a stable JSON value for a Blender RNA or ID-property value."""
    if hasattr(value, "to_list"):
        return _json_value(value.to_list())
    if isinstance(value, (list, tuple)):
        return [_json_value(item) for item in value]
    if isinstance(value, dict):
        return {str(key): _json_value(item) for key, item in sorted(value.items())}
    if isinstance(value, (str, int, float, bool)) or value is None:
        return value
    return str(value)


def _action_content_hash(action: Any) -> str:
    """Fingerprint complete Action content without adding diagnostics to it."""
    curves = []
    for curve in sorted(_action_fcurves(action), key=lambda item: (item.data_path, item.array_index)):
        curves.append({
            "data_path": curve.data_path,
            "array_index": curve.array_index,
            "extrapolation": curve.extrapolation,
            "keys": [
                {
                    "co": list(point.co), "handle_left": list(point.handle_left), "handle_right": list(point.handle_right),
                    "interpolation": point.interpolation, "handle_left_type": point.handle_left_type,
                    "handle_right_type": point.handle_right_type,
                }
                for point in curve.keyframe_points
            ],
            "modifiers": [
                {
                    property_definition.identifier: _json_value(getattr(modifier, property_definition.identifier))
                    for property_definition in modifier.bl_rna.properties
                    if property_definition.identifier != "rna_type" and not property_definition.is_readonly
                    and property_definition.type not in {"POINTER", "COLLECTION"}
                }
                for modifier in curve.modifiers
            ],
        })
    payload = {
        "name": action.name, "use_fake_user": action.use_fake_user, "frame_range": list(action.frame_range),
        "id_properties": {key: _json_value(action[key]) for key in sorted(action.keys())},
        "slots": [{"identifier": slot.identifier, "target_id_type": slot.target_id_type} for slot in action.slots],
        "curves": curves,
    }
    return hashlib.sha256(json.dumps(payload, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest()


def _action_snapshot() -> dict[str, str]:
    return {action.name: _action_content_hash(action) for action in sorted(bpy.data.actions, key=lambda item: item.name)}


def _select_role(role: Any, segment: dict[str, int], sources: list[Any], rig: Any, rig_basis: Any) -> tuple[Any, list[dict[str, Any]], list[dict[str, Any]]]:
    """Mirror materialiser candidate eligibility without materialising Actions."""
    source_code = _source_walk_role(role)
    candidates = []
    candidate_rows = []
    rejected = []
    for action in (item for item in sources if action_is_candidate_for_role(item, source_code)):
        geometry = _foot_geometry(rig, action, segment["start_frame"], segment["end_frame"], rig_basis)
        phase_rows = [
            row for side in ("Left", "Right")
            for row in release_event_evidence(segment, geometry, side, source_code)
        ]
        try:
            expected_start, expected_end = role_window(
                segment, source_code, _action_fcurves(action), geometry,
                role.derivation, role.code,
            )
            candidate = score_candidate(action.name, _action_fcurves(action), expected_start, expected_end)
            candidates.append(candidate)
            candidate_rows.append({
                "action": action.name, "source_range": [expected_start, expected_end],
                "phase": {"event_rows": phase_rows}, "selection": {"accepted": True, "reason": "role window derived from evidenced source phase"},
                "ranking_evidence": candidate.evidence,
            })
        except SelectionError as error:
            row = {"action": action.name, "source_range": None, "phase": {"event_rows": phase_rows}, "selection": {"accepted": False, "reason": str(error)}}
            candidate_rows.append(row)
            rejected.append({"action": action.name, "reason": str(error), "stage": "phase"})
    metrics = {
        candidate.action_name: _candidate_quality_metrics(
            rig, bpy.data.actions[candidate.action_name], candidate.start, candidate.end, rig_basis
        )
        for candidate in candidates
    }
    quality = quality_evidence(metrics)
    retained = []
    for candidate in candidates:
        evidence = quality[candidate.action_name]
        row = next(row for row in candidate_rows if row["action"] == candidate.action_name)
        row["quality"] = evidence
        if evidence["accepted"]:
            retained.append(candidate)
        else:
            row["selection"] = {"accepted": False, "reason": "; ".join(evidence["reasons"])}
            rejected.append({"action": candidate.action_name, "reason": "; ".join(evidence["reasons"]), "stage": "quality", "quality": evidence})
    if not retained:
        raise AssertionError(f"{role.code} has no valid source candidates; rejected: {rejected}")
    selected = select_candidate(retained)
    for rank, candidate in enumerate(sorted(retained, key=lambda item: (item.action_name, item.start, item.end)), 1):
        row = next(row for row in candidate_rows if row["action"] == candidate.action_name)
        row["deterministic_ranking"] = {"rank": rank, "sort_key": [candidate.action_name, candidate.start, candidate.end]}
    return selected, candidate_rows, rejected


def _assert_role(role: Any, segment: dict[str, int], sources: list[Any], variant: str, rig: Any, rig_basis: Any) -> dict[str, Any]:
    selected, candidate_rows, rejected = _select_role(role, segment, sources, rig, rig_basis)
    output = bpy.data.actions[action_name(variant, role)]
    expected_start, expected_end = selected.start, selected.end
    selected_name = selected.action_name
    if not role.is_loop:
        durable = json.loads(output["anim002_transition_evidence"])
        crossing = durable["selected_crossing"]
        source_frame = int(crossing["source_event"]["source_frame"])
        output_frame = int(crossing["output_frame"])
        output_end = int(output.frame_range[1])
        if "reverse" in role.derivation:
            expected_end = source_frame + output_frame - 1
            expected_start = expected_end - output_end + 1
        else:
            expected_start = source_frame - output_frame + 1
            expected_end = expected_start + output_end - 1
        signature = durable["source_window_content_signature"]
        matches = [
            action for action in sources
            if _source_window_content_signature(action, expected_start, expected_end) == signature
        ]
        assert len(matches) == 1, (role.code, expected_start, expected_end, signature, [item.name for item in matches])
        selected_name = matches[0].name
    source = bpy.data.actions[selected_name]
    frames = list(range(expected_start, expected_end + 1))
    if "reverse" in role.derivation:
        frames.reverse()
    assert int(output.frame_range[1]) == len(frames), (role.code, output.frame_range[:], len(frames))
    # Raw Euler channels are not an oracle: equivalent continuous ZYX solves
    # may differ by a full revolution. Full evaluated matrices are asserted for
    # every mapped frame by the materialiser and role-family oracle regressions.
    expectation = transition_boundary_phase(role.code)
    output_end = int(output.frame_range[1])
    geometry = _foot_geometry(rig, output, 1, output_end, rig_basis)
    # Validate the suffix from output motion, not Action metadata or its name.
    if role.is_loop:
        lead = "Right" if role.code.endswith("_r") else "Left"
        other = "Left" if lead == "Right" else "Right"
        assert abs(geometry[lead][2].speed) >= abs(geometry[other][2].speed) + 1e-4, (role.code, lead)
        assert abs(geometry[lead][output_end].speed) >= abs(geometry[other][output_end].speed) + 1e-4, (role.code, lead, "recurrence")
        output_context = {
            "kind": "loop", "release_foot_side": lead,
            "output_release_frames": [2, output_end],
        }
    else:
        assert expectation is not None, role.code
        edge, lead = expectation
        events = [
            (side, frame)
            for side in ("Left", "Right")
            for frame in release_events({"start_frame": 1, "end_frame": output_end}, geometry, side, role.code)
        ]
        selected_event = (lead, output_end) if edge == "end" else (lead, 2)
        same_side = [event for event in events if event[0] == lead]
        assert not same_side or selected_event in same_side, (role.code, events)
        output_rows = [
            row for side in ("Left", "Right")
            for row in release_event_evidence({"start_frame": 1, "end_frame": output_end}, geometry, side, role.code)
        ]
        output_context = {
            "kind": "transition", "boundary_edge": edge, "release_foot_side": lead,
            "eligible_output_crossings": events, "output_event_rows": output_rows,
            "selected_output_crossing": selected_event,
            "surrounding_gait_crossings": [event for event in events if event != selected_event],
        }
    return {
        "selected": {"source": selected_name, "range": [expected_start, expected_end], "derivation": role.derivation},
        "eligible_sources": [row["action"] for row in candidate_rows if row["selection"]["accepted"]],
        "rejected_sources": rejected,
        "candidates": candidate_rows,
        "output_phase_crossing_edge_context": output_context,
    }


def main() -> None:
    variant = sys.argv[sys.argv.index("--variant") + 1]
    audit_directory = Path(sys.argv[sys.argv.index("--audit-dir") + 1]).resolve() if "--audit-dir" in sys.argv else None
    blend_path = Path(bpy.data.filepath).resolve()
    blend_hash_before = hashlib.sha256(blend_path.read_bytes()).hexdigest()
    action_snapshot_before = _action_snapshot()
    # Saved custom RNA needs its fallback schema registered before observing
    # motion segments. Registration is process-local and is never saved.
    ensure_registered()
    segments = {item["index"]: item for item in _segments()}
    sources = [action for action in bpy.data.actions if action.name.startswith("kimodo_multi_")]
    rig, _, rig_basis = _rig()
    # Materialisation samples source Actions from the saved reference pose.
    # Published blends intentionally reopen with no generated Action assigned,
    # but Blender can retain the last evaluated generated pose at the unchanged
    # scene frame. Re-establish the source-only precondition before independently
    # deriving transition windows; generated output remains present and audited.
    _activate_action(rig, bpy.data.actions[REFERENCE_ACTION_NAME])
    bpy.context.scene.frame_set(1)
    bpy.context.view_layer.update()
    roles = [role for role in ROLE_REGISTRY if role.segment_index == 0]
    assert {role.code for role in roles} == {
        "wf_r", "wf_l", "wb_r", "wb_l",
        *(f"{form}_{side}" for form in ("i2f", "f2i", "i2b", "b2i") for side in ("l", "r")),
    }
    target_names = {action_name(variant, role) for role in roles}
    non_target_snapshot_before = {
        name: content_hash for name, content_hash in action_snapshot_before.items()
        if name not in target_names
    }
    role_audit = {
        role.code: _assert_role(role, segments[role.segment_index], sources, variant, rig, rig_basis)
        for role in roles
    }
    action_snapshot_after = _action_snapshot()
    non_target_snapshot_after = {
        name: content_hash for name, content_hash in action_snapshot_after.items()
        if name not in target_names
    }
    blend_hash_after = hashlib.sha256(blend_path.read_bytes()).hexdigest()
    assert blend_hash_after == blend_hash_before, "read-only assertion changed the source blend"
    assert action_snapshot_after == action_snapshot_before, "read-only assertion changed Action content in memory"
    assert non_target_snapshot_after == non_target_snapshot_before, "read-only assertion changed non-target Actions in memory"
    report = {
        "source_blend": str(blend_path), "source_blend_sha256_before": blend_hash_before,
        "source_blend_sha256_after": blend_hash_after, "action_content_hashes": action_snapshot_after,
        "generated_actions": {action_name(variant, role): role_audit[role.code]["selected"] for role in roles},
        "roles": role_audit,
        "non_target_snapshot_comparison": {"unchanged": True, "before": non_target_snapshot_before, "after": non_target_snapshot_after},
        "edge_evidence_residue": {"detected": False, "reason": "Action hashes and non-target snapshot are unchanged; edge evidence exists only in this manifest."},
    }
    if audit_directory is not None:
        temp_root = (ROOT / "game/temp").resolve()
        assert audit_directory.is_relative_to(temp_root), f"audit directory must be below {temp_root}"
        audit_directory.mkdir(parents=True, exist_ok=True)
        run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
        manifest = audit_directory / f"transition-mapping-{blend_hash_before}-{run_id}.json"
        assert not manifest.exists(), f"refusing to overwrite immutable audit manifest: {manifest}"
        manifest.write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")
        report["manifest"] = str(manifest)
    # Blender truncates very long console records. The immutable manifest holds
    # complete evidence; this compact record is intentionally only the test seam.
    console_report = {
        "roles": {
            code: {"selected": audit["selected"], "rejected_sources": audit["rejected_sources"]}
            for code, audit in role_audit.items()
        },
        "source_blend_sha256_before": blend_hash_before,
        "source_blend_sha256_after": blend_hash_after,
    }
    print("KIMODO_TRANSITION_MAPPING_JSON=" + json.dumps(console_report, sort_keys=True))


main()
