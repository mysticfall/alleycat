"""Bounded, deterministic two-ended construction of non-confined loops.

This module contains the Blender-independent admission and ranking contract.
Raw source recurrence is deliberately not an eligibility requirement: the
measured values describe the repair which would be required after role
derivation.  The Blender adapter in :mod:`materialise` performs the equivalent
operation on evaluated hierarchy transforms.
"""
from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Any, Iterable, Mapping, Sequence, cast


REPAIR_CONTRACT = "symmetric_two_ended_hierarchy_pose_repair_v1"
REPAIR_PROVENANCE_CONTRACT = "anim002_selected_loop_repair_provenance_v2"
REPAIR_DIAGNOSTIC_CONTRACT = "read_only_loop_repair_profile_comparison_v1"
REPAIR_BLEND_PROFILES = ("quadratic", "linear", "smoothstep")
HIERARCHY_CADENCE_CONTRACT = "evaluated_hierarchy_native_cadence_v1"


@dataclass(frozen=True)
class LoopRepairLimits:
    """Physical repair budgets, expressed relative to anatomical scale."""

    endpoint_translation_scale: float = .15
    endpoint_rotation_radians: float = .20
    linear_derivative_scale: float = .10
    angular_derivative_radians: float = .20
    correction_curvature_scale: float = .12
    angular_correction_curvature_radians: float = .24
    minimum_window_frames: int = 4
    preferred_window_frames: int = 6


DEFAULT_LOOP_REPAIR_LIMITS = LoopRepairLimits()


def repair_thresholds(character_scale: float, limits: LoopRepairLimits = DEFAULT_LOOP_REPAIR_LIMITS) -> dict[str, float]:
    if not math.isfinite(character_scale) or character_scale <= 1e-12:
        raise ValueError("loop repair requires a valid anatomical character scale")
    return {
        "endpoint_translation": limits.endpoint_translation_scale * character_scale,
        "endpoint_rotation_rad": limits.endpoint_rotation_radians,
        "linear_derivative_mismatch": limits.linear_derivative_scale * character_scale,
        "angular_derivative_mismatch_rad": limits.angular_derivative_radians,
        "correction_curvature": limits.correction_curvature_scale * character_scale,
        "angular_correction_curvature_rad": limits.angular_correction_curvature_radians,
    }


def evaluated_hierarchy_cadence(
    rows: Sequence[Mapping[str, Sequence[float]]],
    character_scale: float,
    fps: float,
) -> dict[str, object]:
    """Measure seven ordered native-cadence samples around a loop boundary."""
    if len(rows) != 7:
        raise ValueError("hierarchy cadence requires offsets -3 through +3")
    if not math.isfinite(character_scale) or character_scale <= 1e-12:
        raise ValueError("hierarchy cadence requires a valid anatomical scale")
    if not math.isfinite(fps) or fps <= 0.0:
        raise ValueError("hierarchy cadence requires a positive frame rate")
    names = set(rows[0])
    if not names or any(set(row) != names for row in rows):
        raise ValueError("hierarchy cadence rows must contain identical landmarks")

    def vector(values: Sequence[float]) -> tuple[float, float, float]:
        if len(values) != 3 or any(not math.isfinite(float(value)) for value in values):
            raise ValueError("hierarchy cadence contains an invalid landmark position")
        return tuple(float(value) for value in values)  # type: ignore[return-value]

    def subtract(first: Sequence[float], second: Sequence[float]) -> tuple[float, float, float]:
        return tuple(float(second[index]) - float(first[index]) for index in range(3))  # type: ignore[return-value]

    def length(values: Sequence[float]) -> float:
        return math.sqrt(sum(float(value) ** 2 for value in values))

    per_landmark = {}
    for name in sorted(names):
        positions = [vector(row[name]) for row in rows]
        steps = [subtract(positions[index], positions[index + 1]) for index in range(6)]
        distances = [length(step) / character_scale for step in steps]
        velocities = [tuple(value * fps / character_scale for value in step) for step in steps]
        accelerations = [subtract(velocities[index], velocities[index + 1]) for index in range(5)]
        jerks = [subtract(accelerations[index], accelerations[index + 1]) for index in range(4)]
        reversals = [
            index for index in range(5)
            if length(velocities[index]) > 1e-9
            and length(velocities[index + 1]) > 1e-9
            and sum(velocities[index][axis] * velocities[index + 1][axis] for axis in range(3)) < 0.0
        ]
        per_landmark[name] = {
            "step_distances_scale": distances,
            "central_step_imbalance_scale": abs(distances[2] - distances[3]),
            "central_acceleration_scale_per_second2": length(accelerations[2]) * fps,
            "maximum_acceleration_scale_per_second2": max(length(value) * fps for value in accelerations),
            "maximum_jerk_scale_per_second3": max(length(value) * fps * fps for value in jerks),
            "direction_reversal_step_indices": reversals,
        }
    return {
        "contract": HIERARCHY_CADENCE_CONTRACT,
        "coordinate_space": "generated Root-relative evaluated hierarchy",
        "sample_offsets": [-3, -2, -1, 0, 1, 2, 3],
        "fps": float(fps),
        "normalisation": "anatomical character scale and native frame rate",
        "per_landmark": per_landmark,
        "rank_key": list(hierarchy_cadence_rank_key({"per_landmark": per_landmark})),
    }


