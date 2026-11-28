"""Pure setup-parity comparison coverage (KIMODO-SET-002)."""
from __future__ import annotations

from kimodo_pipeline.setup_parity import (
    helper_configuration_mismatches,
    constraint_graph_mismatches,
    parity_fingerprint_records,
    setup_parity_failures,
)


def _identity_helper(name: str, roll: float = 0.0) -> list:
    matrix = [1.0 if index % 5 == 0 else 0.0 for index in range(16)]
    if roll:
        matrix[0] = roll
    return [name, None, matrix, [1.0 if index % 5 == 0 else 0.0 for index in range(16)]]


def _constraint(bone: str, slot: int, kind: str, mute: bool = False, **overrides) -> list:
    row = [bone, slot, kind, "Kimodo_Source", f"{bone}_LOOK", "WORLD", "WORLD", mute, 1.0]
    for index, value in overrides.items():
        row[int(index)] = value
    return row


def _records(mute: bool = True, roll: float = 0.0) -> dict:
    return {
        "target_rig": "Female",
        "target_constraint_edges": [
            _constraint("Root", 0, "COPY_LOCATION"),
            _constraint("Root", 1, "COPY_ROTATION"),
        ],
        "source_constraint_edges": [
            _constraint("thigh_l_RET", 0, "LOCKED_TRACK", mute=mute),
            _constraint("thigh_r_RET", 0, "LOCKED_TRACK", mute=mute),
        ],
        "helper_entries": [
            _identity_helper("thigh_l_RET", roll=roll),
            _identity_helper("thigh_r_RET", roll=roll),
            _identity_helper("lowerarm_l_RET", roll=roll),
            _identity_helper("lowerarm_r_RET", roll=roll),
        ],
    }


def test_identical_records_pass_the_parity_gate() -> None:
    assert setup_parity_failures(_records(), _records()) == []


def test_a_reset_helper_roll_is_named_as_calibration_drift() -> None:
    """Synthetic rest-only drift remains protected independently of pose."""
    approved = _records(roll=0.6109)
    actual = _records()
    failures = setup_parity_failures(approved, actual)
    assert len(failures) == 4
    for helper in ("thigh_l_RET", "thigh_r_RET", "lowerarm_l_RET", "lowerarm_r_RET"):
        assert any(
            helper in failure and "rest basis/roll state" in failure and "calibration drift" in failure
            for failure in failures
        ), failure


def test_an_unmuted_locked_track_is_named_as_calibration_mute_drift() -> None:
    approved = _records(mute=True)
    actual = _records(mute=False)
    failures = setup_parity_failures(approved, actual)
    assert len(failures) == 2
    for failure in failures:
        assert "LOCKED_TRACK" in failure
        assert "mute state differs: approved=True, idle source=False" in failure


def test_helper_and_constraint_rows_are_matched_by_identity() -> None:
    approved = _records()
    actual = _records()
    actual["helper_entries"] = list(reversed(actual["helper_entries"]))
    actual["source_constraint_edges"] = list(reversed(actual["source_constraint_edges"]))
    assert setup_parity_failures(approved, actual) == []


def test_missing_and_extra_helpers_and_constraints_are_named() -> None:
    approved = _records()
    actual = _records()
    actual["helper_entries"] = actual["helper_entries"][:3] + [_identity_helper("calf_l_RET")]
    actual["source_constraint_edges"] = actual["source_constraint_edges"][:1]
    failures = setup_parity_failures(approved, actual)
    assert any("thigh_r_RET" in failure and "absent from the idle source" in failure for failure in failures)
    assert any("'calf_l_RET'" in failure and "present in the idle source but absent" in failure for failure in failures)
    assert any("thigh_r_RET" in failure and "absent from the idle source" in failure for failure in failures)


def test_influence_and_space_binding_drift_are_named() -> None:
    approved = _records()
    actual = _records()
    actual["source_constraint_edges"] = [
        _constraint("thigh_l_RET", 0, "LOCKED_TRACK", mute=True, **{"8": 0.5}),
        _constraint("thigh_r_RET", 0, "LOCKED_TRACK", mute=True, **{"5": "LOCAL"}),
    ]
    failures = setup_parity_failures(approved, actual)
    assert any("influence differs: approved=1.0, idle source=0.5" in failure for failure in failures)
    assert any("target/space binding differs" in failure for failure in failures)


