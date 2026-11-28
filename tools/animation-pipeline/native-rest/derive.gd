extends SceneTree
## Bake corrected source-local digit frames from the reviewed immutable F6 donor and current import.
func _initialize():
 var dir=get_script().resource_path.get_base_dir();var args=OS.get_cmdline_user_args()
 var output=ProjectSettings.globalize_path('res://assets/animations/motionbricks/source-rest') if args.is_empty() else args[0]
 var info=JSON.parse_string(FileAccess.get_file_as_string('res://assets/animations/motionbricks/poses.json'))
 var names=JSON.parse_string(FileAccess.get_file_as_string(dir+'/semantic.json'))
 for sex in ['female','male']:
  var donor=str_to_var(FileAccess.get_file_as_string(dir+'/'+sex+'.txt'));var actor=load('res://assets/characters/reference/%s/reference_%s.blend'%[sex,sex]).instantiate();var t=actor.get_node(sex.capitalize()+'/GeneralSkeleton');var skin=t.get_node(sex.capitalize()+'_body').skin
  var src=Skeleton3D.new();src.name='SOMA';var binds={}
  for j in skin.get_bind_count():
   var k=skin.get_bind_name(j);var bind=skin.get_bind_pose(j)
   if binds.has(k) or t.find_bone(k)<0 or not valid(bind):return fail('Invalid target bind '+str(k))
   binds[k]=bind
  if donor.size()!=t.get_bone_count() or names.size()!=77:return fail('Unexpected skeleton size')
  for k in donor:
   var b=t.find_bone(k)
   if b<0:return fail('Missing target bone '+k)
   var parent=t.get_bone_parent(b);var pn=t.get_bone_name(parent) if parent>=0 else ''
   if pn!=donor[k][0]:return fail('Parent mismatch '+k)
  for j in info.names.size():
   var n=names.get(info.names[j]);var pi=int(info.parents[j]);var r=info.rest[j]
   if n==null or src.find_bone(n)>=0 or pi>=j or (pi<0 and j!=0):return fail('Invalid canonical hierarchy')
   src.add_bone(n);src.set_bone_parent(j,pi);src.set_bone_rest(j,Transform3D(Basis(Quaternion(r[3],r[4],r[5],r[6]).normalized()),Vector3(r[0],r[1],r[2])))
   if not (n.ends_with('Distal') or n.ends_with('Intermediate') or n.ends_with('Proximal') or n.ends_with('Metacarpal')):continue
   var b=t.find_bone(n)
   if b<0:return fail('Missing digit '+n)
   var pn=t.get_bone_name(t.get_bone_parent(b))
   for k in [n,pn]:
    if not binds.has(k) or not valid(donor[k][2]) or not valid(donor[k][1]):return fail('Missing digit/parent bind '+k)
   var ak=t.get_bone_global_rest(b);var ap=t.get_bone_global_rest(t.find_bone(pn));var sp=src.get_bone_global_rest(pi).basis
   var goal=donor[n][1]*(donor[n][2]*binds[n].affine_inverse());var fp=donor[pn][1]*donor[pn][2]*binds[pn].affine_inverse()
   var pos=Vector3(r[0],r[1],r[2])-(ap.basis.inverse()*sp).inverse()*((fp.affine_inverse()*goal).origin-t.get_bone_rest(b).origin)/t.motion_scale
   src.set_bone_rest(j,Transform3D(sp.inverse()*(ak.basis*goal.basis.inverse()).orthonormalized(),pos))
  var packed=PackedScene.new();var code=packed.pack(src)
  if code!=OK or ResourceSaver.save(packed,output+'/'+sex+'.tscn')!=OK:return fail('Cannot save '+sex)
  actor.free();src.free()
 quit()
func valid(x):return x is Transform3D and x.is_finite() and abs(x.basis.determinant())>0.000001
func fail(message):push_error(message);quit(1)
