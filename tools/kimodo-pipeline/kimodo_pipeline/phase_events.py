"""Portable ANIM-002 source, derivation, and post-repair event contracts."""
from __future__ import annotations

import math
from typing import Any, Mapping, Sequence


PHASE_EVENT_EVIDENCE_VERSION = 1
PHASE_EVENT_ALGORITHM = "kimodo_evaluated_release_v1"
SOURCE_DIRECTIONAL_EVENT_CONTRACT = "anim002_source_directional_phase_events_v1"
DERIVATION_EVENT_CONTRACT = "anim002_event_derivation_v1"
POST_REPAIR_EVENT_CONTRACT = "anim002_post_repair_directional_phase_events_v2"
SEED_DISPLACEMENT_CONTRACT = "anim002_post_repair_phase_seed_displacement_v1"
PHASE_EVENT_DETECTOR_STENCIL_SAMPLES = 4
INAPPLICABLE_ROLES = frozenset({"sl", "sr", "pl45", "pr45"})
EVENT_KINDS = frozenset({"Release", "Acquire"})


class PhaseEventEvidenceError(ValueError):
    """Raised when phase-event evidence cannot be published."""


def expected_phase_event_sides(role: str) -> tuple[str, ...] | None:
    """Return required anatomy, or ``None`` for explicitly inapplicable roles."""
    if role in INAPPLICABLE_ROLES:
        return None
    if role in {"wf", "wb"} or role.startswith(("wf_", "wb_")):
        return "Left", "Right"
    if role.startswith(("xl_", "xr_", "al_", "ar_", "i2f_", "f2i_", "i2b_", "b2i_")):
        return ("Left",) if role.endswith("_l") else ("Right",)
    raise PhaseEventEvidenceError(f"unsupported ANIM-002 phase-event role: {role}")


def transition_boundary_context(role: str) -> str | None:
    """Return the locomotion-facing output boundary for a transition role."""
    if role.startswith(("i2f_", "i2b_")):
        return "end"
    if role.startswith(("f2i_", "b2i_")):
        return "start"
    return None


def event_id(side: str, kind: str = "Release") -> str:
    if side not in {"Left", "Right"}:
        raise PhaseEventEvidenceError(f"unknown anatomical side: {side}")
    if kind not in EVENT_KINDS:
        raise PhaseEventEvidenceError(f"unknown physical event kind: {kind}")
    return f"{kind.lower()}_{side[0].lower()}"


def derivation_operations(derivation: str) -> tuple[str, ...]:
    """Decode derivation without depending on action names, roles, or token order."""
    if derivation == "native":
        return ()
    if derivation == "temporal_reversal":
        return ("reverse",)
    tokens = tuple(token for token in derivation.split("_") if token)
    if not tokens or set(tokens) - {"reverse", "mirror"} or len(set(tokens)) != len(tokens):
        raise PhaseEventEvidenceError(f"unsupported event derivation: {derivation}")
    # Spatial anatomy and temporal order commute.  Canonical ordering makes
    # reverse_mirror and mirror_reverse publish byte-identical semantics.
    return tuple(operation for operation in ("reverse", "mirror") if operation in tokens)


def transform_event_kind(kind: str, derivation: str) -> str:
    if kind not in EVENT_KINDS:
        raise PhaseEventEvidenceError(f"unknown physical event kind: {kind}")
    if "reverse" not in derivation_operations(derivation):
        return kind
    return "Acquire" if kind == "Release" else "Release"


def transform_boundary_context(boundary: str | None, derivation: str) -> str | None:
    if boundary not in {None, "start", "end"}:
        raise PhaseEventEvidenceError(f"unknown finite boundary context: {boundary}")
    if boundary is None or "reverse" not in derivation_operations(derivation):
        return boundary
    return "end" if boundary == "start" else "start"


def transform_anatomical_side(side: str, derivation: str) -> str:
    if side not in {"Left", "Right"}:
        raise PhaseEventEvidenceError(f"unknown anatomical side: {side}")
    if "mirror" not in derivation_operations(derivation):
        return side
    return "Right" if side == "Left" else "Left"


def transform_frame(
    source_frame: int, source_start: int, source_end: int,
    output_start: int, derivation: str,
) -> int:
    if any(isinstance(value, bool) or not isinstance(value, int) for value in (
        source_frame, source_start, source_end, output_start,
    )) or not source_start <= source_frame <= source_end:
        raise PhaseEventEvidenceError("event frame is outside its source interval")
    offset = source_end - source_frame if "reverse" in derivation_operations(derivation) else source_frame - source_start
    return output_start + offset


