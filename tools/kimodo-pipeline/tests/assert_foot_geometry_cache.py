"""Blender-side regression checks for materialisation source geometry caching."""
from __future__ import annotations

import sys
from pathlib import Path

import bpy

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.discovery import _action_fcurves, _segments
from kimodo_pipeline.compatibility import ensure_registered
from kimodo_pipeline.materialise import (
    EvaluatedFootGeometryCache,
    _candidate_phase_evidence,
    _candidate_quality_metrics,
    _foot_geometry,
    _rig,
)
from kimodo_pipeline.selection import SelectionError, quality_evidence, role_window, score_candidate, select_candidate


def _first_difference(left, right, path="snapshot"):
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
    if isinstance(left, (list, tuple)):
        if len(left) != len(right):
            return f"{path}: lengths {len(left)} != {len(right)}"
        for index, (left_item, right_item) in enumerate(zip(left, right)):
            difference = _first_difference(left_item, right_item, f"{path}[{index}]")
            if difference:
                return difference
        return None
    return None if left == right else f"{path}: {left!r} != {right!r}"


def _selection_snapshot(rig, basis, sources, segment, role_code: str, cached: bool):
    cache = EvaluatedFootGeometryCache(rig, basis) if cached else None
    candidates = []
    phase = {}
    for action in sources:
        geometry = cache.get(action, segment["start_frame"], segment["end_frame"]) if cache else _foot_geometry(
            rig, action, segment["start_frame"], segment["end_frame"], basis
        )
        phase[action.name] = _candidate_phase_evidence(segment, role_code, geometry)
        try:
            start, end = role_window(segment, role_code, _action_fcurves(action), geometry)
        except SelectionError as error:
            phase[action.name]["window_error"] = str(error)
            continue
        candidates.append(score_candidate(action.name, _action_fcurves(action), start, end))
    metrics = {
        candidate.action_name: _candidate_quality_metrics(
            rig, bpy.data.actions[candidate.action_name], candidate.start, candidate.end, basis, cache
        )
        for candidate in candidates
    }
    quality = quality_evidence(metrics)
    retained = [candidate for candidate in candidates if quality[candidate.action_name]["accepted"]]
    if not retained:
        raise AssertionError(f"cache regression fixture has no eligible {role_code} candidate")
    snapshot = {
        "phase": phase,
        "metrics": metrics,
        "quality": quality,
        "candidates": [(candidate.action_name, candidate.start, candidate.end, candidate.score, candidate.evidence) for candidate in candidates],
        "selected": select_candidate(retained),
    }
    return snapshot, cache


def assert_cache_equivalence() -> None:
    rig, _, basis = _rig()
    sources = sorted((action for action in bpy.data.actions if action.name.startswith("kimodo_multi_")), key=lambda action: action.name)
    segments = {item["index"]: item for item in _segments()}

    # These roles exercise the distinct evidence semantics: Root-forward
    # Segment 0, lateral named-foot recurrence Segment 4, and cyclic arc
    # recurrence Segment 5.  Compare every selection input and result rather
    # than merely proving cache hits return a value.
    snapshots = {}
    cache = None
    for role_code, segment_index in (("wf_r", 0), ("xr_l", 4), ("ar_l", 5)):
        segment = segments[segment_index]
        direct, _ = _selection_snapshot(rig, basis, sources, segment, role_code, False)
        cached, current_cache = _selection_snapshot(rig, basis, sources, segment, role_code, True)
        assert direct == cached, (
            f"cached source geometry changed {role_code} phase, metrics, quality, candidate, or evidence selection; "
            f"first difference: {_first_difference(direct, cached)}"
        )
        snapshots[role_code] = cached
        if role_code == "wf_r":
            cache = current_cache
        else:
            if current_cache is None:
                raise AssertionError(f"cache regression did not create the {role_code} cache")
            current_cache.close()
    if cache is None:
        raise AssertionError("cache regression did not retain the Segment-0 cache")

    for action_phase in snapshots["xr_l"]["phase"].values():
        assert action_phase["axis"] == "lateral"
    for action_phase in snapshots["ar_l"]["phase"].values():
        assert action_phase["axis"] == "arc_tangent"

    segment = segments[0]
    action = sources[0]
    original = cache.get(action, segment["start_frame"], segment["end_frame"])
    assert cache.get(action, segment["start_frame"], segment["end_frame"]) is original
    before_range_miss = cache.misses
    cache.get(action, segment["start_frame"] + 1, segment["end_frame"])
    assert cache.misses == before_range_miss + 1, "cache reused a changed frame range"

    curve = next(
        curve for curve in _action_fcurves(action)
        if curve.data_path == 'pose.bones["Hips"].location' and curve.array_index == 2
    )
    point = curve.keyframe_points[0]
    original_value = float(point.co.y)
    try:
        point.co.y = original_value + 0.001
        action.update_tag()
        before_mutation_miss = cache.misses
        resampled = cache.get(action, segment["start_frame"], segment["end_frame"])
        assert cache.misses == before_mutation_miss + 1, "cache reused geometry after Action mutation"
        assert resampled is not original, "cache returned the pre-mutation source snapshot"
    finally:
        point.co.y = original_value
        action.update_tag()
    cache.close()
    try:
        cache.get(action, segment["start_frame"], segment["end_frame"])
    except RuntimeError:
        pass
    else:
        raise AssertionError("closed source geometry cache accepted a post-selection lookup")


def main() -> None:
    ensure_registered()
    assert_cache_equivalence()


if __name__ == "__main__":
    main()
