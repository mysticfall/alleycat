"""Anatomy-oriented lateral convention for the loaded Kimodo Source rig."""
from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from mathutils import Vector


class LateralBasisError(ValueError):
    """The loaded rig cannot establish an unambiguous anatomical lateral axis."""


@dataclass(frozen=True)
class AnatomicalLateralBasis:
    """Root-rest basis whose positive lateral direction points anatomically left."""

    axis: Vector
    evidence: dict[str, object]


def anatomical_lateral_basis(rig: Any, root_rest_basis: Any) -> AnatomicalLateralBasis:
    """Orient Root-rest lateral from named left/right rest anatomy, never Blender axes."""
    up = (root_rest_basis.to_3x3() @ Vector((0.0, 1.0, 0.0))).normalized()
    forward = root_rest_basis.to_3x3() @ Vector((0.0, 0.0, 1.0))
    forward -= up * forward.dot(up)
    if forward.length <= 1e-8:
        raise LateralBasisError("Root rest basis has no forward direction orthogonal to Root up")
    forward.normalize()
    candidate = up.cross(forward)
    if candidate.length <= 1e-8:
        raise LateralBasisError("Root rest basis has no lateral direction")
    candidate.normalize()

    try:
        left = rig.data.bones["LeftFoot"].matrix_local.translation
        right = rig.data.bones["RightFoot"].matrix_local.translation
    except (KeyError, AttributeError) as error:
        raise LateralBasisError("named LeftFoot/RightFoot rest anatomy is unavailable") from error
    orientation_projection = float((left - right).dot(candidate))
    if abs(orientation_projection) <= 1e-8:
        raise LateralBasisError("named LeftFoot/RightFoot rest anatomy does not orient Root-rest lateral")
    if orientation_projection < 0.0:
        candidate.negate()
    return AnatomicalLateralBasis(candidate, {
        "convention": "positive lateral points from RightFoot to LeftFoot",
        "basis": "loaded Root-rest/rigger basis",
        "orientation_evidence": "named LeftFoot and RightFoot rest transforms",
        "root_rest_up": list(up),
        "root_rest_forward": list(forward),
        "anatomical_lateral_axis_armature": list(candidate),
        "named_rest_separation": abs(orientation_projection),
    })
