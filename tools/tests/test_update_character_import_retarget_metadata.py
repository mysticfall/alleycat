from __future__ import annotations

import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

from tools import update_character_import_retarget_metadata as import_metadata


BASE_IMPORT = """[remap]

importer="scene"
uid="uid://kept"
path="res://.godot/imported/kept.scn"

[deps]

source_file="res://assets/characters/sample/sample.colliders.blend"
dest_files=["res://.godot/imported/kept.scn"]

[params]

nodes/root_type=""
_subresources={}
blender/nodes/visible=0
"""


class CharacterImportRetargetMetadataTests(unittest.TestCase):
    def test_main_sidecar_repairs_blank_character_root_metadata(self) -> None:
        main_import = BASE_IMPORT.replace(
            'source_file="res://assets/characters/sample/sample.colliders.blend"',
            'source_file="res://assets/characters/ayana/ayana.blend"',
        ).replace(
            'nodes/root_type=""',
            'nodes/root_type=""\nnodes/root_name=""\nnodes/root_script=null',
        )

        with TemporaryDirectory() as temp_dir:
            sidecar_path = Path(temp_dir) / "ayana.blend.import"
            sidecar_path.write_text(main_import, encoding="utf-8")

            changed = import_metadata.apply_main_retarget_metadata(sidecar_path, "Ayana")

            text = sidecar_path.read_text(encoding="utf-8")

        self.assertTrue(changed)
        self.assertIn('uid="uid://kept"', text)
        self.assertIn('path="res://.godot/imported/kept.scn"', text)
        self.assertIn('nodes/root_type="CharacterBody3D"', text)
        self.assertIn('nodes/root_name="Ayana"', text)
        self.assertIn('nodes/root_script=' + import_metadata.CHARACTER_ROOT_SCRIPT, text)
        self.assertIn('import_script/path=' + import_metadata.EYE_ANIMATION_IMPORT_SCRIPT, text)
        self.assertIn('"PATH:Ayana/Skeleton3D": {', text)
        self.assertIn(import_metadata.BONE_MAP_RESOURCE, text)

    def test_main_sidecar_preserves_existing_import_metadata_and_options(self) -> None:
        populated_import = """[remap]

importer="scene"
uid="uid://preserved"
path="res://.godot/imported/preserved.scn"

[deps]

source_file="res://assets/characters/ayana/ayana.blend"
dest_files=["res://.godot/imported/preserved.scn"]

[params]

nodes/root_type="Node3D"
nodes/root_name="OldName"
nodes/root_script=null
animation/import=true
_subresources={
"animations": {
"Idle": {
"settings/loop_mode": 1
}
},
"nodes": {
"PATH:Female/Skeleton3D": {
"retarget/bone_map": null
}
}
}
blender/nodes/visible=0
"""
        with TemporaryDirectory() as temp_dir:
            sidecar_path = Path(temp_dir) / "ayana.blend.import"
            sidecar_path.write_text(populated_import, encoding="utf-8")

            changed = import_metadata.apply_main_retarget_metadata(sidecar_path, "Ayana")

            text = sidecar_path.read_text(encoding="utf-8")

        self.assertTrue(changed)
        self.assertIn('uid="uid://preserved"', text)
        self.assertIn('path="res://.godot/imported/preserved.scn"', text)
        self.assertIn('dest_files=["res://.godot/imported/preserved.scn"]', text)
        self.assertIn('animation/import=true', text)
        self.assertIn('blender/nodes/visible=0', text)
        self.assertIn('"animations": {', text)
        self.assertIn('nodes/root_type="CharacterBody3D"', text)
        self.assertIn('nodes/root_name="Ayana"', text)
        self.assertIn('nodes/root_script=' + import_metadata.CHARACTER_ROOT_SCRIPT, text)
        self.assertIn('import_script/path=' + import_metadata.EYE_ANIMATION_IMPORT_SCRIPT, text)

    def test_collider_sidecar_retarget_update_does_not_apply_character_root_metadata(self) -> None:
        with TemporaryDirectory() as temp_dir:
            sidecar_path = Path(temp_dir) / "sample.colliders.blend.import"
            sidecar_path.write_text(
                BASE_IMPORT.replace(
                    'nodes/root_type=""',
                    'nodes/root_type=""\nnodes/root_name=""\nnodes/root_script=null',
                ),
                encoding="utf-8",
            )

            changed = import_metadata.apply_retarget_metadata(
                sidecar_path,
                import_metadata.collider_skeleton_path(),
            )

            text = sidecar_path.read_text(encoding="utf-8")

        self.assertTrue(changed)
        self.assertIn('nodes/root_type=""', text)
        self.assertIn('nodes/root_name=""', text)
        self.assertIn('nodes/root_script=null', text)
        self.assertNotIn(import_metadata.CHARACTER_ROOT_SCRIPT, text)
        self.assertIn('"PATH:Ragdoll/Skeleton3D": {', text)

    def test_injects_collider_retarget_metadata_into_empty_subresources(self) -> None:
        with TemporaryDirectory() as temp_dir:
            sidecar_path = Path(temp_dir) / "sample.colliders.blend.import"
            sidecar_path.write_text(BASE_IMPORT, encoding="utf-8")

            changed = import_metadata.apply_retarget_metadata(
                sidecar_path,
                import_metadata.collider_skeleton_path(),
            )

            text = sidecar_path.read_text(encoding="utf-8")

        self.assertTrue(changed)
        self.assertIn('uid="uid://kept"', text)
        self.assertIn('path="res://.godot/imported/kept.scn"', text)
        self.assertIn('"nodes": {', text)
        self.assertIn('"PATH:Ragdoll/Skeleton3D": {', text)
        self.assertIn('"retarget/bone_map": ' + import_metadata.BONE_MAP_RESOURCE, text)
        self.assertIn('"retarget/rest_fixer/fix_silhouette/enable": true', text)
        self.assertIn('"retarget/rest_fixer/fix_silhouette/filter": ' + import_metadata.SILHOUETTE_FILTER, text)

    def test_merges_retarget_metadata_with_populated_subresources(self) -> None:
        populated_import = """[params]

_subresources={
"animations": {
"Idle": {
"settings/loop_mode": 1
}
},
"nodes": {
"PATH:Ayana/Female_body": {
"import/skip_import": true
}
}
}
animation/import=true
"""
        with TemporaryDirectory() as temp_dir:
            sidecar_path = Path(temp_dir) / "ayana.blend.import"
            sidecar_path.write_text(populated_import, encoding="utf-8")

            changed = import_metadata.apply_main_retarget_metadata(sidecar_path, "Ayana")

            text = sidecar_path.read_text(encoding="utf-8")

        self.assertTrue(changed)
        self.assertIn('"animations": {', text)
        self.assertIn('"settings/loop_mode": 1', text)
        self.assertIn('"PATH:Ayana/Female_body": {', text)
        self.assertIn('"import/skip_import": true', text)
        self.assertIn('"PATH:Ayana/Skeleton3D": {', text)
        self.assertIn(import_metadata.BONE_MAP_RESOURCE, text)

    def test_main_sidecar_updates_existing_imported_skeleton_path(self) -> None:
        populated_import = """[params]

_subresources={
"animations": {
"Idle": {
"settings/loop_mode": 1
}
},
"nodes": {
"PATH:Female/Skeleton3D": {
"retarget/bone_map": null,
"retarget/rest_fixer/fix_silhouette/enable": false
},
"PATH:Female/Female_body": {
"import/skip_import": true
}
}
}
animation/import=true
"""
        with TemporaryDirectory() as temp_dir:
            sidecar_path = Path(temp_dir) / "ayana.blend.import"
            sidecar_path.write_text(populated_import, encoding="utf-8")

            changed = import_metadata.apply_main_retarget_metadata(sidecar_path, "Ayana")

            text = sidecar_path.read_text(encoding="utf-8")

        self.assertTrue(changed)
        self.assertIn('"animations": {', text)
        self.assertIn('"settings/loop_mode": 1', text)
        self.assertIn('"PATH:Female/Female_body": {', text)
        self.assertIn('"import/skip_import": true', text)
        self.assertIn('"PATH:Female/Skeleton3D": {', text)
        self.assertNotIn('"PATH:Ayana/Skeleton3D": {', text)
        self.assertNotIn('"retarget/bone_map": null', text)
        self.assertNotIn('"retarget/rest_fixer/fix_silhouette/enable": false', text)
        self.assertIn(import_metadata.BONE_MAP_RESOURCE, text)

    def test_replaces_existing_skeleton_entry_without_dropping_unrelated_nodes(self) -> None:
        populated_import = """[params]

_subresources={
"nodes": {
"PATH:Ragdoll/Skeleton3D": {
"retarget/bone_map": null,
"retarget/rest_fixer/fix_silhouette/enable": false
},
"PATH:Ragdoll/Collider": {
"import/skip_import": true
}
}
}
"""
        with TemporaryDirectory() as temp_dir:
            sidecar_path = Path(temp_dir) / "sample.colliders.blend.import"
            sidecar_path.write_text(populated_import, encoding="utf-8")

            changed = import_metadata.apply_retarget_metadata(
                sidecar_path,
                import_metadata.collider_skeleton_path(),
            )

            text = sidecar_path.read_text(encoding="utf-8")

        self.assertTrue(changed)
        self.assertNotIn('"retarget/bone_map": null', text)
        self.assertNotIn('"retarget/rest_fixer/fix_silhouette/enable": false', text)
        self.assertIn('"PATH:Ragdoll/Collider": {', text)
        self.assertIn('"import/skip_import": true', text)

    def test_derives_import_paths_from_config_output_file(self) -> None:
        sidecars = import_metadata.derive_character_import_sidecars(
            Path("game/assets/characters/ayana/ayana.blend")
        )

        self.assertEqual(Path("game/assets/characters/ayana/ayana.blend.import"), sidecars.main)
        self.assertEqual(
            Path("game/assets/characters/ayana/ayana.colliders.blend.import"),
            sidecars.colliders,
        )

    def test_reports_missing_sidecars_without_fabricating_import_metadata(self) -> None:
        with TemporaryDirectory() as temp_dir:
            output_path = Path(temp_dir) / "characters" / "NoImport.blend"
            messages = import_metadata.update_existing_character_sidecars(output_path, "NoImport")

        self.assertEqual(2, len(messages))
        self.assertTrue(all("Missing Godot import sidecar" in message for message in messages))
        self.assertTrue(all("Run Godot import" in message for message in messages))


if __name__ == "__main__":
    unittest.main()
