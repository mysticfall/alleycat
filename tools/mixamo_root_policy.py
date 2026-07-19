#!/usr/bin/env python3
"""Pure metadata policy helpers for Mixamo Root reconstruction."""

from __future__ import annotations

import math
from dataclasses import dataclass


ROOT_POLICY_MOVING = "Moving"
ROOT_POLICY_TURN_IN_PLACE = "Turn-In-Place"
ROOT_POLICY_STATIONARY = "Stationary"

ROOT_SUBTYPE_STRAIGHT_MOVING = "StraightMoving"
ROOT_SUBTYPE_CURVED_MOVING = "CurvedMoving"

# Straight-moving policy directions are stored in the Blender action/root frame used by
# retarget_mixamo_animation.py: planar (X, Y) with Z up, Root rest tail along +Y.
# They are *not* Godot world-space vectors.  The reference rig has a 180° yawed
# runtime container, so avatar-semantic forward/right in skeleton-local space
# resolves to +Z/-X.  In this authoring frame that maps to -Y/-X respectively;
# using +Y for metadata "forward" makes the reconstructed Root move opposite to
# the visible Mixamo character travel after import.
STRAIGHT_DIRECTION_FORWARD = (0.0, -1.0)
STRAIGHT_DIRECTION_BACKWARD = (0.0, 1.0)
STRAIGHT_DIRECTION_LEFT = (1.0, 0.0)
STRAIGHT_DIRECTION_RIGHT = (-1.0, 0.0)


def avatar_semantic_direction_to_blender_root_planar(direction: str) -> tuple[float, float]:
    """Return an avatar-semantic direction in Blender action/root planar coordinates.

    The input names describe character intent (forward/back/left/right), while the
    output is the authoring Root coordinate frame consumed by Blender animation
    tracks: (X, Y) on the ground plane with Z up.  This isolates the reference
    rig's 180° runtime-container yaw from metadata policy decisions.
    """

    match direction.strip().lower():
        case "forward":
            return STRAIGHT_DIRECTION_FORWARD
        case "backward" | "back":
            return STRAIGHT_DIRECTION_BACKWARD
        case "left":
            return STRAIGHT_DIRECTION_LEFT
        case "right":
            return STRAIGHT_DIRECTION_RIGHT
        case _:
            raise ValueError(f"Unknown avatar semantic direction: {direction!r}")


@dataclass(frozen=True)
class RootReconstructionClassification:
    policy: str
    subtype: str | None


@dataclass(frozen=True)
class RootReconstructionInput:
    category: str
    motion_class: str
    tags: tuple[str, ...]


@dataclass(frozen=True)
class StraightMovingPlan:
    direction: tuple[float, float]
    yaw_radians: float
    direction_source: str


@dataclass(frozen=True)
class StraightMovingDiagnostics:
    plan: StraightMovingPlan
    raw_direction_disagreement_radians: float | None
    viewpoint_direction_disagreement_radians: float | None
    flags: tuple[str, ...]


def normalise_metadata(metadata: RootReconstructionInput) -> RootReconstructionInput:
    return RootReconstructionInput(
        category=metadata.category.strip().lower(),
        motion_class=metadata.motion_class.strip().lower(),
        tags=tuple(tag.strip().lower() for tag in metadata.tags if tag.strip()),
    )


def classify_root_reconstruction(metadata: RootReconstructionInput) -> RootReconstructionClassification:
    metadata = normalise_metadata(metadata)
    tags = set(metadata.tags)
    if "turn" in tags and "in_place" in tags:
        return RootReconstructionClassification(policy=ROOT_POLICY_TURN_IN_PLACE, subtype=None)
    if metadata.category == "locomotion" and (
        tags.intersection({"walk", "run", "strafe", "sidestep", "crouch", "start", "stop", "backward", "forward"})
        or "locomotion" in metadata.motion_class
        or "step" in metadata.motion_class
    ):
        subtype = ROOT_SUBTYPE_CURVED_MOVING if "arc" in tags else ROOT_SUBTYPE_STRAIGHT_MOVING
        return RootReconstructionClassification(policy=ROOT_POLICY_MOVING, subtype=subtype)
    return RootReconstructionClassification(policy=ROOT_POLICY_STATIONARY, subtype=None)


