"""Synthetic Blender transaction coverage for workspace bootstrap."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess
import shutil
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
    baseline = overrides.get(
        "approved",
        {
            "fixture_character": {
                "canonical_source": _sha256(fixture["canonical"]),
                "neutral_template": _sha256(fixture["template"]),
                "character_setup": _sha256(fixture["setup"]),
            }
        },
    )
    return [
        "--python", ENTRYPOINT, "--", "--variant", FIXTURE_VARIANT,
        "--character-root", fixture["character"], "--repository-root", fixture["repository"],
        "--setup", overrides.get("setup", fixture["setup"]),
        "--template", overrides.get("template", fixture["template"]),
        "--idle-blend", overrides.get("idle_blend", fixture["template"]),
        "--idle-action", overrides.get("idle_action", "pose_ref_idle"),
        "--temp-root", fixture["temp"],
        "--approved-character-baselines", json.dumps(baseline, sort_keys=True),
    ]


def _summary(result: subprocess.CompletedProcess[str]) -> dict:
    return json.loads(next(
        line.removeprefix("KIMODO_BOOTSTRAP_WORKSPACE_JSON=")
        for line in result.stdout.splitlines()
        if line.startswith("KIMODO_BOOTSTRAP_WORKSPACE_JSON=")
    ))


class BlenderBootstrapWorkspaceSmokeTests(unittest.TestCase):
    def test_approved_renamed_target_is_composed_with_real_fingerprints(self) -> None:
        with tempfile.TemporaryDirectory(prefix="kimodo-workspace-", dir=ROOT / "game/temp") as directory:
            fixture = _workspace_fixture(Path(directory))
            from kimodo_pipeline.blender_cli import run_blender_python

            renamed = run_blender_python([fixture["setup"], "--python-expr",
                "import bpy; bpy.data.objects['Female'].name='Reference_Fixture'; "
                "bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath,check_existing=False)"], ROOT)
            self.assertEqual(renamed.returncode, 0, renamed.stdout + renamed.stderr)
            result = run_blender_python(_arguments(fixture), ROOT)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            report = json.loads(Path(_summary(result)["report_path"]).read_text())
            fingerprints = report["inventory"]["fingerprints"]
            self.assertEqual(fingerprints["female_constraint_graph_edge_count"], 2)
            self.assertEqual(len(fingerprints["female_constraint_graph_sha256"]), 64)
            self.assertIn("Reference_Fixture", [row["name"] for row in report["inventory"]["objects"]])

    def test_identical_approved_setup_at_another_path_is_admitted(self) -> None:
        with tempfile.TemporaryDirectory(prefix="kimodo-workspace-", dir=ROOT / "game/temp") as directory:
            fixture = _workspace_fixture(Path(directory))
            alternate = fixture["root"] / "identical-approved.blend"
            shutil.copy2(fixture["setup"], alternate)
            from kimodo_pipeline.blender_cli import run_blender_python

            result = run_blender_python(_arguments(fixture, setup=alternate), ROOT)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertTrue(_summary(result)["validation_passed"])

    def test_unapproved_pose_drift_override_refuses_before_staging(self) -> None:
        with tempfile.TemporaryDirectory(prefix="kimodo-workspace-", dir=ROOT / "game/temp") as directory:
            fixture = _workspace_fixture(Path(directory))
            approved = {"fixture_character": {
                "canonical_source": _sha256(fixture["canonical"]),
                "neutral_template": _sha256(fixture["template"]),
                "character_setup": _sha256(fixture["setup"]),
            }}
            alternate = fixture["root"] / "unapproved-helper-pose.blend"
            from kimodo_pipeline.blender_cli import run_blender_python

            mutation = run_blender_python([fixture["setup"], "--python-expr",
                "import bpy; from mathutils import Matrix; "
                "bpy.data.objects['Kimodo_Source'].pose.bones['Root_RET'].matrix_basis=Matrix.Rotation(0.4,4,'Y'); "
                f"bpy.ops.wm.save_as_mainfile(filepath={str(alternate)!r},check_existing=False)"], ROOT)
            self.assertEqual(mutation.returncode, 0, mutation.stdout + mutation.stderr)
            result = run_blender_python(_arguments(fixture, setup=alternate, approved=approved), ROOT)
            self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
            self.assertIn(_sha256(alternate), result.stderr)
            self.assertFalse(fixture["workspace"].exists())
            self.assertFalse((fixture["temp"] / "kimodo-bootstrap-workspace/staged").exists())

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
            self.assertEqual(summary["baseline_character"], "fixture_character")
            self.assertTrue(fixture["workspace"].is_file())
            report = json.loads(Path(summary["report_path"]).read_text(encoding="utf-8"))
            self.assertEqual(report["baseline_character"], "fixture_character")
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

    def test_an_unapproved_character_setup_hash_is_refused_before_composition(self) -> None:
        with tempfile.TemporaryDirectory(prefix="kimodo-workspace-", dir=ROOT / "game/temp") as directory:
            fixture = _workspace_fixture(Path(directory))
            fixture["workspace"].parent.mkdir(parents=True, exist_ok=True)
            fixture["workspace"].write_bytes(b"fixture workspace sentinel")
            from kimodo_pipeline.blender_cli import run_blender_python

            # An approved entry for a different character's setup bytes never
            # admits this fixture's setup: onboarding is an explicit action.
            unrecognised = {
                "other_character": {
                    "canonical_source": _sha256(fixture["canonical"]),
                    "neutral_template": _sha256(fixture["template"]),
                    "character_setup": "0" * 64,
                }
            }
            result = run_blender_python(_arguments(fixture, approved=unrecognised), ROOT)
            self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
            self.assertIn(_sha256(fixture["setup"]), result.stderr)
            self.assertIn("other_character", result.stderr)
            self.assertEqual(fixture["workspace"].read_bytes(), b"fixture workspace sentinel")
