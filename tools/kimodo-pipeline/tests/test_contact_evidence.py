"""Synthetic phase and bone-motion coverage for ANIM-002 contact refinement."""
from __future__ import annotations

import json
from dataclasses import replace
from pathlib import Path

import pytest


from kimodo_pipeline.bootstrap_paths import repository_root


ROOT = repository_root()

from kimodo_pipeline.phase_events import (  # type: ignore[import-not-found]
    canonicalise_duplicate_endpoint_events,
    derive_output_event,
    derivation_operations,
    event_id,
    transform_anatomical_side,
    transform_boundary_context,
)

from kimodo_pipeline.contact_evidence import (
    BoneSample,
    ContactConfiguration,
    ContactEvidenceError,
    METHOD,
    PhasePath,
    build_evidence,
    load_mapping,
    load_policy,
    refine_phase_path,
    resolve_phase_paths,
    validate_available_bones,
    validate_evidence_matches_package,
)


CONFIG = ContactConfiguration()


def policy() -> dict:
    return load_policy(ROOT / "tools/kimodo-pipeline/config/phase_contact_policy_v3.json")


def phase_entry(
    role: str,
    events: list[tuple[str, int, str | None]],
    start: int = 0,
    end: int = 10,
    identity: str = "opaque-identity",
    action: str = "opaque-action",
    derivation_name: str = "native",
    repair_width: int = 4,
) -> dict:
    duplicate = role in {"wf", "wb"} or role.startswith(("xl_", "xr_", "al_", "ar_"))
    source_rows = []
    for output_side, output_frame, output_boundary in events:
        source_side = transform_anatomical_side(output_side, derivation_name)
        source_frame = end - (output_frame - start) if "reverse" in derivation_name else output_frame
        source_boundary = transform_boundary_context(output_boundary, derivation_name)
        source_rows.append({
            "id": event_id(source_side), "anatomical_side": source_side,
            "event_kind": "Release", "source_frame": source_frame,
            "boundary_context": source_boundary, "selector_confidence": 0.95,
            "selector_evidence": {"fixture": True},
        })
    source = {
        "contract": "anim002_source_directional_phase_events_v1",
        "source_interval": [start, end], "events": source_rows,
    }
    derived = [
        derive_output_event(
            row, derivation=derivation_name, source_start=start,
            source_end=end, output_start=start,
        )
        for row in source_rows
    ]
    derived = canonicalise_duplicate_endpoint_events(
        derived, frame_start=start, frame_end=end, duplicate_endpoint=duplicate,
    )
    derivation = {
        "contract": "anim002_event_derivation_v1", "derivation": derivation_name,
        "operations": list(derivation_operations(derivation_name)),
        "frame_mapping": {"source_start": start, "source_end": end, "output_start": start, "output_end": end},
        "events": sorted(derived, key=lambda row: row["id"]),
    }
    post_events = [{
        **row, "accepted": True, "measurement": {"fixture": True},
        "confidence": 0.99, "derivation_event_id": row["id"],
        "derived_output_frame": row["output_frame"], "repair_frame_delta": 0,
        "seed_displacement_provenance": {
            "contract": "anim002_post_repair_phase_seed_displacement_v1",
            "derived_output_frame": row["output_frame"],
            "post_repair_output_frame": row["output_frame"],
            "canonical_delta_frames": 0,
            "source_detector_stencil_frames": [row["output_frame"]],
            "post_repair_detector_stencil_frames": [row["output_frame"]],
            "repair_support_dilated_frames": [],
            "source_repair_intersection_frames": [],
            "post_repair_repair_intersection_frames": [],
            "displacement_authorised": False,
        },
    } for row in derived]
    for row in post_events:
        row.pop("source_event_id", None)
    conceptual = end - start + 1
    post = {
        "contract": "anim002_post_repair_directional_phase_events_v2",
        "evaluation_stage": "final_repaired_evaluated_hierarchy",
        "timeline": {"fps": 10.0, "frame_start": start, "frame_end": end},
        "sample_semantics": {
            "is_loop": duplicate, "conceptual_sample_count": conceptual,
            "unique_runtime_sample_count": conceptual - (1 if duplicate else 0),
            "duplicate_endpoint": duplicate,
        },
        "events": sorted(post_events, key=lambda row: row["id"]), "refusals": [],
    }
    result = {
        "action": action, "external_identity": identity, "role": role, "metrics": "metrics.json",
        "source_directional_event_evidence": source,
        "event_derivation_evidence": derivation,
        "post_repair_event_evidence": post,
        "transport": {"frame_start": start, "frame_end": end},
    }
    if duplicate and role not in {"sl", "sr", "pl45", "pr45"}:
        repair = loop_repair(start, end, repair_width)
        result["loop_repair_evidence"] = repair
        unique = conceptual - 1
        raw = {
            start + (offset % unique)
            for field in ("opening_support", "closing_support")
            for offset in range(repair[field]["window"][0], repair[field]["window"][1] + 1)
        }
        dilated = {
            start + ((frame - start + delta) % unique)
            for frame in raw for delta in range(-4, 5)
        }
        for row in post_events:
            seed = row["output_frame"]
            stencil = {start + ((seed - start + delta) % unique) for delta in range(-4, 5)}
            intersection = stencil & dilated
            provenance = row["seed_displacement_provenance"]
            provenance["source_detector_stencil_frames"] = sorted(stencil)
            provenance["post_repair_detector_stencil_frames"] = sorted(stencil)
            provenance["repair_support_dilated_frames"] = sorted(dilated)
            provenance["source_repair_intersection_frames"] = sorted(intersection)
            provenance["post_repair_repair_intersection_frames"] = sorted(intersection)
            provenance["displacement_authorised"] = bool(intersection)
    return result