def hierarchy_cadence_rank_key(evidence: Mapping[str, object]) -> tuple[float, ...]:
    """Rank repaired output by direct boundary cadence without fitted limits."""
    rows = evidence.get("per_landmark")
    if not isinstance(rows, Mapping) or not rows:
        raise ValueError("hierarchy cadence evidence is incomplete")
    landmarks = [cast(Mapping[str, float], row) for row in rows.values() if isinstance(row, Mapping)]
    if len(landmarks) != len(rows):
        raise ValueError("hierarchy cadence landmark evidence is incomplete")
    return (
        max(row["central_step_imbalance_scale"] for row in landmarks),
        max(row["central_acceleration_scale_per_second2"] for row in landmarks),
        max(row["maximum_acceleration_scale_per_second2"] for row in landmarks),
        max(row["maximum_jerk_scale_per_second3"] for row in landmarks),
    )


def symmetric_window(sample_count: int, limits: LoopRepairLimits = DEFAULT_LOOP_REPAIR_LIMITS) -> tuple[int, int]:
    """Return equal beginning/ending window lengths with a non-empty interior."""
    maximum = (sample_count - 3) // 2
    width = min(limits.preferred_window_frames, maximum)
    if width < limits.minimum_window_frames:
        raise ValueError(
            f"loop has {sample_count} samples; two {limits.minimum_window_frames}-frame "
            "repair windows and an immutable interior are required"
        )
    return width, width


def assess_repair(
    metrics: Mapping[str, float], character_scale: float, sample_count: int,
    *, foot_reversal: bool = False, foot_spike: bool = False,
    limits: LoopRepairLimits = DEFAULT_LOOP_REPAIR_LIMITS,
    window_frames: int | None = None,
) -> dict[str, object]:
    """Assess and score required repair cost without requiring raw closure.

    ``window_frames`` must name the symmetric support actually used by the
    repair when it differs from the preferred default, so the reported
    ``windows`` and ``immutable_interior`` describe the performed repair
    rather than a hypothetical default-width one.
    """
    thresholds = repair_thresholds(character_scale, limits)
    missing = set(thresholds) - set(metrics)
    if missing:
        raise ValueError(f"loop repair metrics are missing {sorted(missing)}")
    if window_frames is None:
        beginning, ending = symmetric_window(sample_count, limits)
    else:
        beginning = ending = int(window_frames)
        if (
            beginning < limits.minimum_window_frames
            or sample_count < 2 * beginning + 3
        ):
            raise ValueError(
                f"reported repair windows ({beginning} frames) are not bounded "
                f"symmetric supports for {sample_count} samples"
            )
    ratios = {name: float(metrics[name]) / threshold for name, threshold in thresholds.items()}
    failures = {
        name: {"value": float(metrics[name]), "threshold": threshold}
        for name, threshold in thresholds.items() if not math.isfinite(float(metrics[name])) or float(metrics[name]) > threshold
    }
    if foot_reversal:
        failures["foot_contact_reversal"] = {"value": 1.0, "threshold": 0.0}
    if foot_spike:
        failures["foot_contact_spike"] = {"value": 1.0, "threshold": 0.0}
    # Max ratio makes no single unsafe component disappear in an average.  The
    # small mean term deterministically distinguishes similarly bounded repairs.
    cost = max(ratios.values()) + sum(ratios.values()) / (100.0 * len(ratios))
    return {
        "accepted": not failures,
        "contract": REPAIR_CONTRACT,
        "metrics": {name: float(metrics[name]) for name in thresholds},
        "thresholds": thresholds,
        "normalised_cost_components": ratios,
        "repair_cost": cost,
        "failures": failures,
        "windows": {"beginning_frames": beginning, "ending_frames": ending},
        "immutable_interior": [beginning, sample_count - ending - 1],
        "foot_risk": {"reversal": foot_reversal, "spike": foot_spike},
    }


