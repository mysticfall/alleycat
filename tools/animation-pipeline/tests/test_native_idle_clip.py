"""ANIM-003 native idle clip export and variant validator commands.

Covers the authoring contract (authoring.md AUTH-AC-001–003) and the headless validator (variant-format.md
ANIM-003-VAR-AC-001):

- publication keeps the destination UID and a failed export leaves the published clip untouched;
- the Kimodo path re-exports the real ``sad`` source through a verified setup copy, matching the Blender preview,
  with protected sources byte-unchanged;
- the control-rig path bakes a test-built action into a native clip that the game's validator accepts and binds on
  both reference templates, with the keyed rotation reproduced;
- the validator passes explicit variants and composed templates and rejects invalid descriptors by owner, category/id,
  field and path.

Blender and Godot runs write only beneath ``game/temp/anim003-idle-clip-tests``. Requires ``blender`` and
``godot-mono`` (or ``GODOT_PATH``) and a built game C# assembly for the validator cases.

    PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s tools/animation-pipeline/tests -p test_native_idle_clip.py
"""
from __future__ import annotations

import hashlib
import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

TOOL_ROOT = Path(__file__).resolve().parents[1]
ROOT = TOOL_ROOT.parents[1]
sys.path.insert(0, str(TOOL_ROOT))

from kimodo_pipeline.native_clip_export import ExportError, publish, validate_output, with_uid_of  # noqa: E402

EXPORT = TOOL_ROOT / "commands" / "export_idle_clip.py"
VALIDATOR = TOOL_ROOT / "godot" / "commands" / "validate_animation_variants.gd"
FIXTURE_BUILDER = TOOL_ROOT / "tests" / "build_control_rig_fixture.py"
WORK = ROOT / "game" / "temp" / "anim003-idle-clip-tests"
FEMALE = ROOT / "game/assets/characters/reference/female"
FEMALE_AUTHORING = ROOT / "game/assets/animations/authoring/reference/female"
FEMALE_SCENE = "res://assets/characters/reference/female/reference_female.blend"
MALE_SCENE = "res://assets/characters/reference/male/reference_male.blend"
FEMALE_TEMPLATE = "res://assets/characters/templates/reference_female/reference_female_npc.tscn"
MALE_TEMPLATE = "res://assets/characters/templates/reference_male/reference_male_npc.tscn"
SAD_SOURCE = ROOT / "game/assets/animations/idle/sad/source/sad.blend"
SAD_ACTION = "kimodo_m0a3dyf0"
SETUP = FEMALE_AUTHORING / "kimodo_retarget_setup.blend"
FIXTURES = "res://tests/animation/catalogue/"
# The exported clip must reproduce the Blender-evaluated joints it was sampled from; 1 mm is far below visible
# difference and far above float round-off (observed ~3e-5 m).
PREVIEW_PARITY_LIMIT_M = 1e-3
STAGED_HEADER = '[gd_resource type="Animation" format=3]\n\n[resource]\nresource_name = "a"\n'


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _godot() -> str:
    return os.environ.get("GODOT_PATH", "godot-mono")


def _marker(output: str, prefix: str) -> dict:
    line = next((line for line in output.splitlines() if line.startswith(prefix)), None)
    if line is None:
        raise AssertionError(f"{prefix} missing from output:\n{output}")
    return json.loads(line[len(prefix):])


