"""Pure rule coverage for the authoring-workspace bootstrap (Phase 3)."""
from __future__ import annotations

import math
import unittest

from kimodo_pipeline.bootstrap_validation import (
    WORKSPACE_REPORT_SCHEMA,
    derive_rotation_conventions,
    idle_input_failures,
    rotation_action_comparison,
    workspace_failures,
    workspace_report_structure_failures,
)


def _curve(data_path: str, index: int, frame: float, value: float) -> dict:
    return {
        "data_path": data_path,
        "array_index": index,
        "keyframes": [[frame, value, "BEZIER"]],
    }


def _idle_curves(frames: tuple[float, ...] = (0.0,), root_translation: float = 0.0) -> list[dict]:
    curves = [
        _curve('pose.bones["Hips"].location', 0, frames[0], 0.1),
        _curve('pose.bones["Hips"].rotation_euler', 1, frames[0], -0.09),
        _curve('pose.bones["Root"].rotation_euler', 1, frames[0], 0.0),
    ]
    curves.append(_curve('pose.bones["Root"].location', 0, frames[0], root_translation))
    if len(frames) > 1:
        curves.append(_curve('pose.bones["Hips"].location', 1, frames[1], 0.2))
    return curves


def _turn_curves(angle: float, frame: float = 0.0) -> list[dict]:
    curves = _idle_curves()
    return [
        (
            _curve(curve["data_path"], curve["array_index"], frame, curve["keyframes"][0][1] + angle)
            if (curve["data_path"], curve["array_index"]) == ('pose.bones["Root"].rotation_euler', 1)
            else _curve(curve["data_path"], curve["array_index"], frame, curve["keyframes"][0][1])
        )
        for curve in curves
    ]


class IdleInputFailureTests(unittest.TestCase):
    def test_accepts_single_frame_bone_subset_with_zero_root_translation(self) -> None:
        idle = {
            "action_name": "pose_ref_idle",
            "curves": _idle_curves(),
            "rig_bone_names": ["Root", "Hips", "LeftArm_RET"],
        }
        self.assertEqual(idle_input_failures(idle, {}), [])

    def test_rejects_empty_action(self) -> None:
        idle = {"action_name": "pose_ref_idle", "curves": [], "rig_bone_names": ["Root"]}
        failures = idle_input_failures(idle, {})
        self.assertEqual(len(failures), 1)
        self.assertIn("no curves", failures[0])

    def test_rejects_multiple_source_frames(self) -> None:
        idle = {
            "action_name": "pose_ref_idle",
            "curves": _idle_curves(frames=(0.0, 24.0)),
            "rig_bone_names": ["Root", "Hips"],
        }
        failures = idle_input_failures(idle, {})
        self.assertTrue(any("ONE source frame" in failure for failure in failures))

    def test_rejects_bones_absent_from_the_workspace_rig(self) -> None:
        idle = {
            "action_name": "pose_ref_idle",
            "curves": [_curve('pose.bones["Ghost"].location', 0, 0.0, 1.0)],
            "rig_bone_names": ["Root", "Hips"],
        }
        failures = idle_input_failures(idle, {})
        self.assertTrue(any("Ghost" in failure for failure in failures))

    def test_rejects_non_pose_channels(self) -> None:
        idle = {
            "action_name": "pose_ref_idle",
            "curves": [_curve("location", 0, 0.0, 1.0)],
            "rig_bone_names": ["Root"],
        }
        failures = idle_input_failures(idle, {})
        self.assertTrue(any("non-pose channel" in failure for failure in failures))

    def test_rejects_non_finite_samples(self) -> None:
        idle = {
            "action_name": "pose_ref_idle",
            "curves": [
                {
                    "data_path": 'pose.bones["Hips"].location',
                    "array_index": 0,
                    "keyframes": [[0.0, math.inf, "BEZIER"]],
                }
            ],
            "rig_bone_names": ["Root", "Hips"],
        }
        failures = idle_input_failures(idle, {})
        self.assertTrue(any("non-finite" in failure for failure in failures))

    def test_rejects_unexpected_root_translation(self) -> None:
        idle = {
            "action_name": "pose_ref_idle",
            "curves": _idle_curves(root_translation=0.5),
            "rig_bone_names": ["Root", "Hips"],
        }
        failures = idle_input_failures(idle, {})
        self.assertTrue(any("Root translation" in failure for failure in failures))

    def test_tolerates_negligible_root_translation(self) -> None:
        idle = {
            "action_name": "pose_ref_idle",
            "curves": _idle_curves(root_translation=1e-9),
            "rig_bone_names": ["Root", "Hips"],
        }
        self.assertEqual(idle_input_failures(idle, {}), [])