def validate_durable_repair_windows(
    opening: Sequence[int], immutable_interior: Sequence[int], closing: Sequence[int],
    *, sample_count: int, opening_width: int, closing_width: int, first_frame: int = 0,
) -> None:
    """Reject provenance that describes a default support instead of the repair used.

    Durable frame windows are inclusive.  Keeping this independent of Blender
    lets both source repair and generated-action publication guard adaptive
    repairs without re-deriving a hypothetical preferred-width interior.
    """
    expected = (
        (first_frame, first_frame + opening_width - 1),
        (first_frame + opening_width, first_frame + sample_count - closing_width - 1),
        (first_frame + sample_count - closing_width, first_frame + sample_count - 1),
    )
    observed = (tuple(opening), tuple(immutable_interior), tuple(closing))
    if observed != expected:
        raise ValueError(
            "durable repair windows do not describe the selected repair support: "
            f"expected {expected}, observed {observed}"
        )
    if not (opening[1] < immutable_interior[0] <= immutable_interior[1] < closing[0]):
        raise ValueError("durable repair windows do not enclose a strict immutable interior")


def validate_loop_repair_provenance(
    evidence: Mapping[str, Any], *, conceptual_sample_count: int,
) -> dict[str, Any]:
    """Fail closed unless selected repair and runtime sample semantics are complete."""
    required = {
        "contract", "algorithm", "blend_profile", "opening_support", "closing_support",
        "immutable_interior", "repair_cost", "selection", "transform_derivation",
        "sample_semantics",
    }
    if not isinstance(evidence, Mapping) or set(evidence) != required:
        raise ValueError("loop-repair provenance fields are stale or incomplete")
    if evidence.get("contract") != REPAIR_PROVENANCE_CONTRACT or evidence.get("algorithm") != REPAIR_CONTRACT:
        raise ValueError("loop-repair provenance contract is unsupported")
    if evidence.get("blend_profile") not in REPAIR_BLEND_PROFILES:
        raise ValueError("loop-repair provenance has an unknown selected blend profile")
    opening, closing = evidence.get("opening_support"), evidence.get("closing_support")
    interior = evidence.get("immutable_interior")
    if not isinstance(opening, Mapping) or set(opening) != {"window", "width"} or not isinstance(closing, Mapping) or set(closing) != {"window", "width"}:
        raise ValueError("loop-repair support provenance is incomplete")
    for support in (opening, closing):
        if (
            not isinstance(support["window"], list) or len(support["window"]) != 2
            or isinstance(support["width"], bool) or not isinstance(support["width"], int)
        ):
            raise ValueError("loop-repair support provenance is invalid")
    if not isinstance(interior, list) or len(interior) != 2:
        raise ValueError("loop-repair immutable interior is invalid")
    validate_durable_repair_windows(
        opening["window"], interior, closing["window"],
        sample_count=conceptual_sample_count,
        opening_width=opening["width"], closing_width=closing["width"],
    )
    semantics = evidence.get("sample_semantics")
    if (
        not isinstance(semantics, Mapping)
        or set(semantics) != {
            "conceptual_sample_count", "unique_runtime_sample_count", "duplicate_endpoint",
            "conceptual_boundary_sample", "runtime_interval",
        }
        or semantics.get("conceptual_sample_count") != conceptual_sample_count
        or semantics.get("unique_runtime_sample_count") != conceptual_sample_count - 1
        or semantics.get("duplicate_endpoint") is not True
        or semantics.get("conceptual_boundary_sample") != conceptual_sample_count - 1
        or semantics.get("runtime_interval") != [0, conceptual_sample_count - 2]
    ):
        raise ValueError("loop-repair duplicate-endpoint/runtime-sample semantics are stale")
    derivation = evidence.get("transform_derivation")
    if not isinstance(derivation, Mapping) or set(derivation) != {
        "source_range", "frame_mapping", "role_derivation", "temporal_order",
        "anatomical_mapping", "evaluated_space", "repaired_domain", "root_handling",
    }:
        raise ValueError("loop-repair coordinate/transform derivation is incomplete")
    mapping = derivation.get("frame_mapping")
    source_range = derivation.get("source_range")
    if (
        not isinstance(source_range, list) or len(source_range) != 2
        or not isinstance(mapping, list) or len(mapping) != conceptual_sample_count
        or any(
            not isinstance(row, Mapping) or set(row) != {"source_frame", "output_frame"}
            for row in mapping
        )
    ):
        raise ValueError("loop-repair frame derivation is incomplete")
    selection = evidence.get("selection")
    if not isinstance(selection, Mapping) or set(selection) != {"contract", "selected_rank_key"} or not isinstance(selection.get("selected_rank_key"), list):
        raise ValueError("loop-repair selected-option provenance is incomplete")
    cost = evidence.get("repair_cost")
    if isinstance(cost, bool) or not isinstance(cost, (int, float)) or not math.isfinite(cost) or cost < 0.0:
        raise ValueError("loop-repair cost is invalid")
    return dict(evidence)


