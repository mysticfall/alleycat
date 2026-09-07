"""Auditable, bounded Root-local pivot-yaw authoring helpers."""
from __future__ import annotations

from dataclasses import asdict, dataclass
import math
from typing import Any, Mapping

from .contracts import pivot_root_yaw_sign


# Evaluated Blender transforms are single-precision at the source boundary.
# This is deliberately far below a meaningful authored heading change
# (roughly 0.000057 degrees), while absorbing only round-off between adjacent
# sampled frames.  It is a validation allowance, never a signal repair.
HEADING_MONOTONICITY_TOLERANCE = 1e-6
MINIMUM_SIGNED_HEADING_RANGE = 0.10

# These limits deliberately admit the observed, single-frame natural reversal
# (about 0.0002985 rad) without making a generic trace repair permissible.
# They are relative to the validated directional source span and are persisted
# with every Segment-2 selection for review.
MAXIMUM_CORRECTION_MAGNITUDE = 0.01
MAXIMUM_CORRECTED_DURATION = 0.20
MAXIMUM_CORRECTION_AREA = 0.001


@dataclass(frozen=True)
class PivotHeadingProjection:
    """Result of the named bounded phase-preserving monotonic projection."""

    role_code: str
    raw_trace: tuple[float, ...]
    projected_trace: tuple[float, ...]
    unit_yaw: tuple[float, ...]
    directional_span: float
    maximum_correction: float
    corrected_duration: float
    correction_area: float
    correction_runs: tuple[dict[str, float | int], ...]

    def evidence(self) -> dict[str, object]:
        """Return JSON-safe audit data; source and projection remain distinct."""
        result = asdict(self)
        result["procedure"] = "bounded_phase_preserving_monotonic_projection"
        result["budgets"] = {
            "maximum_correction_magnitude": MAXIMUM_CORRECTION_MAGNITUDE,
            "maximum_corrected_duration": MAXIMUM_CORRECTED_DURATION,
            "maximum_correction_area": MAXIMUM_CORRECTION_AREA,
            "minimum_signed_heading_range_rad": MINIMUM_SIGNED_HEADING_RANGE,
        }
        result["source_phase_tolerances"] = {
            "lead_peak_timing_frames": 0,
            "foot_lift_peak_timing_frames": 0,
            "torso_timing_frames": 0,
            "body_heading_degrees": 5.0,
        }
        return result


def _unwrapped_progress(values: tuple[float, ...]) -> tuple[float, ...]:
    unwrapped = [values[0]]
    for value in values[1:]:
        delta = value - unwrapped[-1]
        while delta > math.pi:
            delta -= 2.0 * math.pi
        while delta < -math.pi:
            delta += 2.0 * math.pi
        unwrapped.append(unwrapped[-1] + delta)
    origin = unwrapped[0]
    return tuple(value - origin for value in unwrapped)


def _isotonic_non_decreasing(values: tuple[float, ...]) -> tuple[float, ...]:
    """Use PAVA only on the supplied trace; it neither resamples nor smooths."""
    blocks: list[list[float | int]] = []  # weighted sum, weight, first, last
    for index, value in enumerate(values):
        blocks.append([value, 1.0, index, index])
        while len(blocks) > 1 and blocks[-2][0] / blocks[-2][1] > blocks[-1][0] / blocks[-1][1]:
            right, left = blocks.pop(), blocks.pop()
            blocks.append([left[0] + right[0], left[1] + right[1], left[2], right[3]])
    projected = [0.0] * len(values)
    for total, weight, first, last in blocks:
        for index in range(int(first), int(last) + 1):
            projected[index] = float(total / weight)
    return tuple(projected)


def _correction_runs(errors: tuple[float, ...]) -> tuple[dict[str, float | int], ...]:
    runs, start = [], None
    for index, error in enumerate(errors):
        if error > HEADING_MONOTONICITY_TOLERANCE and start is None:
            start = index
        if start is not None and (error <= HEADING_MONOTONICITY_TOLERANCE or index == len(errors) - 1):
            end = index - 1 if error <= HEADING_MONOTONICITY_TOLERANCE else index
            runs.append({"start_sample": start, "end_sample": end, "sample_count": end - start + 1,
                         "maximum_error_rad": max(errors[start:end + 1])})
            start = None
    return tuple(runs)


