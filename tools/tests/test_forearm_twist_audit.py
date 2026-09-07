from __future__ import annotations

import ast
import json
import math
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from tools.rigging.forearm_twist_bridge import (
    BEND_THRESHOLD_CONFIG,
    BridgeVertex,
    ProvenanceError,
    absolute_pose_metrics,
    anatomical_hand_pose,
    anatomical_wrist_frame,
    assert_bend_sign_convention,
    canonical_triangle_id,
    canonicalise_vertices,
    continuity_metrics,
    evaluate_bend_gates,
    evaluate_candidate_eligibility,
    helper_twist_pose,
    incremental_bend_metrics,
    m_from_basis_origin,
    m_inverse,
    m_mul,
    m_rotation_axis_angle,
    m_transform_direction,
    m_transform_point,
    m_translation,
    pin_bend_population,
    pin_bridge_population,
    propagate_poses,
    projected_wrist_hinge_axis,
    q_from_axis_angle,
    q_from_matrix,
    retention,
    signed_foldover,
    signed_principal_twist,
    skin_point,
    subject_forward_frame,
    triangle_area,
    triangle_normal,
    v_add,
    v_cross,
    v_dot,
    v_length,
    v_normalised,
    v_scale,
    v_sub,
    validate_oriented_triangle_mapping,
    validate_candidate_weight_evidence,
)


REPO_ROOT = Path(__file__).resolve().parents[2]
AUDIT = REPO_ROOT / "tools/rigging/audit_forearm_twist.py"


class ForearmTwistAuditTests(unittest.TestCase):
    @unittest.skipUnless(shutil.which("blender"), "Blender is required")
    def test_unprovenanced_asset_copies_fail_closed_without_pre_helper_provenance(self) -> None:
        # The checked-in reference assets carry real schema-3 provenance now, so the fail-closed
        # contract stays machine-enforced against provenance-free copies of the same assets.
        sources = [
            REPO_ROOT / "game/assets/characters/reference/female/reference_female.blend",
            REPO_ROOT / "game/assets/characters/reference/male/reference_male.blend",
        ]
        before = [source.stat().st_mtime_ns for source in sources]
        with tempfile.TemporaryDirectory(prefix="alleycat-rig-002-") as temporary:
            root = Path(temporary)
            copies = [root / "female.blend", root / "male.blend"]
            for source, copy in zip(sources, copies, strict=True):
                shutil.copy2(source, copy)
            output = root / "evidence.json"
            completed = subprocess.run(
                ["blender", "--background", "--factory-startup", "--python-exit-code", "1", "--python", str(AUDIT), "--", "--female", str(copies[0]), "--male", str(copies[1]), "--output", str(output)],
                cwd=REPO_ROOT, check=False, capture_output=True, text=True, timeout=180,
            )
            self.assertEqual(2, completed.returncode)
            evidence = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(
                {
                    "schema_version": 5,
                    "producer": "tools/rigging/audit_forearm_twist.py",
                    "status": "not_measurable",
                    "reason": "missing_pre_helper_neutral_manifest",
                    "missing_paths": [
                        copy.with_suffix(".forearm_twist_manifest.json").resolve().as_posix()
                        for copy in copies
                    ],
                    "required_inputs": [
                        copy.with_suffix(".forearm_twist_manifest.json").resolve().as_posix()
                        for copy in copies
                    ],
                    "evidence_path": output.resolve().as_posix(),
                },
                evidence,
            )

        self.assertEqual(before, [source.stat().st_mtime_ns for source in sources])

    @unittest.skipUnless(shutil.which("blender"), "Blender is required")
    def test_candidate_evidence_absence_emits_structured_not_measurable_record(self) -> None:
        with tempfile.TemporaryDirectory(prefix="alleycat-rig-002-") as temporary:
            root = Path(temporary)
            female = root / "female.blend"
            male = root / "male.blend"
            for source in (female, male):
                source.with_suffix(".forearm_twist_manifest.json").write_text("{}", encoding="utf-8")
            output = root / "evidence.json"
            completed = subprocess.run(
                ["blender", "--background", "--python-exit-code", "1", "--python", str(AUDIT), "--", "--female", str(female), "--male", str(male), "--output", str(output)],
                cwd=REPO_ROOT, check=False, capture_output=True, text=True, timeout=120,
            )
            self.assertEqual(2, completed.returncode)
            record = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual("missing_candidate_weight_evidence", record["reason"])
            self.assertEqual([female.with_suffix(".forearm_twist_candidate_weights.json").as_posix(), male.with_suffix(".forearm_twist_candidate_weights.json").as_posix()], record["missing_paths"])


