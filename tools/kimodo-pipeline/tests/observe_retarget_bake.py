"""Transparent bake-entry observation for pre-bake refusal integration tests."""
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from kimodo_pipeline import blender_retargeting

original_bake = blender_retargeting.bake_action


def observed_bake(*args, **kwargs):
    print("KIMODO_TEST_BAKE_ENTRY", flush=True)
    return original_bake(*args, **kwargs)


blender_retargeting.bake_action = observed_bake
try:
    blender_retargeting.main()
except Exception as error:
    print(f"Kimodo retarget failed: {error}", file=sys.stderr)
    raise SystemExit(2)
