"""Snapshot source Action geometry independently of published output Actions."""
from __future__ import annotations

from dataclasses import asdict
import json
from pathlib import Path
import sys

import bpy

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.compatibility import ensure_registered
from kimodo_pipeline.materialise import (
    EvaluatedFootGeometryCache,
    _action_signature,
    _candidate_phase_evidence,
    _candidate_quality_metrics,
    _foot_geometry,
    _rig,
)


def _geometry_payload(geometry):
    return {
        side: {str(frame): asdict(sample) for frame, sample in samples.items()}
        for side, samples in geometry.items()
    }


def _stable_evidence(value):
    """Canonicalise below Blender's evaluated matrix precision, not budgets."""
    if isinstance(value, float):
        return round(value, 5)
    if isinstance(value, dict):
        return {str(key): _stable_evidence(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_stable_evidence(item) for item in value]
    return value


def _first_difference(left, right, path="payload"):
    if type(left) is not type(right):
        return f"{path}: {type(left).__name__} != {type(right).__name__}"
    if isinstance(left, dict):
        if left.keys() != right.keys():
            return f"{path}: keys {left.keys()} != {right.keys()}"
        for key in left:
            difference = _first_difference(left[key], right[key], f"{path}[{key!r}]")
            if difference:
                return difference
        return None
    if isinstance(left, list):
        if len(left) != len(right):
            return f"{path}: lengths {len(left)} != {len(right)}"
        for index, (left_item, right_item) in enumerate(zip(left, right)):
            difference = _first_difference(left_item, right_item, f"{path}[{index}]")
            if difference:
                return difference
        return None
    return None if left == right else f"{path}: {left!r} != {right!r}"


def main() -> None:
    arguments = sys.argv[sys.argv.index("--") + 1:]
    mode = arguments[arguments.index("--mode") + 1]
    snapshot = Path(arguments[arguments.index("--snapshot") + 1]).resolve()
    action_name = arguments[arguments.index("--action") + 1]
    role = arguments[arguments.index("--role") + 1]
    start = int(arguments[arguments.index("--start") + 1])
    end = int(arguments[arguments.index("--end") + 1])
    generated_prefix = arguments[arguments.index("--generated-prefix") + 1]

    ensure_registered()
    rig, _, basis = _rig()
    action = bpy.data.actions[action_name]
    direct = _foot_geometry(rig, action, start, end, basis)
    cache = EvaluatedFootGeometryCache(rig, basis)
    cached = cache.get(action, start, end)
    assert _stable_evidence(_geometry_payload(direct)) == _stable_evidence(_geometry_payload(cached)), (
        "source cache differs from direct evaluated geometry beyond Blender evaluation precision"
    )
    assert cache.get(action, start, end) is cached
    direct_metrics = _candidate_quality_metrics(rig, action, start, end, basis)
    cached_metrics = _candidate_quality_metrics(rig, action, start, end, basis, cache)
    assert _stable_evidence(direct_metrics) == _stable_evidence(cached_metrics), (
        "source cache changes quality evidence beyond Blender evaluation precision"
    )
    phase = _candidate_phase_evidence(
        {"start_frame": start, "end_frame": end}, role, direct
    )
    cache.close()

    payload = {
        "action": action_name,
        "action_signature": _stable_evidence(_action_signature(action)),
        "geometry": _stable_evidence(_geometry_payload(direct)),
        "metrics": _stable_evidence(direct_metrics),
        "phase": _stable_evidence(phase),
        "repair_provenance": {
            key: action[key]
            for key in sorted(action.keys())
            if "repair" in key or "reshape" in key or "provenance" in key
        },
    }
    assert payload["repair_provenance"], "fixture source lacks durable repaired-source provenance"
    generated_count = sum(action.name.startswith(generated_prefix) for action in bpy.data.actions)
    if mode == "snapshot":
        assert generated_count == 0, "source snapshot already contains the target generated output"
        snapshot.write_text(json.dumps(payload, indent=2, sort_keys=True), encoding="utf-8")
    elif mode == "assert":
        assert generated_count > 0, "invariance assertion requires reopened target generated output"
        expected = json.loads(snapshot.read_text(encoding="utf-8"))
        assert payload == expected, (
            "generated output changed repaired source Action geometry/cache evidence; "
            f"first difference: {_first_difference(payload, expected)}"
        )
    else:
        raise AssertionError(f"unsupported mode: {mode}")
    print("KIMODO_SOURCE_GEOMETRY_INVARIANCE=" + json.dumps({
        "mode": mode,
        "action": action_name,
        "generated_action_count": generated_count,
        "source_invariant": True,
    }, sort_keys=True))


main()
