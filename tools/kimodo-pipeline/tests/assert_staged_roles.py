"""Capture and verify exact non-target Action preservation in a disposable blend."""
from __future__ import annotations

import hashlib
import json
import sys
from pathlib import Path
from typing import Any

import bpy

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.contracts import ROLE_REGISTRY, action_name
from kimodo_pipeline.discovery import _action_fcurves


def _value(value: Any) -> Any:
    if hasattr(value, "to_list"):
        return _value(value.to_list())
    if isinstance(value, (list, tuple)):
        return [_value(item) for item in value]
    if isinstance(value, dict):
        return {str(key): _value(item) for key, item in sorted(value.items())}
    return value


def _signature(action: Any) -> dict[str, Any]:
    curves = []
    for curve in sorted(_action_fcurves(action), key=lambda item: (item.data_path, item.array_index)):
        curves.append({
            "path": curve.data_path,
            "index": curve.array_index,
            "extrapolation": curve.extrapolation,
            "keys": [
                [list(point.co), list(point.handle_left), list(point.handle_right), point.interpolation, point.handle_left_type, point.handle_right_type]
                for point in curve.keyframe_points
            ],
            "modifiers": [_modifier_signature(modifier) for modifier in curve.modifiers],
        })
    result = {
        "fake_user": action.use_fake_user,
        "frame_range": list(action.frame_range),
        "metadata": {key: _value(action[key]) for key in sorted(action.keys())},
        "slots": [[slot.identifier, slot.target_id_type] for slot in action.slots],
        "fcurves": curves,
    }
    result["hash"] = hashlib.sha256(json.dumps(result, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    return result


def _modifier_signature(modifier: Any) -> dict[str, Any]:
    properties = {}
    for property_definition in modifier.bl_rna.properties:
        if property_definition.identifier == "rna_type" or property_definition.is_readonly:
            continue
        if property_definition.type in {"POINTER", "COLLECTION"}:
            continue
        properties[property_definition.identifier] = _value(getattr(modifier, property_definition.identifier))
    return {"type": modifier.type, "properties": properties}


def main() -> None:
    arguments = sys.argv[sys.argv.index("--") + 1:]
    mode, snapshot_path, variant, target_codes = arguments
    registry = {role.code: role for role in ROLE_REGISTRY}
    target_names = {action_name(variant, registry[code]) for code in target_codes.split(",")}
    signatures = {action.name: _signature(action) for action in bpy.data.actions if action.name not in target_names}
    path = Path(snapshot_path)
    if mode == "snapshot":
        path.write_text(json.dumps(signatures, sort_keys=True), encoding="utf-8")
        return
    expected = json.loads(path.read_text(encoding="utf-8"))
    assert signatures == expected, "non-target Action content changed"
    assert target_names <= {action.name for action in bpy.data.actions}, "requested target Action was not materialised"
    assert "pose_ref_idle" in signatures, "reference Action was not retained"
    assert any(name.startswith("kimodo_multi_") for name in signatures), "source Actions were not retained"
    if "diagnostic_action" in expected:
        assert "diagnostic_action" in signatures, "diagnostic Action was not retained"


main()
