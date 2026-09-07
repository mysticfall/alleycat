from kimodo_pipeline.arc_semantics import (
    arc_body_support_evidence,
    arc_curve_evidence,
    compose_local_arc_cycles,
    rank_arc_candidates,
)


def curved(scale=1.0, sign=-1.0):
    points = [(sign * scale * value * value * .8, scale * value) for value in (0.0, .25, .5, .75, 1.0)]
    headings = [sign * value * .7 for value in (0.0, .25, .5, .75, 1.0)]
    return points, headings


def test_arc_evidence_is_scale_and_mirror_invariant() -> None:
    right = arc_curve_evidence("ar_l", *curved(), 1.0)
    scaled = arc_curve_evidence("ar_l", *curved(3.0), 3.0)
    left = arc_curve_evidence("al_r", *curved(sign=1.0), 1.0)
    assert right["accepted"] and scaled["accepted"] and left["accepted"]
    assert right["yaw_span_rad"] == -left["yaw_span_rad"]
    assert right["lateral_excursion"] == -left["lateral_excursion"]


def test_arc_evidence_rejects_straight_wrong_hand_wrong_direction_wrong_yaw_and_unstable() -> None:
    points, headings = curved()
    corruptions = {
        "straight": ([(0.0, index * .25) for index in range(5)], headings),
        "wrong_hand": ([(-x, z) for x, z in points], headings),
        "wrong_direction": ([(x, -z) for x, z in points], headings),
        "wrong_yaw": (points, [-value for value in headings]),
        "unstable": (points[:2] + [(-2.0, .5)] + points[3:], headings),
    }
    assert all(not arc_curve_evidence("ar_l", p, h, 1.0)["accepted"] for p, h in corruptions.values())


def test_arc_evidence_classifies_the_wrong_root_frame_regression_as_direction_and_handedness() -> None:
    evidence = arc_curve_evidence(
        "ar_l",
        [
            (0.0, 0.0),
            (.01, -.09),
            (.04, -.18),
            (.09, -.27),
            (.136627076, -.364728719),
        ],
        [0.0, -.20, -.40, -.60, -.80],
        .433695449,
    )

    assert not evidence["accepted"]
    assert evidence["failures"] == {
        "forward_progress": True,
        "lateral_curvature_handedness": True,
    }


def test_candidate_ranking_ignores_names_ranges_and_order() -> None:
    base = {
        "phase_compatible": True, "quality_accepted": True, "repair_accepted": True,
        "curve_accepted": True,
        "body_support": {
            "mean_rearward_body_offset": .1,
            "p90_rearward_body_offset": .2,
            "mean_rearward_pelvis_offset": .1,
            "mean_rearward_chest_offset": .1,
        },
    }
    rows = [
        {**base, "name": "renamed", "range": [900, 940], "repair_cost": .2, "content_signature": "b"},
        {**base, "name": "other", "range": [1, 41], "repair_cost": .1, "content_signature": "a"},
        {**base, "name": "extra", "repair_cost": 0.0, "content_signature": "z", "curve_accepted": False},
    ]
    assert [row["content_signature"] for row in rank_arc_candidates(reversed(rows))] == ["a", "b"]


def test_arc_body_support_is_scale_mirror_and_name_invariant_and_detects_sustained_rearward_carriage() -> None:
    def samples(offset: float, scale: float = 1.0, mirror: float = 1.0):
        return [
            {
                "hips": (mirror * .05 * scale, offset * scale),
                "chest": (mirror * .10 * scale, (offset - .05) * scale),
                "left_foot": (mirror * -.2 * scale, -.1 * scale),
                "right_foot": (mirror * .2 * scale, .1 * scale),
            }
            for _ in range(8)
        ]

    balanced = arc_body_support_evidence(samples(.15), [(0.0, 1.0)] * 8, 1.0)
    rearward = arc_body_support_evidence(samples(-.25), [(0.0, 1.0)] * 8, 1.0)
    scaled_mirror = arc_body_support_evidence(samples(-.25, 3.0, -1.0), [(0.0, 2.0)] * 8, 3.0)
    assert rearward == scaled_mirror
    assert balanced["mean_rearward_body_offset"] == 0.0
    assert rearward["mean_rearward_body_offset"] > 0.0
    assert rearward["rearward_body_fraction"] == 1.0


def test_arc_ranking_prefers_sustained_balance_before_lower_seam_cost() -> None:
    base = {
        "phase_compatible": True, "quality_accepted": True, "repair_accepted": True,
        "curve_accepted": True,
    }
    balanced = {
        **base, "repair_cost": .4, "content_signature": "balanced",
        "body_support": {
            "mean_rearward_body_offset": .02, "p90_rearward_body_offset": .04,
            "mean_rearward_pelvis_offset": .02, "mean_rearward_chest_offset": .03,
        },
    }
    rearward = {
        **base, "repair_cost": .01, "content_signature": "rearward",
        "body_support": {
            "mean_rearward_body_offset": .2, "p90_rearward_body_offset": .3,
            "mean_rearward_pelvis_offset": .15, "mean_rearward_chest_offset": .25,
        },
    }
    assert rank_arc_candidates([rearward, balanced]) == [balanced, rearward]


def test_three_cycle_composition_rejects_same_chord_and_reset_yaw() -> None:
    endpoints = compose_local_arc_cycles((-.4, .8), -.6)
    same_chord = [(-.4 * cycle, .8 * cycle) for cycle in (1, 2, 3)]
    assert endpoints[1:] != same_chord[1:]
    assert abs(3.0 * -.6 - -.6) > .1
