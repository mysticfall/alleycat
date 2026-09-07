"""Topology-aware axial weight authoring for the single forearm twist helper.

The authored chain per side is ``lower arm -> twist helper -> hand`` (RIG-002 TR1).
The module deliberately has no Blender dependency.  The ordinary character
generator and the read-only candidate audit both call the same deterministic
formula with extracted mesh coordinates, polygon connectivity, and source weights.
"""

from __future__ import annotations

from dataclasses import dataclass
from collections import deque
import math
from types import MappingProxyType
from typing import Iterable, Mapping, Sequence


@dataclass(frozen=True)
class ForearmTwistWeightProfile:
    """Axial helper distribution parameters; not a runtime release default.

    ``helper_fraction`` is the documented initial skinning ownership setting for
    the single helper (0.5): the authored three-anchor ramp peaks the helper's
    ownership at the middle of the proximal-to-distal transition, rising from
    the proximal boundary and falling to the distal boundary. ``proximal_t`` and
    ``distal_t`` bound that transition in measured lower-arm fractions;
    ``smoothing_passes`` control the connectivity regularisation used by the
    candidate study only.
    """

    name: str
    helper_fraction: float
    proximal_t: float
    distal_t: float
    smoothing_passes: int


@dataclass(frozen=True)
class ForearmVertex:
    """A mesh vertex projected into one forearm's measured rest frame."""

    t: float
    radius: float
    weights: Mapping[str, float]


# Selected only after the Phase 2 candidate audit.
SELECTED_PROFILE = ForearmTwistWeightProfile(
    name="topology-linear-p10-d110-s24",
    helper_fraction=0.5,
    proximal_t=0.10,
    distal_t=1.10,
    smoothing_passes=24,
)

FOREARM_RADIUS = 0.18
FOREARM_MIN_T = -0.05
FOREARM_MAX_T = 1.15
# The axial transition hands ownership from the lower arm to the helper over the
# first three quarters of the profile span, then from the helper to the hand.
AXIAL_TWIST_CROSSOVER_PHASE = 0.75


@dataclass(frozen=True)
class AxialSide:
    """Physical group names and actual deform descendants of this side's hand.

    The caller supplies descendants from the source armature; names alone cannot
    reliably identify custom finger bones.
    """

    lower_arm: str
    helper: str
    hand: str
    finger_groups: frozenset[str]


@dataclass(frozen=True)
class AxialAuthoring:
    """Immutable-stage result: authored axial rows and their side identities."""

    reference: tuple[Mapping[str, float], ...]
    logical_twist: tuple[tuple[float, ...], ...]
    beta: tuple[tuple[float, ...], ...]
    side_keys: tuple[str, ...]
    projections: tuple[tuple[tuple[float, float], ...], ...]
    side_identities: tuple[AxialSide, ...]
    source_domain: tuple[tuple[bool, ...], ...] = ()

    def __post_init__(self) -> None:
        # Freeze even when directly constructed with mutable caller-owned containers.
        object.__setattr__(self, "reference", tuple(MappingProxyType(dict(row)) for row in self.reference))
        object.__setattr__(self, "logical_twist", tuple(tuple(row) for row in self.logical_twist))
        object.__setattr__(self, "beta", tuple(tuple(row) for row in self.beta))
        object.__setattr__(self, "side_keys", tuple(self.side_keys))
        object.__setattr__(self, "projections", tuple(tuple(tuple(point) for point in side) for side in self.projections))
        object.__setattr__(self, "side_identities", tuple(
            AxialSide(side.lower_arm, side.helper, side.hand, frozenset(side.finger_groups))
            for side in self.side_identities
        ))
        object.__setattr__(self, "source_domain", tuple(tuple(row) for row in self.source_domain))


def _smooth(value: float) -> float:
    v = min(1.0, max(0.0, value))
    return v * v * (3.0 - 2.0 * v)


def _assign(row: dict[str, float], name: str, value: float) -> None:
    if value > 0.0 or name in row:
        row[name] = value