class ForearmTwistBridgePrimitiveTests(unittest.TestCase):
    @staticmethod
    def vertex(index: int, *, t: float = 0.90, position: tuple[float, float, float] = (1.0, 2.0, 3.0), helper: float = 0.10, hand: float = 0.10) -> BridgeVertex:
        return BridgeVertex("Body", 0, "left", index, position, t, 0.02, 0.80, helper, hand, (("lowerarm_l", 0.80), ("hand_l", hand), ("LeftForearmTwist", helper)))

    def test_manifest_ids_are_sorted_and_seam_duplicates_weld_only_when_provenance_agrees(self) -> None:
        first = self.vertex(7, position=(3.0, 2.0, 1.0))
        duplicate = self.vertex(8, position=(3.0, 2.0, 1.0))
        earlier = self.vertex(4, position=(1.0, 2.0, 3.0))

        result = canonicalise_vertices([first, duplicate, earlier])

        self.assertEqual(2, len(result))
        self.assertEqual([4], [member.source_index for member in result[0][1]])
        self.assertEqual([7, 8], [member.source_index for member in result[1][1]])

    def test_co_located_source_vertices_with_different_ownership_fail_closed(self) -> None:
        with self.assertRaisesRegex(ProvenanceError, "Ambiguous co-located"):
            canonicalise_vertices([self.vertex(1), self.vertex(2, hand=0.20)])

    def test_missing_and_reversed_oriented_triangle_mappings_fail_closed(self) -> None:
        source = [("a", "b", "c")]
        with self.assertRaisesRegex(ProvenanceError, "Missing or extra"):
            validate_oriented_triangle_mapping(source, [])
        with self.assertRaisesRegex(ProvenanceError, "Reversed"):
            validate_oriented_triangle_mapping(source, [("a", "c", "b")])
        with self.assertRaisesRegex(ProvenanceError, "Ambiguous duplicate"):
            validate_oriented_triangle_mapping(source * 2, source * 2)
        self.assertEqual("a|b|c", canonical_triangle_id(["b", "c", "a"]))

    def test_fixed_mixed_bin_membership_requires_ten_reference_vertices(self) -> None:
        with self.assertRaisesRegex(ProvenanceError, "requires 10"):
            pin_bridge_population([self.vertex(index) for index in range(9)])
        bins, full, helper_dominant = pin_bridge_population([self.vertex(index) for index in range(10)] + [self.vertex(11, helper=0.9, hand=0.0)])
        self.assertEqual(10, sum(len(members) for members in bins.values()))
        self.assertEqual(11, len(full))
        self.assertEqual(1, len(helper_dominant))
        with self.assertRaisesRegex(ProvenanceError, "membership changed"):
            pin_bridge_population([self.vertex(index) for index in range(10)], expected_mixed_bins=[18, 19])

    def test_projected_axis_and_hinge_retention_and_signed_foldover_are_geometric(self) -> None:
        axis = projected_wrist_hinge_axis((0.0, 0.0, 1.0), (0.0, 1.0, 0.0))
        self.assertAlmostEqual(0.0, axis[1])
        self.assertAlmostEqual(1.0, retention((1.0, 0.0, 0.0), (0.0, 0.0, 0.0), axis, 1.0))
        self.assertTrue(signed_foldover((0.0, 0.0, -1.0), (0.0, 0.0, 1.0)))
        self.assertFalse(signed_foldover((0.0, 0.0, 1.0), (0.0, 0.0, 1.0)))

    def test_generated_weight_validator_uses_generator_run_ownership_and_complete_rows(self) -> None:
        from tools import forearm_twist_generator_run_ownership as ownership

        def function(path: str, name: str) -> ast.FunctionDef:
            tree = ast.parse((REPO_ROOT / path).read_text(encoding="utf-8"))
            return next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == name)

        def calls(node: ast.AST) -> set[str]:
            return {ast.unparse(call.func) for call in ast.walk(node) if isinstance(call, ast.Call)}

        self.assertEqual(7, ownership.SCHEMA_VERSION)
        generator = function("tools/generate_character.py", "generate_character")
        stages = {ast.unparse(call.func): call for call in ast.walk(generator) if isinstance(call, ast.Call)}
        self.assertLess(stages["export_copy"].lineno, stages["snapshot_export_physical_memberships"].lineno)
        for stage in ("delete_objects", "purge_orphans", "normalise_exported_object_names",
                      "rename_exported_character_prefix", "reparent_hands_onto_twist_helpers",
                      "capture_forearm_twist_neutral_manifest", "capture_forearm_twist_axial_input"):
            self.assertLess(stages["snapshot_export_physical_memberships"].lineno, stages[stage].lineno)
        self.assertLess(stages["verify_export_physical_memberships"].lineno,
                        stages["apply_forearm_twist_gradient_on_export"].lineno)
        self.assertLess(
            stages["apply_forearm_twist_gradient_on_export"].lineno,
            stages["capture_forearm_twist_generator_run_ownership"].lineno,
        )
        self.assertEqual(
            ["exported_objects", "axial_reference", "authoring_eligible", "physical_memberships", "source_domain"],
            [ast.unparse(arg) for arg in stages["capture_forearm_twist_generator_run_ownership"].args],
        )
        self.assertLess(
            stages["capture_forearm_twist_generator_run_ownership"].lineno,
            stages["forearm_twist_generator_run_ownership.write_evidence"].lineno,
        )
        self.assertEqual(
            "skipped_export_meshes",
            ast.unparse(stages["forearm_twist_generator_run_ownership.write_evidence"].args[-1]),
        )
        authored = function("tools/generate_character.py", "apply_forearm_twist_gradient_on_export")
        self.assertTrue({
            "forearm_twist_weights.author_axial_weights",
            "write_forearm_twist_vertex_weights",
        } <= calls(authored))
        self.assertIn("authored.reference", ast.unparse(authored))
        self.assertIn("authoring_eligible", ast.unparse(generator))

        loader = function("tools/forearm_twist_generator_run_ownership.py", "load_evidence")
        self.assertIn("SCHEMA_VERSION", ast.unparse(loader))
        self.assertIn("sha256_file", calls(loader))
        variant = function("tools/mpfb/validate_forearm_twist_weights.py", "measure_variant")
        self.assertTrue({
            "forearm_twist_generator_run_ownership.load_evidence", "evidence_meshes", "validate_mesh",
        } <= calls(variant))
        self.assertIn("generator-run ownership mesh set does not match final blend", ast.unparse(variant))

        mesh = function("tools/mpfb/validate_forearm_twist_weights.py", "validate_mesh")
        self.assertTrue({
            "vertex_weights",
            "forearm_twist_generator_run_ownership.compare_physical_rows",
            "forearm_twist_generator_run_ownership.compare_zero_ledger",
            "forearm_twist_generator_run_ownership.compare_axial_rows",
            "forearm_twist_weights.assert_axial_ownership_contract",
            "assert_helper_topology",
            "weighted_skinning_transform",
        } <= calls(mesh))
        self.assertIn("axial_mesh.get('authoring_eligible')", ast.unparse(mesh))
        self.assertIn("'physical_rows'", ast.unparse(mesh))
        self.assertIn("'axial_deformation_max_error'", ast.unparse(mesh))

    def test_redistributed_candidate_weights_can_change_without_changing_fixed_membership(self) -> None:
        # Source ownership selected the membership; a later helper distribution
        # is validated from the separate candidate record rather than compared
        # back to immutable source ownership.
        validate_candidate_weight_evidence(
            ["source-a", "source-b"],
            {"source-a": {"helper": 0.65, "hand": 0.20}, "source-b": {"helper": 0.70, "hand": 0.15}},
        )
        with self.assertRaisesRegex(ProvenanceError, "coverage mismatch"):
            validate_candidate_weight_evidence(["source-a", "source-b"], {"source-a": {"helper": 0.65}})


