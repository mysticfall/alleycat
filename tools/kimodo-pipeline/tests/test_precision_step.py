"""Source-sequence-agnostic precision-step confinement contracts."""
from __future__ import annotations

import math

import pytest

from kimodo_pipeline.precision_step import (
    PrecisionStepError,
    PrecisionStepLimits,
    assess_precision_foot_order,
    assess_precision_semantic_events,
    normalise_precision_step,
    precision_confinement_evidence,
    select_precision_candidate,
)
from kimodo_pipeline.leg_silhouette import assess_precision_leg_silhouette


def _step(scale: float = 1.0, mirror: bool = False) -> tuple[list[tuple[float, float]], list[float]]:
    samples = []
    for index in range(41):
        phase = index / 40.0
        lateral = -0.28 * scale * math.sin(math.pi * phase) - 0.12 * scale * phase
        if mirror:
            lateral = -lateral
        samples.append((lateral, 0.018 * scale * math.sin(2.0 * math.pi * phase)))
    return samples, [0.025 * math.sin(2.0 * math.pi * index / 40.0) for index in range(41)]


def test_precision_step_is_scale_shift_name_and_order_independent() -> None:
    original, yaw = _step()
    scaled, scaled_yaw = _step(2.7)
    first = normalise_precision_step("sr", original, yaw, 1.0)
    second = normalise_precision_step("sr", scaled, scaled_yaw, 2.7)
    labelled = {121 + index: sample for index, sample in enumerate(original)}
    shifted = {1401 + index: sample for index, sample in enumerate(original)}
    shifted_result = normalise_precision_step(
        "sr", list(shifted.values()), yaw, 1.0,
    )
    # Action identity, absolute frame labels, and source-list ordering are not
    # inputs. Normalised trajectories therefore agree after spatial scaling.
    assert [value / 2.7 for value in second.lateral_samples] == pytest.approx(first.lateral_samples)
    assert list(labelled.values()) == list(shifted.values())
    assert shifted_result.lateral_samples == pytest.approx(first.lateral_samples)
    assert second.evidence["maximum_root_correction"] == 0.0
    assert first.lateral_samples[-1] == pytest.approx(original[-1][0])
    assert first.evidence["cumulative_unintended_root_drift_per_cycle"] == 0.0


def test_semantic_event_admission_requires_one_complete_non_crossed_side_step() -> None:
    def assess(name: str, active: list[int], *, crossed: bool = False) -> dict[str, object]:
        samples = []
        lateral = []
        for frame in range(9):
            moving = frame in active
            samples.append((100 + frame, -0.03 if moving else 0.0, 0.03 if moving else 0.0, 0.0, 0.0))
            lateral.append(-0.05 * sum(item <= frame for item in active))
        result = assess_precision_semantic_events(samples, lateral, 1.0)
        # Names are deliberately not assessor inputs; crossing remains a
        # separate evaluated anatomical gate.
        result["fixture_name"] = name
        result["crossed"] = crossed
        return result

    one = assess("renamed-anything", [2, 3])
    two = assess("also-renamed", [1, 2, 6, 7])
    assert one["accepted"] and one["complete_event_count"] == 1
    assert not two["accepted"] and two["complete_event_count"] == 2
    crossed = assess_precision_foot_order(
        "sr", [(100, .2, -.2), (101, -.2, .2)], 1.0,
        stage="immutable_pre_repair", basis_evidence={},
    )
    assert not crossed["accepted"]


def test_calibrated_slide_budget_is_a_normal_candidate_gate_not_a_rank_name_exception() -> None:
    baseline_slide, margin = .041, PrecisionStepLimits().calibration_sliding_margin_scale
    maximum = baseline_slide + margin
    assert .045 <= maximum
    assert .052 > maximum


def test_sl_is_the_same_anatomy_temporal_reversal_of_sr() -> None:
    right, yaw = _step()
    sr = normalise_precision_step("sr", right, yaw, 1.0)
    reversed_right = list(reversed(right))
    sl = normalise_precision_step("sl", reversed_right, list(reversed(yaw)), 1.0)
    assert sl.lateral_samples == pytest.approx([
        value - sr.lateral_samples[-1] for value in reversed(sr.lateral_samples)
    ])
    assert sl.evidence["derivation"] == "temporal_reversal"
    assert sl.evidence["anatomy_mapping"] == "same LeftFoot and RightFoot anatomy; no spatial reflection or bone swapping"
    sl_confinement, sr_confinement = sl.evidence["confinement"], sr.evidence["confinement"]
    assert isinstance(sl_confinement, dict) and isinstance(sr_confinement, dict)
    assert {key: value for key, value in sl_confinement.items() if key != "role"} == {
        key: value for key, value in sr_confinement.items() if key != "role"
    }


def test_spatially_reflected_sl_trace_is_not_accepted_as_temporal_reversal() -> None:
    right, yaw = _step()
    sr = normalise_precision_step("sr", right, yaw, 1.0)
    expected = [value - sr.lateral_samples[-1] for value in reversed(sr.lateral_samples)]
    spatial_reflection = [-value for value in sr.lateral_samples]
    error = max(abs(actual - wanted) for actual, wanted in zip(spatial_reflection, expected))
    assert error > 0.08


