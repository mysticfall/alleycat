"""Synthetic saved-scene census contract; no Blender file is generated here."""

import importlib.util
import sys
import types
import unittest
from pathlib import Path
from unittest.mock import patch

VALIDATOR = Path(__file__).resolve().parents[1] / "mpfb/validate_forearm_twist_weights.py"


def validator_module():
    fake_bpy = types.ModuleType("bpy")
    fake_bpy.types = types.SimpleNamespace(Object=object)
    fake_bpy.ops = types.SimpleNamespace(wm=types.SimpleNamespace(open_mainfile=lambda **_: None))
    fake_mathutils = types.ModuleType("mathutils")
    fake_mathutils.Matrix = object
    spec = importlib.util.spec_from_file_location("census_validator", VALIDATOR)
    module = importlib.util.module_from_spec(spec)
    with patch.dict(sys.modules, {"bpy": fake_bpy, "mathutils": fake_mathutils}):
        spec.loader.exec_module(module)
    return module, fake_bpy


class ExportCensusTests(unittest.TestCase):
    def setUp(self):
        self.validator, self.bpy = validator_module()
        self.armature = types.SimpleNamespace(type="ARMATURE", data=types.SimpleNamespace(bones=[]))
        def mesh(name, rows, skinned=False):
            groups = [types.SimpleNamespace(index=i, name=group) for i, group in enumerate(("zero", "positive", "spare"))]
            vertices = [types.SimpleNamespace(index=i, groups=[types.SimpleNamespace(group=groups.index(next(g for g in groups if g.name == key)), weight=weight) for key, weight in row.items()]) for i, row in enumerate(rows)]
            return types.SimpleNamespace(type="MESH", name=name, data=types.SimpleNamespace(vertices=vertices), vertex_groups=groups,
                modifiers=[types.SimpleNamespace(type="ARMATURE", object=self.armature)] if skinned else [])
        self.body = mesh("actor.body", [{}], True)
        self.skipped = mesh("actor.prop", [{"zero": 0.0, "positive": .5}])
        self.bpy.context = types.SimpleNamespace(scene=types.SimpleNamespace(objects=[self.armature, self.body, self.skipped]))
        self.evidence = {"meshes": [{"name": self.body.name, "vertex_count": 1, "axial_rows": []}],
                         "skipped_meshes": [{"name": self.skipped.name, "vertex_count": 1,
                            "group_indices": [[0, "zero"], [1, "positive"], [2, "spare"]],
                            "original_physical_rows": [{"vertex": 0, "weights": {"zero": 0.0, "positive": .5}}]}]}

    def measure(self):
        with patch.object(self.validator.forearm_twist_generator_run_ownership, "load_evidence", return_value=self.evidence), patch.object(self.validator, "validate_mesh", return_value={}):
            return self.validator.measure_variant("synthetic", Path("synthetic.blend"))

    def test_lost_zero_on_excluded_export_mesh_is_rejected(self):
        self.skipped.data.vertices[0].groups.pop(0)
        with self.assertRaisesRegex(AssertionError, "physical|membership"):
            self.measure()

    def test_lost_positive_and_added_zero_are_rejected(self):
        for changed in ([types.SimpleNamespace(group=0, weight=0.0)],
                        [types.SimpleNamespace(group=0, weight=0.0), types.SimpleNamespace(group=1, weight=.5), types.SimpleNamespace(group=2, weight=0.0)]):
            with self.subTest(changed=changed):
                self.skipped.data.vertices[0].groups = changed
                with self.assertRaises(AssertionError):
                    self.measure()

    def test_unchanged_skipped_mesh_and_skinned_partition(self):
        self.assertIn("skipped", self.measure())
        self.evidence["skipped_meshes"].append(self.evidence["meshes"][0])
        with self.assertRaises(AssertionError):
            self.measure()

    def test_tampered_record_and_invalid_mapping_fail(self):
        for mutation in (lambda r: r.update(original_physical_rows=[]),
                         lambda r: r.update(group_indices=[[1, "zero"], [1, "positive"]]),
                         lambda r: r.update(name="other.prop")):
            with self.subTest(mutation=mutation):
                record = self.evidence["skipped_meshes"][0]
                original = dict(record)
                mutation(record)
                with self.assertRaises(AssertionError):
                    self.measure()
                record.clear()
                record.update(original)

    def test_skipped_mesh_missing_extra_and_changed_vertex_count_fail(self):
        for change in (lambda: self.bpy.context.scene.objects.remove(self.skipped),
                       lambda: self.bpy.context.scene.objects.append(types.SimpleNamespace(type="MESH", name="extra", modifiers=[])),
                       lambda: self.skipped.data.vertices.append(types.SimpleNamespace(index=1, groups=[]))):
            with self.subTest(change=change):
                objects = list(self.bpy.context.scene.objects)
                vertices = list(self.skipped.data.vertices)
                change()
                with self.assertRaises(AssertionError):
                    self.measure()
                self.bpy.context.scene.objects[:] = objects
                self.skipped.data.vertices[:] = vertices


if __name__ == "__main__":
    unittest.main()
