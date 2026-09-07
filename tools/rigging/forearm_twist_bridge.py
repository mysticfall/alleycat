"""Candidate-independent RIG-002 bridge provenance and bend metric primitives.

The manifest deliberately records neutral data captured *before* helper
redistribution.  It is a source artefact, not a reconstruction from a generated
mesh: doing the latter would let a candidate redefine the population it is
being judged against.

This module is intentionally free of Blender dependencies (no ``bpy`` and no
``mathutils``) so every pose construction, metric, and gate can be unit tested
outside Blender.  All anatomical pose maths is expressed in one declared frame
(the lower-arm rest frame of the measured side) and never mixes world and
armature quantities; callers convert at documented boundaries.
"""

from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Iterable, Mapping, Sequence


# Schema 2 pinned the neutral/candidate separation and canonical welded IDs.
# Schema 3 added the anatomical bend population and per-pose scenario capture.
# Schema 4 added the removed wrist swing helper to the recorded chain.
# Schema 5 records the twist-only contract: exactly one helper bone per side
# (LowerArm -> ForearmTwist -> Hand), a single lower/helper/hand ownership pool,
# and only the twist helper posed per scenario (twist at weight x twist).  The
# schema-2/3 separation, ID machinery, and candidate-independent population
# semantics are carried over unchanged.
SCHEMA_VERSION = 5
POSITION_GRID_METRES = 2.0e-5
BIN_WIDTH = 0.05
BRIDGE_T_MIN = 0.85
BRIDGE_T_MAX = 1.10
SOURCE_POOL_MINIMUM = 0.50
# The pinned population is classified from PRE-TUNING source ownership, whose
# pool is lower+hand by construction (the helper is the tuning bone and carries
# no wrist-zone ownership on the fresh export).  "mixed" therefore means the
# two-anchor blend rows of that source ownership: lower and hand co-own.
MIXED_LOWER_MINIMUM = 0.05
MIXED_HAND_MINIMUM = 0.05
MINIMUM_BIN_REFERENCE_VERTICES = 10
MINIMUM_RADIUS_METRES = 1.0e-5
MINIMUM_TRIANGLE_AREA_METRES_SQUARED = 1.0e-10

# The shipped runtime ForearmTwistModifier TwistWeight (RIG-002 TR4).  This is
# the runtime helper-rotation weight and is deliberately distinct from the
# weight-distribution profile helper fractions used to author mesh weights.
RUNTIME_TWIST_WEIGHT = 0.50

# Anatomical bend fixture angles (RIG-002 TR8): pronation is derived, never
# these constants; only the commanded bend magnitudes are fixed by the spec.
BEND_FLEXION_DEGREES = 60.0
BEND_EXTENSION_DEGREES = -60.0

# The bend population widens the measured region from the mixed bridge to the
# surrounding wrist and the distal-forearm transition so a pinch relocated
# outside the bridge cannot evade the gates (RIG-002 TR9).
BEND_POPULATION_T_MIN = 0.55
BEND_POPULATION_T_MAX = 1.15
BEND_POPULATION_RADIUS_MAXIMUM = 0.18
# The authored MPFB body rings the distal forearm unevenly (~4-9 vertices per
# 0.05 t), so the wider window declares its own 0.10 t bins; each still holds
# >= 10 reference vertices on the real body topology.
BEND_POPULATION_BIN_WIDTH = 0.10
# Unlike the mixed bridge's lower+hand pool, the bend population accepts the
# full forearm chain: the measured region is substantially helper-owned, and
# excluding helper ownership would remove the very surface under measurement.
BEND_POPULATION_SOURCE_POOL_MINIMUM = 0.50

# Radial retention is only meaningful for vertices a measurable distance from
# the measurement axis; nearer vertices stay covered by area, foldover, and
# continuity gates instead of an unstable 0/0 ratio.
RADIAL_MEASUREMENT_MINIMUM_RADIUS_METRES = 2.0e-3

# Blender +Z-up to Godot +Y-up.  This has determinant +1, so triangle winding
# is preserved rather than silently reversed.
GODOT_FROM_BLENDER = ((1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, -1.0, 0.0))

# Pose-construction sanity assertions mirroring the approved Godot photobooth
# runner; these fail closed on derivation bugs, they are not quality gates.
BEND_EVIDENCE_ASSERTIONS = {
    "palm_forward_minimum_dot": {
        "value": 0.999,
        "rationale": "The pronated palm normal must actually face subject forward (runner parity).",
    },
    "palm_geometry_minimum_alignment": {
        "value": 0.5,
        "rationale": "Hand-rest +Z must agree with thumb/little finger geometry; relaxed stances tilt the thumb so the little finger carries the strict bound below.",
    },
    "little_geometry_minimum_alignment": {
        "value": 0.8,
        "rationale": "The little-finger cross product identifies the palm half-space strictly (runner parity).",
    },
    "subject_forward_anchor_minimum_dot": {
        "value": 0.99,
        "rationale": "Derived subject forward must agree with the MPFB export convention (armature -Y; mirrors the runner's world -Z anchor with recorded dots 0.998-0.9995).",
    },
    "subject_forward_maximum_up_tilt": {
        "value": 0.15,
        "rationale": "A forward axis tilted into the up axis indicates swapped or degenerate subject geometry (runner parity).",
    },
    "twist_recovery_tolerance_radians": {
        "value": 0.002,
        "rationale": "Full-angle principal-twist extraction must recover the commanded pronation (runner parity).",
    },
    "wrist_origin_invariance_metres": {
        "value": 1.0e-4,
        "rationale": "Pronation and bend rotate the hand about the wrist; the wrist origin never moves (runner parity).",
    },
    "helper_response_tolerance_radians": {
        "value": 0.002,
        "rationale": "The posed helper rotation must equal runtime weight x extracted twist (mirrors ForearmTwistModifier and the runner).",
    },
    "bend_sign_convention_minimum_alignment_delta": {
        "value": 0.25,
        "rationale": "Flexion moves the wrist-to-middle-finger direction toward the palm face and extension away from it, asserted from geometry (runner parity).",
    },
    "manifest_parity_position_metres": {
        "value": 2.0e-5,
        "rationale": "Generator and audit must reproduce the same declared-frame deformation within the RIG-002 TR11 parity scale.",
    },
    "manifest_parity_radians": {
        "value": 1.0e-3,
        "rationale": "Generator and audit derive the same anatomical frame from the same armature; drift beyond ~0.06 degrees indicates divergent constructions.",
    },
    "manifest_parity_axis_maximum_component_delta": {
        "value": 1.0e-4,
        "rationale": "Derived and manifest-recorded axes are the same construction on the same rest data.",
    },
}

