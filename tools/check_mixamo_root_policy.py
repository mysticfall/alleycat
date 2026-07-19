#!/usr/bin/env python3
"""Focused harness for metadata-first Mixamo Root policy decisions."""

from __future__ import annotations

from mixamo_root_policy import (
    ROOT_POLICY_TURN_IN_PLACE,
    RootReconstructionInput,
    avatar_semantic_direction_to_blender_root_planar,
    classify_root_reconstruction,
    straight_moving_diagnostics_from_metadata,
)


def assert_close(actual: float, expected: float, label: str) -> None:
    if abs(actual - expected) > 1e-6:
        raise AssertionError(f"{label}: expected {expected}, got {actual}")


def assert_contains(values: tuple[str, ...], expected: str, label: str) -> None:
    if expected not in values:
        raise AssertionError(f"{label}: expected {expected!r} in {values!r}")


def main() -> None:
    if avatar_semantic_direction_to_blender_root_planar("forward") != (0.0, -1.0):
        raise AssertionError("avatar forward must resolve to Blender Root -Y")
    if avatar_semantic_direction_to_blender_root_planar("left") != (1.0, 0.0):
        raise AssertionError("avatar left must resolve to Blender Root +X")

    forward = straight_moving_diagnostics_from_metadata(
        ("walk", "forward"),
        raw_root_delta=(0.0, 1.0),
        viewpoint_delta=(0.0, -1.0),
    )
    if forward.plan.direction != (0.0, -1.0):
        raise AssertionError(f"forward metadata did not choose Blender Root -Y: {forward.plan.direction!r}")
    assert_close(forward.plan.yaw_radians, 0.0, "forward yaw")
    assert_contains(
        forward.flags,
        "straight_metadata_disagrees_with_original_root_direction",
        "forward raw disagreement",
    )

    strafe = straight_moving_diagnostics_from_metadata(
        ("walk", "strafe", "left"),
        raw_root_delta=(-1.0, 0.0),
        viewpoint_delta=(1.0, 0.0),
    )
    if strafe.plan.direction != (1.0, 0.0):
        raise AssertionError(f"left strafe metadata did not choose Blender Root +X: {strafe.plan.direction!r}")
    assert_close(strafe.plan.yaw_radians, 0.0, "left strafe yaw")
    assert_contains(
        strafe.flags,
        "straight_metadata_disagrees_with_original_root_direction",
        "left strafe raw disagreement",
    )

    backward = straight_moving_diagnostics_from_metadata(
        ("walk", "backward"),
        raw_root_delta=(0.0, -1.0),
        viewpoint_delta=(0.0, 1.0),
    )
    if backward.plan.direction != (0.0, 1.0):
        raise AssertionError(f"backward metadata did not choose Blender Root +Y: {backward.plan.direction!r}")
    assert_close(backward.plan.yaw_radians, 0.0, "backward yaw")

    turn = classify_root_reconstruction(
        RootReconstructionInput(
            category="locomotion",
            motion_class="turn",
            tags=("turn", "in_place", "90", "left"),
        )
    )
    if turn.policy != ROOT_POLICY_TURN_IN_PLACE:
        raise AssertionError(f"turn/in_place policy mismatch: {turn.policy!r}")

    print(
        "metadata-first root policy checks passed: "
        f"forward={forward.plan}, strafe={strafe.plan}, backward={backward.plan}, "
        f"turn_policy={turn.policy!r}"
    )


if __name__ == "__main__":
    main()
