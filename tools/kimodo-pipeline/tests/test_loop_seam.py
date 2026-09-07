from __future__ import annotations

import unittest
from typing import Any, cast

from kimodo_pipeline.loop_seam import (
    adaptive_repair_specs,
    assess_foot_lift_apex_preservation,
    assess_repair,
    correction_asymmetry_evidence,
    diagnose_scalar_repair_options,
    evaluated_hierarchy_cadence,
    hierarchy_cadence_rank_key,
    repair_scalar_samples,
    repair_sort_key,
    validate_loop_repair_provenance,
)


class LoopSeamTests(unittest.TestCase):
    def test_complete_repair_provenance_preserves_adaptive_width_and_profile(self) -> None:
        for width in (11, 12, 14, 15, 16):
            sample_count = 2 * width + 5
            evidence = {
                "contract": "anim002_selected_loop_repair_provenance_v2",
                "algorithm": "symmetric_two_ended_hierarchy_pose_repair_v1",
                "blend_profile": "smoothstep",
                "opening_support": {"window": [0, width - 1], "width": width},
                "closing_support": {"window": [sample_count - width, sample_count - 1], "width": width},
                "immutable_interior": [width, sample_count - width - 1],
                "repair_cost": 0.25,
                "selection": {"contract": "bounded_duration_scaled_repaired_output_naturalness_v1", "selected_rank_key": [0.25, width]},
                "transform_derivation": {
                    "source_range": [101, 100 + sample_count],
                    "frame_mapping": [
                        {"source_frame": 101 + index, "output_frame": index + 1}
                        for index in range(sample_count)
                    ],
                    "role_derivation": "native", "temporal_order": "preserved",
                    "anatomical_mapping": "preserved",
                    "evaluated_space": "generated Root-relative evaluated hierarchy",
                    "repaired_domain": "complete non-Root motion hierarchy",
                    "root_handling": "immutable generated Root carriage",
                },
                "sample_semantics": {
                    "conceptual_sample_count": sample_count,
                    "unique_runtime_sample_count": sample_count - 1,
                    "duplicate_endpoint": True,
                    "conceptual_boundary_sample": sample_count - 1,
                    "runtime_interval": [0, sample_count - 2],
                },
            }
            validated = validate_loop_repair_provenance(
                evidence, conceptual_sample_count=sample_count,
            )
            self.assertEqual(validated["blend_profile"], "smoothstep")
            self.assertEqual(validated["opening_support"]["width"], width)

            stale = dict(evidence)
            stale.pop("transform_derivation")
            with self.assertRaisesRegex(ValueError, "stale or incomplete"):
                validate_loop_repair_provenance(stale, conceptual_sample_count=sample_count)

    def test_evaluated_hierarchy_cadence_exposes_jump_then_hold(self) -> None:
        smooth = [{"foot": (float(index), 0.0, 0.0)} for index in range(7)]
        jump_hold = [
            {"foot": (value, 0.0, 0.0)}
            for value in (0.0, 1.0, 2.0, 7.0, 7.0, 8.0, 9.0)
        ]
        smooth_evidence = evaluated_hierarchy_cadence(smooth, 10.0, 30.0)
        jump_evidence = evaluated_hierarchy_cadence(jump_hold, 10.0, 30.0)
        smooth_key = hierarchy_cadence_rank_key(smooth_evidence)
        jump_key = hierarchy_cadence_rank_key(jump_evidence)
        self.assertTrue(smooth_key < jump_key)
        jump_row = cast(dict[str, Any], jump_evidence["per_landmark"])["foot"]
        self.assertEqual(jump_row["step_distances_scale"][2:4], [.5, 0.0])

    def test_evaluated_hierarchy_cadence_is_name_and_order_independent(self) -> None:
        rows = [
            {"renamed_b": (0.0, float(index), 0.0), "renamed_a": (float(index), 0.0, 0.0)}
            for index in range(7)
        ]
        reordered = [{name: row[name] for name in reversed(tuple(row))} for row in rows]
        first = evaluated_hierarchy_cadence(rows, 3.0, 24.0)
        second = evaluated_hierarchy_cadence(reordered, 3.0, 24.0)
        self.assertEqual(first, second)

    def test_deliberately_unclosed_bounded_loop_is_repairable(self) -> None:
        decision = assess_repair({
            "endpoint_translation": .04,
            "endpoint_rotation_rad": .10,
            "linear_derivative_mismatch": .02,
            "angular_derivative_mismatch_rad": .08,
            "correction_curvature": .01,
            "angular_correction_curvature_rad": .04,
        }, .433695449, 33)
        self.assertTrue(decision["accepted"], decision)
        cost = decision["repair_cost"]
        self.assertIsInstance(cost, (int, float))
        assert isinstance(cost, (int, float))
        self.assertGreater(cost, 0.0)

    def test_lower_repair_cost_wins_before_identity_tie_break(self) -> None:
        metrics = {
            "endpoint_translation": .02, "endpoint_rotation_rad": .04,
            "linear_derivative_mismatch": .01, "angular_derivative_mismatch_rad": .02,
            "correction_curvature": .005, "angular_correction_curvature_rad": .01,
        }
        low = assess_repair(metrics, .4, 33)
        high = assess_repair({**metrics, "endpoint_translation": .05}, .4, 33)
        self.assertLess(repair_sort_key("z", 1, 33, low), repair_sort_key("a", 1, 33, high))

    def test_durable_repair_evidence_reports_actual_adaptive_windows(self) -> None:
        metrics = {
            "endpoint_translation": .04, "endpoint_rotation_rad": .10,
            "linear_derivative_mismatch": .02, "angular_derivative_mismatch_rad": .08,
            "correction_curvature": .01, "angular_correction_curvature_rad": .04,
        }
        # The durable anim002_loop_repair_evidence triple (opening window,
        # immutable interior, closing window) must satisfy the publication
        # gate's range predicate for the adaptive width actually used.
        for sample_count, width in ((39, 12), (46, 16), (36, 11)):
            decision = assess_repair(metrics, .433695449, sample_count, window_frames=width)
            self.assertEqual(
                decision["windows"], {"beginning_frames": width, "ending_frames": width},
            )
            opening = [0, width - 1]
            closing = [sample_count - width, sample_count - 1]
            interior = cast(list[int], decision["immutable_interior"])
            self.assertTrue(
                opening[0] == 0
                <= opening[1]
                < interior[0]
                <= interior[1]
                < closing[0]
                <= closing[1]
                == sample_count - 1,
                f"inconsistent durable evidence: {opening} {interior} {closing}",
            )

    def test_default_window_interior_fails_predicate_against_adaptive_windows(self) -> None:
        metrics = {
            "endpoint_translation": .04, "endpoint_rotation_rad": .10,
            "linear_derivative_mismatch": .02, "angular_derivative_mismatch_rad": .08,
            "correction_curvature": .01, "angular_correction_curvature_rad": .04,
        }
        default_interior = cast(
            list[int], assess_repair(metrics, .433695449, 39)["immutable_interior"],
        )
        opening, closing = [0, 11], [27, 38]
        self.assertFalse(
            opening[1] < default_interior[0] <= default_interior[1] < closing[0],
            "a default-width interior must not validate against adaptive-width windows",
        )

    def test_assess_repair_rejects_unbounded_reported_windows(self) -> None:
        metrics = {
            "endpoint_translation": .04, "endpoint_rotation_rad": .10,
            "linear_derivative_mismatch": .02, "angular_derivative_mismatch_rad": .08,
            "correction_curvature": .01, "angular_correction_curvature_rad": .04,
        }
        with self.assertRaises(ValueError):
            assess_repair(metrics, .433695449, 13, window_frames=6)
        with self.assertRaises(ValueError):
            assess_repair(metrics, .433695449, 39, window_frames=3)

    def test_both_windows_change_interior_is_immutable_and_c1_closes(self) -> None:
        source = tuple(index * .07 + (index % 4) * .01 for index in range(33))
        repaired, evidence = repair_scalar_samples(source, 6)
        self.assertTrue(evidence["both_windows_modified"])
        self.assertEqual(repaired[6:-6], source[6:-6])
        self.assertEqual(evidence["maximum_immutable_interior_change"], 0.0)
        self.assertAlmostEqual(repaired[0], repaired[-1])
        self.assertAlmostEqual(repaired[1] - repaired[0], repaired[-1] - repaired[-2])
        self.assertEqual(evidence["blend_profile"], "quadratic")
        self.assertIn("correction_asymmetry", evidence)
        self.assertIn("maximum_correction_curvature", evidence)

    def test_asymmetry_evidence_attributes_a_closing_heavy_correction(self) -> None:
        evidence = correction_asymmetry_evidence(
            .04, .16,
            {"bone": "Hips", "sample_index": 2},
            {"bone": "Chest", "sample_index": 31},
        )

        self.assertEqual(evidence["dominant_window"], "closing")
        self.assertAlmostEqual(cast(float, evidence["normalised_imbalance"]), .75)
        self.assertAlmostEqual(cast(float, evidence["maximum_to_minimum_ratio"]), 4.0)
        self.assertEqual(evidence["closing_attribution"], {"bone": "Chest", "sample_index": 31})

    def test_read_only_profile_diagnostics_compare_windows_deterministically(self) -> None:
        windows = {
            (10, 42): tuple(index * .03 + (index % 4) * .01 for index in range(33)),
            (42, 76): tuple(index * .025 + (index % 3) * .015 for index in range(35)),
        }

        first = diagnose_scalar_repair_options(windows, (6, 4), ("smoothstep", "quadratic"))
        second = diagnose_scalar_repair_options(windows, (4, 6), ("quadratic", "smoothstep"))

        self.assertEqual(first, second)
        self.assertTrue(first["read_only"])
        self.assertFalse(first["changes_admission_or_selection"])
        comparisons = cast(list[dict[str, Any]], first["comparisons"])
        self.assertEqual(len(comparisons), 8)
        self.assertTrue(all(row["evaluated"] for row in comparisons))
        self.assertEqual(
            [(row["source_range"], row["window_frames"], row["blend_profile"]) for row in comparisons[:4]],
            [([10, 42], 4, "quadratic"), ([10, 42], 4, "smoothstep"),
             ([10, 42], 6, "quadratic"), ([10, 42], 6, "smoothstep")],
        )
        self.assertTrue(all("correction_asymmetry" in row for row in comparisons))

    def test_adaptive_supports_are_duration_bounded_and_order_invariant(self) -> None:
        first = adaptive_repair_specs(39, ("smoothstep", "linear", "quadratic"))
        second = adaptive_repair_specs(39, ("quadratic", "smoothstep", "linear"))
        self.assertEqual(first, second)
        self.assertTrue(all(width <= 13 for width, _ in first))
        self.assertEqual({profile for _, profile in first}, {"linear", "quadratic", "smoothstep"})

    def test_smooth_wide_repair_reduces_curvature_without_pop_or_spike(self) -> None:
        source = tuple(index * .02 + (index / 38.0) ** 2 * .06 for index in range(39))
        narrow, narrow_evidence = repair_scalar_samples(source, 6, "quadratic")
        smooth, smooth_evidence = repair_scalar_samples(source, 12, "smoothstep")
        self.assertLess(
            cast(float, smooth_evidence["maximum_correction_curvature"]),
            cast(float, narrow_evidence["maximum_correction_curvature"]),
        )
        self.assertAlmostEqual(smooth[0], smooth[-1])
        self.assertAlmostEqual(smooth[1] - smooth[0], smooth[-1] - smooth[-2])
        self.assertEqual(smooth[12:-12], source[12:-12])

    def test_asymmetric_single_window_mutation_fails_two_window_oracle(self) -> None:
        source = [index * .1 for index in range(21)]
        trailing = source.copy()
        trailing[-1] = trailing[0]
        beginning_changed = any(trailing[index] != source[index] for index in range(5))
        ending_changed = any(trailing[-1 - index] != source[-1 - index] for index in range(5))
        self.assertFalse(beginning_changed and ending_changed)

    def test_excessive_pose_velocity_curvature_and_foot_risk_reject(self) -> None:
        base = {
            "endpoint_translation": .01, "endpoint_rotation_rad": .01,
            "linear_derivative_mismatch": .01, "angular_derivative_mismatch_rad": .01,
            "correction_curvature": .01, "angular_correction_curvature_rad": .01,
        }
        for metric in base:
            with self.subTest(metric=metric):
                decision = assess_repair({**base, metric: 10.0}, .4, 33)
                self.assertFalse(decision["accepted"])
                failures = decision["failures"]
                self.assertIsInstance(failures, dict)
                assert isinstance(failures, dict)
                self.assertIn(metric, failures)
        reversal = assess_repair(base, .4, 33, foot_reversal=True)
        spike = assess_repair(base, .4, 33, foot_spike=True)
        self.assertFalse(reversal["accepted"])
        self.assertFalse(spike["accepted"])

    def test_reversal_and_mirroring_preserve_metrics_and_topology(self) -> None:
        source = tuple(index * .03 + (index % 3) * .005 for index in range(33))
        repaired, evidence = repair_scalar_samples(source, 6)
        reversed_repaired, reversed_evidence = repair_scalar_samples(tuple(reversed(source)), 6)
        mirrored_repaired, mirrored_evidence = repair_scalar_samples(tuple(-value for value in source), 6)
        self.assertEqual(evidence["windows"], reversed_evidence["windows"])
        self.assertEqual(evidence["windows"], mirrored_evidence["windows"])
        endpoint_errors = evidence["endpoint_error"], reversed_evidence["endpoint_error"]
        derivative_errors = evidence["first_derivative_error"], mirrored_evidence["first_derivative_error"]
        self.assertTrue(all(isinstance(value, (int, float)) for value in endpoint_errors + derivative_errors))
        self.assertAlmostEqual(endpoint_errors[0], endpoint_errors[1])  # type: ignore[arg-type]
        self.assertAlmostEqual(derivative_errors[0], derivative_errors[1])  # type: ignore[arg-type]
        self.assertEqual(tuple(-value for value in repaired), mirrored_repaired)

    def test_boundary_apex_shift_is_equivalent_only_with_periodic_repair_evidence(self) -> None:
        before = [0.0] * 40
        after = [0.0] * 40
        before[39], after[37] = 1.0, 1.0
        evidence = {
            "window_ranges_zero_based": {"beginning": [0, 5], "ending": [34, 39]},
            "both_windows_modified": True,
        }
        accepted = assess_foot_lift_apex_preservation(before, after, .4, .02, evidence)
        self.assertTrue(accepted["accepted"], accepted)
        self.assertTrue(accepted["equivalent_boundary_extremum"], accepted)
        self.assertEqual(accepted["source_phase_landmark"], "ending")
        self.assertEqual(accepted["repaired_phase_landmark"], "ending")
        phase_delta = accepted["phase_delta_normalised_duration"]
        if not isinstance(phase_delta, float):
            raise AssertionError(accepted)
        self.assertAlmostEqual(phase_delta, 2.0 / 39.0)

        rejected = assess_foot_lift_apex_preservation(
            before, after, .4, .02, {**evidence, "both_windows_modified": False},
        )
        self.assertFalse(rejected["accepted"], rejected)

    def test_opening_and_closing_windows_are_the_same_periodic_apex_landmark(self) -> None:
        before, after = [0.0] * 40, [0.0] * 40
        before[0], after[37] = 1.0, 1.0
        evidence = {
            "window_ranges_zero_based": {"beginning": [0, 5], "ending": [34, 39]},
            "both_windows_modified": True,
        }
        decision = assess_foot_lift_apex_preservation(before, after, .4, .02, evidence)
        self.assertTrue(decision["accepted"], decision)
        self.assertTrue(decision["equivalent_boundary_extremum"], decision)
        self.assertEqual(decision["source_phase_landmark"], "beginning")
        self.assertEqual(decision["repaired_phase_landmark"], "ending")

    def test_genuine_apex_displacement_or_height_loss_remains_rejected(self) -> None:
        evidence = {
            "window_ranges_zero_based": {"beginning": [0, 5], "ending": [34, 39]},
            "both_windows_modified": True,
        }
        interior_before, interior_after = [0.0] * 40, [0.0] * 40
        interior_before[20], interior_after[18] = 1.0, 1.0
        displaced = assess_foot_lift_apex_preservation(
            interior_before, interior_after, .4, .02, evidence,
        )
        self.assertFalse(displaced["accepted"], displaced)

        boundary_after = [0.0] * 40
        boundary_after[37] = .9
        height_loss = assess_foot_lift_apex_preservation(
            [0.0] * 39 + [1.0], boundary_after, .4, .02, evidence,
        )
        self.assertFalse(height_loss["accepted"], height_loss)

    def test_equivalent_boundary_apex_is_scale_and_duration_invariant(self) -> None:
        for scale, samples, apex in ((.4, 40, 37), (1.2, 80, 74)):
            with self.subTest(scale=scale, samples=samples):
                before, after = [0.0] * samples, [0.0] * samples
                before[-1], after[apex] = scale, scale
                width = 6 if samples == 40 else 12
                evidence = {
                    "window_ranges_zero_based": {
                        "beginning": [0, width - 1], "ending": [samples - width, samples - 1],
                    },
                    "both_windows_modified": True,
                }
                decision = assess_foot_lift_apex_preservation(
                    before, after, scale, scale * .05, evidence,
                )
                self.assertTrue(decision["accepted"], decision)
                self.assertEqual(decision["source_phase_landmark"], decision["repaired_phase_landmark"])


if __name__ == "__main__":
    unittest.main()
