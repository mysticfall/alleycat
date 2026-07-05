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
    def __init__(self, object_names: list[str], action_names: list[str]) -> None:
        self.object_names = object_names
        self.action_names = action_names
        self.data_to = types.SimpleNamespace(actions=[])

    def __enter__(self):
        return (
            types.SimpleNamespace(objects=self.object_names, actions=self.action_names),
            self.data_to,
        )

    def __exit__(self, exc_type, exc_value, traceback) -> bool:
        return False


class FakeLibraries:
    def __init__(self, object_names: list[str], action_names: list[str]) -> None:
        self.object_names = object_names
        self.action_names = action_names
        self.load_calls: list[tuple[str, bool, bool]] = []
        self.load_contexts: list[FakeLibraryLoad] = []

    def load(self, path: str, *, link: bool, relative: bool) -> FakeLibraryLoad:
        self.load_calls.append((path, link, relative))
        context = FakeLibraryLoad(self.object_names, self.action_names)
        self.load_contexts.append(context)
        return context


class FakeCoordinate:
    def __init__(self, y: float) -> None:
        self.y = y


class FakeKeyframePoint:
    def __init__(self, frame: float, value: float) -> None:
        self.co = types.SimpleNamespace(x=frame, y=value)


class FakeFCurve:
    def __init__(self, values: list[float]) -> None:
        self.keyframe_points = [
            FakeKeyframePoint(float(index + 1), value) for index, value in enumerate(values)
        ]


class FakeAction(dict):
    def __init__(self, name: str, frame_range: tuple[float, float], values: list[float]) -> None:
        super().__init__()
        self.name = name
        self.frame_range = frame_range
        self.fcurves = [FakeFCurve(values)]
        self.library = None
        self.use_fake_user = True
        self.users = 1


class FakeActions:
    def __init__(self, actions: list[FakeAction]) -> None:
        self._actions = {action.name: action for action in actions}

    def get(self, name: str):
        return self._actions.get(name)

    def __iter__(self):
        return iter(self._actions.values())


def import_generate_character_with_fake_bpy(object_names: list[str], action_names: list[str] | None = None):
    fake_libraries = FakeLibraries(object_names, action_names or [])
    fake_bpy = types.ModuleType("bpy")
    setattr(fake_bpy, "data", types.SimpleNamespace(libraries=fake_libraries, actions=FakeActions([])))
    setattr(fake_bpy, "types", types.SimpleNamespace(Action=FakeAction))
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

    def test_rigify_detection_accepts_unique_reference_rig_name(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy(["Female.rigify"])

        with TemporaryDirectory() as temp_dir:
            source_path = Path(temp_dir) / "animations.blend"
            source_path.touch()

            self.assertTrue(module.animation_source_contains_rigify_object(source_path, "Alex"))

    def test_rigify_detection_rejects_ambiguous_reference_rig_names(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy(
            ["Female.rigify", "Other.rigify"]
        )

        with TemporaryDirectory() as temp_dir:
            source_path = Path(temp_dir) / "animations.blend"
            source_path.touch()

            with self.assertRaisesRegex(module.ScriptError, "multiple Rigify armature candidates"):
                module.animation_source_contains_rigify_object(source_path, "Alex")

    def test_rigify_detection_prefers_configured_name_when_multiple_rigs_exist(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy(
            ["Alex.rigify", "Female.rigify"]
        )

        with TemporaryDirectory() as temp_dir:
            source_path = Path(temp_dir) / "animations.blend"
            source_path.touch()

            self.assertTrue(module.animation_source_contains_rigify_object(source_path, "Alex"))


class GenerateCharacterRigifyActionTests(unittest.TestCase):
    def test_baked_action_name_strips_legacy_noexp_suffix(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy([])

        self.assertEqual("Idle-loop", module.baked_action_name(FakeAction("Idle-loop-noexp", (1, 92), [0, 1])))

    def test_baked_action_name_accepts_current_suffixless_source_name(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy([])

        self.assertEqual("Idle-loop", module.baked_action_name(FakeAction("Idle-loop", (1, 92), [0, 1])))

    def test_transient_baked_action_name_uses_collision_suffix_for_source_name(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy([])
        source_action = FakeAction("Idle-loop", (1, 92), [0, 1])
        module.bpy.data.actions = FakeActions([source_action])

        self.assertEqual(
            "Idle-loop.baked",
            module.transient_baked_action_name("Idle-loop", source_action),
        )

    def test_static_action_validation_fails_clear_diagnostic(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy([])
        static_action = FakeAction("Static", (1, 30), [0, 0, 0])

        with self.assertRaisesRegex(module.ScriptError, "static/rest-pose-only"):
            module.validate_action_multiframe_non_static(static_action, "Rigify", "source action")

    def test_single_frame_action_validation_fails_clear_diagnostic(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy([])
        single_frame_action = FakeAction("Single", (1, 1), [0, 1])

        with self.assertRaisesRegex(module.ScriptError, "not multi-frame"):
            module.validate_action_multiframe_non_static(single_frame_action, "Rigify", "source action")

    def test_rigify_action_acquisition_reuses_existing_nla_dependency_actions(self) -> None:
        module, fake_libraries = import_generate_character_with_fake_bpy(
            [],
            ["Idle-loop", "Walk-loop"],
        )
        idle_action = FakeAction("Idle-loop", (1, 92), [0, 1])
        walk_action = FakeAction("Walk-loop", (1, 72), [0, 2])
        module.bpy.data.actions = FakeActions([idle_action, walk_action])
        persisted_actions: list[FakeAction] = []

        def fake_create_persistent_action_users(actions, animation_owner, failure_context, action_label):
            persisted_actions.extend(actions)

        setattr(module, "create_persistent_action_users", fake_create_persistent_action_users)

        with TemporaryDirectory() as temp_dir:
            source_path = Path(temp_dir) / "animations_rigify.blend"
            source_path.touch()

            acquired_actions = module.append_rigify_animation_actions(
                source_path,
                types.SimpleNamespace(type="ARMATURE", name="Female.rigify"),
            )

        self.assertEqual([idle_action, walk_action], acquired_actions)
        self.assertEqual([idle_action, walk_action], persisted_actions)
        self.assertEqual([], fake_libraries.load_contexts[-1].data_to.actions)
        self.assertEqual(["Idle-loop", "Walk-loop"], [action.name for action in acquired_actions])
        self.assertTrue(all(not action.name.endswith(".001") for action in acquired_actions))

    def test_rigify_source_cleanup_validation_catches_suffixless_original_collision(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy([])
        suffixed_source_action = FakeAction("Idle-loop.001", (1, 92), [0, 1])
        lingering_original_action = FakeAction("Idle-loop", (1, 92), [0, 1])
        baked_action = FakeAction("Idle-loop.baked", (1, 92), [0, 1])
        module.bpy.data.actions = FakeActions([lingering_original_action, baked_action])

        with self.assertRaisesRegex(module.ScriptError, "Idle-loop"):
            module.validate_removed_rigify_source_actions(
                [suffixed_source_action.name],
                [baked_action],
            )


if __name__ == "__main__":
    unittest.main()