def correction_asymmetry_evidence(
    opening_value: float,
    closing_value: float,
    opening_attribution: Mapping[str, Any] | None = None,
    closing_attribution: Mapping[str, Any] | None = None,
) -> dict[str, object]:
    """Describe opening/closing imbalance without making an admission decision."""
    opening, closing = float(opening_value), float(closing_value)
    maximum, minimum = max(opening, closing), min(opening, closing)
    return {
        "opening_maximum": opening,
        "closing_maximum": closing,
        "absolute_difference": abs(closing - opening),
        "normalised_imbalance": abs(closing - opening) / maximum if maximum > 1e-12 else 0.0,
        "maximum_to_minimum_ratio": maximum / minimum if minimum > 1e-12 else None,
        "dominant_window": "closing" if closing > opening else "opening" if opening > closing else "balanced",
        "opening_attribution": dict(opening_attribution or {}),
        "closing_attribution": dict(closing_attribution or {}),
    }


def repair_profile_weight(distance: int, window_frames: int, profile: str) -> float:
    remaining = 1.0 - distance / window_frames
    if profile == "quadratic":
        return remaining * remaining
    if profile == "linear":
        return remaining
    if profile == "smoothstep":
        return remaining * remaining * (3.0 - 2.0 * remaining)
    raise ValueError(f"unknown loop repair blend profile: {profile}")