class SyntheticForearmRig:
    """A pure-Python two-bone forearm matching the MPFB export conventions.

    Subject forward resolves to armature -Y, the forearm longitudinal axis is
    +X (elbow to wrist), the palm normal is hand-rest local +Z, and the helper
    rest basis is deliberately NOT axis-aligned with the forearm so no local-Y
    assumption can pass silently.
    """

    ELBOW = (0.3, 0.0, 0.8)
    WRIST = (0.5, 0.0, 0.8)
    LONGITUDINAL = (1.0, 0.0, 0.0)

    def __init__(self) -> None:
        # Hand rest columns: local +X toward the thumb side, local +Y (the
        # finger direction) continuing the forearm +X, local +Z the palm
        # normal.  m_from_basis_origin takes the 3x3 block directly, so the
        # tuple rows below are the transposed column images.
        palm = (0.0, -0.5, 0.8660254037844386)
        fingers = (1.0, 0.0, 0.0)
        hand_x = v_cross(palm, fingers)
        hand_rest = m_from_basis_origin(
            (
                (hand_x[0], fingers[0], palm[0]),
                (hand_x[1], fingers[1], palm[1]),
                (hand_x[2], fingers[2], palm[2]),
            ),
            self.WRIST,
        )
        lower_rest = m_translation(self.ELBOW)
        helper_rest = m_translation((0.4, 0.0, 0.8))
        # The middle finger continues the hand's local +Y; the thumb and
        # little finger sit either side of the palm.
        finger_rest = m_mul(hand_rest, m_translation((0.0, 0.12, 0.0)))
        thumb = v_normalised(v_cross(palm, fingers))
        little = v_normalised(v_cross(fingers, palm))
        bones = [
            ("pelvis", None, m_translation((0.0, 0.0, 0.0))),
            ("head", None, m_translation((0.0, 0.0, 1.0))),
            ("upperarm_l", None, m_translation((0.3, 0.0, 0.85))),
            ("upperarm_r", None, m_translation((-0.3, 0.0, 0.85))),
            ("lowerarm_l", None, lower_rest),
            ("LeftForearmTwist", "lowerarm_l", helper_rest),
            ("hand_l", "LeftForearmTwist", hand_rest),
            ("middle_03_l", "hand_l", finger_rest),
            ("thumb_01_l", "hand_l", m_translation(v_add(self.WRIST, v_scale(thumb, 0.08)))),
            ("pinky_03_l", "hand_l", m_translation(v_add(self.WRIST, v_scale(little, 0.08)))),
        ]
        self.bones = bones
        self.by_name = {name: (parent, rest) for name, parent, rest in bones}
        self.lower_rest = self.by_name["lowerarm_l"][1]
        self.hand_rest = self.by_name["hand_l"][1]
        self.helper_rest = self.by_name["LeftForearmTwist"][1]

    def subject_frame(self):
        def origin(name: str):
            return m_transform_point(self.by_name[name][1], (0.0, 0.0, 0.0))

        return subject_forward_frame(
            origin("pelvis"), origin("head"), origin("upperarm_l"), origin("upperarm_r")
        )

    def wrist_frame(self):
        return anatomical_wrist_frame(
            self.lower_rest,
            self.hand_rest,
            self.subject_frame()["forward"],
            m_transform_point(self.by_name["middle_03_l"][1], (0.0, 0.0, 0.0)),
            m_transform_point(self.by_name["thumb_01_l"][1], (0.0, 0.0, 0.0)),
            m_transform_point(self.by_name["pinky_03_l"][1], (0.0, 0.0, 0.0)),
            side_sign=1.0,
        )


class AnatomicalTwistExtractionTests(unittest.TestCase):
    def test_ninety_degree_pronation_is_recovered_exactly(self) -> None:
        # Factor-of-two regression: the superseded audit formula returned the
        # half angle (45 degrees) for a 90-degree rotation.
        quaternion = q_from_axis_angle((0.0, 0.0, 1.0), math.pi * 0.5)
        self.assertAlmostEqual(math.pi * 0.5, signed_principal_twist(quaternion, (0.0, 0.0, 1.0)), places=12)

    def test_twist_extraction_handles_arbitrary_axes_and_angles(self) -> None:
        axis = v_normalised((0.3, -0.6, 0.74))
        for degrees in (-170.0, -60.0, -0.5, 30.0, 88.25, 179.0):
            with self.subTest(degrees=degrees):
                quaternion = q_from_axis_angle(axis, math.radians(degrees))
                self.assertAlmostEqual(
                    math.radians(degrees),
                    signed_principal_twist(quaternion, axis),
                    places=12,
                )

    def test_quaternion_double_cover_produces_identical_twist(self) -> None:
        axis = (0.0, 1.0, 0.0)
        quaternion = q_from_axis_angle(axis, math.radians(-88.25))
        negated = tuple(-component for component in quaternion)
        self.assertAlmostEqual(
            signed_principal_twist(quaternion, axis),
            signed_principal_twist(negated, axis),  # type: ignore[arg-type]
            places=12,
        )

    def test_exact_half_turn_is_deterministic(self) -> None:
        twist = signed_principal_twist(q_from_axis_angle((1.0, 0.0, 0.0), math.pi), (1.0, 0.0, 0.0))
        self.assertAlmostEqual(math.pi, twist, places=12)


