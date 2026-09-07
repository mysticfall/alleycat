#!/usr/bin/env python3
"""Generate phase-guided, relative-motion ANIM-002 contact evidence.

The processed index supplies semantic role identity and authoritative phase
events. Evaluated armature bones only refine boundaries inside those declared
phase windows. No mesh, skin, bone tail, or absolute ground plane is consulted.
Outputs are confined to a caller-supplied disposable processed package.
"""
from __future__ import annotations

import argparse
from dataclasses import asdict, dataclass
import hashlib
import json
import math
from pathlib import Path
import statistics
import sys
from typing import Any, Iterable, Mapping, Sequence, cast


from .bootstrap_paths import repository_root
from .loop_seam import validate_loop_repair_provenance
from .phase_events import validate_complete_event_provenance


ROOT = repository_root()
CONTACT_SCHEMA_VERSION = 1
SOURCE_SCHEMA_VERSION = 5
METHOD = "phase_seeded_relative_motion_segmentation_v2"
DETECTOR_INFLUENCE_SAMPLES = 4
DEFAULT_OUTPUT = Path("provenance/authored_per_foot_contacts_v1.json")
DEFAULT_DESCRIPTOR = Path("provenance/authored_per_foot_contacts_descriptor_v1.json")
DEFAULT_POLICY = ROOT / "tools/kimodo-pipeline/config/phase_contact_policy_v3.json"


class ContactEvidenceError(RuntimeError):
    """A declared source package cannot prove authoritative contact evidence."""


@dataclass(frozen=True)
class ContactConfiguration:
    minimum_support_samples: int = 3
    minimum_transition_samples: int = 2
    minimum_confidence: float = 0.8
    minimum_state_separation: float = 0.18
    minimum_ambiguity_margin: float = 0.015
    semantic_weight: float = 0.22
    cohesion_weight: float = 0.18
    separation_weight: float = 0.22
    transition_weight: float = 0.16
    context_weight: float = 0.12
    ambiguity_weight: float = 0.10


@dataclass(frozen=True)
class BoneSample:
    frame: int
    time_seconds: float
    foot_position: tuple[float, float, float]
    foot_rotation: tuple[float, float, float, float]
    hips_position: tuple[float, float, float]
    shin_position: tuple[float, float, float]
    opposite_foot_position: tuple[float, float, float]
    root_position: tuple[float, float, float]


