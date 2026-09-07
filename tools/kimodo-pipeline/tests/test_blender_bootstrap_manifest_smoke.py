"""Synthetic Blender smoke coverage for the bootstrap manifest command."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import tempfile
import unittest

from synthetic_kimodo_blend import FIXTURE_VARIANT, MANIFEST, SyntheticKimodoBlendBuilder


ROOT = Path(__file__).resolve().parents[3]
ENTRYPOINT = ROOT / "tools/kimodo-pipeline/kimodo_pipeline/manifest_bootstrap.py"


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


class BlenderBootstrapManifestSmokeTests(unittest.TestCase):
    def test_manifests_declared_fixture_without_touching_the_source(self) -> None:
        with tempfile.TemporaryDirectory(prefix="kimodo-bootstrap-", dir=ROOT / "game/temp") as directory:
            fixture = Path(directory)
            blend = fixture / "inputs" / "fixture_monolith.blend"
            built = SyntheticKimodoBlendBuilder().build_bootstrap_monolith(blend)
            self.assertEqual(built.returncode, 0, built.stdout + built.stderr)
            before = _sha256(blend)
            character_root = fixture / "character"
            repository_root = fixture / "repository"
            from kimodo_pipeline.blender_cli import run_blender_python

            result = run_blender_python([
                "--python", ENTRYPOINT, "--", "--blend", blend,
                "--character-root", character_root, "--variant", FIXTURE_VARIANT,
                "--repository-root", repository_root, "--temp-root", fixture / "reports",
            ], ROOT)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            summary = json.loads(next(
                line.removeprefix("KIMODO_BOOTSTRAP_MANIFEST_JSON=")
                for line in result.stdout.splitlines()
                if line.startswith("KIMODO_BOOTSTRAP_MANIFEST_JSON=")
            ))
            manifest = json.loads(Path(summary["manifest_path"]).read_text(encoding="utf-8"))
            self.assertEqual(manifest["source_blend_sha256"], before)
            self.assertEqual(summary["counts"]["segments"], len(MANIFEST.segments))
            self.assertEqual(
                set(manifest["classification_summary"]["objects"].values()),
                {"character_retarget_setup", "locomotion_template"},
            )
            self.assertTrue(manifest["projected_paths"]["authoring_workspace"].endswith(
                f"locomotion_{FIXTURE_VARIANT}_authoring.blend"
            ))
            self.assertEqual(_sha256(blend), before)

    def test_rejects_half_supplied_path_projection(self) -> None:
        with tempfile.TemporaryDirectory(prefix="kimodo-bootstrap-", dir=ROOT / "game/temp") as directory:
            blend = Path(directory) / "fixture.blend"
            built = SyntheticKimodoBlendBuilder().build_bootstrap_monolith(blend)
            self.assertEqual(built.returncode, 0, built.stdout + built.stderr)
            from kimodo_pipeline.blender_cli import run_blender_python

            result = run_blender_python(
                ["--python", ENTRYPOINT, "--", "--blend", blend, "--variant", FIXTURE_VARIANT],
                ROOT,
            )
            self.assertEqual(result.returncode, 2, result.stdout + result.stderr)

    def test_rejects_missing_blend_argument(self) -> None:
        from kimodo_pipeline.blender_cli import run_blender_python

        result = run_blender_python(["--python", ENTRYPOINT, "--"], ROOT)
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
