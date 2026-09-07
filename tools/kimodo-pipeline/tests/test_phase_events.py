"""Pure publication tests for durable ANIM-002 phase-event evidence."""
from __future__ import annotations

from copy import deepcopy
from pathlib import Path
import sys
from typing import Any

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from kimodo_pipeline.phase_events import (
    DERIVATION_EVENT_CONTRACT,
    POST_REPAIR_EVENT_CONTRACT,
    SEED_DISPLACEMENT_CONTRACT,
    SOURCE_DIRECTIONAL_EVENT_CONTRACT,
    PhaseEventEvidenceError,
    canonicalise_duplicate_endpoint_events,
    canonical_frame_delta,
    derive_output_event,
    derivation_operations,
    transform_boundary_context,
    transform_event_kind,
    validate_complete_event_provenance,
    validate_phase_event_evidence,
)


def applicable(role: str, events: list[dict[str, Any]], end: int = 41) -> dict[str, Any]:
    return {
        "version": 1,
        "applicability": "applicable",
        "algorithm": "kimodo_evaluated_release_v1",
        "timeline": {"frame_start": 1, "frame_end": end, "fps": 30.0},
        "events": events,
    }


def release(side: str, frame: int, boundary: str | None = None) -> dict[str, Any]:
    return {
        "id": f"release_{side[0].lower()}",
        "anatomical_side": side,
        "output_frame": frame,
        "source_frame": 100 + frame,
        "boundary_context": boundary,
        "selector_confidence": 0.83,
    }


def test_bilateral_walk_preserves_independent_non_half_phase_frames() -> None:
    evidence = applicable("wf", [release("Left", 13), release("Right", 1)])
    assert validate_phase_event_evidence("wf", evidence, 1, 41) == evidence
    frames = {row["output_frame"] for row in evidence["events"]}
    assert frames == {1, 13}
    assert 21 not in frames  # No opposite-event phase-0.5 inference.


@pytest.mark.parametrize("role,side", [
    ("xl_l", "Left"), ("xr_l", "Left"), ("al_l", "Left"), ("ar_l", "Left"),
    ("xl_r", "Right"), ("xr_r", "Right"), ("al_r", "Right"), ("ar_r", "Right"),
])
def test_sided_travel_requires_exactly_its_declared_event(role: str, side: str) -> None:
    validate_phase_event_evidence(role, applicable(role, [release(side, 1)]), 1, 41)


@pytest.mark.parametrize("role,boundary,frame,side", [
    ("i2f_l", "end", 41, "Left"), ("i2f_r", "end", 41, "Right"),
    ("i2b_l", "end", 41, "Left"), ("i2b_r", "end", 41, "Right"),
    ("f2i_l", "start", 1, "Left"), ("f2i_r", "start", 1, "Right"),
    ("b2i_l", "start", 1, "Left"), ("b2i_r", "start", 1, "Right"),
])
def test_transition_requires_its_locomotion_facing_boundary(role: str, boundary: str, frame: int, side: str) -> None:
    validate_phase_event_evidence(role, applicable(role, [release(side, frame, boundary)]), 1, 41)


@pytest.mark.parametrize("role", ["sl", "sr", "pl45", "pr45"])
def test_confined_roles_are_explicitly_inapplicable(role: str) -> None:
    evidence = {"version": 1, "applicability": "inapplicable", "events": []}
    assert validate_phase_event_evidence(role, evidence, 1, 41) == evidence


@pytest.mark.parametrize("mutation", ["missing", "duplicate", "range", "anatomy", "boundary"])
def test_publication_rejects_invalid_applicable_evidence(mutation: str) -> None:
    evidence = applicable("i2f_l", [release("Left", 41, "end")])
    if mutation == "missing":
        evidence["events"] = []
    elif mutation == "duplicate":
        evidence["events"].append(deepcopy(evidence["events"][0]))
    elif mutation == "range":
        evidence["events"][0]["output_frame"] = 42
    elif mutation == "anatomy":
        evidence["events"][0]["anatomical_side"] = "Right"
    else:
        evidence["events"][0]["boundary_context"] = "start"
    with pytest.raises(PhaseEventEvidenceError):
        validate_phase_event_evidence("i2f_l", evidence, 1, 41)


def source_release(side: str = "Left", frame: int = 3, boundary: str | None = "start") -> dict[str, Any]:
    return {
        "id": f"release_{side[0].lower()}", "anatomical_side": side,
        "event_kind": "Release", "source_frame": frame, "boundary_context": boundary,
        "selector_confidence": 0.91, "selector_evidence": {"immutable": True},
    }


def displacement(derived: int, output: int, delta: int = 0) -> dict[str, Any]:
    return {
        "contract": SEED_DISPLACEMENT_CONTRACT,
        "derived_output_frame": derived,
        "post_repair_output_frame": output,
        "canonical_delta_frames": delta,
        "source_detector_stencil_frames": [derived],
        "post_repair_detector_stencil_frames": [output],
        "repair_support_dilated_frames": [derived, output] if delta else [],
        "source_repair_intersection_frames": [derived] if delta else [],
        "post_repair_repair_intersection_frames": [output] if delta else [],
        "displacement_authorised": bool(delta),
    }


@pytest.mark.parametrize("derivation,side,kind,frame,boundary", [
    ("mirror", "Right", "Release", 3, "start"),
    ("reverse", "Left", "Acquire", 7, "end"),
    ("reverse_mirror", "Right", "Acquire", 7, "end"),
    ("mirror_reverse", "Right", "Acquire", 7, "end"),
])
def test_derivation_transforms_are_explicit_and_compose_independently_of_token_order(
    derivation: str, side: str, kind: str, frame: int, boundary: str,
) -> None:
    output = derive_output_event(
        source_release(), derivation=derivation, source_start=1, source_end=9,
    )
    assert (output["anatomical_side"], output["event_kind"], output["output_frame"], output["boundary_context"]) == (
        side, kind, frame, boundary,
    )
    assert derivation_operations(derivation) == tuple(
        operation for operation in ("reverse", "mirror") if operation in derivation
    )