@dataclass(frozen=True)
class PhasePath:
    role: str
    family: str
    topology: str
    foot: str
    classification: str
    anchor_frame: int | None
    boundary_context: str | None
    selector_confidence: float | None
    frame_start: int
    frame_end: int
    other_anchor_frame: int | None = None
    double_support: str = "undeclared"
    event_kind: str | None = None
    event_id: str | None = None
    duplicate_endpoint: bool = False
    conceptual_sample_count: int | None = None
    unique_runtime_sample_count: int | None = None
    effective_phase_seed_frames: tuple[int, ...] = ()
    phase_seed_provenance: Mapping[str, Any] | None = None
    event_anchor: Mapping[str, Any] | None = None
    event_provenance: Mapping[str, Any] | None = None
    contact_polarity: str | None = None


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _canonical_digest(value: Mapping[str, Any]) -> str:
    encoded = json.dumps(value, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def _read_json(path: Path, label: str) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise ContactEvidenceError(f"{label} is not readable JSON: {path}") from error
    if not isinstance(value, dict):
        raise ContactEvidenceError(f"{label} must be a JSON object: {path}")
    return value


def _portable_relative(path: Path, label: str) -> Path:
    if path.is_absolute() or ".." in path.parts or not path.parts:
        raise ContactEvidenceError(f"{label} must be a portable processed-package-relative path.")
    return path


def _durable_resource_path(path: str, label: str) -> str:
    if not path.startswith("res://") or "/../" in path or path.endswith("/.."):
        raise ContactEvidenceError(f"{label} must be a durable res:// path.")
    if path.startswith(("res://tests/", "res://temp/")):
        raise ContactEvidenceError(f"{label} must not name a disposable test or temp path.")
    return path


def _finite(value: object) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(float(value))


def load_mapping(path: Path) -> dict[str, Any]:
    mapping = _read_json(path, "Bone mapping")
    feet = mapping.get("feet")
    if (
        mapping.get("schema_version") != 2
        or not isinstance(mapping.get("mapping_identity"), str)
        or not isinstance(mapping.get("skeleton"), str)
        or not isinstance(mapping.get("root"), str)
        or not isinstance(mapping.get("hips"), str)
        or not isinstance(feet, dict)
        or set(feet) != {"left", "right"}
    ):
        raise ContactEvidenceError("Bone mapping must declare schema v2, identity, skeleton, Root, Hips, and both feet.")
    for foot, declaration in feet.items():
        if not isinstance(declaration, dict) or set(declaration) != {"foot_bone", "shin_bone"}:
            raise ContactEvidenceError(f"Bone mapping '{foot}' must declare only foot_bone and shin_bone.")
        if not all(isinstance(declaration[field], str) and declaration[field] for field in declaration):
            raise ContactEvidenceError(f"Bone mapping '{foot}' contains an empty bone name.")
    return mapping


def required_bones(mapping: Mapping[str, Any]) -> set[str]:
    names = {str(mapping["root"]), str(mapping["hips"])}
    for declaration in mapping["feet"].values():
        names.update((str(declaration["foot_bone"]), str(declaration["shin_bone"])))
    return names


def validate_available_bones(mapping: Mapping[str, Any], available: Iterable[str]) -> None:
    missing = sorted(required_bones(mapping) - set(available))
    if missing:
        raise ContactEvidenceError(f"Retargeted skeleton lacks declared contact bones: {missing}")


def load_policy(path: Path) -> dict[str, Any]:
    policy = _read_json(path, "Phase-contact policy")
    families = policy.get("families")
    if policy.get("schema_version") != 3 or policy.get("method") != METHOD or not isinstance(families, list):
        raise ContactEvidenceError("Phase-contact policy is stale or malformed.")
    allowed = {"required", "optional", "fallback_only"}
    for family in families:
        if not isinstance(family, dict) or set(family) != {
            "family", "match", "topology", "contact_polarity", "double_support", "event_path", "opposite_path"
        }:
            raise ContactEvidenceError("Phase-contact policy family declaration is malformed.")
        match = family["match"]
        if (
            not isinstance(match, dict)
            or set(match) != {"kind", "values"}
            or match["kind"] != "exact"
            or not isinstance(match["values"], list)
            or not match["values"]
            or family["event_path"] not in allowed
            or family["opposite_path"] not in allowed
            or family["double_support"] not in {"allowed", "boundary_partial", "undeclared", "inapplicable"}
            or family["contact_polarity"] not in {"Acquire", "Release", "directional_seed", "inapplicable"}
        ):
            raise ContactEvidenceError("Phase-contact policy matcher or coverage class is invalid.")
    return policy


def _policy_family(role: str, policy: Mapping[str, Any]) -> Mapping[str, Any]:
    matches = []
    for family in policy["families"]:
        matcher = family["match"]
        values = matcher["values"]
        matched = role in values
        if matched:
            matches.append(family)
    if len(matches) != 1:
        raise ContactEvidenceError(f"Role '{role}' has {len(matches)} phase-contact policy matches.")
    return matches[0]


def load_processed_package(processed_package: Path, mapping: dict[str, Any]) -> tuple[dict[str, Any], Path, Path]:
    package = processed_package.resolve()
    if not package.is_dir():
        raise ContactEvidenceError(f"Processed package is missing: {package}")
    protected_roots = (
        ROOT / "game/assets/characters/reference/female/animations/processed/locomotion",
    )
    if any(package == item.resolve() or item.resolve() in package.parents for item in protected_roots):
        raise ContactEvidenceError("Refusing to generate evidence in a production processed locomotion root.")
    index_path = package / "processed-index.json"
    index = _read_json(index_path, "Processed index")
    if index.get("schema_version") != 2 or index.get("metrics_schema_version") != 2:
        raise ContactEvidenceError("Processed index schema is stale.")
    if index.get("target_skeleton") != mapping["skeleton"]:
        raise ContactEvidenceError("Declared bone mapping skeleton does not match the processed index.")
    actions = index.get("actions")
    if not isinstance(actions, list) or not actions or index.get("action_count") != len(actions):
        raise ContactEvidenceError("Processed index actions are missing or inconsistent.")
    seen_actions: set[str] = set()
    seen_identities: set[str] = set()
    for entry in actions:
        if not isinstance(entry, dict):
            raise ContactEvidenceError("Processed index contains a malformed action entry.")
        values = (entry.get("action"), entry.get("external_identity"), entry.get("role"), entry.get("metrics"))
        if not all(isinstance(value, str) and value for value in values):
            raise ContactEvidenceError("Processed index action correspondence is incomplete.")
        action, identity, metrics = str(values[0]), str(values[1]), str(values[3])
        if action in seen_actions or identity in seen_identities:
            raise ContactEvidenceError("Processed index action correspondence is not bijective.")
        seen_actions.add(action)
        seen_identities.add(identity)
        metrics_document = _read_json(package / _portable_relative(Path(metrics), "Metrics path"), "Action metrics")
        if metrics_document.get("action") != action:
            raise ContactEvidenceError(f"Metrics action mismatch for '{identity}'.")
    # The processed package is variant-scoped: the index sits in the variant
    # metadata directory while the retargeted authoring blend stays at the set
    # level as <set>_<variant>.blend.
    blends = sorted(package.glob("*.blend")) or sorted(
        package.parent.glob(f"{index.get('package')}_{index.get('variant')}.blend")
    )
    if len(blends) != 1:
        raise ContactEvidenceError("Processed package must contain exactly one retargeted blend.")
    return index, index_path, blends[0]


def validate_evidence_matches_package(
    evidence: dict[str, Any], index_path: Path, blend_path: Path, mapping_path: Path, policy_path: Path,
) -> None:
    source = evidence.get("source_derived")
    if not isinstance(source, dict) or not isinstance(source.get("input_digests"), dict):
        raise ContactEvidenceError("Generated evidence lacks source-derived input digests.")
    configuration = source.get("configuration")
    weights = source.get("confidence_weights")
    input_contracts = source.get("input_contracts")
    phase_seed_policy = source.get("phase_seed_policy")
    if (
        source.get("schema_version") != SOURCE_SCHEMA_VERSION
        or source.get("method") != METHOD
        or source.get("sampling") != "native_fps"
        or not isinstance(configuration, dict)
        or configuration.get("minimum_confidence") != 0.8
        or "phase_window_fraction" in configuration
        or source.get("configuration_digest") != _canonical_digest(configuration)
        or not isinstance(weights, dict)
        or not math.isclose(sum(float(value) for value in weights.values()), 1.0, abs_tol=1e-9)
        or input_contracts != [
            "anim002_source_directional_phase_events_v1",
            "anim002_event_derivation_v1",
            "anim002_post_repair_directional_phase_events_v2",
            "anim002_selected_loop_repair_provenance_v2",
        ]
        or not isinstance(phase_seed_policy, dict)
        or phase_seed_policy.get("detector_influence_samples") != DETECTOR_INFLUENCE_SAMPLES
        or phase_seed_policy.get("two_release_sector") != "cyclic_voronoi_midpoints"
        or phase_seed_policy.get("one_release_sector") != "half_cycle_around_repeated_seed"
        or phase_seed_policy.get("absolute_world_slide_affects_eligibility") is not False
    ):
        raise ContactEvidenceError("Generated relative-motion schema or configuration provenance is stale.")
    expected = {
        "processed_index_sha256": _sha256(index_path),
        "retargeted_blend_sha256": _sha256(blend_path),
        "bone_mapping_sha256": _sha256(mapping_path),
        "phase_contact_policy_sha256": _sha256(policy_path),
    }
    if {key: source["input_digests"].get(key) for key in expected} != expected:
        raise ContactEvidenceError("Generated evidence provenance is stale or does not match the declared processed package.")


def build_descriptor(descriptor_input: str, package_identity: str, generation: str) -> dict[str, Any]:
    return {
        "schema_version": 1,
        "contact_evidence": {
            "input": _durable_resource_path(descriptor_input, "Descriptor input"),
            "retained_input": _durable_resource_path(descriptor_input, "Descriptor input"),
            "runtime_path": "authored_per_foot_contacts_v1.json",
            "package_identity": package_identity,
            "generation": generation,
        },
    }


def resolve_phase_paths(entry: Mapping[str, Any], policy: Mapping[str, Any]) -> list[PhasePath]:
    role = str(entry["role"])
    family = _policy_family(role, policy)
    transport = entry.get("transport")
    if not isinstance(transport, dict):
        raise ContactEvidenceError(f"Role '{role}' lacks transport evidence.")
    start, end = transport.get("frame_start"), transport.get("frame_end")
    if not isinstance(start, int) or not isinstance(end, int) or end <= start:
        raise ContactEvidenceError(f"Role '{role}' has invalid phase transport.")

    # These validators are owned by the producer. Reusing them here prevents a
    # locally well-formed but stale selector row from becoming a physical
    # contact boundary.

    source = entry.get("source_directional_event_evidence")
    derivation = entry.get("event_derivation_evidence")
    post_repair = entry.get("post_repair_event_evidence")
    try:
        provenance = validate_complete_event_provenance(
            role,
            cast(Mapping[str, Any], source),
            cast(Mapping[str, Any], derivation),
            cast(Mapping[str, Any], post_repair),
            start,
            end,
        )
    except (TypeError, ValueError) as error:
        raise ContactEvidenceError(
            f"Role '{role}' lacks complete post-repair event provenance: {error}"
        ) from error
    phase = provenance["post_repair_event_evidence"]
    events = phase["events"]
    semantics = phase["sample_semantics"]
    duplicate_endpoint = bool(semantics["duplicate_endpoint"])
    conceptual_count = int(semantics["conceptual_sample_count"])
    unique_count = int(semantics["unique_runtime_sample_count"])

    topology = str(family["topology"])
    repair_provenance: dict[str, Any] | None = None
    if topology.startswith("cyclic_") or topology == "one_sided_release":
        repair = entry.get("loop_repair_evidence")
        try:
            repair = validate_loop_repair_provenance(
                cast(Mapping[str, Any], repair), conceptual_sample_count=conceptual_count,
            )
        except (TypeError, ValueError) as error:
            raise ContactEvidenceError(
                f"Role '{role}' lacks selected post-repair loop provenance: {error}"
            ) from error
        repair_frames: set[int] = set()
        for field in ("opening_support", "closing_support"):
            first, last = repair[field]["window"]
            for offset in range(int(first), int(last) + 1):
                repair_frames.add(start + (offset % unique_count))
        dilated = {
            start + ((frame - start + delta) % unique_count)
            for frame in repair_frames
            for delta in range(-DETECTOR_INFLUENCE_SAMPLES, DETECTOR_INFLUENCE_SAMPLES + 1)
        }
        repair_provenance = {
            "contract": repair["contract"],
            "blend_profile": repair["blend_profile"],
            "opening_support": repair["opening_support"],
            "closing_support": repair["closing_support"],
            "immutable_interior": repair["immutable_interior"],
            "detector_influence_samples": DETECTOR_INFLUENCE_SAMPLES,
            "canonical_support_frames": sorted(repair_frames),
            "repair_support_dilated_frames": sorted(dilated),
        }
        for event in events:
            displacement = event.get("seed_displacement_provenance")
            if (
                not isinstance(displacement, Mapping)
                or displacement.get("repair_support_dilated_frames") != sorted(dilated)
            ):
                raise ContactEvidenceError(
                    f"Role '{role}' phase-seed displacement provenance does not match selected loop repair support."
                )
    by_foot: dict[str, Mapping[str, Any]] = {}
    for event in events:
        if not isinstance(event, dict) or event.get("anatomical_side") not in {"Left", "Right"}:
            raise ContactEvidenceError(f"Role '{role}' has malformed anatomical phase authority.")
        foot = str(event["anatomical_side"]).lower()
        if foot in by_foot:
            raise ContactEvidenceError(f"Role '{role}' has duplicate phase authority for {foot}.")
        by_foot[foot] = event
    if topology == "fallback" and events:
        raise ContactEvidenceError(f"Role '{role}' must have empty post-repair directional phase events.")
    if topology != "fallback" and not events:
        raise ContactEvidenceError(f"Role '{role}' lacks post-repair directional phase seed authority.")
    paths = []
    for foot in ("left", "right"):
        event = by_foot.get(foot)
        classification = family["event_path"] if event is not None else family["opposite_path"]
        other = by_foot.get("right" if foot == "left" else "left")
        anchor = int(event["output_frame"]) if event is not None else None
        if anchor is not None and duplicate_endpoint and anchor == end:
            anchor = start
        effective: set[int] = set()
        if anchor is not None and (topology.startswith("cyclic") or topology == "one_sided_release"):
            unique_frames = range(start, start + unique_count)
            if topology == "cyclic_two_release" and other is not None:
                other_anchor = int(other["output_frame"])
                if duplicate_endpoint and other_anchor == end:
                    other_anchor = start
                def distance(frame: int, seed: int) -> int:
                    direct = abs(frame - seed)
                    return min(direct, unique_count - direct)
                effective = {
                    frame for frame in unique_frames
                    if distance(frame, anchor) <= distance(frame, other_anchor)
                }
            else:
                half_sector_radius = unique_count // 4
                effective = {
                    frame for frame in unique_frames
                    if min(abs(frame - anchor), unique_count - abs(frame - anchor)) <= half_sector_radius
                }
        elif anchor is not None:
            effective = set(range(
                max(start, anchor - DETECTOR_INFLUENCE_SAMPLES),
                min(end, anchor + DETECTOR_INFLUENCE_SAMPLES) + 1,
            ))
        seed_provenance = None if event is None else {
            "contract": "anim002_phase_seed_neighbourhood_v1",
            "topology": topology,
            "seed_frame": anchor,
            "adjacent_seed_frame": int(other["output_frame"]) if other is not None else None,
            "effective_phase_seed_frames": sorted(effective),
            "source_to_post_repair_seed": event.get("seed_displacement_provenance"),
            "repair_support": repair_provenance,
        }
        paths.append(PhasePath(
            role, str(family["family"]), topology, foot, classification,
            anchor,
            event.get("boundary_context") if event is not None else None,
            float(event["confidence"]) if event is not None else None,
            start, end, int(other["output_frame"]) if other is not None else None, str(family["double_support"]),
            event_kind=str(event["event_kind"]) if event is not None else None,
            event_id=str(event["id"]) if event is not None else None,
            duplicate_endpoint=duplicate_endpoint,
            conceptual_sample_count=conceptual_count,
            unique_runtime_sample_count=unique_count,
            effective_phase_seed_frames=tuple(sorted(effective)),
            phase_seed_provenance=seed_provenance,
            event_anchor={
                "contract": phase["contract"], "evaluation_stage": phase["evaluation_stage"],
                "event": dict(event),
            } if event is not None else None,
            event_provenance=provenance,
            contact_polarity=str(family["contact_polarity"]),
        ))
    return paths


def _distance(left: Sequence[float], right: Sequence[float]) -> float:
    return math.sqrt(sum((float(a) - float(b)) ** 2 for a, b in zip(left, right)))


def _rates(values: Sequence[Sequence[float]], times: Sequence[float]) -> list[float]:
    if len(values) < 2:
        return [math.inf] * len(values)
    rates = []
    for index in range(len(values)):
        left, right = max(0, index - 1), min(len(values) - 1, index + 1)
        duration = times[right] - times[left]
        rates.append(_distance(values[left], values[right]) / duration if duration > 0.0 else math.inf)
    return rates


def _scalar_rates(values: Sequence[float], times: Sequence[float]) -> list[float]:
    return _rates([(value,) for value in values], times)


def _angular_rates(rotations: Sequence[Sequence[float]], times: Sequence[float]) -> list[float]:
    normalised = []
    for rotation in rotations:
        length = math.sqrt(sum(value * value for value in rotation))
        normalised.append(tuple(value / length for value in rotation) if length > 1e-12 else (1.0, 0.0, 0.0, 0.0))
    angles = []
    for index in range(len(normalised)):
        left, right = max(0, index - 1), min(len(normalised) - 1, index + 1)
        dot = min(1.0, abs(sum(a * b for a, b in zip(normalised[left], normalised[right]))))
        duration = times[right] - times[left]
        angles.append(2.0 * math.acos(dot) / duration if duration > 0.0 else math.inf)
    return angles


def _cyclic_rates(values: Sequence[Sequence[float]], times: Sequence[float]) -> list[float]:
    """Central finite differences on the unique periodic sample ring."""
    if len(values) < 3 or len(times) != len(values):
        return [math.inf] * len(values)
    step = statistics.median(right - left for left, right in zip(times, times[1:]))
    if step <= 0.0:
        return [math.inf] * len(values)
    return [
        _distance(values[(index - 1) % len(values)], values[(index + 1) % len(values)]) / (2.0 * step)
        for index in range(len(values))
    ]


def _cyclic_scalar_rates(values: Sequence[float], times: Sequence[float]) -> list[float]:
    return _cyclic_rates([(value,) for value in values], times)


def _cyclic_angular_rates(rotations: Sequence[Sequence[float]], times: Sequence[float]) -> list[float]:
    normalised = []
    for rotation in rotations:
        length = math.sqrt(sum(value * value for value in rotation))
        normalised.append(tuple(value / length for value in rotation) if length > 1e-12 else (1.0, 0.0, 0.0, 0.0))
    if len(normalised) < 3:
        return [math.inf] * len(normalised)
    step = statistics.median(right - left for left, right in zip(times, times[1:]))
    result = []
    for index in range(len(normalised)):
        dot = min(1.0, abs(sum(
            a * b for a, b in zip(normalised[(index - 1) % len(normalised)], normalised[(index + 1) % len(normalised)])
        )))
        result.append(2.0 * math.acos(dot) / (2.0 * step) if step > 0.0 else math.inf)
    return result


def _runs(indices: Iterable[int]) -> list[list[int]]:
    result: list[list[int]] = []
    for index in indices:
        if not result or index != result[-1][-1] + 1:
            result.append([index])
        else:
            result[-1].append(index)
    return result


def _candidate_indices(samples: Sequence[BoneSample], path: PhasePath, configuration: ContactConfiguration) -> list[int]:
    if path.anchor_frame is None:
        return []
    anchor = path.anchor_frame
    if path.topology == "cyclic_two_release":
        lower = path.other_anchor_frame if path.other_anchor_frame is not None else path.frame_start
        if lower > anchor:
            after_boundary = [index for index, sample in enumerate(samples) if sample.frame >= lower]
            before_boundary = [index for index, sample in enumerate(samples) if sample.frame <= anchor + DETECTOR_INFLUENCE_SAMPLES]
            return after_boundary + before_boundary
        return [index for index, sample in enumerate(samples) if lower <= sample.frame <= min(path.frame_end, anchor + DETECTOR_INFLUENCE_SAMPLES)]
    return list(range(len(samples)))


def _cluster_baseline(samples: Sequence[BoneSample], run: Sequence[int]) -> tuple[float, float, float]:
    heights = [samples[index].foot_position[2] for index in run]
    baseline = statistics.median(heights)
    residuals = [abs(value - baseline) for value in heights]
    spread = statistics.median(residuals)
    half = max(1, len(heights) // 2)
    drift = abs(statistics.median(heights[-half:]) - statistics.median(heights[:half]))
    return baseline, spread, drift


def _sustained(indices: Sequence[int], predicate: Any, minimum: int) -> int | None:
    for offset in range(len(indices) - minimum + 1):
        window = indices[offset:offset + minimum]
        if all(predicate(index) for index in window):
            return window[0]
    return None


def _refused(
    path: PhasePath,
    reason: str,
    baseline: float | None = None,
    spread: float | None = None,
    drift: float | None = None,
    uncertainty: int | None = None,
) -> dict[str, Any]:
    return {
        "role": path.role, "family": path.family, "foot": path.foot,
        "policy_classification": path.classification, "topology": path.topology,
        "event_side": path.foot if path.anchor_frame is not None else None,
        "event_boundary": path.boundary_context, "outcome": "fallback" if path.classification == "fallback_only" else "refused",
        "acquire_sample": None, "support_samples": None, "release_sample": None,
        "baseline_offset_metres": baseline, "baseline_spread_metres": spread,
        "baseline_drift_metres": drift, "confidence": 0.0,
        "uncertainty_samples": uncertainty, "refusal_reason": reason,
        "refined_boundary_delta_frames": None, "candidate_alternatives": [],
        "rejected_alternatives": [], "objective_terms": None,
        "confidence_components": None, "absolute_slide_diagnostics": None,
        "event_anchor": path.event_anchor,
        "event_kind": path.event_kind,
        "event_id": path.event_id,
        "sample_topology": {
            "conceptual_sample_count": path.conceptual_sample_count,
            "unique_runtime_sample_count": path.unique_runtime_sample_count,
            "duplicate_endpoint_canonicalised": path.duplicate_endpoint,
        },
        "cyclic_search_order": None,
        "effective_phase_seed_frames": list(path.effective_phase_seed_frames),
        "phase_seed_provenance": path.phase_seed_provenance,
        "lifecycle_intervals": [],
    }


def _ranks(values: Sequence[float]) -> list[float]:
    """Return offset- and scale-free mid-ranks in [0, 1]."""
    if len(values) < 2:
        return [0.5] * len(values)
    ordered = sorted((value, index) for index, value in enumerate(values))
    result = [0.0] * len(values)
    position = 0
    while position < len(ordered):
        end = position
        while end + 1 < len(ordered) and math.isclose(ordered[end + 1][0], ordered[position][0], abs_tol=1e-12):
            end += 1
        rank = ((position + end) * 0.5) / (len(values) - 1)
        for item in range(position, end + 1):
            result[ordered[item][1]] = rank
        position = end + 1
    return result


def _mean(values: Sequence[float]) -> float:
    return statistics.mean(values) if values else 0.0


def _transition_persistence_quality(
    values: Sequence[float], separation: float, polarity: str,
) -> float:
    """Score an observed, oriented state change without rewarding unseparated states."""
    if len(values) < 2 or polarity not in {"Acquire", "Release"}:
        return 0.0
    oriented = list(values) if polarity == "Release" else [-value for value in values]
    deltas = [right - left for left, right in zip(oriented, oriented[1:])]
    positive = sum(max(0.0, value) for value in deltas)
    variation = sum(abs(value) for value in deltas)
    monotonicity = positive / max(1e-12, variation)
    sustained = max(0.0, oriented[-1] - oriented[0]) * monotonicity
    sharp = max((max(0.0, value) for value in deltas), default=0.0)
    observed_change = max(sharp, sustained)
    change_quality = min(1.0, observed_change / 0.15)
    separation_quality = min(1.0, max(0.0, separation) / 0.35)
    return min(change_quality, separation_quality)


def _cyclic_distance(left: int, right: int, count: int) -> int:
    direct = abs(left - right)
    return min(direct, count - direct)


def _tied_transition_basin(
    candidates: Sequence[Mapping[str, Any]], best: Mapping[str, Any],
    polarity: str, count: int, margin: float, *, objective: str = "objective",
) -> set[int]:
    """Return the detector-connected basin of tied polarity-owned transitions."""
    transition = "release" if polarity == "Release" else "acquire"
    tied = {
        int(item[transition]) for item in candidates
        if float(item[objective]) - float(best[objective]) < margin
    }
    basin = {int(best[transition])}
    while True:
        neighbours = {
            boundary for boundary in tied
            if any(_cyclic_distance(boundary, member, count) <= DETECTOR_INFLUENCE_SAMPLES for member in basin)
        }
        expanded = basin | neighbours
        if expanded == basin:
            return basin
        basin = expanded


def _cyclic_segments(indices: Sequence[int], count: int) -> list[list[int]]:
    """Return natural-order inclusive runs while retaining a wrap split."""
    selected = set(indices)
    runs = _runs(index for index in range(count) if index in selected)
    if len(runs) > 1 and runs[0][0] == 0 and runs[-1][-1] == count - 1:
        return [runs[-1], runs[0], *runs[1:-1]]
    return runs


def _refine_finite(
    samples: Sequence[BoneSample], path: PhasePath, configuration: ContactConfiguration,
    ordered: Sequence[int], swing_scores: Sequence[float], context_scores: Sequence[float],
    speeds: Sequence[float], accelerations: Sequence[float], jerks: Sequence[float],
    angular: Sequence[float], hips_speeds: Sequence[float], shin_speeds: Sequence[float],
    opposite_speeds: Sequence[float], relative_rates: Sequence[Sequence[float]],
) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    """Optimise the one observed edge of a boundary-censored finite lifecycle."""
    assert path.anchor_frame is not None
    anchor_frame = path.anchor_frame
    polarity = path.contact_polarity
    if polarity not in {"Acquire", "Release"}:
        return _refused(path, "finite transition lacks declared authoritative contact polarity"), []
    authorised = set(path.effective_phase_seed_frames) or set(range(
        max(path.frame_start, anchor_frame - DETECTOR_INFLUENCE_SAMPLES),
        min(path.frame_end, anchor_frame + DETECTOR_INFLUENCE_SAMPLES) + 1,
    ))
    candidates = []
    if polarity == "Release":
        boundaries = range(
            configuration.minimum_support_samples,
            len(ordered) - configuration.minimum_transition_samples + 1,
        )
    else:
        boundaries = range(
            configuration.minimum_transition_samples,
            len(ordered) - configuration.minimum_support_samples + 1,
        )
    for boundary in boundaries:
        boundary_index = ordered[boundary]
        if samples[boundary_index].frame not in authorised:
            continue
        if polarity == "Release":
            support_positions, swing_positions = range(0, boundary), range(boundary, len(ordered))
        else:
            swing_positions, support_positions = range(0, boundary), range(boundary, len(ordered))
        support = [swing_scores[position] for position in support_positions]
        swing = [swing_scores[position] for position in swing_positions]
        if len(support) < configuration.minimum_support_samples or len(swing) < configuration.minimum_transition_samples:
            continue
        separation = _mean(swing) - _mean(support)
        cohesion = 1.0 - min(1.0, 0.5 * (statistics.pstdev(support) + statistics.pstdev(swing)))
        local_positions = [
            position for position, index in enumerate(ordered)
            if samples[index].frame in authorised
        ]
        local_values = [swing_scores[position] for position in local_positions]
        transition_quality = _transition_persistence_quality(local_values, separation, polarity)
        anchor_delta = samples[boundary_index].frame - anchor_frame
        anchor_cost = abs(anchor_delta) / max(1, DETECTOR_INFLUENCE_SAMPLES)
        state_cost = _mean([value * value for value in support]) + _mean([(1.0 - value) ** 2 for value in swing])
        candidates.append({
            "objective": state_cost + 0.20 * anchor_cost - 0.03 * transition_quality,
            "state_cost": state_cost, "anchor_cost": anchor_cost,
            "transition_reward": transition_quality, "boundary": boundary,
            "boundary_delta": anchor_delta, "separation": separation, "cohesion": cohesion,
            "support_positions": list(support_positions), "swing_positions": list(swing_positions),
        })
    candidates.sort(key=lambda item: (item["objective"], -item["separation"], abs(item["boundary_delta"]), item["boundary"]))
    if not candidates:
        return _refused(path, f"semantic {polarity.lower()} phase sector cannot contain a persistent transition", uncertainty=len(ordered)), []
    best = candidates[0]
    if best["separation"] < configuration.minimum_state_separation:
        unrestricted = []
        if polarity == "Release":
            unrestricted_boundaries = range(
                configuration.minimum_support_samples,
                len(ordered) - configuration.minimum_transition_samples + 1,
            )
        else:
            unrestricted_boundaries = range(
                configuration.minimum_transition_samples,
                len(ordered) - configuration.minimum_support_samples + 1,
            )
        for boundary in unrestricted_boundaries:
            if polarity == "Release":
                support, swing = swing_scores[:boundary], swing_scores[boundary:]
            else:
                swing, support = swing_scores[:boundary], swing_scores[boundary:]
            unrestricted.append(_mean(swing) - _mean(support))
        reason = (
            "phase/geometry event ordering contradiction: strongest transition lies outside the semantic neighbourhood"
            if unrestricted and max(unrestricted) >= configuration.minimum_state_separation
            else "no support/swing separability for boundary-censored partial lifecycle"
        )
        row = _refused(path, reason, uncertainty=len(ordered))
        row["candidate_alternatives"] = candidates[:5]
        return row, []
    boundary = int(best["boundary"])
    support_indices = [ordered[position] for position in best["support_positions"]]
    swing_indices = [ordered[position] for position in best["swing_positions"]]
    baseline, spread, drift = _cluster_baseline(samples, support_indices)
    distinct = next((item for item in candidates[1:] if abs(int(item["boundary"]) - boundary) > DETECTOR_INFLUENCE_SAMPLES), None)
    margin = distinct["objective"] - best["objective"] if distinct is not None else 1.0
    proximity = 1.0 - min(1.0, abs(int(best["boundary_delta"])) / max(1, DETECTOR_INFLUENCE_SAMPLES))
    components = {
        "semantic_event_anchor_agreement": 0.80 * float(path.selector_confidence or 0.0) + 0.20 * proximity,
        "within_state_cohesion": best["cohesion"],
        "between_state_separation": min(1.0, best["separation"] / 0.35),
        "transition_persistence_sharpness": best["transition_reward"],
        "contextual_opposite_consistency": _mean([context_scores[position] for position in best["support_positions"]]),
        "ambiguity_margin": min(1.0, margin / 0.04),
    }
    confidence = sum(weight * components[name] for name, weight in (
        ("semantic_event_anchor_agreement", configuration.semantic_weight),
        ("within_state_cohesion", configuration.cohesion_weight),
        ("between_state_separation", configuration.separation_weight),
        ("transition_persistence_sharpness", configuration.transition_weight),
        ("contextual_opposite_consistency", configuration.context_weight),
        ("ambiguity_margin", configuration.ambiguity_weight),
    ))
    alternatives = [{
        f"{polarity.lower()}_sample": samples[ordered[int(item["boundary"])]].frame,
        "objective": item["objective"], "separation": item["separation"],
    } for item in candidates[1:6]]
    if margin < configuration.minimum_ambiguity_margin or confidence < configuration.minimum_confidence:
        reason = "unstable/ambiguous boundary-censored partial segmentation" if margin < configuration.minimum_ambiguity_margin else f"multi-signal confidence {confidence:.6f} is below {configuration.minimum_confidence:.6f}"
        row = _refused(path, reason, baseline, spread, drift, len(ordered))
        row["confidence"], row["confidence_components"] = confidence, components
        row["candidate_alternatives"] = alternatives
        return row, []
    support_displacements = [_distance(samples[left].foot_position, samples[right].foot_position) for left, right in zip(support_indices, support_indices[1:])]
    swing_displacements = [_distance(samples[left].foot_position, samples[right].foot_position) for left, right in zip(swing_indices, swing_indices[1:])]
    absolute_slide = sum(support_displacements)
    slide_ratio = absolute_slide / max(1e-9, absolute_slide + sum(swing_displacements))
    uncertainty = max(1, math.ceil(((1.0 - confidence) + 0.25 * slide_ratio) * len(ordered)))
    duration = samples[-1].time_seconds - samples[0].time_seconds
    transition_index = ordered[boundary]
    transition_time = (samples[transition_index].time_seconds - samples[0].time_seconds) / duration
    first, last = support_indices[0], support_indices[-1]
    acquire = transition_index if polarity == "Acquire" else None
    release = transition_index if polarity == "Release" else None
    matrix = {
        **_refused(path, ""), "outcome": "emitted", "refusal_reason": None,
        "acquire_sample": samples[acquire].frame if acquire is not None else None,
        "support_samples": [samples[first].frame, samples[last].frame],
        "release_sample": samples[release].frame if release is not None else None,
        "baseline_offset_metres": baseline, "baseline_spread_metres": spread,
        "baseline_drift_metres": drift, "confidence": confidence,
        "uncertainty_samples": uncertainty, "refined_boundary_delta_frames": best["boundary_delta"],
        "candidate_alternatives": alternatives, "rejected_alternatives": alternatives,
        "objective_terms": {key: best[key] for key in ("state_cost", "anchor_cost", "transition_reward", "objective")},
        "confidence_components": components, "score_components": components,
        "absolute_slide_diagnostics": {
            "support_path_length_metres": absolute_slide,
            "maximum_support_step_metres": max(support_displacements, default=0.0),
            "support_to_observed_motion_ratio": slide_ratio,
            "fitted_local_point_residual_metres": None, "affects_eligibility": False,
        },
        "lifecycle_coverage": "acquire_outside_clip_or_phase_window" if polarity == "Release" else "release_outside_clip_or_phase_window",
        "signal_summary": {
            "maximum_speed": max(speeds[index] for index in support_indices),
            "maximum_acceleration": max(accelerations[index] for index in support_indices),
            "maximum_jerk": max(jerks[index] for index in support_indices),
            "maximum_angular_velocity": max(angular[index] for index in support_indices),
            "mean_hips_speed": statistics.mean(hips_speeds[index] for index in support_indices),
            "mean_shin_speed": statistics.mean(shin_speeds[index] for index in support_indices),
            "mean_opposite_foot_speed": statistics.mean(opposite_speeds[index] for index in support_indices),
            "mean_foot_root_relative_speed": statistics.mean(relative_rates[0][index] for index in support_indices),
            "mean_foot_hips_relative_speed": statistics.mean(relative_rates[1][index] for index in support_indices),
            "mean_foot_shin_relative_speed": statistics.mean(relative_rates[2][index] for index in support_indices),
        },
    }
    lifecycle = []
    if polarity == "Acquire":
        lifecycle.append({"lifecycle": "acquire", "timing": [transition_time], "confidence": confidence})
        lifecycle.append({"lifecycle": "support", "timing": [transition_time, 1.0], "confidence": confidence})
    else:
        lifecycle.append({"lifecycle": "support", "timing": [0.0, transition_time], "confidence": confidence})
        lifecycle.append({"lifecycle": "release", "timing": [transition_time], "confidence": confidence})
    return matrix, lifecycle


def _refine_cyclic(
    samples: Sequence[BoneSample], path: PhasePath, configuration: ContactConfiguration,
    swing_scores: Sequence[float], context_scores: Sequence[float],
    speeds: Sequence[float], accelerations: Sequence[float], jerks: Sequence[float],
    angular: Sequence[float], hips_speeds: Sequence[float], shin_speeds: Sequence[float],
    opposite_speeds: Sequence[float], relative_rates: Sequence[Sequence[float]],
) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    """Optimise one periodic support/swing lifecycle on unique samples."""
    count = len(samples)
    anchor = path.anchor_frame
    assert anchor is not None
    anchor_index = next((index for index, sample in enumerate(samples) if sample.frame == anchor), None)
    if anchor_index is None:
        return _refused(path, "post-repair directional phase seed is absent from unique runtime samples"), []
    permitted = {
        index for index, sample in enumerate(samples)
        if sample.frame in path.effective_phase_seed_frames
    }
    if not path.effective_phase_seed_frames:
        permitted = {anchor_index}
    if not permitted:
        return _refused(path, "topology-derived cyclic phase sector is empty"), []
    other_index = None
    if path.other_anchor_frame is not None:
        other_frame = path.frame_start if path.duplicate_endpoint and path.other_anchor_frame == path.frame_end else path.other_anchor_frame
        other_index = next((index for index, sample in enumerate(samples) if sample.frame == other_frame), None)
        if other_index == anchor_index:
            return _refused(path, "two-event cyclic authority does not alternate on unique runtime samples"), []

    event_kind = path.event_kind or "Release"
    if event_kind not in {"Acquire", "Release"}:
        return _refused(path, f"unsupported post-repair directional phase event kind '{event_kind}'"), []

    def transition_strength(index: int) -> float:
        local = [
            swing_scores[(index + offset) % count]
            for offset in range(-DETECTOR_INFLUENCE_SAMPLES, DETECTOR_INFLUENCE_SAMPLES + 1)
        ]
        before = local[:DETECTOR_INFLUENCE_SAMPLES]
        after = local[DETECTOR_INFLUENCE_SAMPLES:]
        separation = _mean(after) - _mean(before)
        if event_kind == "Acquire":
            separation = -separation
        return _transition_persistence_quality(local, separation, event_kind)

    strongest = max(range(count), key=lambda index: (transition_strength(index), -_cyclic_distance(index, anchor_index, count), -index))
    permitted_strength = max(transition_strength(index) for index in permitted)
    if (
        all(_cyclic_distance(strongest, index, count) > DETECTOR_INFLUENCE_SAMPLES for index in permitted)
        and transition_strength(strongest) > permitted_strength + configuration.minimum_state_separation
    ):
        row = _refused(
            path,
            "phase/geometry event ordering contradiction: strongest cyclic transition basin lies outside the topology-derived phase sector",
            uncertainty=count,
        )
        row["candidate_alternatives"] = [{
            f"{event_kind.lower()}_sample": samples[strongest].frame,
            "reason": "outside topology-derived phase sector",
        }]
        return row, []

    candidates = []
    for boundary in sorted(permitted, key=lambda index: (((index - anchor_index) % count), index)):
        order = [(boundary + offset) % count for offset in range(count)]
        if event_kind == "Release":
            split_range = range(configuration.minimum_transition_samples, count - configuration.minimum_support_samples + 1)
        else:
            split_range = range(configuration.minimum_support_samples, count - configuration.minimum_transition_samples + 1)
        for split in split_range:
            if event_kind == "Release":
                swing, support = order[:split], order[split:]
                acquire, release = order[split], boundary
            else:
                support, swing = order[:split], order[split:]
                acquire, release = boundary, order[split]
            support_values = [swing_scores[index] for index in support]
            swing_values = [swing_scores[index] for index in swing]
            separation = _mean(swing_values) - _mean(support_values)
            local = [
                swing_scores[(boundary + offset) % count]
                for offset in range(-DETECTOR_INFLUENCE_SAMPLES, DETECTOR_INFLUENCE_SAMPLES + 1)
                if (boundary + offset) % count in permitted
            ]
            transition = _transition_persistence_quality(local, separation, event_kind)
            cohesion = 1.0 - min(1.0, 0.5 * (statistics.pstdev(support_values) + statistics.pstdev(swing_values)))
            signed = (boundary - anchor_index) % count
            if signed > count // 2:
                signed -= count
            sector_radius = max(
                min((index - anchor_index) % count, (anchor_index - index) % count)
                for index in permitted
            )
            anchor_cost = abs(signed) / max(1, sector_radius)
            state_cost = _mean([value * value for value in support_values]) + _mean([(1.0 - value) ** 2 for value in swing_values])
            physical_objective = state_cost - 0.08 * transition
            candidates.append({
                "objective": physical_objective + 0.20 * anchor_cost,
                "physical_objective": physical_objective,
                "state_cost": state_cost, "anchor_cost": anchor_cost,
                "transition_reward": transition, "separation": separation,
                "cohesion": cohesion, "boundary": boundary, "acquire": acquire,
                "release": release, "support": support, "swing": swing, "order": order,
                "boundary_delta": signed,
            })
    candidates.sort(key=lambda item: (
        item["physical_objective"], -item["separation"],
        abs(item["boundary_delta"]), item["boundary"],
    ))
    if not candidates:
        return _refused(path, "topology-derived cyclic phase sector cannot contain a complete lifecycle", uncertainty=count), []
    physical_best = candidates[0]
    unrestricted_good = physical_best["separation"] >= configuration.minimum_state_separation
    if not unrestricted_good:
        row = _refused(path, "no support/swing separability in cyclic relative per-foot motion", uncertainty=count)
        row["candidate_alternatives"] = [{"boundary_sample": samples[item["boundary"]].frame, "separation": item["separation"]} for item in candidates[:5]]
        return row, []
    transition_field = "release" if event_kind == "Release" else "acquire"
    basin = _tied_transition_basin(
        candidates, physical_best, event_kind, count, configuration.minimum_ambiguity_margin,
        objective="physical_objective",
    )
    basin_candidates = [item for item in candidates if int(item[transition_field]) in basin]
    best = min(basin_candidates, key=lambda item: (
        item["objective"], item["physical_objective"], -item["separation"],
        abs(item["boundary_delta"]), item["boundary"],
    ))
    distinct = next((item for item in candidates if int(item[transition_field]) not in basin), None)
    margin = distinct["physical_objective"] - physical_best["physical_objective"] if distinct is not None else 1.0
    semantic = 0.80 * float(path.selector_confidence or 0.0) + 0.20 * max(0.0, 1.0 - best["anchor_cost"])
    components = {
        "semantic_event_anchor_agreement": semantic,
        "within_state_cohesion": best["cohesion"],
        "between_state_separation": min(1.0, best["separation"] / 0.35),
        "transition_persistence_sharpness": best["transition_reward"],
        "contextual_opposite_consistency": _mean([context_scores[index] for index in best["support"]]),
        "ambiguity_margin": min(1.0, margin / 0.04),
    }
    confidence = sum(weight * components[name] for name, weight in (
        ("semantic_event_anchor_agreement", configuration.semantic_weight),
        ("within_state_cohesion", configuration.cohesion_weight),
        ("between_state_separation", configuration.separation_weight),
        ("transition_persistence_sharpness", configuration.transition_weight),
        ("contextual_opposite_consistency", configuration.context_weight),
        ("ambiguity_margin", configuration.ambiguity_weight),
    ))
    support = best["support"]
    baseline, spread, drift = _cluster_baseline(samples, support)
    alternatives = [{
        "event_sample": samples[item["boundary"]].frame,
        "acquire_sample": samples[item["acquire"]].frame,
        "release_sample": samples[item["release"]].frame,
        "objective": item["objective"], "physical_objective": item["physical_objective"],
        "separation": item["separation"],
    } for item in candidates if item is not best][:5]
    if margin < configuration.minimum_ambiguity_margin or confidence < configuration.minimum_confidence:
        reason = "unstable/ambiguous cyclic relative-motion segmentation" if margin < configuration.minimum_ambiguity_margin else f"multi-signal confidence {confidence:.6f} is below {configuration.minimum_confidence:.6f}"
        row = _refused(path, reason, baseline, spread, drift, count)
        row["confidence"], row["confidence_components"] = confidence, components
        row["candidate_alternatives"] = alternatives
        row["rejected_alternatives"] = alternatives
        row["cyclic_search_order"] = [samples[index].frame for index in best["order"]]
        return row, []
    support_displacements = [_distance(samples[left].foot_position, samples[right].foot_position) for left, right in zip(support, support[1:])]
    swing = best["swing"]
    swing_displacements = [_distance(samples[left].foot_position, samples[right].foot_position) for left, right in zip(swing, swing[1:])]
    absolute_slide = sum(support_displacements)
    slide_ratio = absolute_slide / max(1e-9, absolute_slide + sum(swing_displacements))
    basin_width = max(
        (_cyclic_distance(boundary, int(best[transition_field]), count) for boundary in basin),
        default=0,
    )
    uncertainty = max(1, basin_width, math.ceil(((1.0 - confidence) + 0.25 * slide_ratio) * count))
    segments = _cyclic_segments(support, count)
    duration = count * statistics.median(right.time_seconds - left.time_seconds for left, right in zip(samples, samples[1:]))
    lifecycle_intervals = [[segment[0] / count, (segment[-1] + 1) / count] for segment in segments]
    matrix = {
        **_refused(path, ""), "outcome": "emitted", "refusal_reason": None,
        "acquire_sample": samples[best["acquire"]].frame,
        "release_sample": samples[best["release"]].frame,
        "support_samples": [[samples[segment[0]].frame, samples[segment[-1]].frame] for segment in segments] if len(segments) > 1 else [samples[segments[0][0]].frame, samples[segments[0][-1]].frame],
        "baseline_offset_metres": baseline, "baseline_spread_metres": spread,
        "baseline_drift_metres": drift, "confidence": confidence,
        "uncertainty_samples": uncertainty,
        "refined_boundary_delta_frames": best["boundary_delta"],
        "candidate_alternatives": alternatives, "rejected_alternatives": alternatives,
        "objective_terms": {key: best[key] for key in (
            "state_cost", "anchor_cost", "transition_reward", "physical_objective", "objective",
        )},
        "confidence_components": components, "score_components": components,
        "absolute_slide_diagnostics": {
            "support_path_length_metres": absolute_slide,
            "maximum_support_step_metres": max(support_displacements, default=0.0),
            "support_to_observed_motion_ratio": slide_ratio,
            "fitted_local_point_residual_metres": None, "affects_eligibility": False,
        },
        "lifecycle_coverage": "complete_cyclic",
        "cyclic_search_order": [samples[index].frame for index in best["order"]],
        "lifecycle_intervals": lifecycle_intervals,
        "signal_summary": {
            "maximum_speed": max(speeds[index] for index in support),
            "maximum_acceleration": max(accelerations[index] for index in support),
            "maximum_jerk": max(jerks[index] for index in support),
            "maximum_angular_velocity": max(angular[index] for index in support),
            "mean_hips_speed": statistics.mean(hips_speeds[index] for index in support),
            "mean_shin_speed": statistics.mean(shin_speeds[index] for index in support),
            "mean_opposite_foot_speed": statistics.mean(opposite_speeds[index] for index in support),
            "mean_foot_root_relative_speed": statistics.mean(relative_rates[0][index] for index in support),
            "mean_foot_hips_relative_speed": statistics.mean(relative_rates[1][index] for index in support),
            "mean_foot_shin_relative_speed": statistics.mean(relative_rates[2][index] for index in support),
        },
    }
    lifecycle = [
        {"lifecycle": "acquire", "timing": [best["acquire"] / count], "confidence": confidence},
        *({"lifecycle": "support", "timing": interval, "confidence": confidence} for interval in lifecycle_intervals),
        {"lifecycle": "release", "timing": [best["release"] / count], "confidence": confidence},
    ]
    return matrix, lifecycle


def refine_phase_path(
    samples: Sequence[BoneSample], path: PhasePath, configuration: ContactConfiguration,
) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    """Optimise a phase-authorised support lifecycle from relative motion."""
    if path.classification == "fallback_only":
        return _refused(path, "phase policy declares fallback-only coverage"), []
    if path.anchor_frame is None:
        return _refused(path, "no anatomical phase event authorises this foot"), []
    samples = list(samples)
    if path.duplicate_endpoint:
        if (
            path.conceptual_sample_count != len(samples)
            or path.unique_runtime_sample_count != len(samples) - 1
            or not samples or samples[-1].frame != path.frame_end
        ):
            return _refused(path, "trajectory does not match duplicate-endpoint sample provenance"), []
        samples = samples[:-1]
    elif path.unique_runtime_sample_count is not None and path.unique_runtime_sample_count != len(samples):
        return _refused(path, "trajectory does not match unique runtime sample provenance"), []
    if len(samples) < configuration.minimum_support_samples + configuration.minimum_transition_samples:
        return _refused(path, "insufficient native-FPS bone samples"), []
    scalars = [value for sample in samples for value in (
        sample.time_seconds, *sample.foot_position, *sample.foot_rotation, *sample.hips_position,
        *sample.shin_position, *sample.opposite_foot_position, *sample.root_position,
    )]
    if not all(_finite(value) for value in scalars):
        return _refused(path, "non-finite evaluated bone trajectory"), []
    if any(right.time_seconds <= left.time_seconds or right.frame <= left.frame for left, right in zip(samples, samples[1:])):
        return _refused(path, "non-monotonic native-FPS bone trajectory"), []
    is_cyclic = path.topology in {"cyclic_two_release", "one_sided_release"}
    ordered = list(range(len(samples))) if is_cyclic else _candidate_indices(samples, path, configuration)
    minimum = configuration.minimum_support_samples + configuration.minimum_transition_samples
    if len(ordered) < minimum:
        return _refused(path, "phase-constrained candidate window is too short"), []

    times = [sample.time_seconds for sample in samples]
    vector_rates = _cyclic_rates if is_cyclic else _rates
    scalar_rates = _cyclic_scalar_rates if is_cyclic else _scalar_rates
    angular_rates = _cyclic_angular_rates if is_cyclic else _angular_rates
    speeds = vector_rates([sample.foot_position for sample in samples], times)
    accelerations = scalar_rates(speeds, times)
    jerks = scalar_rates(accelerations, times)
    angular = angular_rates([sample.foot_rotation for sample in samples], times)
    hips_speeds = vector_rates([sample.hips_position for sample in samples], times)
    shin_speeds = vector_rates([sample.shin_position for sample in samples], times)
    opposite_speeds = vector_rates([sample.opposite_foot_position for sample in samples], times)
    foot_root = [tuple(a - b for a, b in zip(sample.foot_position, sample.root_position)) for sample in samples]
    foot_hips = [tuple(a - b for a, b in zip(sample.foot_position, sample.hips_position)) for sample in samples]
    foot_shin = [tuple(a - b for a, b in zip(sample.foot_position, sample.shin_position)) for sample in samples]
    relative_rates = [
        vector_rates(foot_root, times), vector_rates(foot_hips, times), vector_rates(foot_shin, times),
    ]
    relative_accelerations = scalar_rates(relative_rates[0], times)
    relative_jerks = scalar_rates(relative_accelerations, times)
    height_ranks = _ranks([samples[index].foot_position[2] for index in ordered])
    ranked_signals = [
        _ranks([signal[index] for index in ordered])
        for signal in (
            relative_rates[0], relative_accelerations, relative_jerks,
            angular, relative_rates[1], relative_rates[2],
        )
    ]
    opposite_rank = _ranks([opposite_speeds[index] for index in ordered])
    swing_scores = []
    context_scores = []
    for position in range(len(ordered)):
        motion = 0.50 * ranked_signals[0][position] + 0.20 * ranked_signals[1][position] + 0.10 * ranked_signals[2][position] + 0.20 * ranked_signals[3][position]
        relative = _mean([signal[position] for signal in ranked_signals[4:]])
        contrast = max(0.0, relative - opposite_rank[position]) if path.topology == "cyclic_two_release" else relative
        swing_scores.append(0.45 * motion + 0.25 * height_ranks[position] + 0.20 * relative + 0.10 * contrast)
        context_scores.append(1.0 - abs(relative - motion))

    anchor_frame = path.anchor_frame
    assert anchor_frame is not None
    if is_cyclic:
        return _refine_cyclic(
            samples, path, configuration, swing_scores, context_scores,
            speeds, accelerations, jerks, angular, hips_speeds, shin_speeds,
            opposite_speeds, relative_rates,
        )
    return _refine_finite(
        samples, path, configuration, ordered, swing_scores, context_scores,
        speeds, accelerations, jerks, angular, hips_speeds, shin_speeds,
        opposite_speeds, relative_rates,
    )



def build_evidence(
    index: dict[str, Any], index_path: Path, blend_path: Path, mapping_path: Path, mapping: dict[str, Any],
    policy_path: Path, policy: dict[str, Any], configuration: ContactConfiguration, package_identity: str,
    generation: str, trajectories: dict[str, dict[str, list[BoneSample]]],
) -> dict[str, Any]:
    samples: list[dict[str, Any]] = []
    clips: list[dict[str, Any]] = []
    matrix: list[dict[str, Any]] = []
    required_refusals = []
    configuration_document = asdict(configuration)
    confidence_weights = {
        "semantic_event_anchor_agreement": configuration.semantic_weight,
        "within_state_cohesion": configuration.cohesion_weight,
        "between_state_separation": configuration.separation_weight,
        "transition_persistence_sharpness": configuration.transition_weight,
        "contextual_opposite_consistency": configuration.context_weight,
        "ambiguity_margin": configuration.ambiguity_weight,
    }
    for entry in index["actions"]:
        identity, role = entry["external_identity"], entry["role"]
        by_foot = trajectories.get(identity, {})
        clip_rows = []
        for path in resolve_phase_paths(entry, policy):
            row, lifecycle = refine_phase_path(by_foot.get(path.foot, []), path, configuration)
            row["identity"] = identity
            row["action"] = entry["action"]
            row.setdefault("event_anchor", path.event_anchor)
            row.setdefault("event_kind", path.event_kind)
            row.setdefault("event_id", path.event_id)
            row.setdefault("sample_topology", {
                "conceptual_sample_count": path.conceptual_sample_count,
                "unique_runtime_sample_count": path.unique_runtime_sample_count,
                "duplicate_endpoint_canonicalised": path.duplicate_endpoint,
            })
            row.setdefault("cyclic_search_order", None)
            row.setdefault("effective_phase_seed_frames", list(path.effective_phase_seed_frames))
            row.setdefault("phase_seed_provenance", path.phase_seed_provenance)
            row.setdefault("lifecycle_intervals", [item["timing"] for item in lifecycle if item["lifecycle"] == "support"])
            row["input_contracts"] = {
                name: value.get("contract") if isinstance(value, Mapping) else None
                for name, value in (
                    ("source_directional_events", entry.get("source_directional_event_evidence")),
                    ("event_derivation", entry.get("event_derivation_evidence")),
                    ("post_repair_directional_phase_events", entry.get("post_repair_event_evidence")),
                    ("selected_loop_repair", entry.get("loop_repair_evidence")),
                )
            }
            row["input_contract_digests"] = {
                name: _canonical_digest(value)
                for name, value in (
                    ("source_directional_events", entry.get("source_directional_event_evidence")),
                    ("event_derivation", entry.get("event_derivation_evidence")),
                    ("post_repair_directional_phase_events", entry.get("post_repair_event_evidence")),
                    ("selected_loop_repair", entry.get("loop_repair_evidence")),
                ) if isinstance(value, Mapping)
            }
            matrix.append(row)
            clip_rows.append(row)
            if row["outcome"] == "refused" and path.classification == "required":
                required_refusals.append(f"{identity}:{path.foot}: {row['refusal_reason']}")
            for item in lifecycle:
                samples.append({
                    "schema_version": CONTACT_SCHEMA_VERSION, "role": role, "foot": path.foot,
                    "lifecycle": item["lifecycle"], "timing": item["timing"], "validity": "valid",
                    "confidence": item["confidence"], "provenance": METHOD,
                    "clip": identity, "variant": index["variant"],
                    "reference": f"source_derived:{identity}:{path.foot}", "identity": identity, "generation": generation,
                })
        emitted = [row for row in clip_rows if row["outcome"] == "emitted"]
        for row in emitted:
            row["overlap_windows"] = []
        if len(emitted) == 2 and all(row["topology"] == "cyclic_two_release" for row in emitted):
            def intervals(row: Mapping[str, Any]) -> list[tuple[int, int]]:
                value = row["support_samples"]
                return [tuple(item) for item in value] if value and isinstance(value[0], list) else [tuple(value)]
            for left_interval in intervals(emitted[0]):
                for right_interval in intervals(emitted[1]):
                    overlap = [max(left_interval[0], right_interval[0]), min(left_interval[1], right_interval[1])]
                    if overlap[0] <= overlap[1]:
                        for row in emitted:
                            row["overlap_windows"].append(overlap)
        clips.append({"identity": identity, "role": role, "action": entry["action"], "paths": clip_rows})
    return {
        "schema_version": CONTACT_SCHEMA_VERSION, "package_identity": package_identity, "samples": samples,
        "source_derived": {
            "schema_version": SOURCE_SCHEMA_VERSION, "method": METHOD, "generation": generation,
            "publication_eligible": not required_refusals, "required_refusals": required_refusals,
            "source_package": {"package": index["package"], "variant": index["variant"], "action_count": index["action_count"]},
            "paths_relative_to_processed_package": {"processed_index": index_path.name, "retargeted_blend": blend_path.name},
            "input_digests": {
                "processed_index_sha256": _sha256(index_path), "retargeted_blend_sha256": _sha256(blend_path),
                "bone_mapping_sha256": _sha256(mapping_path), "phase_contact_policy_sha256": _sha256(policy_path),
            },
            "phase_policy": {"identity": policy["policy_identity"], "digest": _canonical_digest(policy)},
            "bone_mapping": {"identity": mapping["mapping_identity"], "digest": _canonical_digest(mapping), **mapping},
            "input_contracts": [
                "anim002_source_directional_phase_events_v1",
                "anim002_event_derivation_v1",
                "anim002_post_repair_directional_phase_events_v2",
                "anim002_selected_loop_repair_provenance_v2",
            ],
            "phase_seed_policy": {
                "sample_domain": "unique_runtime_samples",
                "duplicate_endpoint_canonicalisation": "conceptual_endpoint_removed_before_signals_and_optimisation",
                "detector_influence_samples": DETECTOR_INFLUENCE_SAMPLES,
                "two_release_sector": "cyclic_voronoi_midpoints",
                "one_release_sector": "half_cycle_around_repeated_seed",
                "repair_displacement": "seed_stencil_intersects_exact_repair_support_dilated_by_detector_influence",
                "absolute_world_slide_affects_eligibility": False,
            },
            "sampling": "native_fps", "configuration": configuration_document,
            "configuration_digest": _canonical_digest(configuration_document),
            "confidence_weights": confidence_weights, "clips": clips,
            "role_contact_matrix": matrix,
        },
    }


def _evaluate_trajectories(
    blend_path: Path, index: dict[str, Any], mapping: dict[str, Any], policy: dict[str, Any],
) -> dict[str, dict[str, list[BoneSample]]]:
    import bpy  # type: ignore[import-not-found]

    bpy.ops.wm.open_mainfile(filepath=str(blend_path), load_ui=False, use_scripts=False)
    rig = bpy.data.objects.get(mapping["skeleton"])
    if rig is None or rig.type != "ARMATURE":
        raise ContactEvidenceError("Retargeted blend lacks the declared mapped armature.")
    bone_names = required_bones(mapping)
    validate_available_bones(mapping, rig.pose.bones.keys())
    declared_actions = {entry["action"] for entry in index["actions"]}
    if {action.name for action in bpy.data.actions} != declared_actions:
        raise ContactEvidenceError("Retargeted blend Action set does not bijectively match the processed index.")
    scene = bpy.context.scene
    native_fps = float(scene.render.fps) / float(scene.render.fps_base or 1.0)
    if not math.isfinite(native_fps) or native_fps <= 0.0:
        raise ContactEvidenceError("Retargeted scene has invalid native FPS.")
    animation_data = rig.animation_data_create()
    previous_action = animation_data.action
    result: dict[str, dict[str, list[BoneSample]]] = {}
    try:
        for entry in index["actions"]:
            resolve_phase_paths(entry, policy)
            action = bpy.data.actions.get(entry["action"])
            if action is None:
                raise ContactEvidenceError(f"Retargeted blend lacks indexed Action '{entry['action']}'.")
            start, end = (int(math.floor(value)) for value in action.frame_range)
            transport = entry.get("transport", {})
            phase = entry.get("post_repair_event_evidence", {})
            timeline = phase.get("timeline", {}) if isinstance(phase, dict) else {}
            if (
                not isinstance(transport, dict) or transport.get("frame_start") != start or transport.get("frame_end") != end
                or (timeline and not math.isclose(float(timeline.get("fps", 0.0)), native_fps, abs_tol=1e-6))
            ):
                raise ContactEvidenceError(f"Processed index timing does not correspond to '{entry['external_identity']}'.")
            animation_data.action = action
            by_foot: dict[str, list[BoneSample]] = {"left": [], "right": []}
            for frame in range(start, end + 1):
                scene.frame_set(frame)
                depsgraph = bpy.context.evaluated_depsgraph_get()
                depsgraph.update()
                evaluated = rig.evaluated_get(depsgraph)
                matrices = {name: evaluated.matrix_world @ evaluated.pose.bones[name].matrix for name in bone_names}
                for foot in ("left", "right"):
                    opposite = "right" if foot == "left" else "left"
                    declaration = mapping["feet"][foot]
                    opposite_declaration = mapping["feet"][opposite]
                    foot_matrix = matrices[declaration["foot_bone"]]
                    rotation = foot_matrix.to_quaternion()
                    def origin(name: str) -> tuple[float, float, float]:
                        value = matrices[name].translation
                        return float(value.x), float(value.y), float(value.z)
                    by_foot[foot].append(BoneSample(
                        frame, (frame - start) / native_fps, origin(declaration["foot_bone"]),
                        (float(rotation.w), float(rotation.x), float(rotation.y), float(rotation.z)),
                        origin(mapping["hips"]), origin(declaration["shin_bone"]),
                        origin(opposite_declaration["foot_bone"]), origin(mapping["root"]),
                    ))
            result[entry["external_identity"]] = by_foot
    finally:
        animation_data.action = previous_action
    return result


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--processed-package", type=Path, required=True)
    parser.add_argument("--mapping", type=Path, required=True)
    parser.add_argument("--policy", type=Path, default=DEFAULT_POLICY)
    parser.add_argument("--package-identity", required=True)
    parser.add_argument("--generation", required=True)
    parser.add_argument("--descriptor-input", required=True)
    parser.add_argument("--output-relative", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--descriptor-relative", type=Path, default=DEFAULT_DESCRIPTOR)
    parser.add_argument("--diagnostic", action="store_true")
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    return parser.parse_args(arguments)


def main() -> None:
    args = parse_args()
    descriptor_input = _durable_resource_path(args.descriptor_input, "Descriptor input")
    output_relative = _portable_relative(args.output_relative, "Output path")
    descriptor_relative = _portable_relative(args.descriptor_relative, "Descriptor path")
    mapping_path, policy_path = args.mapping.resolve(), args.policy.resolve()
    mapping, policy = load_mapping(mapping_path), load_policy(policy_path)
    index, index_path, blend_path = load_processed_package(args.processed_package, mapping)
    configuration = ContactConfiguration()
    trajectories = _evaluate_trajectories(blend_path, index, mapping, policy)
    evidence = build_evidence(
        index, index_path, blend_path, mapping_path, mapping, policy_path, policy, configuration,
        args.package_identity, args.generation, trajectories,
    )
    validate_evidence_matches_package(evidence, index_path, blend_path, mapping_path, policy_path)
    if args.diagnostic:
        print("ANIM002_CONTACT_DIAGNOSTIC=" + json.dumps({
            "publication_eligible": evidence["source_derived"]["publication_eligible"],
            "role_contact_matrix": evidence["source_derived"]["role_contact_matrix"],
        }, sort_keys=True))
        return
    if not evidence["source_derived"]["publication_eligible"]:
        raise ContactEvidenceError("Required contact paths refused: " + " | ".join(evidence["source_derived"]["required_refusals"]))
    package = args.processed_package.resolve()
    output_path, descriptor_path = package / output_relative, package / descriptor_relative
    if output_path.exists() or descriptor_path.exists():
        raise ContactEvidenceError("Refusing to replace an existing contact evidence or descriptor artefact.")
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(evidence, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    descriptor_path.parent.mkdir(parents=True, exist_ok=True)
    descriptor_path.write_text(json.dumps(build_descriptor(descriptor_input, args.package_identity, args.generation), indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print("ANIM002_CONTACT_EVIDENCE=" + json.dumps({
        "evidence": output_relative.as_posix(), "descriptor": descriptor_relative.as_posix(),
        "identity_count": len(index["actions"]), "sample_count": len(evidence["samples"]),
        "package_identity": args.package_identity, "generation": args.generation,
    }, sort_keys=True))
