"""Scale-invariant Root confinement for ANIM-002 precision side steps.

The complete Segment-1 trace is retained.  Only its small cycle-closing Root
residual is removed, using the minimum-jerk endpoint interpolant; visible body
motion remains source-authored because the materialiser solves Hips carriage
against the corrected Root exactly once.
"""
from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Any, Mapping, Sequence


PRECISION_STEP_NORMALISER_VERSION = "precision_step_confined_lateral_projection_v1"
PRECISION_FOOT_ORDER_ALGORITHM = "precision_step_signed_anatomical_foot_order_v1"
PRECISION_EVENT_ALGORITHM = "precision_step_complete_lateral_event_v1"
# This is deliberately policy configuration, rather than an eligibility rule.
# It identifies the one calibration measurement from which the normal sliding
# budget is derived; normal ranking only sees evaluated semantics.
PRECISION_CALIBRATION_BASELINE_ACTION = "kimodo_multi_388sjrhc"


@dataclass(frozen=True)
class PrecisionStepLimits:
    minimum_lateral_excursion_scale: float = 0.08
    maximum_lateral_excursion_scale: float = 1.00
    maximum_wrong_sign_excursion_scale: float = 0.12
    maximum_forward_excursion_scale: float = 0.25
    maximum_source_yaw_excursion_radians: float = 0.35
    maximum_crossover_depth_scale: float = 0.03
    maximum_lateral_width_scale: float = 1.20
    soft_lateral_width_scale: float = 0.70
    maximum_lateral_path_net_ratio: float = 50.0
    calibration_sliding_margin_scale: float = 0.01
    minimum_release_speed_scale: float = 0.005
    minimum_release_directional_confidence: float = 0.60
    release_speed_margin_scale: float = 0.0001


DEFAULT_PRECISION_STEP_LIMITS = PrecisionStepLimits()


class PrecisionStepError(ValueError):
    """A complete real Segment-1 candidate is not safely confinable."""


@dataclass(frozen=True)
class PrecisionStepNormalisation:
    lateral_samples: tuple[float, ...]
    evidence: dict[str, object]


