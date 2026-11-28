"""ANIM-003 native idle clip export and pack validator commands.

Covers the authoring contract (authoring.md AUTH-AC-001–003) and the headless validator (pack-format.md
PACK-AC-001):

- publication keeps the destination UID and a failed export leaves the published clip untouched;
- the Kimodo path re-exports the real ``sad`` source through a verified setup copy, matching the Blender preview,
  with protected sources byte-unchanged;
- the control-rig path bakes a test-built action into a native clip that the game's validator accepts and binds on
  both reference templates, with the keyed rotation reproduced;
- the validator passes the default, fixture and extension packs and rejects every invalid fixture by pack, entry,
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
VALIDATOR = TOOL_ROOT / "godot" / "commands" / "validate_animation_pack.gd"
FIXTURE_BUILDER = TOOL_ROOT / "tests" / "build_control_rig_fixture.py"
WORK = ROOT / "game" / "temp" / "anim003-idle-clip-tests"
FEMALE = ROOT / "game/assets/characters/reference/female"
FEMALE_AUTHORING = ROOT / "game/assets/animations/source/reference/female"
FEMALE_SCENE = "res://assets/characters/reference/female/reference_female.blend"
MALE_SCENE = "res://assets/characters/reference/male/reference_male.blend"
FEMALE_TEMPLATE = "res://assets/characters/templates/reference_female/reference_female_npc.tscn"
MALE_TEMPLATE = "res://assets/characters/templates/reference_male/reference_male_npc.tscn"
SAD_SOURCE = FEMALE_AUTHORING / "idle/sad.blend"
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
        for output in (SAD_SOURCE.with_suffix(".tres"), ROOT / "clip.tres", self.directory / "clip.res"):
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
        (cls.directory / "pack").mkdir(parents=True)
        cls.blend = cls.directory / "authoring.blend"
        built = subprocess.run(
            ["blender", "--background", "--factory-startup", "--python-exit-code", "1", "--python", str(FIXTURE_BUILDER),
             "--", str(FEMALE / "reference_female.blend"), str(cls.blend)],
            cwd=ROOT, text=True, capture_output=True, check=False,
        )
        assert built.returncode == 0, built.stdout + built.stderr
        cls.clip = cls.directory / "pack" / "rig_probe.tres"
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
        pack = self.directory / "pack" / "pack.json"
        pack.write_text(json.dumps({
            "schema": "anim003_pack_v1", "id": "rig_probe",
            "entries": [{"id": "rig_probe", "category": "idle", "tags": ["probe"],
                         "description": "sways and lifts one arm", "clip": "rig_probe.tres"}],
        }), encoding="utf-8")
        root = "res://" + pack.parent.relative_to(ROOT / "game").as_posix() + "/"
        result = _validate(f"--pack={root}pack.json", f"--content-root={root}", f"--template={FEMALE_TEMPLATE}",
                           f"--template={MALE_TEMPLATE}", "--require-compatible")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(_marker(result.stdout, "ANIMATION_PACK_VALIDATION_JSON=")["passed"])
        for template in (FEMALE_TEMPLATE, MALE_TEMPLATE):
            self.assertIn(f"Template '{template}': idle/rig_probe (pack 'rig_probe') binds to skeleton 'GeneralSkeleton'.", result.stdout)


class ValidatorCommandTests(unittest.TestCase):
    def test_valid_packs_pass(self) -> None:
        default = _validate("--pack=res://assets/animations/packs/default/pack.json", f"--template={FEMALE_TEMPLATE}",
                            f"--template={MALE_TEMPLATE}", "--require-compatible")
        self.assertEqual(default.returncode, 0, default.stdout + default.stderr)
        fixture = _validate(f"--pack={FIXTURES}fixture/pack.json", f"--content-root={FIXTURES}fixture/")
        self.assertEqual(fixture.returncode, 0, fixture.stdout + fixture.stderr)
        extension = _validate(f"--pack={FIXTURES}extension/pack.json", f"--template={MALE_TEMPLATE}")
        self.assertEqual(extension.returncode, 0, extension.stdout + extension.stderr)
        self.assertIn("idle entry 'sad' from pack 'extension' overrides pack 'default'", extension.stdout)

    def test_every_rejection_class_fails_with_a_located_diagnostic(self) -> None:
        expected = {
            "malformed_json": (None, None, "malformed JSON"),
            "wrong_schema": (None, "schema", "schema must be 'anim003_pack_v1'"),
            "unknown_field": ("slump", "mood", "unknown entry field"),
            "missing_description": ("slump", "description", "description is missing or blank"),
            "empty_tags": ("slump", "tags", "tags must be a non-empty array"),
            "bad_category": ("slump", "category", "category must be 'idle' or 'locomotion'"),
            "duplicate_id": ("slump", "id", "duplicate idle entry id 'slump'"),
            "absolute_path": ("slump", "clip", "not a 'res://' or other scheme path"),
            "escaping_path": ("slump", "clip", "must not escape the content root"),
            "missing_file": ("slump", "clip", "does not exist"),
            "clip_not_animation": ("slump", "clip", "clip must be a loadable native Animation resource, found Curve"),
        }
        packs = [f"--pack={FIXTURES}fixture/invalid/{name}.json" for name in expected]
        result = _validate(*packs, f"--content-root={FIXTURES}fixture/")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        errors = _marker(result.stdout, "ANIMATION_PACK_VALIDATION_JSON=")["errors"]
        for name, (entry, field, message) in expected.items():
            with self.subTest(name=name):
                pack = f"{FIXTURES}fixture/invalid/{name}.json" if name == "malformed_json" else name
                located = [error for error in errors if error.startswith(f"Animation pack '{pack}'") and message in error]
                self.assertEqual(len(located), 1, errors)
                if entry is not None:
                    self.assertIn(f"entry '{entry}'", located[0])
                if field is not None:
                    self.assertIn(f"field '{field}'", located[0])
                self.assertIn(", path '", located[0])

        rejected = _validate(f"--pack={FIXTURES}extension/invalid/set_rejected.json")
        self.assertEqual(rejected.returncode, 1, rejected.stdout + rejected.stderr)
        self.assertIn(
            "Animation pack 'set_rejected', entry 'stagger', field 'set', "
            "path 'tests/animation/catalogue/extension/invalid/rejected-set/set.json': "
            "the ANIM-002 reviewed-set loader rejects the set",
            rejected.stderr + rejected.stdout,
        )

    def test_bad_arguments_are_refused(self) -> None:
        result = _validate("--packs=res://assets/animations/packs/default/pack.json")
        self.assertEqual(result.returncode, 2)
        self.assertIn("unknown argument '--packs=", result.stderr)


if __name__ == "__main__":
    unittest.main()
