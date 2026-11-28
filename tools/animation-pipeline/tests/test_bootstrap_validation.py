"""Pure rule, prompt-blanking, and report-structure coverage for bootstrap extraction."""
from __future__ import annotations

import unittest

from kimodo_pipeline.bootstrap_validation import (
    blank_segment_prompts,
    canonical_source_failures,
    character_setup_failures,
    expectations_from_manifest,
    neutral_template_failures,
    report_structure_failures,
)


def _manifest_fixture() -> dict:
    return {
        "source_blend_sha256": "a" * 64,
        "classification_summary": {
            "objects": {
                "Female": "character_retarget_setup",
                "Female.body": "character_retarget_setup",
                "Kimodo_Source": "character_retarget_setup",
                "Kimodo_IK": "locomotion_template",
                "Kimodo_PoseRef_idle": "locomotion_template",
                "Kimodo_Waypoint_idle": "locomotion_template",
            }
        },
        "actions": [
            {"name": "pose_ref_idle", "frame_range": [0.0, 0.0], "classification": "locomotion_template"},
            {"name": "pose_ref_turn_180", "frame_range": [1.0, 1.0], "classification": "locomotion_template"},
            {"name": "kimodo_multi_x", "frame_range": [1.0, 9.0], "classification": "variant_discard"},
        ],
        "materials": [
            {"name": "Female.body", "classification": "character_retarget_setup"},
            {"name": "Female.high-poly", "classification": "character_retarget_setup"},
        ],
        "scene": {
            "segments": [
                {"index": 0, "start_frame": 1, "end_frame": 120, "prompt": "a woman walks"},
                {"index": 1, "start_frame": 121, "end_frame": 160, "prompt": "a woman steps"},
            ]
        },
        "clean_rig_subset": {
            "clean_bones": ["Hips", "LeftArm", "Root"],
            "clean_bone_count": 3,
            "retarget_addition_bones": ["LeftArm_RET", "Root_RET"],
            "retarget_addition_count": 2,
            "total_bone_count": 5,
        },
        "fingerprints": {
            "female_constraint_graph_sha256": "b" * 64,
            "female_constraint_graph_edge_count": 55,
            "kimodo_source_constraint_graph_sha256": "c" * 64,
            "kimodo_source_helper_configuration_sha256": "d" * 64,
            "kimodo_source_helper_bone_count": 53,
        },
    }


def _canonical_inventory(expectations: dict) -> dict:
    return {
        "datablock_counts": {"objects": 1, "meshes": 0, "armatures": 1, "actions": 0, "materials": 0, "images": 3},
        "objects": [
            {
                "name": "Kimodo_Source",
                "type": "ARMATURE",
                "bone_count": expectations["clean_bone_count"],
                "bone_collections": {},
                "pose_constraint_count": 0,
                "ret_helper_bone_count": 0,
            }
        ],
        "actions": [],
        "materials": [],
        "images": [{"name": "Render Result", "filepath": "", "file_exists": None}],
        "segments": [],
        "bone_names": list(expectations["clean_bones"]),
    }


def _template_inventory(expectations: dict) -> dict:
    return {
        "datablock_counts": {"objects": 3, "meshes": 0, "armatures": 3, "actions": 2, "materials": 0, "images": 3},
        "objects": [
            {"name": "Kimodo_IK", "type": "ARMATURE", "pose_constraint_count": 11},
            {"name": "Kimodo_PoseRef_idle", "type": "ARMATURE", "pose_constraint_count": 4},
            {"name": "Kimodo_Waypoint_idle", "type": "EMPTY", "pose_constraint_count": 0},
        ],
        "actions": [
            {"name": "pose_ref_idle", "frame_range": [0.0, 0.0], "users": 2, "fake_user": True},
            {"name": "pose_ref_turn_180", "frame_range": [1.0, 1.0], "users": 1, "fake_user": True},
        ],
        "materials": [],
        "images": [{"name": "Render Result", "filepath": "", "file_exists": None}],
        "segments": [
            {"index": 0, "start_frame": 1, "end_frame": 120, "prompt": ""},
            {"index": 1, "start_frame": 121, "end_frame": 160, "prompt": ""},
        ],
    }


