from __future__ import annotations

import subprocess
import sys
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory


REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
COMMAND_PATH = (
    REPOSITORY_ROOT
    / "tools"
    / "gdscript-api-doc-generator"
    / "commands"
    / "generate_gdscript_api_docs.sh"
)


class GDScriptApiDocsCommandSmokeTests(unittest.TestCase):
    def test_help_is_available_from_an_unrelated_working_directory(self) -> None:
        with TemporaryDirectory() as temporary_directory:
            completed = subprocess.run(
                [str(COMMAND_PATH), "--help"],
                cwd=temporary_directory,
                capture_output=True,
                check=False,
                text=True,
            )

        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertIn("Generate concise Markdown API docs for GDScript files.", completed.stdout)
        self.assertIn("--include-private", completed.stdout)


if __name__ == "__main__":
    unittest.main()