def project_pivot_heading(role_code: str, source_global_z_yaw: list[float]) -> PivotHeadingProjection:
    """Apply the one permitted bounded, phase-preserving monotonic projection.

    The raw input is copied before unwrapping and is never mutated. PAVA changes
    only adjacent monotonicity violations at their original sample times; it is
    not an envelope, filter, interpolation, or replacement timing curve.
    """
    if role_code not in {"pr45", "pl45"}:
        raise ValueError(f"unsupported pivot role: {role_code}")
    if len(source_global_z_yaw) < 3:
        raise ValueError("pivot unit requires at least three samples")
    raw_input = tuple(float(value) for value in source_global_z_yaw)
    if not all(math.isfinite(value) for value in raw_input):
        raise ValueError("pivot source Hips global-Z heading contains a non-finite sample")
    endpoint = pivot_root_yaw_sign(role_code) * math.pi / 4.0
    sign = 1.0 if endpoint > 0 else -1.0
    raw = _unwrapped_progress(raw_input)
    span = raw[-1] * sign
    if span < -HEADING_MONOTONICITY_TOLERANCE:
        raise ValueError(
            "pivot source Hips global-Z heading has the wrong signed direction: "
            f"endpoint progress {raw[-1]:.9g} rad for {role_code}"
        )
    if span <= MINIMUM_SIGNED_HEADING_RANGE:
        raise ValueError(
            "pivot source Hips global-Z heading has insufficient signed range: "
            f"{span:.9g} rad (requires > {MINIMUM_SIGNED_HEADING_RANGE:.9g} rad)"
        )
    directed = tuple(value * sign for value in raw)
    # Bound the *projection* to immutable endpoints before PAVA. An endpoint
    # overshoot is a final local reversal, not a licence to move either source
    # endpoint. Its correction is still counted and budget-gated below.
    endpoint_bounded = tuple(min(span, max(0.0, value)) for value in directed)
    projected_directed = _isotonic_non_decreasing(endpoint_bounded)
    if projected_directed[0] != 0.0 or not math.isclose(projected_directed[-1], span, abs_tol=HEADING_MONOTONICITY_TOLERANCE):
        raise ValueError("pivot projection did not preserve immutable source endpoints")
    projected = tuple(value * sign for value in projected_directed)
    errors = tuple(abs(after - before) for before, after in zip(raw, projected))
    runs = _correction_runs(errors)
    maximum = max(errors) / span
    corrected_duration = sum(run["sample_count"] for run in runs) / (len(raw) - 1)
    area = sum((left + right) * 0.5 for left, right in zip(errors, errors[1:])) / (span * (len(raw) - 1))
    if maximum > MAXIMUM_CORRECTION_MAGNITUDE:
        raise ValueError(f"pivot heading reversal exceeds correction-magnitude budget ({maximum:.9g} > {MAXIMUM_CORRECTION_MAGNITUDE:.9g})")
    if corrected_duration > MAXIMUM_CORRECTED_DURATION:
        raise ValueError(f"pivot heading reversals exceed corrected-duration budget ({corrected_duration:.9g} > {MAXIMUM_CORRECTED_DURATION:.9g})")
    if area > MAXIMUM_CORRECTION_AREA:
        raise ValueError(f"pivot heading reversals exceed correction-area budget ({area:.9g} > {MAXIMUM_CORRECTION_AREA:.9g})")
    unit = tuple(endpoint * value / raw[-1] for value in projected)
    if any(value * sign < -HEADING_MONOTONICITY_TOLERANCE or value * sign > math.pi / 4.0 + HEADING_MONOTONICITY_TOLERANCE for value in unit):
        raise ValueError("pivot projection produces an invalid bounded unit")
    return PivotHeadingProjection(role_code, raw, projected, unit, span, maximum, corrected_duration, area, runs)