def _setup_inventory(expectations: dict) -> dict:
    return {
        "datablock_counts": {"objects": 3, "meshes": 1, "armatures": 2, "actions": 0, "materials": 2, "images": 3},
        "objects": [
            {
                "name": "Female",
                "type": "ARMATURE",
                "bone_count": 55,
                "bone_collections": {"Bones": 55},
                "pose_constraint_count": 55,
                "ret_helper_bone_count": 0,
            },
            {"name": "Female.body", "type": "MESH", "pose_constraint_count": 0},
            {
                "name": "Kimodo_Source",
                "type": "ARMATURE",
                "bone_count": expectations["total_bone_count"],
                "bone_collections": {"Retarget Bones": expectations["retarget_addition_count"]},
                "pose_constraint_count": 4,
                "ret_helper_bone_count": expectations["kimodo_source_helper_bone_count"],
            },
        ],
        "actions": [],
        "materials": ["Female.body", "Female.high-poly"],
        "images": [{"name": "Render Result", "filepath": "", "file_exists": None}],
        "segments": [],
        "fingerprints": {
            "female_constraint_graph_sha256": expectations["female_constraint_graph_sha256"],
            "female_constraint_graph_edge_count": expectations["female_constraint_graph_edge_count"],
            "kimodo_source_constraint_graph_sha256": expectations[
                "kimodo_source_constraint_graph_sha256"
            ],
            "kimodo_source_helper_configuration_sha256": expectations[
                "kimodo_source_helper_configuration_sha256"
            ],
        },
    }


class ExpectationDerivationTests(unittest.TestCase):
    def test_derives_expectations_from_classification(self) -> None:
        expectations = expectations_from_manifest(_manifest_fixture())
        self.assertEqual(
            expectations["template_object_names"],
            ["Kimodo_IK", "Kimodo_PoseRef_idle", "Kimodo_Waypoint_idle"],
        )
        self.assertEqual(
            expectations["setup_object_names"], ["Female", "Female.body", "Kimodo_Source"]
        )
        self.assertEqual(
            expectations["template_action_frame_ranges"],
            {"pose_ref_idle": [0.0, 0.0], "pose_ref_turn_180": [1.0, 1.0]},
        )
        self.assertEqual(expectations["setup_material_names"], ["Female.body", "Female.high-poly"])
        self.assertEqual(expectations["clean_bone_count"], 3)
        self.assertEqual(expectations["source_blend_sha256"], "a" * 64)

    def test_rejects_incomplete_manifests(self) -> None:
        manifest = _manifest_fixture()
        del manifest["fingerprints"]
        with self.assertRaises(ValueError):
            expectations_from_manifest(manifest)
        with self.assertRaises(ValueError):
            expectations_from_manifest({"classification_summary": {"objects": {}}})


class PromptBlankingTests(unittest.TestCase):
    def test_blanks_prompts_and_records_provenance(self) -> None:
        blanked, provenance = blank_segment_prompts(
            [
                {"index": 0, "start_frame": 1, "end_frame": 120, "prompt": "a woman walks"},
                {"index": 1, "start_frame": 121, "end_frame": 160, "prompt": ""},
            ]
        )
        self.assertEqual([segment["prompt"] for segment in blanked], ["", ""])
        self.assertEqual(blanked[0]["start_frame"], 1)
        self.assertEqual(
            provenance,
            [
                {
                    "index": 0,
                    "start_frame": 1,
                    "end_frame": 120,
                    "original_prompt": "a woman walks",
                },
                {"index": 1, "start_frame": 121, "end_frame": 160, "original_prompt": ""},
            ],
        )


