"""Atomic set publication and missing-role refusal, using the accepted durable source bundles, not gait scoring."""
from contextlib import chdir
import hashlib
import json
from pathlib import Path
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from publish_reviewed_set import publish

class ReviewedSetTests(unittest.TestCase):
    def test_complete_set_and_refusal_preserve_previous_valid_bytes(self):
        repo = Path(__file__).resolve().parents[3]
        task = repo / "game/temp/motionbricks/reviewed-set-tests"
        task.mkdir(parents=True, exist_ok=True)
        feminine = repo / "game/assets/animations/locomotion/feminine"
        authoring = feminine / "source"
        # Immutable approval paths must resolve from the disposable publication CWD.
        historical = Path("game/assets/characters/animations/source/locomotion")
        with tempfile.TemporaryDirectory(dir=task) as name:
            root = Path(name)
            saved = json.loads((feminine / "set.json").read_text())
            for source in {entry["source"] for entry in saved["roles"]}:
                bundle = root / source
                shutil.copyfile(feminine / source, bundle)
                for path, digest in json.loads(bundle.read_text())["review"]["evidence_sha256"].items():
                    evidence = Path(path).relative_to(historical)
                    self.assertEqual(evidence, Path(evidence.name))
                    mirror = root / historical / evidence
                    mirror.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(authoring / evidence, mirror)
                    self.assertEqual(hashlib.sha256(mirror.read_bytes()).hexdigest(), digest)
            selection = {"roles": [
                dict(source=entry["source"], **{key: entry["cycle"][key] for key in
                     ("variant", "stage", "role", "first", "end_exclusive")},
                     kind=entry["cycle"]["repetition_kind"], reason=entry["cycle"]["selection_reason"])
                for entry in saved["roles"]
            ]}
            destination = root / "set.json"
            with chdir(root):
                publish(selection, destination)
                before = destination.read_bytes()
                result = json.loads(before)
                self.assertEqual(len(result["roles"]), 8)
                self.assertEqual(result["roles"][-1]["cycle"]["repetition_kind"], "angular_episode")
                selection["roles"].pop()
                with self.assertRaisesRegex(ValueError, "All eight unique roles required"):
                    publish(selection, destination)
                self.assertEqual(destination.read_bytes(), before)

if __name__=='__main__': unittest.main()
