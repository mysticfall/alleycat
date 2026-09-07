"""Source-repair contracts exercised with declared synthetic fixture evidence."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import tempfile

import pytest

from kimodo_pipeline.blender_cli import run_blender_python
from kimodo_pipeline.loop_seam import validate_durable_repair_windows
from kimodo_pipeline.workflow_binding import (
    WorkflowBindingError,
    canonical_json_sha256,
    validate_action_provenance_binding,
    validate_materialisation_report,
    validate_repair_precursor,
)

from synthetic_kimodo_blend import MANIFEST, SyntheticKimodoBlendBuilder


ROOT = Path(__file__).resolve().parents[3]
SEED_SYNTHETIC_STRAFE = ROOT / "tools/kimodo-pipeline/tests/seed_synthetic_strafe_action.py"
ASSERT_STRAFE_PRODUCER = ROOT / "tools/kimodo-pipeline/tests/assert_strafe_correction_producer.py"


def test_synthetic_fixture_declares_repair_roles_contacts_and_rejections() -> None:
    actions = {action.kind: action for action in MANIFEST.actions}
    assert actions["loop"].expected_selection
    assert actions["finite_transition"].expected_selection
    assert actions["pivot"].heading_radians[1] < 0
    assert actions["lateral"].contacts == ("right_release",)
    assert actions["arc"].contacts == ("left_release", "right_release")
    assert actions["reversal_contact"].expected_selection == ("sr", "sl")
    assert actions["intentional_failure"].intended_rejection == "missing_required_chest_track"


def test_adaptive_repair_durable_windows_describe_the_selected_support() -> None:
    # An eight-frame selected support must retain the matching, non-overlapping
    # immutable interval rather than reporting the preferred six-frame window.
    validate_durable_repair_windows(
        (0, 7), (8, 24), (25, 32), sample_count=33, opening_width=8, closing_width=8,
    )
    with pytest.raises(ValueError, match="selected repair support"):
        validate_durable_repair_windows(
            (0, 5), (6, 26), (27, 32), sample_count=33, opening_width=8, closing_width=8,
        )
    with pytest.raises(ValueError, match="strict immutable interior"):
        validate_durable_repair_windows(
            (0, 7), (8, 7), (8, 15), sample_count=16, opening_width=8, closing_width=8,
        )


def test_synthetic_builder_writes_only_its_supplied_output() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-repair-") as directory:
        blend = Path(directory) / "fixture.blend"
        result = SyntheticKimodoBlendBuilder().build(blend)
        assert result.returncode == 0, result.stdout + result.stderr
        first_hash = hashlib.sha256(blend.read_bytes()).hexdigest()
        assert first_hash
        assert {path.name for path in Path(directory).iterdir()} == {blend.name}


def test_repair_report_binding_refuses_a_later_mutation(tmp_path: Path) -> None:
    precursor = tmp_path / "immutable-repair-precursor.blend"
    precursor.write_bytes(b"declared repaired bytes")
    digest = hashlib.sha256(precursor.read_bytes()).hexdigest()
    report_path = tmp_path / "repair.json"
    report_path.write_text(json.dumps({
        "output": str(precursor.resolve()), "after_sha256": digest,
    }), encoding="utf-8")
    assert validate_repair_precursor(report_path, precursor)["after_sha256"] == digest

    precursor.write_bytes(b"materialised in place")
    with pytest.raises(WorkflowBindingError, match="no longer match"):
        validate_repair_precursor(report_path, precursor)


def test_materialisation_report_binds_distinct_precursor_and_final_carriers(tmp_path: Path) -> None:
    precursor = tmp_path / "repair-precursor.blend"
    final = tmp_path / "materialised.blend"
    evidence = tmp_path / "selection.json"
    repair_report = tmp_path / "repair.json"
    materialisation_report = tmp_path / "materialisation.json"
    precursor.write_bytes(b"repair")
    final.write_bytes(b"repair plus actions")
    evidence.write_text("{}", encoding="utf-8")
    repair_report.write_text(json.dumps({
        "output": str(precursor.resolve()),
        "after_sha256": hashlib.sha256(precursor.read_bytes()).hexdigest(),
    }), encoding="utf-8")
    provenance = {"wf": {"event_derivation_evidence": {"events": []}}}
    materialisation_report.write_text(json.dumps({
        "contract": "kimodo_materialisation_carrier_binding_v1",
        "precursor": {"path": str(precursor), "sha256": hashlib.sha256(precursor.read_bytes()).hexdigest()},
        "final": {"path": str(final), "sha256": hashlib.sha256(final.read_bytes()).hexdigest()},
        "source_repair_report": {"path": str(repair_report), "sha256": hashlib.sha256(repair_report.read_bytes()).hexdigest()},
        "selection_evidence": {"path": str(evidence), "sha256": hashlib.sha256(evidence.read_bytes()).hexdigest()},
        "action_provenance_sha256": canonical_json_sha256(provenance),
        "roles": ["wf"],
    }), encoding="utf-8")
    validate_materialisation_report(materialisation_report)

    final.write_bytes(b"later mutation")
    with pytest.raises(WorkflowBindingError, match="final bytes changed"):
        validate_materialisation_report(materialisation_report)


def test_cross_carrier_action_provenance_drift_is_refused_independently_of_order() -> None:
    evidence = {
        "xr_l": {"source": {"events": [{"id": "release_l", "frame": 3}]}},
        "xr_r": {"source": {"events": [{"id": "release_r", "frame": 3}]}},
    }
    reordered = {key: evidence[key] for key in reversed(tuple(evidence))}
    assert validate_action_provenance_binding(evidence, reordered) == canonical_json_sha256(evidence)

    drifted = json.loads(json.dumps(reordered))
    drifted["xr_l"]["source"]["events"][0]["frame"] = 4
    with pytest.raises(WorkflowBindingError, match="cross-carrier"):
        validate_action_provenance_binding(evidence, drifted)


def test_full_strafe_repair_producer_applies_correction_and_reopens_provenance() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-repair-producer-") as directory:
        blend = Path(directory) / "fixture.blend"
        built = SyntheticKimodoBlendBuilder().build(blend)
        assert built.returncode == 0, built.stdout + built.stderr
        seeded = run_blender_python([
            "--python", SEED_SYNTHETIC_STRAFE, "--", blend,
        ], ROOT)
        assert seeded.returncode == 0, seeded.stdout + seeded.stderr
        before = hashlib.sha256(blend.read_bytes()).hexdigest()

        produced = run_blender_python([
            blend, "--python", ASSERT_STRAFE_PRODUCER, "--", blend,
        ], ROOT)

        assert produced.returncode == 0, produced.stdout + produced.stderr
        assert hashlib.sha256(blend.read_bytes()).hexdigest() != before
        evidence = json.loads(next(
            row.removeprefix("KIMODO_STRAFE_CORRECTION_PRODUCER=")
            for row in produced.stdout.splitlines()
            if row.startswith("KIMODO_STRAFE_CORRECTION_PRODUCER=")
        ))
        assert max(evidence["before"]) > 15.0
        assert max(evidence["after"]) < 15.0
        assert evidence["provenance_survived_reopen"] is True