class CanonicalSourceRuleTests(unittest.TestCase):
    def setUp(self) -> None:
        self.expectations = expectations_from_manifest(_manifest_fixture())

    def test_passes_on_contract_inventory(self) -> None:
        self.assertEqual(canonical_source_failures(_canonical_inventory(self.expectations), self.expectations), [])

    def test_rejects_bone_set_drift(self) -> None:
        inventory = _canonical_inventory(self.expectations)
        inventory["bone_names"] = ["Hips", "LeftArm"]
        failures = canonical_source_failures(inventory, self.expectations)
        self.assertTrue(any("clean bone set mismatch" in failure for failure in failures))

    def test_rejects_leftover_retarget_collection(self) -> None:
        inventory = _canonical_inventory(self.expectations)
        inventory["objects"][0]["bone_collections"] = {"Retarget Bones": 2}
        failures = canonical_source_failures(inventory, self.expectations)
        self.assertTrue(any("Retarget Bones" in failure for failure in failures))

    def test_rejects_constraints_actions_segments_and_meshes(self) -> None:
        for mutation in (
            {"objects": [{"name": "Kimodo_Source", "type": "ARMATURE", "pose_constraint_count": 1}]},
            {"actions": [{"name": "stray", "frame_range": [0, 0], "users": 1, "fake_user": True}]},
            {"segments": [{"index": 0, "start_frame": 1, "end_frame": 2, "prompt": ""}]},
            {"materials": ["Female.body"]},
        ):
            inventory = _canonical_inventory(self.expectations)
            inventory.update(mutation)
            if "objects" in mutation:
                inventory["bone_names"] = list(self.expectations["clean_bones"])
            self.assertTrue(
                canonical_source_failures(inventory, self.expectations),
                f"expected failures for mutation {mutation}",
            )

    def test_rejects_images_whose_paths_do_not_resolve(self) -> None:
        broken = {
            "name": "gone.png",
            "filepath": "/definitely/gone.png",
            "file_exists": False,
        }
        relative = {
            "name": "monochrome_studio_04_1k.exr",
            "filepath": "//../../../../hdri/monochrome_studio_04_1k.exr",
            "file_exists": False,
        }
        inventories = {
            canonical_source_failures: _canonical_inventory(self.expectations),
            neutral_template_failures: _template_inventory(self.expectations),
            character_setup_failures: _setup_inventory(self.expectations),
        }
        for rule, inventory in inventories.items():
            inventory["images"].extend([broken, relative])
            failures = rule(inventory, self.expectations)
            self.assertTrue(any("does not resolve" in failure for failure in failures), rule.__name__)
            self.assertTrue(any("must be absolute" in failure for failure in failures), rule.__name__)


class NeutralTemplateRuleTests(unittest.TestCase):
    def setUp(self) -> None:
        self.expectations = expectations_from_manifest(_manifest_fixture())

    def test_passes_on_contract_inventory(self) -> None:
        self.assertEqual(neutral_template_failures(_template_inventory(self.expectations), self.expectations), [])

    def test_rejects_object_set_and_character_leak(self) -> None:
        inventory = _template_inventory(self.expectations)
        inventory["objects"].append(
            {"name": "Kimodo_Source", "type": "ARMATURE", "pose_constraint_count": 0}
        )
        failures = neutral_template_failures(inventory, self.expectations)
        self.assertTrue(any("object set mismatch" in failure for failure in failures))
        self.assertTrue(any("Kimodo_Source must be absent" in failure for failure in failures))

    def test_rejects_action_user_and_frame_drift(self) -> None:
        inventory = _template_inventory(self.expectations)
        inventory["actions"][0]["users"] = 0
        inventory["actions"][1]["frame_range"] = [2.0, 2.0]
        failures = neutral_template_failures(inventory, self.expectations)
        self.assertTrue(any("lost all users" in failure for failure in failures))
        self.assertTrue(any("frame placement" in failure for failure in failures))

    def test_rejects_unblanked_prompt_and_range_drift(self) -> None:
        inventory = _template_inventory(self.expectations)
        inventory["segments"][0]["prompt"] = "a woman walks"
        inventory["segments"][1]["end_frame"] = 999
        failures = neutral_template_failures(inventory, self.expectations)
        self.assertTrue(any("prompt must be blank" in failure for failure in failures))
        self.assertTrue(any("frame range" in failure for failure in failures))

    def test_rejects_discard_actions(self) -> None:
        inventory = _template_inventory(self.expectations)
        inventory["actions"].append(
            {"name": "kimodo_multi_x", "frame_range": [1.0, 9.0], "users": 1, "fake_user": True}
        )
        self.assertTrue(neutral_template_failures(inventory, self.expectations))


