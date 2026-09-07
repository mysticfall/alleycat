from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from tools.forearm_twist_generator_run_ownership import sidecar_path
from tools.forearm_twist_weights import AxialSide, ForearmVertex, author_axial_weights


REPO_ROOT = Path(__file__).resolve().parents[2]
MPFB_ROOT = REPO_ROOT / "tools" / "mpfb"
CONFIG_ROOT = MPFB_ROOT / "config"
RIGS_ROOT = MPFB_ROOT / "data" / "rigs"
MPFB_EXTENSION_ROOT = Path(
    os.environ.get(
        "MPFB_EXTENSION_ROOT",
        Path.home() / ".config" / "blender" / "5.2" / "extensions" / "blender_org" / "mpfb",
    )
)
SPATIAL_VALIDATOR = MPFB_ROOT / "validate_forearm_twist_weights.py"
SAVED_FIXTURE = REPO_ROOT / "tools/tests/fixtures/forearm_twist_saved_validator_fixture.py"

EXPECTED_PRESETS = {
    "female": {
        "filename": "human.alleycat_female.json",
        "rig": "custom.alleycat_female",
        "source_settings_sha256": "6c79558eff21aa299e9078e1d43976b1a9dac88f83198a1a324e68b61912a8b0",
    },
    "male": {
        "filename": "human.alleycat_male.json",
        "rig": "custom.alleycat_male",
        "source_settings_sha256": "544ee4828ff6879554b3445021358265daac0b8a9629c183aa3c68f53fe7cc7c",
    },
}


def load_json(path: Path) -> dict:
    with path.open(encoding="utf-8") as source:
        data = json.load(source)
    if not isinstance(data, dict):
        raise AssertionError(f"{path} must contain a JSON object.")
    return data