# Declared bend quality gates (RIG-002 TR9).  Provisional starting points for
# user review before Stage 4 optimisation; every value is emitted verbatim in
# the evidence JSON so it can be read straight out of the measurement record.
BEND_THRESHOLD_CONFIG = {
    "config_version": 1,
    "units": {"ratios": "dimensionless", "angles": "degrees", "edge_fractions": "dimensionless"},
    "note": "Provisional starting points pending Stage 3 measured evidence and user review; the neutral-rest 0.90 hinge gate is intentionally NOT reused for bend poses.",
    "gates": {
        "absolute_minimum_area_retention": {
            "value": 0.55,
            "rationale": "Per-pose floor below the ideal 50/50 single-ring contraction (~0.82-0.87 at the commanded angles) with headroom for mesh noise while still failing crimping or collapse.",
        },
        "absolute_signed_foldover_maximum": {
            "value": 0,
            "rationale": "No signed normal inversion is anatomically acceptable in any evaluated pose.",
        },
        "absolute_minimum_radial_retention": {
            "value": 0.70,
            "rationale": "Per-pose radius-about-the-pose-axis floor; ideal 50/50 blending retains >=0.82 at the commanded angles, so 0.70 tolerates real mesh noise and fails candy-wrapper collapse.",
        },
        "incremental_minimum_radial_retention": {
            "value": 0.80,
            "rationale": "Primary bend gate: the ideal added 60-degree swing contraction at 50/50 is cos(30 degrees) ~ 0.866, so 0.80 isolates bend-added collapse beyond unavoidable linear-blend thinning.",
        },
        "incremental_minimum_area_retention": {
            "value": 0.55,
            "rationale": "Per-triangle bend-versus-palm-forward floor; rigidly moved triangles retain 1.0, only blended triangles contract.",
        },
        "incremental_maximum_radial_deviation_edge_fraction": {
            "value": 0.5,
            "rationale": "Complements the incremental ratio gate where the palm-forward denominator radius is near the measurement floor: added collapse must stay below half a local neutral edge length.",
        },
        "continuity_maximum_dihedral_increase_degrees": {
            "value": 30.0,
            "rationale": "Half the commanded bend must not concentrate across a single mesh edge; smooth transitions distribute it, weight cliffs crease it.",
        },
    },
}


class ProvenanceError(AssertionError):
    """The source/import provenance cannot be proven and must fail closed."""


@dataclass(frozen=True)
class BridgeVertex:
    mesh: str
    surface: int
    side: str
    source_index: int
    position: tuple[float, float, float]
    t: float
    radius: float
    lower: float
    helper: float
    hand: float
    candidate: tuple[tuple[str, float], ...]

    @property
    def source_pool(self) -> float:
        return self.lower + self.hand


def quantize(value: float, grid: float = POSITION_GRID_METRES) -> int:
    return int(math.floor((value / grid) + 0.5))


def godot_position(position: Sequence[float]) -> tuple[float, float, float]:
    x, y, z = position
    return (x, z, -y)


def canonical_vertex_key(vertex: BridgeVertex) -> tuple[object, ...]:
    return (
        vertex.mesh,
        vertex.surface,
        vertex.side,
        *(quantize(value) for value in godot_position(vertex.position)),
    )


def canonical_vertex_id(vertex: BridgeVertex) -> str:
    mesh, surface, side, x, y, z = canonical_vertex_key(vertex)
    return f"{mesh}|s{surface}|{side}|{x}:{y}:{z}"


def ownership_signature(vertex: BridgeVertex) -> tuple[int, ...]:
    return (
        quantize(vertex.t),
        quantize(vertex.radius),
        quantize(vertex.lower),
        quantize(vertex.helper),
        quantize(vertex.hand),
        *(item for pair in vertex.candidate for item in (pair[0], quantize(pair[1]))),
    )


def canonicalise_vertices(vertices: Iterable[BridgeVertex]) -> list[tuple[str, list[BridgeVertex]]]:
    """Return deterministic welded classes, rejecting material co-location ambiguity."""

    grouped: dict[tuple[object, ...], list[BridgeVertex]] = {}
    for vertex in vertices:
        grouped.setdefault(canonical_vertex_key(vertex), []).append(vertex)
    result = []
    for key in sorted(grouped):
        members = sorted(grouped[key], key=lambda item: item.source_index)
        if len({ownership_signature(member) for member in members}) != 1:
            raise ProvenanceError(
                f"Ambiguous co-located source vertices for {canonical_vertex_id(members[0])}: "
                "topology-smoothed t, ownership, radius, or candidate weights differ."
            )
        result.append((canonical_vertex_id(members[0]), members))
    return result


def bridge_class(vertex: BridgeVertex) -> str | None:
    """Classify one pre-tuning window vertex for the pinned neutral bridge.

    Every in-window vertex whose source pool qualifies must be pinned: the Godot
    coverage contract requires each imported bridge-window vertex with a
    material lower+hand pool to be a manifest row.  Rows that are not a
    two-anchor blend are pinned as the single-anchor remainder.
    """

    if not (BRIDGE_T_MIN <= vertex.t < BRIDGE_T_MAX) or vertex.source_pool < SOURCE_POOL_MINIMUM or vertex.radius < MINIMUM_RADIUS_METRES:
        return None
    if vertex.lower >= MIXED_LOWER_MINIMUM and vertex.hand >= MIXED_HAND_MINIMUM:
        return "mixed"
    return "single_anchor"


def bridge_bin(t: float) -> int:
    return math.floor(t / BIN_WIDTH)


def pin_bridge_population(
    vertices: Iterable[BridgeVertex], expected_mixed_bins: Iterable[int] | None = None
) -> tuple[dict[int, list[BridgeVertex]], list[BridgeVertex], list[BridgeVertex]]:
    """Pin fixed bridge membership and validate the mixed reference density."""

    mixed: dict[int, list[BridgeVertex]] = {}
    full: list[BridgeVertex] = []
    single_anchor: list[BridgeVertex] = []
    for vertex in vertices:
        classification = bridge_class(vertex)
        if classification is None:
            continue
        full.append(vertex)
        if classification == "mixed":
            mixed.setdefault(bridge_bin(vertex.t), []).append(vertex)
        else:
            single_anchor.append(vertex)
    expected = set(mixed) if expected_mixed_bins is None else set(expected_mixed_bins)
    if set(mixed) != expected:
        raise ProvenanceError(
            f"Fixed mixed bridge bin membership changed: expected {sorted(expected)}, actual {sorted(mixed)}."
        )
    for bin_index in sorted(expected):
        members = mixed.get(bin_index, [])
        if len(members) < MINIMUM_BIN_REFERENCE_VERTICES:
            raise ProvenanceError(
                f"Fixed mixed bridge bin {bin_index} has {len(members)} reference vertices; "
                f"requires {MINIMUM_BIN_REFERENCE_VERTICES}."
            )
    return mixed, full, single_anchor


def canonical_triangle_id(vertex_ids: Sequence[str]) -> str:
    if len(vertex_ids) != 3 or len(set(vertex_ids)) != 3:
        raise ProvenanceError("Canonical triangle requires three distinct vertex IDs.")
    rotations = [tuple(vertex_ids[index:] + vertex_ids[:index]) for index in range(3)]
    return "|".join(min(rotations))