def author_axial_weights(
    vertices: Sequence[ForearmVertex],
    polygons: Iterable[Sequence[int]],
    sides: Sequence[AxialSide],
    projections: Mapping[str, Sequence[tuple[float, float]]],
    profile: ForearmTwistWeightProfile = SELECTED_PROFILE,
) -> AxialAuthoring:
    """Author the conserved three-anchor axial reference from immutable physical rows.

    Side eligibility is established before either side is authored. No whole-row
    normalisation, coordinate relaxation, weld or triangulation is performed; the
    lower-arm/helper/hand pool is conserved row by row.
    """
    if not sides or len({name for side in sides for name in
                         (side.lower_arm, side.helper, side.hand)}) != 3 * len(sides):
        raise ValueError("Axial sides must have distinct physical group names")
    for side in sides:
        if side.lower_arm[-2:] not in {"_l", "_r"} or side.hand[-2:] != side.lower_arm[-2:]:
            raise ValueError("Axial lower arm and hand must share an _l or _r suffix")
    keys = tuple(side.lower_arm[-2:] for side in sides)
    if len(set(keys)) != len(keys) or set(projections) != set(keys):
        raise ValueError("Axial projections must have exactly one entry per physical side")
    if any(len(projections[key]) != len(vertices) for key in keys):
        raise ValueError("Axial projection lengths must match physical rows")
    coordinates = tuple(tuple((t, radius) for t, radius in projections[key]) for key in keys)
    for side_coordinates in coordinates:
        for t, radius in side_coordinates:
            if not math.isfinite(t) or not math.isfinite(radius) or radius < 0:
                raise ValueError("Nonfinite or negative forearm coordinate")
    for vertex in vertices:
        if any(not math.isfinite(weight) or weight < 0 for weight in vertex.weights.values()):
            raise ValueError("Nonfinite or negative physical weight")
    span = profile.distal_t - profile.proximal_t
    if span <= 0.0:
        raise ValueError("Axial profile distal_t must exceed proximal_t")
    neighbours = build_vertex_neighbours(len(vertices), polygons)
    physical = tuple(dict(vertex.weights) for vertex in vertices)
    result = [dict(row) for row in physical]
    eligibility: list[list[bool]] = []
    for side, side_coordinates in zip(sides, coordinates, strict=True):
        suffix = side.lower_arm[-2:]
        opposite = "_r" if suffix == "_l" else "_l"
        opposite_groups = {name for other in sides if other is not side
                           for name in (other.lower_arm, other.helper, other.hand)}
        eligibility.append([
            FOREARM_MIN_T < t < FOREARM_MAX_T
            and radius <= FOREARM_RADIUS
            and (source.get(side.lower_arm, 0.0) > 0.0
                 or source.get(side.hand, 0.0) > 0.0)
            and not any(source.get(name, 0.0) > 0.0 for name in side.finger_groups)
            and not any((name.endswith(opposite) or name in opposite_groups) and value > 0.0
                        for name, value in source.items())
            for (t, radius), source in zip(side_coordinates, physical, strict=True)
        ])
    # Snapshot applicability before either side is authored. The helper's positive
    # support is warranted only over the wrist-side transition; outside it, source
    # ownership does not warrant positive helper support.
    source_domain = tuple(tuple(
        allowed and 0.75 < point[0] < 1.10
        for allowed, point in zip(eligible, side_coordinates, strict=True)
    ) for eligible, side_coordinates in zip(eligibility, coordinates, strict=True))
    betas: list[list[float]] = []
    logical: list[list[float]] = []
    for side, eligible, side_coordinates in zip(sides, eligibility, coordinates, strict=True):
        # Ineligible vertices are k=0; eligible components without a boundary
        # (including isolated vertices) retain distance -1 and receive beta=1.
        distances = [-1] * len(vertices)
        queue = deque()
        for index, allowed in enumerate(eligible):
            if not allowed:
                distances[index] = 0
                queue.append(index)
        while queue:
            index = queue.popleft()
            for adjacent in neighbours[index]:
                if distances[adjacent] == -1:
                    distances[adjacent] = distances[index] + 1
                    queue.append(adjacent)
        beta = [0.0 if not allowed else 0.5 if distances[index] == 1 else 1.0
                for index, allowed in enumerate(eligible)]
        betas.append(beta)
        side_logical = []
        for index, source in enumerate(physical):
            l, x, h = (source.get(name, 0.0) for name in (side.lower_arm, side.helper, side.hand))
            blend = beta[index]
            if blend:
                pool = l + x + h
                u = min(1.0, max(0.0, (side_coordinates[index][0] - profile.proximal_t) / span))
                if u <= AXIAL_TWIST_CROSSOVER_PHASE:
                    z_twist = _smooth(u / AXIAL_TWIST_CROSSOVER_PHASE)
                    z_lower, z_hand = 1.0 - z_twist, 0.0
                else:
                    z_hand = _smooth((u - AXIAL_TWIST_CROSSOVER_PHASE) / (1.0 - AXIAL_TWIST_CROSSOVER_PHASE))
                    z_twist, z_lower = 1.0 - z_hand, 0.0
                row = result[index]
                _assign(row, side.lower_arm, (1.0 - blend) * l + blend * pool * z_lower)
                _assign(row, side.hand, (1.0 - blend) * h + blend * pool * z_hand)
                _assign(row, side.helper, (1.0 - blend) * x + blend * pool * z_twist)
            side_logical.append(result[index].get(side.helper, 0.0))
        logical.append(side_logical)
    return AxialAuthoring(result, logical, betas, keys, coordinates, tuple(sides), source_domain)


