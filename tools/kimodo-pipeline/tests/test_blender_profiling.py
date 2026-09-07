"""Boundary tests for opt-in Blender subprocess profiling."""
from __future__ import annotations

import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline import blender_cli


class BlenderProfilingTests(unittest.TestCase):
    def setUp(self) -> None:
        blender_cli.clear_pytest_blender_profiling()
        self.addCleanup(blender_cli.clear_pytest_blender_profiling)

    def _run_with_mocked_blender(self, dependency_directory: Path) -> tuple[subprocess.CompletedProcess[str], list]:
        calls = []

        def run(command, **kwargs):
            calls.append((command, kwargs))
            return subprocess.CompletedProcess(command, 0, "", "")

        with patch.object(blender_cli, "_dependency_directory", return_value=dependency_directory), patch.object(
            blender_cli.subprocess, "run", side_effect=run
        ):
            result = blender_cli.run_blender_python(["--python", "target.py"], ROOT)
        return result, calls

    def test_inherited_profile_environment_does_not_activate_profiling(self) -> None:
        with tempfile.TemporaryDirectory() as directory, patch.dict(
            os.environ, {blender_cli.PROFILE_DIRECTORY_ENVIRONMENT_VARIABLE: directory}, clear=False
        ):
            result, calls = self._run_with_mocked_blender(Path(directory))

        self.assertEqual(result.returncode, 0)
        command, environment = calls[0]
        self.assertNotIn(str(blender_cli.PROFILE_RUNNER), command)
        self.assertNotIn(blender_cli.PROFILE_DIRECTORY_ENVIRONMENT_VARIABLE, environment["env"])
        self.assertNotIn(blender_cli.PROFILE_BASETEMP_ENVIRONMENT_VARIABLE, environment["env"])

    def test_explicit_pytest_configuration_profiles_beneath_basetemp(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            basetemp = Path(directory) / "pytest-base"
            profile_directory = basetemp / "kimodo-blender-profiles"
            profile_directory.mkdir(parents=True)
            blender_cli.configure_pytest_blender_profiling(profile_directory, basetemp)

            result, calls = self._run_with_mocked_blender(Path(directory))

            self.assertEqual(result.returncode, 0)
            command = calls[0][0]
            self.assertIn(str(blender_cli.PROFILE_RUNNER), command)
            output = Path(command[command.index("--output") + 1]).resolve()
            self.assertTrue(output.is_relative_to(basetemp.resolve()))
            self.assertEqual(Path(command[command.index("--basetemp") + 1]), basetemp.resolve())
            ledger = profile_directory / "blender-subprocess-ledger.jsonl"
            self.assertTrue(ledger.is_file())
            self.assertTrue(ledger.resolve().is_relative_to(basetemp.resolve()))

    def test_escaping_profile_directory_is_rejected_without_creating_artefacts(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            basetemp = root / "pytest-base"
            outside = root / "outside"
            basetemp.mkdir()
            outside.mkdir()

            with self.assertRaisesRegex(RuntimeError, "escapes pytest basetemp"):
                blender_cli.configure_pytest_blender_profiling(outside, basetemp)

            self.assertEqual(list(outside.iterdir()), [])

            escaped_link = basetemp / "escaped-link"
            escaped_link.symlink_to(outside, target_is_directory=True)
            with self.assertRaisesRegex(RuntimeError, "escapes pytest basetemp"):
                blender_cli.configure_pytest_blender_profiling(escaped_link, basetemp)
            self.assertEqual(list(outside.iterdir()), [])

    def test_profile_runner_writes_an_explicit_opt_in_artefact_beneath_basetemp(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            basetemp = root / "pytest-base"
            profile_directory = basetemp / "profiles"
            target = root / "target.py"
            output = profile_directory / "target.pstats"
            basetemp.mkdir()
            profile_directory.mkdir()
            target.write_text("value = 1\n", encoding="utf-8")
            old_argv = sys.argv
            try:
                sys.argv = [
                    "profile_blender_script.py", "--", "--target", str(target), "--output", str(output),
                    "--basetemp", str(basetemp), "--",
                ]
                from profile_blender_script import main

                main()
            finally:
                sys.argv = old_argv
            self.assertTrue(output.is_file())
            self.assertTrue(output.resolve().is_relative_to(basetemp.resolve()))

    def test_profile_runner_rejects_escaping_output_before_writing(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            basetemp = root / "pytest-base"
            profile_directory = basetemp / "profiles"
            outside = root / "outside.pstats"
            basetemp.mkdir()
            profile_directory.mkdir()
            old_argv = sys.argv
            try:
                sys.argv = [
                    "profile_blender_script.py", "--", "--target", str(ROOT / "tools/kimodo-pipeline/tests/profile_blender_script.py"),
                    "--output", str(outside), "--basetemp", str(basetemp), "--",
                ]
                from profile_blender_script import main

                with self.assertRaisesRegex(RuntimeError, "escapes pytest basetemp"):
                    main()
            finally:
                sys.argv = old_argv
            self.assertFalse(outside.exists())