def pivot_unit_yaw(role_code: str, source_global_z_yaw: list[float]) -> list[float]:
    """Map once from the permitted projected source phase to Root local-Y."""
    return list(project_pivot_heading(role_code, source_global_z_yaw).unit_yaw)


def pivot_three_cycle_evidence(
    role_code: str,
    unit_yaw: tuple[float, ...] | list[float],
    root_translation: tuple[tuple[float, float, float], ...] | list[list[float]],
) -> dict[str, Any]:
    """Prove three-cycle accumulated yaw and exact-zero translation."""
    if len(unit_yaw) < 3 or len(root_translation) != len(unit_yaw):
        raise ValueError("pivot three-cycle evidence requires matching complete unit samples")
    sign = pivot_root_yaw_sign(role_code)
    endpoint = sign * math.pi / 4.0
    if any(any(float(component) != 0.0 for component in sample) for sample in root_translation):
        raise ValueError("pivot three-cycle composition contains Root translation leakage")
    if not math.isclose(float(unit_yaw[0]), 0.0, abs_tol=1e-12):
        raise ValueError("pivot unit does not start at zero yaw")
    if not math.isclose(float(unit_yaw[-1]), endpoint, abs_tol=1e-9):
        raise ValueError("pivot unit has an incorrect signed 45-degree endpoint")
    if any((float(after) - float(before)) * sign < -HEADING_MONOTONICITY_TOLERANCE for before, after in zip(unit_yaw, unit_yaw[1:])):
        raise ValueError("pivot unit contains a wrong-sign or non-monotonic yaw reversal")
    period = len(unit_yaw) - 1
    boundaries = [cycle * endpoint for cycle in range(4)]
    samples = []
    for cycle in range(3):
        first_frame = 1 if cycle == 0 else cycle * period + 2
        for index, value in enumerate(unit_yaw if cycle == 0 else unit_yaw[1:], first_frame):
            samples.append({"frame": index, "yaw": cycle * endpoint + float(value), "translation": [0.0, 0.0, 0.0]})
    return {
        "period_frames": period,
        "cycles": 3,
        "boundary_yaw": boundaries,
        "final_yaw": 3.0 * endpoint,
        "root_translation_maximum": 0.0,
        "reset_detected": False,
        "wrong_sign_detected": False,
        "samples": samples,
    }


def validate_pivot_repair_oracles(evidence: Mapping[str, Any]) -> None:
    """Reject the declared source-repair corruption classes from durable evidence."""
    required_frame = "cycle-normalised generated Root"
    if evidence.get("comparison_frame") != required_frame:
        raise ValueError("pivot repair uses the wrong comparison frame")
    if evidence.get("both_repair_windows_used") is not True:
        raise ValueError("pivot repair is one-sided")
    if float(evidence.get("maximum_immutable_interior_change", math.inf)) != 0.0:
        raise ValueError("pivot repair leaked into the immutable interior")
    if float(evidence.get("root_translation_maximum", math.inf)) != 0.0:
        raise ValueError("pivot repair contains Root translation leakage")
    if evidence.get("double_application_detected") is True:
        raise ValueError("pivot repair was applied twice")
    if evidence.get("contact_topology_preserved") is not True:
        raise ValueError("pivot repair changed planted-foot topology")
    if float(evidence.get("maximum_contact_velocity_delta", math.inf)) > float(evidence.get("contact_velocity_limit", -math.inf)):
        raise ValueError("pivot repair introduced a planted-foot velocity spike")
    if float(evidence.get("maximum_limb_length_delta", math.inf)) > float(evidence.get("limb_length_limit", -math.inf)):
        raise ValueError("pivot repair stretched a limb")
    if float(evidence.get("minimum_knee_orientation_dot", -math.inf)) < float(evidence.get("knee_orientation_limit", math.inf)):
        raise ValueError("pivot repair inverted a knee")