def derive_output_event(
    source_event: Mapping[str, Any], *, derivation: str,
    source_start: int, source_end: int, output_start: int = 1,
) -> dict[str, Any]:
    """Transform immutable source identity into explicit physical output semantics."""
    side = transform_anatomical_side(str(source_event.get("anatomical_side")), derivation)
    source_kind = str(source_event.get("event_kind"))
    kind = transform_event_kind(source_kind, derivation)
    source_frame = source_event.get("source_frame")
    if isinstance(source_frame, bool) or not isinstance(source_frame, int):
        raise PhaseEventEvidenceError("source directional event has no integer source frame")
    output_frame = transform_frame(source_frame, source_start, source_end, output_start, derivation)
    return {
        "source_event_id": source_event.get("id"),
        "id": event_id(side, kind),
        "anatomical_side": side,
        "event_kind": kind,
        "output_frame": output_frame,
        "boundary_context": transform_boundary_context(source_event.get("boundary_context"), derivation),
        "canonicalised_from_duplicate_endpoint": None,
    }


def canonicalise_duplicate_endpoint_events(
    events: Sequence[Mapping[str, Any]], *, frame_start: int, frame_end: int,
    duplicate_endpoint: bool,
) -> list[dict[str, Any]]:
    """Collapse a conceptual endpoint recurrence onto the first runtime sample."""
    rows = [dict(row) for row in events]
    if not duplicate_endpoint:
        return rows
    identities_at_start = {
        (row.get("event_kind"), row.get("anatomical_side"))
        for row in rows if row.get("output_frame") == frame_start
    }
    result = []
    for row in rows:
        identity = row.get("event_kind"), row.get("anatomical_side")
        if row.get("output_frame") == frame_end and identity in identities_at_start:
            continue
        if row.get("output_frame") == frame_end:
            row["output_frame"] = frame_start
            row["canonicalised_from_duplicate_endpoint"] = frame_end
        result.append(row)
    return result


def canonical_frame_delta(
    output_frame: int, derived_frame: int, *, frame_start: int, frame_end: int,
    duplicate_endpoint: bool,
) -> int:
    """Return a linear finite delta or the shortest signed cyclic delta."""
    direct = output_frame - derived_frame
    if not duplicate_endpoint:
        return direct
    period = frame_end - frame_start
    if period <= 0:
        raise PhaseEventEvidenceError("cyclic event interval has no unique runtime samples")
    return min((direct, direct - period, direct + period), key=lambda value: (abs(value), value))


def _finite_number(value: Any, *, minimum: float = -math.inf, maximum: float = math.inf) -> bool:
    return (
        isinstance(value, (int, float)) and not isinstance(value, bool)
        and math.isfinite(value) and minimum <= value <= maximum
    )


def validate_source_directional_event_evidence(evidence: Mapping[str, Any]) -> dict[str, Any]:
    """Validate immutable selector evidence before any output derivation."""
    if not isinstance(evidence, Mapping) or evidence.get("contract") != SOURCE_DIRECTIONAL_EVENT_CONTRACT:
        raise PhaseEventEvidenceError("source directional event evidence is stale or incomplete")
    if set(evidence) != {"contract", "source_interval", "events"}:
        raise PhaseEventEvidenceError("source directional event evidence has invalid fields")
    interval, events = evidence.get("source_interval"), evidence.get("events")
    if (
        not isinstance(interval, list) or len(interval) != 2
        or any(isinstance(value, bool) or not isinstance(value, int) for value in interval)
        or interval[1] < interval[0] or not isinstance(events, list)
    ):
        raise PhaseEventEvidenceError("source directional event evidence has an invalid interval")
    seen: set[str] = set()
    for row in events:
        if not isinstance(row, Mapping) or set(row) != {
            "id", "anatomical_side", "event_kind", "source_frame", "boundary_context",
            "selector_confidence", "selector_evidence",
        }:
            raise PhaseEventEvidenceError("source directional event row is incomplete")
        side, kind, identifier = row.get("anatomical_side"), row.get("event_kind"), row.get("id")
        if kind != "Release" or side not in {"Left", "Right"} or identifier != event_id(str(side), str(kind)):
            raise PhaseEventEvidenceError("source directional identity is invalid")
        if identifier in seen:
            raise PhaseEventEvidenceError(f"duplicate source directional event {identifier}")
        seen.add(str(identifier))
        if row.get("boundary_context") not in {None, "start", "end"}:
            raise PhaseEventEvidenceError("source directional boundary context is invalid")
        if not isinstance(row.get("source_frame"), int) or isinstance(row.get("source_frame"), bool) or not interval[0] <= row["source_frame"] <= interval[1]:
            raise PhaseEventEvidenceError("source directional event is outside its interval")
        if not _finite_number(row.get("selector_confidence"), minimum=0.0, maximum=1.0):
            raise PhaseEventEvidenceError("source directional confidence is invalid")
        if not isinstance(row.get("selector_evidence"), Mapping):
            raise PhaseEventEvidenceError("source directional selector evidence is missing")
    return dict(evidence)