def _export(*arguments: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run([sys.executable, str(EXPORT), *arguments], cwd=ROOT, text=True, capture_output=True, check=False)


def _validate(*arguments: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [_godot(), "--headless", "--xr-mode", "off", "--path", str(ROOT / "game"), "--script", str(VALIDATOR), "--", *arguments],
        cwd=ROOT, text=True, capture_output=True, check=False, timeout=600,
    )


def _rotation_keys(clip: Path, bone: str) -> list[tuple[float, float, float, float]]:
    """Read the rotation keys of one bone track from a text Animation resource."""
    text = clip.read_text(encoding="utf-8")
    index = next(
        match.group(1) for match in re.finditer(r'tracks/(\d+)/path = NodePath\("%GeneralSkeleton:([^"]+)"\)', text)
        if match.group(2) == bone and f'tracks/{match.group(1)}/type = "rotation_3d"' in text
    )
    values = re.search(rf"tracks/{index}/keys = PackedFloat32Array\(([^)]*)\)", text).group(1)
    numbers = [float(value) for value in values.split(",")]
    return [tuple(numbers[offset + 2:offset + 6]) for offset in range(0, len(numbers), 6)]


def _angle_degrees(a: tuple[float, ...], b: tuple[float, ...]) -> float:
    dot = min(1.0, abs(sum(x * y for x, y in zip(a, b))))
    return math.degrees(2.0 * math.acos(dot))


class PublicationTests(unittest.TestCase):
    def setUp(self) -> None:
        WORK.mkdir(parents=True, exist_ok=True)
        self.directory = Path(tempfile.mkdtemp(prefix="publish-", dir=WORK))

    def tearDown(self) -> None:
        shutil.rmtree(self.directory, ignore_errors=True)

    def test_publication_keeps_the_destination_uid(self) -> None:
        destination = self.directory / "clip.tres"
        destination.write_text(STAGED_HEADER.replace("format=3]", 'format=3 uid="uid://keepme123"]'), encoding="utf-8")
        staged = self.directory / "staged.tres"
        staged.write_text(STAGED_HEADER.replace('"a"', '"b"'), encoding="utf-8")

        publish(staged, destination)

        published = destination.read_text(encoding="utf-8")
        self.assertTrue(published.startswith('[gd_resource type="Animation" format=3 uid="uid://keepme123"]'))
        self.assertIn('resource_name = "b"', published)

    def test_new_clip_has_no_borrowed_uid(self) -> None:
        self.assertEqual(with_uid_of(STAGED_HEADER, self.directory / "absent.tres"), STAGED_HEADER)

    def test_failed_publication_leaves_the_published_clip_intact(self) -> None:
        destination = self.directory / "clip.tres"
        destination.write_text(STAGED_HEADER, encoding="utf-8")
        before = destination.read_bytes()
        staged = self.directory / "staged.tres"
        staged.write_text('[gd_resource type="Curve" format=3]\n', encoding="utf-8")

        with self.assertRaises(ExportError):
            publish(staged, destination)

        self.assertEqual(destination.read_bytes(), before)
        self.assertEqual(sorted(path.name for path in self.directory.iterdir()), ["clip.tres", "staged.tres"])

    def test_output_must_be_a_project_text_resource_outside_sources(self) -> None:
        for output in (SAD_SOURCE.with_suffix(".tres"), FEMALE_AUTHORING / "clip.tres",
                       ROOT / "clip.tres", self.directory / "clip.res"):
            with self.subTest(output=output), self.assertRaises(ExportError):
                validate_output(output, [SAD_SOURCE])


class KimodoPathTests(unittest.TestCase):
    """Real-asset Kimodo export of the migrated ``sad`` source (staged only, never published)."""

    def test_sad_reexports_matching_the_blender_preview_without_touching_sources(self) -> None:
        before = {path: _sha256(path) for path in (SETUP, SAD_SOURCE)}
        result = _export(
            "kimodo", "--character-root", str(FEMALE_AUTHORING), "--source", str(SAD_SOURCE), "--action", SAD_ACTION,
            "--skeleton-scene", FEMALE_SCENE, "--check-skeleton-scene", MALE_SCENE,
            "--output", str(WORK / "kimodo" / "sad.tres"), "--temp-root", str(WORK / "kimodo"), "--stage-only",
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        summary = _marker(result.stdout, "IDLE_CLIP_JSON=")
        self.assertFalse(summary["published"])
        self.assertFalse((WORK / "kimodo" / "sad.tres").exists())
        self.assertEqual(summary["frames"], 240)
        self.assertAlmostEqual(summary["length_seconds"], 239 / 30, places=6)
        self.assertLess(summary["preview_parity_worst_joint_m"], PREVIEW_PARITY_LIMIT_M)
        self.assertEqual(set(summary["compatibility"].values()), {True})
        report = json.loads((ROOT / summary["staging"] / "report.json").read_text(encoding="utf-8"))
        self.assertEqual(report["source"]["baseline_character"], "female")
        self.assertEqual(report["source"]["slot"], "OBKimodo")
        self.assertEqual({path: _sha256(path) for path in before}, before)
        self.assertFalse((ROOT / summary["staging"] / "inputs").exists())

    def test_missing_action_fails_and_leaves_prior_output(self) -> None:
        output = WORK / "kimodo-missing" / "clip.tres"
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(STAGED_HEADER, encoding="utf-8")
        result = _export(
            "kimodo", "--character-root", str(FEMALE_AUTHORING), "--source", str(SAD_SOURCE), "--action", "no_such_action",
            "--skeleton-scene", FEMALE_SCENE, "--output", str(output), "--temp-root", str(WORK / "kimodo-missing"),
        )
        self.assertEqual(result.returncode, 1)
        self.assertIn('Action "no_such_action" is not in', result.stderr)
        self.assertEqual(output.read_text(encoding="utf-8"), STAGED_HEADER)


class ControlRigPathTests(unittest.TestCase):
    """A control rig over the reference skeleton, built by the test, baked to a native clip."""

    @classmethod
    def setUpClass(cls) -> None:
        cls.directory = WORK / "rig"
        shutil.rmtree(cls.directory, ignore_errors=True)
        (cls.directory / "variant").mkdir(parents=True)
        cls.blend = cls.directory / "authoring.blend"
        built = subprocess.run(
            ["blender", "--background", "--factory-startup", "--python-exit-code", "1", "--python", str(FIXTURE_BUILDER),
             "--", str(FEMALE / "reference_female.blend"), str(cls.blend)],
            cwd=ROOT, text=True, capture_output=True, check=False,
        )
        assert built.returncode == 0, built.stdout + built.stderr
        cls.clip = cls.directory / "variant" / "rig_probe.tres"
        cls.result = _export(
            "rig", "--blend", str(cls.blend), "--control-rig", "Controls", "--target", "Female",
            "--action", "rig_probe_sway", "--skeleton-scene", FEMALE_SCENE, "--check-skeleton-scene", MALE_SCENE,
            "--output", str(cls.clip), "--temp-root", str(cls.directory),
        )

    def test_rig_action_bakes_to_a_native_clip_on_the_reference_skeleton(self) -> None:
        self.assertEqual(self.result.returncode, 0, self.result.stdout + self.result.stderr)
        summary = _marker(self.result.stdout, "IDLE_CLIP_JSON=")
        self.assertEqual((summary["frames"], summary["length_seconds"]), (31, 1.0))
        self.assertLess(summary["preview_parity_worst_joint_m"], PREVIEW_PARITY_LIMIT_M)
        text = self.clip.read_text(encoding="utf-8")
        self.assertTrue(text.startswith('[gd_resource type="Animation"'))
        self.assertIn("loop_mode = 1", text)
        self.assertNotIn("Controls", text)
        self.assertEqual(set(re.findall(r'NodePath\("(%[^:"]+):', text)), {"%GeneralSkeleton"})

    def test_keyed_control_rotation_is_reproduced(self) -> None:
        # The fixture keys upperarm_l 30 degrees about its local axis at frame 16 (key 15) with its parent still, so
        # the native local rotation must turn by the same angle whatever the importer's bone axes are.
        keys = _rotation_keys(self.clip, "LeftUpperArm")
        self.assertAlmostEqual(_angle_degrees(keys[0], keys[15]), 30.0, delta=0.05)
        self.assertAlmostEqual(_angle_degrees(keys[0], keys[30]), 0.0, delta=0.05)

    def test_frozen_action_is_refused_without_output(self) -> None:
        output = self.directory / "still" / "still.tres"
        result = _export(
            "rig", "--blend", str(self.blend), "--control-rig", "Controls", "--target", "Female",
            "--action", "rig_probe_still", "--skeleton-scene", FEMALE_SCENE,
            "--output", str(output), "--temp-root", str(self.directory),
        )
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn('Action "rig_probe_still" evaluates to one frozen pose', result.stderr)
        self.assertFalse(output.exists())

    def test_validator_accepts_the_clip_on_both_reference_templates(self) -> None:
        directory = self.directory / "variant"
        relative = directory.relative_to(ROOT / "game").as_posix()
        descriptor = directory / "variant.json"
        descriptor.write_text(json.dumps({
            "schema": "anim003_variant_v1", "id": "rig_probe", "category": "idle", "tags": ["probe"],
            "description": "sways and lifts one arm", "clip": relative + "/rig_probe.tres",
        }), encoding="utf-8")
        resource = f"res://{relative}/variant.json"
        templates = []
        for index, template in enumerate((FEMALE_TEMPLATE, MALE_TEMPLATE)):
            target = f"template_{index}.tscn"
            (directory / target).write_text(f'''[gd_scene format=3]

[ext_resource type="PackedScene" path="{template}" id="1_template"]
[ext_resource type="Script" path="res://src/Animations/AnimationVariantLayer.cs" id="2_layer"]
[ext_resource type="Script" path="res://src/Animations/AnimationVariantOperation.cs" id="3_operation"]

[sub_resource type="Resource" id="Resource_probe"]
script = ExtResource("3_operation")
Descriptor = "{resource}"

[node name="ProbeTemplate" instance=ExtResource("1_template")]

[node name="ProbeVariants" type="Node" parent="AnimationCatalogue" index="1"]
script = ExtResource("2_layer")
Operations = [SubResource("Resource_probe")]
''', encoding="utf-8")
            templates.append(f"res://{relative}/{target}")
        result = _validate(f"--variant={resource}", *(f"--template={value}" for value in templates), "--require-compatible")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(_marker(result.stdout, "ANIMATION_VARIANT_VALIDATION_JSON=")["passed"])
        for template in templates:
            self.assertIn(f"Template '{template}': idle/rig_probe (owner 'default') binds", result.stdout)


class ValidatorCommandTests(unittest.TestCase):
    def test_valid_variants_and_templates_pass(self) -> None:
        result = _validate("--variant=res://assets/animations/locomotion/feminine/variant.json",
                           f"--template={FEMALE_TEMPLATE}", f"--template={MALE_TEMPLATE}", "--require-compatible")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(_marker(result.stdout, "ANIMATION_VARIANT_VALIDATION_JSON=")["passed"])
        fixture = _validate(f"--variant={FIXTURES}fixture/idle/variant.json")
        self.assertEqual(fixture.returncode, 0, fixture.stdout + fixture.stderr)

    def test_every_rejection_class_fails_with_a_located_diagnostic(self) -> None:
        expected = {
            "malformed_json": ("descriptor/payload", "field"),
            "wrong_schema": ("schema", "must be anim003_variant_v1"),
            "unknown_field": ("mood", "unknown"),
            "missing_description": ("description", "must not be blank"),
            "empty_tags": ("tags", "non-empty array"),
            "bad_category": ("category", "must be idle or locomotion"),
            "duplicate_field": ("id", "duplicate field"),
            "absolute_path": ("clip", "owner-root-relative"),
            "escaping_path": ("clip", "parent traversal"),
            "missing_file": ("clip", "does not exist"),
            "clip_not_animation": ("clip", "loadable native Animation resource, found Curve"),
            "rejected-set/variant": ("set", "ANIM-002 reviewed-set loader rejects the set"),
        }
        prefix = f"{FIXTURES}fixture/invalid/"
        result = _validate(*(f"--variant={prefix}{name}.json" for name in expected))
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        errors = _marker(result.stdout, "ANIMATION_VARIANT_VALIDATION_JSON=")["errors"]
        self.assertEqual(len(errors), len(expected))
        for name, (field, message) in expected.items():
            with self.subTest(name=name):
                located = [error for error in errors if error.startswith(f"Descriptor '{prefix}{name}.json'")]
                self.assertEqual(len(located), 1, errors)
                self.assertIn(f"field '{field}'", located[0])
                self.assertIn(message, located[0])

    def test_bad_arguments_are_refused(self) -> None:
        result = _validate("--variants=res://assets/animations/idle/sad/variant.json")
        self.assertEqual(result.returncode, 2)
        self.assertIn("unknown or blank argument '--variants=", result.stderr)


if __name__ == "__main__":
    unittest.main()
