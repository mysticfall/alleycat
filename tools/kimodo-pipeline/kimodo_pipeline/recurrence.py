"""Anatomical-scale-normalised physical recurrence acceptance.

The contract measures the closing edge against the opening edge at the native
one-frame sampling interval.  Linear values therefore scale with the longest
deforming anatomical rest segment while angular values do not.  At Kimodo's
authoritative 0.433695449 m scale the limits are 4.337 mm pose, 8.674 mm/frame
linear-velocity delta, and 17.348 mm/frame² linear-acceleration delta.  The
angular equivalents are 0.03 rad, 0.06 rad/frame, and 0.12 rad/frame².

These limits mean that a wrap may consume no more than one percent of a major
limb segment as an instantaneous positional step, with a 2x/4x derivative
envelope for ordinary sampled gait curvature.  They reject a visible pop (for
example the known 42.954 mm / 0.116919 rad WF seam) without pretending that
source-backed motion can be reconstructed to a tighter physical seam.
"""
from __future__ import annotations

from collections.abc import Mapping
import math
from typing import NotRequired, TypedDict


_LENGTH_FACTORS = {
    "pose_translation": .01,
    "linear_velocity": .02,
    "linear_acceleration": .04,
}
_ANGULAR_THRESHOLDS = {
    "pose_rotation_rad": .03,
    "angular_velocity_rad": .06,
    "angular_acceleration_rad": .12,
}
_UNITS = {
    "pose_translation": "metres",
    "pose_rotation_rad": "radians",
    "linear_velocity": "metres/frame",
    "angular_velocity_rad": "radians/frame",
    "linear_acceleration": "metres/frame^2",
    "angular_acceleration_rad": "radians/frame^2",
}

RECURRENCE_CONTRACT = "anatomical_scale_native_frame_pose_derivative_recurrence_v1"
REFERENCE_ANATOMICAL_SCALE_METRES = .433695449
FOOT_MOVING_SPEED_FACTOR = .005
FOOT_SPEED_RATIO_MAXIMUM = 2.5


class RecurrenceDecision(TypedDict):
    """Serializable evidence from the shared physical recurrence gate."""

    accepted: bool
    diagnostic: str
    contract: str
    sampling_interval_frames: int
    character_scale_metres: float
    units: dict[str, str]
    thresholds: dict[str, float]
    failures: dict[str, dict[str, float]]
    foot_failures: NotRequired[object]


def recurrence_thresholds(character_scale: float) -> dict[str, float]:
    """Build native-frame physical limits from anatomical character scale."""
    if not math.isfinite(character_scale) or character_scale <= 1e-12:
        raise ValueError("recurrence requires a valid anatomical character scale")
    return {
        **{name: factor * character_scale for name, factor in _LENGTH_FACTORS.items()},
        **_ANGULAR_THRESHOLDS,
    }


def recurrence_acceptance(
    maxima: Mapping[str, float], character_scale: float, diagnostic: str,
) -> RecurrenceDecision:
    """Return the one physical accept/reject decision used at both gates."""
    thresholds, failures = recurrence_metric_failures(maxima, character_scale)
    return {
        "accepted": not failures,
        "diagnostic": diagnostic,
        "contract": RECURRENCE_CONTRACT,
        "sampling_interval_frames": 1,
        "character_scale_metres": float(character_scale),
        "units": _UNITS,
        "thresholds": thresholds,
        "failures": failures,
    }


def recurrence_metric_failures(
    maxima: Mapping[str, float], character_scale: float,
) -> tuple[dict[str, float], dict[str, dict[str, float]]]:
    """Return thresholds and failed pose/derivative recurrence metrics."""
    thresholds = recurrence_thresholds(character_scale)
    failures = {
        key: {"value": float(maxima[key]), "threshold": threshold}
        for key, threshold in thresholds.items()
        if maxima[key] > threshold
    }
    return thresholds, failures


def foot_recurrence_flags(
    incoming_speed: float,
    outgoing_speed: float,
    velocity_dot: float,
    linear_velocity_delta: float,
    character_scale: float,
) -> tuple[bool, bool]:
    """Classify source-loop foot reversal and boundary-speed spikes."""
    thresholds = recurrence_thresholds(character_scale)
    smaller = min(incoming_speed, outgoing_speed)
    larger = max(incoming_speed, outgoing_speed)
    # These topology guards only activate once both edge samples move by more
    # than 0.5% of anatomical scale per frame. Opposed directions then expose
    # a wrap reversal; a >2.5x speed ratio plus an over-limit derivative exposes
    # a one-frame speed spike. Both are scale-normalised and shared by the two
    # physical gates, rather than numerical reconstruction tolerances.
    moving = smaller > FOOT_MOVING_SPEED_FACTOR * character_scale
    reversal = moving and velocity_dot < 0.0
    spike = (
        moving
        and larger / smaller > FOOT_SPEED_RATIO_MAXIMUM
        and linear_velocity_delta > thresholds["linear_velocity"]
    )
    return reversal, spike
