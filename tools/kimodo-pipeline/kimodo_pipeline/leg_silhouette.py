"""Evaluated anatomical leg-silhouette admission for precision steps."""
from __future__ import annotations

import math
from typing import Any, Mapping, Sequence


PRECISION_LEG_SILHOUETTE_ALGORITHM = "precision_step_evaluated_leg_silhouette_v1"


class LegSilhouetteError(ValueError):
    """The evaluated precision-leg silhouette cannot be measured safely."""


def _at_height(first: tuple[float, float], second: tuple[float, float], height: float) -> float:
    delta = second[1] - first[1]
    if abs(delta) <= 1e-12:
        return min(first[0], second[0])
    ratio = (height - first[1]) / delta
    return first[0] + (second[0] - first[0]) * ratio


def assess_precision_leg_silhouette(
    role_code: str,
    samples: Sequence[tuple[int, Sequence[tuple[float, float]], Sequence[tuple[float, float]]]],
    anatomical_scale: float,
    *,
    stage: str,
    armature_identity: str,
    basis_evidence: Mapping[str, Any],
    tolerance_scale: float = 0.03,
) -> dict[str, Any]:
    """Measure Left-minus-Right leg separation at matching vertical bands.

    Samples are already evaluated in the Root-rest anatomical lateral/up basis.
    Comparing every overlapping hip-to-knee and knee-to-ankle/foot segment makes
    this independent of animation frame, Action identity, rig pose, scale, and
    armature world orientation.
    """
    if role_code not in {"sr", "sl"}:
        raise LegSilhouetteError(f"precision leg-silhouette assessment is not applicable to {role_code}")
    if stage not in {"immutable_pre_repair", "repaired_output", "female_bake", "runtime_playback"}:
        raise LegSilhouetteError(f"unknown precision leg-silhouette stage: {stage}")
    if not samples:
        raise LegSilhouetteError("precision leg-silhouette assessment requires sampled chains")
    if not math.isfinite(anatomical_scale) or anatomical_scale <= 1e-12:
        raise LegSilhouetteError("precision leg-silhouette assessment requires a finite anatomical scale")
    if not math.isfinite(tolerance_scale) or tolerance_scale < 0.0:
        raise LegSilhouetteError("precision leg-silhouette assessment requires a non-negative finite tolerance")

    worst: tuple[float, int, int, int, float, float] | None = None
    for frame, left, right in samples:
        if len(left) < 3 or len(right) < 3:
            raise LegSilhouetteError("precision leg-silhouette assessment requires hip, knee, and ankle/foot points")
        if any(not math.isfinite(value) for chain in (left, right) for point in chain for value in point):
            raise LegSilhouetteError("precision leg-silhouette assessment contains a non-finite point")
        for left_segment, (left_start, left_end) in enumerate(zip(left, left[1:])):
            for right_segment, (right_start, right_end) in enumerate(zip(right, right[1:])):
                low = max(min(left_start[1], left_end[1]), min(right_start[1], right_end[1]))
                high = min(max(left_start[1], left_end[1]), max(right_start[1], right_end[1]))
                if low > high + 1e-12:
                    continue
                for height in (low, high):
                    separation = _at_height(left_start, left_end, height) - _at_height(right_start, right_end, height)
                    candidate = (separation, int(frame), left_segment, right_segment, height, separation)
                    if worst is None or candidate[:4] < worst[:4]:
                        worst = candidate
    if worst is None:
        raise LegSilhouetteError("precision leg chains have no comparable vertical bands")

    minimum, frame, left_segment, right_segment, height, _ = worst
    depth = max(0.0, -minimum)
    normalised_depth = depth / anatomical_scale
    accepted = normalised_depth <= tolerance_scale
    reason = (
        "evaluated leg silhouette remains within the non-crossed tolerance"
        if accepted else
        f"maximum scale-normalised leg-silhouette crossover depth {normalised_depth:.9f} exceeds {tolerance_scale:.9f}"
    )
    segment_names = ("hip_to_knee", "knee_to_ankle", "ankle_to_foot")
    return {
        "algorithm": PRECISION_LEG_SILHOUETTE_ALGORITHM,
        "applicability": "precision_lateral",
        "role": role_code,
        "stage": stage,
        "armature_identity": armature_identity,
        "signed_lateral_convention": "Left leg minus Right leg at matching Root-rest anatomical-up bands; non-crossed is positive",
        "minimum_signed_separation": minimum,
        "maximum_crossover_depth": depth,
        "maximum_scale_normalised_crossover_depth": normalised_depth,
        "normalised_tolerance": tolerance_scale,
        "minimum_allowed_signed_separation": -tolerance_scale * anatomical_scale,
        "anatomical_scale": anatomical_scale,
        "worst_frame": frame,
        "worst_left_segment": segment_names[left_segment] if left_segment < len(segment_names) else f"segment_{left_segment}",
        "worst_right_segment": segment_names[right_segment] if right_segment < len(segment_names) else f"segment_{right_segment}",
        "worst_vertical_band": height,
        "basis": dict(basis_evidence),
        "derivation": "native" if role_code == "sr" else "temporal_reversal",
        "anatomy_mapping": "same left/right anatomy; no spatial reflection or bone swapping",
        "accepted": accepted,
        "rejection_reason": None if accepted else reason,
        "reason": reason,
    }
