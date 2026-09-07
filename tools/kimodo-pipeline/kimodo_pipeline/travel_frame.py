"""Stable constant source-travel frame estimation for straight locomotion.

The estimator is deliberately pure Python so its confidence contract can be
tested without Blender.  Coordinates are horizontal ``(lateral, forward)``
samples in the loaded Root-rest basis and distances are in rig units.
"""
from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Sequence


@dataclass(frozen=True)
class TravelFrameLimits:
    minimum_net_travel_rig_scale: float = 0.25
    minimum_pca_anisotropy: float = 16.0
    maximum_endpoint_pca_angle_degrees: float = 5.0
    maximum_subwindow_angle_degrees: float = 15.0
    maximum_orthogonal_excursion_fraction: float = 0.15
    minimum_subwindow_travel_rig_scale: float = 0.05


DEFAULT_TRAVEL_FRAME_LIMITS = TravelFrameLimits()


class TravelFrameError(ValueError):
    """The samples do not establish a stable straight locomotion frame."""


@dataclass(frozen=True)
class TravelFrame:
    direction: tuple[float, float]
    progress: tuple[float, ...]
    net_travel_rig_scale: float
    pca_anisotropy: float
    endpoint_pca_angle_degrees: float
    subwindow_angle_degrees: tuple[float, float, float]
    subwindow_travel_rig_scale: tuple[float, float, float]
    orthogonal_excursion_fraction: float

    def evidence(self) -> dict[str, object]:
        return {
            "procedure": "constant_endpoint_pca_three_subwindow_source_travel_frame",
            "direction_lateral_forward": list(self.direction),
            "progress": list(self.progress),
            "net_travel_rig_scale": self.net_travel_rig_scale,
            "pca_anisotropy": self.pca_anisotropy,
            "endpoint_pca_angle_degrees": self.endpoint_pca_angle_degrees,
            "subwindow_angle_degrees": list(self.subwindow_angle_degrees),
            "subwindow_travel_rig_scale": list(self.subwindow_travel_rig_scale),
            "orthogonal_excursion_fraction": self.orthogonal_excursion_fraction,
        }


def _dot(first: tuple[float, float], second: tuple[float, float]) -> float:
    return first[0] * second[0] + first[1] * second[1]


def _normalise(vector: tuple[float, float]) -> tuple[float, float]:
    length = math.hypot(*vector)
    if length <= 1e-12:
        raise TravelFrameError("degenerate source travel: zero horizontal direction")
    return vector[0] / length, vector[1] / length


def _angle(first: tuple[float, float], second: tuple[float, float]) -> float:
    cosine = max(-1.0, min(1.0, _dot(_normalise(first), _normalise(second))))
    return math.degrees(math.acos(cosine))


