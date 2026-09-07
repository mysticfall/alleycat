"""Pure, source-sequence-agnostic arc semantics and SE(2) composition."""
from __future__ import annotations

import math
from statistics import mean
from typing import Any, Iterable, Mapping, Sequence


def arc_curve_evidence(
    role_code: str,
    points: Sequence[tuple[float, float]],
    headings: Sequence[float],
    character_scale: float,
) -> dict[str, Any]:
    if len(points) < 3 or len(points) != len(headings) or character_scale <= 0.0:
        raise ValueError("arc evidence requires matching finite trajectory/heading samples and positive scale")
    expected_sign = -1.0 if role_code.startswith("ar_") else 1.0 if role_code.startswith("al_") else 0.0
    if expected_sign == 0.0:
        raise ValueError(f"unsupported arc role: {role_code}")
    steps = [math.dist(points[index - 1], points[index]) for index in range(1, len(points))]
    polyline = sum(steps)
    chord = math.dist(points[0], points[-1])
    lateral_excursion = max((point[0] - points[0][0] for point in points), key=abs)
    role_relative_lateral_excursion = (
        -min(point[0] - points[0][0] for point in points)
        if expected_sign < 0.0
        else max(point[0] - points[0][0] for point in points)
    )
    forward_progress = points[-1][1] - points[0][1]
    yaw_span = headings[-1] - headings[0]
    failures = {}
    if expected_sign * yaw_span <= 0.10:
        failures["yaw_sign_or_span"] = True
    if role_relative_lateral_excursion <= character_scale * .05:
        failures["lateral_curvature_handedness"] = True
    if forward_progress <= character_scale * .10:
        failures["forward_progress"] = True
    if polyline - chord <= character_scale * .01:
        failures["straight_or_near_straight"] = True
    if max(steps) > character_scale * .5:
        failures["unstable_curve_step"] = True
    return {
        "expected_handedness_sign": expected_sign,
        "translation_samples": [list(point) for point in points],
        "heading_samples_rad": list(headings),
        "yaw_span_rad": yaw_span,
        "lateral_excursion": lateral_excursion,
        "role_relative_lateral_excursion": role_relative_lateral_excursion,
        "forward_progress": forward_progress,
        "polyline_length": polyline,
        "chord_length": chord,
        "curvature_excess": polyline - chord,
        "failures": failures,
        "accepted": not failures,
    }


def compose_local_arc_cycles(
    cycle_translation: tuple[float, float], cycle_yaw: float, count: int = 3,
) -> list[tuple[float, float]]:
    if count < 1:
        raise ValueError("cycle composition count must be positive")
    x = z = 0.0
    endpoints = []
    for cycle in range(count):
        angle = cycle * cycle_yaw
        x += math.cos(angle) * cycle_translation[0] - math.sin(angle) * cycle_translation[1]
        z += math.sin(angle) * cycle_translation[0] + math.cos(angle) * cycle_translation[1]
        endpoints.append((x, z))
    return endpoints


def arc_body_support_evidence(
    samples: Sequence[Mapping[str, tuple[float, float]]],
    headings: Sequence[tuple[float, float]],
    character_scale: float,
) -> dict[str, Any]:
    """Measure sustained rearward body carriage in the intended travel frame."""
    if len(samples) < 3 or len(samples) != len(headings) or character_scale <= 0.0:
        raise ValueError("arc body-support evidence requires matching samples and positive scale")
    required = {"hips", "chest", "left_foot", "right_foot"}
    pelvis_rearward = []
    chest_rearward = []
    body_rearward = []
    signed_body_offsets = []
    for sample, heading in zip(samples, headings):
        if set(sample) != required or not all(math.isfinite(value) for point in sample.values() for value in point):
            raise ValueError("arc body-support sample is incomplete or non-finite")
        heading_length = math.hypot(*heading)
        if not math.isfinite(heading_length) or heading_length <= 1e-9:
            raise ValueError("arc body-support heading is non-finite or degenerate")
        forward = heading[0] / heading_length, heading[1] / heading_length
        support = tuple(
            (sample["left_foot"][axis] + sample["right_foot"][axis]) * .5
            for axis in range(2)
        )

        def longitudinal(point: tuple[float, float]) -> float:
            return sum((point[axis] - support[axis]) * forward[axis] for axis in range(2)) / character_scale

        pelvis = longitudinal(sample["hips"])
        chest = longitudinal(sample["chest"])
        body = (pelvis + chest) * .5
        pelvis_rearward.append(max(0.0, -pelvis))
        chest_rearward.append(max(0.0, -chest))
        body_rearward.append(max(0.0, -body))
        signed_body_offsets.append(body)

    ordered = sorted(body_rearward)
    p90 = ordered[min(len(ordered) - 1, math.ceil(.90 * len(ordered)) - 1)]
    return {
        "coordinate_frame": "cycle-normalised generated Root heading; bilateral foot midpoint support origin",
        "normalisation": "anatomical character scale",
        "sample_count": len(samples),
        "mean_rearward_body_offset": mean(body_rearward),
        "p90_rearward_body_offset": p90,
        "mean_rearward_pelvis_offset": mean(pelvis_rearward),
        "mean_rearward_chest_offset": mean(chest_rearward),
        "rearward_body_fraction": sum(value > 0.0 for value in body_rearward) / len(body_rearward),
        "minimum_signed_body_support_offset": min(signed_body_offsets),
    }


def arc_body_support_rank_key(evidence: Mapping[str, Any]) -> tuple[float, ...]:
    """Prefer sustained balance before peak balance without fitted thresholds."""
    return tuple(float(evidence[name]) for name in (
        "mean_rearward_body_offset",
        "p90_rearward_body_offset",
        "mean_rearward_pelvis_offset",
        "mean_rearward_chest_offset",
    ))


def rank_arc_candidates(candidates: Iterable[Mapping[str, Any]]) -> list[Mapping[str, Any]]:
    """Rank only semantically eligible candidates; identity is never dispatch."""
    eligible = [
        row for row in candidates
        if row.get("phase_compatible") and row.get("quality_accepted")
        and row.get("repair_accepted") and row.get("curve_accepted")
    ]
    return sorted(eligible, key=lambda row: (
        *arc_body_support_rank_key(row["body_support"]),
        float(row["repair_cost"]),
        str(row["content_signature"]),
    ))
