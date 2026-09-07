"""Deterministic, data-driven source-window selection for ANIM-002."""
from __future__ import annotations

from dataclasses import dataclass
import math
from statistics import median
from typing import Any, Iterable, Mapping, Sequence

from .contracts import transition_boundary_phase


class SelectionError(RuntimeError):
    """Raised when observed source data cannot supply a required role."""


@dataclass(frozen=True)
class Candidate:
    action_name: str
    start: int
    end: int
    score: float
    evidence: dict[str, Any]
    content_signature: str = ""


@dataclass(frozen=True)
class FootSample:
    """Foot position and evaluated velocity relative to the translating Hips."""

    position: float
    velocity: tuple[float, float, float]
    lateral_position: float = 0.0
    arc_position: float | None = None
    arc_velocity: float | None = None

    @property
    def speed(self) -> float:
        """Return the full three-dimensional relative velocity magnitude."""
        return math.sqrt(sum(component * component for component in self.velocity))

    @property
    def root_forward_velocity(self) -> float:
        """Return the signed velocity on the immutable Root-forward axis."""
        return self.velocity[2]

    @property
    def root_forward_confidence(self) -> float:
        """Return the share of movement which is actually Root-forward."""
        return abs(self.root_forward_velocity) / self.speed if self.speed > 1e-9 else 0.0


FootGeometry = Mapping[str, Mapping[int, FootSample]]


def phase_semantics(role_code: str) -> tuple[str, int]:
    """Return the rig-semantic crossing axis and declared travel sign.

    The sampler always stores velocity as (lateral, up, forward).  This keeps
    phase selection independent of Blender's local channel axes and makes the
    same hysteresis rule usable for straight, lateral, and curved travel.
    """
    if role_code.startswith("xr"):
        return "lateral", -1
    if role_code.startswith("xl"):
        return "lateral", 1
    if role_code.startswith(("ar", "al")):
        return "arc_tangent", 1
    if role_code.startswith(("wf", "i2f", "f2i")):
        return "forward", 1
    if role_code.startswith(("wb", "i2b", "b2i")):
        return "forward", -1
    raise SelectionError(f"phase selection does not support {role_code}")


def _axis_position(sample: FootSample, axis: str) -> float:
    if axis == "lateral":
        return sample.lateral_position
    if axis == "arc_tangent":
        return sample.position if sample.arc_position is None else sample.arc_position
    return sample.position


def _axis_velocity(sample: FootSample, axis: str) -> float:
    if axis == "lateral":
        return sample.velocity[0]
    if axis == "arc_tangent":
        return sample.root_forward_velocity if sample.arc_velocity is None else sample.arc_velocity
    return sample.root_forward_velocity


def _axis_confidence(sample: FootSample, axis: str) -> float:
    return abs(_axis_velocity(sample, axis)) / sample.speed if sample.speed > 1e-9 else 0.0


# Speeds are magnitudes measured relative to Hips.  The threshold is an
# intentionally small hysteresis floor for sampled metre/frame animation data;
# the relation, not a particular positional offset, establishes release.
RELEASE_SPEED_MARGIN = 1e-4
RELEASE_HYSTERESIS_FRAMES = 2
# Root-forward velocity is rig-scale-normalised by the evaluated Blender
# sampler. These values describe a phase, not a source frame or position.
RELEASE_FORWARD_MINIMUM = 0.005
RELEASE_FORWARD_CONFIDENCE_MINIMUM = 0.60


@dataclass(frozen=True)
class TransitionEvidence:
    """A measured transition window and the reason it may not be used."""

    crossing: int
    start: int
    end: int
    eligible_crossings: tuple[tuple[str, int], ...]
    accepted: bool
    reason: str | None
    source_foot_side: str
    mapped_output_foot_side: str


def _sample_evidence(sample: FootSample, axis: str) -> dict[str, Any]:
    """Return JSON-safe, complete Root-semantic sample evidence."""
    return {
        "phase_axis": axis,
        "phase_axis_position": _axis_position(sample, axis),
        "semantic_lateral_position": sample.lateral_position,
        "root_forward_position": sample.position,
        "relative_velocity": list(sample.velocity),
        "phase_axis_velocity": _axis_velocity(sample, axis),
        "root_forward_velocity": sample.root_forward_velocity,
        "velocity_magnitude": sample.speed,
        "directional_release_confidence": _axis_confidence(sample, axis),
    }


