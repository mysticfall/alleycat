"""Synthetic Blender transaction coverage for workspace bootstrap."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

from synthetic_kimodo_blend import FIXTURE_VARIANT, SyntheticKimodoBlendBuilder
from kimodo_pipeline.bootstrap_paths import derive_artefact_paths
from test_blender_bootstrap_extraction_smoke import _arguments as extraction_arguments
from test_blender_bootstrap_extraction_smoke import _fixture, _manifest


ROOT = Path(__file__).resolve().parents[3]
EXTRACTION_ENTRYPOINT = ROOT / "tools/kimodo-pipeline/kimodo_pipeline/extract_bootstrap.py"
ENTRYPOINT = ROOT / "tools/kimodo-pipeline/kimodo_pipeline/workspace_bootstrap.py"


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _workspace_fixture(work: Path) -> dict[str, Path]:
    fixture = _fixture(work)
    manifest = _manifest(fixture)
    from kimodo_pipeline.blender_cli import run_blender_python

    extracted = run_blender_python(extraction_arguments(fixture, manifest), ROOT)
    if extracted.returncode != 0:
        raise RuntimeError(extracted.stdout + extracted.stderr)
    paths = derive_artefact_paths(fixture["character"], FIXTURE_VARIANT, fixture["repository"])
    fixture["setup"] = paths.character_setup
    fixture["template"] = paths.neutral_template
    fixture["canonical"] = paths.canonical_source
    fixture["workspace"] = paths.authoring_workspace
    return fixture


def _arguments(fixture: dict[str, Path], **overrides: Path | str) -> list[Path | str]:
    baseline = {
        "canonical_source": _sha256(fixture["canonical"]),
        "neutral_template": _sha256(fixture["template"]),
        "character_setup": _sha256(fixture["setup"]),
    }
    return [
        "--python", ENTRYPOINT, "--", "--variant", FIXTURE_VARIANT,
        "--character-root", fixture["character"], "--repository-root", fixture["repository"],
        "--setup", overrides.get("setup", fixture["setup"]),
        "--template", overrides.get("template", fixture["template"]),
        "--idle-blend", overrides.get("idle_blend", fixture["template"]),
        "--idle-action", overrides.get("idle_action", "pose_ref_idle"),
        "--temp-root", fixture["temp"],
        "--baseline-input-sha256", json.dumps(baseline, sort_keys=True),
    ]


def _summary(result: subprocess.CompletedProcess[str]) -> dict:
    return json.loads(next(
        line.removeprefix("KIMODO_BOOTSTRAP_WORKSPACE_JSON=")
        for line in result.stdout.splitlines()
        if line.startswith("KIMODO_BOOTSTRAP_WORKSPACE_JSON=")
    ))


class BlenderBootstrapWorkspaceSmokeTests(unittest.TestCase):
    def test_composes_a_declared_workspace_without_touching_inputs(self) -> None:
        with tempfile.TemporaryDirectory(prefix="kimodo-workspace-", dir=ROOT / "game/temp") as directory:
            fixture = _workspace_fixture(Path(directory))
            inputs = {key: _sha256(fixture[key]) for key in ("setup", "template", "canonical")}
            from kimodo_pipeline.blender_cli import run_blender_python
            from kimodo_pipeline.bootstrap_validation import workspace_report_structure_failures

            result = run_blender_python(_arguments(fixture), ROOT)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            summary = _summary(result)
            self.assertTrue(summary["validation_passed"])
            self.assertTrue(summary["inputs_unchanged"])
            self.assertTrue(fixture["workspace"].is_file())
            report = json.loads(Path(summary["report_path"]).read_text(encoding="utf-8"))
            self.assertEqual(workspace_report_structure_failures(report), [])
            self.assertEqual(
                sorted(record["name"] for record in report["inventory"]["objects"]),
                sorted(set(report["composition"]["setup_objects"]) | set(report["composition"]["template_objects"])),
            )
            self.assertEqual(report["composition"]["frame_range"]["chosen"], report["composition"]["frame_range"]["segment_extents"])
            self.assertTrue(all(entry["equal"] for entry in report["composition"]["fingerprints"].values()))
            self.assertEqual({key: _sha256(fixture[key]) for key in inputs}, inputs)

    def test_rejection_preserves_an_existing_workspace(self) -> None:
        with tempfile.TemporaryDirectory(prefix="kimodo-workspace-", dir=ROOT / "game/temp") as directory:
            fixture = _workspace_fixture(Path(directory))
            fixture["workspace"].parent.mkdir(parents=True, exist_ok=True)
            fixture["workspace"].write_bytes(b"fixture workspace sentinel")
            from kimodo_pipeline.blender_cli import run_blender_python

            result = run_blender_python(_arguments(fixture, idle_action="fixture_missing_idle"), ROOT)
            self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
            self.assertEqual(fixture["workspace"].read_bytes(), b"fixture workspace sentinel")