def build_vertex_neighbours(vertex_count: int, polygons: Iterable[Sequence[int]]) -> list[set[int]]:
    """Build undirected source-topology adjacency without triangulating authored faces."""

    neighbours = [set() for _ in range(vertex_count)]
    for polygon in polygons:
        indices = list(polygon)
        for offset, first in enumerate(indices):
            second = indices[(offset + 1) % len(indices)]
            if first == second:
                continue
            neighbours[first].add(second)
            neighbours[second].add(first)
    return neighbours


def topology_coordinates(
    vertices: Sequence[ForearmVertex],
    neighbours: Sequence[set[int]],
    profile: ForearmTwistWeightProfile,
    eligible: Sequence[bool],
) -> list[float]:
    """Return a connectivity-regularised proximal-to-distal coordinate.

    End rows are fixed from the measured rest frame.  Interior values are relaxed over
    actual mesh edges, making every circumferential row converge together instead of
    assigning visibly different crossover weights from axis position alone.
    """

    span = profile.distal_t - profile.proximal_t
    if span <= 0.0:
        raise ValueError("Forearm twist profile distal_t must exceed proximal_t")
    coordinates = [min(1.0, max(0.0, (vertex.t - profile.proximal_t) / span)) for vertex in vertices]
    fixed = [
        not eligible[index]
        or vertex.t <= profile.proximal_t
        or vertex.t >= profile.distal_t
        for index, vertex in enumerate(vertices)
    ]
    for _pass in range(profile.smoothing_passes):
        updated = list(coordinates)
        for index, adjacent in enumerate(neighbours):
            if fixed[index]:
                continue
            connected = [coordinates[item] for item in adjacent if eligible[item]]
            if connected:
                # Retain a small measured-position term to stop disconnected or highly
                # irregular clothing topology from drifting longitudinally.
                updated[index] = (coordinates[index] + sum(connected)) / (len(connected) + 1)
        coordinates = updated
    return coordinates


def prune_and_normalise_deform_weights(
    weights: Mapping[str, float], deform_group_names: set[str]
) -> dict[str, float]:
    """Apply the export/import zero-pruning and normalisation contract to deform weights."""

    retained = {
        name: weight
        for name, weight in weights.items()
        if name in deform_group_names and weight > 0.0
    }
    total = sum(retained.values())
    if total <= 0.0:
        return {}
    return {name: weight / total for name, weight in retained.items()}


def assert_axial_ownership_contract(
    reference: Mapping[str, float],
    candidate: Mapping[str, float],
    deform_group_names: set[str],
    lower_arm: str,
    helper: str,
    hand: str,
    tolerance: float,
) -> tuple[dict[str, float], dict[str, float]]:
    """Verify the immutable axial ownership snapshot against the final mesh row.

    The saved mesh must realise the authored three-anchor reference: every
    positive deform influence, including the single helper, retains its authored
    ownership after the export/import float32 round trip.
    """

    before = prune_and_normalise_deform_weights(reference, deform_group_names)
    after = prune_and_normalise_deform_weights(candidate, deform_group_names)
    if not before:
        raise ValueError("reference row has no deform ownership after pruning")
    if not after:
        raise ValueError("candidate row has no deform ownership after pruning")
    if any(weight < 0.0 for weight in candidate.values()):
        raise ValueError("candidate row contains a negative influence")

    if abs(before.get(helper, 0.0) - after.get(helper, 0.0)) > tolerance:
        raise ValueError(
            "helper ownership does not equal the authored axial reference "
            f"({after.get(helper, 0.0):.9f} != {before.get(helper, 0.0):.9f})"
        )

    for name in (lower_arm, hand):
        if abs(before.get(name, 0.0) - after.get(name, 0.0)) > tolerance:
            label = "lower-arm" if name == lower_arm else "hand"
            raise ValueError(
                f"{label} influence {name!r} changed against the axial reference "
                f"({after.get(name, 0.0):.9f} != {before.get(name, 0.0):.9f})"
            )
    for name in sorted((set(before) | set(after)) - {helper, lower_arm, hand}):
        if abs(before.get(name, 0.0) - after.get(name, 0.0)) > tolerance:
            raise ValueError(
                f"unrelated influence {name!r} changed against the axial reference "
                f"({after.get(name, 0.0):.9f} != {before.get(name, 0.0):.9f})"
            )
    return before, after


def clean_mirrored_contamination(
    weights: dict[str, float],
    wrong_suffix: str,
    correct_suffix: str,
) -> None:
    """Retain source membership: suffix alone cannot establish exporter contamination.

    No exporter-added assignment is independently attributable at this boundary.
    """


def is_source_bilateral(weights: Mapping[str, float]) -> bool:
    """Detect positive physical ownership on both sides, ignoring zero memberships."""

    return any(name.endswith("_l") and weight > 0.0 for name, weight in weights.items()) and any(
        name.endswith("_r") and weight > 0.0 for name, weight in weights.items()
    )