def release_event_evidence(
    segment: dict[str, int], geometry: FootGeometry, side: str, role_code: str = "wf_r"
) -> list[dict[str, Any]]:
    """Audit every positional crossing, including directionally rejected ones.

    A positional centre-line crossing is only a *proposed* release.  Keeping
    the rejected proposals makes a near-zero or tangential crossing auditable
    instead of silently disappearing before transition-window selection.
    """
    if side not in {"Left", "Right"}:
        raise SelectionError(f"unknown anatomical foot: {side}")
    start, end = int(segment["start_frame"]), int(segment["end_frame"])
    axis, direction = phase_semantics(role_code)
    other = "Left" if side == "Right" else "Right"
    rows: list[dict[str, Any]] = []
    for frame in range(start + 1, end + 1):
        previous = _axis_position(geometry[side][frame - 1], axis) - _axis_position(geometry[other][frame - 1], axis)
        current = _axis_position(geometry[side][frame], axis) - _axis_position(geometry[other][frame], axis)
        crosses = previous <= 0.0 < current if direction > 0 else previous >= 0.0 > current
        if not crosses:
            continue
        horizon = list(range(frame, min(end, frame + RELEASE_HYSTERESIS_FRAMES - 1) + 1))
        horizon_rows = []
        reasons = []
        for sample_frame in horizon:
            sample, planted = geometry[side][sample_frame], geometry[other][sample_frame]
            direction_ok = direction * _axis_velocity(sample, axis) >= RELEASE_FORWARD_MINIMUM
            confidence_ok = _axis_confidence(sample, axis) >= RELEASE_FORWARD_CONFIDENCE_MINIMUM
            release_faster = sample.speed >= planted.speed + RELEASE_SPEED_MARGIN
            if not direction_ok:
                reasons.append(f"frame {sample_frame} Root-forward velocity has wrong direction or is below minimum")
            if not confidence_ok:
                reasons.append(f"frame {sample_frame} directional-release confidence is below minimum")
            if not release_faster:
                reasons.append(f"frame {sample_frame} releasing foot is not faster than planted foot")
            horizon_rows.append({
                "frame": sample_frame,
                "releasing": _sample_evidence(sample, axis),
                "planted_velocity_magnitude": planted.speed,
                "direction_ok": direction_ok,
                "confidence_ok": confidence_ok,
                "release_faster_than_planted": release_faster,
            })
        accepted = not reasons
        rows.append({
            "source_frame": frame,
            "foot_side": side,
            "phase_axis": axis,
            "direction": direction,
            "crossing_scalar": {"previous": previous, "current": current},
            "sample": _sample_evidence(geometry[side][frame], axis),
            "hysteresis_horizon_samples": horizon_rows,
            "accepted": accepted,
            "reason": "accepted directional release" if accepted else "; ".join(reasons),
        })
    return rows