def _repair_scalar_samples(
    samples: Sequence[float], window_frames: int, profile: str,
) -> tuple[tuple[float, ...], dict[str, object]]:
    """Reference scalar form of the two-ended pose/first-derivative repair.

    It is used by pure regression tests and documents the transform adapter's
    topology.  The opening value is copied to the endpoint.  The average of the
    two raw boundary derivatives becomes the common periodic derivative, and
    correction falls to zero before the immutable interior.
    """
    if len(samples) < 2 * window_frames + 3 or window_frames < 2:
        raise ValueError("symmetric repair windows overlap the immutable interior")
    if profile not in REPAIR_BLEND_PROFILES:
        raise ValueError(f"unknown loop repair blend profile: {profile}")
    original = tuple(float(value) for value in samples)
    result = list(original)
    derivative = (
        ((original[1] - original[0]) + (original[-1] - original[-2])) * .5
        + (original[-1] - original[0]) / (2.0 * window_frames)
    )
    acceleration = (
        (original[2] - 2.0 * original[1] + original[0])
        + (original[-1] - 2.0 * original[-2] + original[-3])
    ) * .5
    targets = {
        0: original[0],
        1: original[0] + derivative,
        2: original[0] + 2.0 * derivative + acceleration,
        len(result) - 3: original[0] - 2.0 * derivative + acceleration,
        len(result) - 2: original[0] - derivative,
        len(result) - 1: original[0],
    }
    for index, target in targets.items():
        result[index] = target
    # Blend neighbour corrections to zero at the outside edge of each declared
    # window.  The explicit neighbour values above retain exact C1 closure.
    for distance in range(3, window_frames):
        weight = repair_profile_weight(distance, window_frames, profile)
        opening_correction = targets[2] - original[2]
        closing_correction = targets[len(result) - 3] - original[-3]
        result[distance] = original[distance] + opening_correction * weight
        result[-1 - distance] = original[-1 - distance] + closing_correction * weight
    changed_beginning = any(result[index] != original[index] for index in range(window_frames))
    changed_ending = any(result[-1 - index] != original[-1 - index] for index in range(window_frames))
    interior = range(window_frames, len(result) - window_frames)
    maximum_interior_change = max((abs(result[index] - original[index]) for index in interior), default=0.0)
    corrections = [result[index] - original[index] for index in range(len(result))]
    correction_curvatures = [
        corrections[index + 1] - 2.0 * corrections[index] + corrections[index - 1]
        for index in range(1, len(result) - 1)
    ]

    def maximum_record(indices: Iterable[int], values: Sequence[float]) -> dict[str, Any]:
        rows = tuple(indices)
        index = max(rows, key=lambda item: (abs(values[item]), -item))
        return {"value": abs(values[index]), "signed_value": values[index], "sample_index": index}

    opening_correction = maximum_record(range(window_frames), corrections)
    closing_correction = maximum_record(range(len(result) - window_frames, len(result)), corrections)
    opening_curvature = maximum_record(range(window_frames - 1), correction_curvatures)
    closing_curvature = maximum_record(
        range(len(correction_curvatures) - window_frames + 1, len(correction_curvatures)),
        correction_curvatures,
    )
    # Curvature sample i describes the correction centred at original sample i+1.
    opening_curvature["sample_index"] = int(opening_curvature["sample_index"]) + 1
    closing_curvature["sample_index"] = int(closing_curvature["sample_index"]) + 1
    evidence = {
        "contract": REPAIR_CONTRACT,
        "blend_profile": profile,
        "windows": {"beginning": [0, window_frames - 1], "ending": [len(result) - window_frames, len(result) - 1]},
        "both_windows_modified": changed_beginning and changed_ending,
        "maximum_immutable_interior_change": maximum_interior_change,
        "endpoint_error": abs(result[-1] - result[0]),
        "first_derivative_error": abs((result[1] - result[0]) - (result[-1] - result[-2])),
        "maximum_correction": max(abs(value) for value in corrections),
        "maximum_correction_curvature": max((abs(value) for value in correction_curvatures), default=0.0),
        "correction_asymmetry": {
            "magnitude": correction_asymmetry_evidence(
                float(opening_correction["value"]), float(closing_correction["value"]),
                opening_correction, closing_correction,
            ),
            "curvature": correction_asymmetry_evidence(
                float(opening_curvature["value"]), float(closing_curvature["value"]),
                opening_curvature, closing_curvature,
            ),
        },
    }
    if not evidence["both_windows_modified"] or maximum_interior_change != 0.0:
        raise ValueError("two-ended repair topology invariant failed")
    return tuple(result), evidence


def repair_scalar_samples(
    samples: Sequence[float], window_frames: int, profile: str = "quadratic",
) -> tuple[tuple[float, ...], dict[str, object]]:
    """Apply a production repair profile to scalar reference samples."""
    return _repair_scalar_samples(samples, window_frames, profile)