class AnatomicalPoseConstructionTests(unittest.TestCase):
    def test_synthetic_rig_derives_expected_pronation_and_flex_axis(self) -> None:
        frame = SyntheticForearmRig().wrist_frame()
        self.assertAlmostEqual(60.0, math.degrees(float(frame["pronation_radians"])), places=9)
        flex_axis = frame["flex_axis"]
        self.assertAlmostEqual(0.0, math.hypot(flex_axis[0], flex_axis[1]), places=9)
        self.assertAlmostEqual(-1.0, flex_axis[2], places=9)
        # Palm-forward check: pronation maps the palm normal onto the target.
        pronation = float(frame["pronation_radians"])
        rotated_palm = m_transform_direction(
            m_rotation_axis_angle(frame["longitudinal"], pronation), frame["palm_rest"]  # type: ignore[arg-type]
        )
        self.assertGreater(v_dot(rotated_palm, frame["palm_target"]), 0.999999)  # type: ignore[arg-type]

    @staticmethod
    def __right_multiplied_swing(rotation, axis, twist: float):
        """The remaining swing D x T^-1 of a composed Swing x Twist delta."""

        ax, ay, az, aw = rotation
        bx, by, bz, bw = q_from_axis_angle(axis, twist)  # type: ignore[arg-type]
        bx, by, bz = -bx, -by, -bz  # conjugate (inverse) of the unit twist quaternion
        product = (
            aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz,
        )
        scale = 1.0 / math.sqrt(sum(component * component for component in product))
        return tuple(component * scale for component in product)

    def test_pose_construction_recovers_commanded_components(self) -> None:
        # The composed pose is Swing(bend) x Twist(pronation): full-angle twist
        # extraction recovers the derived pronation, the remaining swing
        # recovers the commanded bend about an axis perpendicular to the
        # forearm, the wrist origin is invariant, and the anatomical bend sign
        # convention holds for both flexion and extension.
        rig = SyntheticForearmRig()
        frame = rig.wrist_frame()
        poses = self.__posed(rig, frame)
        hand_delta = m_mul(poses["hand_l"], m_inverse(rig.hand_rest))
        twist = signed_principal_twist(q_from_matrix(hand_delta), frame["longitudinal"])  # type: ignore[arg-type]
        self.assertAlmostEqual(float(frame["pronation_radians"]), twist, places=10)
        swing = self.__right_multiplied_swing(q_from_matrix(hand_delta), frame["longitudinal"], twist)  # type: ignore[arg-type]
        swing_angle = 2.0 * math.atan2(v_length(swing[:3]), swing[3])
        self.assertAlmostEqual(math.radians(60.0), swing_angle, places=10)
        if swing_angle > 1.0e-8:
            swing_axis_longitudinal_dot = abs(
                v_dot(v_normalised(swing[:3]), frame["longitudinal"])  # type: ignore[arg-type]
            )
            self.assertLess(swing_axis_longitudinal_dot, 1.0e-4)
        posed_origin = m_transform_point(poses["hand_l"], (0.0, 0.0, 0.0))
        self.assertLess(
            v_length(v_sub(posed_origin, m_transform_point(rig.hand_rest, (0.0, 0.0, 0.0)))), 1.0e-12
        )
        # Flexion moves the wrist-to-middle-finger direction toward the palm
        # face and extension away from it, asserted from geometry alone.
        self.assertGreater(assert_bend_sign_convention(frame, math.radians(60.0)), 0.25)
        self.assertLess(assert_bend_sign_convention(frame, math.radians(-60.0)), -0.25)

    def test_right_multiplied_swing_recovers_the_anatomical_flexion_axis(self) -> None:
        # For the anatomical composition Swing(bend) x Twist(pronation), the
        # right-multiplied residual D x T^-1 recovers the flexion rotation
        # itself; the conjugated order T^-1 x D keeps the angle and
        # perpendicularity but rotates the swing axis away from the anatomical
        # flexion axis. The twist-only writer extracts only the axial component,
        # and this oracle pins the fixture's composition contract.
        rig = SyntheticForearmRig()
        frame = rig.wrist_frame()
        posed_hand = anatomical_hand_pose(rig.lower_rest, rig.hand_rest, frame, math.radians(60.0))
        hand_delta = m_mul(posed_hand, m_inverse(rig.hand_rest))
        twist = signed_principal_twist(q_from_matrix(hand_delta), frame["longitudinal"])  # type: ignore[arg-type]
        swing = self.__right_multiplied_swing(q_from_matrix(hand_delta), frame["longitudinal"], twist)  # type: ignore[arg-type]

        swing_vector = swing[:3]
        signed_about_flex = 2.0 * math.atan2(v_dot(swing_vector, frame["flex_axis"]), swing[3])  # type: ignore[arg-type]
        self.assertAlmostEqual(math.radians(60.0), signed_about_flex, places=10)
        self.assertGreater(
            v_dot(v_normalised(swing_vector), frame["flex_axis"]),  # type: ignore[arg-type]
            0.9999,
            "The D x T^-1 swing axis must be the anatomical flexion axis.",
        )

    @staticmethod
    def __posed(rig, frame):
        posed_hand = anatomical_hand_pose(rig.lower_rest, rig.hand_rest, frame, math.radians(60.0))
        posed_helper = helper_twist_pose(
            rig.helper_rest, frame["longitudinal"], float(frame["pronation_radians"]), 0.25
        )
        return propagate_poses(rig.bones, {"hand_l": posed_hand, "LeftForearmTwist": posed_helper})

    def test_metrics_are_invariant_under_non_identity_world_transforms(self) -> None:
        # The declared measurement frame is the lower-arm rest frame; a
        # non-identity armature object transform must not change any metric.
        # World execution conjugates the skinning maps and transforms the
        # points; converting back through the (likewise transformed) declared
        # frame must reproduce the armature-space measurements exactly.
        rig = SyntheticForearmRig()
        frame = rig.wrist_frame()
        world = m_mul(
            m_translation((-0.4, 0.9, 0.33)),
            m_rotation_axis_angle(v_normalised((0.9, -0.1, 0.4)), math.radians(113.0)),
        )
        poses = self.__posed(rig, frame)
        rests = {name: rest for name, _parent, rest in rig.bones}
        vertices = {
            "v0": v_add(rig.WRIST, (0.0, 0.03, 0.0)),
            "v1": v_add(rig.WRIST, (0.0, -0.03, 0.0)),
            "v2": v_add(rig.WRIST, (0.02, 0.0, 0.0)),
        }
        influences = {
            "v0": [("lowerarm_l", 0.5), ("hand_l", 0.5)],
            "v1": [("lowerarm_l", 1.0)],
            "v2": [("lowerarm_l", 1.0)],
        }
        triangle = ("v0", "v1", "v2")

        def declared_metrics(declared_from_armature, armature_poses, armature_points):
            declared_inverse = m_inverse(declared_from_armature)
            neutral_declared = {
                identifier: m_transform_point(declared_inverse, position)
                for identifier, position in armature_points.items()
            }
            deformed_declared = {
                identifier: m_transform_point(
                    declared_inverse,
                    skin_point(armature_points[identifier], influences[identifier], armature_poses, rests),
                )
                for identifier in armature_points
            }
            return neutral_declared, deformed_declared

        neutral_identity, deformed_identity = declared_metrics(rig.lower_rest, poses, vertices)

        world_poses = {name: m_mul(world, pose) for name, pose in poses.items()}
        world_rests = {name: m_mul(world, rest) for name, rest in rests.items()}
        world_vertices = {identifier: m_transform_point(world, position) for identifier, position in vertices.items()}
        world_declared_frame = m_mul(world, rig.lower_rest)
        world_declared_inverse = m_inverse(world_declared_frame)
        neutral_world = {
            identifier: m_transform_point(world_declared_inverse, position)
            for identifier, position in world_vertices.items()
        }
        deformed_world = {
            identifier: m_transform_point(
                world_declared_inverse,
                skin_point(world_vertices[identifier], influences[identifier], world_poses, world_rests),
            )
            for identifier in world_vertices
        }

        for identifier in vertices:
            self.assertLess(
                v_length(v_sub(neutral_identity[identifier], neutral_world[identifier])), 1.0e-12
            )
            self.assertLess(
                v_length(v_sub(deformed_identity[identifier], deformed_world[identifier])), 1.0e-12
            )

        # The family metrics agree between the identity and world executions.
        identity_metrics = incremental_bend_metrics(
            {k: deformed_identity[k] for k in vertices},
            {k: neutral_identity[k] for k in vertices},
            [triangle],
            {"v0": 0.005, "v1": 0.005, "v2": 0.005},
            m_transform_point(m_inverse(rig.lower_rest), rig.WRIST),
            m_transform_direction(m_inverse(rig.lower_rest), frame["flex_axis"]),
        )
        world_metrics = incremental_bend_metrics(
            {k: deformed_world[k] for k in vertices},
            {k: neutral_world[k] for k in vertices},
            [triangle],
            {"v0": 0.005, "v1": 0.005, "v2": 0.005},
            m_transform_point(world_declared_inverse, m_transform_point(world, rig.WRIST)),
            m_transform_direction(world_declared_inverse, m_transform_direction(world, frame["flex_axis"])),
        )
        for key in ("minimum_radial_retention", "maximum_radial_deviation_edge_fraction", "minimum_area_retention"):
            self.assertAlmostEqual(
                float(identity_metrics[key]),
                float(world_metrics[key]),
                places=12,
                msg=key,
            )
        # Contributor IDs are deliberately not compared across frames here: v1 and v2
        # are rest-owned vertices whose retentions tie exactly at 1.0, and the world
        # path's ~1e-12 conversion noise can reorder that exact tie. ID determinism
        # (first of exact ties in measurement insertion order) is pinned by the
        # dedicated tie test in BendMetricFamilyTests instead.

    def test_posed_hand_propagates_to_finger_descendants(self) -> None:
        # Regression for the superseded behaviour that left finger descendants
        # at rest: a finger-weighted vertex must follow the posed hand.
        rig = SyntheticForearmRig()
        frame = rig.wrist_frame()
        posed_hand = anatomical_hand_pose(
            rig.lower_rest, rig.hand_rest, frame, math.radians(60.0)
        )
        poses = propagate_poses(rig.bones, {"hand_l": posed_hand})
        finger_rest = rig.by_name["middle_03_l"][1]
        hand_local_finger = m_mul(m_inverse(rig.hand_rest), finger_rest)
        expected = m_mul(poses["hand_l"], hand_local_finger)
        for row in range(4):
            for column in range(4):
                self.assertAlmostEqual(expected[row][column], poses["middle_03_l"][row][column], places=12)
        self.assertGreater(
            v_length(
                v_sub(
                    m_transform_point(poses["middle_03_l"], (0.0, 0.0, 0.0)),
                    m_transform_point(finger_rest, (0.0, 0.0, 0.0)),
                )
            ),
            1.0e-3,
            "The finger descendant must move with the posed hand.",
        )
        fingertip = m_transform_point(finger_rest, (0.0, 0.05, 0.0))
        moved = skin_point(fingertip, [("middle_03_l", 1.0)], poses, {name: rest for name, _p, rest in rig.bones})
        at_rest = skin_point(
            fingertip,
            [("middle_03_l", 1.0)],
            {name: rest for name, _parent, rest in rig.bones},
            {name: rest for name, _parent, rest in rig.bones},
        )
        self.assertGreater(v_length(v_sub(moved, at_rest)), 1.0e-3)


