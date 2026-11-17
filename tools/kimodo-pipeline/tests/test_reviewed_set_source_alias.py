"""AR-B02: resolved aliases may not overwrite a source; exercise only disposable adjacent source copies."""
import json
from pathlib import Path
import shutil
import sys
import unittest
import uuid

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from publish_reviewed_set import publish

class ReviewedSetSourceAliasTests(unittest.TestCase):
    def test_AR_B02_resolved_dot_alias_refuses_and_preserves_all_source_bytes(self):
        repo=Path(__file__).resolve().parents[3]
        root=repo/'game/temp/motionbricks/anim002-regenerated-source-adoption-01'/('publisher-alias-'+uuid.uuid4().hex)
        root.mkdir(parents=True,exist_ok=False)
        saved=json.loads((repo/'game/data/motionbricks/reviewed-feminine/set.json').read_text())
        sources={entry['source'] for entry in saved['roles']}
        for source in sources:
            shutil.copyfile(repo/'game/data/motionbricks/reviewed-feminine'/source,root/source)
        selection={'roles':[dict(source='./'+e['source'],**{k:e['cycle'][k] for k in
          ('variant','stage','role','first','end_exclusive')},kind=e['cycle']['repetition_kind'],
          reason=e['cycle']['selection_reason']) for e in saved['roles']]}
        before={source:(root/source).read_bytes() for source in sources}
        refused=False
        try:
            publish(selection,root/saved['roles'][0]['source'])
        except ValueError as error:
            refused=True
            message=str(error)
        else:
            message='Publisher returned successfully and replaced a disposable source copy'
        same={source:(root/source).read_bytes()==data for source,data in before.items()}
        with (root/'result.json').open('x') as f:
            json.dump(dict(contract='AR-B02 resolved source alias refusal',refused=refused,
                exact_source_bytes_unchanged=same,message=message),f,indent=2)
        self.assertTrue(refused,'AR-B02: resolved source alias was not refused before publication')
        self.assertEqual(same,{source:True for source in sources})

if __name__=='__main__': unittest.main()