def assess_precision_semantic_events(
    samples: Sequence[tuple[int, float, float, float, float]],
    lateral_trajectory: Sequence[float],
    rig_scale: float,
    limits: PrecisionStepLimits = DEFAULT_PRECISION_STEP_LIMITS,
) -> dict[str, Any]:
    """Assess complete native right-side precision events over Segment 1.

    Each sample is ``(frame, left lateral velocity, left speed, right lateral
    velocity, right speed)`` from the authoritative evaluated rig.  A release
    requires directed lateral motion, directional confidence and an opposite
    planted foot.  Consecutive qualifying samples form one event; a complete
    event must both release and return to support inside the supplied window.
    The independent trajectory record makes a stationary/contact-only trace
    ineligible even if its velocity samples are malformed.
    """
    if len(samples) < 3 or len(lateral_trajectory) != len(samples):
        raise PrecisionStepError("precision semantic assessment requires a complete sampled Segment-1 window")
    if not math.isfinite(rig_scale) or rig_scale <= 1e-12:
        raise PrecisionStepError("precision semantic assessment requires a finite anatomical scale")
    if any(not math.isfinite(value) for row in samples for value in row[1:]) or any(
        not math.isfinite(value) for value in lateral_trajectory
    ):
        raise PrecisionStepError("precision semantic assessment contains a non-finite sample")

    minimum_speed = limits.minimum_release_speed_scale * rig_scale
    speed_margin = limits.release_speed_margin_scale * rig_scale
    release_rows: list[dict[str, Any]] = []
    active: list[dict[str, Any]] = []
    for frame, left_velocity, left_speed, right_velocity, right_speed in samples:
        qualifying: list[dict[str, Any]] = []
        for side, velocity, speed, other_speed in (
            ("Left", left_velocity, left_speed, right_speed),
            ("Right", right_velocity, right_speed, left_speed),
        ):
            directed = -velocity  # Native ``sr`` moves towards anatomical right.
            confidence = directed / speed if speed > 1e-12 else 0.0
            accepted = (
                directed >= minimum_speed
                and confidence >= limits.minimum_release_directional_confidence
                and speed >= other_speed + speed_margin
            )
            row = {
                "frame": frame, "foot_side": side, "lateral_velocity": velocity,
                "speed": speed, "opposite_speed": other_speed,
                "directed_lateral_velocity": directed, "directional_confidence": confidence,
                "release_accepted": accepted,
            }
            release_rows.append(row)
            if accepted:
                qualifying.append(row)
        # Simultaneous releases do not describe a planted-foot precision step.
        active.append(qualifying[0] if len(qualifying) == 1 else {})

    # A precision event is the continuous *body lateral trajectory*, not each
    # alternating foot release within it.  Releases are retained as evidence
    # inside that event.  This prevents the two feet participating in a single
    # side step from being miscounted as a sustained two-step gait.
    trajectory_velocity = [
        lateral_trajectory[index] - lateral_trajectory[index - 1] if index else 0.0
        for index in range(len(lateral_trajectory))
    ]
    moving = [velocity < -minimum_speed for velocity in trajectory_velocity]
    events: list[dict[str, Any]] = []
    index = 0
    while index < len(moving):
        if not moving[index]:
            index += 1
            continue
        first = index
        while index + 1 < len(moving) and moving[index + 1]:
            index += 1
        last = index
        releases = [dict(active[item]) for item in range(first, last + 1) if active[item]]
        trajectory_start = lateral_trajectory[first]
        trajectory_end = lateral_trajectory[last]
        events.append({
            "start_frame": samples[first][0], "release_frame": samples[first][0],
            "support_return_frame": samples[last + 1][0] if last + 1 < len(samples) else None,
            "end_frame": samples[last][0], "foot_sides": sorted({str(row["foot_side"]) for row in releases}),
            "direction": "right", "complete": first > 0 and last < len(samples) - 1 and bool(releases),
            "release_samples": releases,
            "lateral_trajectory_delta": trajectory_end - trajectory_start,
            "trajectory_direction_accepted": trajectory_end < trajectory_start,
        })
        index += 1
    # A natural precision side-step may transfer support between feet after a
    # brief planted interval.  Join those release/trajectory lobes only when
    # their gap is local-duration bounded; separated sustained-gait lobes stay
    # independent events.  This is measured from the window, never source IDs.
    merge_gap = max(1, len(samples) // 4)
    merged: list[dict[str, Any]] = []
    for event in events:
        if merged and samples.index(next(row for row in samples if row[0] == event["start_frame"])) - samples.index(
            next(row for row in samples if row[0] == merged[-1]["end_frame"])
        ) - 1 <= merge_gap:
            prior = merged[-1]
            prior["end_frame"] = event["end_frame"]
            prior["support_return_frame"] = event["support_return_frame"]
            prior["foot_sides"] = sorted(set(prior["foot_sides"]) | set(event["foot_sides"]))
            prior["release_samples"].extend(event["release_samples"])
            prior["lateral_trajectory_delta"] += event["lateral_trajectory_delta"]
            prior["complete"] = prior["complete"] and event["complete"]
        else:
            merged.append(event)
    complete = [event for event in merged if event["complete"] and event["trajectory_direction_accepted"]]
    net = lateral_trajectory[-1] - lateral_trajectory[0]
    accepted = len(complete) == 1
    reason = "exactly one complete precision side-step event" if accepted else (
        f"expected exactly one complete precision side-step event; observed {len(complete)}"
    )
    return {
        "algorithm": PRECISION_EVENT_ALGORITHM, "window": [samples[0][0], samples[-1][0]],
        "native_direction": "right", "release_criteria": {
            "minimum_directed_lateral_speed": minimum_speed,
            "minimum_directional_confidence": limits.minimum_release_directional_confidence,
            "minimum_speed_advantage_over_planted_foot": speed_margin,
        },
        "trajectory_criteria": {
            "net_lateral_displacement": net,
            "minimum_rightward_displacement": limits.minimum_lateral_excursion_scale * rig_scale,
        },
        "release_rows": release_rows, "lateral_trajectory": list(lateral_trajectory),
        "trajectory_velocity": trajectory_velocity, "event_merge_gap_samples": merge_gap,
        "events": merged, "complete_event_count": len(complete),
        "accepted": accepted, "rejection_reason": None if accepted else reason, "reason": reason,
    }


def assess_precision_foot_order(
    role_code: str,
    samples: Sequence[tuple[int, float, float]],
    rig_scale: float,
    *,
    stage: str,
    basis_evidence: Mapping[str, Any],
    limits: PrecisionStepLimits = DEFAULT_PRECISION_STEP_LIMITS,
) -> dict[str, Any]:
    """Assess non-crossed feet using positive-left anatomical lateral coordinates."""
    if role_code not in {"sr", "sl"}:
        raise PrecisionStepError(f"precision foot-order assessment is not applicable to {role_code}")
    if stage not in {"immutable_pre_repair", "repaired_output"}:
        raise PrecisionStepError(f"unknown precision foot-order assessment stage: {stage}")
    if not samples:
        raise PrecisionStepError("precision foot-order assessment requires sampled feet")
    if not math.isfinite(rig_scale) or rig_scale <= 1e-12:
        raise PrecisionStepError("precision foot-order assessment requires a finite anatomical scale")
    if any(not math.isfinite(value) for _, left, right in samples for value in (left, right)):
        raise PrecisionStepError("precision foot-order assessment contains a non-finite sample")

    separations = [(sample, left - right) for sample, left, right in samples]
    responsible_sample, minimum = min(separations, key=lambda item: (item[1], item[0]))
    crossover_depth = max(0.0, -minimum)
    normalised_depth = crossover_depth / rig_scale
    threshold = limits.maximum_crossover_depth_scale
    accepted = normalised_depth <= threshold
    reason = (
        "signed anatomical foot order remains within the non-crossed tolerance"
        if accepted else
        f"maximum scale-normalised crossover depth {normalised_depth:.9f} exceeds {threshold:.9f}"
    )
    return {
        "algorithm": PRECISION_FOOT_ORDER_ALGORITHM,
        "applicability": "precision_lateral",
        "role": role_code,
        "stage": stage,
        "signed_lateral_convention": "LeftFoot minus RightFoot on anatomy-oriented Root-rest lateral; non-crossed is positive",
        "minimum_signed_separation": minimum,
        "maximum_crossover_depth": crossover_depth,
        "maximum_scale_normalised_crossover_depth": normalised_depth,
        "responsible_sample": responsible_sample,
        "anatomical_scale": rig_scale,
        "normalised_tolerance": threshold,
        "minimum_allowed_signed_separation": -threshold * rig_scale,
        "derivation": "native" if role_code == "sr" else "temporal_reversal",
        "anatomy_mapping": "same LeftFoot and RightFoot anatomy; temporal reversal never swaps bones",
        "basis": dict(basis_evidence),
        "accepted": accepted,
        "rejection_reason": None if accepted else reason,
        "reason": reason,
    }


def select_precision_candidate(candidates: Sequence[Mapping[str, Any]]) -> Mapping[str, Any]:
    """Select by semantic/evaluated evidence; labels and sequence order are inert."""
    eligible = [
        row for row in candidates
        if row.get("phase_compatible") is True
        and row.get("foot_order_accepted") is True
        and row.get("quality_accepted") is True
        and row.get("repair_accepted") is True
    ]
    if not eligible:
        raise PrecisionStepError("no foot-order-, phase-, quality-, and repair-compatible precision-step candidate")
    return min(eligible, key=precision_candidate_rank_key)


def precision_candidate_rank_key(row: Mapping[str, Any]) -> tuple[Any, ...]:
    """Return the declared precision-family ranking key.

    All values are already rig-scale-normalised.  Missing optional evidence is
    deliberately worst-case so an older/stale record cannot win a selection.
    """
    metrics = row.get("width_evidence", {})
    confinement = row.get("confinement", {})
    support = row.get("quality_metrics", {})
    return (
        float(row["repair_cost"]),
        float(metrics.get("maximum", math.inf)),
        float(metrics.get("median", math.inf)),
        float(confinement.get("reverse_lateral_excursion_scale", math.inf)),
        float(confinement.get("fore_aft_range_scale", math.inf)),
        float(confinement.get("yaw_range_radians", math.inf)),
        float(confinement.get("lateral_path_net_endpoint_ratio", math.inf)),
        float(support.get("body_support_offset", math.inf)),
        float(support.get("planted_foot_slip", math.inf)),
        float(support.get("foot_acceleration", math.inf)),
        float(support.get("foot_jerk", math.inf)),
        str(row["source_window_content_signature"]),
    )


def precision_confinement_evidence(
    role_code: str,
    lateral_forward_samples: Sequence[tuple[float, float]],
    yaw_samples: Sequence[float],
    rig_scale: float,
    limits: PrecisionStepLimits = DEFAULT_PRECISION_STEP_LIMITS,
) -> dict[str, object]:
    """Measure and admit complete Segment-1 lateral semantics before repair.

    This is a measurement gate, not a trajectory correction.  The complete
    source trace remains untouched by the precision policy.
    """
    if role_code not in {"sr", "sl"}:
        raise PrecisionStepError(f"precision-step confinement is not applicable to {role_code}")
    if len(lateral_forward_samples) < 11 or len(yaw_samples) != len(lateral_forward_samples):
        raise PrecisionStepError("precision-step confinement requires one complete sampled cycle")
    if not math.isfinite(rig_scale) or rig_scale <= 1e-12:
        raise PrecisionStepError("precision-step confinement requires a finite anatomical scale")
    if any(not math.isfinite(value) for sample in lateral_forward_samples for value in sample) or any(
        not math.isfinite(value) for value in yaw_samples
    ):
        raise PrecisionStepError("precision-step confinement contains a non-finite sample")

    origin_lateral, origin_forward = lateral_forward_samples[0]
    lateral = tuple(sample[0] - origin_lateral for sample in lateral_forward_samples)
    forward = tuple(sample[1] - origin_forward for sample in lateral_forward_samples)
    sign = -1.0 if role_code == "sr" else 1.0
    signed_lateral = tuple(sign * value for value in lateral)
    intended_excursion = max(signed_lateral)
    reverse_excursion = max(0.0, -min(signed_lateral))
    path_length = sum(abs(second - first) for first, second in zip(lateral, lateral[1:]))
    endpoint = signed_lateral[-1]
    endpoint_magnitude = abs(endpoint)
    path_ratio = path_length / endpoint_magnitude if endpoint_magnitude > rig_scale * 1e-9 else math.inf
    forward_range = max(forward) - min(forward)
    forward_endpoint_drift = abs(forward[-1])
    yaw_origin = yaw_samples[0]
    yaw_range = max(yaw_samples) - min(yaw_samples)
    checks = {
        "lateral_excursion_minimum": (intended_excursion, limits.minimum_lateral_excursion_scale * rig_scale, "minimum"),
        "lateral_excursion_maximum": (intended_excursion, limits.maximum_lateral_excursion_scale * rig_scale, "maximum"),
        "reverse_lateral_excursion": (reverse_excursion, limits.maximum_wrong_sign_excursion_scale * rig_scale, "maximum"),
        "fore_aft_range": (forward_range, limits.maximum_forward_excursion_scale * rig_scale, "maximum"),
        "fore_aft_endpoint_drift": (forward_endpoint_drift, limits.maximum_forward_excursion_scale * rig_scale, "maximum"),
        "yaw_range": (yaw_range, limits.maximum_source_yaw_excursion_radians, "maximum"),
        "lateral_path_net_endpoint_ratio": (path_ratio, limits.maximum_lateral_path_net_ratio, "maximum"),
    }
    failures = {
        name: {"value": value, "threshold": threshold}
        for name, (value, threshold, kind) in checks.items()
        if (kind == "minimum" and value < threshold) or (kind == "maximum" and value > threshold)
    }
    return {
        "policy": "precision_lateral_complete_segment_confinement_v1",
        "role_family": "precision_lateral",
        "role": role_code,
        "signed_intended_lateral_endpoint": endpoint,
        "signed_intended_lateral_excursion": intended_excursion,
        "reverse_lateral_excursion": reverse_excursion,
        "reverse_lateral_excursion_scale": reverse_excursion / rig_scale,
        "fore_aft_range": forward_range,
        "fore_aft_range_scale": forward_range / rig_scale,
        "fore_aft_endpoint_drift": forward_endpoint_drift,
        "yaw_range_radians": yaw_range,
        "yaw_origin_radians": yaw_origin,
        "lateral_path_length": path_length,
        "lateral_path_net_endpoint_ratio": path_ratio,
        "limits": {name: threshold for name, (_, threshold, _) in checks.items()},
        "accepted": not failures,
        "failures": failures,
        "reason": "accepted complete precision-step confinement" if not failures else f"precision confinement rejected: {failures}",
    }


def normalise_precision_step(
    role_code: str,
    lateral_forward_samples: Sequence[tuple[float, float]],
    yaw_samples: Sequence[float],
    rig_scale: float,
    limits: PrecisionStepLimits = DEFAULT_PRECISION_STEP_LIMITS,
) -> PrecisionStepNormalisation:
    """Confine the native right step or its same-anatomy temporal reversal."""
    if role_code not in {"sr", "sl"}:
        raise PrecisionStepError(f"precision-step normalisation is not applicable to {role_code}")
    if len(lateral_forward_samples) < 11 or len(yaw_samples) != len(lateral_forward_samples):
        raise PrecisionStepError("precision-step normalisation requires one complete sampled cycle")
    if not math.isfinite(rig_scale) or rig_scale <= 1e-12:
        raise PrecisionStepError("precision-step normalisation requires a finite anatomical scale")
    if any(not math.isfinite(value) for sample in lateral_forward_samples for value in sample):
        raise PrecisionStepError("precision-step trajectory contains a non-finite sample")
    if any(not math.isfinite(value) for value in yaw_samples):
        raise PrecisionStepError("precision-step heading contains a non-finite sample")

    # ``sl`` has no independent directional admission. Its input is the native
    # sequence in reverse time, so assess the re-reversed trace with ``sr``'s
    # direction gates. This preserves the complete source motion, including a
    # legitimate native excursion beyond its eventual repeat-offset endpoint.
    canonical_samples = lateral_forward_samples
    if role_code == "sl":
        reversed_samples = tuple(reversed(lateral_forward_samples))
        lateral_origin, forward_origin = reversed_samples[0]
        canonical_samples = tuple(
            (lateral - lateral_origin, forward - forward_origin)
            for lateral, forward in reversed_samples
        )
    canonical_yaw = yaw_samples if role_code == "sr" else tuple(reversed(yaw_samples))
    confinement = precision_confinement_evidence("sr", canonical_samples, canonical_yaw, rig_scale, limits)
    origin_lateral, _ = lateral_forward_samples[0]
    lateral = tuple(sample[0] - origin_lateral for sample in lateral_forward_samples)
    maximum_excursion = float(str(confinement["signed_intended_lateral_excursion"]))
    wrong_sign_excursion = float(str(confinement["reverse_lateral_excursion"]))
    forward_excursion = float(str(confinement["fore_aft_range"]))
    yaw_excursion = float(str(confinement["yaw_range_radians"]))

    admission = {
        "minimum_lateral_excursion": limits.minimum_lateral_excursion_scale * rig_scale,
        "maximum_lateral_excursion": limits.maximum_lateral_excursion_scale * rig_scale,
        "maximum_wrong_sign_excursion": limits.maximum_wrong_sign_excursion_scale * rig_scale,
        "maximum_forward_excursion": limits.maximum_forward_excursion_scale * rig_scale,
        "maximum_source_yaw_excursion_radians": limits.maximum_source_yaw_excursion_radians,
    }
    failures: dict[str, dict[str, float]] = {}
    checks = {
        "lateral_excursion_minimum": (maximum_excursion, admission["minimum_lateral_excursion"], "minimum"),
        "lateral_excursion_maximum": (maximum_excursion, admission["maximum_lateral_excursion"], "maximum"),
        "wrong_sign_excursion": (wrong_sign_excursion, admission["maximum_wrong_sign_excursion"], "maximum"),
        "forward_excursion": (forward_excursion, admission["maximum_forward_excursion"], "maximum"),
        "source_yaw_excursion": (yaw_excursion, admission["maximum_source_yaw_excursion_radians"], "maximum"),
    }
    for name, (value, threshold, kind) in checks.items():
        if (kind == "minimum" and value < threshold) or (kind == "maximum" and value > threshold):
            failures[name] = {"value": value, "threshold": threshold}

    confinement_failures = confinement["failures"]
    assert isinstance(confinement_failures, dict)
    failures.update(confinement_failures)
    period = len(lateral) - 1
    corrected = lateral
    if failures:
        raise PrecisionStepError(f"unsafe precision-step candidate: {failures}")

    evidence: dict[str, object] = {
        "algorithm": PRECISION_STEP_NORMALISER_VERSION,
        "role_family": "precision_lateral",
        "semantic_source_role": "sr",
        "output_role": role_code,
        "derivation": "native" if role_code == "sr" else "temporal_reversal",
        "native_source_role": "sr",
        "anatomy_mapping": "same LeftFoot and RightFoot anatomy; no spatial reflection or bone swapping",
        "source_cycle_samples": len(lateral),
        "loop_period_samples": period,
        "source_lateral_excursion": maximum_excursion,
        "source_wrong_sign_excursion": wrong_sign_excursion,
        "source_forward_excursion": forward_excursion,
        "source_yaw_excursion_radians": yaw_excursion,
        "declared_lateral_displacement_per_cycle": corrected[-1],
        "maximum_root_correction": 0.0,
        "maximum_root_correction_velocity": 0.0,
        "maximum_root_correction_acceleration": 0.0,
        "correction_function": "exact Root-rest lateral projection; no lateral pacing deformation",
        "source_pacing_preserved": True,
        "visible_pose_retimed": False,
        "root_endpoint_lateral": corrected[-1],
        "root_forward_samples": [0.0] * len(corrected),
        "root_vertical_samples": [0.0] * len(corrected),
        "root_yaw_samples": [0.0] * len(corrected),
        "root_repeat_policy": "REPEAT_OFFSET for declared lateral correction displacement",
        "cumulative_unintended_root_drift_per_cycle": 0.0,
        "admission_bounds": admission,
        "confinement": confinement,
        "correction_samples": [0.0] * len(corrected),
        "normalised_lateral_samples": list(corrected),
    }
    return PrecisionStepNormalisation(corrected, evidence)