class BendMetricFamilyTests(unittest.TestCase):
    @staticmethod
    def blended_vertex_position(rig, frame, bend_degrees: float, weight: float = 0.5):
        posed_hand = anatomical_hand_pose(
            rig.lower_rest, rig.hand_rest, frame, math.radians(bend_degrees)
        )
        posed_helper = helper_twist_pose(
            rig.helper_rest, frame["longitudinal"], float(frame["pronation_radians"]), weight
        )
        poses = propagate_poses(rig.bones, {"hand_l": posed_hand, "LeftForearmTwist": posed_helper})
        rests = {name: rest for name, _parent, rest in rig.bones}
        vertex = v_add(rig.WRIST, (0.0, 0.03, 0.0))
        return skin_point(vertex, [("lowerarm_l", 0.5), ("hand_l", 0.5)], poses, rests), poses

    def test_absolute_radial_retention_matches_ideal_cos_half_angle(self) -> None:
        rig = SyntheticForearmRig()
        frame = rig.wrist_frame()
        deformed, _poses = self.blended_vertex_position(rig, frame, bend_degrees=0.0)
        neutral = v_add(rig.WRIST, (0.0, 0.03, 0.0))
        # Lower-arm-owned fillers: one off-axis, one near-axis (skipped by the
        # radial floor but still covered by the area and foldover checks).
        filler_off_axis = v_add(rig.WRIST, (0.0, -0.03, 0.0))
        filler_near_axis = v_add(rig.WRIST, (0.02, 0.0, 0.0))
        triangle = ("v0", "v1", "v2")
        triangle_id = canonical_triangle_id(list(triangle))
        metrics = absolute_pose_metrics(
            {"v0": deformed, "v1": filler_off_axis, "v2": filler_near_axis},
            [triangle],
            {triangle_id: triangle_area(neutral, filler_off_axis, filler_near_axis)},
            {"v0": 0.03, "v1": 0.03, "v2": 0.0},
            rig.WRIST,
            rig.LONGITUDINAL,
            {triangle_id: triangle_normal(neutral, filler_off_axis, filler_near_axis)},
        )
        # A 60-degree pronation blended 50/50 retains exactly cos(30 degrees).
        self.assertAlmostEqual(math.cos(math.radians(30.0)), metrics["minimum_radial_retention"], places=9)
        # Contributor IDs: v0 is the only evaluated vertex deformed off its reference radius
        # (v1 stays at retention 1.0; v2 sits below the radial measurement floor), so it is
        # the strict minimum, and the single evaluated triangle carries the area change.
        self.assertEqual("v0", metrics["minimum_radial_retention_vertex_id"])
        self.assertEqual(triangle_id, metrics["minimum_area_retention_triangle_id"])

    def test_incremental_metric_is_zero_for_palm_forward_versus_itself(self) -> None:
        rig = SyntheticForearmRig()
        frame = rig.wrist_frame()
        palm_forward, _poses = self.blended_vertex_position(rig, frame, bend_degrees=0.0)
        neutral = v_add(rig.WRIST, (0.0, 0.03, 0.0))
        states = {
            "v0": palm_forward,
            "v1": v_add(rig.WRIST, (0.0, -0.03, 0.0)),
            "v2": v_add(rig.WRIST, (0.02, 0.0, 0.0)),
        }
        metrics = incremental_bend_metrics(
            states,
            states,
            [("v0", "v1", "v2")],
            {"v0": 0.005, "v1": 0.005, "v2": 0.005},
            rig.WRIST,
            frame["flex_axis"],
        )
        self.assertAlmostEqual(1.0, metrics["minimum_radial_retention"], places=12)
        self.assertAlmostEqual(0.0, metrics["maximum_radial_deviation_edge_fraction"], places=12)
        # Identity state: every retention and deviation ties exactly, so the contributor
        # IDs pin the first-of-ties tie-break rather than any quality distinction.
        self.assertEqual("v0", metrics["minimum_radial_retention_vertex_id"])
        self.assertEqual("v0", metrics["maximum_radial_deviation_vertex_id"])
        self.assertEqual("v0|v1|v2", metrics["minimum_area_retention_triangle_id"])

    def test_incremental_metric_detects_bend_damage(self) -> None:
        rig = SyntheticForearmRig()
        frame = rig.wrist_frame()
        palm_forward, _poses = self.blended_vertex_position(rig, frame, bend_degrees=0.0)
        flexion, _poses = self.blended_vertex_position(rig, frame, bend_degrees=60.0)
        palm_forward_state = {
            "v0": palm_forward,
            "v1": v_add(rig.WRIST, (0.0, -0.03, 0.0)),
            "v2": v_add(rig.WRIST, (0.02, 0.0, 0.0)),
        }
        flexion_state = {
            "v0": flexion,
            "v1": palm_forward_state["v1"],
            "v2": palm_forward_state["v2"],
        }
        metrics = incremental_bend_metrics(
            palm_forward_state,
            flexion_state,
            [("v0", "v1", "v2")],
            {"v0": 0.005, "v1": 0.005, "v2": 0.005},
            rig.WRIST,
            frame["flex_axis"],
        )
        self.assertLess(float(metrics["minimum_radial_retention"]), 0.999)
        self.assertGreater(float(metrics["maximum_radial_deviation_edge_fraction"]), 0.0)
        # Only v0 moves between the palm-forward and flexion states (v1/v2 retention stays
        # 1.0 and deviation stays 0.0), so v0 is unambiguously both the minimum-retention
        # and maximum-deviation contributor, and the single triangle carries the area change.
        self.assertEqual("v0", metrics["minimum_radial_retention_vertex_id"])
        self.assertEqual("v0", metrics["maximum_radial_deviation_vertex_id"])
        self.assertEqual("v0|v1|v2", metrics["minimum_area_retention_triangle_id"])

    def test_contributor_ids_break_exact_ties_by_first_measurement_order(self) -> None:
        # Tie-break rule found in the implementation (tools/rigging/forearm_twist_bridge.py):
        # every contributor ID is min()/max() over an insertion-ordered mapping (Python
        # dicts preserve insertion order), and min()/max() return the FIRST extremum
        # encountered. Exact ties therefore resolve to the first identifier in measurement
        # insertion order — not the lexicographically smallest and not the last — which is
        # deterministic for the audit's deterministic input construction, so no
        # stable-sort fallback is required. Inserting the lexicographically larger
        # identifier first pins that contract.
        rig = SyntheticForearmRig()
        shared_palm_forward = v_add(rig.WRIST, (0.0, 0.03, 0.0))
        shared_bend = v_add(rig.WRIST, (0.0, 0.033, 0.0))
        incremental = incremental_bend_metrics(
            {"zz": shared_palm_forward, "aa": shared_palm_forward},
            {"zz": shared_bend, "aa": shared_bend},
            [],
            {"zz": 0.005, "aa": 0.005},
            rig.WRIST,
            rig.LONGITUDINAL,
        )
        # Both vertices share bitwise-identical positions, so their retentions
        # and deviations tie exactly and the first-inserted ID must win.
        self.assertEqual("zz", incremental["minimum_radial_retention_vertex_id"])
        self.assertEqual("zz", incremental["maximum_radial_deviation_vertex_id"])
        self.assertIsNone(incremental["minimum_area_retention_triangle_id"])

        absolute = absolute_pose_metrics(
            {"zz": shared_bend, "aa": shared_bend},
            [],
            {},
            {"zz": 0.03, "aa": 0.03},
            rig.WRIST,
            rig.LONGITUDINAL,
            {},
        )
        self.assertEqual("zz", absolute["minimum_radial_retention_vertex_id"])
        self.assertIsNone(absolute["minimum_area_retention_triangle_id"])