def straight_moving_plan_from_metadata(tags: tuple[str, ...]) -> StraightMovingPlan:
    tag_set = {tag.strip().lower() for tag in tags if tag.strip()}
    lateral = "strafe" in tag_set or "sidestep" in tag_set

    if lateral and "left" in tag_set:
        return StraightMovingPlan(avatar_semantic_direction_to_blender_root_planar("left"), 0.0, "metadata_strafe_left")
    if lateral and "right" in tag_set:
        return StraightMovingPlan(avatar_semantic_direction_to_blender_root_planar("right"), 0.0, "metadata_strafe_right")
    if "backward" in tag_set:
        return StraightMovingPlan(avatar_semantic_direction_to_blender_root_planar("backward"), 0.0, "metadata_backward")
    if "forward" in tag_set:
        return StraightMovingPlan(avatar_semantic_direction_to_blender_root_planar("forward"), 0.0, "metadata_forward")
    if lateral:
        return StraightMovingPlan(avatar_semantic_direction_to_blender_root_planar("right"), 0.0, "metadata_strafe_default_right")
    return StraightMovingPlan(avatar_semantic_direction_to_blender_root_planar("forward"), 0.0, "metadata_default_forward")


def signed_turn_angle_from_metadata(tags: tuple[str, ...]) -> float | None:
    tag_set = {tag.strip().lower() for tag in tags if tag.strip()}
    angle_degrees = None
    for candidate in ("45", "90", "180"):
        if candidate in tag_set:
            angle_degrees = float(candidate)
            break
    if angle_degrees is None or not ({"left", "right"} & tag_set):
        return None
    direction = -1.0 if "left" in tag_set else 1.0
    return math.radians(angle_degrees) * direction


def planar_direction_disagreement_angle(
    expected: tuple[float, float],
    observed: tuple[float, float],
    epsilon: float = 1e-4,
) -> float | None:
    expected_length = math.hypot(float(expected[0]), float(expected[1]))
    observed_length = math.hypot(float(observed[0]), float(observed[1]))
    if expected_length <= epsilon or observed_length <= epsilon:
        return None
    dot = (
        float(expected[0]) / expected_length * float(observed[0]) / observed_length
        + float(expected[1]) / expected_length * float(observed[1]) / observed_length
    )
    return math.acos(max(-1.0, min(1.0, dot)))


def straight_moving_diagnostics_from_metadata(
    tags: tuple[str, ...],
    raw_root_delta: tuple[float, float],
    viewpoint_delta: tuple[float, float],
    disagreement_threshold_radians: float = math.radians(45.0),
    static_epsilon: float = 1e-4,
) -> StraightMovingDiagnostics:
    plan = straight_moving_plan_from_metadata(tags)
    raw_disagreement = planar_direction_disagreement_angle(plan.direction, raw_root_delta, static_epsilon)
    viewpoint_disagreement = planar_direction_disagreement_angle(plan.direction, viewpoint_delta, static_epsilon)
    flags: list[str] = []
    if raw_disagreement is not None and raw_disagreement > disagreement_threshold_radians:
        flags.append("straight_metadata_disagrees_with_original_root_direction")
    if viewpoint_disagreement is not None and viewpoint_disagreement > disagreement_threshold_radians:
        flags.append("straight_metadata_disagrees_with_viewpoint_direction")
    if math.hypot(float(raw_root_delta[0]), float(raw_root_delta[1])) <= static_epsilon:
        flags.append("moving_metadata_with_static_original_root_displacement")
    if math.hypot(float(viewpoint_delta[0]), float(viewpoint_delta[1])) <= static_epsilon:
        flags.append("moving_metadata_with_static_viewpoint_displacement")
    return StraightMovingDiagnostics(plan, raw_disagreement, viewpoint_disagreement, tuple(flags))
