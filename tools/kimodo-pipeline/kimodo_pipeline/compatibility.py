"""Minimal saved-RNA compatibility for read-only Kimodo segment discovery."""
from __future__ import annotations

import bpy
from bpy.props import CollectionProperty, IntProperty, PointerProperty, StringProperty


class KimodoPipelineMotionSegment(bpy.types.PropertyGroup):
    """The saved fields required to observe source segment semantics and bounds."""

    prompt: StringProperty()
    start_frame: IntProperty()
    end_frame: IntProperty()


class KimodoPipelineSettings(bpy.types.PropertyGroup):
    motion_segments: CollectionProperty(type=KimodoPipelineMotionSegment)


_owns_registration = False


def ensure_registered() -> bool:
    """Register a fallback schema only when the Kimodo add-on schema is absent."""
    global _owns_registration
    if hasattr(bpy.types.Scene, "kimodo"):
        return False
    bpy.utils.register_class(KimodoPipelineMotionSegment)
    bpy.utils.register_class(KimodoPipelineSettings)
    bpy.types.Scene.kimodo = PointerProperty(type=KimodoPipelineSettings)
    _owns_registration = True
    return True


def unregister() -> None:
    """Remove only this module's fallback registration."""
    global _owns_registration
    if not _owns_registration:
        return
    del bpy.types.Scene.kimodo
    bpy.utils.unregister_class(KimodoPipelineSettings)
    bpy.utils.unregister_class(KimodoPipelineMotionSegment)
    _owns_registration = False