def validate_oriented_triangle_mapping(source: Iterable[Sequence[str]], imported: Iterable[Sequence[str]]) -> None:
    """Require exact orientation-preserving triangle coverage.

    Cyclic rotations describe the same oriented face.  Reversed winding shares
    the same vertex set but has no accepted cyclic representation and is an
    explicit provenance failure.
    """

    source_rows = [tuple(row) for row in source]
    imported_rows = [tuple(row) for row in imported]
    source_ids = {canonical_triangle_id(list(row)) for row in source_rows}
    imported_ids = {canonical_triangle_id(list(row)) for row in imported_rows}
    if len(source_ids) != len(source_rows) or len(imported_ids) != len(imported_rows):
        raise ProvenanceError("Ambiguous duplicate canonical oriented triangle IDs.")
    for row in imported_rows:
        if frozenset(row) in {frozenset(source_row) for source_row in source_rows} and canonical_triangle_id(list(row)) not in source_ids:
            raise ProvenanceError(f"Reversed Godot triangle winding for {'|'.join(row)}.")
    if source_ids != imported_ids:
        raise ProvenanceError("Missing or extra canonical oriented bridge triangles.")


def projected_wrist_hinge_axis(hand_rest_z: Sequence[float], lower_axis: Sequence[float]) -> tuple[float, float, float]:
    """The wrist hinge direction: hand-rest +Z projected off the forearm axis."""

    dot = sum(first * second for first, second in zip(hand_rest_z, lower_axis, strict=True))
    projected = tuple(first - (dot * second) for first, second in zip(hand_rest_z, lower_axis, strict=True))
    length = math.sqrt(sum(value * value for value in projected))
    if length <= 1.0e-8:
        raise ProvenanceError("Hand rest +Z is parallel to the lower-arm longitudinal axis.")
    result = tuple(value / length for value in projected)
    if abs(sum(first * second for first, second in zip(result, lower_axis, strict=True))) > 1.0e-5:
        raise ProvenanceError("Projected wrist hinge axis is not perpendicular to the lower-arm longitudinal axis.")
    return result


def retention(deformed: Sequence[float], pivot: Sequence[float], axis: Sequence[float], neutral_radius: float) -> float:
    if neutral_radius < MINIMUM_RADIUS_METRES:
        raise ProvenanceError("Hinge retention requested for an unpinned zero-radius vertex.")
    relative = tuple(point - centre for point, centre in zip(deformed, pivot, strict=True))
    along = sum(first * second for first, second in zip(relative, axis, strict=True))
    radius_squared = sum(value * value for value in relative) - (along * along)
    return math.sqrt(max(radius_squared, 0.0)) / neutral_radius


def signed_foldover(deformed_cross: Sequence[float], transported_normal: Sequence[float]) -> bool:
    return sum(first * second for first, second in zip(deformed_cross, transported_normal, strict=True)) <= 0.0


def validate_candidate_weight_evidence(
    fixed_vertex_ids: Iterable[str], candidate_weights: Mapping[str, Mapping[str, float]]
) -> None:
    """Require a complete candidate record without reclassifying source ownership."""

    expected = set(fixed_vertex_ids)
    actual = set(candidate_weights)
    if expected != actual:
        raise ProvenanceError(
            f"Candidate-weight evidence coverage mismatch: missing {sorted(expected - actual)}, extra {sorted(actual - expected)}."
        )


# --------------------------------------------------------------------------------------
# Minimal pure-Python linear algebra.
#
# Matrices are row-major 4x4 tuples acting on column vectors (p' = M . p), matching
# the mathutils convention used by the Blender callers.  Keeping these primitives in
# this module lets the anatomical oracle be tested and reasoned about without Blender.
# --------------------------------------------------------------------------------------

Vector3 = tuple[float, float, float]
Matrix4 = tuple[tuple[float, float, float, float], ...]
Quaternion = tuple[float, float, float, float]  # (x, y, z, w)

_LINEAR_ALGEBRA_EPSILON = 1.0e-12


def v_add(first: Sequence[float], second: Sequence[float]) -> Vector3:
    return (first[0] + second[0], first[1] + second[1], first[2] + second[2])


def v_sub(first: Sequence[float], second: Sequence[float]) -> Vector3:
    return (first[0] - second[0], first[1] - second[1], first[2] - second[2])


def v_scale(vector: Sequence[float], scale: float) -> Vector3:
    return (vector[0] * scale, vector[1] * scale, vector[2] * scale)


def v_dot(first: Sequence[float], second: Sequence[float]) -> float:
    return first[0] * second[0] + first[1] * second[1] + first[2] * second[2]


def v_cross(first: Sequence[float], second: Sequence[float]) -> Vector3:
    return (
        first[1] * second[2] - first[2] * second[1],
        first[2] * second[0] - first[0] * second[2],
        first[0] * second[1] - first[1] * second[0],
    )


def v_length(vector: Sequence[float]) -> float:
    return math.sqrt(v_dot(vector, vector))


def v_normalised(vector: Sequence[float]) -> Vector3:
    length = v_length(vector)
    if length <= _LINEAR_ALGEBRA_EPSILON:
        raise ProvenanceError("Cannot normalise a degenerate vector.")
    return v_scale(vector, 1.0 / length)


def v_perpendicular_component(vector: Sequence[float], axis: Sequence[float]) -> Vector3:
    """Remove the axial component, leaving the part perpendicular to ``axis``."""

    unit = v_normalised(axis)
    return v_sub(vector, v_scale(unit, v_dot(vector, unit)))


def m_identity() -> Matrix4:
    return (
        (1.0, 0.0, 0.0, 0.0),
        (0.0, 1.0, 0.0, 0.0),
        (0.0, 0.0, 1.0, 0.0),
        (0.0, 0.0, 0.0, 1.0),
    )


def m_mul(first: Matrix4, second: Matrix4) -> Matrix4:
    return tuple(  # type: ignore[return-value]
        tuple(sum(first[row][inner] * second[inner][column] for inner in range(4)) for column in range(4))
        for row in range(4)
    )


def m_transform_point(matrix: Matrix4, point: Sequence[float]) -> Vector3:
    return (
        matrix[0][0] * point[0] + matrix[0][1] * point[1] + matrix[0][2] * point[2] + matrix[0][3],
        matrix[1][0] * point[0] + matrix[1][1] * point[1] + matrix[1][2] * point[2] + matrix[1][3],
        matrix[2][0] * point[0] + matrix[2][1] * point[1] + matrix[2][2] * point[2] + matrix[2][3],
    )


def m_transform_direction(matrix: Matrix4, direction: Sequence[float]) -> Vector3:
    return (
        matrix[0][0] * direction[0] + matrix[0][1] * direction[1] + matrix[0][2] * direction[2],
        matrix[1][0] * direction[0] + matrix[1][1] * direction[1] + matrix[1][2] * direction[2],
        matrix[2][0] * direction[0] + matrix[2][1] * direction[1] + matrix[2][2] * direction[2],
    )