def loop_repair(start: int, end: int, width: int) -> dict:
    count = end - start + 1
    assert count >= 2 * width + 3
    return {
        "contract": "anim002_selected_loop_repair_provenance_v2",
        "algorithm": "symmetric_two_ended_hierarchy_pose_repair_v1",
        "blend_profile": "smoothstep",
        "opening_support": {"window": [0, width - 1], "width": width},
        "closing_support": {"window": [count - width, count - 1], "width": width},
        "immutable_interior": [width, count - width - 1], "repair_cost": 0.1,
        "selection": {"contract": "fixture", "selected_rank_key": [0.1]},
        "transform_derivation": {
            "source_range": [start, end],
            "frame_mapping": [{"source_frame": frame, "output_frame": frame} for frame in range(start, end + 1)],
            "role_derivation": "native", "temporal_order": "preserved",
            "anatomical_mapping": "preserved", "evaluated_space": "fixture",
            "repaired_domain": "fixture", "root_handling": "fixture",
        },
        "sample_semantics": {
            "conceptual_sample_count": count, "unique_runtime_sample_count": count - 1,
            "duplicate_endpoint": True, "conceptual_boundary_sample": count - 1,
            "runtime_interval": [0, count - 2],
        },
    }


def bone_samples(
    start: int = 0,
    end: int = 10,
    stable_ranges: tuple[tuple[int, int], ...] = ((0, 4),),
    offset: float = 0.0,
    drift: float = 0.0,
    lift: float = 0.08,
    support_slide: float = 0.0,
    swing_step: float = 0.08,
) -> list[BoneSample]:
    rows = []
    moving_x = 0.0
    for frame in range(start, end + 1):
        stable = any(first <= frame <= last for first, last in stable_ranges)
        moving_x += support_slide if stable else swing_step
        z = offset + drift * (frame - start) / max(1, end - start)
        position = (moving_x, 0.0, z if stable else z + lift)
        rows.append(BoneSample(
            frame, (frame - start) / 10.0, position, (1.0, 0.0, 0.0, 0.0),
            (frame * 0.02, 0.0, 1.0), (moving_x, 0.0, 0.5),
            (frame * -0.02, 0.0, offset), (frame * 0.01, 0.0, 0.0),
        ))
    return rows


def path(role: str = "i2f_l", anchor: int = 0, boundary: str | None = "end", classification: str = "required", end: int = 10) -> PhasePath:
    return PhasePath(
        role, "fixture", "finite_boundary_release", "left", classification,
        anchor, boundary, 0.95, 0, end, event_kind="Release", event_id="release_l",
        contact_polarity="Release",
    )


def declared_package(tmp_path: Path, entries: list[dict]) -> tuple[dict, Path, Path, Path, dict, Path, dict]:
    package = tmp_path / "package"
    package.mkdir()
    blend = package / "retarget.blend"
    blend.write_bytes(b"synthetic retarget")
    metrics = package / "metrics.json"
    metrics.write_text(json.dumps({"action": entries[0]["action"]}), encoding="utf-8")
    index = {
        "schema_version": 2, "metrics_schema_version": 2, "package": "fixture", "variant": "fixture",
        "target_skeleton": "FixtureRig", "action_count": len(entries), "actions": entries,
    }
    index_path = package / "processed-index.json"
    index_path.write_text(json.dumps(index), encoding="utf-8")
    mapping_path = tmp_path / "mapping.json"
    mapping_path.write_text(json.dumps({
        "schema_version": 2, "mapping_identity": "fixture-map", "skeleton": "FixtureRig",
        "root": "Root", "hips": "Hips", "feet": {
            "left": {"foot_bone": "DeclaredRightFoot", "shin_bone": "DeclaredRightShin"},
            "right": {"foot_bone": "DeclaredLeftFoot", "shin_bone": "DeclaredLeftShin"},
        },
    }), encoding="utf-8")
    policy_path = ROOT / "tools/kimodo-pipeline/config/phase_contact_policy_v3.json"
    return index, index_path, blend, mapping_path, load_mapping(mapping_path), policy_path, load_policy(policy_path)


def test_cyclic_walk_uses_two_declared_anatomical_release_paths() -> None:
    entry = phase_entry("wf", [("Left", 6, None), ("Right", 0, None)])
    paths = resolve_phase_paths(entry, policy())
    assert [(item.foot, item.classification, item.other_anchor_frame) for item in paths] == [
        ("left", "required", 0), ("right", "required", 6),
    ]
    left, _ = refine_phase_path(bone_samples(stable_ranges=((0, 4),)), paths[0], CONFIG)
    right, _ = refine_phase_path(bone_samples(stable_ranges=((5, 9),)), paths[1], CONFIG)
    assert left["outcome"] == right["outcome"] == "emitted"
    assert left["release_sample"] == 5
    assert right["release_sample"] in {9, 0}
    assert left["acquire_sample"] is not None
    assert left["lifecycle_coverage"] == "complete_cyclic"
    assert right["acquire_sample"] is not None


def test_cyclic_support_cluster_wraps_and_emits_two_runtime_support_intervals() -> None:
    swing_x = {2: 0.1, 3: 0.2, 4: 0.3, 5: 0.3, 6: 0.2, 7: 0.1}
    samples = [
        replace(
            sample,
            foot_position=(swing_x.get(sample.frame, 0.0), 0.0, 0.0 if sample.frame in {0, 1, 8, 9, 10} else 0.2),
            hips_position=(0.0, 0.0, 1.0), shin_position=(0.0, 0.0, 0.5),
            opposite_foot_position=(0.0, 0.0, 0.0), root_position=(0.0, 0.0, 0.0),
        )
        for sample in bone_samples(stable_ranges=((0, 1), (8, 10)))
    ]
    wrap_path = next(
        item for item in resolve_phase_paths(
            phase_entry("xr_l", [("Left", 2, None)]), policy(),
        ) if item.foot == "left"
    )
    row, lifecycle = refine_phase_path(samples, wrap_path, CONFIG)
    supports = [item for item in lifecycle if item["lifecycle"] == "support"]
    assert row["outcome"] == "emitted"
    assert row["acquire_sample"] == 9
    assert row["support_samples"] == [[9, 9], [0, 1]]
    assert row["release_sample"] == 2
    assert len(supports) == 2
    assert supports[0]["timing"][1] == 1.0
    assert supports[1]["timing"][0] == 0.0


