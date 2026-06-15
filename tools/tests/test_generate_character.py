from __future__ import annotations

import importlib.util
import sys
import types
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory


REPO_ROOT = Path(__file__).resolve().parents[2]
GENERATE_CHARACTER_PATH = REPO_ROOT / "tools" / "generate_character.py"


class FakeLibraryLoad:
    def __init__(self, object_names: list[str]) -> None:
        self.object_names = object_names

    def __enter__(self):
        return types.SimpleNamespace(objects=self.object_names), types.SimpleNamespace()

    def __exit__(self, exc_type, exc_value, traceback) -> bool:
        return False


class FakeLibraries:
    def __init__(self, object_names: list[str]) -> None:
        self.object_names = object_names
        self.load_calls: list[tuple[str, bool, bool]] = []

    def load(self, path: str, *, link: bool, relative: bool) -> FakeLibraryLoad:
        self.load_calls.append((path, link, relative))
        return FakeLibraryLoad(self.object_names)


def import_generate_character_with_fake_bpy(object_names: list[str]):
    fake_libraries = FakeLibraries(object_names)
    fake_bpy = types.ModuleType("bpy")
    setattr(fake_bpy, "data", types.SimpleNamespace(libraries=fake_libraries))
    setattr(fake_bpy, "types", types.SimpleNamespace())
    fake_generate_body_colliders = types.ModuleType("generate_body_colliders")

    previous_bpy = sys.modules.get("bpy")
    previous_generate_body_colliders = sys.modules.get("generate_body_colliders")
    sys.modules["bpy"] = fake_bpy
    sys.modules["generate_body_colliders"] = fake_generate_body_colliders

    module_name = "generate_character_under_test"
    sys.modules.pop(module_name, None)
    spec = importlib.util.spec_from_file_location(module_name, GENERATE_CHARACTER_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Could not load module spec for {GENERATE_CHARACTER_PATH}")

    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    try:
        spec.loader.exec_module(module)
    finally:
        if previous_bpy is None:
            sys.modules.pop("bpy", None)
        else:
            sys.modules["bpy"] = previous_bpy

        if previous_generate_body_colliders is None:
            sys.modules.pop("generate_body_colliders", None)
        else:
            sys.modules["generate_body_colliders"] = previous_generate_body_colliders

    return module, fake_libraries


class GenerateCharacterRigifyDetectionTests(unittest.TestCase):
    def test_rigify_animation_object_name_uses_configured_character_name(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy([])

        self.assertEqual("Alex.rigify", module.rigify_animation_object_name("Alex"))
        self.assertEqual("Female.rigify", module.rigify_animation_object_name("Female"))

    def test_rigify_detection_uses_non_female_configured_name(self) -> None:
        module, fake_libraries = import_generate_character_with_fake_bpy(["Alex.rigify"])

        with TemporaryDirectory() as temp_dir:
            source_path = Path(temp_dir) / "animations.blend"
            source_path.touch()

            self.assertTrue(module.animation_source_contains_rigify_object(source_path, "Alex"))

        self.assertEqual(1, len(fake_libraries.load_calls))
        self.assertTrue(all(call[1:] == (False, False) for call in fake_libraries.load_calls))

    def test_rigify_detection_does_not_fall_back_to_female_name(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy(["Female.rigify"])

        with TemporaryDirectory() as temp_dir:
            source_path = Path(temp_dir) / "animations.blend"
            source_path.touch()

            self.assertFalse(module.animation_source_contains_rigify_object(source_path, "Alex"))


if __name__ == "__main__":
    unittest.main()