def m_translation(translation: Sequence[float]) -> Matrix4:
    return (
        (1.0, 0.0, 0.0, translation[0]),
        (0.0, 1.0, 0.0, translation[1]),
        (0.0, 0.0, 1.0, translation[2]),
        (0.0, 0.0, 0.0, 1.0),
    )


def m_rotation_axis_angle(axis: Sequence[float], radians: float) -> Matrix4:
    """Rodrigues rotation about a unit axis through the coordinate origin."""

    unit = v_normalised(axis)
    cosine = math.cos(radians)
    sine = math.sin(radians)
    one_minus_cosine = 1.0 - cosine
    x, y, z = unit
    return (
        (cosine + x * x * one_minus_cosine, x * y * one_minus_cosine - z * sine, x * z * one_minus_cosine + y * sine, 0.0),
        (y * x * one_minus_cosine + z * sine, cosine + y * y * one_minus_cosine, y * z * one_minus_cosine - x * sine, 0.0),
        (z * x * one_minus_cosine - y * sine, z * y * one_minus_cosine + x * sine, cosine + z * z * one_minus_cosine, 0.0),
        (0.0, 0.0, 0.0, 1.0),
    )


def m_inverse(matrix: Matrix4) -> Matrix4:
    """General 4x4 inverse by Gauss-Jordan elimination with partial pivoting."""

    rows = [list(row) for row in matrix]
    inverse = [list(row) for row in m_identity()]
    for column in range(4):
        pivot_row = max(range(column, 4), key=lambda row: abs(rows[row][column]))
        pivot = rows[pivot_row][column]
        if abs(pivot) <= _LINEAR_ALGEBRA_EPSILON:
            raise ProvenanceError("Cannot invert a singular matrix.")
        rows[column], rows[pivot_row] = rows[pivot_row], rows[column]
        inverse[column], inverse[pivot_row] = inverse[pivot_row], inverse[column]
        divisor = rows[column][column]
        rows[column] = [value / divisor for value in rows[column]]
        inverse[column] = [value / divisor for value in inverse[column]]
        for row in range(4):
            if row == column:
                continue
            factor = rows[row][column]
            if factor == 0.0:
                continue
            rows[row] = [value - factor * pivot_value for value, pivot_value in zip(rows[row], rows[column], strict=True)]
            inverse[row] = [value - factor * pivot_value for value, pivot_value in zip(inverse[row], inverse[column], strict=True)]
    return tuple(tuple(row) for row in inverse)  # type: ignore[return-value]


def m_basis(matrix: Matrix4) -> tuple[Vector3, Vector3, Vector3]:
    """Return the row basis vectors (images of the local axes under M . v)."""

    return (
        (matrix[0][0], matrix[0][1], matrix[0][2]),
        (matrix[1][0], matrix[1][1], matrix[1][2]),
        (matrix[2][0], matrix[2][1], matrix[2][2]),
    )


def m_origin(matrix: Matrix4) -> Vector3:
    return (matrix[0][3], matrix[1][3], matrix[2][3])


def m_from_basis_origin(basis: Sequence[Sequence[float]], origin: Sequence[float]) -> Matrix4:
    """Embed a 3x3 block and an origin into an affine 4x4 matrix."""

    return (
        (basis[0][0], basis[0][1], basis[0][2], origin[0]),
        (basis[1][0], basis[1][1], basis[1][2], origin[1]),
        (basis[2][0], basis[2][1], basis[2][2], origin[2]),
        (0.0, 0.0, 0.0, 1.0),
    )


def q_from_axis_angle(axis: Sequence[float], radians: float) -> Quaternion:
    unit = v_normalised(axis)
    half = radians * 0.5
    sine = math.sin(half)
    return (unit[0] * sine, unit[1] * sine, unit[2] * sine, math.cos(half))


def q_from_matrix(matrix: Matrix4) -> Quaternion:
    """Shepperd's method for the unit quaternion of a rotation matrix."""

    m00, m01, m02 = matrix[0][:3]
    m10, m11, m12 = matrix[1][:3]
    m20, m21, m22 = matrix[2][:3]
    trace = m00 + m11 + m22
    if trace > 0.0:
        root = math.sqrt(trace + 1.0) * 2.0
        return (
            (m21 - m12) / root,
            (m02 - m20) / root,
            (m10 - m01) / root,
            0.25 * root,
        )
    if m00 > m11 and m00 > m22:
        root = math.sqrt(1.0 + m00 - m11 - m22) * 2.0
        return (
            0.25 * root,
            (m01 + m10) / root,
            (m02 + m20) / root,
            (m21 - m12) / root,
        )
    if m11 > m22:
        root = math.sqrt(1.0 + m11 - m00 - m22) * 2.0
        return (
            (m01 + m10) / root,
            0.25 * root,
            (m12 + m21) / root,
            (m02 - m20) / root,
        )
    root = math.sqrt(1.0 + m22 - m00 - m11) * 2.0
    return (
        (m02 + m20) / root,
        (m12 + m21) / root,
        0.25 * root,
        (m10 - m01) / root,
    )


def q_normalised(quaternion: Quaternion) -> Quaternion:
    length = math.sqrt(sum(component * component for component in quaternion))
    if length <= _LINEAR_ALGEBRA_EPSILON:
        raise ProvenanceError("Cannot normalise a degenerate quaternion.")
    return tuple(component / length for component in quaternion)  # type: ignore[return-value]


def q_canonicalise_double_cover(quaternion: Quaternion, tolerance: float = 1.0e-5) -> Quaternion:
    """Resolve the quaternion double cover deterministically (mirrors ForearmTwistMath)."""

    x, y, z, w = quaternion
    if w < -tolerance:
        return (-x, -y, -z, -w)
    if abs(w) > tolerance:
        return quaternion
    selected = x if abs(x) >= abs(y) and abs(x) >= abs(z) else (y if abs(y) >= abs(z) else z)
    return quaternion if selected >= 0.0 else (-x, -y, -z, -w)


def signed_principal_twist(rotation: Quaternion, axis: Sequence[float]) -> float:
    """Deterministic signed principal twist about ``axis`` in radians.

    Uses the full-angle formula ``2 * atan2(|twist vector|, w)``.  The previous
    Blender audit omitted the factor of two, which zero-twist assertions hid.
    """

    normalised_rotation = q_canonicalise_double_cover(q_normalised(rotation))
    normalised_axis = v_normalised(axis)
    signed_sin_half_angle = (
        normalised_rotation[0] * normalised_axis[0]
        + normalised_rotation[1] * normalised_axis[1]
        + normalised_rotation[2] * normalised_axis[2]
    )
    twist = q_canonicalise_double_cover(
        q_normalised(
            (
                normalised_axis[0] * signed_sin_half_angle,
                normalised_axis[1] * signed_sin_half_angle,
                normalised_axis[2] * signed_sin_half_angle,
                normalised_rotation[3],
            )
        )
    )
    twist_vector_length = math.sqrt(twist[0] * twist[0] + twist[1] * twist[1] + twist[2] * twist[2])
    angle = 2.0 * math.atan2(twist_vector_length, twist[3])
    return -angle if signed_sin_half_angle < 0.0 else angle


