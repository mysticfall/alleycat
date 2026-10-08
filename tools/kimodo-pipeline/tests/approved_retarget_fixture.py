"""Synthetic consumer fixture with independently frozen extracted setup approval."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import tempfile

from synthetic_kimodo_blend import FIXTURE_VARIANT, SyntheticKimodoBlendBuilder

ROOT = Path(__file__).resolve().parents[3]


class ApprovedRetargetBuilder(SyntheticKimodoBlendBuilder):
    """Freeze an extracted approved setup before consumer source mutations."""

    def build_bootstrap_monolith(self, output: Path, target_rig_name: str = "Female"):
        result = super().build_bootstrap_monolith(output, target_rig_name=target_rig_name)
        if result.returncode != 0:
            return result
        from test_blender_bootstrap_workspace_smoke import _workspace_fixture
        from kimodo_pipeline.bootstrap_paths import derive_artefact_paths

        paths = derive_artefact_paths(output.parent, FIXTURE_VARIANT, ROOT)
        paths.character_setup.parent.mkdir(parents=True, exist_ok=True)
        # Publication fixtures live beneath assets/testing for Godot import, but
        # manifest/extraction scratch must still stay beneath game/temp.
        evidence = ROOT / "game/temp/wp6-b02-fix"
        evidence.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="approval-fixture-", dir=evidence) as directory:
            fixture = _workspace_fixture(Path(directory))
            shutil.copy2(fixture["setup"], paths.character_setup)
        if target_rig_name != "Female":
            renamed = subprocess.run([
                "blender", "--background", "--factory-startup", str(paths.character_setup),
                "--python-expr", "import bpy; bpy.data.objects['Female'].name=" + repr(target_rig_name)
                + "; bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath,check_existing=False)",
            ], cwd=ROOT, text=True, capture_output=True, check=False, timeout=120)
            assert renamed.returncode == 0, renamed.stdout + renamed.stderr
        baseline = {"fixture_character": {
            key: hashlib.sha256(getattr(paths, key).read_bytes()).hexdigest()
            for key in ("canonical_source", "neutral_template", "character_setup")
        }}
        (output.parent / "approved-baseline.json").write_text(json.dumps(baseline), encoding="utf-8")
        return result
