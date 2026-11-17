"""Atomically bind reviewed sources and explicit raw repetition selections; no source processing or admission."""
import argparse
import json
import math
import os
from pathlib import Path
import tempfile
from kimodo_pipeline.reviewed_source import validate_bundle, sha, require, blocks, ROLES

def publish(selection, destination):
    destination=Path(destination).resolve()
    require(destination.suffix=='.json', 'Set destination must be JSON')
    root=destination.parent
    require(all(destination != (root/r['source']).resolve() for r in selection['roles']), 'Cannot overwrite a source bundle')
    entries=[]
    for row in selection['roles']:
        source=root/row['source']
        require(source.resolve().parent==root, 'Sources must be beside the set, not task dependencies')
        bundle=json.loads(source.read_text()); validate_bundle(bundle)
        key=row['variant']+'/'+row['stage']+'/'+row['role']
        role=next(r for k,r in blocks(bundle['descriptor']) if k==key)
        first,end=row['first'],row['end_exclusive']
        require(type(first) is int and type(end) is int, 'Integer source frame identities required')
        require(role['usable'][0]<=first<end<=role['usable'][1], 'Repetition outside approved range')
        require(row['kind'] in ('gait_cycle','angular_episode') and row['reason'], 'Explicit experimental selection required')
        samples=bundle['samples']; offset=bundle['descriptor']['source']['frames'][0]
        a,z=[samples[f-offset] for f in (first,end)]
        yaw=z['heading_radians']-a['heading_radians'];c,s=math.cos(yaw),math.sin(yaw)
        x,y,zp=a['root_m']; X,Y,Z=z['root_m']
        cycle=dict(schema='anim002_experimental_raw_cycle_v1',status='EXPERIMENTAL_NOT_ADMITTED',
          bundle_sha256=sha(source),variant=row['variant'],stage=row['stage'],role=row['role'],fps=30,
          first=first,end_exclusive=end,period_frames=end-first,endpoint_policy='next_boundary_reference_not_duplicated',
          source_processing='none',cycle_transform={'translation_m':[X-c*x-s*zp,0,Z+s*x-c*zp],'yaw_radians':yaw},
          repetition_kind=row['kind'],selection_reason=row['reason'])
        entries.append(dict(source=row['source'],sha256=sha(source),cycle=cycle))
    require(len(entries)==8 and {r['cycle']['role'] for r in entries}==set(ROLES), 'All eight unique roles required')
    require(len({(r['cycle']['variant'],r['cycle']['stage']) for r in entries})==1, 'One explicit variant/stage per set')
    data=dict(schema='anim002_reviewed_set_v1',roles=entries,disposition='HUMAN_ACCEPTED_SOURCES_EXPERIMENTAL_REPETITION')
    root.mkdir(parents=True,exist_ok=True)
    fd,name=tempfile.mkstemp(dir=root,prefix='.reviewed-set-',suffix='.json')
    try:
        with os.fdopen(fd,'w') as f: json.dump(data,f,separators=(',',':'));f.flush();os.fsync(f.fileno())
        require(json.loads(Path(name).read_text())==data,'Set readback mismatch')
        os.replace(name,destination)
    finally: Path(name).unlink(missing_ok=True)

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--selection',type=Path,required=True);p.add_argument('--destination',type=Path,required=True)
    a=p.parse_args();publish(json.loads(a.selection.read_text()),a.destination)