class ContinuityCreaseTests(unittest.TestCase):
    @staticmethod
    def strip_rings(ring_count: int, segment_count: int = 8):
        """A closed strip of rings around the forearm axis, x in [0.40, 0.60]."""

        rings = []
        for ring_index in range(ring_count):
            x = 0.40 + 0.20 * ring_index / (ring_count - 1)
            weight = ring_index / (ring_count - 1)
            vertices = []
            for segment in range(segment_count):
                angle = 2.0 * math.pi * segment / segment_count
                vertices.append((x, 0.03 * math.cos(angle), 0.03 * math.sin(angle) + 0.8))
            rings.append((vertices, weight))
        triangles = []

        def vid(ring: int, segment: int) -> str:
            return f"r{ring % ring_count}s{segment % segment_count}"

        for ring in range(ring_count - 1):
            for segment in range(segment_count):
                triangles.append((vid(ring, segment), vid(ring + 1, segment), vid(ring, segment + 1)))
                triangles.append(
                    (vid(ring + 1, segment), vid(ring + 1, segment + 1), vid(ring, segment + 1))
                )
        return rings, triangles

    @classmethod
    def deformed_strip(cls, ring_count: int):
        """Skin the strip with a 60-degree flexion swing and per-ring blending."""

        rig = SyntheticForearmRig()
        frame = rig.wrist_frame()
        posed_hand = anatomical_hand_pose(rig.lower_rest, rig.hand_rest, frame, math.radians(60.0))
        poses = propagate_poses(rig.bones, {"hand_l": posed_hand})
        rests = {name: rest for name, _parent, rest in rig.bones}
        rings, triangles = cls.strip_rings(ring_count)
        positions = {}
        neutral = {}
        for ring_index, (vertices, weight) in enumerate(rings):
            for segment, position in enumerate(vertices):
                identifier = f"r{ring_index}s{segment}"
                neutral[identifier] = position
                positions[identifier] = skin_point(
                    position,
                    [("lowerarm_l", 1.0 - weight), ("hand_l", weight)],
                    poses,
                    rests,
                )
        return neutral, positions, triangles

    def test_smooth_weight_transition_stays_below_the_declared_crease_bound(self) -> None:
        # Thirteen rings spread the same 60-degree swing smoothly (each band
        # folds only a few degrees); the cliff case below concentrates it.
        neutral, deformed, triangles = self.deformed_strip(ring_count=13)
        metrics = continuity_metrics(neutral, deformed, triangles)
        self.assertGreater(int(metrics["interior_edge_count"]), 0)
        self.assertIsNotNone(metrics["maximum_dihedral_increase_edge"])
        self.assertLessEqual(
            float(metrics["maximum_dihedral_increase_degrees"]),
            float(BEND_THRESHOLD_CONFIG["gates"]["continuity_maximum_dihedral_increase_degrees"]["value"]),
        )

    def test_injected_weight_cliff_creases_and_is_detected(self) -> None:
        neutral, deformed, triangles = self.deformed_strip(ring_count=2)
        metrics = continuity_metrics(neutral, deformed, triangles)
        self.assertIsNotNone(metrics["maximum_dihedral_increase_edge"])
        self.assertEqual(2, len(metrics["maximum_dihedral_increase_edge"]))
        self.assertGreater(
            float(metrics["maximum_dihedral_increase_degrees"]),
            float(BEND_THRESHOLD_CONFIG["gates"]["continuity_maximum_dihedral_increase_degrees"]["value"]),
        )

    def test_maximum_dihedral_increase_edge_breaks_exact_ties_by_sorted_edge_order(self) -> None:
        # Tie-break rule found in the implementation for the edge contributor ID:
        # interior_edges() returns lexicographically sorted undirected pairs and
        # max() keeps the FIRST extremum encountered, so an exact dihedral tie
        # reports the lexicographically smallest edge regardless of triangle
        # submission order. Deterministic, so no stable-sort fallback is needed.
        # Listing the b-quad's triangles first pins that the tie-break reads the
        # sorted edge order, not input order.
        def quad(prefix: str):
            neutral = {
                f"{prefix}0": (0.0, 0.0, 0.0),
                f"{prefix}1": (1.0, 0.0, 0.0),
                f"{prefix}2": (1.0, 1.0, 0.0),
                f"{prefix}3": (0.0, 1.0, 0.0),
            }
            deformed = dict(neutral)
            deformed[f"{prefix}1"] = (1.0, 0.0, 0.2)
            triangles = [
                (f"{prefix}0", f"{prefix}1", f"{prefix}2"),
                (f"{prefix}0", f"{prefix}2", f"{prefix}3"),
            ]
            return neutral, deformed, triangles

        neutral_a, deformed_a, triangles_a = quad("a")
        neutral_b, deformed_b, triangles_b = quad("b")
        metrics = continuity_metrics(
            {**neutral_b, **neutral_a},
            {**deformed_b, **deformed_a},
            triangles_b + triangles_a,
        )
        # The quads are geometrically identical, so both interior edges tie exactly.
        self.assertEqual(2, metrics["interior_edge_count"])
        self.assertGreater(float(metrics["maximum_dihedral_increase_degrees"]), 0.0)
        self.assertEqual(["a0", "a2"], metrics["maximum_dihedral_increase_edge"])