def test_reverse_swaps_release_acquire_and_finite_boundaries_both_ways() -> None:
    assert transform_event_kind("Release", "reverse") == "Acquire"
    assert transform_event_kind("Acquire", "reverse") == "Release"
    assert transform_boundary_context("start", "reverse") == "end"
    assert transform_boundary_context("end", "reverse") == "start"
    assert transform_event_kind("Release", "mirror") == "Release"
    assert transform_boundary_context("start", "mirror") == "start"


def test_duplicate_endpoint_is_canonicalised_to_unique_runtime_sample() -> None:
    rows = [
        {"id": "release_l", "event_kind": "Release", "anatomical_side": "Left", "output_frame": 1},
        {"id": "release_l", "event_kind": "Release", "anatomical_side": "Left", "output_frame": 9},
        {"id": "release_r", "event_kind": "Release", "anatomical_side": "Right", "output_frame": 9},
    ]
    result = canonicalise_duplicate_endpoint_events(rows, frame_start=1, frame_end=9, duplicate_endpoint=True)
    assert [(row["id"], row["output_frame"]) for row in result] == [("release_l", 1), ("release_r", 1)]
    assert result[-1]["canonicalised_from_duplicate_endpoint"] == 9


def test_complete_provenance_rejects_stale_cross_carrier_event() -> None:
    event = source_release(boundary=None)
    source = {"contract": SOURCE_DIRECTIONAL_EVENT_CONTRACT, "source_interval": [1, 9], "events": [event]}
    derived = derive_output_event(event, derivation="native", source_start=1, source_end=9)
    derivation = {
        "contract": DERIVATION_EVENT_CONTRACT, "derivation": "native", "operations": [],
        "frame_mapping": {"source_start": 1, "source_end": 9, "output_start": 1, "output_end": 9},
        "events": [derived],
    }
    post = {
        "contract": POST_REPAIR_EVENT_CONTRACT, "evaluation_stage": "final_repaired_evaluated_hierarchy",
        "timeline": {"frame_start": 1, "frame_end": 9, "fps": 30.0},
        "sample_semantics": {"is_loop": True, "conceptual_sample_count": 9, "unique_runtime_sample_count": 8, "duplicate_endpoint": True},
        "events": [{**derived, "derivation_event_id": derived["id"], "derived_output_frame": derived["output_frame"], "repair_frame_delta": 0, "seed_displacement_provenance": displacement(derived["output_frame"], derived["output_frame"]), "accepted": True, "confidence": 0.8, "measurement": {"evaluated": True}}],
        "refusals": [],
    }
    validate_complete_event_provenance("xl_l", source, derivation, post, 1, 9)
    post_repair_shift = deepcopy(post)
    post_repair_shift["events"][0]["output_frame"] = 4
    post_repair_shift["events"][0]["repair_frame_delta"] = 1
    post_repair_shift["events"][0]["seed_displacement_provenance"] = displacement(3, 4, 1)
    assert validate_complete_event_provenance(
        "xl_l", source, derivation, post_repair_shift, 1, 9,
    )["post_repair_event_evidence"]["events"][0]["output_frame"] == 4
    blocked_displacement = deepcopy(post_repair_shift)
    blocked_displacement["events"][0]["seed_displacement_provenance"] = displacement(3, 4, 0)
    blocked_displacement["events"][0]["seed_displacement_provenance"]["canonical_delta_frames"] = 1
    with pytest.raises(PhaseEventEvidenceError, match="frame-shift link"):
        validate_complete_event_provenance("xl_l", source, derivation, blocked_displacement, 1, 9)
    post["events"][0]["output_frame"] = 4
    with pytest.raises(PhaseEventEvidenceError, match="frame-shift link"):
        validate_complete_event_provenance("xl_l", source, derivation, post, 1, 9)


def test_cyclic_frame_delta_uses_shortest_signed_distance() -> None:
    assert canonical_frame_delta(38, 1, frame_start=1, frame_end=39, duplicate_endpoint=True) == -1
    assert canonical_frame_delta(38, 1, frame_start=1, frame_end=39, duplicate_endpoint=False) == 37


def test_old_post_repair_physical_contract_is_rejected() -> None:
    event = source_release(boundary=None)
    source = {"contract": SOURCE_DIRECTIONAL_EVENT_CONTRACT, "source_interval": [1, 9], "events": [event]}
    derived = derive_output_event(event, derivation="native", source_start=1, source_end=9)
    derivation = {
        "contract": DERIVATION_EVENT_CONTRACT, "derivation": "native", "operations": [],
        "frame_mapping": {"source_start": 1, "source_end": 9, "output_start": 1, "output_end": 9},
        "events": [derived],
    }
    post = {
        "contract": "anim002_post_repair_physical_events_v1",
        "evaluation_stage": "final_repaired_evaluated_hierarchy",
        "timeline": {"frame_start": 1, "frame_end": 9, "fps": 30.0},
        "sample_semantics": {"is_loop": True, "conceptual_sample_count": 9, "unique_runtime_sample_count": 8, "duplicate_endpoint": True},
        "events": [], "refusals": [],
    }
    with pytest.raises(PhaseEventEvidenceError, match="stale or incomplete"):
        validate_complete_event_provenance("xl_l", source, derivation, post, 1, 9)