def validate_event_derivation_evidence(evidence: Mapping[str, Any]) -> dict[str, Any]:
    if not isinstance(evidence, Mapping) or evidence.get("contract") != DERIVATION_EVENT_CONTRACT:
        raise PhaseEventEvidenceError("event derivation evidence is stale or incomplete")
    if set(evidence) != {"contract", "derivation", "operations", "frame_mapping", "events"}:
        raise PhaseEventEvidenceError("event derivation evidence has invalid fields")
    operations = derivation_operations(str(evidence.get("derivation")))
    if evidence.get("operations") != list(operations):
        raise PhaseEventEvidenceError("event derivation operations are not canonical")
    mapping, events = evidence.get("frame_mapping"), evidence.get("events")
    if not isinstance(mapping, Mapping) or set(mapping) != {"source_start", "source_end", "output_start", "output_end"} or not isinstance(events, list):
        raise PhaseEventEvidenceError("event derivation frame mapping is incomplete")
    for row in events:
        if not isinstance(row, Mapping) or set(row) != {
            "source_event_id", "id", "anatomical_side", "event_kind", "output_frame", "boundary_context",
            "canonicalised_from_duplicate_endpoint",
        }:
            raise PhaseEventEvidenceError("derived event row is incomplete")
        if row.get("id") != event_id(str(row.get("anatomical_side")), str(row.get("event_kind"))):
            raise PhaseEventEvidenceError("derived event identity is invalid")
    return dict(evidence)