class EligibilityAndThresholdTests(unittest.TestCase):
    def test_threshold_config_is_complete_and_rationaled(self) -> None:
        gates = BEND_THRESHOLD_CONFIG["gates"]
        expected = {
            "absolute_minimum_area_retention",
            "absolute_signed_foldover_maximum",
            "absolute_minimum_radial_retention",
            "incremental_minimum_radial_retention",
            "incremental_minimum_area_retention",
            "incremental_maximum_radial_deviation_edge_fraction",
            "continuity_maximum_dihedral_increase_degrees",
        }
        self.assertEqual(expected, set(gates))
        for name, gate in gates.items():
            with self.subTest(gate=name):
                self.assertTrue(isinstance(gate["value"], (int, float)))
                self.assertTrue(str(gate["rationale"]).strip())

    def test_bend_radial_and_area_gates_do_not_carry_over_the_neutral_rest_hinge_value(self) -> None:
        # TR9: the neutral-rest 0.90 hinge-radius retention must not be silently reused as a
        # bend-pose gate. Every declared radial/area retention gate has to differ from that
        # value, so an accidental carry-over fails here instead of quietly tightening bend
        # measurement to a rest-pose contract. The exact gate values are provisional starting
        # points pending Stage 3 measured evidence and user review; this guard pins only the
        # no-silent-carry-over contract, not the numbers themselves.
        neutral_rest_hinge_retention = 0.90
        gates = BEND_THRESHOLD_CONFIG["gates"]
        for name in (
            "absolute_minimum_radial_retention",
            "absolute_minimum_area_retention",
            "incremental_minimum_radial_retention",
            "incremental_minimum_area_retention",
        ):
            with self.subTest(gate=name):
                self.assertNotEqual(
                    neutral_rest_hinge_retention,
                    float(gates[name]["value"]),
                    "The neutral-rest 0.90 hinge gate was carried over into a bend gate (TR9 forbids this).",
                )

    def test_candidate_passing_axial_but_failing_bend_is_not_eligible(self) -> None:
        # Regression for the superseded aggregate() that decided eligibility
        # from axial scenarios only.
        failing_bend = {"all_passed": False, "failed": ["incremental_minimum_radial_retention[flexion_60]"]}
        decision = evaluate_candidate_eligibility(True, "axial gates passed", failing_bend)
        self.assertEqual("rejected", decision["decision"])
        self.assertFalse(decision["bend_gates_passed"])
        self.assertTrue(any("anatomical-bend" in reason for reason in decision["reasons"]))

        passing_bend = {"all_passed": True, "failed": []}
        decision = evaluate_candidate_eligibility(True, "axial gates passed", passing_bend)
        self.assertEqual("eligible", decision["decision"])
        self.assertEqual([], decision["reasons"])

    def test_evaluate_bend_gates_reports_every_declared_gate(self) -> None:
        absolute = {
            "palm_forward": {"minimum_area_retention": 0.9, "signed_foldover_count": 0, "minimum_radial_retention": 0.9},
            "flexion_60": {"minimum_area_retention": 0.9, "signed_foldover_count": 0, "minimum_radial_retention": 0.9},
            "extension_60": {"minimum_area_retention": 0.9, "signed_foldover_count": 0, "minimum_radial_retention": 0.9},
        }
        incremental = {
            "flexion_60": {"minimum_radial_retention": 0.9, "maximum_radial_deviation_edge_fraction": 0.1, "minimum_area_retention": 0.9},
            "extension_60": {"minimum_radial_retention": 0.5, "maximum_radial_deviation_edge_fraction": 0.1, "minimum_area_retention": 0.9},
        }
        continuity = {
            "palm_forward": {"maximum_dihedral_increase_degrees": 1.0},
            "flexion_60": {"maximum_dihedral_increase_degrees": 1.0},
            "extension_60": {"maximum_dihedral_increase_degrees": 1.0},
        }
        gates = evaluate_bend_gates(absolute, incremental, continuity)
        self.assertFalse(gates["all_passed"])
        self.assertEqual(["incremental_minimum_radial_retention[extension_60]"], gates["failed"])
        self.assertEqual(3 * 3 + 2 * 3 + 3, len(gates["gates"]))