@pytest.mark.parametrize(("role", "anchor", "boundary", "stable", "release", "derivation_name"), [
    ("f2i_l", 10, "start", ((4, 10),), None, "temporal_reversal"),
    ("i2f_r", 10, "end", ((0, 9),), 9, "native"),
])
def test_finite_boundary_release_refines_only_event_authorised_foot(
    role: str, anchor: int, boundary: str, stable: tuple[tuple[int, int], ...], release: int,
    derivation_name: str,
) -> None:
    side = "Left" if role.endswith("_l") else "Right"
    paths = resolve_phase_paths(
        phase_entry(role, [(side, anchor, boundary)], derivation_name=derivation_name), policy(),
    )
    authorised = next(item for item in paths if item.anchor_frame is not None)
    opposite = next(item for item in paths if item.anchor_frame is None)
    row, lifecycle = refine_phase_path(bone_samples(stable_ranges=stable), authorised, CONFIG)
    fallback, fallback_lifecycle = refine_phase_path(bone_samples(), opposite, CONFIG)
    assert row["release_sample"] == release
    if boundary == "start":
        assert {item["lifecycle"] for item in lifecycle} == {"acquire", "support"}
        assert row["acquire_sample"] is not None
        assert row["lifecycle_coverage"] == "release_outside_clip_or_phase_window"
    else:
        assert {item["lifecycle"] for item in lifecycle} == {"support", "release"}
        assert row["acquire_sample"] is None
        assert row["lifecycle_coverage"] == "acquire_outside_clip_or_phase_window"
    assert fallback["outcome"] == "fallback" and not fallback_lifecycle


def test_one_sided_roles_and_inapplicable_roles_obey_family_coverage() -> None:
    strafe = resolve_phase_paths(phase_entry("xr_l", [("Left", 0, None)]), policy())
    arc = resolve_phase_paths(phase_entry("ar_r", [("Right", 0, None)]), policy())
    confined = resolve_phase_paths(phase_entry("pr45", []), policy())
    assert [(item.foot, item.classification) for item in strafe] == [("left", "required"), ("right", "fallback_only")]
    assert [(item.foot, item.classification) for item in arc] == [("left", "fallback_only"), ("right", "optional")]
    assert all(item.classification == "fallback_only" for item in confined)
    assert all(refine_phase_path([], item, CONFIG)[0]["outcome"] == "fallback" for item in confined)


def test_semantic_only_bone_only_disagreement_and_missing_samples_refuse() -> None:
    semantic_only, _ = refine_phase_path(bone_samples(stable_ranges=((0, 10),)), path(), CONFIG)
    disagreement, _ = refine_phase_path(bone_samples(stable_ranges=()), path(), CONFIG)
    missing, _ = refine_phase_path([], path(), CONFIG)
    assert "separability" in semantic_only["refusal_reason"]
    assert "separability" in disagreement["refusal_reason"]
    assert "insufficient" in missing["refusal_reason"]


def test_separate_competing_transition_basins_refuse_and_retain_alternatives() -> None:
    ambiguous_path = PhasePath(
        "wf", "fixture", "cyclic_two_release", "left", "required",
        100, None, .95, 0, 100, 0, effective_phase_seed_frames=tuple(range(101)),
    )
    row, lifecycle = refine_phase_path(
        bone_samples(end=100, stable_ranges=((80, 84), (87, 91))), ambiguous_path,
        replace(CONFIG, minimum_ambiguity_margin=0.1),
    )
    assert not lifecycle
    assert "ambiguous" in row["refusal_reason"]
    assert row["rejected_alternatives"]


def test_neighbouring_transition_samples_form_one_basin() -> None:
    entry = phase_entry("xr_l", [("Left", 0, None)], end=40, repair_width=11)
    authorised = next(item for item in resolve_phase_paths(entry, policy()) if item.foot == "left")
    row, lifecycle = refine_phase_path(
        bone_samples(end=40, stable_ranges=((25, 40),)), authorised, CONFIG,
    )
    assert row["outcome"] == "emitted"
    assert lifecycle
    assert row["candidate_alternatives"]
    assert all(
        alternative["release_sample"] == row["release_sample"]
        or min(
            abs(alternative["release_sample"] - row["release_sample"]),
            40 - abs(alternative["release_sample"] - row["release_sample"]),
        ) <= 4
        for alternative in row["candidate_alternatives"][:3]
    )
    assert row["uncertainty_samples"] >= 1


@pytest.mark.parametrize("offset", [0.1, -0.05])
def test_constant_hover_and_penetration_offsets_preserve_boundaries(offset: float) -> None:
    baseline, _ = refine_phase_path(bone_samples(stable_ranges=((0, 3),)), path(), CONFIG)
    shifted, _ = refine_phase_path(bone_samples(stable_ranges=((0, 3),), offset=offset), path(), CONFIG)
    assert shifted["support_samples"] == baseline["support_samples"]
    assert shifted["release_sample"] == baseline["release_sample"]
    assert shifted["baseline_offset_metres"] == pytest.approx(baseline["baseline_offset_metres"] + offset)


@pytest.mark.parametrize("lift", [0.05, 0.15])
def test_normal_lift_after_release_refines_transition_without_offset_refusal(lift: float) -> None:
    row, lifecycle = refine_phase_path(bone_samples(stable_ranges=((0, 3),), lift=lift), path(), CONFIG)
    assert lifecycle
    assert row["outcome"] == "emitted"
    assert row["release_sample"] == 3
    assert row["baseline_drift_metres"] == pytest.approx(0.0)