def directional_phase_event_observation(
    segment: dict[str, int], geometry: FootGeometry, side: str, role_code: str,
    event_kind: str, frame: int, *, duplicate_endpoint: bool,
    boundary_context: str | None = None,
) -> dict[str, Any]:
    """Measure a directional Release/Acquire phase seed on the final hierarchy.

    Acquire is the exact temporal inverse of Release: it uses the following
    positional sample and a backwards hysteresis horizon.  This keeps physical
    lifecycle semantics separate from the source selector's directional
    release label while using the same fixed kinematic thresholds.
    """
    if event_kind not in {"Release", "Acquire"}:
        raise SelectionError(f"unknown physical event kind: {event_kind}")
    if side not in {"Left", "Right"}:
        raise SelectionError(f"unknown anatomical foot: {side}")
    start, end = int(segment["start_frame"]), int(segment["end_frame"])
    if not start <= frame <= end:
        raise SelectionError("physical event frame is outside the evaluated interval")
    unique_end = end - 1 if duplicate_endpoint else end
    if unique_end < start + 1:
        raise SelectionError("physical event interval has insufficient unique samples")
    other = "Left" if side == "Right" else "Right"
    axis, direction = phase_semantics(role_code)

    def wrap(value: int) -> int:
        if value < start:
            return unique_end
        if value > unique_end:
            return start
        return value

    current_frame = start if duplicate_endpoint and frame == end else frame
    neighbour_value = current_frame - 1 if event_kind == "Release" else current_frame + 1
    neighbour_frame = (
        wrap(neighbour_value)
        if duplicate_endpoint else max(start, min(unique_end, neighbour_value))
    )
    current = _axis_position(geometry[side][current_frame], axis) - _axis_position(geometry[other][current_frame], axis)
    neighbour = _axis_position(geometry[side][neighbour_frame], axis) - _axis_position(geometry[other][neighbour_frame], axis)
    directed_current, directed_neighbour = direction * current, direction * neighbour
    crosses = (
        directed_neighbour <= 0.0 < directed_current
        if event_kind == "Release"
        else directed_current < 0.0 <= directed_neighbour
    )
    boundary_context_supplies_omitted_neighbour = (
        not duplicate_endpoint
        and (
            (boundary_context == "start" and current_frame == start)
            or (boundary_context == "end" and current_frame == unique_end)
        )
    )
    if boundary_context_supplies_omitted_neighbour:
        crosses = True
    if duplicate_endpoint:
        horizon = [
            wrap(current_frame + (-offset if event_kind == "Acquire" else offset))
            for offset in range(RELEASE_HYSTERESIS_FRAMES)
        ]
    elif event_kind == "Release":
        horizon = list(range(current_frame, min(unique_end, current_frame + RELEASE_HYSTERESIS_FRAMES - 1) + 1))
    else:
        horizon = list(range(current_frame, max(start, current_frame - RELEASE_HYSTERESIS_FRAMES + 1) - 1, -1))
    samples, reasons = [], []
    # Reversing time also reverses the declared travel direction.  Therefore a
    # derived Acquire still moves in its output role's travel direction even
    # though its lifecycle and hysteresis order are the inverse of Release.
    velocity_sign = direction
    for sample_frame in horizon:
        # The first runtime sample of a loop has no preceding sample in the
        # evaluated geometry cache, so its velocity is necessarily zero.  If
        # a conceptual endpoint event was canonicalised onto that sample, use
        # the duplicate endpoint's final evaluated derivative while retaining
        # the runtime frame identity and opening pose for crossing evidence.
        lifecycle_frame = (
            end if duplicate_endpoint and sample_frame == start else sample_frame
        )
        if (
            boundary_context_supplies_omitted_neighbour
            and lifecycle_frame == start
        ):
            lifecycle_frame = min(unique_end, start + 1)
        sample = geometry[side][lifecycle_frame]
        counterpart = geometry[other][lifecycle_frame]
        direction_ok = velocity_sign * _axis_velocity(sample, axis) >= RELEASE_FORWARD_MINIMUM
        confidence_ok = _axis_confidence(sample, axis) >= RELEASE_FORWARD_CONFIDENCE_MINIMUM
        lifecycle_contrast = sample.speed >= counterpart.speed + RELEASE_SPEED_MARGIN
        if not direction_ok:
            reasons.append(f"frame {sample_frame} lifecycle velocity has wrong direction or is below minimum")
        if not confidence_ok:
            reasons.append(f"frame {sample_frame} lifecycle directional confidence is below minimum")
        if not lifecycle_contrast:
            reasons.append(f"frame {sample_frame} transitioning foot is not faster than counterpart")
        samples.append({
            "frame": sample_frame,
            "evaluated_derivative_frame": lifecycle_frame,
            "transitioning": _sample_evidence(sample, axis),
            "counterpart_velocity_magnitude": counterpart.speed,
            "direction_ok": direction_ok,
            "confidence_ok": confidence_ok,
            "lifecycle_contrast": lifecycle_contrast,
        })
    if not crosses:
        reasons.insert(0, "post-repair evaluated hierarchy has no expected centre-line transition")
    effective_derivative_frames = [int(item["evaluated_derivative_frame"]) for item in samples]
    confidence = min(
        (_axis_confidence(geometry[side][sample_frame], axis) for sample_frame in effective_derivative_frames),
        default=0.0,
    )
    return {
        "accepted": not reasons,
        "reason": "accepted post-repair directional phase transition" if not reasons else "; ".join(reasons),
        "confidence": confidence,
        "measurement": {
            "coordinate_space": "generated Root-relative evaluated hierarchy",
            "boundary_context": boundary_context,
            "boundary_context_supplies_omitted_neighbour": boundary_context_supplies_omitted_neighbour,
            "phase_axis": axis,
            "declared_travel_direction": direction,
            "current_frame": current_frame,
            "neighbour_frame": neighbour_frame,
            "crossing_scalar": {"neighbour": neighbour, "current": current},
            "hysteresis_samples": samples,
            "effective_derivative_frames": effective_derivative_frames,
        },
    }