class RotationConventionTests(unittest.TestCase):
    def test_derives_axis_angles_and_frames(self) -> None:
        conventions = derive_rotation_conventions(
            _idle_curves(),
            {
                "pose_ref_turn_45": _turn_curves(-math.pi / 4),
                "pose_ref_turn_180": _turn_curves(math.pi, frame=1.0),
            },
        )
        self.assertEqual(conventions["axis_data_path"], 'pose.bones["Root"].rotation_euler')
        self.assertEqual(conventions["axis_array_index"], 1)
        self.assertEqual(conventions["idle_frame"], 0.0)
        self.assertEqual(conventions["curve_count"], 4)
        self.assertAlmostEqual(conventions["turns"]["pose_ref_turn_45"]["angle"], -math.pi / 4)
        self.assertEqual(conventions["turns"]["pose_ref_turn_45"]["frame"], 0.0)
        self.assertAlmostEqual(conventions["turns"]["pose_ref_turn_180"]["angle"], math.pi)
        self.assertEqual(conventions["turns"]["pose_ref_turn_180"]["frame"], 1.0)

    def test_treats_float_bake_noise_as_the_same_channel(self) -> None:
        def root_frame_curves(root_x: float, root_y: float, root_z: float) -> list[dict]:
            return [
                _curve('pose.bones["Hips"].location', 0, 0.0, 0.1),
                _curve('pose.bones["Hips"].rotation_euler', 1, 0.0, -0.09),
                _curve('pose.bones["Root"].rotation_euler', 0, 0.0, root_x),
                _curve('pose.bones["Root"].rotation_euler', 1, 0.0, root_y),
                _curve('pose.bones["Root"].rotation_euler', 2, 0.0, root_z),
                _curve('pose.bones["Root"].location', 0, 0.0, 0.0),
            ]

        idle = root_frame_curves(0.0, 0.0, 0.0)
        noisy = root_frame_curves(2.4e-07, math.pi, -7.5e-08)
        conventions = derive_rotation_conventions(idle, {"pose_ref_turn_180": noisy})
        self.assertEqual(conventions["axis_array_index"], 1)
        self.assertAlmostEqual(conventions["turns"]["pose_ref_turn_180"]["angle"], math.pi)

    def test_rejects_turn_without_a_differing_channel(self) -> None:
        with self.assertRaises(ValueError) as raised:
            derive_rotation_conventions(_idle_curves(), {"pose_ref_turn_x": _idle_curves()})
        self.assertIn("exactly one channel", str(raised.exception))

    def test_rejects_turns_rotating_different_axes(self) -> None:
        hips_rotated = [
            _curve('pose.bones["Hips"].location', 0, 0.0, 0.1 + 0.25),
            _curve('pose.bones["Hips"].rotation_euler', 1, 0.0, -0.09),
            _curve('pose.bones["Root"].rotation_euler', 1, 0.0, 0.0),
            _curve('pose.bones["Root"].location', 0, 0.0, 0.0),
        ]
        with self.assertRaises(ValueError) as raised:
            derive_rotation_conventions(
                _idle_curves(),
                {"pose_ref_turn_a": _turn_curves(math.pi), "pose_ref_turn_b": hips_rotated},
            )
        self.assertIn("rotate different channels", str(raised.exception))

    def test_rejects_coverage_mismatch(self) -> None:
        partial = _turn_curves(math.pi)[:2]
        with self.assertRaises(ValueError) as raised:
            derive_rotation_conventions(_idle_curves(), {"pose_ref_turn_x": partial})
        self.assertIn("coverage", str(raised.exception))

    def test_rejects_multi_frame_turn(self) -> None:
        spread = _turn_curves(math.pi)
        spread[0] = {
            "data_path": spread[0]["data_path"],
            "array_index": spread[0]["array_index"],
            "keyframes": [[0.0, 0.1, "BEZIER"], [12.0, 0.2, "BEZIER"]],
        }
        with self.assertRaises(ValueError) as raised:
            derive_rotation_conventions(_idle_curves(), {"pose_ref_turn_x": spread})
        self.assertIn("one frame", str(raised.exception))

    def test_rejects_missing_turns(self) -> None:
        with self.assertRaises(ValueError) as raised:
            derive_rotation_conventions(_idle_curves(), {})
        self.assertIn("no pose-reference turn actions", str(raised.exception))