def test_time_varying_vertical_distortion_that_preserves_separation_still_emits() -> None:
    samples = bone_samples(stable_ranges=((0, 3),))
    support_heights = (0.0, 0.013, 0.026, 0.039)
    samples = [
        replace(sample, foot_position=(sample.foot_position[0], sample.foot_position[1], support_heights[sample.frame]))
        if sample.frame < len(support_heights) else sample
        for sample in samples
    ]
    row, lifecycle = refine_phase_path(samples, path(), CONFIG)
    assert lifecycle
    assert row["outcome"] == "emitted"
    assert row["baseline_drift_metres"] > 0.0
    assert row["absolute_slide_diagnostics"]["affects_eligibility"] is False


def test_flat_and_sliding_false_contacts_refuse() -> None:
    flat, flat_lifecycle = refine_phase_path(bone_samples(stable_ranges=((0, 10),)), path(), CONFIG)
    sliding, sliding_lifecycle = refine_phase_path(bone_samples(stable_ranges=(), lift=0.0), path(), CONFIG)
    assert not flat_lifecycle and "separability" in flat["refusal_reason"]
    assert not sliding_lifecycle and "separability" in sliding["refusal_reason"]


def test_sliding_support_retains_lifecycle_with_poorer_absolute_quality() -> None:
    clean, _ = refine_phase_path(bone_samples(stable_ranges=((0, 4),)), path(), CONFIG)
    sliding, lifecycle = refine_phase_path(
        bone_samples(stable_ranges=((0, 4),), support_slide=0.015, swing_step=0.08), path(), CONFIG,
    )
    assert lifecycle
    assert sliding["support_samples"] == clean["support_samples"]
    assert sliding["release_sample"] == clean["release_sample"]
    assert sliding["absolute_slide_diagnostics"]["support_path_length_metres"] > 0.0
    assert sliding["uncertainty_samples"] >= clean["uncertainty_samples"]


def test_phase_geometry_order_contradiction_refuses_specifically() -> None:
    contradicted = path(anchor=10, boundary="end")
    row, lifecycle = refine_phase_path(bone_samples(stable_ranges=((0, 3),)), contradicted, CONFIG)
    assert not lifecycle
    assert "ordering contradiction" in row["refusal_reason"]


def test_time_varying_distortion_that_destroys_separation_refuses() -> None:
    samples = bone_samples(stable_ranges=((0, 4),))
    distorted = [
        replace(sample, foot_position=(frame * 0.08, 0.0, (frame % 2) * 0.08))
        for frame, sample in enumerate(samples)
    ]
    row, lifecycle = refine_phase_path(distorted, path(), CONFIG)
    assert not lifecycle
    assert "separability" in row["refusal_reason"] or "ambiguous" in row["refusal_reason"]


def test_identity_order_and_mirror_permutations_do_not_change_phase_authority() -> None:
    first = phase_entry("xr_l", [("Left", 0, None)], identity="identity-z", action="action-b")
    second = phase_entry("ar_r", [("Right", 0, None)], identity="identity-a", action="action-a")
    forward = [resolve_phase_paths(item, policy()) for item in (first, second)]
    reverse = [resolve_phase_paths(item, policy()) for item in (second, first)]
    summarise = lambda groups: sorted((path.role, path.foot, path.classification, path.anchor_frame) for group in groups for path in group)
    assert summarise(forward) == summarise(reverse)
    assert next(path for path in forward[0] if path.classification == "required").foot == "left"


def test_missing_context_bones_fail_closed() -> None:
    mapping = {
        "root": "Root", "hips": "Hips", "feet": {
            "left": {"foot_bone": "foot_l", "shin_bone": "shin_l"},
            "right": {"foot_bone": "foot_r", "shin_bone": "shin_r"},
        },
    }
    with pytest.raises(ContactEvidenceError, match="shin_r"):
        validate_available_bones(mapping, {"Root", "Hips", "foot_l", "shin_l", "foot_r"})


def test_required_refusal_blocks_eligibility_but_optional_and_fallback_do_not(tmp_path: Path) -> None:
    entries = [phase_entry("wf", [("Left", 6, None), ("Right", 0, None)])]
    index, index_path, blend, mapping_path, mapping, policy_path, declared_policy = declared_package(tmp_path, entries)
    evidence = build_evidence(
        index, index_path, blend, mapping_path, mapping, policy_path, declared_policy, CONFIG,
        "fixture-package", "fixture-generation",
        {"opaque-identity": {"left": [], "right": []}},
    )
    assert not evidence["source_derived"]["publication_eligible"]
    assert len(evidence["source_derived"]["required_refusals"]) == 2
    assert all(sample["clip"] == sample["identity"] for sample in evidence["samples"])

    entries[0] = phase_entry("ar_l", [("Left", 0, None)])
    index["actions"] = entries
    optional = build_evidence(
        index, index_path, blend, mapping_path, mapping, policy_path, declared_policy, CONFIG,
        "fixture-package", "fixture-generation",
        {"opaque-identity": {"left": [], "right": []}},
    )
    assert optional["source_derived"]["publication_eligible"]
    assert {row["outcome"] for row in optional["source_derived"]["role_contact_matrix"]} == {"refused", "fallback"}