def validate_post_repair_event_evidence(
    role: str, evidence: Mapping[str, Any], frame_start: int, frame_end: int,
) -> dict[str, Any]:
    """Require output measurements made after the final hierarchy repair."""
    if not isinstance(evidence, Mapping) or evidence.get("contract") != POST_REPAIR_EVENT_CONTRACT:
        raise PhaseEventEvidenceError(f"{role} post-repair directional phase event evidence is stale or incomplete")
    expected_fields = {"contract", "evaluation_stage", "timeline", "sample_semantics", "events", "refusals"}
    if set(evidence) != expected_fields or evidence.get("evaluation_stage") != "final_repaired_evaluated_hierarchy":
        raise PhaseEventEvidenceError(f"{role} post-repair directional phase event evidence has invalid fields")
    timeline, semantics = evidence.get("timeline"), evidence.get("sample_semantics")
    if not isinstance(timeline, Mapping) or timeline.get("frame_start") != frame_start or timeline.get("frame_end") != frame_end:
        raise PhaseEventEvidenceError(f"{role} post-repair timeline does not match the Action range")
    if not _finite_number(timeline.get("fps"), minimum=1e-12):
        raise PhaseEventEvidenceError(f"{role} post-repair timeline has invalid fps")
    sample_count = frame_end - frame_start + 1
    if (
        not isinstance(semantics, Mapping)
        or semantics.get("conceptual_sample_count") != sample_count
        or semantics.get("duplicate_endpoint") is not bool(semantics.get("is_loop"))
        or semantics.get("unique_runtime_sample_count") != sample_count - (1 if semantics.get("duplicate_endpoint") else 0)
    ):
        raise PhaseEventEvidenceError(f"{role} has stale duplicate-endpoint sample semantics")
    events, refusals = evidence.get("events"), evidence.get("refusals")
    if not isinstance(events, list) or not isinstance(refusals, list):
        raise PhaseEventEvidenceError(f"{role} post-repair events/refusals must be lists")
    expected_sides = expected_phase_event_sides(role)
    if expected_sides is None:
        if events or refusals:
            raise PhaseEventEvidenceError(f"{role} inapplicable directional phase events must be empty")
        return dict(evidence)
    if refusals:
        raise PhaseEventEvidenceError(f"{role} post-repair directional phase events contain refusals")
    if len(events) != len(expected_sides):
        raise PhaseEventEvidenceError(f"{role} post-repair directional phase event count is incomplete")
    for row in events:
        if not isinstance(row, Mapping) or row.get("accepted") is not True or not isinstance(row.get("measurement"), Mapping):
            raise PhaseEventEvidenceError(f"{role} has unaccepted post-repair directional phase evidence")
        side, kind = row.get("anatomical_side"), row.get("event_kind")
        if side not in expected_sides or kind not in EVENT_KINDS or row.get("id") != event_id(str(side), str(kind)):
            raise PhaseEventEvidenceError(f"{role} post-repair directional phase identity is invalid")
        if not isinstance(row.get("output_frame"), int) or isinstance(row.get("output_frame"), bool) or not frame_start <= row["output_frame"] <= frame_end:
            raise PhaseEventEvidenceError(f"{role} post-repair directional phase event is outside its Action range")
        displacement = row.get("seed_displacement_provenance")
        displacement_fields = {
            "contract", "derived_output_frame", "post_repair_output_frame", "canonical_delta_frames",
            "source_detector_stencil_frames", "post_repair_detector_stencil_frames",
            "repair_support_dilated_frames", "source_repair_intersection_frames",
            "post_repair_repair_intersection_frames", "displacement_authorised",
        }
        if not isinstance(displacement, Mapping) or set(displacement) != displacement_fields:
            raise PhaseEventEvidenceError(f"{role} post-repair phase seed displacement provenance is incomplete")
        derived_output_frame = row.get("derived_output_frame")
        if not isinstance(derived_output_frame, int) or isinstance(derived_output_frame, bool):
            raise PhaseEventEvidenceError(f"{role} post-repair event lacks its derivation frame")
        expected_delta = canonical_frame_delta(
            row["output_frame"], derived_output_frame,
            frame_start=frame_start, frame_end=frame_end,
            duplicate_endpoint=bool(semantics.get("duplicate_endpoint")),
        )
        source_detector_frames = displacement.get("source_detector_stencil_frames")
        post_detector_frames = displacement.get("post_repair_detector_stencil_frames")
        repair_frames = displacement.get("repair_support_dilated_frames")
        source_intersection = displacement.get("source_repair_intersection_frames")
        post_intersection = displacement.get("post_repair_repair_intersection_frames")
        if (
            row.get("derivation_event_id") != row.get("id")
            or row.get("repair_frame_delta") != expected_delta
            or displacement.get("contract") != SEED_DISPLACEMENT_CONTRACT
            or displacement.get("derived_output_frame") != row.get("derived_output_frame")
            or displacement.get("post_repair_output_frame") != row.get("output_frame")
            or displacement.get("canonical_delta_frames") != expected_delta
            or any(not isinstance(value, list) or any(not isinstance(frame, int) for frame in value) for value in (
                source_detector_frames, post_detector_frames, repair_frames, source_intersection, post_intersection,
            ))
            or not isinstance(displacement.get("displacement_authorised"), bool)
            or set(source_intersection or []) != set(source_detector_frames or []) & set(repair_frames or [])
            or set(post_intersection or []) != set(post_detector_frames or []) & set(repair_frames or [])
            or displacement.get("displacement_authorised") is not bool(source_intersection and post_intersection)
            or (expected_delta != 0 and (
                displacement.get("displacement_authorised") is not True
                or not source_intersection or not post_intersection
            ))
        ):
            raise PhaseEventEvidenceError(f"{role} post-repair event lacks its derivation/frame-shift link")
        if not _finite_number(row.get("confidence"), minimum=0.0, maximum=1.0):
            raise PhaseEventEvidenceError(f"{role} post-repair directional phase confidence is invalid")
    return dict(evidence)