class RotationComparisonTests(unittest.TestCase):
    def test_identical_actions_report_zero_differences(self) -> None:
        comparison = rotation_action_comparison(_turn_curves(math.pi), _turn_curves(math.pi))
        self.assertEqual(comparison["differing_channel_count"], 0)
        self.assertEqual(comparison["max_abs_value_delta"], 0.0)
        self.assertEqual(comparison["max_abs_frame_delta"], 0.0)

    def test_records_value_and_coverage_deltas(self) -> None:
        comparison = rotation_action_comparison(
            _turn_curves(math.pi), _turn_curves(math.pi / 2)[:3]
        )
        self.assertEqual(comparison["differing_channel_count"], 2)
        self.assertGreater(comparison["max_abs_value_delta"], 1.0)
        self.assertEqual(comparison["template_curve_count"], 3)


def _workspace_expectations() -> dict:
    return {
        "setup_object_names": ["Female", "Female.body", "Kimodo_Source"],
        "template_object_names": ["Kimodo_PoseRef_idle", "Kimodo_Waypoint_idle"],
        "setup_mesh_names": ["Female.body"],
        "kimodo_source": {
            "bone_count": 5,
            "bone_collections": {"Retarget Bones": 2},
            "pose_constraint_count": 4,
        },
        "fingerprints": {
            "female_constraint_graph_sha256": "b" * 64,
            "female_constraint_graph_edge_count": 9,
            "kimodo_source_constraint_graph_sha256": "c" * 64,
            "kimodo_source_helper_configuration_sha256": "d" * 64,
            "kimodo_source_helper_bone_count": 2,
        },
        "segments": [
            {"index": 0, "start_frame": 1, "end_frame": 120},
            {"index": 1, "start_frame": 121, "end_frame": 160},
        ],
        "action_placements": {"pose_ref_idle": 0.0, "pose_ref_turn_45": 0.0},
        "reference_curve_count": 4,
        "frame_range": [1, 160],
        "motion_constraints": [
            {"index": 0, "label": "Kimodo_Waypoint_0030", "marker_object": "Kimodo_Waypoint_idle"},
            {"index": 1, "label": "Kimodo_PoseRef_0001", "marker_object": "Kimodo_PoseRef_idle"},
        ],
    }


def _workspace_inventory() -> dict:
    def mesh(name: str, hidden: bool = False) -> dict:
        return {
            "name": name,
            "type": "MESH",
            "hide_viewport": hidden,
            "hide_render": hidden,
            "pose_constraint_count": 0,
        }

    return {
        "datablock_counts": {"objects": 5, "meshes": 1, "armatures": 3, "actions": 2, "materials": 1, "images": 1},
        "objects": [
            mesh("Female.body"),
            {
                "name": "Female",
                "type": "ARMATURE",
                "hide_viewport": False,
                "hide_render": False,
                "pose_constraint_count": 9,
                "bone_count": 55,
                "bone_collections": {"Bones": 55},
            },
            {
                "name": "Kimodo_PoseRef_idle",
                "type": "ARMATURE",
                "hide_viewport": False,
                "hide_render": False,
                "pose_constraint_count": 4,
                "bone_count": 5,
                "bone_collections": {},
            },
            {
                "name": "Kimodo_Source",
                "type": "ARMATURE",
                "hide_viewport": False,
                "hide_render": False,
                "pose_constraint_count": 4,
                "bone_count": 5,
                "bone_collections": {"Retarget Bones": 2},
            },
            {
                "name": "Kimodo_Waypoint_idle",
                "type": "EMPTY",
                "hide_viewport": False,
                "hide_render": False,
                "pose_constraint_count": 0,
            },
        ],
        "actions": [
            {"name": "pose_ref_idle", "frame_range": [0.0, 0.0], "curve_count": 4, "key_frames": [0.0], "users": 2, "fake_user": True},
            {"name": "pose_ref_turn_45", "frame_range": [0.0, 0.0], "curve_count": 4, "key_frames": [0.0], "users": 1, "fake_user": True},
        ],
        "materials": ["Female.body"],
        "images": [{"name": "Render Result", "filepath": "", "file_exists": None}],
        "segments": [
            {"index": 0, "start_frame": 1, "end_frame": 120, "prompt": ""},
            {"index": 1, "start_frame": 121, "end_frame": 160, "prompt": ""},
        ],
        "scene": {"frame_range": [1, 160]},
        "scene_links": {
            "source_armature": "Kimodo_Source",
            "reuse_armature": "Kimodo_Source",
            "motion_constraints": [
                {"index": 0, "constraint_type": 0, "frame": 1, "label": "Kimodo_Waypoint_0030", "marker_object": "Kimodo_Waypoint_idle"},
                {"index": 1, "constraint_type": 1, "frame": 1, "label": "Kimodo_PoseRef_0001", "marker_object": "Kimodo_PoseRef_idle"},
            ],
        },
        "fingerprints": {
            "female_constraint_graph_sha256": "b" * 64,
            "female_constraint_graph_edge_count": 9,
            "kimodo_source_constraint_graph_sha256": "c" * 64,
            "kimodo_source_helper_configuration_sha256": "d" * 64,
            "kimodo_source_helper_bone_count": 2,
        },
    }