def test_a_different_target_rig_binding_is_refused_by_name() -> None:
    actual = _records()
    actual["target_rig"] = "Reference_Male"
    failures = setup_parity_failures(_records(), actual)
    assert any(
        "target rig differs" in failure and "'Female'" in failure and "'Reference_Male'" in failure
        for failure in failures
    )


def test_helper_mismatches_report_rest_drift() -> None:
    approved = [_identity_helper("thigh_l_RET")]
    actual = [_identity_helper("thigh_l_RET")]
    actual[0][2][6] = 0.35
    failures = helper_configuration_mismatches(approved, actual)
    assert failures == [
        "calibration helper bone 'thigh_l_RET' rest basis/roll state differs from the "
        "approved setup; calibration drift"
    ]


def test_constraint_mismatches_name_the_rig_label_bone_and_slot() -> None:
    failures = constraint_graph_mismatches(
        "target rig",
        [_constraint("Root", 0, "COPY_LOCATION", mute=False)],
        [_constraint("Root", 0, "COPY_LOCATION", mute=False, **{"3": None})],
    )
    assert failures == [
        "target rig constraint 'COPY_LOCATION' on bone 'Root' (slot 0) target/space "
        "binding differs: approved=('Kimodo_Source', 'Root_LOOK', 'WORLD', 'WORLD'), "
        "idle source=(None, 'Root_LOOK', 'WORLD', 'WORLD')"
    ]


def test_parity_fingerprint_records_digest_the_calibration_state() -> None:
    base = parity_fingerprint_records(_records())
    assert base["target_rig"] == "Female"
    assert base["target_constraint_graph_edge_count"] == 2
    assert base["source_constraint_graph_edge_count"] == 2
    assert base["source_helper_bone_count"] == 4
    assert all(
        len(base[key]) == 64
        for key in (
            "target_constraint_graph_sha256",
            "source_constraint_graph_sha256",
            "source_helper_configuration_sha256",
        )
    )
    # Both calibration channels change the fingerprints: a mute flip and a
    # helper roll reset must each register.
    assert parity_fingerprint_records(_records(mute=False))["source_constraint_graph_sha256"] != base[
        "source_constraint_graph_sha256"
    ]
    assert parity_fingerprint_records(_records(roll=0.7))["source_helper_configuration_sha256"] != base[
        "source_helper_configuration_sha256"
    ]
    # An identical record digests identically.
    assert parity_fingerprint_records(_records()) == base


def test_incomplete_parity_records_are_refused_structurally() -> None:
    try:
        parity_fingerprint_records({"target_rig": "Female"})
    except ValueError as error:
        assert "target_constraint_edges" in str(error)
    else:
        raise AssertionError("an incomplete parity record must be refused")


def test_fixed_pose_drift_changes_refusal_and_provenance_without_rest_drift() -> None:
    approved, actual = _records(), _records()
    actual["helper_entries"][0][3][6] = 0.35
    assert setup_parity_failures(approved, actual) == [
        "calibration helper bone 'thigh_l_RET' fixed pose matrix_basis differs "
        "from the approved setup; calibration drift"
    ]
    before, after = parity_fingerprint_records(approved), parity_fingerprint_records(actual)
    assert before["helper_capture_contract"] == "rest-and-unanimated-pose-basis-v2"
    assert before["source_helper_configuration_sha256"] != after["source_helper_configuration_sha256"]
    assert before["source_constraint_graph_sha256"] == after["source_constraint_graph_sha256"]


def test_rest_only_rows_cannot_establish_fixed_pose_parity() -> None:
    actual = _records()
    actual["helper_entries"][0].pop()
    assert "lacks fixed pose matrix_basis evidence" in setup_parity_failures(_records(), actual)[0]
    try:
        parity_fingerprint_records(actual)
    except ValueError as error:
        assert "rest and fixed pose" in str(error)
    else:
        raise AssertionError("rest-only rows cannot produce v2 provenance")
