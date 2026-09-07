from __future__ import annotations

import unittest

from tools import character_template_bone_bindings as bone_bindings


CONSISTENT_SCENE = """\
[gd_scene format=3]

[sub_resource type="JointLimitationCone3D" id="JointLimitationCone3D_55bxu"]
angle = 0.87266463

[node name="Female" instance=ExtResource("1_5molt")]

[node name="Head" type="BoneAttachment3D" parent="Female/GeneralSkeleton" index="9"]
transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 1.4594107, 0.04187462)
bone_name = "Head"
bone_idx = 48

[node name="RightHand" type="BoneAttachment3D" parent="Female/GeneralSkeleton" index="10"]
bone_name = "RightHand"
bone_idx = 30

[node name="NeckSpineIK" type="CCDIK3D" parent="Female/GeneralSkeleton" index="13"]
setting_count = 1
settings/0/root_bone_name = "Spine"
settings/0/root_bone = 2
settings/0/end_bone_name = "Head"
settings/0/end_bone = 48

[node name="RightArmTwoBoneIKController" type="TwoBoneIK3D" parent="Female/GeneralSkeleton" index="18"]
settings/0/root_bone_name = "RightUpperArm"
settings/0/root_bone = 28
settings/0/middle_bone_name = "RightLowerArm"
settings/0/middle_bone = 29
settings/0/end_bone_name = "RightHand"
settings/0/end_bone = 30
settings/0/pole_direction = 0
settings/0/use_virtual_end = false
settings/0/extend_end_bone = false

[node name="HeadCopyRotation" type="CopyTransformModifier3D" parent="Female/GeneralSkeleton" index="14"]
settings/0/apply_bone_name = "Head"
settings/0/apply_bone = 48

[node name="RightForearmTwistCopy" type="CopyTransformModifier3D" parent="Female/GeneralSkeleton" index="15"]
settings/0/apply_bone_name = "RightForearmTwist"
settings/0/apply_bone = 46
"""

STALE_SCENE = CONSISTENT_SCENE.replace(
    '[node name="Head" type="BoneAttachment3D" parent="Female/GeneralSkeleton" index="9"]\n'
    "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 1.4594107, 0.04187462)\n"
    'bone_name = "Head"\n'
    "bone_idx = 48",
    '[node name="Head" type="BoneAttachment3D" parent="Female/GeneralSkeleton" index="9"]\n'
    "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 1.4594107, 0.04187462)\n"
    'bone_name = "Head"\n'
    "bone_idx = 46",
)


class CharacterTemplateBoneBindingValidationTests(unittest.TestCase):
    def test_consistent_bindings_validate_without_errors(self) -> None:
        bindings = bone_bindings.parse_scene_bone_bindings(CONSISTENT_SCENE)

        self.assertEqual([], bone_bindings.validate_bone_attachment_bindings(bindings))

    def test_stored_attachment_index_mismatch_is_reported_with_owner_bone(self) -> None:
        bindings = bone_bindings.parse_scene_bone_bindings(STALE_SCENE)

        errors = bone_bindings.validate_bone_attachment_bindings(bindings)

        self.assertEqual(1, len(errors))
        self.assertIn("BoneAttachment3D 'Head'", errors[0])
        self.assertIn("bone_idx 46", errors[0])
        self.assertIn("map 'Head' to 48", errors[0])
        self.assertIn("'RightForearmTwist'", errors[0])

    def test_refresh_rewrites_only_the_stale_bone_idx_line(self) -> None:
        fixed_text, changes = bone_bindings.refresh_bone_attachment_binding_indices(STALE_SCENE)

        original_lines = STALE_SCENE.splitlines()
        fixed_lines = fixed_text.splitlines()
        self.assertEqual(len(original_lines), len(fixed_lines))
        changed = [
            (index, original, fixed)
            for index, (original, fixed) in enumerate(zip(original_lines, fixed_lines))
            if original != fixed
        ]
        self.assertEqual(
            [(10, "bone_idx = 46", "bone_idx = 48")],
            changed,
        )
        self.assertEqual(1, len(changes))
        self.assertIn("bone_idx 46 -> 48", changes[0])
        self.assertEqual(
            [],
            bone_bindings.validate_bone_attachment_bindings(bone_bindings.parse_scene_bone_bindings(fixed_text)),
        )

    def test_attachment_without_reference_pair_is_reported(self) -> None:
        scene = CONSISTENT_SCENE.replace(
            '[node name="RightHand" type="BoneAttachment3D" parent="Female/GeneralSkeleton" index="10"]\n'
            'bone_name = "RightHand"\n'
            "bone_idx = 30",
            '[node name="RightHand" type="BoneAttachment3D" parent="Female/GeneralSkeleton" index="10"]\n'
            'bone_name = "RightFoot"\n'
            "bone_idx = 55",
        )

        errors = bone_bindings.validate_bone_attachment_bindings(bone_bindings.parse_scene_bone_bindings(scene))

        self.assertEqual(1, len(errors))
        self.assertIn("BoneAttachment3D 'RightHand'", errors[0])
        self.assertIn("no refreshed", errors[0])
        self.assertIn("cannot be verified textually", errors[0])

    def test_inconsistent_modifier_pairs_are_reported(self) -> None:
        scene = CONSISTENT_SCENE.replace('settings/0/apply_bone = 48', 'settings/0/apply_bone = 46')

        errors = bone_bindings.validate_bone_attachment_bindings(bone_bindings.parse_scene_bone_bindings(scene))

        self.assertEqual(2, len(errors))
        self.assertIn("disagree on bone 'Head'", errors[0])
        self.assertIn("both 48 and 46", errors[0])
        # The disagreement also invalidates the attachment that referenced the bone.
        self.assertIn("BoneAttachment3D 'Head'", errors[1])

    def test_missing_bone_idx_serialisation_is_reported(self) -> None:
        scene = CONSISTENT_SCENE.replace("bone_idx = 30\n", "", 1)

        errors = bone_bindings.validate_bone_attachment_bindings(bone_bindings.parse_scene_bone_bindings(scene))

        self.assertEqual(1, len(errors))
        self.assertIn("BoneAttachment3D 'RightHand'", errors[0])
        self.assertIn("does not serialise bone_idx", errors[0])

    def test_scopes_are_checked_per_parent_path(self) -> None:
        scene = CONSISTENT_SCENE + (
            '[node name="OtherHand" type="BoneAttachment3D" parent="Other/Skeleton" index="0"]\n'
            'bone_name = "RightHand"\n'
            "bone_idx = 30\n"
        )

        errors = bone_bindings.validate_bone_attachment_bindings(bone_bindings.parse_scene_bone_bindings(scene))

        self.assertEqual(1, len(errors))
        self.assertIn("parent 'Other/Skeleton'", errors[0])


if __name__ == "__main__":
    unittest.main()