@pytest.mark.parametrize(
    ("corruption", "message"),
    [
        ([(0.25 * index / 40.0, 0.0) for index in range(41)], "wrong_sign_excursion"),
        ([(-0.30 * index / 40.0, 0.40 * math.sin(math.pi * index / 40.0)) for index in range(41)], "forward_excursion"),
        ([(-1.2 * math.sin(math.pi * index / 40.0), 0.0) for index in range(41)], "lateral_excursion_maximum"),
    ],
)
def test_unsafe_precision_or_curved_slices_are_rejected(corruption, message: str) -> None:
    with pytest.raises(PrecisionStepError, match=message):
        normalise_precision_step("sr", corruption, [0.0] * 41, 1.0)


def test_accidental_yaw_is_rejected() -> None:
    samples, _ = _step()
    with pytest.raises(PrecisionStepError, match="source_yaw_excursion"):
        normalise_precision_step("sr", samples, [0.5 * index / 40.0 for index in range(41)], 1.0)


def test_double_application_is_numerically_idempotent() -> None:
    samples, yaw = _step()
    first = normalise_precision_step("sr", samples, yaw, 1.0)
    second = normalise_precision_step(
        "sr", [(value, samples[index][1]) for index, value in enumerate(first.lateral_samples)], yaw, 1.0,
    )
    assert second.lateral_samples == pytest.approx(first.lateral_samples, abs=1e-12)
    assert second.evidence["maximum_root_correction"] == pytest.approx(0.0, abs=1e-12)


def test_dispatch_ignores_action_names_order_and_ineligible_extra_sequences() -> None:
    best = {
        "phase_compatible": True, "quality_accepted": True, "repair_accepted": True,
        "foot_order_accepted": True,
        "repair_cost": 0.42, "source_window_content_signature": "content-b",
        "action_name_evidence_only": "original_name",
    }
    higher_cost = {**best, "repair_cost": 0.61, "source_window_content_signature": "content-a"}
    unsafe_extra = {**best, "quality_accepted": False, "repair_cost": 0.01, "source_window_content_signature": "extra"}
    assert select_precision_candidate([higher_cost, unsafe_extra, best])["source_window_content_signature"] == "content-b"
    renamed = {**best, "action_name_evidence_only": "renamed_without_semantic_effect"}
    assert select_precision_candidate([renamed, higher_cost])["source_window_content_signature"] == "content-b"
    assert select_precision_candidate([higher_cost, renamed])["source_window_content_signature"] == "content-b"


def test_signed_foot_order_records_worst_source_sample_and_scale_normalised_depth() -> None:
    evidence = assess_precision_foot_order(
        "sr",
        [(101, 0.2, -0.2), (102, -0.021, 0.019), (103, 0.1, -0.1)],
        2.0,
        stage="immutable_pre_repair",
        basis_evidence={"convention": "positive lateral points from RightFoot to LeftFoot"},
    )
    assert evidence["accepted"]
    assert evidence["minimum_signed_separation"] == pytest.approx(-0.04)
    assert evidence["maximum_scale_normalised_crossover_depth"] == pytest.approx(0.02)
    assert evidence["responsible_sample"] == 102
    assert evidence["normalised_tolerance"] == pytest.approx(0.03)


@pytest.mark.parametrize(
    "samples",
    [
        [(1, 0.001, -0.001), (2, 0.0001, -0.0001)],
        [(1, 0.0, 0.0), (2, 0.02, -0.02)],
    ],
)
def test_narrow_or_touching_correctly_ordered_feet_are_admitted(samples) -> None:
    evidence = assess_precision_foot_order(
        "sr", samples, 1.0,
        stage="immutable_pre_repair", basis_evidence={"basis": "test"},
    )
    assert evidence["accepted"]
    assert evidence["minimum_signed_separation"] >= 0.0
    assert evidence["maximum_scale_normalised_crossover_depth"] == 0.0
    assert evidence["rejection_reason"] is None


def test_material_crossover_is_rejected_for_both_same_anatomy_roles() -> None:
    limits = PrecisionStepLimits(maximum_crossover_depth_scale=0.03)
    native = assess_precision_foot_order(
        "sr", [(7, -0.04, 0.04)], 1.0,
        stage="immutable_pre_repair", basis_evidence={}, limits=limits,
    )
    mirrored = assess_precision_foot_order(
        "sl", [(7, -0.04, 0.04)], 1.0,
        stage="repaired_output", basis_evidence={}, limits=limits,
    )
    assert not native["accepted"]
    assert not mirrored["accepted"]
    assert native["maximum_scale_normalised_crossover_depth"] == mirrored["maximum_scale_normalised_crossover_depth"]
    assert "exceeds" in native["rejection_reason"]


