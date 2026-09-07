"""Assert role-scoped admission for the selected real strafe repair."""
from __future__ import annotations

import json
import sys
from pathlib import Path

import bpy

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.source_repair import (
    PROVENANCE_PROPERTY,
    action_is_candidate_for_role,
    source_repair_roles,
)

INELIGIBLE_ACTION_NAME = "kimodo_multi_ineligible_strafe_repair"


def main() -> None:
    arguments = sys.argv[sys.argv.index("--") + 1:]
    evidence = json.loads(Path(arguments[arguments.index("--evidence") + 1]).read_text(encoding="utf-8"))
    selected = evidence["roles"]["xr_r"]
    action = bpy.data.actions[selected["selected"]]
    roles = source_repair_roles(action)
    if roles is None:
        assert action.get("anim002_synthetic_fixture") == "deterministic_hierarchy_strafe_v1"
    else:
        assert roles == frozenset({"xr_r"}), (action.name, roles)
        provenance = json.loads(action[PROVENANCE_PROPERTY])
        assert provenance["contract"] == "symmetric_two_ended_hierarchy_pose_repair_v1"
        assert provenance["role_family"] == "strafe"
        assert provenance["semantic_source_role"] == "xr_r"
        assert provenance["roles"] == ["xr_r"]
        assert provenance["derived_range"] == selected["selected_source_range"]
        assert provenance["phase_contact_evidence"]["source_release_frames"] == selected["selected_source_range"]

    candidate = next(
        row for row in selected["candidates"]
        if row["action"] == action.name and row["selection"]["accepted"]
    )
    assert candidate["selection"]["recurrence"]
    assert candidate["selection"]["loop_construction"]["accepted"]

    # This malformed repair must be discarded by role confinement before its
    # stale provenance can be inspected or admitted as an xr_r candidate.
    ineligible = bpy.data.actions[INELIGIBLE_ACTION_NAME]
    assert not action_is_candidate_for_role(ineligible, "xr_r")
    assert all(
        row["action"] != INELIGIBLE_ACTION_NAME
        for role in evidence["roles"].values()
        for row in role["candidates"]
    )

    print("KIMODO_STRAFE_SOURCE_SELECTION=" + json.dumps({
        "selected": action.name,
        "role_confined": roles is not None,
        "provenance_current": roles is not None,
        "recurrence_accepted": True,
        "ineligible_derivative_excluded": True,
    }, sort_keys=True))


main()
