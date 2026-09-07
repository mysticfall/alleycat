"""Pure-Python contract tests for the first Kimodo pipeline slice."""
from __future__ import annotations

import sys
import tempfile
from pathlib import Path
import unittest
import math
from typing import Any, cast


PACKAGE_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PACKAGE_ROOT))

from kimodo_pipeline.contracts import ROLE_REGISTRY, action_name, asset_identity, pivot_root_yaw_sign, role_projection_policy, transition_boundary_phase, validate_variant
from kimodo_pipeline.temp_paths import repository_temp_root, resolve_temp_root
from kimodo_pipeline.pivot import (
    MAXIMUM_CORRECTION_AREA,
    MAXIMUM_CORRECTION_MAGNITUDE,
    MAXIMUM_CORRECTED_DURATION,
    pivot_unit_yaw,
    project_pivot_heading,
    pivot_three_cycle_evidence,
    validate_pivot_repair_oracles,
)
from kimodo_pipeline.travel_frame import TravelFrameError, alignment_angle, estimate_source_travel_frame
from kimodo_pipeline.trajectory_normalisation import TrajectoryNormalisationError, normalise_xr_trajectory


class RoleRegistryTests(unittest.TestCase):
    def test_registry_is_the_closed_24_slot_catalogue(self) -> None:
        self.assertEqual(len(ROLE_REGISTRY), 24)
        self.assertEqual(len({role.code for role in ROLE_REGISTRY}), 24)
        loops = {role.code for role in ROLE_REGISTRY if role.is_loop}
        self.assertIn("sl", loops)
        self.assertIn("sr", loops)
        self.assertEqual({role.code for role in ROLE_REGISTRY if role.code.startswith("p")}, {"pl45", "pr45"})

    def test_segment_declarations_cover_the_authoritative_source_semantics(self) -> None:
        by_code = {role.code: role.segment_index for role in ROLE_REGISTRY}
        self.assertEqual(by_code["sl"], 1)
        self.assertEqual(by_code["sr"], 1)
        self.assertEqual(by_code["pl45"], 2)
        self.assertEqual(by_code["pr45"], 2)
        self.assertEqual({role.segment_index for role in ROLE_REGISTRY}, {0, 1, 2, 4, 5})

    def test_loop_suffix_is_blender_only(self) -> None:
        roles = {role.code: role for role in ROLE_REGISTRY}
        self.assertEqual(action_name("injured", roles["sl"]), "loc_injured_sl_loop")
        self.assertEqual(action_name("injured", roles["i2f_l"]), "loc_injured_i2f_l")
        self.assertEqual(asset_identity("injured", roles["sl"]), "loc_injured_sl")

    def test_transition_boundary_phase_is_a_role_contract(self) -> None:
        self.assertEqual(transition_boundary_phase("i2f_r"), ("end", "Right"))
        self.assertEqual(transition_boundary_phase("f2i_r"), ("start", "Right"))
        self.assertEqual(transition_boundary_phase("b2i_l"), ("start", "Left"))
        self.assertEqual(transition_boundary_phase("i2b_l"), ("end", "Left"))
        self.assertIsNone(transition_boundary_phase("wf_r"))

    def test_every_role_has_exactly_one_authoritative_projection_policy(self) -> None:
        policies = {role.code: role_projection_policy(role) for role in ROLE_REGISTRY}
        self.assertEqual(len(policies), 24)
        self.assertEqual({policy.family for policy in policies.values()}, {
            "walk", "walk_transition", "precision_lateral", "pivot", "strafe", "arc",
        })
        self.assertEqual(policies["wf_r"].translation_axes, (2,))
        self.assertFalse(policies["wf_r"].promote_global_z_yaw)
        self.assertEqual(policies["pr45"].translation_axes, ())
        self.assertTrue(policies["pr45"].reconstruct_full_hierarchy)
        self.assertEqual(policies["sr"].loop_closure, "complete_event_repeat_offset")
        self.assertEqual(policies["xr_l"].loop_closure, "generated_recurrence")

    def test_pivot_root_unit_is_source_paced_bounded_and_rejects_invalid_source_headings(self) -> None:
        source_heading = [0.0, 0.03, 0.14, 0.18, 0.48]
        for code, sign in (("pr45", -1.0), ("pl45", 1.0)):
            self.assertEqual(pivot_root_yaw_sign(code), sign)
            values = pivot_unit_yaw(code, [sign * value for value in source_heading])
            self.assertEqual(values[0], 0.0)
            self.assertTrue(math.isclose(values[-1], sign * math.pi / 4.0, abs_tol=1e-12))
            self.assertTrue(all(0.0 <= value * sign <= math.pi / 4.0 for value in values))
            self.assertTrue(all((following - preceding) * sign >= 0.0 for preceding, following in zip(values, values[1:])))
            self.assertNotAlmostEqual(values[1] / values[-1], 1 / (len(values) - 1))
        with self.assertRaisesRegex(ValueError, "correction-magnitude budget"):
            pivot_unit_yaw("pr45", [0.0, -0.20, -0.15, -0.40])
        with self.assertRaisesRegex(ValueError, "insufficient signed range"):
            pivot_unit_yaw("pr45", [0.0, -0.00003, 0.00002, -0.00004])
        with self.assertRaisesRegex(ValueError, "wrong signed direction"):
            pivot_unit_yaw("pr45", [0.0, 0.20, 0.40])
        with self.assertRaisesRegex(ValueError, "insufficient signed range"):
            pivot_unit_yaw("pr45", [0.0, -1e-6, -2e-6])

    def test_pivot_heading_projection_accepts_and_measures_a_small_natural_reversal(self) -> None:
        raw = [-index * 0.03 for index in range(22)]
        raw[10] = raw[9] + 0.0002985
        projection = project_pivot_heading("pr45", raw)
        self.assertEqual(projection.raw_trace[0], projection.projected_trace[0])
        self.assertEqual(projection.raw_trace[-1], projection.projected_trace[-1])
        self.assertGreater(projection.maximum_correction, 0.0)
        self.assertLessEqual(projection.maximum_correction, MAXIMUM_CORRECTION_MAGNITUDE)
        self.assertLessEqual(projection.corrected_duration, MAXIMUM_CORRECTED_DURATION)
        self.assertLessEqual(projection.correction_area, MAXIMUM_CORRECTION_AREA)
        self.assertTrue(all(b <= a for a, b in zip(projection.projected_trace, projection.projected_trace[1:])))
        self.assertEqual(projection.correction_runs[0]["start_sample"], 9)

    def test_pivot_heading_projection_rejects_oversized_noisy_and_wrong_sign_sources(self) -> None:
        for trace, reason in (
            ([0.0, -0.20, -0.15, -0.40], "correction-magnitude budget"),
            ([0.0, -0.10, -0.098, -0.20, -0.198, -0.30, -0.298, -0.50], "corrected-duration budget"),
            ([0.0, 0.20, 0.40], "wrong signed direction"),
            ([0.0, -0.00003, 0.00002, -0.00004], "insufficient signed range"),
        ):
            with self.subTest(trace=trace), self.assertRaisesRegex(ValueError, reason):
                project_pivot_heading("pr45", trace)

    def test_pivot_three_cycle_composition_accumulates_without_translation_or_reset(self) -> None:
        unit = pivot_unit_yaw("pr45", [0.0, -0.03, -0.14, -0.18, -0.48])
        evidence = pivot_three_cycle_evidence(
            "pr45", unit, [[0.0, 0.0, 0.0] for _ in unit],
        )
        self.assertEqual(evidence["period_frames"], 4)
        self.assertTrue(math.isclose(float(evidence["final_yaw"]), -3.0 * math.pi / 4.0))
        self.assertEqual(evidence["boundary_yaw"], [0.0, -math.pi / 4.0, -math.pi / 2.0, -3.0 * math.pi / 4.0])
        with self.assertRaisesRegex(ValueError, "translation leakage"):
            pivot_three_cycle_evidence("pr45", unit, [[0.0, 0.0, 1e-4] for _ in unit])
        with self.assertRaisesRegex(ValueError, "incorrect signed"):
            pivot_three_cycle_evidence("pl45", unit, [[0.0, 0.0, 0.0] for _ in unit])

    def test_pivot_source_repair_oracles_reject_declared_corruptions(self) -> None:
        valid = {
            "comparison_frame": "cycle-normalised generated Root",
            "both_repair_windows_used": True,
            "maximum_immutable_interior_change": 0.0,
            "root_translation_maximum": 0.0,
            "double_application_detected": False,
            "contact_topology_preserved": True,
            "maximum_contact_velocity_delta": 0.01,
            "contact_velocity_limit": 0.02,
            "maximum_limb_length_delta": 0.001,
            "limb_length_limit": 0.002,
            "minimum_knee_orientation_dot": 0.95,
            "knee_orientation_limit": 0.8,
        }
        validate_pivot_repair_oracles(valid)
        corruptions = {
            "comparison_frame": "source local",
            "both_repair_windows_used": False,
            "maximum_immutable_interior_change": 1e-6,
            "root_translation_maximum": 1e-6,
            "double_application_detected": True,
            "contact_topology_preserved": False,
            "maximum_contact_velocity_delta": 0.03,
            "maximum_limb_length_delta": 0.003,
            "minimum_knee_orientation_dot": -0.1,
        }
        for key, value in corruptions.items():
            with self.subTest(corruption=key), self.assertRaises(ValueError):
                validate_pivot_repair_oracles({**valid, key: value})


