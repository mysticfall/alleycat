"""Exercise the complete strafe repair producer on a disposable synthetic blend."""
from __future__ import annotations

import json
from pathlib import Path
import sys

import bpy


ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.contracts import ROLE_REGISTRY
from kimodo_pipeline.compatibility import ensure_registered
from kimodo_pipeline.materialise import _rig, _segments, _straight_source_projection
from kimodo_pipeline.source_repair import (
    PROVENANCE_PROPERTY,
    _select_strafe_candidate,
    _semantic_source_candidates,
    derive_repaired_action,
    source_repair_window,
)


SOURCE_ACTION = "kimodo_multi_synthetic_strafe"
DERIVED_ACTION = "kimodo_multi_repair_synthetic_xr_l"
START = 162
END = 178


def main() -> None:
    blend = Path(sys.argv[sys.argv.index("--") + 1]).resolve()
    ensure_registered()
    bpy.ops.wm.open_mainfile(filepath=str(blend), load_ui=False, use_scripts=False)
    rig, _, rig_basis = _rig()
    segment = next(item for item in _segments() if item["index"] == 4)
    reference = bpy.data.actions.get("pose_ref_idle")
    candidates = _semantic_source_candidates(
        rig, segment, (reference,) if reference is not None else (),
    )
    source, selected_range, selection = _select_strafe_candidate(
        rig, candidates, "xr_l", segment, rig_basis,
    )
    assert source.name == SOURCE_ACTION
    assert selected_range == (START, END)
    selected_candidate = next(item for item in selection["candidates"] if item.get("selected"))
    selected_trajectory = selected_candidate["trajectory_normalisation"]
    assert max(selected_trajectory["subwindow_angle_degrees_before"]) > 15.0
    assert max(selected_trajectory["subwindow_angle_degrees_after"]) < 15.0
    provenance = derive_repaired_action(
        rig, source, START, END, DERIVED_ACTION, ("xr_l",), selection,
    )
    trajectory = provenance["trajectory_normalisation"]
    assert trajectory["applied"] is True
    assert max(trajectory["subwindow_angle_degrees_before"]) > 15.0
    assert max(trajectory["subwindow_angle_degrees_after"]) < 15.0
    assert trajectory["source_pacing_changed"] is False
    repair_selection = provenance["repair_option_selection"]
    assert repair_selection["contract"] == "fixed_symmetric_two_ended_source_repair_v1"
    assert len(repair_selection["selected_rank_key"]) == 1
    assert isinstance(repair_selection["selected_rank_key"][0], float)

    role = next(item for item in ROLE_REGISTRY if item.code == "xr_l")
    _, _, _, travel = _straight_source_projection(
        rig, role, bpy.data.actions[DERIVED_ACTION], list(range(START, END + 1)), rig_basis,
    )
    assert travel["applicability"] == "straight_directional"
    assert travel["declared_direction_lateral_forward"] == [-1.0, 0.0]

    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=str(blend), check_existing=False)
    bpy.ops.wm.open_mainfile(filepath=str(blend), load_ui=False, use_scripts=False)
    durable = bpy.data.actions[DERIVED_ACTION]
    durable_provenance = json.loads(durable[PROVENANCE_PROPERTY])
    assert durable_provenance["trajectory_normalisation"] == trajectory
    assert json.loads(durable["anim002_trajectory_normalisation"]) == trajectory
    assert source_repair_window(durable, "xr_l") == (START, END)
    print("KIMODO_STRAFE_CORRECTION_PRODUCER=" + json.dumps({
        "before": trajectory["subwindow_angle_degrees_before"],
        "after": trajectory["subwindow_angle_degrees_after"],
        "derived_action": DERIVED_ACTION,
        "source_range": [START, END],
        "provenance_survived_reopen": True,
    }, sort_keys=True))


main()
