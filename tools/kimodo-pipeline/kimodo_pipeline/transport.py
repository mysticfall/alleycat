"""Authoritative Blender-to-Godot sample transport evidence for ANIM-002."""
from __future__ import annotations

import math
from typing import Any, Mapping


TRANSPORT_CONTRACT = "anim002_sample_transport_v1"
COORDINATE_CONTRACT = "anim002_female_root_rest_basis_v1"
COORDINATE_STRATEGY = "canonical_axis_conversion_with_strict_processed_root_rest"
CANONICAL_BLENDER_TO_GODOT_COLUMNS = (
    (-1.0, 0.0, 0.0),
    (0.0, 0.0, 1.0),
    (0.0, 1.0, 0.0),
)


class TransportContractError(ValueError):
    """Raised when an Action cannot prove its finite transport timeline."""


def _columns(matrix: Any) -> list[list[float]]:
    return [
        [round(float(matrix[row][column]), 9) for row in range(3)]
        for column in range(3)
    ]


def make_coordinate_contract(target: Any) -> dict[str, Any]:
    """Bind canonical axis conversion to the loaded Female Root edit-bone rest.

    Strategy B is intentional: canonical ``(-x, z, y)`` remains the transport
    mapping, while the processed index carries the actual loaded Blender rest and
    its mapped Godot expectation. Godot packaging must reject any imported rig
    whose real Root rest does not satisfy this evidence before writing outputs.
    """
    if target is None or getattr(target, "type", None) != "ARMATURE" or target.name != "Female":
        raise TransportContractError('coordinate contract requires the supported "Female" armature')
    root = target.data.bones.get("Root")
    if root is None or root.parent is not None:
        raise TransportContractError('coordinate contract requires an unparented Female bone named "Root"')
    matrix = root.matrix_local.copy()
    conversion = matrix.to_3x3().__class__(CANONICAL_BLENDER_TO_GODOT_COLUMNS).transposed()
    blender_basis = matrix.to_3x3()
    godot_basis = conversion @ blender_basis @ conversion.inverted()
    godot_origin = conversion @ matrix.translation
    return {
        "version": COORDINATE_CONTRACT,
        "strategy": COORDINATE_STRATEGY,
        "source_armature": "Female",
        "root_bone": "Root",
        "blender_rest": {
            "coordinate_space": "female_armature_local_z_up",
            "basis_columns": _columns(blender_basis),
            "origin": [round(float(value), 9) for value in matrix.translation],
        },
        "canonical_blender_to_godot": {
            "vector_mapping": "(-x,z,y)",
            "basis_columns": [list(column) for column in CANONICAL_BLENDER_TO_GODOT_COLUMNS],
        },
        "expected_godot_root_rest": {
            "coordinate_space": "imported_skeleton_local_y_up",
            "basis_columns": _columns(godot_basis),
            "origin": [round(float(value), 9) for value in godot_origin],
        },
    }


def make_transport_evidence(
    *, frame_start: int, frame_end: int, fps: float, is_loop: bool,
) -> dict[str, Any]:
    """Create transport evidence from an authored inclusive Action range."""
    if isinstance(frame_start, bool) or not isinstance(frame_start, int):
        raise TransportContractError("transport frame_start must be an integer")
    if isinstance(frame_end, bool) or not isinstance(frame_end, int):
        raise TransportContractError("transport frame_end must be an integer")
    if isinstance(fps, bool) or not isinstance(fps, float):
        raise TransportContractError("transport fps must be a float")
    if frame_end <= frame_start or frame_start < 1:
        raise TransportContractError("transport requires at least two inclusive samples")
    if not math.isfinite(fps) or fps <= 0.0:
        raise TransportContractError("transport fps must be finite and positive")
    sample_count = frame_end - frame_start + 1
    duration_frames = sample_count - 1
    evidence: dict[str, Any] = {
        "contract": TRANSPORT_CONTRACT,
        "boundary": "loop" if is_loop else "transition",
        "fps": float(fps),
        "frame_start": frame_start,
        "frame_end": frame_end,
        "sample_count": sample_count,
        "duration_frames": duration_frames,
        "duration_seconds": duration_frames / float(fps),
    }
    if is_loop:
        evidence.update({
            "period_frames": duration_frames,
            "runtime_sample_count": duration_frames,
            "conceptual_boundary_frame": frame_end,
        })
    return evidence


def validate_transport_evidence(
    evidence: Mapping[str, Any], *, frame_start: int, frame_end: int,
    fps: float, is_loop: bool,
) -> dict[str, Any]:
    """Reject missing, malformed, or off-by-one transport evidence."""
    if not isinstance(evidence, Mapping):
        raise TransportContractError("transport evidence must be an object")
    expected = make_transport_evidence(
        frame_start=frame_start, frame_end=frame_end, fps=fps, is_loop=is_loop,
    )
    if set(evidence) != set(expected):
        raise TransportContractError("transport evidence fields do not match its boundary kind")
    for key, expected_value in expected.items():
        actual = evidence.get(key)
        if isinstance(expected_value, float):
            if (
                isinstance(actual, bool)
                or not isinstance(actual, float)
                or not math.isfinite(actual)
                or not math.isclose(float(actual), expected_value, abs_tol=1e-7)
            ):
                raise TransportContractError(f"transport {key} is inconsistent")
        elif isinstance(expected_value, int) and (
            isinstance(actual, bool) or not isinstance(actual, int) or actual != expected_value
        ):
            raise TransportContractError(f"transport {key} is inconsistent")
        elif not isinstance(expected_value, int) and actual != expected_value:
            raise TransportContractError(f"transport {key} is inconsistent")
    return dict(evidence)
