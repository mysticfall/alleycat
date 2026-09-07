"""Pure corruption and invariance oracles for finite walk transitions."""
from __future__ import annotations

import math
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from kimodo_pipeline.selection import Candidate, select_candidate
from kimodo_pipeline.travel_frame import (
    TravelFrameError,
    estimate_finite_transition_frame,
    estimate_source_travel_frame,
)


class FiniteTransitionContractTests(unittest.TestCase):
    def test_finite_estimator_does_not_apply_loop_only_equal_subwindow_gate(self) -> None:
        samples = [
            (0.00, 0.00), (0.018, 0.01), (0.026, 0.04),
            (0.030, 0.10), (0.031, 0.20), (0.030, 0.32),
            (0.028, 0.45), (0.025, 0.59), (0.020, 0.74),
        ]
        with self.assertRaisesRegex(TravelFrameError, "subwindow"):
            estimate_source_travel_frame(samples, 1.0)
        finite = estimate_finite_transition_frame(samples, 1.0)
        self.assertEqual(len(finite.progress), len(samples))
        self.assertGreater(finite.progress[-1], .70)

    def test_finite_estimator_preserves_non_linear_source_progress(self) -> None:
        samples = [(0.0, value) for value in (0.0, .01, .04, .11, .23, .40, .62)]
        finite = estimate_finite_transition_frame(samples, 1.0)
        linear = [finite.progress[-1] * index / (len(samples) - 1) for index in range(len(samples))]
        self.assertGreater(max(abs(actual - expected) for actual, expected in zip(finite.progress, linear)), .05)
        self.assertEqual(tuple(value for _, value in samples), finite.progress)

    def test_finite_estimator_is_uniform_scale_and_mirror_invariant(self) -> None:
        samples = [(0.01 * index, value) for index, value in enumerate((0.0, .01, .04, .11, .23, .40, .62))]
        baseline = estimate_finite_transition_frame(samples, 1.0)
        scaled = estimate_finite_transition_frame([(x * 3.0, z * 3.0) for x, z in samples], 3.0)
        mirrored = estimate_finite_transition_frame([(-x, z) for x, z in samples], 1.0)
        self.assertAlmostEqual(baseline.net_travel_rig_scale, scaled.net_travel_rig_scale)
        for expected, actual in zip(baseline.progress, scaled.progress):
            self.assertAlmostEqual(expected, actual / 3.0)
        for expected, actual in zip(baseline.progress, mirrored.progress):
            self.assertAlmostEqual(expected, actual)
        self.assertAlmostEqual(-baseline.direction[0], mirrored.direction[0])
        self.assertAlmostEqual(baseline.direction[1], mirrored.direction[1])

    def test_finite_estimator_rejects_curved_and_wrong_basis_evidence(self) -> None:
        curved = [(math.sin(index * math.pi / 4.0) * .30, index * .08) for index in range(9)]
        with self.assertRaisesRegex(TravelFrameError, "unstable finite transition"):
            estimate_finite_transition_frame(curved, 1.0)

    def test_content_signature_precedes_action_identity_and_order(self) -> None:
        first = Candidate("renamed_z", 101, 109, 0.0, {}, "aaa")
        second = Candidate("renamed_a", 1, 9, 0.0, {}, "bbb")
        self.assertIs(select_candidate((second, first)), first)
        self.assertIs(select_candidate((first, second)), first)
