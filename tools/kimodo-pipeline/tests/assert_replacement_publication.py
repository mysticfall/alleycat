"""Capture stale target signatures and prove reopened replacement publication."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import sys

import bpy

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.contracts import ROLE_REGISTRY, action_name
from kimodo_pipeline.materialise import METADATA, ROOT as ROOT_PATH, _action_signature, _curves


def digest(action: object) -> str:
    return hashlib.sha256(repr(_action_signature(action)).encode("utf-8")).hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=("snapshot", "assert"), required=True)
    parser.add_argument("--snapshot", type=Path, required=True)
    parser.add_argument("--variant", required=True)
    parser.add_argument("--roles", required=True)
    options = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    registry = {role.code: role for role in ROLE_REGISTRY}
    targets = {
        code: bpy.data.actions[action_name(options.variant, registry[code])]
        for code in options.roles.split(",")
    }
    if options.mode == "snapshot":
        rows = {}
        for code, action in targets.items():
            assert action.get("anim002_stale_fixture") is True, code
            curves = _curves(action)
            stale_curve = curves[(f"{ROOT_PATH}.location", 0)]
            assert [float(point.co.y) for point in stale_curve.keyframe_points] == [99.0, 101.0]
            assert any(modifier.type == "NOISE" for modifier in stale_curve.modifiers)
            rows[code] = {
                "signature": digest(action),
                "metadata": {key: repr(action[key]) for key in action.keys()},
                "curve_count": len(curves),
            }
        options.snapshot.write_text(json.dumps(rows, sort_keys=True), encoding="utf-8")
        return

    stale = json.loads(options.snapshot.read_text(encoding="utf-8"))
    report = {}
    for code, action in targets.items():
        assert "anim002_stale_fixture" not in action, code
        assert "anim002_stale_payload" not in action, code
        assert all(key in action for key in METADATA), code
        curves = _curves(action)
        assert len(curves) > stale[code]["curve_count"], code
        assert digest(action) != stale[code]["signature"], code
        root_curve = curves[(f"{ROOT_PATH}.location", 2)]
        assert len(root_curve.keyframe_points) == 33, code
        assert all(modifier.type != "NOISE" for curve in curves.values() for modifier in curve.modifiers), code
        report[code] = {
            "stale_signature": stale[code]["signature"],
            "published_signature": digest(action),
            "published_curve_count": len(curves),
            "stale_metadata_removed": True,
            "stale_tracks_removed": True,
        }
    print("KIMODO_REPLACEMENT_PUBLICATION=" + json.dumps(report, sort_keys=True))


main()