def validate_complete_event_provenance(
    role: str, source: Mapping[str, Any], derivation: Mapping[str, Any],
    post_repair: Mapping[str, Any], frame_start: int, frame_end: int,
) -> dict[str, Any]:
    """Cross-check all three carriers so internally valid stale rows cannot mix."""
    source = validate_source_directional_event_evidence(source)
    derivation = validate_event_derivation_evidence(derivation)
    post_repair = validate_post_repair_event_evidence(role, post_repair, frame_start, frame_end)
    mapping = derivation["frame_mapping"]
    rebuilt = [
        derive_output_event(
            row, derivation=derivation["derivation"],
            source_start=mapping["source_start"], source_end=mapping["source_end"],
            output_start=mapping["output_start"],
        )
        for row in source["events"]
    ]
    rebuilt = canonicalise_duplicate_endpoint_events(
        rebuilt, frame_start=frame_start, frame_end=frame_end,
        duplicate_endpoint=post_repair["sample_semantics"]["duplicate_endpoint"],
    )
    if sorted(rebuilt, key=lambda row: row["id"]) != derivation["events"]:
        raise PhaseEventEvidenceError(f"{role} event derivation does not reproduce immutable source events")
    derived_physical = {
        (row["id"], row["anatomical_side"], row["event_kind"], row["boundary_context"])
        for row in derivation["events"]
    }
    measured_physical = {
        (row["id"], row["anatomical_side"], row["event_kind"], row["boundary_context"])
        for row in post_repair["events"]
    }
    if derived_physical != measured_physical:
        raise PhaseEventEvidenceError(f"{role} post-repair events do not corroborate the derived directional phase identities")
    expected_frames = {row["id"]: row["output_frame"] for row in derivation["events"]}
    if any(row.get("derived_output_frame") != expected_frames.get(row["id"]) for row in post_repair["events"]):
        raise PhaseEventEvidenceError(f"{role} post-repair events have stale derivation frame links")
    return {
        "source_directional_event_evidence": source,
        "event_derivation_evidence": derivation,
        "post_repair_event_evidence": post_repair,
    }


def validate_phase_event_evidence(
    role: str, evidence: Mapping[str, Any], frame_start: int, frame_end: int,
) -> dict[str, Any]:
    """Validate the legacy compact directional selector structure.

    New publication additionally requires the three explicit contracts above;
    this validator remains for disposable diagnostics and migration errors.
    """
    if not isinstance(evidence, Mapping):
        raise PhaseEventEvidenceError(f"{role} phase_event_evidence must be an object")
    if evidence.get("version") != PHASE_EVENT_EVIDENCE_VERSION:
        raise PhaseEventEvidenceError(f"{role} has an unsupported phase-event evidence version")
    applicability, events = evidence.get("applicability"), evidence.get("events")
    if not isinstance(events, list):
        raise PhaseEventEvidenceError(f"{role} phase-event events must be a list")
    expected_sides = expected_phase_event_sides(role)
    if expected_sides is None:
        if applicability != "inapplicable" or events or set(evidence) != {"version", "applicability", "events"}:
            raise PhaseEventEvidenceError(f"{role} phase events must be explicitly inapplicable and empty")
        return dict(evidence)
    if applicability != "applicable" or evidence.get("algorithm") != PHASE_EVENT_ALGORITHM:
        raise PhaseEventEvidenceError(f"{role} phase events are not applicable current selector evidence")
    timeline = evidence.get("timeline")
    if not isinstance(timeline, Mapping) or timeline.get("frame_start") != frame_start or timeline.get("frame_end") != frame_end:
        raise PhaseEventEvidenceError(f"{role} phase-event timeline does not match the Action range")
    if not _finite_number(timeline.get("fps"), minimum=1e-12) or len(events) != len(expected_sides):
        raise PhaseEventEvidenceError(f"{role} phase-event timeline or count is invalid")
    expected_boundary, seen, actual_sides = transition_boundary_context(role), set(), []
    for row in events:
        if not isinstance(row, Mapping):
            raise PhaseEventEvidenceError(f"{role} phase event must be an object")
        side, identifier = row.get("anatomical_side"), row.get("id")
        if side not in {"Left", "Right"} or identifier != event_id(str(side)) or identifier in seen:
            raise PhaseEventEvidenceError(f"{role} phase event ID and anatomy do not match")
        seen.add(identifier); actual_sides.append(side)
        output_frame = row.get("output_frame")
        if not isinstance(output_frame, int) or isinstance(output_frame, bool) or not frame_start <= output_frame <= frame_end:
            raise PhaseEventEvidenceError(f"{role} phase event is outside its Action range")
        if not isinstance(row.get("source_frame"), int) or isinstance(row.get("source_frame"), bool):
            raise PhaseEventEvidenceError(f"{role} phase event lacks its authoritative source frame")
        if row.get("boundary_context") != expected_boundary or (expected_boundary == "start" and output_frame != frame_start) or (expected_boundary == "end" and output_frame != frame_end):
            raise PhaseEventEvidenceError(f"{role} phase event has invalid transition boundary context")
        if not _finite_number(row.get("selector_confidence"), minimum=0.0, maximum=1.0):
            raise PhaseEventEvidenceError(f"{role} phase event has invalid selector confidence")
    if set(actual_sides) != set(expected_sides):
        raise PhaseEventEvidenceError(f"{role} phase-event anatomy does not match its declared role")
    return dict(evidence)