def _q_conjugate(quaternion: Quaternion) -> Quaternion:
    return (-quaternion[0], -quaternion[1], -quaternion[2], quaternion[3])


def _matrix3_from_quaternion(quaternion: Quaternion) -> Matrix4:
    x, y, z, w = q_normalised(quaternion)
    return (
        (1.0 - 2.0 * (y * y + z * z), 2.0 * (x * y - z * w), 2.0 * (x * z + y * w), 0.0),
        (2.0 * (x * y + z * w), 1.0 - 2.0 * (x * x + z * z), 2.0 * (y * z - x * w), 0.0),
        (2.0 * (x * z - y * w), 2.0 * (y * z + x * w), 1.0 - 2.0 * (x * x + y * y), 0.0),
        (0.0, 0.0, 0.0, 1.0),
    )


# --------------------------------------------------------------------------------------
# Anatomical wrist-bend frame and pose construction (RIG-002 TR8).
#
# Every quantity below lives in armature space unless its name says otherwise; the
# declared measurement frame is the lower-arm rest frame of the measured side.  All
# constructions mirror the approved Godot photobooth runner exactly.
# --------------------------------------------------------------------------------------


def subject_forward_frame(
    hips_origin: Sequence[float],
    head_origin: Sequence[float],
    left_upper_arm_origin: Sequence[float],
    right_upper_arm_origin: Sequence[float],
) -> dict[str, Vector3]:
    """Derive the subject forward basis from skeleton geometry, never a world constant."""

    up = v_normalised(v_sub(head_origin, hips_origin))
    right = v_normalised(v_sub(right_upper_arm_origin, left_upper_arm_origin))
    forward = v_normalised(v_cross(up, right))
    maximum_up_tilt = float(
        BEND_EVIDENCE_ASSERTIONS["subject_forward_maximum_up_tilt"]["value"]
    )
    if abs(v_dot(forward, up)) > maximum_up_tilt:
        raise ProvenanceError(
            f"Derived subject forward is tilted into the rest up axis (forward={forward}, up={up})."
        )
    return {"up": up, "right": right, "forward": forward}


def anatomical_wrist_frame(
    lower_arm_rest: Matrix4,
    hand_rest: Matrix4,
    forward_armature: Sequence[float],
    middle_distal_origin: Sequence[float],
    thumb_proximal_origin: Sequence[float],
    little_distal_origin: Sequence[float],
    side_sign: float,
) -> dict[str, object]:
    """Derive the palm normal, pronation, and flex axis for one side.

    ``side_sign`` is +1 for the left side and -1 for the right; it orients the
    finger cross products so both mirrored sides identify the same palm
    half-space, exactly as the runner does.
    """

    elbow = m_origin(lower_arm_rest)
    wrist = m_origin(hand_rest)
    longitudinal = v_normalised(v_sub(wrist, elbow))
    forward = v_normalised(forward_armature)

    # Palm normal: the hand-rest local +Z axis (the matrix's third column;
    # all helpers here use the column-vector convention p' = M . p).
    palm_rest = v_normalised(m_transform_direction(hand_rest, (0.0, 0.0, 1.0)))

    # Cross-check the palm normal against independent finger geometry.
    fingers = v_normalised(v_sub(middle_distal_origin, wrist))
    thumb = v_normalised(v_perpendicular_component(v_sub(thumb_proximal_origin, wrist), longitudinal))
    little = v_normalised(v_perpendicular_component(v_sub(little_distal_origin, wrist), longitudinal))
    palm_from_thumb = v_scale(v_normalised(v_cross(fingers, thumb)), side_sign)
    palm_from_little = v_scale(v_normalised(v_cross(little, fingers)), side_sign)
    thumb_alignment = v_dot(palm_from_thumb, palm_rest)
    little_alignment = v_dot(palm_from_little, palm_rest)
    palm_minimum = float(BEND_EVIDENCE_ASSERTIONS["palm_geometry_minimum_alignment"]["value"])
    little_minimum = float(BEND_EVIDENCE_ASSERTIONS["little_geometry_minimum_alignment"]["value"])
    if thumb_alignment < palm_minimum or little_alignment < little_minimum:
        raise ProvenanceError(
            "Hand-rest +Z is not the palm normal according to rest finger geometry "
            f"(thumb_alignment={thumb_alignment:.9f}, little_alignment={little_alignment:.9f})."
        )

    # Palm-forward target and pronation about the forearm longitudinal axis.
    palm_target = v_normalised(v_perpendicular_component(forward, longitudinal))
    palm_perpendicular = v_normalised(v_perpendicular_component(palm_rest, longitudinal))
    pronation = math.atan2(
        v_dot(longitudinal, v_cross(palm_perpendicular, palm_target)),
        v_dot(palm_perpendicular, palm_target),
    )

    # Flex axis: perpendicular to both the forearm and the palm-forward target.
    flex_axis = v_normalised(v_cross(longitudinal, palm_target))

    # Bend sign convention: flexion moves the wrist-to-middle-finger direction
    # toward the palm face, extension away from it (asserted from geometry).
    tip_rest = v_normalised(v_sub(middle_distal_origin, wrist))
    tip_after_pronation = v_normalised(m_transform_direction(m_rotation_axis_angle(longitudinal, pronation), tip_rest))

    return {
        "elbow": elbow,
        "wrist": wrist,
        "longitudinal": longitudinal,
        "forward": forward,
        "palm_rest": palm_rest,
        "palm_target": palm_target,
        "palm_perpendicular": palm_perpendicular,
        "pronation_radians": pronation,
        "flex_axis": flex_axis,
        "thumb_alignment": thumb_alignment,
        "little_alignment": little_alignment,
        "tip_after_pronation": tip_after_pronation,
    }


def palm_forward_alignment(
    posed_palm_normal: Sequence[float],
    palm_target: Sequence[float],
    longitudinal: Sequence[float],
) -> float:
    """Alignment of the posed palm's perpendicular component with the target.

    The palm normal may legitimately tilt along the forearm axis; pronation
    aligns its perpendicular component onto the subject-forward target, so the
    palm-forward check must measure that component rather than the full normal.
    """

    perpendicular = v_normalised(v_perpendicular_component(posed_palm_normal, longitudinal))
    return v_dot(perpendicular, palm_target)


def assert_bend_sign_convention(
    frame: Mapping[str, object], bend_radians: float
) -> float:
    """Assert flexion/extension anatomical direction and return the alignment delta."""

    flex_axis = frame["flex_axis"]
    palm_target = frame["palm_target"]
    tip_after_pronation = frame["tip_after_pronation"]
    tip_after_bend = v_normalised(
        m_transform_direction(m_rotation_axis_angle(flex_axis, bend_radians), tip_after_pronation)  # type: ignore[arg-type]
    )
    delta = v_dot(tip_after_bend, palm_target) - v_dot(tip_after_pronation, palm_target)  # type: ignore[arg-type]
    minimum = float(BEND_EVIDENCE_ASSERTIONS["bend_sign_convention_minimum_alignment_delta"]["value"])
    if bend_radians > 0.0 and delta < minimum:
        raise ProvenanceError(
            f"Flexion did not rotate the wrist-to-middle-finger direction toward the palm face (delta={delta:.9f})."
        )
    if bend_radians < 0.0 and delta > -minimum:
        raise ProvenanceError(
            f"Extension did not rotate the wrist-to-middle-finger direction away from the palm face (delta={delta:.9f})."
        )
    return delta


