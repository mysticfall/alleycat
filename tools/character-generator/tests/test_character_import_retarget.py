from __future__ import annotations

import io
import sys
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from tempfile import TemporaryDirectory


def repository_root() -> Path:
    """Locate the repository root without assuming a test-file depth."""

    for candidate in Path(__file__).resolve().parents:
        if (candidate / ".git").exists():
            return candidate

    raise RuntimeError("Could not locate the repository root from the character-generator tests.")


CHARACTER_GENERATOR_ROOT = repository_root() / "tools" / "character-generator"
sys.path.insert(0, str(CHARACTER_GENERATOR_ROOT))

from character_generator import character_import_retarget as retarget_check
from character_generator.update_character_import_retarget_metadata import BONE_MAP_RESOURCE, SILHOUETTE_FILTER


HEALTHY_MAIN_IMPORT = f"""[params]

nodes/root_type="CharacterBody3D"
nodes/root_name="Ayana"
_subresources={{
"animations": {{
"Idle": {{
"settings/loop_mode": 1
}}
}},
"nodes": {{
"PATH:Female/Skeleton3D": {{
"retarget/bone_map": {BONE_MAP_RESOURCE},
"retarget/rest_fixer/fix_silhouette/enable": true,
"retarget/rest_fixer/fix_silhouette/filter": {SILHOUETTE_FILTER}
}}
}}
}}
blender/nodes/visible=0
"""

HEALTHY_COLLIDER_IMPORT = f"""[params]

nodes/root_type=""
_subresources={{
"nodes": {{
"PATH:Ragdoll/Skeleton3D": {{
"retarget/bone_map": {BONE_MAP_RESOURCE},
"retarget/rest_fixer/fix_silhouette/enable": true,
"retarget/rest_fixer/fix_silhouette/filter": {SILHOUETTE_FILTER}
}}
}}
}}
blender/nodes/visible=0
"""

SHIPPED_BUG_COLLIDER_IMPORT = """[params]

nodes/root_type=""
_subresources={}
blender/nodes/visible=0
"""


def write_character(
    directory: Path,
    name: str,
    main_import_text: str | None = HEALTHY_MAIN_IMPORT,
    collider_import_text: str | None = HEALTHY_COLLIDER_IMPORT,
) -> Path:
    """Write one generated character's .blend sources and sidecars; return the main .blend path."""

    main_blend = directory / f"{name}.blend"
    main_blend.write_bytes(b"blend")
    if main_import_text is not None:
        directory.joinpath(f"{name}.blend.import").write_text(main_import_text, encoding="utf-8")

    collider_blend = directory / f"{name}.colliders.blend"
    collider_blend.write_bytes(b"blend")
    if collider_import_text is not None:
        collider_blend.with_name(f"{name}.colliders.blend.import").write_text(
            collider_import_text, encoding="utf-8"
        )
    return main_blend


