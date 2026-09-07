"""Pure classification-policy and fingerprint coverage for the bootstrap manifest."""
from __future__ import annotations

import unittest

from kimodo_pipeline.bootstrap_policy import (
    classify_action,
    classify_material,
    classify_object,
    constraint_graph_fingerprint,
    helper_configuration_fingerprint,
    partition_clean_rig,
)


def _edge(bone: str, influence: float = 1.0, muted: bool = False) -> list[object]:
    return [bone, 0, "COPY_ROTATION", "Kimodo_Source", f"{bone}_RET", "WORLD", "WORLD", muted, influence]


def _helper(name: str, translation: float = 0.0) -> list[object]:
    return [name, None, [round(translation + value, 7) for value in (0.0, 1.0, 0.0, 0.0)] * 4]


class ClassificationPolicyTests(unittest.TestCase):
    def test_character_setup_objects(self) -> None:
        for name, kind in (
            ("Female", "ARMATURE"),
            ("Female.body", "MESH"),
            ("Female.wolgade_female_top_01", "MESH"),
            ("Kimodo_Source", "ARMATURE"),
        ):
            decision = classify_object({"name": name, "type": kind})
            self.assertEqual(decision.classification, "character_retarget_setup", name)
            self.assertTrue(decision.rationale)
            self.assertEqual(decision.confidence, "high")

    def test_template_objects(self) -> None:
        for name in (
            "Kimodo_IK",
            "Kimodo_PoseRef_idle",
            "Kimodo_PoseRef_turn_45",
            "Kimodo_Waypoint_arc",
            "Kimodo_Waypoint_walk_forawrd",
        ):
            decision = classify_object({"name": name, "type": "ARMATURE"})
            self.assertEqual(decision.classification, "locomotion_template", name)

    def test_unmatched_objects_default_to_discard_with_low_confidence(self) -> None:
        decision = classify_object({"name": "Mystery_Rig", "type": "LATTICE"})
        self.assertEqual(decision.classification, "variant_discard")
        self.assertEqual(decision.confidence, "low")

    def test_actions(self) -> None:
        expected = {
            "pose_ref_idle": "locomotion_template",
            "pose_ref_turn_45": "locomotion_template",
            "pose_ref_turn_180": "locomotion_template",
            "kimodo_multi_388sjrhc": "variant_discard",
            "kimodo_multi_repair_sl_sr_c4c66b7df1ac": "variant_discard",
            "loc_ember_wf_r_loop": "variant_discard",
            "loc_ember_i2f_l": "variant_discard",
        }
        for name, classification in expected.items():
            self.assertEqual(classify_action({"name": name}).classification, classification, name)

    def test_material_actions_and_unknowns_default_to_discard(self) -> None:
        self.assertEqual(
            classify_material({"name": "Female.body"}).classification, "character_retarget_setup"
        )
        self.assertEqual(classify_material({"name": "Scratch"}).confidence, "low")
        self.assertEqual(classify_action({"name": "scratch"}).confidence, "low")
        self.assertEqual(classify_action({"name": "loc_"}).classification, "variant_discard")

    def test_every_decision_names_a_known_classification(self) -> None:
        from kimodo_pipeline.bootstrap_policy import CLASSIFICATIONS

        decisions = [
            classify_object({"name": "Female", "type": "ARMATURE"}),
            classify_object({"name": "Kimodo_IK", "type": "ARMATURE"}),
            classify_object({"name": "?", "type": "EMPTY"}),
            classify_action({"name": "pose_ref_idle"}),
            classify_action({"name": "kimodo_multi_x"}),
            classify_action({"name": "?"}),
            classify_material({"name": "Female.body"}),
            classify_material({"name": "?"}),
        ]
        self.assertTrue(all(decision.classification in CLASSIFICATIONS for decision in decisions))


class CleanRigPartitionTests(unittest.TestCase):
    def test_partitions_bones_into_additions_and_clean_rig(self) -> None:
        bones = ["Root", "spine_01", "upperarm_l", "upperarm_l_RET", "LeftForeArm_LOOK", "head_RET"]
        additions = ["upperarm_l_RET", "LeftForeArm_LOOK", "head_RET"]
        partition = partition_clean_rig(bones, additions)
        self.assertEqual(partition["total_bone_count"], 6)
        self.assertEqual(partition["retarget_addition_bones"], sorted(additions))
        self.assertEqual(partition["clean_bones"], ["Root", "spine_01", "upperarm_l"])
        self.assertEqual(partition["clean_bone_count"], 3)
        self.assertEqual(partition["ret_helper_bones"], ["head_RET", "upperarm_l_RET"])
        self.assertEqual(partition["look_target_bones"], ["LeftForeArm_LOOK"])

    def test_rejects_additions_outside_the_armature(self) -> None:
        with self.assertRaises(ValueError):
            partition_clean_rig(["Root"], ["Ghost_RET"])


class FingerprintTests(unittest.TestCase):
    def test_constraint_graph_fingerprint_is_stable(self) -> None:
        edges = [_edge("Root"), _edge("pelvis", influence=0.5), _edge("spine_01", muted=True)]
        self.assertEqual(constraint_graph_fingerprint(edges), constraint_graph_fingerprint(edges))
        self.assertEqual(constraint_graph_fingerprint([]), constraint_graph_fingerprint([]))

    def test_constraint_graph_fingerprint_reacts_to_structure(self) -> None:
        base = [_edge("Root"), _edge("pelvis")]
        self.assertNotEqual(
            constraint_graph_fingerprint(base),
            constraint_graph_fingerprint([_edge("pelvis"), _edge("Root")]),
            "Edge order is part of the constraint graph identity.",
        )
        self.assertNotEqual(
            constraint_graph_fingerprint(base),
            constraint_graph_fingerprint([_edge("Root"), _edge("pelvis", influence=0.9999999)]),
        )
        self.assertNotEqual(
            constraint_graph_fingerprint(base),
            constraint_graph_fingerprint([_edge("Root"), _edge("pelvis", muted=True)]),
        )

    def test_helper_configuration_fingerprint_normalises_signed_zero(self) -> None:
        first = [_helper("head_RET", translation=0.0)]
        second = [[
            "head_RET",
            None,
            [value + 0.0 for value in [-0.0, 1.0, -0.0, -0.0]] * 4,
        ]]
        self.assertEqual(
            helper_configuration_fingerprint(first),
            helper_configuration_fingerprint(second),
        )

    def test_helper_configuration_fingerprint_reacts_to_rest_matrix(self) -> None:
        self.assertNotEqual(
            helper_configuration_fingerprint([_helper("head_RET")]),
            helper_configuration_fingerprint([_helper("head_RET", translation=0.001)]),
        )

    def test_fingerprints_reject_non_finite_values(self) -> None:
        with self.assertRaises(ValueError):
            constraint_graph_fingerprint([["Root", 0, "COPY_LOCATION", None, None, None, None, False, float("nan")]])


if __name__ == "__main__":
    unittest.main()