class WorkspaceFailureTests(unittest.TestCase):
    def test_accepts_a_conforming_inventory(self) -> None:
        self.assertEqual(workspace_failures(_workspace_inventory(), _workspace_expectations()), [])

    def test_flags_unexpected_object(self) -> None:
        inventory = _workspace_inventory()
        inventory["objects"].append(
            {"name": "Cube", "type": "MESH", "hide_viewport": False, "hide_render": False, "pose_constraint_count": 0}
        )
        failures = workspace_failures(inventory, _workspace_expectations())
        self.assertTrue(any("object set mismatch" in failure for failure in failures))

    def test_flags_hidden_character_mesh(self) -> None:
        inventory = _workspace_inventory()
        inventory["objects"][0]["hide_viewport"] = True
        failures = workspace_failures(inventory, _workspace_expectations())
        self.assertTrue(any("must stay visible" in failure for failure in failures))

    def test_flags_source_rig_structure_drift(self) -> None:
        inventory = _workspace_inventory()
        record = next(item for item in inventory["objects"] if item["name"] == "Kimodo_Source")
        record["bone_count"] = 4
        failures = workspace_failures(inventory, _workspace_expectations())
        self.assertTrue(any("bone count 4" in failure for failure in failures))

    def test_flags_variant_and_leftover_actions(self) -> None:
        inventory = _workspace_inventory()
        inventory["actions"].append(
            {"name": "loc_ember_wf_r_loop", "frame_range": [1.0, 2.0], "curve_count": 1, "key_frames": [1.0], "users": 0, "fake_user": True}
        )
        failures = workspace_failures(inventory, _workspace_expectations())
        self.assertTrue(any("variant leftovers" in failure for failure in failures))

    def test_flags_wrong_action_placement(self) -> None:
        inventory = _workspace_inventory()
        inventory["actions"][0]["key_frames"] = [7.0]
        failures = workspace_failures(inventory, _workspace_expectations())
        self.assertTrue(any("key placement" in failure for failure in failures))

    def test_flags_lost_fake_user(self) -> None:
        inventory = _workspace_inventory()
        inventory["actions"][1]["fake_user"] = False
        failures = workspace_failures(inventory, _workspace_expectations())
        self.assertTrue(any("fake user" in failure for failure in failures))

    def test_flags_non_blank_segment_prompt(self) -> None:
        inventory = _workspace_inventory()
        inventory["segments"][1]["prompt"] = "a woman walks"
        failures = workspace_failures(inventory, _workspace_expectations())
        self.assertTrue(any("prompt must be blank" in failure for failure in failures))

    def test_flags_segment_range_drift(self) -> None:
        inventory = _workspace_inventory()
        inventory["segments"][0]["end_frame"] = 119
        failures = workspace_failures(inventory, _workspace_expectations())
        self.assertTrue(any("frame range" in failure and "drifted" in failure for failure in failures))

    def test_flags_frame_range_not_encompassing_segments(self) -> None:
        inventory = _workspace_inventory()
        inventory["scene"]["frame_range"] = [1, 34]
        failures = workspace_failures(inventory, _workspace_expectations())
        self.assertTrue(any("encompass the segment extents" in failure for failure in failures))

    def test_flags_dangling_scene_links(self) -> None:
        inventory = _workspace_inventory()
        inventory["scene_links"]["source_armature"] = None
        inventory["scene_links"]["motion_constraints"][1]["marker_object"] = None
        failures = workspace_failures(inventory, _workspace_expectations())
        self.assertTrue(any("source_armature" in failure for failure in failures))
        self.assertTrue(any("marker_object is dangling" in failure for failure in failures))

    def test_flags_marker_rebound_to_the_wrong_object(self) -> None:
        inventory = _workspace_inventory()
        inventory["scene_links"]["motion_constraints"][0]["marker_object"] = "Kimodo_PoseRef_idle"
        failures = workspace_failures(inventory, _workspace_expectations())
        self.assertTrue(any("instead of" in failure for failure in failures))

    def test_flags_fingerprint_drift(self) -> None:
        inventory = _workspace_inventory()
        inventory["fingerprints"]["female_constraint_graph_sha256"] = "0" * 64
        failures = workspace_failures(inventory, _workspace_expectations())
        self.assertTrue(any("female_constraint_graph_sha256" in failure for failure in failures))

    def test_flags_relative_image_path(self) -> None:
        inventory = _workspace_inventory()
        inventory["images"].append(
            {"name": "texture.png", "filepath": "//../texture.png", "file_exists": False}
        )
        failures = workspace_failures(inventory, _workspace_expectations())
        self.assertTrue(any("must be absolute" in failure for failure in failures))