def test_stale_policy_and_mapping_provenance_are_rejected(tmp_path: Path) -> None:
    entries = [phase_entry("pr45", [])]
    index, index_path, blend, mapping_path, mapping, source_policy_path, declared_policy = declared_package(tmp_path, entries)
    policy_path = tmp_path / "policy.json"
    policy_path.write_bytes(source_policy_path.read_bytes())
    evidence = build_evidence(
        index, index_path, blend, mapping_path, mapping, policy_path, declared_policy, CONFIG,
        "fixture-package", "fixture-generation", {"opaque-identity": {}},
    )
    validate_evidence_matches_package(evidence, index_path, blend, mapping_path, policy_path)
    policy_path.write_text(policy_path.read_text() + " ", encoding="utf-8")
    with pytest.raises(ContactEvidenceError, match="stale"):
        validate_evidence_matches_package(evidence, index_path, blend, mapping_path, policy_path)
    policy_path.write_bytes(source_policy_path.read_bytes())
    mapping_path.write_text(mapping_path.read_text() + " ", encoding="utf-8")
    with pytest.raises(ContactEvidenceError, match="stale"):
        validate_evidence_matches_package(evidence, index_path, blend, mapping_path, policy_path)


def test_stale_configuration_provenance_is_rejected(tmp_path: Path) -> None:
    entries = [phase_entry("pr45", [])]
    index, index_path, blend, mapping_path, mapping, policy_path, declared_policy = declared_package(tmp_path, entries)
    evidence = build_evidence(
        index, index_path, blend, mapping_path, mapping, policy_path, declared_policy, CONFIG,
        "fixture-package", "fixture-generation", {"opaque-identity": {}},
    )
    evidence["source_derived"]["configuration"]["minimum_confidence"] = 0.1
    with pytest.raises(ContactEvidenceError, match="configuration provenance"):
        validate_evidence_matches_package(evidence, index_path, blend, mapping_path, policy_path)


def test_method_and_confidence_threshold_are_versioned_and_fixed() -> None:
    assert METHOD == "phase_seeded_relative_motion_segmentation_v2"
    assert CONFIG.minimum_confidence == 0.8
    assert sum((
        CONFIG.semantic_weight, CONFIG.cohesion_weight, CONFIG.separation_weight,
        CONFIG.transition_weight, CONFIG.context_weight, CONFIG.ambiguity_weight,
    )) == pytest.approx(1.0)
    assert all(family["match"]["kind"] == "exact" for family in policy()["families"])
    finite = {
        role: family["contact_polarity"]
        for family in policy()["families"]
        for role in family["match"]["values"]
        if role.startswith(("i2f", "i2b", "f2i", "b2i"))
    }
    assert finite == {
        "i2f_l": "Release", "i2f_r": "Release", "i2b_l": "Release", "i2b_r": "Release",
        "f2i_l": "Acquire", "f2i_r": "Acquire", "b2i_l": "Acquire", "b2i_r": "Acquire",
    }


def test_one_release_cycle_models_tail_support_before_opening_swing() -> None:
    entry = phase_entry("xr_l", [("Left", 0, None)], end=40, repair_width=11)
    authorised = next(item for item in resolve_phase_paths(entry, policy()) if item.foot == "left")
    row, lifecycle = refine_phase_path(
        bone_samples(end=40, stable_ranges=((25, 40),)), authorised, CONFIG,
    )
    assert row["outcome"] == "emitted"
    assert row["release_sample"] in {0, 39}
    assert row["support_samples"][0] >= 20
    assert row["cyclic_search_order"][0] == row["release_sample"]
    assert {item["lifecycle"] for item in lifecycle} == {"acquire", "support", "release"}


def test_duplicate_conceptual_endpoint_is_removed_before_all_signals() -> None:
    entry = phase_entry("xr_l", [("Left", 0, None)], end=40, repair_width=11)
    authorised = next(item for item in resolve_phase_paths(entry, policy()) if item.foot == "left")
    samples = bone_samples(end=40, stable_ranges=((25, 40),))
    baseline, _ = refine_phase_path(samples, authorised, CONFIG)
    poisoned_endpoint = replace(
        samples[-1], foot_position=(999.0, -999.0, 999.0),
        hips_position=(-999.0, 999.0, -999.0),
    )
    changed, _ = refine_phase_path([*samples[:-1], poisoned_endpoint], authorised, CONFIG)
    assert changed["release_sample"] == baseline["release_sample"]
    assert changed["support_samples"] == baseline["support_samples"]
    assert changed["confidence"] == pytest.approx(baseline["confidence"])
    assert changed["sample_topology"] == {
        "conceptual_sample_count": 41,
        "unique_runtime_sample_count": 40,
        "duplicate_endpoint_canonicalised": True,
    }


@pytest.mark.parametrize("width", [11, 12, 14, 15, 16])
def test_phase_seed_provenance_retains_exact_repair_supports(width: int) -> None:
    end = 2 * width + 12
    entry = phase_entry("xr_l", [("Left", 0, None)], end=end, repair_width=width)
    authorised = next(item for item in resolve_phase_paths(entry, policy()) if item.foot == "left")
    provenance = authorised.phase_seed_provenance
    assert provenance is not None
    repair = provenance["repair_support"]
    assert repair["opening_support"] == {"window": [0, width - 1], "width": width}
    assert repair["closing_support"] == {"window": [end + 1 - width, end], "width": width}
    unique = end
    raw = set(range(width)) | {offset % unique for offset in range(end + 1 - width, end + 1)}
    expected = {((offset + delta) % unique) for offset in raw for delta in range(-4, 5)}
    assert set(repair["repair_support_dilated_frames"]) == expected


@pytest.mark.parametrize(("derivation_name", "output_side", "kind"), [
    ("native", "Left", "Release"),
    ("mirror", "Right", "Release"),
    ("temporal_reversal", "Left", "Acquire"),
    ("reverse_mirror", "Right", "Acquire"),
])
def test_mirror_reverse_and_combined_derivations_preserve_physical_semantics(
    derivation_name: str, output_side: str, kind: str,
) -> None:
    role = "xr_r" if output_side == "Right" else "xr_l"
    entry = phase_entry(
        role, [(output_side, 0, None)], end=40,
        derivation_name=derivation_name, repair_width=11,
    )
    authorised = next(item for item in resolve_phase_paths(entry, policy()) if item.anchor_frame is not None)
    assert authorised.foot == output_side.lower()
    assert authorised.event_kind == kind
    assert authorised.event_id == f"{kind.lower()}_{output_side[0].lower()}"


