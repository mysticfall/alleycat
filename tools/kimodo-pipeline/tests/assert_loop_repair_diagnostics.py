"""Blender-side checks for read-only hierarchy repair option diagnostics."""
from __future__ import annotations

import json
import math
import sys
from pathlib import Path

from mathutils import Matrix, Quaternion, Vector

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.materialise import _diagnose_periodic_pose_repair_options


def _rows(sample_count: int, closing_bias: float) -> list[dict[str, Matrix]]:
    rows = []
    for index in range(sample_count):
        phase = index / (sample_count - 1)
        closing_drift = closing_bias * phase * phase
        rows.append({
            name: Matrix.LocRotScale(
                Vector((phase * .03 + closing_drift * factor, math.sin(phase * math.tau) * .01, 0.0)),
                Quaternion(Vector((0.0, 1.0, 0.0)), phase * .08 * factor),
                Vector((1.0, 1.0, 1.0)),
            )
            for name, factor in (("Hips", 1.0), ("LeftFoot", .7), ("RightFoot", .9))
        })
    return rows


def main() -> None:
    windows = {(10, 30): _rows(21, .02), (30, 54): _rows(25, .05)}
    first = _diagnose_periodic_pose_repair_options(
        windows, .433695449, (6, 4), ("smoothstep", "quadratic"),
    )
    second = _diagnose_periodic_pose_repair_options(
        windows, .433695449, (4, 6), ("quadratic", "smoothstep"),
    )
    if first != second:
        raise AssertionError("hierarchy repair diagnostics are not deterministic")
    if not first["read_only"] or first["changes_admission_or_selection"]:
        raise AssertionError("hierarchy repair diagnostics must remain non-authoritative")
    comparisons = first["comparisons"]
    if len(comparisons) != 8 or not all(row["evaluated"] for row in comparisons):
        raise AssertionError(f"unexpected hierarchy diagnostic population: {comparisons}")
    if not all("translation_correction" in row["opening_closing_correction_asymmetry"] for row in comparisons):
        raise AssertionError("hierarchy diagnostics lack correction asymmetry")
    if not all("rotation_vector" in row["axis_angular_correction"]["curvature"]["maximum"] for row in comparisons):
        raise AssertionError("hierarchy diagnostics lack axis-aware angular curvature attribution")
    json.dumps(first, sort_keys=True)
    print("KIMODO_LOOP_REPAIR_DIAGNOSTICS=ok")


if __name__ == "__main__":
    main()
