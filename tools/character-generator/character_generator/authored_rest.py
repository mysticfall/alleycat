"""Construct the MakeHuman T-body import action without posing the live armature."""


def add_authored_rest(rig):
    import bpy
    from mathutils import Matrix, Vector

    name = "AuthoredT"
    if bpy.data.actions.get(name) is not None:
        raise ValueError(f"Refusing to replace existing {name} action")

    # Evaluate an isolated pose: a default action can key even the pelvis location.
    probe = rig.copy()
    probe.animation_data_clear()
    bpy.context.scene.collection.objects.link(probe)
    update = bpy.context.view_layer.update
    try:
        for bone in probe.pose.bones:
            bone.rotation_mode = "QUATERNION"
            bone.matrix_basis = Matrix.Identity(4)
        update()
        for side, horizontal in (("l", 1), ("r", -1)):
            for parent, child, direction in (
                ("upperarm", "lowerarm", (horizontal, 0, 0)),
                ("lowerarm", "hand", (horizontal, 0, 0)),
                ("hand", "middle_01", (horizontal, 0, 0)),
                ("thigh", "calf", (0, 0, -1)),
                ("calf", "foot", (0, 0, -1)),
            ):
                bone = probe.pose.bones[f"{parent}_{side}"]
                segment = probe.pose.bones[f"{child}_{side}"].head - bone.head
                swing = segment.rotation_difference(Vector(direction))
                matrix = bone.matrix.copy()
                bone.matrix = Matrix.LocRotScale(matrix.translation, swing @ matrix.to_quaternion(), matrix.to_scale())
                update()
            foot = probe.pose.bones[f"foot_{side}"]
            foot.matrix = Matrix.LocRotScale(foot.head, foot.bone.matrix_local.to_quaternion(), Vector((1, 1, 1)))
            update()
        transforms = {bone.name: bone.matrix_basis.copy() for bone in probe.pose.bones}
        landmarks = {bone.name: (bone.head.copy(), bone.tail.copy()) for bone in probe.pose.bones}
    finally:
        bpy.data.objects.remove(probe, do_unlink=True)

    action = bpy.data.actions.new(name)
    slot = action.slots.new("OBJECT", rig.name)
    strip = action.layers.new("Static").strips.new(type="KEYFRAME")
    bag = strip.channelbag(slot, ensure=True)
    for bone in rig.pose.bones:
        location, rotation, scale = transforms[bone.name].decompose()
        if bone.rotation_mode == "QUATERNION":
            rotation_channel = ("rotation_quaternion", rotation)
        elif bone.rotation_mode == "AXIS_ANGLE":
            rotation_channel = ("rotation_axis_angle", (rotation.angle, *rotation.axis))
        else:
            rotation_channel = ("rotation_euler", rotation.to_euler(bone.rotation_mode))
        for prop, values in (("location", location), rotation_channel, ("scale", scale)):
            for index, value in enumerate(values):
                curve = bag.fcurves.new(bone.path_from_id(prop), index=index)
                for frame in (1, 31):
                    curve.keyframe_points.insert(frame, value)
    # Decode the finished action on a separate native-mode recipient. The oracle is
    # the posed donor above, never the recipient's own evaluated animation.
    recipient = rig.copy()
    recipient.animation_data_clear()
    bpy.context.scene.collection.objects.link(recipient)
    try:
        data = recipient.animation_data_create()
        data.action = action
        data.action_slot = slot
        update()
        for name_, (head, tail) in landmarks.items():
            bone = recipient.pose.bones[name_]
            if max((bone.head - head).length, (bone.tail - tail).length) > 1e-6:
                raise ValueError(f"AuthoredT action does not reproduce donor bone {name_}")
    finally:
        bpy.data.objects.remove(recipient, do_unlink=True)
    track = rig.animation_data_create().nla_tracks.new()
    track.name = f"LinkedActionReference::{name}"
    track.mute = True
    track.strips.new(name, 1, action).action_slot = slot