def test_reverse_swaps_finite_boundary_and_release_to_acquire() -> None:
    entry = phase_entry(
        "f2i_l", [("Left", 0, "start")], end=10,
        derivation_name="temporal_reversal",
    )
    authorised = next(item for item in resolve_phase_paths(entry, policy()) if item.anchor_frame is not None)
    assert authorised.event_kind == "Acquire"
    assert authorised.boundary_context == "start"


def test_cyclic_acquire_anchor_preserves_acquire_then_support_order() -> None:
    entry = phase_entry(
        "xr_l", [("Left", 0, None)], derivation_name="temporal_reversal",
    )
    authorised = next(item for item in resolve_phase_paths(entry, policy()) if item.anchor_frame is not None)
    row, lifecycle = refine_phase_path(
        bone_samples(stable_ranges=((0, 4),)), authorised, CONFIG,
    )
    assert row["outcome"] == "emitted"
    assert row["event_kind"] == "Acquire"
    assert row["acquire_sample"] in {0, 1}
    assert row["release_sample"] > row["acquire_sample"]
    assert {item["lifecycle"] for item in lifecycle} == {"acquire", "support", "release"}


def test_source_only_legacy_event_is_refused_as_stale() -> None:
    entry = phase_entry("xr_l", [("Left", 0, None)])
    entry.pop("post_repair_event_evidence")
    with pytest.raises(ContactEvidenceError, match="post-repair event provenance"):
        resolve_phase_paths(entry, policy())


def test_action_identity_event_order_and_action_count_do_not_select_anatomy() -> None:
    entry = phase_entry(
        "wf", [("Right", 0, None), ("Left", 6, None)],
        identity="unrelated-z", action="unrelated-a",
    )
    entry["source_directional_event_evidence"]["events"].reverse()
    paths = resolve_phase_paths(entry, policy())
    assert {(item.foot, item.event_id) for item in paths} == {
        ("left", "release_l"), ("right", "release_r"),
    }


def test_one_release_half_cycle_sector_crosses_runtime_wrap_deterministically() -> None:
    entry = phase_entry("xr_l", [("Left", 0, None)], end=40, repair_width=11)
    authorised = next(item for item in resolve_phase_paths(entry, policy()) if item.foot == "left")
    permitted = set(authorised.effective_phase_seed_frames)
    assert {0, 1, 2, 38, 39} <= permitted
    assert 20 not in permitted


def test_contradictory_xl_r_like_seed_and_geometric_basin_remains_refused() -> None:
    entry = phase_entry("xl_r", [("Right", 0, None)], end=60, repair_width=11)
    authorised = next(item for item in resolve_phase_paths(entry, policy()) if item.foot == "right")
    samples = bone_samples(end=60, stable_ranges=((10, 29),))
    row, lifecycle = refine_phase_path(samples, authorised, CONFIG)
    assert not lifecycle
    assert "outside the topology-derived phase sector" in row["refusal_reason"]


def test_two_release_events_use_cyclic_voronoi_sectors() -> None:
    paths = resolve_phase_paths(
        phase_entry("wf", [("Left", 2, None), ("Right", 8, None)]), policy(),
    )
    left = next(item for item in paths if item.foot == "left")
    right = next(item for item in paths if item.foot == "right")
    assert set(left.effective_phase_seed_frames) == {0, 1, 2, 3, 4, 5}
    assert set(right.effective_phase_seed_frames) == {0, 5, 6, 7, 8, 9}


def test_clear_motion_transition_separated_from_seed_within_sector_succeeds() -> None:
    entry = phase_entry("xr_l", [("Left", 5, None)], end=60, repair_width=11)
    authorised = next(item for item in resolve_phase_paths(entry, policy()) if item.foot == "left")
    row, lifecycle = refine_phase_path(
        bone_samples(
            end=60, stable_ranges=((40, 60), (0, 9)), swing_step=0.2, lift=0.2,
        ), authorised, CONFIG,
    )
    assert row["outcome"] == "emitted"
    assert row["release_sample"] in authorised.effective_phase_seed_frames
    assert lifecycle


def test_seed_ranks_only_within_the_best_physical_transition_basin() -> None:
    entry = phase_entry("xl_r", [("Right", 26, None)], end=45, repair_width=11)
    authorised = next(item for item in resolve_phase_paths(entry, policy()) if item.foot == "right")
    row, lifecycle = refine_phase_path(
        bone_samples(
            end=45, stable_ranges=((0, 16), (31, 45)), swing_step=0.2, lift=0.2,
        ), authorised, CONFIG,
    )
    assert row["outcome"] == "emitted"
    assert row["release_sample"] == 17
    assert row["acquire_sample"] == 32
    assert row["refined_boundary_delta_frames"] == -9
    assert lifecycle[0]["lifecycle"] == "acquire"
    assert lifecycle[-1]["lifecycle"] == "release"
    assert all(item["lifecycle"] == "support" for item in lifecycle[1:-1])
    # Physical-first selection may move the declared seed boundary materially;
    # the resulting support lifecycle must still retain its fixed cyclic order.
    assert row["release_sample"] != authorised.anchor_frame


