"""Pure report-structure coverage for the idle-source staging contract."""
from __future__ import annotations

import unittest

from kimodo_pipeline.bootstrap_validation import (
    IDLE_SOURCE_REPORT_SCHEMA,
    idle_source_report_structure_failures,
)


def _report() -> dict:
    return {
        "schema": IDLE_SOURCE_REPORT_SCHEMA,
        "generated_by": "kimodo_pipeline/idle_source_bootstrap.py",
        "blender_version": "5.2.2",
        "variant": "ember",
        "character_asset_root": "/fixture/character",
        "baseline_character": "fixture_character",
        "inputs": {
            key: {
                "path": f"/fixture/{key}.blend",
                "sha256_before": key,
                "sha256_after": key,
                "unchanged": True,
            }
            for key in ("canonical_source", "neutral_template", "character_setup", "donor_blend")
        },
        "baseline": {
            key: {"path": f"/fixture/{key}.blend", "sha256": key, "matches_gate1": True}
            for key in ("canonical_source", "neutral_template", "character_setup")
        },
        "donor": {
            "blend": "/fixture/donor_blend.blend",
            "action": "kimodo_fixture_idle",
            "frame_range": [1, 33],
            "curve_count": 39,
        },
        "idle_source": {
            "destination": "/fixture/character/animations/source/idle/ember.blend",
            "staged": "/fixture/temp/staged/ember.blend",
            "published": True,
            "replaced_existing": False,
            "sha256": "f" * 64,
        },
        "admission": {"passed": True, "failures": []},
        "fingerprints": {
            "source_helper_configuration_sha256": {
                "setup": "a" * 64,
                "idle_source": "a" * 64,
                "equal": True,
            }
        },
        "validation": {"passed": True, "failures": []},
    }


class IdleSourceReportStructureTests(unittest.TestCase):
    def test_accepts_a_contract_report(self) -> None:
        self.assertEqual(idle_source_report_structure_failures(_report()), [])

    def test_rejects_a_wrong_schema_and_missing_sections(self) -> None:
        report = _report()
        del report["baseline_character"]
        del report["fingerprints"]
        report["schema"] = "kimodo-idle-source/0"
        failures = idle_source_report_structure_failures(report)
        self.assertTrue(any("'baseline_character'" in failure for failure in failures))
        self.assertTrue(any("parity fingerprints" in failure for failure in failures))
        self.assertTrue(any("kimodo-idle-source/1" in failure for failure in failures))

    def test_rejects_changed_inputs_and_unattested_gate1_digests(self) -> None:
        report = _report()
        report["inputs"]["donor_blend"]["sha256_after"] = "drifted"
        report["inputs"]["donor_blend"]["unchanged"] = False
        report["baseline"]["character_setup"]["matches_gate1"] = False
        failures = idle_source_report_structure_failures(report)
        self.assertTrue(any("'donor_blend' does not attest unchanged bytes" in failure for failure in failures))
        self.assertTrue(any("does not attest the Gate 1 digests" in failure for failure in failures))

    def test_rejects_a_replacement_of_a_protected_idle_source(self) -> None:
        report = _report()
        report["idle_source"]["replaced_existing"] = True
        failures = idle_source_report_structure_failures(report)
        self.assertTrue(any("no protected idle source was replaced" in failure for failure in failures))

    def test_rejects_disagreeing_or_failed_admission_and_validation(self) -> None:
        report = _report()
        report["admission"] = {"passed": True, "failures": ["fixture failure"]}
        self.assertTrue(any("admission flags disagree" in failure for failure in idle_source_report_structure_failures(report)))
        report = _report()
        report["validation"] = {"passed": False, "failures": ["fixture failure"]}
        self.assertTrue(any("must attest a passed validation" in failure for failure in idle_source_report_structure_failures(report)))

    def test_rejects_drifted_parity_fingerprints(self) -> None:
        report = _report()
        report["fingerprints"]["source_helper_configuration_sha256"]["equal"] = False
        failures = idle_source_report_structure_failures(report)
        self.assertTrue(any("does not attest setup parity" in failure for failure in failures))

    def test_rejects_an_incomplete_donor_record(self) -> None:
        report = _report()
        del report["donor"]["frame_range"]
        failures = idle_source_report_structure_failures(report)
        self.assertTrue(any("donor record lacks 'frame_range'" in failure for failure in failures))


if __name__ == "__main__":
    unittest.main()