def anatomical_hand_pose(
    lower_arm_rest: Matrix4,
    hand_rest: Matrix4,
    frame: Mapping[str, object],
    bend_radians: float,
) -> Matrix4:
    """Compose the anatomical hand pose ``Swing(bend) x Twist(pronation)``.

    The rotation axes are expressed in the declared lower-arm rest frame and the
    wrist origin is invariant, matching the approved runner exactly.
    """

    lower_inverse = m_inverse(lower_arm_rest)
    relative_rest = m_mul(lower_inverse, hand_rest)
    longitudinal_lower = v_normalised(m_transform_direction(lower_inverse, frame["longitudinal"]))  # type: ignore[arg-type]
    flex_axis_lower = v_normalised(m_transform_direction(lower_inverse, frame["flex_axis"]))  # type: ignore[arg-type]
    pronation = float(frame["pronation_radians"])  # type: ignore[arg-type]

    basis = m_mul(
        m_rotation_axis_angle(flex_axis_lower, bend_radians),
        m_rotation_axis_angle(longitudinal_lower, pronation),
    )
    relative_basis = m_basis(relative_rest)
    posed_relative = m_mul(
        m_translation(m_origin(relative_rest)),
        m_mul(basis, m_from_basis_origin(relative_basis, (0.0, 0.0, 0.0))),
    )
    return m_mul(lower_arm_rest, posed_relative)


def helper_twist_pose(
    helper_rest: Matrix4,
    longitudinal_armature: Sequence[float],
    twist_radians: float,
    weight: float,
) -> Matrix4:
    """Rest-relative helper local rotation ``weight x twist`` about the forearm axis.

    Mirrors ``ForearmTwistMath.TryComputeHelperPoseRotationFromContinuousTwist``:
    the helper rotates about its own head by the weighted twist, expressed in the
    helper rest frame.
    """

    helper_inverse = m_inverse(helper_rest)
    helper_axis = v_normalised(m_transform_direction(helper_inverse, longitudinal_armature))
    local_rotation = m_rotation_axis_angle(helper_axis, twist_radians * weight)
    return m_mul(helper_rest, local_rotation)


def propagate_poses(
    bones: Sequence[tuple[str, str | None, Matrix4]],
    overrides: Mapping[str, Matrix4],
) -> dict[str, Matrix4]:
    """Compose armature-space posed matrices, propagating through the real hierarchy.

    ``bones`` carries ``(name, parent, matrix_local)`` for every bone.  Only the
    overridden bones (for example the hand and helper) deviate from rest; every
    descendant recomposes its unchanged local rest matrix under its posed
    parent, so finger children follow a posed hand instead of staying at rest.
    """

    by_name = {name: (parent, rest) for name, parent, rest in bones}
    if len(by_name) != len(bones):
        raise ProvenanceError("Duplicate bone names in pose propagation.")
    posed: dict[str, Matrix4] = {}
    visiting: set[str] = set()

    def resolve(name: str) -> Matrix4:
        if name in posed:
            return posed[name]
        if name in visiting:
            raise ProvenanceError(f"Bone hierarchy cycle detected at '{name}'.")
        visiting.add(name)
        parent, rest = by_name[name]
        if name in overrides:
            result = overrides[name]
        elif parent is None:
            result = rest
        else:
            if parent not in by_name:
                raise ProvenanceError(f"Bone '{name}' references unknown parent '{parent}'.")
            local = m_mul(m_inverse(by_name[parent][1]), rest)
            result = m_mul(resolve(parent), local)
        visiting.discard(name)
        posed[name] = result
        return result

    for bone_name in by_name:
        resolve(bone_name)
    return posed


def skin_point(
    point_armature: Sequence[float],
    influences: Sequence[tuple[str, float]],
    poses: Mapping[str, Matrix4],
    rests: Mapping[str, Matrix4],
) -> Vector3:
    """Linear-blend skinning of one armature-space point (audit semantics).

    Influences are normalised by their own total; an empty influence set leaves
    the point undeformed, matching the Blender audit's explicit LBS.
    """

    total = sum(weight for _name, weight in influences)
    if total <= 0.0:
        return (point_armature[0], point_armature[1], point_armature[2])
    deformed = (0.0, 0.0, 0.0)
    for name, weight in influences:
        transform = m_mul(poses[name], m_inverse(rests[name]))
        deformed = v_add(deformed, v_scale(m_transform_point(transform, point_armature), weight / total))
    return deformed


# --------------------------------------------------------------------------------------
# Bend population pinning (RIG-002 TR9).
# --------------------------------------------------------------------------------------


def bend_population_class(vertex: BridgeVertex) -> bool:
    """Membership rule for the candidate-independent bend population."""

    # The chain pool covers the full lower-arm-to-hand helper chain: the single
    # twist helper carries no wrist-zone ownership on a fresh export, so
    # membership is identical to the source lower+hand pool with the authored
    # helper mass measured once it exists (RIG-002 TR11 population carry-over).
    forearm_chain_pool = vertex.lower + vertex.helper + vertex.hand
    return (
        BEND_POPULATION_T_MIN <= vertex.t < BEND_POPULATION_T_MAX
        and vertex.radius <= BEND_POPULATION_RADIUS_MAXIMUM
        and forearm_chain_pool >= BEND_POPULATION_SOURCE_POOL_MINIMUM
        and vertex.radius >= MINIMUM_RADIUS_METRES
    )


def bend_bin(t: float) -> int:
    return math.floor(t / BEND_POPULATION_BIN_WIDTH)


def pin_bend_population(
    vertices: Iterable[BridgeVertex], expected_bins: Iterable[int] | None = None
) -> dict[int, list[BridgeVertex]]:
    """Pin bend population membership and require dense, gap-free t coverage."""

    population: dict[int, list[BridgeVertex]] = {}
    for vertex in vertices:
        if not bend_population_class(vertex):
            continue
        population.setdefault(bend_bin(vertex.t), []).append(vertex)
    expected = set(expected_bins) if expected_bins is not None else set(population)
    for bin_index in sorted(set(population)):
        members = population[bin_index]
        if len(members) < MINIMUM_BIN_REFERENCE_VERTICES:
            raise ProvenanceError(
                f"Bend population bin {bin_index} has {len(members)} reference vertices; "
                f"requires {MINIMUM_BIN_REFERENCE_VERTICES}."
            )
    # The epsilon guards keep the required window stable when the decimal
    # boundaries do not divide exactly in binary floating point.
    required = set(
        range(
            math.floor(BEND_POPULATION_T_MIN / BEND_POPULATION_BIN_WIDTH + 1.0e-9),
            math.ceil(BEND_POPULATION_T_MAX / BEND_POPULATION_BIN_WIDTH - 1.0e-9),
        )
    )
    if set(population) != expected or expected != required:
        raise ProvenanceError(
            f"Bend population bins must densely cover the declared window {required}; "
            f"expected {sorted(expected)}, actual {sorted(population)}."
        )
    return population


