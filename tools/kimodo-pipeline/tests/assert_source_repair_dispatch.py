"""Pure dispatch and provenance-invariance regressions run inside Blender."""
from __future__ import annotations

import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.source_repair import (
    ARC_ANATOMICAL_FIT_ALGORITHM_VERSION,
    BOUNDARY_ALGORITHM_VERSION,
    RESHAPE_ALGORITHM_VERSION,
    TRAJECTORY_NORMALISER_VERSION,
    normaliser_algorithms_for_roles,
    rank_semantic_candidate_evidence,
    source_repair_window,
    SourceRepairError,
)
from kimodo_pipeline.precision_step import select_precision_candidate


families = {
    "strafe": ("xr_l",),
    "precision_lateral": ("sr", "sl"),
    "arc": ("ar_l", "al_r"),
    "pivot": ("pr45", "pl45"),
}
plans = {name: normaliser_algorithms_for_roles(roles) for name, roles in families.items()}
assert plans["strafe"] == (
    BOUNDARY_ALGORITHM_VERSION,
    TRAJECTORY_NORMALISER_VERSION,
)
assert plans["arc"] == (BOUNDARY_ALGORITHM_VERSION,)
assert all(
    RESHAPE_ALGORITHM_VERSION not in algorithms
    for algorithms in plans.values()
)
assert all(ARC_ANATOMICAL_FIT_ALGORITHM_VERSION not in algorithms for algorithms in plans.values())

baseline = [
    {
        "source_action": "kimodo_multi_a",
        "source_range": [261, 313],
        "repair_cost": 2.5,
        "source_window_content_signature": "content-b",
    },
    {
        "source_action": "kimodo_multi_b",
        "source_range": [253, 305],
        "repair_cost": 1.5,
        "source_window_content_signature": "content-a",
    },
]


def decision(rows: list[dict[str, object]]) -> tuple[float, str]:
    selected = rank_semantic_candidate_evidence(rows)[0]
    return float(selected["repair_cost"]), str(selected["source_window_content_signature"])


expected = decision(baseline)
renamed = [{**row, "source_action": f"renamed_{index}"} for index, row in enumerate(baseline)]
shifted = [
    {**row, "source_range": [value + 700 for value in row["source_range"]]}
    for row in baseline
]
reordered = list(reversed(baseline))
equivalent_alternative = [
    {
        **baseline[1],
        "source_action": "equivalent_other_identity",
        "source_range": [901, 953],
    },
    *baseline,
]
assert decision(renamed) == expected
assert decision(shifted) == expected
assert decision(reordered) == expected
assert decision(equivalent_alternative) == expected

precision_baseline = [
    {
        "phase_compatible": True,
        "foot_order_accepted": True,
        "quality_accepted": True,
        "repair_accepted": True,
        "repair_cost": 0.4,
        "source_window_content_signature": "eligible",
        "source_action": "identity_a",
        "source_range": [121, 160],
    },
    {
        "phase_compatible": True,
        "foot_order_accepted": False,
        "quality_accepted": True,
        "repair_accepted": True,
        "repair_cost": 0.01,
        "source_window_content_signature": "crossing",
        "source_action": "identity_b",
        "source_range": [121, 160],
    },
]
for rows in (
    precision_baseline,
    list(reversed(precision_baseline)),
    [{**row, "source_action": f"renamed_{index}"} for index, row in enumerate(precision_baseline)],
    [{**row, "source_range": [821, 860]} for row in precision_baseline],
):
    assert select_precision_candidate(rows)["source_window_content_signature"] == "eligible"


class StalePrecisionAction(dict):
    name = "kimodo_multi_repair_stale_precision"


stale = StalePrecisionAction({
    "anim002_source_roles": json.dumps(["sl", "sr"]),
    "anim002_source_content_repair": json.dumps({"derived_range": [121, 160]}),
})
try:
    source_repair_window(stale, "sr")
except SourceRepairError as error:
    assert "stale precision repair derivative" in str(error)
else:
    raise AssertionError("stale precision derivative was admitted")

print("KIMODO_SOURCE_REPAIR_DISPATCH=" + json.dumps({
    "families_invoked": sorted(families),
    "normaliser_plans": {key: list(value) for key, value in plans.items()},
    "provenance_invariance": ["renamed", "shifted_range", "reordered", "equivalent_alternative"],
    "precision_gate_precedes_cost": True,
    "stale_precision_derivative_excluded": True,
}, sort_keys=True))
