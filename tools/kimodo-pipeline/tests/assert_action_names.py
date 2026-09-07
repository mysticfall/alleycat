"""Assert Action presence and absence in a disposable Blender test copy."""
from __future__ import annotations

import sys

import bpy


def main() -> None:
    arguments = sys.argv[sys.argv.index("--") + 1:]
    present, absent = arguments[arguments.index("--present") + 1], arguments[arguments.index("--absent") + 1]
    names = {action.name for action in bpy.data.actions}
    assert present in names, f"expected Action was removed: {present}"
    assert absent not in names, f"stale Action was retained: {absent}"


main()