# --------------------------------------------------------------------------------------
# Two-family bend metrics (RIG-002 TR9).
#
# All positions are plain tuples in the declared lower-arm rest frame; every
# reference construction is candidate-independent and manifest-frozen.
# --------------------------------------------------------------------------------------


def radial_distance(point: Sequence[float], pivot: Sequence[float], axis: Sequence[float]) -> float:
    relative = v_sub(point, pivot)
    along = v_dot(relative, axis)
    return v_length(v_sub(relative, v_scale(axis, along)))


def triangle_area(first: Sequence[float], second: Sequence[float], third: Sequence[float]) -> float:
    return v_length(v_cross(v_sub(second, first), v_sub(third, first))) * 0.5


def triangle_normal(first: Sequence[float], second: Sequence[float], third: Sequence[float]) -> Vector3:
    return v_normalised(v_cross(v_sub(second, first), v_sub(third, first)))


def blended_direction_transport(
    direction: Sequence[float], influences: Sequence[tuple[float, Matrix4]]
) -> Vector3:
    """Transport a direction through weighted per-influence matrices (LBS transport)."""

    transported = (0.0, 0.0, 0.0)
    for weight, matrix in influences:
        transported = v_add(transported, v_scale(m_transform_direction(matrix, direction), weight))
    return transported


def absolute_pose_metrics(
    deformed: Mapping[str, Sequence[float]],
    triangles: Sequence[Sequence[str]],
    neutral_areas: Mapping[str, float],
    reference_radii: Mapping[str, float],
    pivot: Sequence[float],
    axis: Sequence[float],
    transported_normals: Mapping[str, Sequence[float]],
) -> dict[str, object]:
    """Family A: per-pose absolute shape quality for one evaluated pose state.

    Neutral geometry enters only through the manifest-frozen neutral triangle
    areas and reference radii, both candidate-independent by construction.
    """

    area_ratios: dict[str, float] = {}
    foldovers = 0
    for identifiers in triangles:
        triangle_id = canonical_triangle_id(list(identifiers))
        points = [deformed[identifier] for identifier in identifiers]
        neutral_area = neutral_areas[triangle_id]
        if neutral_area <= MINIMUM_TRIANGLE_AREA_METRES_SQUARED:
            continue
        deformed_area = triangle_area(*points)
        area_ratios[triangle_id] = deformed_area / neutral_area
        deformed_cross = v_cross(v_sub(points[1], points[0]), v_sub(points[2], points[0]))
        if signed_foldover(deformed_cross, transported_normals[triangle_id]):
            foldovers += 1

    retentions: dict[str, float] = {}
    for identifier, reference_radius in reference_radii.items():
        if reference_radius < RADIAL_MEASUREMENT_MINIMUM_RADIUS_METRES:
            continue
        measured = radial_distance(deformed[identifier], pivot, axis)
        retentions[identifier] = measured / reference_radius

    return {
        "triangle_count": len(area_ratios),
        "minimum_area_retention": min(area_ratios.values()) if area_ratios else 0.0,
        "minimum_area_retention_triangle_id": min(area_ratios, key=area_ratios.get) if area_ratios else None,
        "signed_foldover_count": foldovers,
        "minimum_radial_retention": min(retentions.values()) if retentions else 1.0,
        "minimum_radial_retention_vertex_id": min(retentions, key=retentions.get) if retentions else None,
        "radial_sample_count": len(retentions),
    }


def incremental_bend_metrics(
    palm_forward: Mapping[str, Sequence[float]],
    bend: Mapping[str, Sequence[float]],
    triangles: Sequence[Sequence[str]],
    local_scales: Mapping[str, float],
    pivot: Sequence[float],
    axis: Sequence[float],
) -> dict[str, object]:
    """Family B: bend damage relative to the palm-forward state at the same weights."""

    retentions: dict[str, float] = {}
    deviations: dict[str, float] = {}
    for identifier in palm_forward:
        palm_forward_radius = radial_distance(palm_forward[identifier], pivot, axis)
        if palm_forward_radius < RADIAL_MEASUREMENT_MINIMUM_RADIUS_METRES:
            continue
        bend_radius = radial_distance(bend[identifier], pivot, axis)
        retentions[identifier] = bend_radius / palm_forward_radius
        scale = local_scales.get(identifier, 0.0)
        deviations[identifier] = abs(bend_radius - palm_forward_radius) / scale if scale > MINIMUM_RADIUS_METRES else float("inf")

    area_ratios: dict[str, float] = {}
    for identifiers in triangles:
        triangle_id = canonical_triangle_id(list(identifiers))
        palm_forward_area = triangle_area(*[palm_forward[identifier] for identifier in identifiers])
        if palm_forward_area <= MINIMUM_TRIANGLE_AREA_METRES_SQUARED:
            continue
        bend_area = triangle_area(*[bend[identifier] for identifier in identifiers])
        area_ratios[triangle_id] = bend_area / palm_forward_area

    return {
        "vertex_sample_count": len(retentions),
        "minimum_radial_retention": min(retentions.values()) if retentions else 1.0,
        "minimum_radial_retention_vertex_id": min(retentions, key=retentions.get) if retentions else None,
        "maximum_radial_deviation_edge_fraction": max(deviations.values()) if deviations else 0.0,
        "maximum_radial_deviation_vertex_id": max(deviations, key=deviations.get) if deviations else None,
        "minimum_area_retention": min(area_ratios.values()) if area_ratios else 1.0,
        "minimum_area_retention_triangle_id": min(area_ratios, key=area_ratios.get) if area_ratios else None,
    }


def interior_edges(triangles: Sequence[Sequence[str]]) -> list[tuple[str, str]]:
    """Undirected edges shared by exactly two triangles of the measured set."""

    counts: dict[tuple[str, str], int] = {}
    for identifiers in triangles:
        for first, second in ((identifiers[0], identifiers[1]), (identifiers[1], identifiers[2]), (identifiers[2], identifiers[0])):
            counts[tuple(sorted((first, second)))] = counts.get(tuple(sorted((first, second))), 0) + 1
    return sorted(edge for edge, count in counts.items() if count == 2)


