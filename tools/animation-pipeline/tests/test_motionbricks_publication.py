"""Exercise each exporter's actual publication statement without requiring Blender/CUDA."""
import ast
import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

TOOL = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOL))


def fixture(kind):
    names = ["Hips", "Child"]
    matrix = [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1]]
    if kind == "model-frames":
        return dict(names=names, offsets=[[[1, 0, 0], [0, 1, 0], [0, 0, 1]]] * 2)
    pose = [[0, 1, 0, 0, 0, 0, 1], [0, 0.4, 0, 0, 0, 0, 1]]
    return dict(names=names, parents=[-1, 0], rest=pose, blenderRest=[matrix, matrix],
                sources={"fixture.blend": "a" * 64})


def export_publication(kind, directory, data):
    filename = "export_motionbricks_frames.py" if kind == "model-frames" else "export_motionbricks.py"
    tree = ast.parse((TOOL / filename).read_text())
    # Execute the real final publication call, not a reimplementation of its algorithm.
    statements = [n for n in tree.body if isinstance(n, ast.Expr) and isinstance(n.value, ast.Call)
                  and ((isinstance(n.value.func, ast.Attribute) and n.value.func.attr == "write_text")
                       or (isinstance(n.value.func, ast.Name) and n.value.func.id == "publish_motionbricks"))]
    assert len(statements) == 1, "Exporter must have exactly one checked publication call"
    scope = dict(OUT=directory, out=directory, data=data, json=json,
                 skeleton=SimpleNamespace(bone_order_names=data["names"],
                     global_rot_offsets=SimpleNamespace(tolist=lambda: data.get("offsets"))))
    if isinstance(statements[0].value.func, ast.Name):
        from kimodo_pipeline.motionbricks_publication import publish_motionbricks
        scope["publish_motionbricks"] = publish_motionbricks
    exec(compile(ast.Module(body=statements, type_ignores=[]), filename, "exec"), scope)


class PublicationTests(unittest.TestCase):
    def test_exporter_publishes_the_skeleton_without_clips(self):
        tree = ast.parse((TOOL / "export_motionbricks.py").read_text())
        assigned = {target.id for node in tree.body if isinstance(node, ast.Assign)
                    for target in node.targets if isinstance(target, ast.Name)}
        self.assertNotIn("CLIPS", assigned)

    def test_clip_rows_are_rejected_and_preserve_previous_output(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            destination = directory / "poses.json"
            destination.write_bytes(b"previous durable output\n")
            data = fixture("poses")
            data["clips"] = {"walk": dict(frames=[data["rest"]] * 4, fps=30, action="walk", source="fixture.blend")}
            with self.assertRaisesRegex(ValueError, "Unexpected clip rows"):
                export_publication("poses", directory, data)
            self.assertEqual(b"previous durable output\n", destination.read_bytes())

    def test_partial_write_preserves_previous_output_for_both_exporters(self):
        original_write = Path.write_text
        for kind in ("poses", "model-frames"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as folder:
                directory = Path(folder)
                destination = directory / (kind + ".json")
                destination.write_bytes(b"previous durable output\n")

                def partial_write(path, text, *args, **kwargs):
                    original_write(path, text[:17], *args, **kwargs)
                    raise OSError("injected partial publication write")

                with patch.object(Path, "write_text", partial_write):
                    with self.assertRaisesRegex(OSError, "partial publication"):
                        export_publication(kind, directory, fixture(kind))
                self.assertEqual(b"previous durable output\n", destination.read_bytes())
                self.assertEqual([destination], list(directory.iterdir()))

    def test_success_publishes_complete_output_for_both_exporters(self):
        for kind in ("poses", "model-frames"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as folder:
                directory = Path(folder)
                data = fixture(kind)
                export_publication(kind, directory, data)
                self.assertEqual(data, json.loads((directory / (kind + ".json")).read_text()))

    def test_invalid_complete_output_preserves_previous_output(self):
        for kind in ("poses", "model-frames"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as folder:
                directory = Path(folder)
                destination = directory / (kind + ".json")
                destination.write_bytes(b"previous durable output\n")
                data = copy.deepcopy(fixture(kind))
                data["names"] = ["Hips", "Hips"]
                with self.assertRaises(ValueError):
                    export_publication(kind, directory, data)
                self.assertEqual(b"previous durable output\n", destination.read_bytes())

    def test_replace_failure_preserves_previous_output_and_cleans_staging(self):
        for kind in ("poses", "model-frames"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as folder:
                directory = Path(folder)
                destination = directory / (kind + ".json")
                destination.write_bytes(b"previous durable output\n")
                with patch("kimodo_pipeline.motionbricks_publication.os.replace", side_effect=OSError("replace denied")):
                    with self.assertRaisesRegex(OSError, "replace denied"):
                        export_publication(kind, directory, fixture(kind))
                self.assertEqual(b"previous durable output\n", destination.read_bytes())
                self.assertEqual([destination], list(directory.iterdir()))

    def test_incomplete_or_nonfinite_payload_is_not_published(self):
        for kind in ("poses", "model-frames"):
            for invalid in ("short", "nonfinite"):
                with self.subTest(kind=kind, invalid=invalid), tempfile.TemporaryDirectory() as folder:
                    directory = Path(folder)
                    destination = directory / (kind + ".json")
                    destination.write_bytes(b"previous durable output\n")
                    data = copy.deepcopy(fixture(kind))
                    if kind == "poses":
                        row = data["rest"][-1]
                    else:
                        row = data["offsets"][-1][-1]
                    if invalid == "short":
                        row.pop()
                    else:
                        row[0] = float("nan")
                    with self.assertRaises(ValueError):
                        export_publication(kind, directory, data)
                    self.assertEqual(b"previous durable output\n", destination.read_bytes())


if __name__ == "__main__":
    unittest.main()