def _workspace_report() -> dict:
    digest = "e" * 64
    return {
        "schema": WORKSPACE_REPORT_SCHEMA,
        "generated_by": "kimodo_pipeline/workspace_bootstrap.py",
        "blender_version": "5.2.0",
        "variant": "ember",
        "character_asset_root": "/somewhere/female",
        "inputs": {
            key: {"path": f"/somewhere/{key}.blend", "sha256_before": digest, "sha256_after": digest, "unchanged": True}
            for key in ("canonical_source", "neutral_template", "character_setup", "idle_blend")
        },
        "baseline": {
            key: {"path": f"/somewhere/{key}.blend", "sha256": digest, "matches_gate1": True}
            for key in ("canonical_source", "neutral_template", "character_setup")
        },
        "workspace": {
            "destination": "/somewhere/locomotion_ember_authoring.blend",
            "staged": "/tmp/workspace.blend",
            "published": True,
            "replaced_existing": False,
            "sha256": "f" * 64,
        },
        "composition": {
            "merged_objects": ["Kimodo_Waypoint_idle"],
            "appended_collections": {"Waypoints": ["Kimodo_Waypoint_idle"]},
            "idle": {"validation": {"passed": True, "failures": []}, "canonical_frame": 0.0},
            "rotation_generation": {"turns": {}},
            "segments": [],
            "motion_constraints_rebind": [],
            "scene_links": {"source_armature": "Kimodo_Source"},
            "frame_range": {"chosen": [1, 160]},
            "fingerprints": {},
        },
        "validation": {"passed": True, "failures": []},
        "inventory": _workspace_inventory(),
    }


class WorkspaceReportStructureTests(unittest.TestCase):
    def test_accepts_a_conforming_report(self) -> None:
        self.assertEqual(workspace_report_structure_failures(_workspace_report()), [])

    def test_flags_missing_fields(self) -> None:
        report = _workspace_report()
        del report["composition"]
        del report["baseline"]
        failures = workspace_report_structure_failures(report)
        self.assertTrue(any("'composition'" in failure for failure in failures))
        self.assertTrue(any("'baseline'" in failure for failure in failures))

    def test_flags_changed_input_bytes(self) -> None:
        report = _workspace_report()
        report["inputs"]["idle_blend"]["sha256_after"] = "0" * 64
        report["inputs"]["idle_blend"]["unchanged"] = False
        failures = workspace_report_structure_failures(report)
        self.assertTrue(any("idle_blend" in failure for failure in failures))

    def test_flags_baseline_drift_attestation(self) -> None:
        report = _workspace_report()
        report["baseline"]["neutral_template"]["matches_gate1"] = False
        failures = workspace_report_structure_failures(report)
        self.assertTrue(any("Gate 1" in failure for failure in failures))

    def test_flags_failed_validation_and_missing_digest(self) -> None:
        report = _workspace_report()
        report["validation"] = {"passed": False, "failures": ["mismatch"]}
        report["workspace"]["sha256"] = ""
        failures = workspace_report_structure_failures(report)
        self.assertTrue(any("attest a passed validation" in failure for failure in failures))
        self.assertTrue(any("no workspace digest" in failure for failure in failures))


if __name__ == "__main__":
    unittest.main()
