"""Blender-backed materialisation boundaries using disposable synthetic input."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import shutil
import tempfile

from kimodo_pipeline.blender_cli import run_blender_python
from kimodo_pipeline.workflow_binding import (
    MATERIALISATION_BINDING_CONTRACT,
    WorkflowBindingError,
    canonical_json_sha256,
    validate_materialisation_report,
)
import pytest

from synthetic_kimodo_blend import MANIFEST, RIG_NAME, SyntheticKimodoBlendBuilder


ROOT = Path(__file__).resolve().parents[3]
ENTRYPOINT = ROOT / "tools/kimodo-pipeline/kimodo_pipeline/materialise_locomotion.py"


def test_synthetic_blender_fixture_has_the_declared_minimal_rig_and_actions() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-materialise-") as directory:
        blend = Path(directory) / "fixture.blend"
        result = SyntheticKimodoBlendBuilder().build(blend)
        assert result.returncode == 0, result.stdout + result.stderr
        expression = (
            "import bpy,json; "
            "print('KIMODO_SYNTHETIC_INVENTORY='+json.dumps({'rig':bpy.data.objects.get(" + repr(RIG_NAME)
            + ").name,'bones':sorted(bpy.data.objects[" + repr(RIG_NAME)
            + "].data.bones.keys()),'actions':sorted(bpy.data.actions.keys())}))"
        )
        inspected = run_blender_python([blend, "--python-expr", expression], ROOT)
        assert inspected.returncode == 0, inspected.stdout + inspected.stderr
        inventory = next(
            row.removeprefix("KIMODO_SYNTHETIC_INVENTORY=")
            for row in inspected.stdout.splitlines()
            if row.startswith("KIMODO_SYNTHETIC_INVENTORY=")
        )
        assert RIG_NAME in inventory
        assert all(action.name in inventory for action in MANIFEST.actions)


def test_invalid_materialisation_variant_rejects_the_synthetic_input_without_mutation() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-materialise-") as directory:
        blend = Path(directory) / "fixture.blend"
        built = SyntheticKimodoBlendBuilder().build(blend)
        assert built.returncode == 0, built.stdout + built.stderr
        before = hashlib.sha256(blend.read_bytes()).hexdigest()
        result = run_blender_python([
            "--python", ENTRYPOINT, "--", "--blend", blend, "--variant", "Invalid-Variant",
            "--temp-root", Path(directory) / "staging",
        ], ROOT)
        assert result.returncode != 0
        assert hashlib.sha256(blend.read_bytes()).hexdigest() == before


def test_blender_mutation_of_a_report_bound_materialisation_carrier_is_refused() -> None:
    with tempfile.TemporaryDirectory(
        prefix="kimodo-bound-materialise-", dir=ROOT / "game/temp",
    ) as directory:
        root = Path(directory)
        precursor = root / "immutable-repair-precursor.blend"
        built = SyntheticKimodoBlendBuilder().build(precursor)
        assert built.returncode == 0, built.stdout + built.stderr
        precursor_hash = hashlib.sha256(precursor.read_bytes()).hexdigest()
        repair_report = root / "repair.json"
        repair_report.write_text(json.dumps({
            "algorithm": "declared_synthetic_repair_v1",
            "output": str(precursor.resolve()),
            "after_sha256": precursor_hash,
        }), encoding="utf-8")
        final = root / "materialised.blend"
        binding_report = root / "materialisation.json"
        evidence = root / "selection.json"
        evidence.write_text("{}", encoding="utf-8")
        shutil.copy2(precursor, final)
        materialised = run_blender_python([
            final, "--python-expr",
            "import bpy; bpy.context.scene['declared_materialisation']='24 roles'; "
            f"bpy.ops.wm.save_as_mainfile(filepath={str(final)!r},check_existing=False)",
        ], ROOT)
        assert materialised.returncode == 0, materialised.stdout + materialised.stderr
        provenance = {"wf_l": {"declared": "synthetic"}}
        binding_report.write_text(json.dumps({
            "contract": MATERIALISATION_BINDING_CONTRACT,
            "precursor": {"path": str(precursor), "sha256": precursor_hash},
            "final": {"path": str(final), "sha256": hashlib.sha256(final.read_bytes()).hexdigest()},
            "source_repair_report": {"path": str(repair_report), "sha256": hashlib.sha256(repair_report.read_bytes()).hexdigest()},
            "selection_evidence": {"path": str(evidence), "sha256": hashlib.sha256(evidence.read_bytes()).hexdigest()},
            "action_provenance_sha256": canonical_json_sha256(provenance),
            "roles": ["wf_l"],
        }), encoding="utf-8")
        report = validate_materialisation_report(binding_report)
        assert hashlib.sha256(precursor.read_bytes()).hexdigest() == precursor_hash
        assert report["precursor"]["path"] == str(precursor.resolve())
        assert report["final"]["path"] == str(final.resolve())
        assert report["precursor"]["sha256"] != report["final"]["sha256"]
        assert report["roles"] == ["wf_l"]

        mutated = run_blender_python([
            final, "--python-expr",
            "import bpy; bpy.context.scene['later_mutation']=True; "
            f"bpy.ops.wm.save_as_mainfile(filepath={str(final)!r},check_existing=False)",
        ], ROOT)
        assert mutated.returncode == 0, mutated.stdout + mutated.stderr
        with pytest.raises(WorkflowBindingError, match="final bytes changed"):
            validate_materialisation_report(binding_report)
