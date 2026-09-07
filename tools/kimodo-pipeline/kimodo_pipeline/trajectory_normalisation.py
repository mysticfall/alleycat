"""Pure, scale-invariant admission for minimal strafe trajectory repair.

The normaliser operates in one candidate-local horizontal frame.  It preserves
every sample's signed scalar progress and contracts only the orthogonal
component by the largest admissible constant factor.  It therefore does not
replace source pacing with an authored envelope.
"""
from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Sequence

from .travel_frame import DEFAULT_TRAVEL_FRAME_LIMITS, estimate_source_travel_frame


TRAJECTORY_NORMALISER_VERSION = "xr_candidate_local_orthogonal_contraction_v1"


@dataclass(frozen=True)
class TrajectoryNormalisationLimits:
    target_subwindow_angle_degrees: float = 14.0
    maximum_input_subwindow_angle_degrees: float = 25.0
    maximum_correction_rig_scale: float = 0.15
    maximum_correction_velocity_rig_scale: float = 0.05
    maximum_correction_acceleration_rig_scale: float = 0.03
    maximum_endpoint_derivative_rig_scale: float = 0.03
    maximum_scalar_progress_error_rig_scale: float = 1e-9


DEFAULT_TRAJECTORY_NORMALISATION_LIMITS = TrajectoryNormalisationLimits()


class TrajectoryNormalisationError(ValueError):
    """The candidate is not eligible for bounded ``xr_*`` normalisation."""


@dataclass(frozen=True)
class TrajectoryNormalisation:
    samples: tuple[tuple[float, float], ...]
    evidence: dict[str, object]


def _dot(first: tuple[float, float], second: tuple[float, float]) -> float:
    return first[0] * second[0] + first[1] * second[1]


def _length(value: tuple[float, float]) -> float:
    return math.hypot(*value)


def _differences(values: Sequence[tuple[float, float]]) -> tuple[tuple[float, float], ...]:
    return tuple((second[0] - first[0], second[1] - first[1]) for first, second in zip(values, values[1:]))


def _maximum_length(values: Sequence[tuple[float, float]]) -> float:
    return max((_length(value) for value in values), default=0.0)