def _crossings_in_window(
    segment: dict[str, int], geometry: FootGeometry, role_code: str, start: int, end: int
) -> tuple[tuple[str, int], ...]:
    """Count both feet, using the complete segment as crossing context.

    A frame at an output edge still needs its predecessor and hysteresis
    successor from the source segment.  This deliberately avoids treating a
    clipped Action's first sample as evidence that no crossing occurred.
    """
    return tuple(
        (side, frame)
        for side in ("Left", "Right")
        for frame in _release_events(segment, geometry, side, role_code)
        if start <= frame <= end
    )


def transition_evidence(
    segment: dict[str, int], role_code: str, geometry: FootGeometry,
    derivation: str = "native", output_role_code: str | None = None,
) -> tuple[TransitionEvidence, ...]:
    """Return every boundary candidate, retaining phase rejection evidence."""
    boundary = transition_boundary_phase(role_code)
    if boundary is None:
        raise SelectionError(f"transition selection does not support {role_code}")
    edge, lead = boundary
    start, end = int(segment["start_frame"]), int(segment["end_frame"])
    rows = []
    required_output_lead = transition_boundary_phase(output_role_code or role_code)
    if required_output_lead is None:
        raise SelectionError(f"transition selection does not support {output_role_code or role_code}")
    for named_lead in (lead,):
        for crossing in _release_events(segment, geometry, named_lead, role_code):
            retained_start, retained_end = (start, crossing) if edge == "end" else (crossing, end)
            crossings = _crossings_in_window(segment, geometry, role_code, retained_start, retained_end)
            mapped_output_lead = derived_anatomical_side(named_lead, derivation)
            # Finite acceleration/deceleration may legitimately retain gait
            # evidence from the opposite foot.  The selected role event is the
            # named crossing nearest the locomotion-facing edge; surrounding
            # crossings are retained as topology evidence, not treated as a
            # sustained-loop recurrence violation.
            phase_accepted = (named_lead, crossing) in crossings
            anatomy_accepted = mapped_output_lead == required_output_lead[1]
            accepted = phase_accepted and anatomy_accepted
            reason = None
            if not phase_accepted:
                reason = f"retained interval does not retain selected crossing {(named_lead, crossing)}"
            elif not anatomy_accepted:
                reason = (
                    f"mapped output anatomy is {mapped_output_lead}, "
                    f"not required {required_output_lead[1]}"
                )
            rows.append(TransitionEvidence(
                crossing, retained_start, retained_end, crossings, accepted, reason,
                named_lead, mapped_output_lead,
            ))
    # Boundary proximity identifies the role crossing. Other finite-transition
    # gait evidence does not compete with that role event.
    # Segment 0 is idle -> sustained travel -> idle. Entry therefore owns the
    # first context-qualified named crossing after its idle endpoint; exit owns
    # the last one before its idle endpoint. This is candidate-local temporal
    # context, not a source frame convention.
    boundary_frame = start if edge == "end" else end
    return tuple(sorted(rows, key=lambda row: (abs(row.crossing - boundary_frame), row.crossing)))


