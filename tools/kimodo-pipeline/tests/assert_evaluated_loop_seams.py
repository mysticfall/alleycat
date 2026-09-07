"""Report evaluated full-hierarchy loop seams from a disposable catalogue."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import bpy

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.contracts import ROLE_REGISTRY, action_name
from kimodo_pipeline.materialise import _loop_hierarchy_seam_metrics, _rig, _validate_loop_hierarchy_seam


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--variant", required=True)
    parser.add_argument("--roles", required=True)
    arguments = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    requested = arguments.roles.split(",")
    declarations = {role.code: role for role in ROLE_REGISTRY if role.is_loop}
    rig, _, _ = _rig()
    report = {}
    for code in requested:
        role = declarations[code]
        action = bpy.data.actions[action_name(arguments.variant, role)]
        _validate_loop_hierarchy_seam(rig, action, role)
        report[code] = _loop_hierarchy_seam_metrics(rig, action)
    first = next(iter(report.values()))
    aggregate = {
        "roles": requested,
        "maxima": {
            key: max(row["maxima"][key] for row in report.values())
            for key in first["maxima"]
        },
        "landmarks": {
            name: {
                key: max(row["landmarks"][name][key] for row in report.values())
                for key in first["landmarks"][name]
            }
            for name in ("Hips", "Chest", "Head", "LeftFoot", "RightFoot")
        },
        "root_second_cycle_accumulation_error": {
            key: max(row["cycle_extension_fidelity"]["root_second_cycle_accumulation_error"][key] for row in report.values())
            for key in ("translation", "yaw")
        },
    }
    print("KIMODO_EVALUATED_LOOP_SEAMS=" + json.dumps(report, sort_keys=True))
    print("KIMODO_EVALUATED_LOOP_SEAM_AGGREGATE=" + json.dumps(aggregate, sort_keys=True))


main()