def diagnose_scalar_repair_options(
    source_windows: Mapping[tuple[int, int], Sequence[float]],
    window_frames: Iterable[int],
    profiles: Iterable[str] = REPAIR_BLEND_PROFILES,
) -> dict[str, object]:
    """Compare recurrence windows and repair profiles without selecting or publishing."""
    widths = tuple(sorted(set(int(value) for value in window_frames)))
    profile_names = tuple(sorted(set(profiles)))
    if not widths:
        raise ValueError("loop repair diagnostics require at least one window width")
    if not profile_names or any(profile not in REPAIR_BLEND_PROFILES for profile in profile_names):
        raise ValueError("loop repair diagnostics contain an unknown blend profile")
    comparisons = []
    for source_range, samples in sorted(source_windows.items()):
        for width in widths:
            for profile in profile_names:
                try:
                    _, evidence = _repair_scalar_samples(samples, width, profile)
                except ValueError as error:
                    comparisons.append({
                        "source_range": list(source_range), "window_frames": width,
                        "blend_profile": profile, "evaluated": False, "reason": str(error),
                    })
                    continue
                comparisons.append({
                    "source_range": list(source_range), "window_frames": width,
                    "blend_profile": profile, "evaluated": True,
                    "maximum_correction": evidence["maximum_correction"],
                    "maximum_correction_curvature": evidence["maximum_correction_curvature"],
                    "correction_asymmetry": evidence["correction_asymmetry"],
                    "endpoint_error": evidence["endpoint_error"],
                    "first_derivative_error": evidence["first_derivative_error"],
                })
    return {
        "contract": REPAIR_DIAGNOSTIC_CONTRACT,
        "read_only": True,
        "changes_admission_or_selection": False,
        "comparisons": comparisons,
    }


