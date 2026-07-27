import importlib.util
from pathlib import Path
import sys
import json
import tempfile
import unittest


MODULE_PATH = Path(__file__).resolve().parents[1] / "process_mixamo_animations.py"
SPEC = importlib.util.spec_from_file_location("process_mixamo_animations", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class SourceLoopActionNameTests(unittest.TestCase):
    def test_source_loop_marker_uses_only_persisted_pose_seam_intent(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "metrics.json"
            path.write_text(json.dumps({"loop_intent": {"effective_loop_intent": True}}), encoding="utf-8")
            self.assertEqual("walk-loop", MODULE.source_action_name("walk", path))
            path.write_text(json.dumps({"loop_intent": {"effective_loop_intent": False}}), encoding="utf-8")
            self.assertEqual("walk", MODULE.source_action_name("walk", path))

    def test_source_loop_marker_rejects_missing_or_non_boolean_intent(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "metrics.json"
            path.write_text(json.dumps({"loop_intent": {}}), encoding="utf-8")
            with self.assertRaisesRegex(MODULE.ScriptError, "persisted effective loop intent"):
                MODULE.source_action_name("turn", path)
