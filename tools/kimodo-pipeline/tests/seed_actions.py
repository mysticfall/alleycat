"""Seed disposable Blender test copies with Actions that materialisation must handle."""
from __future__ import annotations

import sys

import bpy


def main() -> None:
    arguments = sys.argv[sys.argv.index("--") + 1:]
    blend, names = arguments[0], arguments[1:]
    bpy.ops.wm.open_mainfile(filepath=blend, load_ui=False, use_scripts=False)
    for name in names:
        bpy.data.actions.new(name).use_fake_user = True
    bpy.ops.wm.save_as_mainfile(filepath=blend, check_existing=False)


main()