def source_settings_digest(preset: dict) -> str:
    source_settings = dict(preset)
    source_settings.pop("rig", None)
    payload = json.dumps(source_settings, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def assert_positive_side_support(test: unittest.TestCase, side: dict) -> None:
    """Require actual final positive helper support on eligible owned vertices."""

    if side["source_domain_rows"] > 0:
        test.assertGreater(side["positive_twist_rows"], 0)
        test.assertGreater(side["positive_twist_mass"], 0.0)


class MpfbForearmTwistAssetTests(unittest.TestCase):
    @unittest.skipUnless(shutil.which("blender"), "Blender is required for saved-output validation")
    def test_saved_validator_rejects_applicable_mesh_without_helper_support(self) -> None:
        # The complete observation window is a reopened, SHA-linked .blend and
        # its schema-7 sidecar, through the ordinary validator CLI exit status.
        with tempfile.TemporaryDirectory(prefix="rig-002-c1-", dir=REPO_ROOT / "game/temp") as directory:
            generated = subprocess.run(
                [shutil.which("blender"), "--background", "--python-exit-code", "1",
                 "--python", str(SAVED_FIXTURE), "--", directory],
                cwd=REPO_ROOT, capture_output=True, text=True, timeout=120,
            )
            self.assertEqual(0, generated.returncode, generated.stdout + generated.stderr)
            for scenario in ("clothing_missing", "body_missing", "unowned", "protected"):
                with self.subTest(scenario=scenario):
                    output = Path(directory) / f"{scenario}.blend"
                    result = subprocess.run(
                        [shutil.which("blender"), "--background", "--python-exit-code", "1",
                         "--python", str(SPATIAL_VALIDATOR), "--", scenario, str(output)],
                        cwd=REPO_ROOT, capture_output=True, text=True, timeout=120,
                    )
                    if scenario.endswith("missing"):
                        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                        self.assertIn(
                            f"AssertionError: {scenario} Fixture.{'body' if scenario == 'body_missing' else 'clothing'} left: "
                            "independent source domain has no positive physical twist support",
                            result.stdout + result.stderr,
                        )
                        self.assertIn(
                            "source rows=1, twist rows=0, twist mass=0.0",
                            result.stdout + result.stderr,
                        )
                    else:
                        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_row_count_is_not_positive_helper_support(self) -> None:
        # The old gate accepts this body despite having no physical helper support.
        body = {"ownership_rows": 12, "eligible_owned_rows": 3, "source_domain_rows": 3,
                "positive_twist_rows": 0, "positive_twist_mass": 0.0}
        self.assertGreater(body["ownership_rows"], 0)
        with self.assertRaises(AssertionError):
            assert_positive_side_support(self, body)

    def test_clothing_source_domain_cannot_disappear_without_helper_support(self) -> None:
        # Complete visible window: a positive same-side lower/hand pool in the
        # distal forearm is saved without the physical helper channel.
        sides = (AxialSide("lowerarm_l", "forearm_l", "hand_l", frozenset({"finger_l"})),
                 AxialSide("lowerarm_r", "forearm_r", "hand_r", frozenset({"finger_r"})))
        vertices = [ForearmVertex(0.0, 0.0, {"lowerarm_l": 0.4, "hand_l": 0.6})]
        authored = author_axial_weights(vertices, (), sides,
                                        {"_l": [(0.85, 0.02)], "_r": [(0.85, 0.02)]})
        self.assertEqual(((True,), (False,)), authored.source_domain)
        # Deliberately absent authored/saved helper rows, as in the C1 failure.
        clothing = {"eligible_owned_rows": 0, "source_domain_rows": sum(authored.source_domain[0]),
                    "positive_twist_rows": 0, "positive_twist_mass": 0.0}
        with self.assertRaises(AssertionError):
            assert_positive_side_support(self, clothing)

    def test_unowned_and_protected_clothing_do_not_claim_wrist_support(self) -> None:
        side = AxialSide("lowerarm_l", "forearm_l", "hand_l", frozenset({"finger_l"}))
        rows = [{"body": 1.0}, {"lowerarm_l": 0.4, "hand_l": 0.6, "finger_l": 0.01},
                {"lowerarm_l": 0.4, "hand_l": 0.6, "lowerarm_r": 0.01}]
        authored = author_axial_weights(
            [ForearmVertex(0.0, 0.0, row) for row in rows], (), (side,),
            {"_l": [(0.85, 0.02)] * len(rows)})
        self.assertEqual((False, False, False), authored.source_domain[0])
        assert_positive_side_support(self, {"source_domain_rows": 0})

    def test_empty_physical_zero_ledger_is_not_body_evidence(self) -> None:
        # A count of zero passes the previous >= 0 check, but not the body gate.
        physical_rows = {"retained_original_zero_memberships": 0}
        self.assertGreaterEqual(physical_rows["retained_original_zero_memberships"], 0)
        with self.assertRaises(AssertionError):
            self.assertGreater(physical_rows["retained_original_zero_memberships"], 0)

    def test_repository_catalogue_is_complete_and_contains_only_generic_assets(self) -> None:
        self.assertEqual(
            {item["filename"] for item in EXPECTED_PRESETS.values()},
            {path.name for path in CONFIG_ROOT.glob("*.json")},
        )
        self.assertEqual(
            {
                "alleycat_female.json",
                "weights.alleycat_female.json",
                "alleycat_male.json",
                "weights.alleycat_male.json",
            },
            {path.name for path in RIGS_ROOT.glob("*.json")},
        )

    def test_presets_preserve_generic_source_settings_and_reference_custom_rigs(self) -> None:
        for sex, expected in EXPECTED_PRESETS.items():
            with self.subTest(sex=sex):
                preset = load_json(CONFIG_ROOT / expected["filename"])
                self.assertEqual(expected["rig"], preset.get("rig"))
                self.assertTrue(
                    (RIGS_ROOT / f"{expected['rig'].removeprefix('custom.')}.json").is_file()
                )
                self.assertEqual(expected["source_settings_sha256"], source_settings_digest(preset))

    def test_custom_rigs_and_weights_use_mpfb_schema_110(self) -> None:
        for sex in EXPECTED_PRESETS:
            with self.subTest(sex=sex):
                rig = load_json(RIGS_ROOT / f"alleycat_{sex}.json")
                weights = load_json(RIGS_ROOT / f"weights.alleycat_{sex}.json")

                self.assertEqual(110, rig.get("version"))
                self.assertFalse(rig.get("is_subrig"))
                self.assertIsInstance(rig.get("bones"), dict)
                self.assertEqual(["LeftForearmTwist", "RightForearmTwist"], rig.get("identifying_bones"))
                self.assertEqual(110, weights.get("version"))
                self.assertIsInstance(weights.get("weights"), dict)

    def test_every_custom_rig_bone_has_matching_weights(self) -> None:
        for sex in EXPECTED_PRESETS:
            with self.subTest(sex=sex):
                rig = load_json(RIGS_ROOT / f"alleycat_{sex}.json")
                weights = load_json(RIGS_ROOT / f"weights.alleycat_{sex}.json")
                self.assertEqual(set(rig["bones"]), set(weights["weights"]))

    def test_custom_rigs_preserve_their_respective_base_definitions(self) -> None:
        female_bones = load_json(RIGS_ROOT / "alleycat_female.json")["bones"]
        male_bones = load_json(RIGS_ROOT / "alleycat_male.json")["bones"]

        self.assertTrue({"breast_l", "breast_r"}.issubset(female_bones))
        self.assertTrue({"breast_l", "breast_r"}.isdisjoint(male_bones))
        self.assertTrue({"lowerarm_l", "lowerarm_r", "hand_l", "hand_r"}.issubset(female_bones))
        self.assertTrue({"lowerarm_l", "lowerarm_r", "hand_l", "hand_r"}.issubset(male_bones))

    def test_helpers_have_exact_bilateral_deform_topology(self) -> None:
        for sex in EXPECTED_PRESETS:
            with self.subTest(sex=sex):
                bones = load_json(RIGS_ROOT / f"alleycat_{sex}.json")["bones"]
                helpers = {name for name in bones if "ForearmTwist" in name}
                self.assertEqual({"LeftForearmTwist", "RightForearmTwist"}, helpers)

                for side, lower_arm, hand, helper in (
                    ("left", "lowerarm_l", "hand_l", "LeftForearmTwist"),
                    ("right", "lowerarm_r", "hand_r", "RightForearmTwist"),
                ):
                    with self.subTest(sex=sex, side=side):
                        self.assertEqual(lower_arm, bones[helper]["parent"])
                        self.assertEqual(lower_arm, bones[hand]["parent"])
                        self.assertEqual({}, bones[helper]["rigify"])
                        self.assertEqual(
                            {**bones[lower_arm], "parent": lower_arm},
                            bones[helper],
                        )
                        self.assertFalse(
                            any(
                                forbidden in field.lower()
                                for field in bones[helper]
                                for forbidden in ("profile", "retarget", "ik")
                            )
                        )

    def test_helper_weights_are_separate_from_lower_arm_weights(self) -> None:
        for sex in EXPECTED_PRESETS:
            weights = load_json(RIGS_ROOT / f"weights.alleycat_{sex}.json")["weights"]
            for lower_arm, helper in (
                ("lowerarm_l", "LeftForearmTwist"),
                ("lowerarm_r", "RightForearmTwist"),
            ):
                with self.subTest(sex=sex, helper=helper):
                    self.assertEqual(
                        [vertex for vertex, _weight in weights[lower_arm]],
                        [vertex for vertex, _weight in weights[helper]],
                    )
                    self.assertTrue(weights[helper])
                    self.assertTrue(any(weight == 0.0 for _vertex, weight in weights[helper]))
                    self.assertTrue(any(
                        lower_weight != helper_weight
                        for (_vertex, lower_weight), (_helper_vertex, helper_weight) in zip(
                            weights[lower_arm], weights[helper], strict=True
                        )
                    ))
                    for (_vertex, lower_weight), (_helper_vertex, helper_weight) in zip(
                        weights[lower_arm], weights[helper], strict=True
                    ):
                        self.assertGreaterEqual(lower_weight, helper_weight)

    @unittest.skipUnless(
        shutil.which("blender") and (MPFB_EXTENSION_ROOT / "blender_manifest.toml").is_file(),
        "Blender 5.2 with the MPFB extension is required for disposable spatial-weight validation.",
    )
    def test_ordinary_generator_proves_body_and_clothing_twist_weights(self) -> None:
        blender = shutil.which("blender")
        with tempfile.TemporaryDirectory(prefix=".alleycat-mpfb-test-", dir=REPO_ROOT / "game" / "temp") as temporary_directory:
            temporary_root = Path(temporary_directory)
            outputs: dict[str, Path] = {}
            for variant in EXPECTED_PRESETS:
                output_path = temporary_root / f"{variant}.blend"
                configuration_path = temporary_root / f"{variant}.json"
                configuration_path.write_text(
                    json.dumps(
                        {
                            "preset": f"alleycat_{variant}",
                            "name": f"MpfbProbe{variant.title()}",
                            "outputFile": output_path.relative_to(REPO_ROOT / "game").as_posix(),
                            "amimations": [],
                        }
                    ),
                    encoding="utf-8",
                )
                result = subprocess.run(
                    [str(REPO_ROOT / "tools" / "generate_character.sh"), str(configuration_path)],
                    check=False,
                    capture_output=True,
                    encoding="utf-8",
                    cwd=REPO_ROOT,
                    timeout=180,
                )
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertTrue(output_path.is_file())
                self.assertTrue(sidecar_path(output_path).is_file())
                outputs[variant] = output_path

            result = subprocess.run(
                [
                    blender,
                    "--background",
                    "--python-exit-code",
                    "1",
                    "--python",
                    str(SPATIAL_VALIDATOR),
                    "--",
                    *(value for item in outputs.items() for value in (item[0], str(item[1]))),
                ],
                check=False,
                capture_output=True,
                encoding="utf-8",
                cwd=REPO_ROOT,
                timeout=120,
            )

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        measurement_lines = [line for line in result.stdout.splitlines() if line.startswith("{")]
        self.assertTrue(measurement_lines, result.stdout + result.stderr)
        measurements = json.loads(measurement_lines[-1])
        print("ordinary MPFB physical support: " + json.dumps({
            variant: {
                "body": {"zeros": item["body"]["physical_rows"]["retained_original_zero_memberships"],
                         **{side: {key: value for key, value in item["body"][side].items()
                                   if key in ("eligible_owned_rows", "source_domain_rows", "positive_twist_rows",
                                              "positive_twist_mass")}
                            for side in ("left", "right")}},
                "clothing": {
                    name: {side: item_mesh[side]["source_domain_rows"] for side in ("left", "right")}
                    for name, item_mesh in item["clothing"].items()},
            } for variant, item in measurements.items()
        }, sort_keys=True))
        self.assertEqual({"female", "male"}, set(measurements))
        self.assertGreaterEqual(len(measurements["male"]["clothing"]), 1)
        for variant, result in measurements.items():
            with self.subTest(variant=variant):
                self.assertIsNotNone(result["body"])
                for mesh_name, mesh_measurements in {"body": result["body"], **result["clothing"]}.items():
                    with self.subTest(mesh=mesh_name, measurement="physical_rows"):
                        physical_rows = mesh_measurements["physical_rows"]
                        self.assertGreater(physical_rows["count"], 0)
                        self.assertLessEqual(physical_rows["max_error"], 1.0e-6)
                        if mesh_name == "body":
                            self.assertGreater(physical_rows["retained_original_zero_memberships"], 0)
                    for side in ("left", "right"):
                        side_measurements = mesh_measurements[side]
                        with self.subTest(mesh=mesh_name, side=side):
                            if mesh_name == "body":
                                self.assertGreater(side_measurements["eligible_owned_rows"], 0)
                                self.assertGreater(side_measurements["source_domain_rows"], 0)
                            assert_positive_side_support(self, side_measurements)
                            self.assertLessEqual(side_measurements["ownership_max_error"], 1.0e-5)
                            self.assertLessEqual(side_measurements["axial_deformation_max_error"], 1.0e-5)


if __name__ == "__main__":
    unittest.main()