@pytest.mark.parametrize(("role", "side"), [("xr_l", "Left"), ("xr_r", "Right")])
def test_one_release_mirror_pairs_choose_the_same_physical_basin(role: str, side: str) -> None:
    authorised = next(
        item for item in resolve_phase_paths(
            phase_entry(role, [(side, 26, None)], end=45, repair_width=11), policy(),
        ) if item.anchor_frame is not None
    )
    samples = bone_samples(end=45, stable_ranges=((0, 16), (31, 45)), swing_step=0.2, lift=0.2)
    if side == "Right":
        samples = [replace(
            sample,
            foot_position=(-sample.foot_position[0], sample.foot_position[1], sample.foot_position[2]),
            hips_position=(-sample.hips_position[0], sample.hips_position[1], sample.hips_position[2]),
            shin_position=(-sample.shin_position[0], sample.shin_position[1], sample.shin_position[2]),
            opposite_foot_position=(-sample.opposite_foot_position[0], sample.opposite_foot_position[1], sample.opposite_foot_position[2]),
            root_position=(-sample.root_position[0], sample.root_position[1], sample.root_position[2]),
        ) for sample in samples]
    row, lifecycle = refine_phase_path(samples, authorised, CONFIG)
    assert row["outcome"] == "emitted"
    assert (row["release_sample"], row["acquire_sample"]) == (17, 32)
    assert lifecycle[0]["lifecycle"] == "acquire"
    assert lifecycle[-1]["lifecycle"] == "release"
    assert all(item["lifecycle"] == "support" for item in lifecycle[1:-1])


def test_cyclic_reindexing_preserves_the_selected_transition_basin() -> None:
    entry = phase_entry("xr_l", [("Left", 5, None)], end=40, repair_width=11)
    authorised = next(item for item in resolve_phase_paths(entry, policy()) if item.anchor_frame is not None)
    original = bone_samples(end=40, stable_ranges=((25, 40), (0, 4)), swing_step=0.2, lift=0.2)
    baseline, _ = refine_phase_path(original, authorised, CONFIG)
    assert baseline["outcome"] == "emitted"

    shift = 9
    unique = original[:-1]
    rotated_unique = [replace(
        unique[(index - shift) % len(unique)], frame=index, time_seconds=index / 10.0,
    ) for index in range(len(unique))]
    rotated = [*rotated_unique, replace(rotated_unique[0], frame=40, time_seconds=4.0)]
    anchor = authorised.anchor_frame
    assert anchor is not None
    rotated_path = replace(
        authorised,
        anchor_frame=(anchor + shift) % 40,
        effective_phase_seed_frames=tuple(sorted((frame + shift) % 40 for frame in authorised.effective_phase_seed_frames)),
    )
    changed, _ = refine_phase_path(rotated, rotated_path, CONFIG)
    assert changed["outcome"] == "emitted"
    assert changed["release_sample"] == (baseline["release_sample"] + shift) % 40
    assert changed["acquire_sample"] == (baseline["acquire_sample"] + shift) % 40
    assert changed["confidence"] == pytest.approx(baseline["confidence"])


def test_wrong_polarity_basin_is_refused_even_when_physically_strong() -> None:
    authorised = next(
        item for item in resolve_phase_paths(
            phase_entry("xr_l", [("Left", 16, None)], end=40, repair_width=11), policy(),
        ) if item.anchor_frame is not None
    )
    wrong_polarity = replace(authorised, event_kind="Acquire", event_id="acquire_l")
    row, lifecycle = refine_phase_path(
        bone_samples(
            end=40, stable_ranges=((0, 15), (30, 40)), swing_step=0.2, lift=0.2,
        ), wrong_polarity, CONFIG,
    )
    assert not lifecycle
    assert "outside the topology-derived phase sector" in row["refusal_reason"]


def test_declared_action_name_identity_and_event_order_do_not_change_refinement() -> None:
    entries = [
        phase_entry("wf", [("Left", 6, None), ("Right", 0, None)], identity="carrier-a", action="z-action"),
        phase_entry("wf", [("Right", 0, None), ("Left", 6, None)], identity="carrier-z", action="a-action"),
    ]
    outcomes = []
    for entry in entries:
        entry["source_directional_event_evidence"]["events"].reverse()
        paths = resolve_phase_paths(entry, policy())
        rows = {}
        for phase_path in paths:
            stable = ((0, 4),) if phase_path.foot == "left" else ((5, 9),)
            row, lifecycle = refine_phase_path(bone_samples(stable_ranges=stable), phase_path, CONFIG)
            rows[phase_path.foot] = (
                row["outcome"], row["acquire_sample"], row["release_sample"],
                tuple(item["lifecycle"] for item in lifecycle),
            )
        outcomes.append(rows)
    assert outcomes[0] == outcomes[1]


@pytest.mark.parametrize(("role", "boundary", "kind", "polarity", "stable", "outside"), [
    ("i2f_l", "start", "Release", "Release", ((0, 3),), "acquire"),
    ("i2b_l", "end", "Acquire", "Release", ((0, 6),), "acquire"),
    ("f2i_l", "start", "Release", "Acquire", ((4, 10),), "release"),
    ("b2i_l", "end", "Acquire", "Acquire", ((7, 10),), "release"),
])
def test_all_finite_boundary_event_quadrants_emit_explicit_partial_lifecycles(
    role: str, boundary: str, kind: str, polarity: str, stable: tuple[tuple[int, int], ...], outside: str,
) -> None:
    anchor = 0 if boundary == "start" else 10
    finite_path = PhasePath(
        role, "fixture", "finite_boundary_release", "left", "required",
        anchor, boundary, .99, 0, 10, event_kind=kind, event_id=f"{kind.lower()}_l",
        effective_phase_seed_frames=tuple(range(max(0, anchor - 4), min(10, anchor + 4) + 1)),
        contact_polarity=polarity,
    )
    row, lifecycle = refine_phase_path(bone_samples(stable_ranges=stable), finite_path, CONFIG)
    assert row["outcome"] == "emitted", row
    assert row[f"{outside}_sample"] is None
    assert {item["lifecycle"] for item in lifecycle} == (
        {"support", "release"} if polarity == "Release" else {"acquire", "support"}
    )