def canonical_transition_events(
    segment: dict[str, int], geometry: FootGeometry, role_code: str,
    edge_context: dict[str, Any] | None,
) -> list[dict[str, Any]]:
    """Return the deduplicated, canonical release phases for a clipped transition.

    A transition's locomotion-facing output edge is allowed to be the first
    whole frame after a source crossing.  Its predecessor is then outside the
    generated Action, so a raw output sign-change is neither required nor
    sufficient.  ``edge_context`` is the retained source event and its mapped
    output edge; it must include the source predecessor/successor context.
    All other accepted crossings are evaluated directly from the complete
    output interval.
    """
    boundary = transition_boundary_phase(role_code)
    if boundary is None:
        raise SelectionError(f"canonical transition selection does not support {role_code}")
    edge, lead = boundary
    start, end = int(segment["start_frame"]), int(segment["end_frame"])
    boundary_frame = end if edge == "end" else start
    rows = [
        row
        for side in ("Left", "Right")
        for row in release_event_evidence(segment, geometry, side, role_code)
        if row["accepted"]
    ]
    canonical: list[dict[str, Any]] = []
    same_phase_frames: list[int] = []
    context_side: str | None = None
    if edge_context is not None:
        source_event = edge_context.get("source_event")
        predecessor = edge_context.get("predecessor_source_frame")
        successor = edge_context.get("successor_source_frame")
        output_frame = edge_context.get("output_frame")
        context_phase_frames = edge_context.get("same_phase_output_frames")
        if (
            not isinstance(source_event, dict)
            or not source_event.get("accepted")
            or (lead is not None and source_event.get("foot_side") != lead)
            or output_frame != boundary_frame
            or not isinstance(predecessor, int)
            or not isinstance(successor, int)
            or predecessor != source_event.get("source_frame", 0) - 1
            or successor < source_event.get("source_frame", 0) + RELEASE_HYSTERESIS_FRAMES - 1
            or not isinstance(context_phase_frames, list)
            or boundary_frame not in context_phase_frames
        ):
            label = lead.lower() if lead is not None else "lateral"
            raise SelectionError(f"{role_code} has no context-qualified {label} edge release")
        same_phase_frames = context_phase_frames
        context_side = source_event["foot_side"]
        canonical.append({
            "kind": "edge",
            "edge": edge,
            "output_frame": boundary_frame,
            "foot_side": source_event["foot_side"],
            "predecessor_context": {
                "source_frame": predecessor,
                "crossing_frame": source_event["source_frame"],
                "successor_source_frame": successor,
                "crossing_scalar": source_event["crossing_scalar"],
                "hysteresis_horizon_samples": source_event["hysteresis_horizon_samples"],
            },
        })
    # A raw event at the locomotion-facing edge is the same phase as the
    # context-qualified edge event, not a second release.  Every other raw
    # eligible crossing is independently disqualifying.
    for row in rows:
        if row["source_frame"] in same_phase_frames and row["foot_side"] == context_side and edge_context is not None:
            continue
        canonical.append({
            "kind": "interior",
            "edge": None,
            "output_frame": row["source_frame"],
            "foot_side": row["foot_side"],
            "predecessor_context": None,
        })
    return canonical


def foot_samples(positions: dict[str, dict[int, float]]) -> dict[str, dict[int, FootSample]]:
    """Create velocity samples for pure tests from fixed-axis positions."""
    result: dict[str, dict[int, FootSample]] = {"Left": {}, "Right": {}}
    for side, values in positions.items():
        previous: float | None = None
        for frame in sorted(values):
            position = values[frame]
            result[side][frame] = FootSample(
                position,
                (0.0, 0.0, 0.0) if previous is None else (0.0, 0.0, position - previous),
                arc_position=position,
                arc_velocity=0.0 if previous is None else position - previous,
            )
            previous = position
    return result


def lateral_foot_samples(positions: dict[str, dict[int, float]]) -> dict[str, dict[int, FootSample]]:
    """Create pure-test samples whose supplied scalar is rig-semantic lateral."""
    result: dict[str, dict[int, FootSample]] = {"Left": {}, "Right": {}}
    for side, values in positions.items():
        previous: float | None = None
        for frame in sorted(values):
            position = values[frame]
            velocity = 0.0 if previous is None else position - previous
            result[side][frame] = FootSample(0.0, (velocity, 0.0, 0.0), position)
            previous = position
    return result


def derived_anatomical_side(side: str, derivation: str) -> str:
    """Apply derivation semantics without confusing reverse time with anatomy."""
    if side not in {"Left", "Right"}:
        raise SelectionError(f"unknown anatomical foot: {side}")
    return ("Left" if side == "Right" else "Right") if "mirror" in derivation else side


def map_transition_event_to_output_anatomy(
    source_event: Mapping[str, Any] | None, derivation: str,
) -> dict[str, Any] | None:
    """Copy source evidence with its anatomical side expressed in output space.

    Reversal changes frame order, not anatomy.  Mirroring changes the output
    foot represented by a source release, so context consumed by generated
    output validation must use the mapped side while retaining the original
    source-frame and hysteresis evidence.
    """
    if source_event is None:
        return None
    mapped_event = dict(source_event)
    foot_side = mapped_event.get("foot_side")
    if not isinstance(foot_side, str):
        raise SelectionError("transition source event has no anatomical foot side")
    mapped_event["foot_side"] = derived_anatomical_side(foot_side, derivation)
    return mapped_event


