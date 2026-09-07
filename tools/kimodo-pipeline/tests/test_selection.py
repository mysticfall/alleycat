"""Pure selection tests that do not encode a source variant's content."""
from __future__ import annotations
import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from kimodo_pipeline.selection import Candidate, FootSample, SelectionError, canonical_transition_events, derived_anatomical_side, directional_phase_event_observation, foot_samples, lateral_foot_samples, map_transition_event_to_output_anatomy, natural_cycle_candidate_rank_key, quality_evidence, recurrence_window_evidence, release_event_evidence, release_events, role_window, score_candidate, select_candidate, transition_evidence


class Curve:
    def __init__(self, path: str, values: dict[int, float], key_count: int = 0) -> None:
        self.data_path, self.array_index, self.values = path, 2, values
        self.keyframe_points = [object() for _ in range(key_count)]

    def evaluate(self, frame: int) -> float:
        return self.values[frame]


class SourceWindowTests(unittest.TestCase):
    def test_final_hierarchy_directional_phase_observation_distinguishes_release_from_acquire(self) -> None:
        segment = {"start_frame": 1, "end_frame": 3}
        stationary = {frame: FootSample(0.0, (0.0, 0.0, 0.0)) for frame in range(1, 4)}
        release_geometry = {
            "Left": {
                1: FootSample(-1.0, (0.0, 0.0, 0.01)),
                2: FootSample(1.0, (0.0, 0.0, 0.01)),
                3: FootSample(2.0, (0.0, 0.0, 0.01)),
            },
            "Right": stationary,
        }
        release = directional_phase_event_observation(
            segment, release_geometry, "Left", "wf_l", "Release", 2,
            duplicate_endpoint=False,
        )
        self.assertTrue(release["accepted"], release)

        acquire_geometry = {
            "Left": {
                1: FootSample(2.0, (0.0, 0.0, -0.01)),
                2: FootSample(1.0, (0.0, 0.0, -0.01)),
                3: FootSample(-1.0, (0.0, 0.0, -0.01)),
            },
            "Right": stationary,
        }
        acquire = directional_phase_event_observation(
            segment, acquire_geometry, "Left", "wb_l", "Acquire", 2,
            duplicate_endpoint=False,
        )
        self.assertTrue(acquire["accepted"], acquire)
        self.assertNotEqual(release["measurement"]["declared_travel_direction"], acquire["measurement"]["declared_travel_direction"])

    def test_loop_boundary_observation_uses_duplicate_endpoint_derivative(self) -> None:
        segment = {"start_frame": 1, "end_frame": 4}
        stationary = {frame: FootSample(0.0, (0.0, 0.0, 0.0)) for frame in range(1, 5)}
        geometry = {
            "Left": {
                1: FootSample(0.0, (0.0, 0.0, 0.0), lateral_position=-1.0),
                2: FootSample(0.0, (-0.01, 0.0, 0.0), lateral_position=-2.0),
                3: FootSample(0.0, (-0.01, 0.0, 0.0), lateral_position=1.0),
                4: FootSample(0.0, (-0.01, 0.0, 0.0), lateral_position=-1.0),
            },
            "Right": stationary,
        }

        release = directional_phase_event_observation(
            segment, geometry, "Left", "xr_l", "Release", 1,
            duplicate_endpoint=True,
        )

        self.assertTrue(release["accepted"], release)
        first = release["measurement"]["hysteresis_samples"][0]
        self.assertEqual(first["frame"], 1)
        self.assertEqual(first["evaluated_derivative_frame"], 4)
        self.assertEqual(release["measurement"]["effective_derivative_frames"][0], 4)
        self.assertGreater(release["confidence"], 0.0)

    def test_finite_endpoint_observation_does_not_wrap_into_idle_boundary(self) -> None:
        segment = {"start_frame": 1, "end_frame": 3}
        stationary = {frame: FootSample(0.0, (0.0, 0.0, 0.0)) for frame in range(1, 4)}
        geometry = {
            "Left": stationary,
            "Right": {
                1: FootSample(-2.0, (0.0, 0.0, 0.0)),
                2: FootSample(-1.0, (0.0, 0.0, 0.0)),
                3: FootSample(1.0, (0.0, 0.0, 0.01)),
            },
        }

        release = directional_phase_event_observation(
            segment, geometry, "Right", "i2f_r", "Release", 3,
            duplicate_endpoint=False,
        )

        self.assertTrue(release["accepted"], release)
        self.assertEqual(
            [row["frame"] for row in release["measurement"]["hysteresis_samples"]],
            [3],
        )

    def test_finite_start_boundary_uses_first_available_output_derivative(self) -> None:
        segment = {"start_frame": 1, "end_frame": 3}
        stationary = {frame: FootSample(0.0, (0.0, 0.0, 0.0)) for frame in range(1, 4)}
        geometry = {
            "Left": {
                1: FootSample(1.0, (0.0, 0.0, 0.0)),
                2: FootSample(2.0, (0.0, 0.0, 0.01)),
                3: FootSample(3.0, (0.0, 0.0, 0.01)),
            },
            "Right": stationary,
        }

        release = directional_phase_event_observation(
            segment, geometry, "Left", "f2i_l", "Release", 1,
            duplicate_endpoint=False, boundary_context="start",
        )

        self.assertTrue(release["accepted"], release)
        self.assertTrue(release["measurement"]["boundary_context_supplies_omitted_neighbour"])
        self.assertEqual(
            release["measurement"]["hysteresis_samples"][0]["evaluated_derivative_frame"],
            2,
        )
        self.assertEqual(release["measurement"]["effective_derivative_frames"], [2, 2])
        self.assertGreater(release["confidence"], 0.0)

    def test_finite_end_boundary_accepts_omitted_following_neighbour(self) -> None:
        segment = {"start_frame": 1, "end_frame": 5}
        stationary = {frame: FootSample(0.0, (0.0, 0.0, 0.0)) for frame in range(1, 6)}
        geometry = {
            "Left": stationary,
            "Right": {
                1: FootSample(3.0, (0.0, 0.0, 0.0)),
                2: FootSample(2.0, (0.0, 0.0, -0.01)),
                3: FootSample(1.5, (0.0, 0.0, -0.01)),
                4: FootSample(1.25, (0.0, 0.0, -0.01)),
                5: FootSample(1.0, (0.0, 0.0, -0.01)),
            },
        }

        acquire = directional_phase_event_observation(
            segment, geometry, "Right", "i2b_r", "Acquire", 5,
            duplicate_endpoint=False, boundary_context="end",
        )

        self.assertTrue(acquire["accepted"], acquire)
        self.assertTrue(acquire["measurement"]["boundary_context_supplies_omitted_neighbour"])
        self.assertGreater(acquire["confidence"], 0.0)

    def test_entry_and_exit_are_derived_from_observed_bounds(self) -> None:
        segment = {"index": 0, "start_frame": 10, "end_frame": 42}
        geometry = self._geometry(segment, {15: 2, 24: -1, 34: 2})
        self.assertEqual(role_window(segment, "i2f_r", (), geometry), (10, 15))
        self.assertEqual(role_window(segment, "f2i_r", (), geometry), (34, 42))
        self.assertEqual(role_window(segment, "b2i_r", (), geometry), (24, 42))
        self.assertEqual(role_window(segment, "i2b_r", (), geometry), (10, 24))

    @staticmethod
    def _geometry(segment: dict[str, int], right_changes: dict[int, int]):
        """Build fixed-axis samples; a jump is a decisive releasing velocity."""
        value, right, direction = -1.0, {}, 0.0
        for frame in range(segment["start_frame"], segment["end_frame"] + 1):
            if frame in right_changes:
                value = float(right_changes[frame])
                direction = 0.1 if value > 0.0 else -0.1
            else:
                value += direction
            right[frame] = value
        return foot_samples({"Left": {frame: 0.0 for frame in right}, "Right": right})

    def test_walk_uses_measured_right_leading_centre_line_crossings_for_a_complete_cycle(self) -> None:
        segment = {"index": 0, "start_frame": 1, "end_frame": 8}
        # The right foot becomes leading at frames 5 and 9.  The preceding
        # R-to-L crossings are physically left-leading and must not be used.
        geometry = foot_samples({"Right": {1: -2, 2: -1, 3: 0.5, 4: 1.5, 5: -1.5, 6: -0.5, 7: 0.5, 8: 1.5}, "Left": {frame: 0 for frame in range(1, 9)}})
        self.assertEqual(role_window(segment, "wf_r", (), geometry), (3, 7))

    def test_recurrence_diagnostics_enumerate_adjacent_required_foot_windows_only_as_admissible(self) -> None:
        segment = {"index": 0, "start_frame": 1, "end_frame": 12}
        geometry = foot_samples({
            "Right": {1: -1, 2: -.5, 3: 1, 4: 2, 5: 2, 6: 2, 7: 5, 8: 6, 9: 6, 10: 6, 11: 9, 12: 10},
            "Left": {1: 0, 2: 0, 3: 0, 4: 0, 5: 3, 6: 4, 7: 4, 8: 4, 9: 7, 10: 8, 11: 8, 12: 8},
        })

        rows = recurrence_window_evidence(segment, "wf_r", geometry)

        admissible = [row for row in rows if row["phase_admissible"]]
        diagnostic = [row for row in rows if row["diagnostic_only"]]
        self.assertEqual([row["source_range"] for row in admissible], [[3, 7], [7, 11]])
        self.assertEqual([row["selected_by_current_policy"] for row in admissible], [True, False])
        self.assertEqual([row["source_range"] for row in diagnostic], [[5, 9]])
        self.assertTrue(all(row["source_foot_side"] == "Left" for row in diagnostic))
        self.assertTrue(all("cannot admit wf_r" in row["reason"] for row in diagnostic))

    def test_backward_walk_uses_the_opposite_root_axis_crossing_for_the_same_anatomical_foot(self) -> None:
        segment = {"index": 0, "start_frame": 1, "end_frame": 8}
        geometry = foot_samples({"Right": {1: 2, 2: 1, 3: -0.5, 4: -1.5, 5: 1.5, 6: 0.5, 7: -0.5, 8: -1.5}, "Left": {frame: 0 for frame in range(1, 9)}})
        self.assertEqual(release_events(segment, geometry, "Right", "wb_r"), [3, 7])
        self.assertEqual(role_window(segment, "wb_r", (), geometry), (3, 7))

    def test_walk_without_two_evidenced_crossings_is_rejected(self) -> None:
        segment = {"index": 0, "start_frame": 1, "end_frame": 7}
        geometry = foot_samples({"Right": {frame: 1 for frame in range(1, 8)}, "Left": {frame: 0 for frame in range(1, 8)}})
        with self.assertRaisesRegex(SelectionError, "no evidenced complete"):
            role_window(segment, "wf_r", (), geometry)

    def test_walk_role_name_cannot_replace_missing_right_leading_geometry_evidence(self) -> None:
        segment = {"index": 0, "start_frame": 1, "end_frame": 7}
        # The role identifier is deliberately valid, but neither foot ever
        # crosses the anatomical centre line.  There is no metadata input to
        # selection that can turn this into an evidenced phase.
        geometry = foot_samples({"Right": {frame: 0 for frame in range(1, 8)}, "Left": {frame: 0 for frame in range(1, 8)}})
        with self.assertRaisesRegex(SelectionError, "right release"):
            role_window(segment, "wf_r", (), geometry)

    def test_backward_transitions_swap_forward_source_windows_before_reversal(self) -> None:
        segment = {"index": 0, "start_frame": 1, "end_frame": 9}
        geometry = self._geometry(segment, {3: 2, 5: -1, 7: 2})
        self.assertEqual(role_window(segment, "i2f_r", (), geometry), (1, 3))
        self.assertEqual(role_window(segment, "b2i_r", (), geometry), (5, 9))
        self.assertEqual(role_window(segment, "f2i_r", (), geometry), (7, 9))
        self.assertEqual(role_window(segment, "i2b_r", (), geometry), (1, 5))

    def test_ambiguous_crossing_is_rejected_by_velocity_hysteresis(self) -> None:
        segment = {"index": 0, "start_frame": 1, "end_frame": 7}
        geometry = foot_samples({"Right": {1: -2, 2: -1, 3: 0.5, 4: 1.5, 5: -1.5, 6: -0.5, 7: 0.5}, "Left": {1: 2, 2: 1, 3: -0.5, 4: -1.5, 5: 1.5, 6: 0.5, 7: -0.5}})
        self.assertEqual(release_events(segment, geometry, "Right"), [])

    def test_former_tangential_pattern_rejects_wrong_direction_with_auditable_confidence_reason(self) -> None:
        segment = {"index": 0, "start_frame": 1, "end_frame": 7}
        geometry = foot_samples({"Right": {1: -2, 2: -1, 3: 1, 4: 2, 5: -1, 6: 1, 7: 2}, "Left": {frame: 0 for frame in range(1, 8)}})
        # This explicitly reconstructs the former near-centre/tangential
        # proposal, but makes its Root-forward component wrong-direction. It
        # must remain a recorded proposal rather than being filtered silently.
        geometry["Right"][5] = FootSample(-1e-7, (2.0, 0.0, -0.1))
        geometry["Right"][6] = FootSample(1e-7, (2.0, 0.0, -0.1))
        rows = release_event_evidence(segment, geometry, "Right")
        rejected = next(row for row in rows if row["source_frame"] == 6)
        self.assertFalse(rejected["accepted"])
        self.assertIn("wrong direction", rejected["reason"])
        self.assertLess(rejected["sample"]["directional_release_confidence"], .60)
        self.assertEqual(release_events(segment, geometry, "Right"), [3])

    def test_direction_consistent_release_is_accepted_over_the_hysteresis_horizon(self) -> None:
        segment = {"index": 0, "start_frame": 1, "end_frame": 6}
        geometry = foot_samples({"Right": {1: -2, 2: -1, 3: 1, 4: 2, 5: 3, 6: 4}, "Left": {frame: 0 for frame in range(1, 7)}})
        self.assertEqual(release_events(segment, geometry, "Right"), [3])
        accepted = release_event_evidence(segment, geometry, "Right")[0]
        self.assertTrue(accepted["accepted"])
        self.assertEqual(accepted["reason"], "accepted directional release")

    def test_mirror_swaps_and_reverse_preserves_anatomical_release_side(self) -> None:
        segment = {"index": 0, "start_frame": 1, "end_frame": 8}
        native = foot_samples({"Right": {1: -2, 2: -1, 3: 0.5, 4: 1.5, 5: -1.5, 6: -0.5, 7: 0.5, 8: 1.5}, "Left": {frame: 0 for frame in range(1, 9)}})
        mirrored = foot_samples({"Left": {frame: native["Right"][frame].position for frame in native["Right"]}, "Right": {frame: native["Left"][frame].position for frame in native["Left"]}})
        self.assertEqual(release_events(segment, native, "Right"), [3, 7])
        self.assertEqual(release_events(segment, mirrored, "Left"), [3, 7])
        self.assertEqual(derived_anatomical_side("Right", "mirror"), "Left")
        self.assertEqual(derived_anatomical_side("Right", "reverse"), "Right")

    def test_reverse_and_mirror_detection_keep_the_contractual_anatomical_side(self) -> None:
        segment = {"index": 0, "start_frame": 1, "end_frame": 8}
        reverse = foot_samples({"Right": {1: 2, 2: 1, 3: -0.5, 4: -1.5, 5: 1.5, 6: 0.5, 7: -0.5, 8: -1.5}, "Left": {frame: 0 for frame in range(1, 9)}})
        reverse_mirror = foot_samples({"Left": {frame: reverse["Right"][frame].position for frame in reverse["Right"]}, "Right": {frame: 0 for frame in range(1, 9)}})
        self.assertEqual(release_events(segment, reverse, "Right", "wb_r"), [3, 7])
        self.assertEqual(release_events(segment, reverse_mirror, "Left", "wb_l"), [3, 7])
        self.assertEqual(role_window(segment, "wb_l", (), reverse_mirror), (3, 7))

    def test_complete_segments_are_not_sliced(self) -> None:
        self.assertEqual(role_window({"index": 1, "start_frame": 17, "end_frame": 48}, "sr"), (17, 48))
        self.assertEqual(role_window({"index": 2, "start_frame": 17, "end_frame": 48}, "pr45"), (17, 48))

    def test_strafe_cycles_are_named_lateral_crossing_recurrences_not_thirds(self) -> None:
        segment = {"index": 4, "start_frame": 200, "end_frame": 330}
        # Asymmetric gait: each anatomical release has its own recurrence.
        left = {frame: 2.0 for frame in range(200, 331)}
        right = {frame: 2.0 for frame in range(200, 331)}
        for frame, value in ((255, -1.0), (256, -2.0), (306, 2.0), (307, -1.0), (308, -2.0)):
            left[frame] = value
        for frame, value in ((230, -1.0), (231, -2.0), (285, 2.0), (286, -1.0), (287, -2.0)):
            right[frame] = value
        geometry = lateral_foot_samples({"Left": left, "Right": right})
        self.assertEqual(role_window(segment, "xr_l", (), geometry), (255, 307))
        self.assertEqual(role_window(segment, "xr_r", (), geometry), (230, 286))
        self.assertNotEqual(role_window(segment, "xr_l", (), geometry), (243, 287))

    def test_arc_cycle_is_evidenced_recurrence_not_middle_third(self) -> None:
        segment = {"index": 5, "start_frame": 100, "end_frame": 190}
        left = {frame: -1.0 for frame in range(100, 191)}
        left.update({121: 1.0, 122: 2.0, 161: 1.0, 162: 2.0})
        geometry = foot_samples({"Left": left, "Right": {frame: 0.0 for frame in range(100, 191)}})
        self.assertEqual(role_window(segment, "ar_l", (), geometry), (121, 161))
        self.assertNotEqual(role_window(segment, "ar_l", (), geometry), (130, 160))

    def test_selection_is_deterministic_for_a_score_tie(self) -> None:
        chosen = select_candidate((Candidate("kimodo_multi_z", 1, 2, 3, {}), Candidate("kimodo_multi_a", 1, 2, 3, {})))
        self.assertEqual(chosen.action_name, "kimodo_multi_a")

    def test_lower_evaluated_repair_cost_precedes_source_identity(self) -> None:
        chosen = select_candidate((
            Candidate("kimodo_multi_a", 1, 33, .8, {}),
            Candidate("kimodo_multi_z", 1, 33, .2, {}),
        ))
        self.assertEqual(chosen.action_name, "kimodo_multi_z")

    def test_duration_neutral_ranking_does_not_prefer_the_short_window_by_full_action_key_density(self) -> None:
        # Before the regression fix, the same full-Action key count produced
        # density 5.0 for z_short (two frames) and 0.5 for a_long (20 frames),
        # so z_short won despite otherwise identical eligibility.  Ranking is
        # now duration-neutral, so the deterministic structural tie-break picks
        # a_long by action name.
        short = score_candidate("kimodo_multi_z_short", (Curve("pose.bones[\"Root\"].location", {frame: 0.0 for frame in range(1, 3)}, 10),), 1, 2)
        long = score_candidate("kimodo_multi_a_long", (Curve("pose.bones[\"Root\"].location", {frame: 0.0 for frame in range(1, 21)}, 10),), 1, 20)

        chosen = select_candidate((short, long))

        self.assertEqual(chosen.action_name, "kimodo_multi_a_long")
        self.assertEqual(short.score, 0.0)
        self.assertEqual(long.score, 0.0)
        self.assertEqual(
            short.evidence["excluded_from_ranking"],
            [
                "full_action_key_count",
                "candidate_window_duration",
                "full_action_key_count_divided_by_candidate_window_duration",
                "raw_fcurve_values",
            ],
        )

    def test_finite_transition_selects_named_boundary_crossing_with_surrounding_gait_evidence(self) -> None:
        segment = {"index": 0, "start_frame": 1, "end_frame": 78}
        # These reproduce the reported selected boundaries, but the retained
        # f2i [36,78] and i2f [5,58] intervals each contain a second foot phase.
        f2i_geometry = foot_samples({
            "Right": {frame: -1.0 if frame < 36 else (1.0 if frame == 36 else 2.0) for frame in range(1, 79)},
            "Left": {frame: 0.0 if frame < 50 else (2.0 if frame == 50 else (4.0 if frame == 51 else 6.0)) for frame in range(1, 79)},
        })
        i2f_geometry = foot_samples({
            "Right": {frame: -1.0 if frame < 58 else (1.0 if frame == 58 else 2.0) for frame in range(1, 79)},
            "Left": {frame: -2.0 if frame == 40 else (0.0 if frame == 41 else (2.0 if frame == 42 else 0.0)) for frame in range(1, 79)},
        })
        f2i = transition_evidence(segment, "f2i_r", f2i_geometry)
        i2f = transition_evidence({"index": 0, "start_frame": 5, "end_frame": 78}, "i2f_r", i2f_geometry)
        self.assertTrue(any((row.start, row.end) == (36, 78) and row.accepted for row in f2i))
        self.assertTrue(any((row.start, row.end) == (5, 58) and row.accepted for row in i2f))
        self.assertTrue(any(len(row.eligible_crossings) > 1 for row in (*f2i, *i2f)))

    def test_context_qualified_edge_release_counts_once_without_an_in_clip_sign_change(self) -> None:
        segment = {"index": 0, "start_frame": 1, "end_frame": 4}
        geometry = foot_samples({
            "Right": {frame: 1.0 for frame in range(1, 5)},
            "Left": {frame: 0.0 for frame in range(1, 5)},
        })
        source_event = release_event_evidence(
            {"index": 0, "start_frame": 1, "end_frame": 4},
            foot_samples({"Right": {1: -1.0, 2: 1.0, 3: 2.0, 4: 3.0}, "Left": {frame: 0.0 for frame in range(1, 5)}}),
            "Right", "f2i_r",
        )[0]
        events = canonical_transition_events(segment, geometry, "f2i_r", {
            "output_frame": 1, "predecessor_source_frame": 1,
            "successor_source_frame": 3, "same_phase_output_frames": [1], "source_event": source_event,
        })
        self.assertEqual([(event["kind"], event["foot_side"]) for event in events], [("edge", "Right")])

    def test_transition_without_context_qualified_edge_release_is_rejected_as_zero_phase(self) -> None:
        segment = {"index": 0, "start_frame": 1, "end_frame": 4}
        geometry = foot_samples({"Right": {frame: 1.0 for frame in range(1, 5)}, "Left": {frame: 0.0 for frame in range(1, 5)}})
        self.assertEqual(canonical_transition_events(segment, geometry, "f2i_r", None), [])

    def test_segment_zero_transition_context_maps_source_anatomy_for_every_derivation(self) -> None:
        """The canonical edge must use output anatomy, never source anatomy."""
        source_geometry = foot_samples({
            "Right": {1: -1.0, 2: 1.0, 3: 2.0, 4: 3.0},
            "Left": {frame: 0.0 for frame in range(1, 5)},
        })
        output_geometry = foot_samples({
            "Right": {frame: 0.0 for frame in range(1, 5)},
            "Left": {frame: 0.0 for frame in range(1, 5)},
        })
        cases = (
            ("i2f_r", "i2f_r", "native", "Right"),
            ("f2i_r", "f2i_r", "native", "Right"),
            ("i2b_r", "f2i_r", "reverse", "Right"),
            ("b2i_r", "i2f_r", "reverse", "Right"),
            ("i2f_l", "i2f_r", "mirror", "Left"),
            ("f2i_l", "f2i_r", "mirror", "Left"),
            ("i2b_l", "f2i_r", "reverse_mirror", "Left"),
            ("b2i_l", "i2f_r", "reverse_mirror", "Left"),
        )
        for output_role, source_role, derivation, expected_side in cases:
            with self.subTest(output_role=output_role):
                source_event = release_event_evidence(
                    {"index": 0, "start_frame": 1, "end_frame": 4}, source_geometry, "Right", source_role,
                )[0]
                mapped_event = map_transition_event_to_output_anatomy(source_event, derivation)
                assert mapped_event is not None
                self.assertEqual(mapped_event["foot_side"], expected_side)
                edge = "end" if output_role.startswith("i2") else "start"
                boundary_frame = 4 if edge == "end" else 1
                events = canonical_transition_events(
                    {"index": 0, "start_frame": 1, "end_frame": 4}, output_geometry, output_role,
                    {
                        "output_frame": boundary_frame,
                        "predecessor_source_frame": source_event["source_frame"] - 1,
                        "successor_source_frame": source_event["source_frame"] + 1,
                        "same_phase_output_frames": [boundary_frame],
                        "source_event": mapped_event,
                    },
                )
                self.assertEqual([(event["kind"], event["foot_side"]) for event in events], [("edge", expected_side)])

    def test_mapped_segment_zero_context_does_not_hide_an_extra_output_phase(self) -> None:
        source = foot_samples({
            "Right": {1: -1.0, 2: 1.0, 3: 2.0, 4: 3.0},
            "Left": {frame: 0.0 for frame in range(1, 5)},
        })
        source_event = release_event_evidence(
            {"index": 0, "start_frame": 1, "end_frame": 4}, source, "Right", "i2f_r",
        )[0]
        output = foot_samples({
            "Left": {frame: 0.0 for frame in range(1, 5)},
            "Right": {1: -1.0, 2: 1.0, 3: 2.0, 4: 3.0},
        })
        events = canonical_transition_events(
            {"index": 0, "start_frame": 1, "end_frame": 4}, output, "i2f_l",
            {
                "output_frame": 4,
                "predecessor_source_frame": source_event["source_frame"] - 1,
                "successor_source_frame": source_event["source_frame"] + 1,
                "same_phase_output_frames": [4],
                "source_event": map_transition_event_to_output_anatomy(source_event, "mirror"),
            },
        )
        self.assertEqual([(event["kind"], event["foot_side"]) for event in events], [("edge", "Left"), ("interior", "Right")])

    def test_quality_evidence_rejects_generic_wide_step_outlier_deterministically(self) -> None:
        candidates = {
            "viable_b": {"maximum_lateral_width": .26, "maximum_fore_aft_reach": .35, "foot_speed": .3, "foot_acceleration": .4, "foot_jerk": .5, "ground_envelope": .2, "planted_foot_slip": .1, "body_support_offset": .2},
            "wide_step": {"maximum_lateral_width": .90, "maximum_fore_aft_reach": .35, "foot_speed": .3, "foot_acceleration": .4, "foot_jerk": .5, "ground_envelope": .2, "planted_foot_slip": .1, "body_support_offset": .2},
            "viable_a": {"maximum_lateral_width": .25, "maximum_fore_aft_reach": .34, "foot_speed": .3, "foot_acceleration": .4, "foot_jerk": .5, "ground_envelope": .2, "planted_foot_slip": .1, "body_support_offset": .2},
        }
        report = quality_evidence(candidates)
        self.assertFalse(report["wide_step"]["accepted"])
        self.assertTrue(report["viable_a"]["accepted"])
        self.assertEqual(list(report), ["viable_a", "viable_b", "wide_step"])
        self.assertIn("baselines", report["wide_step"])
        self.assertIn("thresholds", report["wide_step"])
        self.assertEqual(
            report["wide_step"]["reasons"],
            ["maximum_lateral_width exceeds severe geometry limit"],
        )

    def test_low_minimum_separation_is_not_a_quality_rejection(self) -> None:
        report = quality_evidence({
            "narrow_phase": {"maximum_lateral_width": .25, "maximum_fore_aft_reach": .35},
            "ordinary_phase": {"maximum_lateral_width": .26, "maximum_fore_aft_reach": .36},
        })
        self.assertTrue(report["narrow_phase"]["accepted"])

    def test_long_stride_is_evidence_only_when_limb_cycle_is_anatomically_safe(self) -> None:
        report = quality_evidence({"long_stride": {
            "maximum_lateral_width": .3,
            "maximum_fore_aft_reach": 1.65,
            "maximum_leg_segment_scale_error": .001,
            "maximum_leg_extension_ratio": .96,
            "minimum_knee_orientation_dot": .62,
            "minimum_leg_lateral_separation_ratio": .18,
            "collapsed_leg_fraction": .02,
            "maximum_torso_skew_ratio": .14,
            "maximum_torso_tilt_rad": .2,
        }}, "walk")
        self.assertTrue(report["long_stride"]["accepted"], report)
        self.assertIsNone(report["long_stride"]["thresholds"]["maximum_fore_aft_reach"]["severe_maximum"])

    def test_direct_anatomy_contact_indicators_reject_unsafe_cycles(self) -> None:
        safe = {
            "maximum_lateral_width": .3, "maximum_fore_aft_reach": 1.6,
            "maximum_leg_segment_scale_error": .001, "maximum_leg_extension_ratio": .96,
            "minimum_knee_orientation_dot": .6, "minimum_leg_lateral_separation_ratio": .2,
            "collapsed_leg_fraction": .02, "maximum_torso_skew_ratio": .1,
            "maximum_torso_tilt_rad": .2, "planted_foot_slip": .1,
        }
        corruptions = {
            "overextended": {"maximum_leg_extension_ratio": 1.02},
            "crossed": {"minimum_leg_lateral_separation_ratio": -.3},
            "inverted_knee": {"minimum_knee_orientation_dot": -.2},
            "collapsed": {"collapsed_leg_fraction": .3},
            "torso_skew": {"maximum_torso_skew_ratio": .8},
            "contact": {"planted_foot_slip": 1.1},
        }
        for name, changed in corruptions.items():
            with self.subTest(name=name):
                report = quality_evidence({name: {**safe, **changed}}, "walk")
                self.assertFalse(report[name]["accepted"], report)

    def test_small_leg_segment_peer_outlier_is_evidence_only(self) -> None:
        reports = quality_evidence({
            "a": {"maximum_leg_segment_scale_error": 1e-7},
            "b": {"maximum_leg_segment_scale_error": 1.1e-7},
            "c": {"maximum_leg_segment_scale_error": 1.2e-7},
            "reconstructed": {"maximum_leg_segment_scale_error": 4e-7},
        }, "pivot")
        self.assertTrue(reports["reconstructed"]["accepted"])
        self.assertTrue(
            reports["reconstructed"]["policy"]["peer_outliers"]["maximum_leg_segment_scale_error"]
        )
        unsafe = quality_evidence({
            "stretched": {"maximum_leg_segment_scale_error": .031},
        }, "pivot")
        self.assertFalse(unsafe["stretched"]["accepted"])

    def test_natural_alternation_is_not_ranked_by_instantaneous_bilateral_equality(self) -> None:
        metrics = {
            "minimum_leg_lateral_separation_ratio": .05,
            "collapsed_leg_fraction": .0,
            "maximum_leg_extension_ratio": .98,
            "minimum_knee_orientation_dot": .5,
        }
        report = quality_evidence({"alternating": metrics}, "walk")
        self.assertTrue(report["alternating"]["accepted"], report)
        self.assertNotIn("bilateral_equality", report["alternating"]["metrics"])

    def test_natural_cycle_ranking_ignores_names_and_input_order(self) -> None:
        candidates = [
            {"name": "renamed_b", "metrics": {"planted_foot_slip": .2}, "repair": (.1,), "content": "b", "start": 1, "end": 30},
            {"name": "renamed_a", "metrics": {"planted_foot_slip": .1}, "repair": (.2,), "content": "a", "start": 5, "end": 35},
        ]
        key = lambda row: natural_cycle_candidate_rank_key(
            row["metrics"], row["repair"], row["content"], row["start"], row["end"],
        )
        self.assertEqual(sorted(candidates, key=key)[0]["content"], "a")
        self.assertEqual(sorted(reversed(candidates), key=key)[0]["content"], "a")
        candidates[1]["name"] = "arbitrary_name"
        self.assertEqual(sorted(candidates, key=key)[0]["content"], "a")

    def test_later_naturally_better_same_foot_cycle_can_win(self) -> None:
        early = natural_cycle_candidate_rank_key(
            {"planted_foot_slip": .2, "body_support_offset": .2}, (.1,), "early", 10, 40,
        )
        later = natural_cycle_candidate_rank_key(
            {"planted_foot_slip": .1, "body_support_offset": .2}, (.3,), "later", 40, 70,
        )
        self.assertLess(later, early)

    def test_lateral_role_width_and_crossing_proxies_are_evidence_only(self) -> None:
        def metrics(width):
            return {"maximum_lateral_width": width, "maximum_fore_aft_reach": .35, "foot_speed": .3,
                    "foot_acceleration": .4, "foot_jerk": .5, "ground_envelope": .2,
                    "planted_foot_slip": .1, "body_support_offset": .2}
        for family in ("strafe", "precision_lateral", "arc"):
            with self.subTest(family=family):
                report = quality_evidence({
                    "intentional_crossover": {
                        **metrics(1.200001),
                        "minimum_leg_lateral_separation_ratio": -.4,
                    },
                }, family)
                self.assertTrue(report["intentional_crossover"]["accepted"])
                self.assertIsNone(report["intentional_crossover"]["policy"]["maximum_lateral_width"])
                self.assertIn("evidence_only", report["intentional_crossover"]["policy"]["lateral_width_and_separation"])

    def test_lateral_role_direct_physical_failures_still_reject(self) -> None:
        safe = {
            "maximum_lateral_width": 1.4,
            "minimum_leg_lateral_separation_ratio": -.4,
            "maximum_leg_extension_ratio": .98,
            "minimum_knee_orientation_dot": .4,
            "collapsed_leg_fraction": .1,
            "maximum_torso_skew_ratio": .2,
            "planted_foot_slip": .1,
        }
        corruptions = {
            "extension": {"maximum_leg_extension_ratio": 1.02},
            "knee": {"minimum_knee_orientation_dot": -.2},
            "collapse": {"collapsed_leg_fraction": .3},
            "torso": {"maximum_torso_skew_ratio": .8},
            "contact": {"planted_foot_slip": 1.1},
        }
        for family in ("strafe", "precision_lateral", "arc"):
            for name, corruption in corruptions.items():
                with self.subTest(family=family, corruption=name):
                    report = quality_evidence({name: {**safe, **corruption}}, family)
                    self.assertFalse(report[name]["accepted"])

    def test_precision_peer_outliers_are_evidence_only_and_need_four_candidates(self) -> None:
        def metrics(width):
            return {"maximum_lateral_width": width, "maximum_fore_aft_reach": .35, "foot_speed": .3,
                    "foot_acceleration": .4, "foot_jerk": .5, "ground_envelope": .2,
                    "planted_foot_slip": .1, "body_support_offset": .2}
        small = quality_evidence({"a": metrics(.8), "b": metrics(.81), "c": metrics(1.1)}, "precision_lateral")
        large = quality_evidence({"a": metrics(.8), "b": metrics(.81), "c": metrics(.82), "outlier": metrics(1.1)}, "precision_lateral")
        self.assertTrue(small["c"]["accepted"])
        self.assertEqual(small["c"]["policy"]["peer_outliers"], {})
        self.assertTrue(large["outlier"]["accepted"])
        self.assertTrue(large["outlier"]["policy"]["peer_outliers"]["maximum_lateral_width"])

    def test_generic_and_walk_families_retain_the_generic_width_ceiling(self) -> None:
        report = quality_evidence({"wide": {"maximum_lateral_width": .71, "maximum_fore_aft_reach": .35}})
        self.assertFalse(report["wide"]["accepted"])
        self.assertEqual(report["wide"]["policy"]["maximum_lateral_width"], .70)
        walk = quality_evidence({"wide": {"maximum_lateral_width": .71}}, "walk")
        self.assertFalse(walk["wide"]["accepted"])