def test_finite_transition_without_declared_contact_polarity_refuses() -> None:
    row, lifecycle = refine_phase_path(
        bone_samples(stable_ranges=((0, 3),)),
        replace(path(), contact_polarity=None),
        CONFIG,
    )
    assert lifecycle == []
    assert row["refusal_reason"] == "finite transition lacks declared authoritative contact polarity"


def test_travel_to_idle_release_seed_uses_role_authoritative_acquire() -> None:
    authorised = next(
        item for item in resolve_phase_paths(
            phase_entry("f2i_l", [("Left", 0, "start")]), policy(),
        ) if item.anchor_frame is not None
    )
    row, lifecycle = refine_phase_path(bone_samples(stable_ranges=((4, 10),)), authorised, CONFIG)
    assert authorised.event_kind == "Release"
    assert authorised.contact_polarity == "Acquire"
    assert row["outcome"] == "emitted"
    assert row["acquire_sample"] is not None and row["release_sample"] is None
    assert {item["lifecycle"] for item in lifecycle} == {"acquire", "support"}


def test_smooth_finite_release_ramp_and_reversed_acquire_are_symmetric() -> None:
    samples = bone_samples(stable_ranges=((0, 4),), lift=0.0, swing_step=0.04)
    ramp = (0.0, 0.0, 0.01, 0.03, 0.06, 0.10, 0.15, 0.21, 0.28, 0.36, 0.45)
    samples = [replace(sample, foot_position=(sample.foot_position[0], 0.0, ramp[index])) for index, sample in enumerate(samples)]
    release_path = replace(path(anchor=6), contact_polarity="Release", effective_phase_seed_frames=tuple(range(2, 11)))
    assert release_path.anchor_frame is not None
    release, release_lifecycle = refine_phase_path(samples, release_path, CONFIG)
    reversed_samples = [
        replace(sample, frame=index, time_seconds=index / 10.0)
        for index, sample in enumerate(reversed(samples))
    ]
    acquire_path = replace(
        release_path, role="f2i_l", anchor_frame=10 - release_path.anchor_frame,
        boundary_context="start", contact_polarity="Acquire",
        effective_phase_seed_frames=tuple(range(0, 9)),
    )
    acquire, acquire_lifecycle = refine_phase_path(reversed_samples, acquire_path, CONFIG)
    assert release["outcome"] == acquire["outcome"] == "emitted"
    assert release["confidence_components"]["transition_persistence_sharpness"] == pytest.approx(
        acquire["confidence_components"]["transition_persistence_sharpness"], abs=1e-9,
    )
    assert {item["lifecycle"] for item in release_lifecycle} == {"support", "release"}
    assert {item["lifecycle"] for item in acquire_lifecycle} == {"acquire", "support"}


def test_relative_motion_refinement_is_similarity_mirror_and_reindex_invariant() -> None:
    samples = bone_samples(stable_ranges=((0, 4),))
    finite_path = replace(path(anchor=5), effective_phase_seed_frames=tuple(range(1, 10)))
    baseline, _ = refine_phase_path(samples, finite_path, CONFIG)

    def transform_point(point: tuple[float, float, float], frame: int) -> tuple[float, float, float]:
        translated = (point[0] + 2.0, -point[1] - 3.0, point[2] + 4.0)
        yawed = (-translated[1], translated[0], translated[2])
        return 3.0 * yawed[0], 3.0 * yawed[1], 3.0 * yawed[2]

    transformed = [replace(
        sample, frame=sample.frame + 20, time_seconds=sample.time_seconds + 2.0,
        foot_position=transform_point(sample.foot_position, sample.frame),
        hips_position=transform_point(sample.hips_position, sample.frame),
        shin_position=transform_point(sample.shin_position, sample.frame),
        opposite_foot_position=transform_point(sample.opposite_foot_position, sample.frame),
        root_position=transform_point(sample.root_position, sample.frame),
    ) for sample in samples]
    transformed_path = replace(
        finite_path, foot="right", anchor_frame=25, frame_start=20, frame_end=30,
        effective_phase_seed_frames=tuple(range(21, 30)),
    )
    changed, _ = refine_phase_path(transformed, transformed_path, CONFIG)
    assert changed["outcome"] == baseline["outcome"]
    assert changed["release_sample"] - 20 == baseline["release_sample"]
    assert changed["confidence"] == pytest.approx(baseline["confidence"])


def test_increased_world_slide_changes_diagnostics_not_contact_eligibility() -> None:
    samples = bone_samples(stable_ranges=((0, 4),))
    finite_path = replace(path(anchor=5), effective_phase_seed_frames=tuple(range(1, 10)))
    baseline, _ = refine_phase_path(samples, finite_path, CONFIG)

    def slide(point: tuple[float, float, float], frame: int) -> tuple[float, float, float]:
        return point[0] + frame * 0.03, point[1], point[2]

    sliding = [replace(
        sample, foot_position=slide(sample.foot_position, sample.frame),
        hips_position=slide(sample.hips_position, sample.frame),
        shin_position=slide(sample.shin_position, sample.frame),
        opposite_foot_position=slide(sample.opposite_foot_position, sample.frame),
        root_position=slide(sample.root_position, sample.frame),
    ) for sample in samples]
    changed, _ = refine_phase_path(sliding, finite_path, CONFIG)
    assert changed["outcome"] == baseline["outcome"] == "emitted"
    assert changed["support_samples"] == baseline["support_samples"]
    assert changed["confidence"] == pytest.approx(baseline["confidence"])
    assert changed["absolute_slide_diagnostics"]["support_path_length_metres"] > baseline["absolute_slide_diagnostics"]["support_path_length_metres"]
    assert changed["absolute_slide_diagnostics"]["affects_eligibility"] is False