class VariantValidationTests(unittest.TestCase):
    def test_lower_case_snake_case_variant_is_accepted(self) -> None:
        self.assertEqual(validate_variant("injured_guard"), "injured_guard")

    def test_invalid_variant_fails_safely(self) -> None:
        for variant in ("", "Invalid", "invalid-1", "invalid__1", "1invalid"):
            with self.subTest(variant=variant):
                with self.assertRaises(ValueError):
                    validate_variant(variant)


class TemporaryRootValidationTests(unittest.TestCase):
    def test_nested_override_is_accepted(self) -> None:
        with tempfile.TemporaryDirectory(dir=repository_temp_root()) as directory:
            nested = Path(directory) / "evidence"
            self.assertEqual(resolve_temp_root(nested), nested.resolve())

    def test_outside_override_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            outside = Path(directory) / "not-game-temp"
            with self.assertRaisesRegex(ValueError, r"--temp-root must resolve beneath.*game/temp"):
                resolve_temp_root(outside)


class SourceTravelFrameTests(unittest.TestCase):
    def test_offset_direction_preserves_every_source_progress_sample(self) -> None:
        progress = (0.0, .03, .11, .27, .46, .72, .93)
        angle = math.radians(-3.385872306)
        direction = math.sin(angle), math.cos(angle)
        samples = tuple((direction[0] * value, direction[1] * value) for value in progress)
        frame = estimate_source_travel_frame(samples, 1.0)
        self.assertEqual(len(frame.progress), len(progress))
        for actual, expected in zip(frame.progress, progress):
            self.assertAlmostEqual(actual, expected, places=12)
        self.assertAlmostEqual(math.degrees(alignment_angle(frame.direction, (0.0, 1.0))), -3.385872306, places=8)
        self.assertGreater(max(abs(value - progress[-1] * index / (len(progress) - 1)) for index, value in enumerate(frame.progress)), .01)

    def test_mirror_and_reversal_commute_with_constant_frame_estimation(self) -> None:
        samples = tuple((-.06 * value, value) for value in (0.0, .1, .25, .45, .7, .85, 1.0))
        native = estimate_source_travel_frame(samples, 1.0)
        mirrored = estimate_source_travel_frame(tuple((-x, z) for x, z in samples), 1.0)
        reversed_samples = tuple((x - samples[-1][0], z - samples[-1][1]) for x, z in reversed(samples))
        reversed_frame = estimate_source_travel_frame(reversed_samples, 1.0)
        self.assertAlmostEqual(mirrored.direction[0], -native.direction[0], places=12)
        self.assertAlmostEqual(mirrored.direction[1], native.direction[1], places=12)
        self.assertAlmostEqual(reversed_frame.direction[0], -native.direction[0], places=12)
        self.assertAlmostEqual(reversed_frame.direction[1], -native.direction[1], places=12)
        expected_reversed = tuple(native.progress[-1] - value for value in reversed(native.progress))
        self.assertEqual(
            tuple(round(value, 12) for value in reversed_frame.progress),
            tuple(round(value, 12) for value in expected_reversed),
        )

    def test_degenerate_curved_and_wrong_basis_traces_reject(self) -> None:
        traces = (
            tuple((index * .001, 0.0) for index in range(7)),
            tuple((math.sin(index * math.pi / 12), 1.0 - math.cos(index * math.pi / 12)) for index in range(7)),
            ((0.0, 0.0), (.15, 0.0), (.30, 0.0), (.30, .15), (.30, .30), (.15, .30), (0.0, .30)),
        )
        for samples in traces:
            with self.subTest(samples=samples), self.assertRaises(TravelFrameError):
                estimate_source_travel_frame(samples, 1.0)


