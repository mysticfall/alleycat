"""Focused matrix-only loop seam checks; run with Blender's bundled Python."""

import importlib.util
import math
from pathlib import Path
import sys
import unittest

from mathutils import Matrix, Vector


MODULE_PATH = Path(__file__).resolve().parents[1] / "retarget_mixamo_animation.py"
SPEC = importlib.util.spec_from_file_location("retarget_mixamo_animation", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class RootMotionInvariantPoseMatrixTests(unittest.TestCase):
    def test_current_selected_roles_have_expected_reconstruction_classes(self) -> None:
        cases = {
            "pivot": (("turn", "in_place", "left", "90"), MODULE.ROOT_POLICY_TURN_IN_PLACE, None),
            "straight": (("walk", "forward"), MODULE.ROOT_POLICY_MOVING, "StraightMoving"),
            "side_step": (("walk", "sidestep", "left"), MODULE.ROOT_POLICY_MOVING, "StraightMoving"),
            "arc": (("walk", "arc", "left", "forward"), MODULE.ROOT_POLICY_MOVING, "CurvedMoving"),
        }
        for label, (tags, policy, subtype) in cases.items():
            classification = MODULE.classify_root_reconstruction(
                MODULE.RootReconstructionInput("locomotion", "TurnInPlace" if label == "pivot" else "StandingLocomotion", tags)
            )
            self.assertEqual((classification.policy, classification.subtype), (policy, subtype), label)

    def test_class_specific_validation_rejects_wrong_physical_heading_for_pivot_and_arc(self) -> None:
        pivot = MODULE.RootReconstructionInput("locomotion", "TurnInPlace", ("turn", "in_place", "left", "90"))
        arc = MODULE.RootReconstructionInput("locomotion", "StandingLocomotion", ("walk", "arc", "left", "forward"))
        with self.assertRaises(MODULE.ScriptError):
            MODULE.validate_class_specific_root_sequences(
                pivot, [Vector(), Vector()], [0.0, math.radians(-90.0)], [0.0, math.radians(-90.0)]
            )
        with self.assertRaises(MODULE.ScriptError):
            MODULE.validate_class_specific_root_sequences(
                arc, [Vector(), Vector((0.0, -1.0, 0.0))], [0.0, math.radians(-45.0)], [0.0, math.radians(-45.0)]
            )

    def test_class_specific_validation_rejects_pivot_travel_and_allows_fixed_heading_straight(self) -> None:
        pivot = MODULE.RootReconstructionInput("locomotion", "TurnInPlace", ("turn", "in_place", "right", "90"))
        with self.assertRaises(MODULE.ScriptError):
            MODULE.validate_class_specific_root_sequences(
                pivot, [Vector(), Vector((0.01, 0.0, 0.0))], [0.0, math.radians(90.0)], [0.0, math.radians(90.0)]
            )
        straight = MODULE.RootReconstructionInput("locomotion", "StandingLocomotion", ("walk", "sidestep", "left"))
        MODULE.validate_class_specific_root_sequences(
            straight, [Vector(), Vector((1.0, 0.0, 0.0))], [0.0, 0.0], [0.0, math.radians(15.0)]
        )

    def test_positive_visible_turn_writes_negative_euler_and_positive_physical_root_heading(self) -> None:
        visible_heading = math.radians(83.158)
        euler_z = MODULE.visible_clockwise_heading_to_canonical_root_euler_z(visible_heading)
        physical_heading = MODULE.evaluated_root_physical_heading_radians(MODULE.yaw_matrix(Vector(), euler_z))
        self.assertAlmostEqual(euler_z, -visible_heading)
        self.assertAlmostEqual(physical_heading, visible_heading, places=6)

    def test_negative_visible_turn_writes_positive_euler_and_negative_physical_root_heading(self) -> None:
        visible_heading = math.radians(-83.158)
        euler_z = MODULE.visible_clockwise_heading_to_canonical_root_euler_z(visible_heading)
        physical_heading = MODULE.evaluated_root_physical_heading_radians(MODULE.yaw_matrix(Vector(), euler_z))
        self.assertAlmostEqual(euler_z, -visible_heading)
        self.assertAlmostEqual(physical_heading, visible_heading, places=6)

    def test_named_right_pivot_reconstruction_keeps_zero_translation_and_matches_visible_heading(self) -> None:
        pivot = MODULE.RootReconstructionInput("locomotion", "TurnInPlace", ("turn", "in_place", "right", "90"))
        matrices, _ = MODULE.reconstruct_root_matrices(
            [1, 12, 23, 34, 46],
            {frame: Matrix.Identity(4) for frame in [1, 12, 23, 34, 46]},
            [Vector() for _ in range(5)],
            [0.0, 0.36, 0.72, 1.08, 1.451382],
            pivot,
        )
        end = matrices[46]
        self.assertAlmostEqual(end.to_euler("XYZ").z, -1.451382, places=6)
        self.assertAlmostEqual(MODULE.evaluated_root_physical_heading_radians(end), 1.451382, places=6)
        self.assertTrue(all(matrix.translation.length <= 1e-6 for matrix in matrices.values()))

    def test_root_yaw_change_with_closed_direct_child_and_descendant_bases_qualifies(self) -> None:
        start_root = MODULE.yaw_matrix(Vector((0.0, 0.0, 0.0)), 0.0)
        end_root = MODULE.yaw_matrix(Vector((3.0, -2.0, 0.0)), math.radians(90.0))
        direct_child_basis = Matrix.Translation((0.02, -0.01, 0.0)) @ Matrix.Rotation(math.radians(8.0), 4, "X")
        descendant_basis = Matrix.Translation((0.01, 0.02, 0.0)) @ Matrix.Rotation(math.radians(-4.0), 4, "Y")

        self.assertMatrixNotAlmostEqual(start_root, end_root)
        self.assertMatrixAlmostEqual(
            MODULE.retained_non_root_local_basis(direct_child_basis),
            MODULE.retained_non_root_local_basis(direct_child_basis),
        )
        self.assertMatrixAlmostEqual(
            MODULE.retained_non_root_local_basis(descendant_basis),
            MODULE.retained_non_root_local_basis(descendant_basis),
        )

    def test_real_retained_direct_child_translation_and_rotation_seam_fails(self) -> None:
        start_basis = Matrix.Identity(4)
        end_basis = Matrix.Translation((0.08, 0.0, 0.0)) @ Matrix.Rotation(math.radians(5.0), 4, "X")
        start_local = MODULE.retained_non_root_local_basis(start_basis)
        end_local = MODULE.retained_non_root_local_basis(end_basis)
        start_location, start_rotation, _ = start_local.decompose()
        end_location, end_rotation, _ = end_local.decompose()
        self.assertGreater((end_location - start_location).length, MODULE.LOOP_POSE_TRANSLATION_TOLERANCE)
        self.assertGreater(
            start_rotation.rotation_difference(end_rotation).angle,
            MODULE.LOOP_POSE_ANGULAR_TOLERANCE_RADIANS,
        )

    def assertMatrixAlmostEqual(self, actual: Matrix, expected: Matrix) -> None:
        for actual_row, expected_row in zip(actual, expected):
            for actual_value, expected_value in zip(actual_row, expected_row):
                self.assertAlmostEqual(actual_value, expected_value, places=6)

    def assertMatrixNotAlmostEqual(self, actual: Matrix, expected: Matrix) -> None:
        self.assertTrue(
            any(
                not math.isclose(actual_value, expected_value, abs_tol=1e-6)
                for actual_row, expected_row in zip(actual, expected)
                for actual_value, expected_value in zip(actual_row, expected_row)
            )
        )


if __name__ == "__main__":
    # Blender retains its own command-line arguments for embedded Python.
    unittest.main(argv=[sys.argv[0]])