class CharacterSetupRuleTests(unittest.TestCase):
    def setUp(self) -> None:
        self.expectations = expectations_from_manifest(_manifest_fixture())

    def test_passes_on_contract_inventory(self) -> None:
        self.assertEqual(character_setup_failures(_setup_inventory(self.expectations), self.expectations), [])

    def test_rejects_fingerprint_drift(self) -> None:
        inventory = _setup_inventory(self.expectations)
        inventory["fingerprints"]["female_constraint_graph_sha256"] = "0" * 64
        failures = character_setup_failures(inventory, self.expectations)
        self.assertTrue(any("female_constraint_graph_sha256" in failure for failure in failures))

    def test_rejects_bone_and_collection_drift(self) -> None:
        inventory = _setup_inventory(self.expectations)
        inventory["objects"][2]["bone_count"] = 100
        inventory["objects"][2]["bone_collections"] = {"Retarget Bones": 40}
        failures = character_setup_failures(inventory, self.expectations)
        self.assertTrue(any("bone count" in failure for failure in failures))
        self.assertTrue(any("membership" in failure for failure in failures))

    def test_rejects_template_leak_and_actions(self) -> None:
        inventory = _setup_inventory(self.expectations)
        inventory["objects"].append(
            {"name": "Kimodo_Waypoint_idle", "type": "EMPTY", "pose_constraint_count": 0}
        )
        inventory["actions"] = [
            {"name": "pose_ref_idle", "frame_range": [0.0, 0.0], "users": 1, "fake_user": True}
        ]
        failures = character_setup_failures(inventory, self.expectations)
        self.assertTrue(any("leaked" in failure for failure in failures))
        self.assertTrue(any("no actions" in failure for failure in failures))

    def test_rejects_material_drift(self) -> None:
        inventory = _setup_inventory(self.expectations)
        inventory["materials"] = ["Female.body"]
        self.assertTrue(character_setup_failures(inventory, self.expectations))


def _report_fixture() -> dict:
    expectations = expectations_from_manifest(_manifest_fixture())
    artefacts = {}
    for key, inventory in (
        ("canonical_source", _canonical_inventory(expectations)),
        ("neutral_template", _template_inventory(expectations)),
        ("character_setup", _setup_inventory(expectations)),
    ):
        artefacts[key] = {
            "destination": f"/dest/{key}.blend",
            "staged": f"/staged/{key}.blend",
            "published": True,
            "replaced_existing": False,
            "sha256": "e" * 64,
            "validation": {"passed": True, "failures": []},
            "inventory": inventory,
        }
    return {
        "schema": "kimodo-bootstrap-extraction/1",
        "generated_by": "kimodo_pipeline/extract_bootstrap.py",
        "blender_version": "5.2.0",
        "source_blend": "/source/fixture.blend",
        "source_blend_sha256_before": "a" * 64,
        "source_blend_sha256_after": "a" * 64,
        "manifest_path": "/temp/manifest.json",
        "variant": "ember",
        "character_asset_root": "/characters/female",
        "artefacts": artefacts,
        "template_blank_prompts": [],
        "dangling_by_design": {"neutral_template": [], "character_setup": []},
        "monolith_unchanged": True,
    }


class ReportStructureTests(unittest.TestCase):
    def test_accepts_a_contract_report(self) -> None:
        self.assertEqual(report_structure_failures(_report_fixture()), [])

    def test_rejects_missing_fields_and_sections(self) -> None:
        report = _report_fixture()
        del report["template_blank_prompts"]
        del report["artefacts"]["canonical_source"]
        failures = report_structure_failures(report)
        self.assertTrue(any("template_blank_prompts" in failure for failure in failures))
        self.assertTrue(any("artefacts" in failure for failure in failures))

    def test_rejects_disagreeing_validation_flags_and_changed_monolith(self) -> None:
        report = _report_fixture()
        report["artefacts"]["neutral_template"]["validation"] = {
            "passed": True,
            "failures": ["a failure"],
        }
        report["source_blend_sha256_after"] = "f" * 64
        failures = report_structure_failures(report)
        self.assertTrue(any("disagree" in failure for failure in failures))
        self.assertTrue(any("monolith digest changed" in failure for failure in failures))
        self.assertTrue(any("does not attest" in failure for failure in failures))


if __name__ == "__main__":
    unittest.main()
