"""Synthetic Blender transaction coverage for bootstrap extraction."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import tempfile
import unittest

from synthetic_kimodo_blend import FIXTURE_VARIANT, MANIFEST, SyntheticKimodoBlendBuilder
from kimodo_pipeline.bootstrap_paths import derive_artefact_paths


ROOT = Path(__file__).resolve().parents[3]
MANIFEST_ENTRYPOINT = ROOT / "tools/kimodo-pipeline/kimodo_pipeline/manifest_bootstrap.py"
ENTRYPOINT = ROOT / "tools/kimodo-pipeline/kimodo_pipeline/extract_bootstrap.py"


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _fixture(work: Path) -> dict[str, Path]:
    fixture = {
        "root": work,
        "repository": work / "repository",
        "character": work / "character",
        "temp": work / "temp",
        "source": work / "inputs" / "fixture_monolith.blend",
    }
    built = SyntheticKimodoBlendBuilder().build_bootstrap_monolith(fixture["source"])
    if built.returncode != 0:
        raise RuntimeError(built.stdout + built.stderr)
    return fixture


def _manifest(fixture: dict[str, Path]) -> Path:
    from kimodo_pipeline.blender_cli import run_blender_python

    result = run_blender_python([
        "--python", MANIFEST_ENTRYPOINT, "--", "--blend", fixture["source"],
        "--character-root", fixture["character"], "--variant", FIXTURE_VARIANT,
        "--repository-root", fixture["repository"], "--temp-root", fixture["temp"],
    ], ROOT)
    if result.returncode != 0:
        raise RuntimeError(result.stdout + result.stderr)
    return Path(json.loads(next(
        line.removeprefix("KIMODO_BOOTSTRAP_MANIFEST_JSON=")
        for line in result.stdout.splitlines()
        if line.startswith("KIMODO_BOOTSTRAP_MANIFEST_JSON=")
    ))["manifest_path"])


def _arguments(fixture: dict[str, Path], manifest: Path, temp: Path | None = None) -> list[Path | str]:
    return [
        "--python", ENTRYPOINT, "--", "--blend", fixture["source"],
        "--character-root", fixture["character"], "--variant", FIXTURE_VARIANT,
        "--repository-root", fixture["repository"], "--manifest", manifest,
        "--temp-root", temp if temp is not None else fixture["temp"],
    ]


class BlenderBootstrapExtractionSmokeTests(unittest.TestCase):
    def test_extracts_declared_artefacts_without_touching_the_fixture_source(self) -> None:
        with tempfile.TemporaryDirectory(prefix="kimodo-bootstrap-", dir=ROOT / "game/temp") as directory:
            fixture = _fixture(Path(directory))
            manifest = _manifest(fixture)
            before = _sha256(fixture["source"])
            from kimodo_pipeline.blender_cli import run_blender_python
            from kimodo_pipeline.bootstrap_validation import report_structure_failures

            result = run_blender_python(_arguments(fixture, manifest), ROOT)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            summary = json.loads(next(
                line.removeprefix("KIMODO_BOOTSTRAP_EXTRACTION_JSON=")
                for line in result.stdout.splitlines()
                if line.startswith("KIMODO_BOOTSTRAP_EXTRACTION_JSON=")
            ))
            self.assertTrue(summary["monolith_unchanged"])
            report = json.loads(Path(summary["report_path"]).read_text(encoding="utf-8"))
            self.assertEqual(report_structure_failures(report), [])
            paths = derive_artefact_paths(fixture["character"], FIXTURE_VARIANT, fixture["repository"])
            expected = {
                "canonical_source": paths.canonical_source,
                "neutral_template": paths.neutral_template,
                "character_setup": paths.character_setup,
            }
            for key, destination in expected.items():
                self.assertEqual(Path(summary["artefacts"][key]["destination"]), destination)
                self.assertTrue(destination.is_file())
                self.assertEqual(_sha256(destination), summary["artefacts"][key]["sha256"])
            self.assertEqual(len(report["artefacts"]["neutral_template"]["inventory"]["segments"]), len(MANIFEST.segments))
            self.assertEqual(_sha256(fixture["source"]), before)

    def test_validation_failure_publishes_nothing(self) -> None:
        with tempfile.TemporaryDirectory(prefix="kimodo-bootstrap-", dir=ROOT / "game/temp") as directory:
            fixture = _fixture(Path(directory))
            manifest = _manifest(fixture)
            document = json.loads(manifest.read_text(encoding="utf-8"))
            document["fingerprints"]["female_constraint_graph_sha256"] = "0" * 64
            doctored = fixture["root"] / "doctored.json"
            doctored.write_text(json.dumps(document), encoding="utf-8")
            from kimodo_pipeline.blender_cli import run_blender_python

            result = run_blender_python(_arguments(fixture, doctored), ROOT)
            self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
            self.assertIn("failed structural validation", result.stderr)
            self.assertFalse(fixture["repository"].exists())
            self.assertFalse(fixture["character"].exists())
