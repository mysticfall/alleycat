from __future__ import annotations

import json
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

import tools.forearm_twist_generator_run_ownership as ownership


REPO_ROOT = Path(__file__).resolve().parents[2]
SIDES = (("lowerarm_l", "LeftForearmTwist", "hand_l"),
         ("lowerarm_r", "RightForearmTwist", "hand_r"))
DEFORM_GROUPS = {"lower", "helper", "hand", "finger", "garment"}
AXIAL_ROWS = [
    {
        "vertex": 0,
        "weights": {"lower": 0.2, "helper": 0.4, "hand": 0.1, "finger": 0.3},
    }
]


class GeneratorRunOwnershipTests(unittest.TestCase):
    def test_saved_zero_promotion_requires_original_same_side_unprotected_ownership(self) -> None:
        # Complete export-positive key ledger -> authored axial -> saved physical row.
        # A forged eligibility list and a positive axial helper must not legitimise
        # wrong-side or protected original physical ownership.
        fingers = {"LeftForearmTwist": {"finger_l"}, "RightForearmTwist": {"finger_r"}}
        for helper, lower, hand, opposite_lower in (
            ("LeftForearmTwist", "lowerarm_l", "hand_l", "lowerarm_r"),
            ("RightForearmTwist", "lowerarm_r", "hand_r", "lowerarm_l"),
        ):
            eligible = {side[1]: ([0] if side[1] == helper else []) for side in SIDES}
            axial = [{"vertex": 0, "weights": {lower: .4, helper: .3, hand: .3}}]
            saved = {0: dict(axial[0]["weights"])}
            zero = [{"vertex": 0, "zeros": [helper]}]
            positive_cases = (
                ("wrong-side lower", [opposite_lower]),
                ("opposite unsuffixed helper", ["RightForearmTwist" if helper.startswith("Left") else "LeftForearmTwist"]),
                ("protected finger", [lower, "finger_l" if helper.startswith("Left") else "finger_r"]),
                ("opposite finger", [lower, "finger_r" if helper.startswith("Left") else "finger_l"]),
                ("bilateral", [lower, opposite_lower]),
            )
            for label, names in positive_cases:
                with self.subTest(helper=helper, label=label), self.assertRaisesRegex(ValueError, "lost physical zero"):
                    ownership.compare_zero_ledger(zero, axial, saved, eligible, SIDES, "saved",
                                                  [{"vertex": 0, "positives": names}], fingers)
            for positive in ([lower], [hand]):
                with self.subTest(helper=helper, valid=positive):
                    self.assertEqual(0, ownership.compare_zero_ledger(
                        zero, axial, saved, eligible, SIDES, "saved",
                        [{"vertex": 0, "positives": positive}], fingers))
            with self.assertRaisesRegex(ValueError, "lost physical zero"):
                ownership.compare_zero_ledger(zero, axial, saved, eligible, SIDES, "saved",
                                              [{"vertex": 0, "positives": [lower]}])
            # An originally absent helper can also be authored: no original
            # zero key is present to promote, and the positive axial row is valid.
            self.assertEqual(0, ownership.compare_zero_ledger(
                [], axial, saved, eligible, SIDES, "saved",
                [{"vertex": 0, "positives": [lower]}], fingers))

    def test_original_positive_helper_authored_to_zero_is_not_an_invented_key(self) -> None:
        # Complete saved-row observation: an originally positive helper membership may
        # physically persist at zero when its authored axial mass is zero.
        axial = [{"vertex": 0, "weights": {"lowerarm_l": .3, "LeftForearmTwist": .4, "hand_l": .3}}]
        eligible = {"LeftForearmTwist": [0], "RightForearmTwist": []}
        original_zeros = []
        original_positives = [{"vertex": 0, "positives": ["lowerarm_l", "LeftForearmTwist", "hand_l"]}]
        saved = {0: {"lowerarm_l": .3, "LeftForearmTwist": .4, "hand_l": .3}}
        self.assertEqual(1, ownership.compare_physical_rows(axial, saved, eligible, SIDES, 1e-6, "saved")[0])
        self.assertEqual(0, ownership.compare_zero_ledger(
            original_zeros, axial, saved, eligible, SIDES, "saved", original_positives))
        with self.assertRaisesRegex(ValueError, "invented physical zero"):
            ownership.compare_zero_ledger(original_zeros, axial,
                {0: {**saved[0], "ghost": 0.0}}, eligible, SIDES, "saved", original_positives)
        with self.assertRaisesRegex(ValueError, "missing physical key ledger"):
            ownership.compare_zero_ledger(original_zeros, axial, saved, eligible, SIDES, "saved", None)
        with self.assertRaisesRegex(ValueError, "malformed physical positive ledger"):
            ownership.compare_zero_ledger(original_zeros, axial, saved, eligible, SIDES, "saved",
                [{"vertex": 0, "positives": ["LeftForearmTwist", "LeftForearmTwist"]}])

    def test_export_local_out_of_domain_zero_survives_saved_membership_window(self) -> None:
        # Synthetic export-local row, captured before authoring and observed after save.
        # The old positive-only comparison silently accepts the lost nonbilateral key.
        axial = [{"vertex": 0, "weights": {"lowerarm_l": .3, "LeftForearmTwist": .4, "hand_l": .3}}]
        final = {0: dict(axial[0]["weights"])}
        eligible = {"LeftForearmTwist": [], "RightForearmTwist": []}
        self.assertEqual(1, ownership.compare_physical_rows(axial, final, eligible, SIDES, 1e-6, "saved")[0])
        ledger = [{"vertex": 0, "zeros": ["pin"]}]
        with self.assertRaisesRegex(ValueError, "lost physical zero"):
            ownership.compare_zero_ledger(ledger, axial, final, eligible, SIDES, "saved", [])

    def test_zero_ledger_promotion_and_protected_membership(self) -> None:
        axial = [{"vertex": 0, "weights": {"lowerarm_l": .3, "LeftForearmTwist": .4, "hand_l": .3}},
                 {"vertex": 1, "weights": {"lowerarm_l": .4, "hand_l": .6}}]
        eligible = {"LeftForearmTwist": [0], "RightForearmTwist": []}
        ledger = [{"vertex": 0, "zeros": ["LeftForearmTwist", "finger_r", "pin"]},
                  {"vertex": 1, "zeros": ["finger_l"]}]
        final = {0: {**axial[0]["weights"], "finger_r": 0.0, "pin": 0.0},
                 1: {**axial[1]["weights"], "finger_l": 0.0}}
        original_positives = [{"vertex": 0, "positives": ["lowerarm_l", "hand_l"]}]
        fingers = {"LeftForearmTwist": {"finger_l"}, "RightForearmTwist": {"finger_r"}}
        self.assertEqual(3, ownership.compare_zero_ledger(
            ledger, axial, final, eligible, SIDES, "saved", original_positives, fingers))
        for label, changed, changed_ledger, changed_eligible, message in (
            ("protected pin", {0: {k: v for k, v in final[0].items() if k != "pin"}, 1: final[1]}, ledger, eligible, "lost physical zero"),
            ("finger ghost", {0: {k: v for k, v in final[0].items() if k != "finger_r"}, 1: final[1]}, ledger, eligible, "lost physical zero"),
            ("forged promotion", {0: final[0], 1: {**final[1], "finger_l": .1}}, ledger, eligible, "lost physical zero"),
            ("ineligible helper", final, ledger, {"LeftForearmTwist": [], "RightForearmTwist": []}, "lost physical zero"),
            ("invented zero", {0: {**final[0], "ghost": 0.0}, 1: final[1]}, ledger, eligible, "invented physical zero"),
            ("near-zero is positive", {0: {**final[0], "pin": 1e-9}, 1: final[1]}, ledger, eligible, "lost physical zero"),
            ("missing ledger", final, None, eligible, "missing physical key ledger"),
            ("wrong vertex", final, [{"vertex": 2, "zeros": ["pin"]}], eligible, "malformed physical zero ledger"),
        ):
            with self.subTest(label=label), self.assertRaisesRegex(ValueError, message):
                ownership.compare_zero_ledger(changed_ledger, axial, changed, changed_eligible, SIDES, "saved",
                                              original_positives, fingers)
        changed_positive = {0: {**final[0], "hand_l": .2}, 1: final[1]}
        self.assertEqual(3, ownership.compare_zero_ledger(
            ledger, axial, changed_positive, eligible, SIDES, "saved", original_positives, fingers))
        with self.assertRaisesRegex(ValueError, "physical influence"):
            ownership.compare_physical_rows(axial, changed_positive, eligible, SIDES, 1e-6, "saved")

    def test_bilateral_zero_helper_cannot_be_promoted(self) -> None:
        axial = [{"vertex": 0, "weights": {"lowerarm_l": .2, "lowerarm_r": .2,
                   "LeftForearmTwist": .2, "hand_l": .2, "hand_r": .2}}]
        with self.assertRaisesRegex(ValueError, "lost physical zero"):
            ownership.compare_zero_ledger([{"vertex": 0, "zeros": ["LeftForearmTwist"]}], axial,
                {0: axial[0]["weights"]}, {"LeftForearmTwist": [0], "RightForearmTwist": []}, SIDES, "saved", [])

    def test_ordinary_physical_rows_reject_changes_hidden_by_deform_normalisation(self) -> None:
        rows = [
            {"vertex": 0, "weights": {"lowerarm_l": .3, "lowerarm_r": .2,
                                        "hand_l": .1, "hand_r": .1, "pin": .3}},
            {"vertex": 1, "weights": {"lowerarm_l": .2, "LeftForearmTwist": .4,
                                        "hand_l": .1, "pin": .3}},
        ]
        final = {0: dict(rows[0]["weights"]),
                 1: {"lowerarm_l": .2, "LeftForearmTwist": .4, "hand_l": .1, "pin": .3}}
        eligible = {"LeftForearmTwist": [1], "RightForearmTwist": []}
        deform = {"lowerarm_l", "lowerarm_r", "hand_l", "hand_r", "finger_l",
                  "LeftForearmTwist", "RightForearmTwist"}
        validator = (REPO_ROOT / "tools/mpfb/validate_forearm_twist_weights.py").read_text(encoding="utf-8")
        self.assertIn('compare_physical_rows(axial_rows, final_physical_rows, axial_mesh.get("authoring_eligible")', validator)
        self.assertEqual(2, ownership.compare_physical_rows(rows, final, eligible, SIDES, 1e-6, "ordinary")[0])
        with_zero_from_authoring = [rows[0], {"vertex": 1, "weights": {
            **rows[1]["weights"], "LeftForearmTwist": 0.0}}]
        with self.assertRaisesRegex(ValueError, "invalid physical authoring eligibility"):
            ownership.compare_physical_rows(
                with_zero_from_authoring, final, eligible, SIDES, 1e-6, "ordinary")
        with self.assertRaisesRegex(ValueError, "protected physical zero membership"):
            ownership.compare_physical_rows([
                {"vertex": 0, "weights": {**rows[0]["weights"], "zero_mask": 0.0}}, rows[1]],
                final, eligible, SIDES, 1e-6, "ordinary")
        for label, changed in (
            ("non-deform", {0: {**final[0], "pin": .2}, 1: final[1]}),
            ("scaled deform", {0: {name: (weight * .5 if name in deform else weight)
                                    for name, weight in final[0].items()}, 1: final[1]}),
            ("scaled eligible", {0: final[0], 1: {name: weight * .5
                                                  for name, weight in final[1].items()}}),
            ("missing membership", {0: {name: weight for name, weight in final[0].items()
                                           if name != "pin"}, 1: final[1]}),
            ("zero membership", {0: {**final[0], "pin": 0.0}, 1: final[1]}),
        ):
            with self.subTest(label=label), self.assertRaisesRegex(ValueError, "physical"):
                ownership.compare_physical_rows(rows, changed, eligible, SIDES, 1e-6, "ordinary")
        outside = {0: final[0], 1: {**rows[1]["weights"], "LeftForearmTwist": .2}}
        with self.assertRaisesRegex(ValueError, "physical"):
            ownership.compare_physical_rows(rows, outside,
                                            {"LeftForearmTwist": [], "RightForearmTwist": []},
                                            SIDES, 1e-6, "out-of-domain")

    def test_both_sides_are_checked_even_when_opposite_authoring_changes(self) -> None:
        rows = [{"vertex": 0, "weights": {"lowerarm_l": .2, "LeftForearmTwist": .3,
                    "RightForearmTwist": .3, "hand_r": .2}}]
        final = {0: {"lowerarm_l": .2, "LeftForearmTwist": .3,
                     "RightForearmTwist": .3, "hand_r": .2}}
        for lower, helper, hand in SIDES:
            self.assertEqual(1, ownership.compare_axial_rows(rows, final, set(final[0]),
                lower, helper, hand, 1e-7, "both sides")[0])
        final[0]["RightForearmTwist"] = .1
        with self.assertRaises(ValueError):
            ownership.compare_axial_rows(rows, final, set(final[0]),
                "lowerarm_r", "RightForearmTwist", "hand_r", 1e-7, "right")

    def test_valid_axial_and_final_rows_pass(self) -> None:
        row_count, maximum_error = ownership.compare_axial_rows(
            AXIAL_ROWS,
            {0: {"lower": 0.2, "helper": 0.4, "hand": 0.1, "finger": 0.3}},
            DEFORM_GROUPS,
            "lower",
            "helper",
            "hand",
            1.0e-7,
            "test",
        )

        self.assertEqual(1, row_count)
        self.assertEqual(0.0, maximum_error)

    def test_forged_final_lower_hand_or_unrelated_ownership_fails(self) -> None:
        forged_rows = {
            "lower": {"lower": 0.1, "helper": 0.4, "hand": 0.1, "finger": 0.3, "garment": 0.1},
            "hand": {"lower": 0.2, "helper": 0.4, "hand": 0.0, "finger": 0.3, "garment": 0.1},
            "unrelated": {"lower": 0.2, "helper": 0.4, "hand": 0.1, "finger": 0.2, "garment": 0.1},
        }

        for label, final in forged_rows.items():
            with self.subTest(label=label):
                with self.assertRaisesRegex(ValueError, label if label != "lower" else "lower-arm"):
                    ownership.compare_axial_rows(
                        AXIAL_ROWS,
                        {0: final},
                        DEFORM_GROUPS,
                        "lower",
                        "helper",
                        "hand",
                        1.0e-7,
                        "test",
                    )

    def test_missing_and_stale_sidecar_fail_closed(self) -> None:
        with TemporaryDirectory() as temporary_directory:
            output_path = Path(temporary_directory) / "generated.blend"
            output_path.write_bytes(b"generated-output")

            with self.assertRaisesRegex(ValueError, "Missing generator-run ownership validation sidecar"):
                ownership.load_evidence(REPO_ROOT, output_path)

            evidence_path = ownership.sidecar_path(output_path)
            evidence = ownership.build_evidence(REPO_ROOT, output_path, "alleycat_female", [])
            evidence_path.write_text(json.dumps(evidence), encoding="utf-8")
            self.assertEqual([], ownership.load_evidence(REPO_ROOT, output_path)["meshes"])

            for stale_schema in (4, 5, 6):
                stale = json.loads(json.dumps(evidence))
                stale["schema_version"] = stale_schema
                evidence_path.write_text(json.dumps(stale), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "Unsupported generator-run ownership validation sidecar"):
                    ownership.load_evidence(REPO_ROOT, output_path)

            census = ownership.build_evidence(REPO_ROOT, output_path, "alleycat_female", [],
                [{"name": "prop", "vertex_count": 1, "group_indices": [[0, "zero"]],
                  "original_physical_rows": [{"vertex": 0, "weights": {"zero": 0.0}}]}])
            census["skipped_meshes"][0]["original_physical_rows"][0]["weights"].clear()
            evidence_path.write_text(json.dumps(census), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "generation mismatch"):
                ownership.load_evidence(REPO_ROOT, output_path)

            malformed = json.loads(json.dumps(evidence))
            malformed["meshes"] = [{"name": "body"}]
            evidence_path.write_text(json.dumps(malformed), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Invalid generator-run ownership validation rows"):
                ownership.load_evidence(REPO_ROOT, output_path)
            missing_domain = ownership.build_evidence(REPO_ROOT, output_path, "alleycat_female",
                [{"name": "body", "original_zero_rows": [], "original_positive_rows": []}])
            evidence_path.write_text(json.dumps(missing_domain), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Invalid generator-run ownership validation rows"):
                ownership.load_evidence(REPO_ROOT, output_path)
            legacy = ownership.build_evidence(REPO_ROOT, output_path, "alleycat_female",
                [{"name": "body", "original_zero_rows": []}])
            evidence_path.write_text(json.dumps(legacy), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Invalid generator-run ownership validation rows"):
                ownership.load_evidence(REPO_ROOT, output_path)
            complete = ownership.build_evidence(REPO_ROOT, output_path, "alleycat_female",
                [{"name": "body", "original_zero_rows": [], "original_positive_rows": [], "source_domain": {}}])
            complete["meshes"][0]["original_positive_rows"] = [{"vertex": 0, "positives": ["ghost"]}]
            evidence_path.write_text(json.dumps(complete), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "generation mismatch"):
                ownership.load_evidence(REPO_ROOT, output_path)
            mismatched = json.loads(json.dumps(evidence))
            mismatched["generation"]["id"] = "0" * 64
            evidence_path.write_text(json.dumps(mismatched), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "generation mismatch"):
                ownership.load_evidence(REPO_ROOT, output_path)

            forged_source = json.loads(json.dumps(evidence))
            forged_source["source_asset"]["sha256"] = "0" * 64
            evidence_path.write_text(json.dumps(forged_source), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Stale generator-run ownership validation source asset"):
                ownership.load_evidence(REPO_ROOT, output_path)

            evidence_path.write_text(json.dumps(evidence), encoding="utf-8")
            output_path.write_bytes(b"altered-generated-output")
            with self.assertRaisesRegex(ValueError, "Stale generator-run ownership validation sidecar"):
                ownership.load_evidence(REPO_ROOT, output_path)


if __name__ == "__main__":
    unittest.main()
