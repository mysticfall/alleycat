"""Byte and provenance bindings for disposable repair/materialisation chains."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Any, Mapping


MATERIALISATION_BINDING_CONTRACT = "kimodo_materialisation_carrier_binding_v1"


class WorkflowBindingError(ValueError):
    """A workflow carrier no longer matches its declared immutable bytes."""


def sha256_file(path: Path) -> str:
    """Hash one declared carrier without filesystem discovery heuristics."""
    return hashlib.sha256(path.read_bytes()).hexdigest()


def canonical_json_sha256(value: Any) -> str:
    """Hash semantic JSON independently of object insertion order."""
    payload = json.dumps(value, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


def validate_repair_precursor(report_path: Path, precursor_path: Path) -> dict[str, Any]:
    """Require a repair report to name the exact immutable precursor bytes."""
    report_file = report_path.expanduser().resolve()
    precursor = precursor_path.expanduser().resolve()
    try:
        report = json.loads(report_file.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise WorkflowBindingError(f"cannot read source-repair report: {report_file}") from error
    if not isinstance(report, dict):
        raise WorkflowBindingError("source-repair report must be a JSON object")
    declared_path = report.get("output")
    declared_hash = report.get("after_sha256")
    if not isinstance(declared_path, str) or Path(declared_path).expanduser().resolve() != precursor:
        raise WorkflowBindingError("source-repair report does not name the declared precursor path")
    if not isinstance(declared_hash, str) or len(declared_hash) != 64:
        raise WorkflowBindingError("source-repair report has no valid output digest")
    if not precursor.is_file() or sha256_file(precursor) != declared_hash:
        raise WorkflowBindingError("source-repair precursor bytes no longer match the report")
    return report


def validate_action_provenance_binding(
    evidence_rows: Mapping[str, Any], action_rows: Mapping[str, Any],
) -> str:
    """Bind external evidence to the same durable per-Action provenance payloads."""
    if dict(evidence_rows) != dict(action_rows):
        raise WorkflowBindingError("cross-carrier Action provenance drifted")
    return canonical_json_sha256(evidence_rows)


def validate_materialisation_report(report_path: Path) -> dict[str, Any]:
    """Revalidate all byte carriers named by a materialisation report."""
    path = report_path.expanduser().resolve()
    try:
        report = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise WorkflowBindingError(f"cannot read materialisation report: {path}") from error
    if not isinstance(report, dict) or report.get("contract") != MATERIALISATION_BINDING_CONTRACT:
        raise WorkflowBindingError("unsupported materialisation binding report")
    for label in ("precursor", "final", "selection_evidence", "source_repair_report"):
        carrier = report.get(label)
        if not isinstance(carrier, dict) or set(carrier) != {"path", "sha256"}:
            raise WorkflowBindingError(f"materialisation report has an invalid {label} carrier")
        carrier_path = Path(str(carrier["path"])).expanduser().resolve()
        if not carrier_path.is_file() or sha256_file(carrier_path) != carrier["sha256"]:
            raise WorkflowBindingError(f"materialisation report-bound {label} bytes changed")
    precursor = Path(report["precursor"]["path"]).expanduser().resolve()
    validate_repair_precursor(Path(report["source_repair_report"]["path"]), precursor)
    if report["precursor"]["sha256"] == report["final"]["sha256"]:
        raise WorkflowBindingError("materialisation report falsely claims unchanged precursor bytes")
    digest = report.get("action_provenance_sha256")
    if not isinstance(digest, str) or len(digest) != 64:
        raise WorkflowBindingError("materialisation report has no Action provenance digest")
    return report