class CharacterImportRetargetCheckTests(unittest.TestCase):
    def test_healthy_reference_style_sidecars_pass(self) -> None:
        with TemporaryDirectory() as temp_dir:
            main_blend = write_character(Path(temp_dir), "ayana")

            checks = retarget_check.check_character_sidecars(main_blend)

        self.assertEqual(2, len(checks))
        self.assertTrue(all(check.passed for check in checks))
        self.assertTrue(all(not check.errors for check in checks))

    def test_shipped_empty_subresources_collider_sidecar_fails(self) -> None:
        with TemporaryDirectory() as temp_dir:
            main_blend = write_character(
                Path(temp_dir),
                "ayana",
                collider_import_text=SHIPPED_BUG_COLLIDER_IMPORT,
            )

            checks = retarget_check.check_character_sidecars(main_blend)

        self.assertEqual(2, len(checks))
        collider_check = checks[1]
        self.assertFalse(collider_check.passed)
        self.assertIn("PATH:Ragdoll/Skeleton3D", collider_check.errors[0])
        self.assertIn("is absent", collider_check.errors[0])
        self.assertIn(retarget_check.RETARGET_HELPER_COMMAND, collider_check.errors[0])

    def test_missing_collider_sidecar_with_blend_present_fails_with_remedy(self) -> None:
        with TemporaryDirectory() as temp_dir:
            main_blend = write_character(
                Path(temp_dir),
                "ayana",
                collider_import_text=None,
            )

            checks = retarget_check.check_character_sidecars(main_blend)

        self.assertEqual(2, len(checks))
        collider_check = checks[1]
        self.assertFalse(collider_check.passed)
        error = collider_check.errors[0]
        self.assertIn("Missing Godot import sidecar", error)
        self.assertIn("Run Godot import", error)
        self.assertIn(retarget_check.RETARGET_HELPER_COMMAND, error)
        self.assertIn("reimport in Godot", error)

    def test_missing_main_sidecar_fails_with_remedy(self) -> None:
        with TemporaryDirectory() as temp_dir:
            main_blend = write_character(Path(temp_dir), "ayana", main_import_text=None)

            checks = retarget_check.check_character_sidecars(main_blend)

        self.assertEqual(2, len(checks))
        main_check = checks[0]
        self.assertFalse(main_check.passed)
        self.assertIn("Missing Godot import sidecar", main_check.errors[0])
        self.assertIn("Run Godot import", main_check.errors[0])

    def test_main_sidecar_legacy_skeleton_path_with_keys_passes(self) -> None:
        with TemporaryDirectory() as temp_dir:
            main_blend = write_character(Path(temp_dir), "ayana")

            errors = retarget_check.main_sidecar_errors(
                (Path(temp_dir) / "ayana.blend.import").read_text(encoding="utf-8")
            )

        self.assertEqual([], errors)

    def test_main_sidecar_skeleton_entry_missing_keys_fails(self) -> None:
        partial_main_import = """[params]

_subresources={
"nodes": {
"PATH:Ayana/Skeleton3D": {
"retarget/bone_map": null
}
}
}
"""
        errors = retarget_check.main_sidecar_errors(partial_main_import)

        self.assertEqual(1, len(errors))
        self.assertIn('"PATH:Ayana/Skeleton3D"', errors[0])
        self.assertIn("retarget/rest_fixer/fix_silhouette/enable", errors[0])
        self.assertIn("retarget/rest_fixer/fix_silhouette/filter", errors[0])

    def test_collider_skeleton_entry_missing_rest_fixer_keys_fails(self) -> None:
        partial_collider_import = """[params]

_subresources={
"nodes": {
"PATH:Ragdoll/Skeleton3D": {
"retarget/bone_map": null
}
}
}
"""
        errors = retarget_check.collider_sidecar_errors(partial_collider_import)

        self.assertEqual(1, len(errors))
        self.assertIn('"PATH:Ragdoll/Skeleton3D"', errors[0])
        self.assertIn("retarget/rest_fixer/fix_silhouette/filter", errors[0])

    def test_collider_sidecar_without_subresources_reports_parse_failure(self) -> None:
        errors = retarget_check.collider_sidecar_errors("[params]\n")

        self.assertEqual(1, len(errors))
        self.assertIn("_subresources", errors[0])

    def test_pack_scan_resolves_paired_characters_only(self) -> None:
        with TemporaryDirectory() as temp_dir:
            pack_root = Path(temp_dir) / "packs" / "packA"
            characters_dir = pack_root / "characters"
            characters_dir.mkdir(parents=True)
            write_character(characters_dir, "nova")
            write_character(characters_dir, "ren", collider_import_text=None)

            props_dir = pack_root / "props"
            props_dir.mkdir(parents=True)
            (props_dir / "chain.blend").write_bytes(b"blend")
            (props_dir / "chain.blend.import").write_text(SHIPPED_BUG_COLLIDER_IMPORT, encoding="utf-8")

            resolved = retarget_check.collect_character_blend_paths(pack_root)

            self.assertEqual(
                [
                    characters_dir / "nova.blend",
                    characters_dir / "ren.blend",
                ],
                resolved,
            )

    def test_file_targets_resolve_to_main_blend_paths(self) -> None:
        with TemporaryDirectory() as temp_dir:
            directory = Path(temp_dir)
            write_character(directory, "ayana")

            self.assertEqual(
                [directory / "ayana.blend"],
                retarget_check.collect_character_blend_paths(directory / "ayana.blend"),
            )
            self.assertEqual(
                [directory / "ayana.blend"],
                retarget_check.collect_character_blend_paths(directory / "ayana.blend.import"),
            )
            self.assertEqual(
                [directory / "ayana.blend"],
                retarget_check.collect_character_blend_paths(directory / "ayana.colliders.blend"),
            )
            self.assertEqual(
                [directory / "ayana.blend"],
                retarget_check.collect_character_blend_paths(
                    directory / "ayana.colliders.blend.import"
                ),
            )

    def test_unsupported_target_is_rejected(self) -> None:
        with TemporaryDirectory() as temp_dir:
            readme = Path(temp_dir) / "notes.txt"
            readme.write_text("notes", encoding="utf-8")

            with self.assertRaises(ValueError):
                retarget_check.collect_character_blend_paths(readme)
            with self.assertRaises(ValueError):
                retarget_check.collect_character_blend_paths(Path(temp_dir) / "missing")

    def test_main_reports_failures_and_exit_code(self) -> None:
        with TemporaryDirectory() as temp_dir:
            directory = Path(temp_dir)
            write_character(directory, "ayana")
            write_character(
                directory,
                "brin",
                collider_import_text=SHIPPED_BUG_COLLIDER_IMPORT,
            )

            stdout = io.StringIO()
            with redirect_stdout(stdout):
                exit_code = retarget_check.main([str(directory)])

        output = stdout.getvalue()
        self.assertEqual(1, exit_code)
        self.assertIn(f"PASS {directory / 'ayana.blend.import'}", output)
        self.assertIn(f"PASS {directory / 'ayana.colliders.blend.import'}", output)
        self.assertIn(f"FAIL {directory / 'brin.colliders.blend.import'}", output)
        self.assertIn("Checked 4 character import sidecars: 3 passed, 1 failed.", output)

    def test_main_exits_zero_for_healthy_directory(self) -> None:
        with TemporaryDirectory() as temp_dir:
            directory = Path(temp_dir)
            write_character(directory, "ayana")

            stdout = io.StringIO()
            with redirect_stdout(stdout):
                exit_code = retarget_check.main([str(directory)])

        self.assertEqual(0, exit_code)
        self.assertIn("0 failed.", stdout.getvalue())

    def test_main_exits_two_for_unsupported_target(self) -> None:
        with TemporaryDirectory() as temp_dir:
            readme = Path(temp_dir) / "notes.txt"
            readme.write_text("notes", encoding="utf-8")

            stdout = io.StringIO()
            with redirect_stdout(stdout):
                exit_code = retarget_check.main([str(readme)])

        self.assertEqual(2, exit_code)
        self.assertIn("ERROR:", stdout.getvalue())

    def test_repository_reference_sidecars_pass(self) -> None:
        reference_root = repository_root() / "game" / "assets" / "characters" / "reference"
        for variant in ("female", "male"):
            with self.subTest(variant=variant):
                checks = retarget_check.check_character_sidecars(
                    reference_root / variant / f"reference_{variant}.blend"
                )

                self.assertEqual(2, len(checks))
                self.assertTrue(all(check.passed for check in checks), [c.errors for c in checks])


if __name__ == "__main__":
    unittest.main()