def dihedral_increase_degrees(
    neutral: Mapping[str, Sequence[float]],
    deformed: Mapping[str, Sequence[float]],
    edge: tuple[str, str],
    triangles: Sequence[Sequence[str]],
) -> float:
    """Increase of the dihedral angle across one interior edge versus neutral."""

    adjacent = [
        identifiers
        for identifiers in triangles
        if edge[0] in identifiers and edge[1] in identifiers
    ]
    if len(adjacent) != 2:
        return 0.0

    def opposite(identifiers: Sequence[str]) -> str:
        return next(identifier for identifier in identifiers if identifier not in edge)

    def angle(state: Mapping[str, Sequence[float]]) -> float:
        first, second = adjacent
        normal_first = triangle_normal(
            state[first[0]], state[first[1]], state[first[2]]
        )
        normal_second = triangle_normal(
            state[second[0]], state[second[1]], state[second[2]]
        )
        cosine = max(-1.0, min(1.0, v_dot(normal_first, normal_second)))
        return math.degrees(math.acos(cosine))

    _ = opposite(adjacent[0])
    return max(0.0, angle(deformed) - angle(neutral))


def continuity_metrics(
    neutral: Mapping[str, Sequence[float]],
    deformed: Mapping[str, Sequence[float]],
    triangles: Sequence[Sequence[str]],
) -> dict[str, object]:
    """Adjacent-triangle continuity: max dihedral-angle increase versus neutral.

    Declared crease measure (RIG-002 TR9): a weight cliff concentrates the bend
    across one mesh edge, sharply increasing its dihedral angle, while a smooth
    weight transition distributes it.  Measuring the *increase* over the neutral
    dihedral keeps authored sharp edges from failing the gate.
    """

    edges = interior_edges(triangles)
    increases = [dihedral_increase_degrees(neutral, deformed, edge, triangles) for edge in edges]
    worst_edge = max(
        ((edge, increase) for edge, increase in zip(edges, increases, strict=True)),
        key=lambda item: item[1],
        default=None,
    )
    return {
        "interior_edge_count": len(edges),
        "maximum_dihedral_increase_degrees": max(increases) if increases else 0.0,
        "maximum_dihedral_increase_edge": list(worst_edge[0]) if worst_edge is not None else None,
    }


def evaluate_bend_gates(
    absolute_by_pose: Mapping[str, Mapping[str, object]],
    incremental_by_pose: Mapping[str, Mapping[str, object]],
    continuity_by_pose: Mapping[str, Mapping[str, object]],
    thresholds: Mapping[str, Mapping[str, object]] | None = None,
) -> dict[str, object]:
    """Evaluate every declared bend gate; a single failure rejects the candidate."""

    config = thresholds if thresholds is not None else BEND_THRESHOLD_CONFIG["gates"]  # type: ignore[assignment]
    gates: dict[str, object] = {}

    def record(name: str, measured: float, threshold: float, passes: bool, detail: str) -> None:
        gates[name] = {
            "measured": measured,
            "threshold": threshold,
            "passed": passes,
            "detail": detail,
        }

    area_minimum = float(config["absolute_minimum_area_retention"]["value"])  # type: ignore[index]
    foldover_maximum = int(config["absolute_signed_foldover_maximum"]["value"])  # type: ignore[index]
    radial_minimum = float(config["absolute_minimum_radial_retention"]["value"])  # type: ignore[index]
    for pose, metrics in absolute_by_pose.items():
        record(
            f"absolute_minimum_area_retention[{pose}]",
            float(metrics["minimum_area_retention"]),
            area_minimum,
            float(metrics["minimum_area_retention"]) >= area_minimum,
            f"minimum triangle area retention in pose {pose}",
        )
        record(
            f"absolute_signed_foldover_maximum[{pose}]",
            int(metrics["signed_foldover_count"]),
            foldover_maximum,
            int(metrics["signed_foldover_count"]) <= foldover_maximum,
            f"signed normal inversions in pose {pose}",
        )
        record(
            f"absolute_minimum_radial_retention[{pose}]",
            float(metrics["minimum_radial_retention"]),
            radial_minimum,
            float(metrics["minimum_radial_retention"]) >= radial_minimum,
            f"minimum radial retention about the {pose} axis",
        )

    incremental_radial_minimum = float(config["incremental_minimum_radial_retention"]["value"])  # type: ignore[index]
    incremental_area_minimum = float(config["incremental_minimum_area_retention"]["value"])  # type: ignore[index]
    incremental_deviation_maximum = float(config["incremental_maximum_radial_deviation_edge_fraction"]["value"])  # type: ignore[index]
    for pose, metrics in incremental_by_pose.items():
        record(
            f"incremental_minimum_radial_retention[{pose}]",
            float(metrics["minimum_radial_retention"]),
            incremental_radial_minimum,
            float(metrics["minimum_radial_retention"]) >= incremental_radial_minimum,
            f"bend-added radial contraction relative to palm-forward in pose {pose}",
        )
        record(
            f"incremental_minimum_area_retention[{pose}]",
            float(metrics["minimum_area_retention"]),
            incremental_area_minimum,
            float(metrics["minimum_area_retention"]) >= incremental_area_minimum,
            f"bend-added triangle contraction relative to palm-forward in pose {pose}",
        )
        record(
            f"incremental_maximum_radial_deviation_edge_fraction[{pose}]",
            float(metrics["maximum_radial_deviation_edge_fraction"]),
            incremental_deviation_maximum,
            float(metrics["maximum_radial_deviation_edge_fraction"]) <= incremental_deviation_maximum,
            f"edge-normalised added radial deviation in pose {pose}",
        )

    continuity_maximum = float(config["continuity_maximum_dihedral_increase_degrees"]["value"])  # type: ignore[index]
    for pose, metrics in continuity_by_pose.items():
        record(
            f"continuity_maximum_dihedral_increase_degrees[{pose}]",
            float(metrics["maximum_dihedral_increase_degrees"]),
            continuity_maximum,
            float(metrics["maximum_dihedral_increase_degrees"]) <= continuity_maximum,
            f"adjacent-triangle dihedral increase versus neutral in pose {pose}",
        )

    return {
        "gates": gates,
        "all_passed": all(bool(gate["passed"]) for gate in gates.values()),
        "failed": sorted(name for name, gate in gates.items() if not bool(gate["passed"])),
    }


def evaluate_candidate_eligibility(
    axial_eligible: bool,
    axial_reason: str,
    bend_gates: Mapping[str, object],
) -> dict[str, object]:
    """Optimiser eligibility: BOTH the axial gates and the anatomical-bend gates.

    The bend gates must have been evaluated at the shipped runtime twist weight
    (RIG-002 TR10); a candidate passing axial gates alone is not eligible.
    """

    bend_passed = bool(bend_gates["all_passed"])
    eligible = axial_eligible and bend_passed
    reasons = []
    if not axial_eligible:
        reasons.append(axial_reason)
    if not bend_passed:
        reasons.append(
            f"failed anatomical-bend gates at runtime twist weight {RUNTIME_TWIST_WEIGHT}: " + ", ".join(bend_gates["failed"])  # type: ignore[index]
        )
    return {
        "decision": "eligible" if eligible else "rejected",
        "axial_eligible": axial_eligible,
        "bend_gates_passed": bend_passed,
        "reasons": reasons,
    }