def locomotion_direction(role_code: str) -> int:
    """Return the Root-forward sign for a straight locomotion role."""
    if role_code.startswith(("wf", "i2f", "f2i")):
        return 1
    if role_code.startswith(("wb", "i2b", "b2i")):
        return -1
    raise SelectionError(f"release selection does not support {role_code}")


def _release_events(segment: dict[str, int], geometry: FootGeometry, side: str, role_code: str) -> list[int]:
    """Return whole frames where ``side`` crosses and decisively releases.

    Position is solely a centre-line diagnostic. A crossing becomes a phase
    boundary only when the named foot moves in the declared Root-forward
    direction with rig-normalised magnitude and directional confidence for the
    hysteresis interval, while exceeding the planted foot's movement.
    """
    return [row["source_frame"] for row in release_event_evidence(segment, geometry, side, role_code) if row["accepted"]]


def release_events(segment: dict[str, int], geometry: FootGeometry, side: str, role_code: str = "wf_r") -> list[int]:
    """Public pure selection seam used by Blender regression assertions."""
    if side not in {"Left", "Right"}:
        raise SelectionError(f"unknown anatomical foot: {side}")
    return _release_events(segment, geometry, side, role_code)


def recurrence_window_evidence(
    segment: dict[str, int], role_code: str, geometry: FootGeometry,
    *, include_non_admissible_phase: bool = True,
) -> list[dict[str, Any]]:
    """Enumerate adjacent same-foot recurrence windows without selecting one."""
    if not role_code.startswith(("wf_", "wb_", "xr_", "xl_", "ar_", "al_")):
        raise SelectionError(f"cycle selection does not support {role_code}")
    lead = "Right" if role_code.endswith("_r") else "Left"
    sides = (lead, "Left" if lead == "Right" else "Right") if include_non_admissible_phase else (lead,)
    rows = []
    for side in sides:
        events = _release_events(segment, geometry, side, role_code)
        admissible = side == lead
        for index, (start, end) in enumerate(zip(events, events[1:])):
            rows.append({
                "source_range": [start, end],
                "source_foot_side": side,
                "required_foot_side": lead,
                "phase_admissible": admissible,
                "diagnostic_only": not admissible,
                "selected_by_current_policy": admissible and index == 0,
                "reason": (
                    "adjacent recurrence of the role-required anatomical foot"
                    if admissible else
                    f"opposite-foot phase experiment is diagnostic only and cannot admit {role_code}"
                ),
            })
    return rows


def _walk_cycle_window(segment: dict[str, int], role_code: str, geometry: FootGeometry) -> tuple[int, int]:
    if not role_code.startswith(("wf_", "wb_", "xr_", "xl_", "ar_", "al_")):
        raise SelectionError(f"cycle selection does not support {role_code}")
    lead = "Right" if role_code.endswith("_r") else "Left"
    events = _release_events(segment, geometry, lead, role_code)
    if len(events) < 2:
        raise SelectionError(f"{role_code} has no evidenced complete {lead.lower()} release cycle")
    return events[0], events[1]


def _transition_window(
    segment: dict[str, int], role_code: str, geometry: FootGeometry,
    derivation: str = "native", output_role_code: str | None = None,
) -> tuple[int, int]:
    """Select the role crossing nearest the locomotion-facing boundary."""
    rows = transition_evidence(segment, role_code, geometry, derivation, output_role_code)
    if not rows:
        boundary = transition_boundary_phase(role_code)
        assert boundary is not None
        label = boundary[1].lower()
        raise SelectionError(f"{role_code} has no evidenced {label} boundary")
    selected = next((row for row in rows if row.accepted), None)
    if selected is None:
        details = "; ".join(
            f"[{row.start},{row.end}] {row.reason}" for row in rows
        )
        raise SelectionError(f"{role_code} has no context-qualified transition candidate: {details}")
    return selected.start, selected.end


