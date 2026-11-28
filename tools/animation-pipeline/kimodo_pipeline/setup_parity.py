"""Pure setup-parity comparison binding compositions/bakes to approved calibration.

KIMODO-SET-002 requires every bake to pass a setup-parity gate: the
character's approved setup baseline binds through byte digests, and the staged
source's setup-derived rig content must fingerprint-match the approved
setup's constraint/helper calibration — helper rest and fixed pose basis plus
constraint mute state.  The comparisons here operate on the plain entry rows
captured from a live blend (``_constraint_graph_edges`` and
``_helper_configuration_entries`` in ``bootstrap_manifest``) so a refusal can
name the calibration-relevant state — which helper bone drifted, which
constraint changed mute — instead of opaque byte drift.  This module
deliberately imports no ``bpy`` so the parity contract is unit-testable
outside Blender.
"""
from __future__ import annotations

from typing import Any, Mapping, Sequence

from .bootstrap_policy import constraint_graph_fingerprint, helper_configuration_fingerprint

PARITY_RECORD_KEYS = (
    "target_rig",
    "target_constraint_edges",
    "source_constraint_edges",
    "helper_entries",
)


def _constraint_key(row: Sequence[Any]) -> tuple[str, int, str]:
    return (str(row[0]), int(row[1]), str(row[2]))


def _helper_rows_by_bone(rows: Sequence[Sequence[Any]]) -> dict[str, Sequence[Any]]:
    return {str(row[0]): row for row in rows}


def helper_configuration_mismatches(
    approved: Sequence[Sequence[Any]], actual: Sequence[Sequence[Any]]
) -> list[str]:
    """Name every helper bone whose rest or fixed pose calibration drifted."""
    failures: list[str] = []
    approved_map = _helper_rows_by_bone(approved)
    actual_map = _helper_rows_by_bone(actual)
    for name in sorted(set(approved_map) - set(actual_map)):
        failures.append(
            f"calibration helper bone {name!r} is absent from the idle source but "
            "present in the approved setup"
        )
    for name in sorted(set(actual_map) - set(approved_map)):
        failures.append(
            f"calibration helper bone {name!r} is present in the idle source but "
            "absent from the approved setup"
        )
    for name in sorted(set(approved_map) & set(actual_map)):
        approved_row, actual_row = approved_map[name], actual_map[name]
        if approved_row[1] != actual_row[1]:
            failures.append(
                f"calibration helper bone {name!r} parent differs: approved="
                f"{approved_row[1]!r}, idle source={actual_row[1]!r}"
            )
        for column, label in ((2, "rest basis/roll state"), (3, "fixed pose matrix_basis")):
            if len(approved_row) <= column or len(actual_row) <= column:
                failures.append(f"calibration helper bone {name!r} lacks {label} evidence")
            elif approved_row[column] != actual_row[column]:
                failures.append(
                    f"calibration helper bone {name!r} {label} differs from the "
                    "approved setup; calibration drift"
                )
    return failures


def constraint_graph_mismatches(
    rig_label: str,
    approved: Sequence[Sequence[Any]],
    actual: Sequence[Sequence[Any]],
) -> list[str]:
    """Name every constraint whose authored binding, mute, or influence drifted."""
    failures: list[str] = []
    approved_map = {_constraint_key(row): row for row in approved}
    actual_map = {_constraint_key(row): row for row in actual}
    for key in sorted(set(approved_map) - set(actual_map)):
        failures.append(
            f"{rig_label} constraint {key[2]!r} on bone {key[0]!r} (slot {key[1]}) is "
            "absent from the idle source but present in the approved setup"
        )
    for key in sorted(set(actual_map) - set(approved_map)):
        failures.append(
            f"{rig_label} constraint {key[2]!r} on bone {key[0]!r} (slot {key[1]}) is "
            "present in the idle source but absent from the approved setup"
        )
    for key in sorted(set(approved_map) & set(actual_map)):
        approved_row, actual_row = approved_map[key], actual_map[key]
        where = f"{rig_label} constraint {key[2]!r} on bone {key[0]!r} (slot {key[1]})"
        if bool(approved_row[7]) != bool(actual_row[7]):
            failures.append(
                f"{where} mute state differs: approved={bool(approved_row[7])}, idle "
                f"source={bool(actual_row[7])}; calibration mute drift"
            )
        if float(approved_row[8]) != float(actual_row[8]):
            failures.append(
                f"{where} influence differs: approved={approved_row[8]}, idle source="
                f"{actual_row[8]}"
            )
        if tuple(approved_row[3:7]) != tuple(actual_row[3:7]):
            failures.append(
                f"{where} target/space binding differs: approved="
                f"{tuple(approved_row[3:7])}, idle source={tuple(actual_row[3:7])}"
            )
    return failures


def setup_parity_failures(
    approved: Mapping[str, Any], actual: Mapping[str, Any]
) -> list[str]:
    """Compare a composed source's rig records against the approved setup's.

    Every failure names the calibration-relevant state that drifted (KIMODO-
    SET-002), so an un-calibrated staged setup is diagnosed as calibration
    drift rather than opaque byte drift.
    """
    failures: list[str] = []
    approved_rig = approved.get("target_rig")
    actual_rig = actual.get("target_rig")
    if approved_rig != actual_rig:
        failures.append(
            f"the resolved target rig differs: the approved setup binds "
            f"{approved_rig!r}, the source binds {actual_rig!r}"
        )
    failures.extend(
        constraint_graph_mismatches(
            "target rig",
            approved.get("target_constraint_edges", ()),
            actual.get("target_constraint_edges", ()),
        )
    )
    failures.extend(
        constraint_graph_mismatches(
            "Kimodo_Source",
            approved.get("source_constraint_edges", ()),
            actual.get("source_constraint_edges", ()),
        )
    )
    failures.extend(
        helper_configuration_mismatches(
            approved.get("helper_entries", ()),
            actual.get("helper_entries", ()),
        )
    )
    return failures


def parity_fingerprint_records(records: Mapping[str, Any]) -> dict[str, Any]:
    """Digest one captured parity record into the publishable fingerprint set."""
    for key in PARITY_RECORD_KEYS:
        if key not in records:
            raise ValueError(f"A setup-parity record is missing its {key!r} rows.")
    target_edges = list(records["target_constraint_edges"])
    source_edges = list(records["source_constraint_edges"])
    helper_entries = list(records["helper_entries"])
    if any(len(row) != 4 or len(row[2]) != 16 or len(row[3]) != 16 for row in helper_entries):
        raise ValueError("Setup-parity fingerprints require rest and fixed pose 4x4 matrix rows.")
    return {
        "helper_capture_contract": "rest-and-unanimated-pose-basis-v2",
        "target_rig": records["target_rig"],
        "target_constraint_graph_sha256": constraint_graph_fingerprint(target_edges),
        "target_constraint_graph_edge_count": len(target_edges),
        "source_constraint_graph_sha256": constraint_graph_fingerprint(source_edges),
        "source_constraint_graph_edge_count": len(source_edges),
        "source_helper_configuration_sha256": helper_configuration_fingerprint(helper_entries),
        "source_helper_bone_count": len(helper_entries),
    }
