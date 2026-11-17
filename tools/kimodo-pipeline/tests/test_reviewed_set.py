"""Atomic set publication and missing-role refusal, using the accepted durable source bundles, not gait scoring."""
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
        repo=Path(__file__).resolve().parents[3]
        task=repo/'game/temp/motionbricks/reviewed-set-tests'
        task.mkdir(parents=True,exist_ok=True)
        with tempfile.TemporaryDirectory(dir=task) as name:
            root=Path(name)
            saved=json.loads((repo/'game/data/motionbricks/reviewed-feminine/set.json').read_text())
            for source in {entry['source'] for entry in saved['roles']}:
                shutil.copyfile(repo/'game/data/motionbricks/reviewed-feminine'/source,root/source)
            selection={'roles':[dict(source=e['source'],**{k:e['cycle'][k] for k in
              ('variant','stage','role','first','end_exclusive')},kind=e['cycle']['repetition_kind'],
              reason=e['cycle']['selection_reason']) for e in saved['roles']]}
            destination=root/'set.json'
            publish(selection,destination)
            before=destination.read_bytes()
            result=json.loads(before)
            self.assertEqual(len(result['roles']),8)
            self.assertEqual(result['roles'][-1]['cycle']['repetition_kind'],'angular_episode')
            selection['roles'].pop()
            with self.assertRaises(ValueError): publish(selection,destination)
            self.assertEqual(destination.read_bytes(),before)

if __name__=='__main__': unittest.main()