def estimate_source_travel_frame(
    samples: Sequence[tuple[float, float]],
    rig_scale: float,
    limits: TravelFrameLimits = DEFAULT_TRAVEL_FRAME_LIMITS,
) -> TravelFrame:
    """Estimate one signed straight frame or reject deterministically.

    Endpoint direction establishes sign.  Total-least-squares/PCA, three
    equal-index displacement subwindows, and orthogonal excursion independently
    reject curved, confined, noisy, or poorly conditioned traces.  Every length
    threshold is normalised by the loaded rig scale.
    """
    if len(samples) < 7:
        raise TravelFrameError("degenerate source travel: fewer than seven samples")
    if not math.isfinite(rig_scale) or rig_scale <= 1e-12:
        raise TravelFrameError("degenerate source travel: invalid rig scale")
    if any(not math.isfinite(value) for sample in samples for value in sample):
        raise TravelFrameError("unstable source travel: non-finite horizontal sample")

    origin = samples[0]
    offsets = tuple((sample[0] - origin[0], sample[1] - origin[1]) for sample in samples)
    endpoint = offsets[-1]
    net = math.hypot(*endpoint)
    net_normalised = net / rig_scale
    if net_normalised < limits.minimum_net_travel_rig_scale:
        raise TravelFrameError(
            f"degenerate source travel: net {net_normalised:.6f} rig scale is below "
            f"{limits.minimum_net_travel_rig_scale:.6f}"
        )
    direction = _normalise(endpoint)

    mean_x = sum(point[0] for point in offsets) / len(offsets)
    mean_y = sum(point[1] for point in offsets) / len(offsets)
    xx = sum((point[0] - mean_x) ** 2 for point in offsets) / len(offsets)
    yy = sum((point[1] - mean_y) ** 2 for point in offsets) / len(offsets)
    xy = sum((point[0] - mean_x) * (point[1] - mean_y) for point in offsets) / len(offsets)
    trace = xx + yy
    discriminant = math.sqrt(max(0.0, (xx - yy) ** 2 + 4.0 * xy * xy))
    major, minor = (trace + discriminant) * 0.5, (trace - discriminant) * 0.5
    anisotropy = major / max(minor, 1e-15)
    if anisotropy < limits.minimum_pca_anisotropy:
        raise TravelFrameError(
            f"unstable source travel: PCA anisotropy {anisotropy:.6f} is below "
            f"{limits.minimum_pca_anisotropy:.6f}"
        )
    pca = _normalise((xy, major - xx) if abs(xy) + abs(major - xx) > 1e-15 else (1.0, 0.0))
    if _dot(pca, direction) < 0.0:
        pca = -pca[0], -pca[1]
    pca_angle = _angle(direction, pca)
    if pca_angle > limits.maximum_endpoint_pca_angle_degrees:
        raise TravelFrameError(
            f"unstable source travel: endpoint/PCA angle {pca_angle:.6f} degrees exceeds "
            f"{limits.maximum_endpoint_pca_angle_degrees:.6f}"
        )

    final_index = len(samples) - 1
    boundaries = (0, final_index // 3, (2 * final_index) // 3, final_index)
    subwindow_angles: list[float] = []
    subwindow_travel: list[float] = []
    for first, last in zip(boundaries, boundaries[1:]):
        displacement = samples[last][0] - samples[first][0], samples[last][1] - samples[first][1]
        travel = math.hypot(*displacement) / rig_scale
        subwindow_travel.append(travel)
        if travel < limits.minimum_subwindow_travel_rig_scale:
            raise TravelFrameError(
                f"degenerate source travel: subwindow {first}:{last} travel {travel:.6f} rig scale "
                f"is below {limits.minimum_subwindow_travel_rig_scale:.6f}"
            )
        angle = _angle(direction, displacement)
        subwindow_angles.append(angle)
        if angle > limits.maximum_subwindow_angle_degrees:
            raise TravelFrameError(
                f"unstable source travel: subwindow {first}:{last} angle {angle:.6f} degrees exceeds "
                f"{limits.maximum_subwindow_angle_degrees:.6f}"
            )

    orthogonal = (-direction[1], direction[0])
    excursion = max(abs(_dot(point, orthogonal)) for point in offsets) / net
    if excursion > limits.maximum_orthogonal_excursion_fraction:
        raise TravelFrameError(
            f"unstable source travel: orthogonal excursion {excursion:.6f} exceeds "
            f"{limits.maximum_orthogonal_excursion_fraction:.6f} of net travel"
        )
    progress = tuple(_dot(point, direction) for point in offsets)
    return TravelFrame(
        direction, progress, net_normalised, anisotropy, pca_angle,
        (subwindow_angles[0], subwindow_angles[1], subwindow_angles[2]),
        (subwindow_travel[0], subwindow_travel[1], subwindow_travel[2]), excursion,
    )


def estimate_finite_transition_frame(
    samples: Sequence[tuple[float, float]],
    rig_scale: float,
    limits: TravelFrameLimits = DEFAULT_TRAVEL_FRAME_LIMITS,
) -> TravelFrame:
    """Estimate a straight frame without imposing sustained-loop subwindows.

    A finite acceleration/deceleration interval is allowed to have very little
    travel and comparatively large sway near its idle endpoint.  Endpoint/PCA
    agreement and bounded orthogonal excursion still reject curved or unstable
    candidates, but equal-index minimum-travel and angle tests are deliberately
    inapplicable.  Scalar progress is copied from the complete finite trace.
    """
    if len(samples) < 3:
        raise TravelFrameError("degenerate finite transition: fewer than three samples")
    if not math.isfinite(rig_scale) or rig_scale <= 1e-12:
        raise TravelFrameError("degenerate finite transition: invalid rig scale")
    if any(not math.isfinite(value) for sample in samples for value in sample):
        raise TravelFrameError("unstable finite transition: non-finite horizontal sample")

    origin = samples[0]
    offsets = tuple((sample[0] - origin[0], sample[1] - origin[1]) for sample in samples)
    endpoint = offsets[-1]
    net = math.hypot(*endpoint)
    net_normalised = net / rig_scale
    if net_normalised < limits.minimum_subwindow_travel_rig_scale:
        raise TravelFrameError(
            f"degenerate finite transition: net {net_normalised:.6f} rig scale is below "
            f"{limits.minimum_subwindow_travel_rig_scale:.6f}"
        )
    direction = _normalise(endpoint)
    mean_x = sum(point[0] for point in offsets) / len(offsets)
    mean_y = sum(point[1] for point in offsets) / len(offsets)
    xx = sum((point[0] - mean_x) ** 2 for point in offsets) / len(offsets)
    yy = sum((point[1] - mean_y) ** 2 for point in offsets) / len(offsets)
    xy = sum((point[0] - mean_x) * (point[1] - mean_y) for point in offsets) / len(offsets)
    trace = xx + yy
    discriminant = math.sqrt(max(0.0, (xx - yy) ** 2 + 4.0 * xy * xy))
    major, minor = (trace + discriminant) * 0.5, (trace - discriminant) * 0.5
    anisotropy = major / max(minor, 1e-15)
    if anisotropy < limits.minimum_pca_anisotropy:
        raise TravelFrameError(
            f"unstable finite transition: PCA anisotropy {anisotropy:.6f} is below "
            f"{limits.minimum_pca_anisotropy:.6f}"
        )
    pca = _normalise((xy, major - xx) if abs(xy) + abs(major - xx) > 1e-15 else (1.0, 0.0))
    if _dot(pca, direction) < 0.0:
        pca = -pca[0], -pca[1]
    pca_angle = _angle(direction, pca)
    if pca_angle > limits.maximum_endpoint_pca_angle_degrees:
        raise TravelFrameError(
            f"unstable finite transition: endpoint/PCA angle {pca_angle:.6f} degrees exceeds "
            f"{limits.maximum_endpoint_pca_angle_degrees:.6f}"
        )
    orthogonal = (-direction[1], direction[0])
    excursion = max(abs(_dot(point, orthogonal)) for point in offsets) / net
    if excursion > limits.maximum_orthogonal_excursion_fraction:
        raise TravelFrameError(
            f"unstable finite transition: orthogonal excursion {excursion:.6f} exceeds "
            f"{limits.maximum_orthogonal_excursion_fraction:.6f} of net travel"
        )
    progress = tuple(_dot(point, direction) for point in offsets)
    return TravelFrame(
        direction, progress, net_normalised, anisotropy, pca_angle,
        (0.0, 0.0, 0.0), (0.0, 0.0, 0.0), excursion,
    )


def alignment_angle(
    source_direction: tuple[float, float], target_direction: tuple[float, float]
) -> float:
    """Return the signed constant horizontal rotation from source to target."""
    source = _normalise(source_direction)
    target = _normalise(target_direction)
    return math.atan2(source[0] * target[1] - source[1] * target[0], _dot(source, target))
