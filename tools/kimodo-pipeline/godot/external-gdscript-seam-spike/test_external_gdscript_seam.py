"""Executable proof that an external GDScript retains the game res:// namespace."""
from __future__ import annotations

from pathlib import Path
import os
import shutil
import subprocess
import tempfile
import unittest


SPIKE_DIRECTORY = Path(__file__).resolve().parent
REPOSITORY_ROOT = SPIKE_DIRECTORY.parents[3]
GAME_DIRECTORY = REPOSITORY_ROOT / "game"
RUNNER_SCRIPT = SPIKE_DIRECTORY / "run_external_gdscript_seam.gd"
SUCCESS_MARKER = "KIMODO_EXTERNAL_GDSCRIPT_SEAM=ok"


class ExternalGDScriptSeamSpikeTests(unittest.TestCase):
    def test_external_script_loads_external_helper_and_project_resource(self) -> None:
        result = self._run("--probe-id=external-path-proof")

        self.assertEqual(result.returncode, 0, self._output(result))
        self.assertIn(SUCCESS_MARKER, result.stdout)
        self.assertIn(f"helper={SPIKE_DIRECTORY / 'external_helper.gd'}", result.stdout)
        self.assertIn("resource=res://default_bus_layout.tres", result.stdout)

    def test_external_script_rejects_missing_required_user_argument(self) -> None:
        result = self._run()

        self.assertEqual(result.returncode, 2, self._output(result))
        self.assertIn("required user argument --probe-id=<value> is missing", result.stderr)

    def _run(self, *user_arguments: str) -> subprocess.CompletedProcess[str]:
        godot_mono = os.environ.get("GODOT_MONO_BIN", "godot-mono")
        if shutil.which(godot_mono) is None:
            self.fail(f"Godot Mono executable is unavailable: {godot_mono}")

        command = [
            godot_mono,
            "--xr-mode",
            "off",
            "--path",
            str(GAME_DIRECTORY),
            "--script",
            str(RUNNER_SCRIPT),
            "--",
            *user_arguments,
        ]
        with tempfile.TemporaryDirectory(prefix="kimodo-external-gdscript-seam-") as cwd:
            return subprocess.run(
                command,
                cwd=cwd,
                text=True,
                capture_output=True,
                check=False,
            )

    @staticmethod
    def _output(result: subprocess.CompletedProcess[str]) -> str:
        return f"stdout:\n{result.stdout}\nstderr:\n{result.stderr}"


if __name__ == "__main__":
    unittest.main()
