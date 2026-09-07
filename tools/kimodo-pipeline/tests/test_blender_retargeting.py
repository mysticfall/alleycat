"""Synthetic Blender contract coverage for Kimodo constraint baking."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile


from kimodo_pipeline.bootstrap_paths import repository_root


ROOT = repository_root()
TOOLS = ROOT / "tools"
sys.path.extend((str(TOOLS / "kimodo-pipeline"), str(TOOLS / "kimodo-pipeline/tests")))
from synthetic_kimodo_blend import FIXTURE_VARIANT, SyntheticKimodoBlendBuilder
from kimodo_pipeline.workflow_binding import (
    MATERIALISATION_BINDING_CONTRACT,
    canonical_json_sha256,
)


ENTRY = TOOLS / "kimodo-pipeline/commands/retarget_animations.py"


def run_processor(source: Path, output: Path, *extra: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run([
        "blender", "--background", "--factory-startup", "--python", str(ENTRY), "--",
        "--package", "locomotion", "--source", str(source), "--output-root", str(output),
        "--variant", FIXTURE_VARIANT, "--roles", "wf", *extra,
    ], cwd=ROOT, text=True, capture_output=True, check=False, timeout=180)


def test_synthetic_bake_preserves_source_and_publishes_portable_package() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-retarget-", dir=ROOT / "game/temp") as directory:
        fixture = Path(directory)
        source = fixture / "fixture_source.blend"
        built = SyntheticKimodoBlendBuilder().build_bootstrap_monolith(source)
        assert built.returncode == 0, built.stdout + built.stderr
        before = hashlib.sha256(source.read_bytes()).hexdigest()
        result = run_processor(source, fixture / "output")
        assert result.returncode == 0, result.stdout + result.stderr
        assert hashlib.sha256(source.read_bytes()).hexdigest() == before
        package = fixture / "output/locomotion"
        index_path = package / FIXTURE_VARIANT / "processed-index.json"
        index = json.loads(index_path.read_text(encoding="utf-8"))
        assert index["variant"] == FIXTURE_VARIANT
        assert index["action_count"] == 1
        entry = index["actions"][0]
        assert entry["role"] == "wf"
        assert entry["source_action"].startswith(f"loc_{FIXTURE_VARIANT}_")
        assert entry["source_directional_event_evidence"]["contract"] == "anim002_source_directional_phase_events_v1"
        assert entry["event_derivation_evidence"]["contract"] == "anim002_event_derivation_v1"
        assert entry["post_repair_event_evidence"]["contract"] == "anim002_post_repair_directional_phase_events_v2"
        assert entry["post_repair_event_evidence"]["evaluation_stage"] == "final_repaired_evaluated_hierarchy"
        assert entry["loop_repair_evidence"]["blend_profile"] == "quadratic"
        assert entry["loop_repair_evidence"]["sample_semantics"]["unique_runtime_sample_count"] == 32
        assert not Path(index["source_path"]).is_absolute()
        assert (index_path.parent / Path(entry["metrics"])).is_file()
        assert (package / f"locomotion_{FIXTURE_VARIANT}.blend").is_file()


def test_obsolete_metadata_is_rejected_without_replacing_a_fixture_package() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-retarget-", dir=ROOT / "game/temp") as directory:
        fixture = Path(directory)
        source = fixture / "fixture_source.blend"
        built = SyntheticKimodoBlendBuilder().build_bootstrap_monolith(source)
        assert built.returncode == 0, built.stdout + built.stderr
        package = fixture / "output/locomotion"
        package.mkdir(parents=True)
        sentinel = package / "sentinel.bin"
        sentinel.write_bytes(b"fixture previous package")
        action = f"loc_{FIXTURE_VARIANT}_wf_r_loop"
        mutation = subprocess.run([
            "blender", "--background", "--factory-startup", str(source), "--python-expr",
            "import bpy; a=bpy.data.actions[" + repr(action) + "]; "
            "a['anim002_trailing_repair']=[1,2]; "
            "bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath,check_existing=False)",
        ], cwd=ROOT, text=True, capture_output=True, check=False, timeout=60)
        assert mutation.returncode == 0, mutation.stdout + mutation.stderr
        result = run_processor(source, fixture / "output")
        assert result.returncode != 0
        assert "obsolete loop-repair metadata" in result.stderr
        assert sentinel.read_bytes() == b"fixture previous package"


def test_missing_synthetic_source_preserves_a_fixture_package() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-retarget-", dir=ROOT / "game/temp") as directory:
        fixture = Path(directory)
        package = fixture / "output/locomotion"
        package.mkdir(parents=True)
        sentinel = package / "sentinel.bin"
        sentinel.write_bytes(b"fixture previous package")
        result = run_processor(fixture / "missing.blend", fixture / "output")
        assert result.returncode != 0
        assert sentinel.read_bytes() == b"fixture previous package"


def test_retarget_binds_the_exact_materialised_carrier_and_rejects_later_mutation() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-retarget-binding-", dir=ROOT / "game/temp") as directory:
        fixture = Path(directory)
        precursor = fixture / "immutable-precursor.blend"
        precursor.write_bytes(b"declared repair precursor")
        repair_report = fixture / "repair.json"
        repair_report.write_text(json.dumps({
            "output": str(precursor.resolve()),
            "after_sha256": hashlib.sha256(precursor.read_bytes()).hexdigest(),
        }), encoding="utf-8")
        source = fixture / "materialised.blend"
        built = SyntheticKimodoBlendBuilder().build_bootstrap_monolith(source)
        assert built.returncode == 0, built.stdout + built.stderr
        evidence = fixture / "selection.json"
        evidence.write_text("{}", encoding="utf-8")
        report = fixture / "materialisation.json"
        report.write_text(json.dumps({
            "contract": MATERIALISATION_BINDING_CONTRACT,
            "precursor": {"path": str(precursor), "sha256": hashlib.sha256(precursor.read_bytes()).hexdigest()},
            "final": {"path": str(source), "sha256": hashlib.sha256(source.read_bytes()).hexdigest()},
            "source_repair_report": {"path": str(repair_report), "sha256": hashlib.sha256(repair_report.read_bytes()).hexdigest()},
            "selection_evidence": {"path": str(evidence), "sha256": hashlib.sha256(evidence.read_bytes()).hexdigest()},
            "action_provenance_sha256": canonical_json_sha256({"wf": "declared synthetic provenance"}),
            "roles": ["wf_r"],
        }), encoding="utf-8")

        accepted = run_processor(
            source, fixture / "accepted", "--materialisation-report", str(report),
        )
        assert accepted.returncode == 0, accepted.stdout + accepted.stderr
        index = json.loads((fixture / "accepted/locomotion" / FIXTURE_VARIANT / "processed-index.json").read_text(encoding="utf-8"))
        assert index["source_carrier_binding"]["source_sha256"] == hashlib.sha256(source.read_bytes()).hexdigest()
        assert index["source_carrier_binding"]["action_provenance_sha256"] == canonical_json_sha256(
            {"wf": "declared synthetic provenance"}
        )

        source.write_bytes(source.read_bytes() + b"later mutation")
        rejected = run_processor(
            source, fixture / "rejected", "--materialisation-report", str(report),
        )
        assert rejected.returncode != 0
        assert "report-bound final bytes changed" in rejected.stderr
        assert not (fixture / "rejected/locomotion").exists()


def test_incomplete_event_provenance_is_rejected_before_fixture_publication() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-retarget-", dir=ROOT / "game/temp") as directory:
        fixture = Path(directory)
        source = fixture / "fixture_source.blend"
        built = SyntheticKimodoBlendBuilder().build_bootstrap_monolith(source)
        assert built.returncode == 0, built.stdout + built.stderr
        package = fixture / "output/locomotion"
        package.mkdir(parents=True)
        sentinel = package / "sentinel.bin"
        sentinel.write_bytes(b"fixture previous package")
        action = f"loc_{FIXTURE_VARIANT}_wf_r_loop"
        mutation = subprocess.run([
            "blender", "--background", "--factory-startup", str(source), "--python-expr",
            "import bpy; a=bpy.data.actions[" + repr(action) + "]; "
            "del a['anim002_event_derivation_evidence']; "
            "bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath,check_existing=False)",
        ], cwd=ROOT, text=True, capture_output=True, check=False, timeout=60)
        assert mutation.returncode == 0, mutation.stdout + mutation.stderr
        result = run_processor(source, fixture / "output")
        assert result.returncode != 0
        assert "stale or incomplete event provenance" in result.stderr
        assert sentinel.read_bytes() == b"fixture previous package"


def test_stale_post_repair_physical_contract_is_rejected_before_fixture_publication() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-retarget-", dir=ROOT / "game/temp") as directory:
        fixture = Path(directory)
        source = fixture / "fixture_source.blend"
        built = SyntheticKimodoBlendBuilder().build_bootstrap_monolith(source)
        assert built.returncode == 0, built.stdout + built.stderr
        package = fixture / "output/locomotion"
        package.mkdir(parents=True)
        sentinel = package / "sentinel.bin"
        sentinel.write_bytes(b"fixture previous package")
        action = f"loc_{FIXTURE_VARIANT}_wf_r_loop"
        expression = (
            "import bpy,json; a=bpy.data.actions[" + repr(action) + "]; "
            "e=json.loads(a['anim002_post_repair_event_evidence']); "
            "e['contract']='anim002_post_repair_physical_events_v1'; "
            "a['anim002_post_repair_event_evidence']=json.dumps(e,sort_keys=True); "
            "bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath,check_existing=False)"
        )
        mutation = subprocess.run([
            "blender", "--background", "--factory-startup", str(source), "--python-expr", expression,
        ], cwd=ROOT, text=True, capture_output=True, check=False, timeout=60)
        assert mutation.returncode == 0, mutation.stdout + mutation.stderr
        result = run_processor(source, fixture / "output")
        assert result.returncode != 0
        assert "stale or incomplete event provenance" in result.stderr
        assert sentinel.read_bytes() == b"fixture previous package"