def quality_evidence(
    metrics_by_candidate: dict[str, dict[str, float]], role_family: str | None = None,
) -> dict[str, dict[str, Any]]:
    """Assess evaluated, rig-normalised metrics against robust peers.

    Values must already be dimensionless (divided by rig scale).  The function
    intentionally knows nothing about Action curves, names, or frame numbers.
    """
    if not metrics_by_candidate:
        return {}
    metric_names = sorted(set.intersection(*(set(metrics) for metrics in metrics_by_candidate.values())))
    baselines: dict[str, tuple[float, float]] = {}
    for metric in metric_names:
        values = [metrics[metric] for metrics in metrics_by_candidate.values()]
        centre = median(values)
        baselines[metric] = centre, median(abs(value - centre) for value in values)
    result: dict[str, dict[str, Any]] = {}
    # Foot-to-foot fore/aft span is stride evidence, not a leg-reach oracle.
    # Hard anatomy uses the evaluated limb chains and cycle-level topology.
    direct_lateral_family = role_family in {"strafe", "precision_lateral", "arc", "pivot"}
    hard_maximums = {
        "foot_speed": 3.0, "foot_acceleration": 4.0, "foot_jerk": 8.0,
        "ground_envelope": 1.5, "planted_foot_slip": 1.0,
        "body_support_offset": 2.0, "knee_extension": 2.0,
        "maximum_leg_segment_scale_error": 0.03,
        "maximum_leg_extension_ratio": 1.01,
        "collapsed_leg_fraction": 0.25,
        "maximum_torso_skew_ratio": 0.75,
        "maximum_torso_tilt_rad": math.radians(55.0),
    }
    if not direct_lateral_family:
        hard_maximums["maximum_lateral_width"] = 0.70
    hard_minimums = {
        "minimum_knee_orientation_dot": -0.15,
    }
    if not direct_lateral_family:
        hard_minimums["minimum_leg_lateral_separation_ratio"] = -0.25
    evidence_only_metrics = {
        "maximum_fore_aft_reach",
        # This measures evaluated segment-length preservation. Its absolute
        # severe limit remains a hard anatomy gate; peer-relative differences
        # at floating-point scale are representation noise, not motion quality.
        "maximum_leg_segment_scale_error",
    }
    if direct_lateral_family:
        evidence_only_metrics.update({
            "maximum_lateral_width",
            "minimum_leg_lateral_separation_ratio",
        })
    for name, metrics in sorted(metrics_by_candidate.items()):
        reasons = []
        baseline = {}
        for metric in metric_names:
            value = metrics[metric]
            centre, deviation = baselines[metric]
            baseline[metric] = {"median": centre, "mad": deviation}
            if metric in hard_maximums and value > hard_maximums[metric]:
                reasons.append(f"{metric} exceeds severe geometry limit")
            elif metric in hard_minimums and value < hard_minimums[metric]:
                reasons.append(f"{metric} violates anatomical orientation limit")
            # Three phase windows are not a robust peer population: one normal
            # asymmetric gait can otherwise make every candidate an outlier on
            # a different metric. Severe absolute geometry remains rejected.
            elif (
                not direct_lateral_family
                and len(metrics_by_candidate) >= 4
                and metric not in evidence_only_metrics
                and deviation > 1e-9 and value > centre + 3.5 * deviation
            ):
                reasons.append(f"{metric} is a robust high outlier")
        peer_outliers = {
            metric: value > baselines[metric][0] + 3.5 * baselines[metric][1]
            for metric, value in metrics.items()
            if len(metrics_by_candidate) >= 4 and baselines[metric][1] > 1e-9
        }
        result[name] = {
            "metrics": metrics, "extrema": {metric: metrics[metric] for metric in metric_names},
            "baselines": baseline,
            "thresholds": {metric: {
                "severe_maximum": hard_maximums.get(metric),
                "severe_minimum": hard_minimums.get(metric),
                "robust_high_maximum": baselines[metric][0] + 3.5 * baselines[metric][1],
            } for metric in metric_names},
            "accepted": not reasons, "reasons": reasons,
            "policy": {
                "role_family": role_family or "generic",
                "maximum_lateral_width": hard_maximums.get("maximum_lateral_width"),
                "fore_aft_reach": "evidence_only; hard safety derives from evaluated limb geometry",
                "lateral_width_and_separation": (
                    "evidence_only; role admission uses direct limb, knee, collapse, carriage, contact, and path evidence"
                    if direct_lateral_family else "generic hard geometry gate"
                ),
                "anatomical_contract": "role_phase_contact_aware_direct_physical_evidence_v2",
                "robust_peer_population": len(metrics_by_candidate),
                "robust_peer_outliers_evidence_only": direct_lateral_family,
                "peer_outliers": peer_outliers,
            },
        }
    return result