def test_precision_candidate_gate_precedes_repair_cost_ranking() -> None:
    crossed = {
        "phase_compatible": True, "foot_order_accepted": False,
        "quality_accepted": True, "repair_accepted": True,
        "repair_cost": 0.01, "source_window_content_signature": "crossed",
    }
    eligible = {
        **crossed, "foot_order_accepted": True,
        "repair_cost": 0.4, "source_window_content_signature": "eligible",
    }
    assert select_precision_candidate([crossed, eligible]) is eligible


def test_precision_ranking_uses_width_and_confinement_after_repair_cost() -> None:
    base = {
        "phase_compatible": True, "foot_order_accepted": True,
        "quality_accepted": True, "repair_accepted": True, "repair_cost": .4,
        "quality_metrics": {"body_support_offset": .2, "planted_foot_slip": .1, "foot_acceleration": .2, "foot_jerk": .2},
        "confinement": {"reverse_lateral_excursion_scale": .01, "fore_aft_range_scale": .02, "yaw_range_radians": .01, "lateral_path_net_endpoint_ratio": 3.0},
    }
    wide = {**base, "source_window_content_signature": "wide", "width_evidence": {"maximum": 1.11, "median": .9}}
    narrow = {**base, "source_window_content_signature": "narrow", "width_evidence": {"maximum": 1.06, "median": .8}}
    assert select_precision_candidate([wide, narrow]) is narrow


def test_precision_confinement_rejects_reverse_and_contaminated_motion() -> None:
    samples, yaw = _step()
    accepted = precision_confinement_evidence("sr", samples, yaw, 1.0)
    assert accepted["accepted"]
    reverse = [(-.28 * math.sin(math.pi * index / 40.0) + .20 * index / 40.0, 0.0) for index in range(41)]
    contaminated = [(-.28 * math.sin(math.pi * index / 40.0), .35 * math.sin(math.pi * index / 40.0)) for index in range(41)]
    reverse_failures = precision_confinement_evidence("sr", reverse, [0.0] * 41, 1.0)["failures"]
    contaminated_failures = precision_confinement_evidence("sr", contaminated, [0.0] * 41, 1.0)["failures"]
    assert isinstance(reverse_failures, dict) and "reverse_lateral_excursion" in reverse_failures
    assert isinstance(contaminated_failures, dict) and "fore_aft_range" in contaminated_failures


def test_material_inversion_evidence_is_scale_and_frame_origin_invariant() -> None:
    unit = assess_precision_foot_order(
        "sr", [(11, -0.04, 0.04)], 1.0,
        stage="immutable_pre_repair", basis_evidence={},
    )
    scaled_shifted = assess_precision_foot_order(
        "sr", [(901, -0.1, 0.1)], 2.5,
        stage="immutable_pre_repair", basis_evidence={},
    )
    assert not unit["accepted"]
    assert not scaled_shifted["accepted"]
    assert unit["maximum_scale_normalised_crossover_depth"] == pytest.approx(
        scaled_shifted["maximum_scale_normalised_crossover_depth"]
    )
    assert unit["normalised_tolerance"] == scaled_shifted["normalised_tolerance"] == 0.03
    assert unit["responsible_sample"] == 11
    assert scaled_shifted["responsible_sample"] == 901
    assert "exceeds 0.030000000" in unit["rejection_reason"]


def test_leg_silhouette_rejects_crossed_shins_when_feet_remain_ordered() -> None:
    evidence = assess_precision_leg_silhouette(
        "sr",
        [(20, [(0.2, 1.0), (-0.08, 0.5), (0.12, 0.0)], [(-0.2, 1.0), (0.08, 0.5), (-0.12, 0.0)])],
        1.0,
        stage="immutable_pre_repair",
        armature_identity="Kimodo_Source:Kimodo_Source",
        basis_evidence={"basis": "fixture"},
    )
    assert not evidence["accepted"]
    assert evidence["worst_frame"] == 20
    assert evidence["worst_left_segment"] == "hip_to_knee"
    assert evidence["worst_right_segment"] == "hip_to_knee"


def test_leg_silhouette_is_scale_and_rotated_basis_invariant() -> None:
    unit = assess_precision_leg_silhouette(
        "sr", [(4, [(0.3, 1.0), (0.2, 0.5), (0.1, 0.0)], [(-0.3, 1.0), (-0.2, 0.5), (-0.1, 0.0)])],
        1.0, stage="repaired_output", armature_identity="fixture", basis_evidence={},
    )
    scaled = assess_precision_leg_silhouette(
        "sr", [(904, [(0.75, 2.5), (0.5, 1.25), (0.25, 0.0)], [(-0.75, 2.5), (-0.5, 1.25), (-0.25, 0.0)])],
        2.5, stage="repaired_output", armature_identity="rotated-fixture", basis_evidence={"rotation": 1.2},
    )
    assert unit["accepted"] and scaled["accepted"]
    assert unit["maximum_scale_normalised_crossover_depth"] == scaled["maximum_scale_normalised_crossover_depth"] == 0.0