def normalise_xr_trajectory(
    role_code: str,
    samples: Sequence[tuple[float, float]],
    rig_scale: float,
    limits: TrajectoryNormalisationLimits = DEFAULT_TRAJECTORY_NORMALISATION_LIMITS,
) -> TrajectoryNormalisation:
    """Return the least orthogonal contraction admitted for an ``xr_*`` slice."""
    if not role_code.startswith("xr_"):
        raise TrajectoryNormalisationError(f"trajectory normalisation is not applicable to {role_code}")
    if len(samples) < 7 or not math.isfinite(rig_scale) or rig_scale <= 1e-12:
        raise TrajectoryNormalisationError("trajectory normalisation requires finite sampled motion and rig scale")
    if any(not math.isfinite(component) for sample in samples for component in sample):
        raise TrajectoryNormalisationError("trajectory contains a non-finite sample")

    origin = samples[0]
    offsets = tuple((sample[0] - origin[0], sample[1] - origin[1]) for sample in samples)
    endpoint = offsets[-1]
    net = _length(endpoint)
    if net / rig_scale < DEFAULT_TRAVEL_FRAME_LIMITS.minimum_net_travel_rig_scale:
        raise TrajectoryNormalisationError("trajectory has insufficient scale-normalised net travel")
    direction = endpoint[0] / net, endpoint[1] / net
    orthogonal = -direction[1], direction[0]
    progress = tuple(_dot(offset, direction) for offset in offsets)
    drift = tuple(_dot(offset, orthogonal) for offset in offsets)

    final_index = len(samples) - 1
    boundaries = (0, final_index // 3, (2 * final_index) // 3, final_index)
    before_angles: list[float] = []
    maximum_factor = 1.0
    for first, last in zip(boundaries, boundaries[1:]):
        along = progress[last] - progress[first]
        across = drift[last] - drift[first]
        if along <= rig_scale * DEFAULT_TRAVEL_FRAME_LIMITS.minimum_subwindow_travel_rig_scale:
            raise TrajectoryNormalisationError("trajectory has a reversing or degenerate travel subwindow")
        angle = math.degrees(math.atan2(abs(across), along))
        before_angles.append(angle)
        if angle > limits.maximum_input_subwindow_angle_degrees:
            raise TrajectoryNormalisationError(
                f"trajectory subwindow angle {angle:.6f} exceeds bounded input limit "
                f"{limits.maximum_input_subwindow_angle_degrees:.6f}"
            )
        if abs(across) > 1e-15:
            maximum_factor = min(
                maximum_factor,
                math.tan(math.radians(limits.target_subwindow_angle_degrees)) * along / abs(across),
            )

    factor = max(0.0, min(1.0, maximum_factor))
    corrected_offsets = tuple(
        (
            direction[0] * along + orthogonal[0] * across * factor,
            direction[1] * along + orthogonal[1] * across * factor,
        )
        for along, across in zip(progress, drift)
    )
    corrected = tuple((origin[0] + value[0], origin[1] + value[1]) for value in corrected_offsets)
    correction = tuple(
        (after[0] - before[0], after[1] - before[1])
        for before, after in zip(samples, corrected)
    )
    velocity = _differences(correction)
    acceleration = _differences(velocity)
    maximum_correction = _maximum_length(correction) / rig_scale
    maximum_velocity = _maximum_length(velocity) / rig_scale
    maximum_acceleration = _maximum_length(acceleration) / rig_scale
    endpoint_derivative = max((_length(velocity[0]), _length(velocity[-1]))) / rig_scale
    endpoint_effect = max(_length(correction[0]), _length(correction[-1])) / rig_scale
    scalar_error = max(
        abs(_dot(value, direction) - along)
        for value, along in zip(corrected_offsets, progress)
    ) / rig_scale
    measurements = {
        "maximum_orthogonal_correction_rig_scale": maximum_correction,
        "maximum_correction_velocity_rig_scale": maximum_velocity,
        "maximum_correction_acceleration_rig_scale": maximum_acceleration,
        "maximum_endpoint_derivative_effect_rig_scale": endpoint_derivative,
        "maximum_endpoint_effect_rig_scale": endpoint_effect,
        "maximum_scalar_progress_error_rig_scale": scalar_error,
    }
    bounds = {
        "maximum_orthogonal_correction_rig_scale": limits.maximum_correction_rig_scale,
        "maximum_correction_velocity_rig_scale": limits.maximum_correction_velocity_rig_scale,
        "maximum_correction_acceleration_rig_scale": limits.maximum_correction_acceleration_rig_scale,
        "maximum_endpoint_derivative_effect_rig_scale": limits.maximum_endpoint_derivative_rig_scale,
        "maximum_endpoint_effect_rig_scale": 1e-12,
        "maximum_scalar_progress_error_rig_scale": limits.maximum_scalar_progress_error_rig_scale,
    }
    failures = {
        key: {"value": value, "limit": bounds[key]}
        for key, value in measurements.items()
        if value > bounds[key] + 1e-12
    }
    if failures:
        raise TrajectoryNormalisationError(f"trajectory correction exceeds bounded admission: {failures}")

    after_frame = estimate_source_travel_frame(corrected, rig_scale)
    if max(after_frame.subwindow_angle_degrees) > limits.target_subwindow_angle_degrees + 1e-9:
        raise TrajectoryNormalisationError("trajectory correction did not establish the requested angular margin")
    evidence: dict[str, object] = {
        "algorithm": TRAJECTORY_NORMALISER_VERSION,
        "applicable_role_family": "xr_*",
        "role": role_code,
        "candidate_local_frame": {
            "origin_lateral_forward": list(origin),
            "travel_direction_lateral_forward": list(direction),
            "orthogonal_direction_lateral_forward": list(orthogonal),
            "basis": "evaluated opening-to-closing Hips horizontal displacement",
        },
        "orthogonal_retention_factor": factor,
        "subwindow_angle_degrees_before": before_angles,
        "subwindow_angle_degrees_after": list(after_frame.subwindow_angle_degrees),
        "target_subwindow_angle_degrees": limits.target_subwindow_angle_degrees,
        "unchanged_selection_limit_degrees": DEFAULT_TRAVEL_FRAME_LIMITS.maximum_subwindow_angle_degrees,
        "scalar_progress": list(progress),
        **measurements,
        "admission_bounds": bounds | {
            "maximum_input_subwindow_angle_degrees": limits.maximum_input_subwindow_angle_degrees,
        },
        "endpoint_correction_is_bilateral_zero": endpoint_effect <= 1e-12,
        "source_pacing_changed": False,
    }
    return TrajectoryNormalisation(corrected, evidence)