def natural_cycle_quality_rank_key(metrics: Mapping[str, float]) -> tuple[float, ...]:
    """Rank safe cycles by body/contact quality without bilateral pose equality."""
    return tuple(float(metrics.get(name, 0.0)) for name in (
        "planted_foot_slip",
        "body_support_offset",
        "maximum_torso_skew_ratio",
        "ground_envelope",
        "foot_jerk",
        "foot_acceleration",
        "collapsed_leg_fraction",
    ))


def natural_cycle_candidate_rank_key(
    metrics: Mapping[str, float],
    repair_rank_key: Sequence[float | int | str],
    content_signature: str,
    start: int,
    end: int,
) -> tuple[Any, ...]:
    """Apply naturalness precedence without Action identity or input order."""
    return (
        *natural_cycle_quality_rank_key(metrics),
        *repair_rank_key,
        content_signature,
        int(start),
        int(end),
    )


def role_window(
    segment: dict[str, int], role_code: str, curves: Iterable[Any] | None = None,
    foot_geometry: FootGeometry | None = None, derivation: str = "native",
    output_role_code: str | None = None,
) -> tuple[int, int]:
    """Derive windows from measured segment geometry, never fixture frame offsets."""
    start, end = int(segment["start_frame"]), int(segment["end_frame"])
    if end - start < 5:
        raise SelectionError("required Kimodo segment is too short to derive role windows")
    phase_roles = ("wf", "wb", "i2f", "f2i", "i2b", "b2i", "xr", "xl", "ar", "al")
    if segment["index"] in (0, 4, 5) and role_code.startswith(phase_roles):
        del curves
        if foot_geometry is None:
            raise SelectionError("segment-zero selection requires evaluated Root-axis foot velocity")
        if role_code.startswith(("wf", "wb", "xr_", "xl_", "ar_", "al_")):
            return _walk_cycle_window(segment, role_code, foot_geometry)
        return _transition_window(segment, role_code, foot_geometry, derivation, output_role_code)
    third = max(1, (end - start + 1) // 3)
    if role_code.startswith("b2i"):
        return start, start + third - 1
    if role_code.startswith("i2b"):
        return end - third + 1, end
    if role_code.startswith("i2"):
        return start, start + third - 1
    if "2i" in role_code:
        return end - third + 1, end
    if segment["index"] in (1, 2):
        return start, end
    return start + third, end - third


def score_candidate(
    action_name: str, curves: Iterable[Any], start: int, end: int,
    ranking_score: float = 0.0, ranking_evidence: Mapping[str, Any] | None = None,
    content_signature: str = "",
) -> Candidate:
    """Build a duration-neutral structural candidate after curve presence validation.

    Motion quality belongs to evaluated rig-normalised metrics, not raw F-curve
    values.  In particular, full-Action key counts must not be normalised by a
    role window's duration: that would rank equivalent windows by their length.
    ``ranking_score`` is supplied only by a role-specific evaluated contract
    (currently bounded loop repair cost). Structural selection leaves it zero.
    """
    rows = tuple(curves)
    if not rows:
        raise SelectionError(f"source Action '{action_name}' has no curves")
    if not math.isfinite(ranking_score) or ranking_score < 0.0:
        raise SelectionError("candidate ranking score must be finite and non-negative")
    return Candidate(action_name, start, end, ranking_score, {
        "ranking_basis": "evaluated role cost then duration-neutral structural tie-break" if ranking_evidence else "duration-neutral structural tie-break",
        "evaluated_role_cost": dict(ranking_evidence or {}),
        "structural_tie_break": {
            "content_signature": content_signature,
            "source_start": start,
            "source_end": end,
        },
        "excluded_from_ranking": [
            "full_action_key_count",
            "candidate_window_duration",
            "full_action_key_count_divided_by_candidate_window_duration",
            "raw_fcurve_values",
        ],
    }, content_signature)


def select_candidate(candidates: Iterable[Candidate]) -> Candidate:
    rows = tuple(candidates)
    if not rows:
        raise SelectionError("no source candidates were available")
    return sorted(rows, key=lambda item: (
        item.score,
        item.content_signature or item.action_name,
        item.start,
        item.end,
        item.action_name,
    ))[0]