class BendPopulationPinningTests(unittest.TestCase):
    @staticmethod
    def bend_vertex(index: int, t: float, *, radius: float = 0.05, lower: float = 0.9, helper: float = 0.0, hand: float = 0.1) -> BridgeVertex:
        return BridgeVertex(
            "Body",
            0,
            "left",
            index,
            (1.0, 2.0, 3.0),
            t,
            radius,
            lower,
            helper,
            hand,
            (("lowerarm_l", lower), ("hand_l", hand)),
        )

    @classmethod
    def dense_population(cls) -> list[BridgeVertex]:
        vertices = []
        index = 0
        for bin_index in range(5, 12):
            # Keep every sample strictly inside the membership window even for
            # the edge bins that straddle it.
            t = bin_index * 0.10 + (0.07 if bin_index < 11 else 0.02)
            for _ in range(12):
                vertices.append(cls.bend_vertex(index, t))
                index += 1
        return vertices

    def test_dense_gap_free_window_is_pinned(self) -> None:
        population = pin_bend_population(self.dense_population())
        self.assertEqual(set(range(5, 12)), set(population))
        self.assertEqual(12, len(population[5]))

    def test_sparse_bins_and_membership_changes_fail_closed(self) -> None:
        sparse = [self.bend_vertex(index, 0.57) for index in range(9)]
        with self.assertRaisesRegex(ProvenanceError, "requires 10"):
            pin_bend_population(sparse)
        with self.assertRaisesRegex(ProvenanceError, "densely cover"):
            pin_bend_population(self.dense_population()[12:])
        with self.assertRaisesRegex(ProvenanceError, "bins must densely cover"):
            pin_bend_population(self.dense_population(), expected_bins=[5, 6])

    def test_out_of_window_and_low_pool_vertices_are_excluded(self) -> None:
        vertices = self.dense_population() + [
            self.bend_vertex(1000, 0.40),
            self.bend_vertex(1001, 1.20),
            self.bend_vertex(1002, 0.60, radius=0.30),
            self.bend_vertex(1003, 0.60, lower=0.2, hand=0.1),
        ]
        population = pin_bend_population(vertices)
        self.assertEqual(set(range(5, 12)), set(population))
        self.assertNotIn(1000, {member.source_index for members in population.values() for member in members})


if __name__ == "__main__":
    unittest.main()
