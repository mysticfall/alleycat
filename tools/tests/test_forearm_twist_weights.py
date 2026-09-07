from __future__ import annotations

import math
import unittest
from pathlib import Path
import tools.forearm_twist_weights as weights_module

from tools.forearm_twist_weights import (
    ForearmTwistWeightProfile,
    ForearmVertex,
    SELECTED_PROFILE,
    assert_axial_ownership_contract,
    clean_mirrored_contamination,
    is_source_bilateral,
    prune_and_normalise_deform_weights,
    topology_coordinates,
)


def profile(helper_fraction: float = 0.5, proximal: float = 0.10, distal: float = 1.10, smoothing_passes: int = 4) -> ForearmTwistWeightProfile:
    return ForearmTwistWeightProfile("test", helper_fraction, proximal, distal, smoothing_passes)


def author_axial(vertices, polygons, sides, authoring_profile=None):
    """Supply the same measured projection per side for legacy single-frame fixtures."""
    coordinates = [(vertex.t, vertex.radius) for vertex in vertices]
    return weights_module.author_axial_weights(
        vertices, polygons, sides, {side.lower_arm[-2:]: coordinates for side in sides}, authoring_profile or SELECTED_PROFILE
    )


class ForearmTwistWeightsTests(unittest.TestCase):
    def test_selected_profile_is_twist_only_with_documented_ownership(self) -> None:
        self.assertEqual("topology-linear-p10-d110-s24", SELECTED_PROFILE.name)
        self.assertEqual(0.5, SELECTED_PROFILE.helper_fraction)
        self.assertEqual(0.10, SELECTED_PROFILE.proximal_t)
        self.assertEqual(1.10, SELECTED_PROFILE.distal_t)
        self.assertEqual(24, SELECTED_PROFILE.smoothing_passes)
        self.assertFalse(hasattr(SELECTED_PROFILE, "swing_phase"))
        with self.assertRaises(TypeError):
            ForearmTwistWeightProfile("bad", 0.5, 0.9, 0.10, 1.10, 24)

    def test_axial_sides_reject_ambiguous_group_names(self) -> None:
        left = weights_module.AxialSide("lowerarm_l", "forearm_l", "hand_l", frozenset({"thumb_l"}))
        vertices = [ForearmVertex(0.85, 0.02, {"lowerarm_l": 0.5, "hand_l": 0.5})]
        for sides in (
            (weights_module.AxialSide("lowerarm_l", "forearm_l", "hand_l", frozenset()), left),
            (left, weights_module.AxialSide("lowerarm_l", "other_l", "hand_l", frozenset())),
            (weights_module.AxialSide("lowerarm_l", "forearm_l", "hand_r", frozenset()),),
            (),
        ):
            with self.subTest(sides=sides), self.assertRaisesRegex(ValueError, "distinct physical group names|share an _l or _r suffix|Axial sides"):
                author_axial(vertices, (), sides)
        with self.assertRaisesRegex(ValueError, "exactly one entry per physical side"):
            weights_module.author_axial_weights(vertices, (), (left,), {"_r": [(0.5, 0.1)]}, SELECTED_PROFILE)
        with self.assertRaisesRegex(ValueError, "Axial projection lengths"):
            weights_module.author_axial_weights(vertices, (), (left,), {"_l": []}, SELECTED_PROFILE)

    def test_axial_authoring_rejects_nonfinite_and_negative_inputs(self) -> None:
        side = weights_module.AxialSide("lowerarm_l", "forearm_l", "hand_l", frozenset())
        for vertex in (
            ForearmVertex(math.nan, 0.02, {"lowerarm_l": 0.5, "hand_l": 0.5}),
            ForearmVertex(0.85, math.inf, {"lowerarm_l": 0.5, "hand_l": 0.5}),
            ForearmVertex(0.85, -0.1, {"lowerarm_l": 0.5, "hand_l": 0.5}),
            ForearmVertex(0.85, 0.02, {"lowerarm_l": -0.1, "hand_l": 1.1}),
            ForearmVertex(0.85, 0.02, {"lowerarm_l": math.nan, "hand_l": 1.0}),
        ):
            with self.subTest(vertex=vertex), self.assertRaisesRegex(ValueError, "Nonfinite or negative"):
                author_axial([vertex], (), (side,))

    def test_axial_authoring_conserves_the_three_anchor_pool_row_by_row(self) -> None:
        side = weights_module.AxialSide("lowerarm_l", "forearm_l", "hand_l", frozenset())
        original = {"lowerarm_l": 0.3, "forearm_l": 0.15, "hand_l": 0.45, "body": 0.8}
        vertices = [ForearmVertex(t, 0.02, dict(original)) for t in (0.2, 0.5, 0.8, 1.05)]
        authored = author_axial(vertices, ((0, 1), (1, 2), (2, 3)), (side,))
        for index, row in enumerate(authored.reference):
            pool_source = original["lowerarm_l"] + original["forearm_l"] + original["hand_l"]
            pool_authored = row["lowerarm_l"] + row["forearm_l"] + row["hand_l"]
            self.assertAlmostEqual(pool_source, pool_authored, places=9, msg=f"row {index}")
            self.assertAlmostEqual(original["body"], row["body"], places=12)

    def test_axial_ramp_moves_ownership_proximal_to_distal(self) -> None:
        side = weights_module.AxialSide("lowerarm_l", "forearm_l", "hand_l", frozenset())
        rows = []
        for t in (0.10, 0.325, 0.55, 0.775, 1.0, 1.10):
            rows.append(ForearmVertex(t, 0.02, {"lowerarm_l": 0.6, "hand_l": 0.4}))
        authored = author_axial(rows, ((index, index + 1) for index in range(5)), (side,))
        reference = list(authored.reference)
        # At the proximal boundary the helper owns nothing and the lower arm keeps everything.
        self.assertAlmostEqual(0.0, reference[0].get("forearm_l", 0.0), places=9)
        self.assertAlmostEqual(1.0, reference[0]["lowerarm_l"], places=9)
        # Mid-transition the helper owns most of the pool.
        self.assertGreater(reference[2]["forearm_l"], reference[2]["lowerarm_l"])
        self.assertGreater(reference[2]["forearm_l"], reference[2]["hand_l"])
        # Past the crossover the hand takes over.
        self.assertGreater(reference[4]["hand_l"], reference[4]["forearm_l"])
        # At the distal boundary the hand owns the whole pool.
        self.assertAlmostEqual(1.0, reference[5]["hand_l"], places=9)
        self.assertAlmostEqual(0.0, reference[5].get("forearm_l", 0.0), places=9)

    def test_profile_boundaries_parameterise_the_transition(self) -> None:
        side = weights_module.AxialSide("lowerarm_l", "forearm_l", "hand_l", frozenset())
        rows = [ForearmVertex(t, 0.02, {"lowerarm_l": 0.6, "hand_l": 0.4}) for t in (0.05, 0.30, 0.60)]
        polygons = ((0, 1), (1, 2))
        narrow = author_axial(rows, polygons, (side,), profile(proximal=0.05, distal=0.60))
        wide = author_axial(rows, polygons, (side,), profile(proximal=0.05, distal=1.10))
        # Under the narrow profile t=0.60 is at the distal boundary: the hand owns everything.
        self.assertAlmostEqual(1.0, narrow.reference[2]["hand_l"], places=9)
        # Under the wide profile t=0.60 is still inside the twist segment.
        self.assertGreater(wide.reference[2]["forearm_l"], 0.0)
        self.assertAlmostEqual(0.0, wide.reference[2]["hand_l"], places=9)

    def test_eligibility_protects_fingers_opposite_sides_windows_and_bilateral_rows(self) -> None:
        side = weights_module.AxialSide("lowerarm_l", "forearm_l", "hand_l", frozenset({"thumb_l"}))
        protected_finger = {"lowerarm_l": 0.5, "hand_l": 0.45, "thumb_l": 0.05}
        opposite = {"lowerarm_l": 0.5, "hand_l": 0.25, "lowerarm_r": 0.25}
        out_of_window = {"lowerarm_l": 0.5, "hand_l": 0.5}
        no_source_anchor = {"other_l": 1.0}
        bilateral = {"lowerarm_l": 0.5, "hand_l": 0.25, "hand_r": 0.25}
        eligible = {"lowerarm_l": 0.5, "hand_l": 0.5}
        rows = [
            ForearmVertex(0.85, 0.02, dict(protected_finger)),
            ForearmVertex(0.85, 0.02, dict(opposite)),
            ForearmVertex(-0.2, 0.02, dict(out_of_window)),
            ForearmVertex(0.85, 0.5, dict(out_of_window)),
            ForearmVertex(0.85, 0.02, dict(no_source_anchor)),
            ForearmVertex(0.85, 0.02, dict(bilateral)),
            ForearmVertex(0.85, 0.02, dict(eligible)),
        ]
        authored = author_axial(rows, ((index, index + 1) for index in range(6)), (side,))
        for index, source in enumerate((protected_finger, opposite, out_of_window, out_of_window, no_source_anchor, bilateral)):
            with self.subTest(index=index):
                self.assertEqual(source, dict(authored.reference[index]))
        self.assertNotEqual(eligible, dict(authored.reference[6]))
        # Finger memberships are protected from authoring entirely, including zero keys.
        self.assertIn("thumb_l", authored.reference[0])

    def test_beta_marks_boundary_and_interior_vertices(self) -> None:
        side = weights_module.AxialSide("lowerarm_l", "forearm_l", "hand_l", frozenset({"thumb_l"}))
        rows = [ForearmVertex(0.85, 0.02, {"lowerarm_l": 0.6, "hand_l": 0.4}) for _ in range(4)]
        # Vertex 3 is ineligible (finger ownership); vertices adjacent to the ineligible
        # region blur at beta 0.5; only true interior vertices hold beta 1.0.
        rows[3].weights["thumb_l"] = 0.05
        authored = author_axial(rows, ((0, 1), (1, 2), (2, 3)), (side,))
        self.assertEqual(0.0, authored.beta[0][3])
        self.assertEqual(1.0, authored.beta[0][1])
        self.assertEqual(0.5, authored.beta[0][2])
        self.assertEqual(1.0, authored.beta[0][0])

    def test_source_domain_snapshots_the_wrist_side_transition_only(self) -> None:
        side = weights_module.AxialSide("lowerarm_l", "forearm_l", "hand_l", frozenset())
        rows = [ForearmVertex(t, 0.02, {"lowerarm_l": 0.6, "hand_l": 0.4}) for t in (0.5, 0.8, 1.05, 1.2)]
        authored = author_axial(rows, ((index, index + 1) for index in range(3)), (side,))
        self.assertEqual((False, True, True, False), tuple(authored.source_domain[0]))

    def test_logical_twist_records_the_single_helper_mass(self) -> None:
        side = weights_module.AxialSide("lowerarm_l", "forearm_l", "hand_l", frozenset())
        rows = [ForearmVertex(t, 0.02, {"lowerarm_l": 0.6, "hand_l": 0.4}) for t in (0.3, 0.9)]
        authored = author_axial(rows, ((0, 1),), (side,))
        self.assertEqual(
            [row["forearm_l"] for row in authored.reference],
            list(authored.logical_twist[0]),
        )

    def test_axial_snapshot_survives_input_and_intermediate_mutation(self) -> None:
        fingers = {"thumb_l"}
        side = weights_module.AxialSide("lowerarm_l", "forearm_l", "hand_l", fingers)
        original = {"lowerarm_l": 0.3, "forearm_l": 0.15, "hand_l": 0.45, "body": 0.8}
        protected = {**original, "thumb_l": 0.01}
        rows = [protected, original.copy(), original.copy()]
        vertices = [ForearmVertex(0.85, 0.02, row) for row in rows]
        authored = author_axial(vertices, ((0, 1), (1, 2)), (side,))
        reference = [dict(row) for row in authored.reference]
        beta = tuple(authored.beta[0])
        source_domain = tuple(authored.source_domain[0])
        logical = tuple(authored.logical_twist[0])
        fingers.add("index_l")
        rows[0]["thumb_l"] = 0.0
        rows[1]["lowerarm_l"] = 99.0
        # An intermediate / exposed stage must not admit mutation of the frozen reference.
        with self.assertRaises(TypeError):
            authored.reference[1]["forearm_l"] = 99.0
        with self.assertRaises(TypeError):
            authored.beta[0][1] = 0.0
        with self.assertRaises(TypeError):
            authored.source_domain[0][1] = False
        self.assertEqual(reference, [dict(row) for row in authored.reference])
        self.assertEqual(beta, tuple(authored.beta[0]))
        self.assertEqual(source_domain, tuple(authored.source_domain[0]))
        self.assertEqual(logical, tuple(authored.logical_twist[0]))

    def test_zero_helper_membership_retained_and_promoted_where_authored(self) -> None:
        side = weights_module.AxialSide("lowerarm_l", "forearm_l", "hand_l", frozenset({"thumb_l"}))
        rows = [
            ForearmVertex(0.85, 0.02, {"lowerarm_l": 0.55, "hand_l": 0.4, "thumb_l": 0.05, "forearm_l": 0.0}),
            ForearmVertex(0.85, 0.02, {"lowerarm_l": 0.6, "hand_l": 0.4, "forearm_l": 0.0}),
            ForearmVertex(0.85, 0.02, {"lowerarm_l": 0.6, "hand_l": 0.4}),
        ]
        authored = author_axial(rows, ((0, 1), (1, 2)), (side,))
        # The protected finger row keeps its pre-existing zero helper membership untouched.
        self.assertIn("forearm_l", authored.reference[0])
        self.assertEqual(0.0, authored.reference[0]["forearm_l"])
        # Eligible rows are promoted to positive helper ownership, zero membership or not.
        self.assertGreater(authored.reference[1]["forearm_l"], 0.0)
        self.assertGreater(authored.reference[2]["forearm_l"], 0.0)

    def test_axial_ownership_contract_preserves_the_authored_reference(self) -> None:
        deform = {"lowerarm_l", "forearm_l", "hand_l", "body"}
        reference = {"lowerarm_l": 0.3, "forearm_l": 0.15, "hand_l": 0.45, "body": 0.8}
        before, after = assert_axial_ownership_contract(reference, dict(reference), deform, "lowerarm_l", "forearm_l", "hand_l", 1e-9)
        self.assertAlmostEqual(0.15 / sum(reference.values()), after["forearm_l"])
        for changed in ({"forearm_l": 0.2}, {"lowerarm_l": 0.4}, {"hand_l": 0.5}, {"body": 0.7}):
            with self.subTest(changed=changed), self.assertRaisesRegex(ValueError, "changed against the axial reference|helper ownership"):
                assert_axial_ownership_contract(
                    reference, {**reference, **changed}, deform, "lowerarm_l", "forearm_l", "hand_l", 1e-9
                )
        with self.assertRaisesRegex(ValueError, "negative influence"):
            assert_axial_ownership_contract(reference, {**reference, "forearm_l": -0.1}, deform, "lowerarm_l", "forearm_l", "hand_l", 1e-9)
        with self.assertRaisesRegex(ValueError, "no deform ownership"):
            assert_axial_ownership_contract(reference, {"unrelated": 1.0}, deform, "lowerarm_l", "forearm_l", "hand_l", 1e-9)

    def test_topology_coordinates_fixes_ends_and_relaxes_interior(self) -> None:
        vertices = [ForearmVertex(0.10, 0.02, {}), ForearmVertex(0.60, 0.02, {}), ForearmVertex(1.10, 0.02, {})]
        neighbours = [{1}, {0, 2}, {1}]
        coordinates = topology_coordinates(vertices, neighbours, profile(smoothing_passes=1), [True] * 3)
        self.assertEqual(0.0, coordinates[0])
        self.assertEqual(1.0, coordinates[2])
        self.assertAlmostEqual((0.5 + 0.0 + 1.0) / 3.0, coordinates[1])
        with self.assertRaisesRegex(ValueError, "distal_t must exceed proximal_t"):
            topology_coordinates(vertices, neighbours, profile(proximal=1.0, distal=1.0), [True] * 3)

    def test_prune_and_normalise_keeps_positive_deform_influences_only(self) -> None:
        weights = {"lowerarm_l": 0.2, "hand_l": 0.3, "forearm_l": 0.5, "unrelated": 0.4}
        result = prune_and_normalise_deform_weights(weights, {"lowerarm_l", "hand_l", "forearm_l"})
        self.assertEqual({"lowerarm_l": 0.2, "hand_l": 0.3, "forearm_l": 0.5}, result)
        self.assertAlmostEqual(1.0, math.fsum(result.values()))
        self.assertEqual({}, prune_and_normalise_deform_weights({"unrelated": 1.0}, {"lowerarm_l"}))

    def test_bilateral_and_mirrored_membership_helpers(self) -> None:
        self.assertTrue(is_source_bilateral({"hand_l": 0.4, "hand_r": 0.1}))
        self.assertFalse(is_source_bilateral({"hand_l": 0.4, "hand_r": 0.0}))
        self.assertFalse(is_source_bilateral({"hand_l": 1.0}))
        weights = {"hand_l": 0.9, "hand_r": 0.1}
        clean_mirrored_contamination(weights, "_r", "_l")
        self.assertEqual({"hand_l": 0.9, "hand_r": 0.1}, weights)


if __name__ == "__main__":
    unittest.main()
