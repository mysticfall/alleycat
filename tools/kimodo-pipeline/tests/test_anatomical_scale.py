"""Scale-invariance contracts for travel-frame and recurrence decisions."""
from __future__ import annotations

import math
from pathlib import Path
import sys
import unittest


PACKAGE_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PACKAGE_ROOT))

from kimodo_pipeline.anatomical_scale import AnatomicalScaleError, anatomical_character_scale
from kimodo_pipeline.recurrence import (
    FOOT_MOVING_SPEED_FACTOR,
    FOOT_SPEED_RATIO_MAXIMUM,
    REFERENCE_ANATOMICAL_SCALE_METRES,
    foot_recurrence_flags,
    recurrence_acceptance,
    recurrence_metric_failures,
    recurrence_thresholds,
)
from kimodo_pipeline.travel_frame import TravelFrameError, estimate_source_travel_frame


class AnatomicalCharacterScaleTests(unittest.TestCase):
    _BONES = (
        ("Root", 1.0, True),
        ("Hips", .043, True),
        ("Chest", .245, True),
        ("LeftLeg", .432, True),
        ("RightLeg", .434, True),
        ("thigh_l_RET", .432, True),
        ("LeftShin_LOOK", .423, True),
        ("CTRL-main", 2.0, True),
        ("UnlabelledNonDeformer", 3.0, False),
    )
    _SAMPLES = tuple((-.04 * value, value) for value in (0.0, .06, .14, .25, .39, .55, .72))
    _RECURRENCE_MAXIMA = {
        "pose_translation": .0050,
        "pose_rotation_rad": .025,
        "linear_velocity": .0080,
        "angular_velocity_rad": .05,
        "linear_acceleration": .0160,
        "angular_acceleration_rad": .10,
    }

    @staticmethod
    def _decisions(bones, samples, maxima):
        scale = anatomical_character_scale(bones)
        try:
            frame = estimate_source_travel_frame(samples, scale)
            travel_accepted = True
        except TravelFrameError:
            frame = None
            travel_accepted = False
        _, failures = recurrence_metric_failures(maxima, scale)
        foot_flags = foot_recurrence_flags(.0030, .0032, .000008, .0003, scale)
        return scale, travel_accepted, frame, not failures and not any(foot_flags)

    def test_root_length_does_not_change_scale_or_normalised_decisions(self) -> None:
        changed = tuple((name, 100.0 if name == "Root" else length, deform) for name, length, deform in self._BONES)
        for pose_translation, expected_recurrence in ((.0040, True), (.0050, False)):
            maxima = {**self._RECURRENCE_MAXIMA, "pose_translation": pose_translation}
            with self.subTest(expected_recurrence=expected_recurrence):
                baseline = self._decisions(self._BONES, self._SAMPLES, maxima)
                mutated = self._decisions(changed, self._SAMPLES, maxima)
                self.assertIsNotNone(baseline[2])
                self.assertIsNotNone(mutated[2])
                assert baseline[2] is not None and mutated[2] is not None
                self.assertEqual(mutated[0], baseline[0])
                self.assertTrue(baseline[1])
                self.assertEqual(baseline[3], expected_recurrence)
                self.assertEqual((mutated[1], mutated[3]), (baseline[1], baseline[3]))
                self.assertAlmostEqual(mutated[2].net_travel_rig_scale, baseline[2].net_travel_rig_scale, places=12)

    def test_helper_and_ret_lengths_do_not_change_scale_or_decisions(self) -> None:
        changed = tuple(
            (name, 200.0 if name.endswith(("_RET", "_LOOK")) or name.startswith("CTRL-") else length, deform)
            for name, length, deform in self._BONES
        )
        for pose_translation, expected_recurrence in ((.0040, True), (.0050, False)):
            maxima = {**self._RECURRENCE_MAXIMA, "pose_translation": pose_translation}
            with self.subTest(expected_recurrence=expected_recurrence):
                baseline = self._decisions(self._BONES, self._SAMPLES, maxima)
                mutated = self._decisions(changed, self._SAMPLES, maxima)
                self.assertEqual(mutated[0], baseline[0])
                self.assertTrue(baseline[1])
                self.assertEqual(baseline[3], expected_recurrence)
                self.assertEqual((mutated[1], mutated[3]), (baseline[1], baseline[3]))

    def test_uniform_anatomical_scaling_preserves_normalised_decisions_and_progress(self) -> None:
        factor = 3.5
        scaled_bones = tuple((name, length * factor, deform) for name, length, deform in self._BONES)
        scaled_samples = tuple((x * factor, z * factor) for x, z in self._SAMPLES)
        scaled_maxima = {
            name: value if name.endswith("_rad") else value * factor
            for name, value in self._RECURRENCE_MAXIMA.items()
        }
        baseline = self._decisions(self._BONES, self._SAMPLES, self._RECURRENCE_MAXIMA)
        scaled = self._decisions(scaled_bones, scaled_samples, scaled_maxima)
        self.assertIsNotNone(baseline[2])
        self.assertIsNotNone(scaled[2])
        assert baseline[2] is not None and scaled[2] is not None
        self.assertAlmostEqual(scaled[0], baseline[0] * factor, places=12)
        self.assertTrue(baseline[1])
        self.assertFalse(baseline[3])
        self.assertEqual((scaled[1], scaled[3]), (baseline[1], baseline[3]))
        self.assertAlmostEqual(scaled[2].net_travel_rig_scale, baseline[2].net_travel_rig_scale, places=12)
        for actual, expected in zip(scaled[2].progress, baseline[2].progress):
            self.assertAlmostEqual(actual / factor, expected, places=12)
        for actual, expected in zip(scaled[2].direction, baseline[2].direction):
            self.assertAlmostEqual(actual, expected, places=12)

    def test_every_physical_recurrence_threshold_has_an_exact_boundary(self) -> None:
        scale = REFERENCE_ANATOMICAL_SCALE_METRES
        thresholds = recurrence_thresholds(scale)
        zero = {name: 0.0 for name in thresholds}
        for metric, threshold in thresholds.items():
            with self.subTest(metric=metric, side="below"):
                decision = recurrence_acceptance({**zero, metric: math.nextafter(threshold, 0.0)}, scale, "source recurrence")
                self.assertTrue(decision["accepted"], decision)
            with self.subTest(metric=metric, side="above"):
                decision = recurrence_acceptance({**zero, metric: math.nextafter(threshold, math.inf)}, scale, "generated recurrence")
                self.assertFalse(decision["accepted"], decision)
                self.assertEqual(set(decision["failures"]), {metric})

    def test_source_and_generated_gates_share_accepted_and_rejected_decisions(self) -> None:
        scale = REFERENCE_ANATOMICAL_SCALE_METRES
        thresholds = recurrence_thresholds(scale)
        accepted = {name: threshold * .75 for name, threshold in thresholds.items()}
        rejected = {**accepted, "pose_translation": .042954, "pose_rotation_rad": .116919}
        for expected, maxima in ((True, accepted), (False, rejected)):
            with self.subTest(expected=expected):
                source_recurrence = recurrence_acceptance(maxima, scale, "source recurrence")
                generated_recurrence = recurrence_acceptance(dict(maxima), scale, "generated recurrence")
                self.assertEqual(source_recurrence["accepted"], expected)
                self.assertEqual(generated_recurrence["accepted"], expected)
                self.assertEqual(source_recurrence["contract"], generated_recurrence["contract"])
                self.assertEqual(source_recurrence["thresholds"], generated_recurrence["thresholds"])
                self.assertEqual(source_recurrence["failures"], generated_recurrence["failures"])
                self.assertEqual(source_recurrence["diagnostic"], "source recurrence")
                self.assertEqual(generated_recurrence["diagnostic"], "generated recurrence")
                if not expected:
                    self.assertEqual(
                        set(source_recurrence["failures"]),
                        {"pose_translation", "pose_rotation_rad"},
                    )

    def test_recurrence_boundary_decisions_are_uniform_scale_invariant(self) -> None:
        scale = REFERENCE_ANATOMICAL_SCALE_METRES
        factor = 3.5
        thresholds = recurrence_thresholds(scale)
        for metric, threshold in thresholds.items():
            for multiplier, expected in ((.999999, True), (1.000001, False)):
                maxima = {name: 0.0 for name in thresholds}
                maxima[metric] = threshold * multiplier
                scaled = {
                    name: value if name.endswith("_rad") else value * factor
                    for name, value in maxima.items()
                }
                baseline_decision = recurrence_acceptance(maxima, scale, "source recurrence")["accepted"]
                scaled_decision = recurrence_acceptance(scaled, scale * factor, "generated recurrence")["accepted"]
                with self.subTest(metric=metric, multiplier=multiplier):
                    self.assertEqual(baseline_decision, expected)
                    self.assertEqual(scaled_decision, expected)

    def test_foot_wrap_pathology_boundaries_and_uniform_scale_invariance(self) -> None:
        scale = REFERENCE_ANATOMICAL_SCALE_METRES
        moving = FOOT_MOVING_SPEED_FACTOR * scale
        below_moving = math.nextafter(moving, 0.0)
        above_moving = math.nextafter(moving, math.inf)
        self.assertEqual(foot_recurrence_flags(below_moving, below_moving, -1.0, 0.0, scale), (False, False))
        self.assertEqual(foot_recurrence_flags(above_moving, above_moving, -1.0, 0.0, scale), (True, False))
        self.assertEqual(foot_recurrence_flags(above_moving, above_moving, 0.0, 0.0, scale), (False, False))
        self.assertEqual(
            foot_recurrence_flags(above_moving, above_moving, math.nextafter(0.0, -math.inf), 0.0, scale),
            (True, False),
        )

        smaller = .01 * scale
        delta = math.nextafter(recurrence_thresholds(scale)["linear_velocity"], math.inf)
        below_ratio = math.nextafter(FOOT_SPEED_RATIO_MAXIMUM, 0.0)
        above_ratio = math.nextafter(FOOT_SPEED_RATIO_MAXIMUM, math.inf)
        self.assertEqual(foot_recurrence_flags(smaller, smaller * below_ratio, 1.0, delta, scale), (False, False))
        self.assertEqual(foot_recurrence_flags(smaller, smaller * above_ratio, 1.0, delta, scale), (False, True))

        factor = 3.5
        baseline = foot_recurrence_flags(smaller, smaller * above_ratio, 1.0, delta, scale)
        scaled = foot_recurrence_flags(
            smaller * factor, smaller * above_ratio * factor, 1.0, delta * factor, scale * factor,
        )
        self.assertEqual(scaled, baseline)

    def test_no_valid_anatomical_bone_rejects_diagnostically(self) -> None:
        degenerate = (
            ("Root", 10.0, True),
            ("pelvis_RET", 8.0, True),
            ("LeftShin_LOOK", 7.0, True),
            ("CTRL-main", 6.0, False),
        )
        with self.assertRaisesRegex(AnatomicalScaleError, "no valid deforming anatomical motion bones"):
            anatomical_character_scale(degenerate)


if __name__ == "__main__":
    unittest.main()