def adaptive_repair_specs(
    sample_count: int,
    profiles: Iterable[str] = REPAIR_BLEND_PROFILES,
    limits: LoopRepairLimits = DEFAULT_LOOP_REPAIR_LIMITS,
) -> tuple[tuple[int, str], ...]:
    """Return bounded, duration-scaled supports independent of input order."""
    maximum = min((sample_count - 3) // 2, max(limits.minimum_window_frames, math.floor(sample_count * .35)))
    widths = {
        limits.minimum_window_frames,
        limits.preferred_window_frames,
        *(round(sample_count * fraction) for fraction in (.20, .25, .30, .35)),
    }
    profile_names = sorted(set(profiles))
    if any(profile not in REPAIR_BLEND_PROFILES for profile in profile_names):
        raise ValueError("adaptive loop repair contains an unknown blend profile")
    return tuple(
        (width, profile)
        for width in sorted(value for value in widths if 2 <= value <= maximum)
        for profile in profile_names
    )


def repair_naturalness_key(evidence: Mapping[str, object]) -> tuple[float | int | str, ...]:
    """Rank safe repairs by output smoothness, magnitude, and two-ended balance."""
    metrics = evidence["metrics"]
    thresholds = evidence["thresholds"]
    asymmetry = evidence["opening_closing_correction_asymmetry"]
    windows = evidence["window_ranges_zero_based"]
    if not all(isinstance(value, Mapping) for value in (metrics, thresholds, asymmetry, windows)):
        raise ValueError("repair naturalness evidence is incomplete")
    metric_rows = cast(Mapping[str, float], metrics)
    threshold_rows = cast(Mapping[str, float], thresholds)
    asymmetry_rows = cast(Mapping[str, Mapping[str, float]], asymmetry)
    window_rows = cast(Mapping[str, Sequence[int]], windows)
    smoothness = max(
        metric_rows["correction_curvature"] / threshold_rows["correction_curvature"],
        metric_rows["angular_correction_curvature_rad"] / threshold_rows["angular_correction_curvature_rad"],
    )
    correction_safety = evidence.get("actual_correction_safety")
    if not isinstance(correction_safety, Mapping):
        raise ValueError("repair naturalness evidence lacks actual correction safety")
    magnitude = max(float(value) for value in cast(Mapping[str, float], correction_safety["normalised_components"]).values())
    balance = max(float(row["normalised_imbalance"]) for row in asymmetry_rows.values())
    support = int(window_rows["beginning"][1]) - int(window_rows["beginning"][0]) + 1
    cadence = evidence.get("evaluated_hierarchy_cadence")
    cadence_key = hierarchy_cadence_rank_key(cast(Mapping[str, object], cadence)) if isinstance(cadence, Mapping) else ()
    return smoothness, magnitude, balance, *cadence_key, support, str(evidence["blend_profile"])


def assess_foot_lift_apex_preservation(
    before: Sequence[float], after: Sequence[float], character_scale: float,
    maximum_height_delta: float, repair_evidence: Mapping[str, object],
) -> dict[str, object]:
    """Preserve an apex exactly unless periodic boundary repair owns its phase.

    A maximum selected from discrete evaluated samples may move inside a repaired
    boundary window without moving the semantic lift landmark.  Interior maxima
    remain exact: the repair contract never owns those samples.
    """
    if len(before) != len(after) or len(before) < 2:
        raise ValueError("foot-lift apex preservation requires matching evaluated samples")
    if not math.isfinite(character_scale) or character_scale <= 1e-12:
        raise ValueError("foot-lift apex preservation requires a valid anatomical character scale")
    if not math.isfinite(maximum_height_delta) or maximum_height_delta < 0.0:
        raise ValueError("foot-lift apex preservation requires a valid height tolerance")
    windows = repair_evidence.get("window_ranges_zero_based")
    both_windows_modified = repair_evidence.get("both_windows_modified")
    if not isinstance(windows, Mapping) or not isinstance(both_windows_modified, bool):
        raise ValueError("foot-lift apex preservation requires periodic repair evidence")

    def window_for(index: int) -> str | None:
        for name in ("beginning", "ending"):
            bounds = windows.get(name)
            if (
                isinstance(bounds, Sequence) and len(bounds) == 2
                and all(isinstance(value, int) for value in bounds)
                and bounds[0] <= index <= bounds[1]
            ):
                return name
        return None

    before_index = max(range(len(before)), key=before.__getitem__)
    after_index = max(range(len(after)), key=after.__getitem__)
    before_window, after_window = window_for(before_index), window_for(after_index)
    phase_delta_samples = abs(after_index - before_index)
    local_duration = len(before) - 1
    height_delta = abs(before[before_index] - after[after_index])
    equivalent_boundary_extremum = (
        both_windows_modified
        and before_window is not None
        and after_window is not None
        and (
            before_window == after_window
            or {before_window, after_window} == {"beginning", "ending"}
        )
        and height_delta <= maximum_height_delta
    )
    accepted = before_index == after_index or equivalent_boundary_extremum
    return {
        "accepted": accepted,
        "source_apex_sample": before_index,
        "repaired_apex_sample": after_index,
        "source_phase": before_index / local_duration,
        "repaired_phase": after_index / local_duration,
        "phase_delta_samples": phase_delta_samples,
        "phase_delta_normalised_duration": phase_delta_samples / local_duration,
        "source_phase_landmark": before_window or "immutable_interior",
        "repaired_phase_landmark": after_window or "immutable_interior",
        "apex_height_before": before[before_index],
        "apex_height_after": after[after_index],
        "apex_height_delta": height_delta,
        "apex_height_tolerance": maximum_height_delta,
        "periodic_repair_both_windows_modified": both_windows_modified,
        "equivalent_boundary_extremum": equivalent_boundary_extremum,
        "reason": (
            "same evaluated apex sample"
            if before_index == after_index else
            "equivalent apex remains in the periodic boundary repair landmark"
            if equivalent_boundary_extremum else
            "apex moved outside its authorised periodic repair landmark or changed height"
        ),
    }


def repair_sort_key(action_name: str, start: int, end: int, assessment: Mapping[str, object]) -> tuple[float, str, int, int]:
    """Lowest bounded repair cost wins; source identity is only a tie-break."""
    cost = assessment["repair_cost"]
    if not isinstance(cost, (int, float)):
        raise ValueError("loop repair assessment has no numeric repair cost")
    return float(cost), action_name, start, end
