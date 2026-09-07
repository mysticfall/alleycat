from __future__ import annotations

import pytest

from kimodo_pipeline.transport import (
    TransportContractError,
    make_transport_evidence,
    validate_transport_evidence,
)


@pytest.mark.parametrize(("sample_count", "period"), [(33, 32), (40, 39)])
def test_loop_period_uses_inclusive_boundary_without_extra_sample(sample_count: int, period: int) -> None:
    evidence = make_transport_evidence(
        frame_start=1, frame_end=sample_count, fps=30.0, is_loop=True,
    )
    assert evidence["sample_count"] == sample_count
    assert evidence["period_frames"] == period
    assert evidence["runtime_sample_count"] == period
    assert evidence["duration_seconds"] == pytest.approx(period / 30.0)


def test_role_timelines_do_not_share_a_length_assumption() -> None:
    short = make_transport_evidence(frame_start=1, frame_end=33, fps=30.0, is_loop=True)
    long = make_transport_evidence(frame_start=1, frame_end=40, fps=30.0, is_loop=True)
    assert short["period_frames"] == 32
    assert long["period_frames"] == 39


def test_finite_transition_retains_its_inclusive_samples_and_duration() -> None:
    evidence = make_transport_evidence(frame_start=1, frame_end=29, fps=30.0, is_loop=False)
    assert evidence["sample_count"] == 29
    assert evidence["duration_frames"] == 28
    assert evidence["duration_seconds"] == pytest.approx(28 / 30.0)
    assert "period_frames" not in evidence
    assert "runtime_sample_count" not in evidence


@pytest.mark.parametrize("corruption", [
    {},
    {"period_frames": 33},
    {"sample_count": 34},
    {"frame_end": 34},
    {"duration_seconds": 1.1},
])
def test_missing_or_off_by_one_transport_evidence_is_rejected(corruption: dict[str, object]) -> None:
    evidence = make_transport_evidence(frame_start=1, frame_end=33, fps=30.0, is_loop=True)
    if not corruption:
        evidence.pop("period_frames")
    else:
        evidence.update(corruption)
    with pytest.raises(TransportContractError):
        validate_transport_evidence(
            evidence, frame_start=1, frame_end=33, fps=30.0, is_loop=True,
        )


@pytest.mark.parametrize(("field", "value"), [
    ("fps", True), ("fps", "30"), ("fps", 30), ("fps", float("nan")),
    ("frame_start", True), ("frame_start", "1"), ("frame_start", 1.5),
    ("frame_end", True), ("sample_count", 33.0), ("duration_frames", 32.0),
    ("duration_seconds", "1.0"), ("duration_seconds", float("inf")),
    ("period_frames", 32.0), ("runtime_sample_count", True),
    ("conceptual_boundary_frame", "33"),
])
def test_transport_evidence_rejects_non_exact_and_non_finite_types(field: str, value: object) -> None:
    evidence = make_transport_evidence(frame_start=1, frame_end=33, fps=30.0, is_loop=True)
    evidence[field] = value
    with pytest.raises(TransportContractError):
        validate_transport_evidence(evidence, frame_start=1, frame_end=33, fps=30.0, is_loop=True)


@pytest.mark.parametrize("kwargs", [
    {"frame_start": True}, {"frame_end": 33.5}, {"fps": 30},
    {"frame_start": 0}, {"frame_end": 1}, {"fps": float("nan")}, {"fps": 0.0},
])
def test_transport_construction_rejects_malformed_boundaries(kwargs: dict[str, object]) -> None:
    arguments: dict[str, object] = {"frame_start": 1, "frame_end": 33, "fps": 30.0, "is_loop": True}
    arguments.update(kwargs)
    with pytest.raises(TransportContractError):
        make_transport_evidence(**arguments)  # type: ignore[arg-type]