class XrTrajectoryNormalisationTests(unittest.TestCase):
    @staticmethod
    def _trace(scale: float = 1.0, offset: tuple[float, float] = (0.0, 0.0)) -> tuple[tuple[float, float], ...]:
        pacing = (0.0, .03, .09, .18, .30, .45, .61, .76, .88, .96, 1.0)
        drift = (0.0, .012, .030, .060, .092, .115, .125, .112, .078, .038, 0.0)
        return tuple((offset[0] + scale * along, offset[1] + scale * across) for along, across in zip(pacing, drift))

    def test_role_specific_normalisation_preserves_source_pacing_and_is_scale_offset_mirror_invariant(self) -> None:
        decisions = []
        for scale, offset, mirror in ((1.0, (0.0, 0.0), 1.0), (2.7, (8.0, -3.0), 1.0), (1.0, (0.0, 0.0), -1.0)):
            source = tuple((x, mirror * z) for x, z in self._trace(scale, offset))
            result = normalise_xr_trajectory("xr_r", source, scale)
            decisions.append(result.evidence)
            frame = cast(dict[str, Any], result.evidence["candidate_local_frame"])
            direction = cast(list[float], frame["travel_direction_lateral_forward"])
            progress = cast(list[float], result.evidence["scalar_progress"])
            projected = tuple(
                (sample[0] - source[0][0]) * direction[0] + (sample[1] - source[0][1]) * direction[1]
                for sample in result.samples
            )
            for actual, expected in zip(projected, progress):
                self.assertAlmostEqual(actual, expected, places=10)
            self.assertLess(max(cast(list[float], result.evidence["subwindow_angle_degrees_after"])), 15.0)
        self.assertAlmostEqual(decisions[0]["orthogonal_retention_factor"], decisions[1]["orthogonal_retention_factor"], places=12)
        self.assertAlmostEqual(decisions[0]["orthogonal_retention_factor"], decisions[2]["orthogonal_retention_factor"], places=12)

    def test_xr_l_admits_bounded_lateral_drift_without_relaxing_the_straight_gate(self) -> None:
        # This declared lateral cycle has stable signed travel, but its natural
        # Hips drift makes a raw equal-index subwindow exceed the 15-degree
        # straight-travel gate.  The role-local correction must preserve its
        # progress and repair only that orthogonal drift; it is not a threshold
        # exception for an authored action or frame range.
        result = normalise_xr_trajectory("xr_l", self._trace(), 1.0)
        before = cast(list[float], result.evidence["subwindow_angle_degrees_before"])
        after = cast(list[float], result.evidence["subwindow_angle_degrees_after"])
        self.assertGreater(max(before), 15.0)
        self.assertLess(max(before), 25.0)
        self.assertLess(max(after), 15.0)
        self.assertEqual(result.evidence["unchanged_selection_limit_degrees"], 15.0)
        self.assertFalse(cast(bool, result.evidence["source_pacing_changed"]))
        self.assertTrue(cast(bool, result.evidence["endpoint_correction_is_bilateral_zero"]))

    def test_non_xr_and_genuinely_curved_or_unstable_slices_reject(self) -> None:
        with self.assertRaisesRegex(TrajectoryNormalisationError, "not applicable"):
            normalise_xr_trajectory("xl_r", self._trace(), 1.0)
        curved = tuple((math.sin(index * math.pi / 12), 1.0 - math.cos(index * math.pi / 12)) for index in range(13))
        with self.assertRaises(TrajectoryNormalisationError):
            normalise_xr_trajectory("xr_r", curved, 1.0)
        reversing = tuple((progress, 0.0) for progress in (
            0.0, .10, .20, .30, .40, .30, .20, .45, .65, .82, 1.0,
        ))
        with self.assertRaisesRegex(TrajectoryNormalisationError, "reversing"):
            normalise_xr_trajectory("xr_l", reversing, 1.0)

    def test_decision_is_independent_of_action_name_frame_offset_and_sequence_order(self) -> None:
        samples = self._trace()
        sequences = [
            {"name": "unrelated", "start": 900, "samples": tuple((index * .2, 0.0) for index in range(11))},
            {"name": "renamed_source", "start": 1280, "samples": samples},
            {"name": "extra_unrelated", "start": -40, "samples": tuple((index * .3, .001 * index) for index in range(11))},
        ]
        baseline = normalise_xr_trajectory("xr_r", samples, 1.0)
        for ordered in (sequences, list(reversed(sequences)), [sequences[2], sequences[0], sequences[1]]):
            candidate = next(row for row in ordered if row["samples"] == samples)
            shifted_frame_labels = tuple(candidate["start"] + index for index in range(len(samples)))
            self.assertNotEqual(shifted_frame_labels[0], 0)
            result = normalise_xr_trajectory("xr_r", candidate["samples"], 1.0)
            self.assertEqual(result.samples, baseline.samples)
            self.assertEqual(result.evidence, baseline.evidence)
