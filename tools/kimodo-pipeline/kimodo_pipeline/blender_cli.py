"""Blender subprocess helpers for Kimodo pipeline tests."""
from __future__ import annotations

import os
from pathlib import Path
import subprocess
import json
import re
import time
from typing import Sequence


TOOL_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_DEPENDENCY_DIRECTORY = TOOL_ROOT / ".blender-python-deps"
DEPENDENCY_DIRECTORY_ENVIRONMENT_VARIABLE = "KIMODO_BLENDER_PYTHON_DEPS"
PROFILE_DIRECTORY_ENVIRONMENT_VARIABLE = "KIMODO_BLENDER_PROFILE_DIRECTORY"
PROFILE_BASETEMP_ENVIRONMENT_VARIABLE = "KIMODO_BLENDER_PROFILE_BASETEMP"
PROFILE_RUNNER = TOOL_ROOT / "tests/profile_blender_script.py"

_profile_directory: Path | None = None
_profile_basetemp: Path | None = None


def _dependency_directory() -> Path:
    configured = os.environ.get(DEPENDENCY_DIRECTORY_ENVIRONMENT_VARIABLE)
    return Path(configured).expanduser() if configured else DEFAULT_DEPENDENCY_DIRECTORY


def _profile_label(arguments: Sequence[str | Path]) -> str:
    """Produce a stable command label for a test-launched Blender helper."""
    values = [str(argument) for argument in arguments]
    target = Path(values[values.index("--python") + 1]).stem
    variant = ""
    if "--variant" in values:
        variant = f"-{values[values.index('--variant') + 1]}"
    return re.sub(r"[^A-Za-z0-9_.-]+", "-", f"{target}{variant}").strip("-")


def _validated_profile_directory(profile_directory: Path, basetemp: Path) -> tuple[Path, Path]:
    """Return real profile paths only when the directory is inside pytest basetemp."""
    resolved_basetemp = basetemp.resolve(strict=True)
    resolved_directory = profile_directory.resolve(strict=True)
    if not resolved_directory.is_dir() or resolved_directory == resolved_basetemp:
        raise RuntimeError("Kimodo profile directory must be a directory beneath pytest basetemp")
    try:
        resolved_directory.relative_to(resolved_basetemp)
    except ValueError as error:
        raise RuntimeError("Kimodo profile directory escapes pytest basetemp") from error
    return resolved_directory, resolved_basetemp


def configure_pytest_blender_profiling(profile_directory: Path, basetemp: Path) -> None:
    """Enable profiling only for the explicit pytest ``--kimodo-profile`` lifecycle."""
    global _profile_directory, _profile_basetemp
    _profile_directory, _profile_basetemp = _validated_profile_directory(profile_directory, basetemp)


def clear_pytest_blender_profiling() -> None:
    """Disable the process-local pytest profiling configuration."""
    global _profile_directory, _profile_basetemp
    _profile_directory = None
    _profile_basetemp = None


def _profiled_arguments(arguments: Sequence[str | Path], profile_directory: Path, basetemp: Path) -> tuple[list[str], Path, str]:
    """Wrap one Blender Python script with the opt-in cProfile runner."""
    values = [str(argument) for argument in arguments]
    python_index = values.index("--python")
    target = values[python_index + 1]
    separator_index = values.index("--", python_index + 2) if "--" in values[python_index + 2:] else len(values)
    script_arguments = values[separator_index + 1:] if separator_index < len(values) else []
    label = _profile_label(values)
    ledger_path = profile_directory / "blender-subprocess-ledger.jsonl"
    ordinal = len(ledger_path.read_text(encoding="utf-8").splitlines()) + 1 if ledger_path.is_file() else 1
    profile_path = profile_directory / f"{ordinal:04d}-{label}.pstats"
    profiled = values[:python_index] + [
        "--python",
        str(PROFILE_RUNNER),
        "--",
        "--target",
        target,
        "--output",
        str(profile_path),
        "--basetemp",
        str(basetemp),
        "--",
        *script_arguments,
    ]
    return profiled, profile_path, label


def _append_profile_ledger(profile_directory: Path, basetemp: Path, label: str, command: Sequence[str], elapsed_seconds: float, exit_status: int, profile_path: Path) -> None:
    profile_directory, basetemp = _validated_profile_directory(profile_directory, basetemp)
    if profile_path.resolve(strict=False).parent != profile_directory:
        raise RuntimeError("Kimodo profile artefact escapes the configured profile directory")
    entry = {
        "command": list(command),
        "elapsed_monotonic_seconds": elapsed_seconds,
        "exit_status": exit_status,
        "label": label,
        "profile_artifact": str(profile_path),
    }
    with (profile_directory / "blender-subprocess-ledger.jsonl").open("a", encoding="utf-8") as ledger:
        ledger.write(json.dumps(entry, sort_keys=True) + "\n")


def run_blender_python(arguments: Sequence[str | Path], cwd: Path) -> subprocess.CompletedProcess[str]:
    """Run a Blender Python command and propagate unhandled script failures."""
    dependency_directory = _dependency_directory()
    if not dependency_directory.is_dir():
        raise RuntimeError(
            f"Blender validation dependencies are missing at {dependency_directory}. "
            "Run python3 tools/kimodo-pipeline/bootstrap_blender_dependencies.py first."
        )

    command = [
        "blender",
        "--background",
        "--factory-startup",
        "--python-exit-code",
        "1",
        "--python-use-system-env",
    ]
    profile_directory = _profile_directory
    profile_basetemp = _profile_basetemp
    profile_path: Path | None = None
    label: str | None = None
    blender_arguments = list(arguments)
    if profile_directory is not None and profile_basetemp is not None:
        profile_directory, profile_basetemp = _validated_profile_directory(profile_directory, profile_basetemp)
        blender_arguments, profile_path, label = _profiled_arguments(arguments, profile_directory, profile_basetemp)
    command.extend(str(argument) for argument in blender_arguments)
    environment = os.environ.copy()
    # Never honour inherited profile switches.  The pytest lifecycle passes the
    # trusted boundary through the wrapper arguments above instead.
    environment.pop(PROFILE_DIRECTORY_ENVIRONMENT_VARIABLE, None)
    environment.pop(PROFILE_BASETEMP_ENVIRONMENT_VARIABLE, None)
    previous_python_path = environment.get("PYTHONPATH")
    environment["PYTHONPATH"] = (
        str(dependency_directory)
        if not previous_python_path
        else f"{dependency_directory}{os.pathsep}{previous_python_path}"
    )
    started = time.monotonic()
    result = subprocess.run(command, cwd=cwd, env=environment, text=True, capture_output=True, check=False)
    if profile_directory is not None and profile_basetemp is not None and profile_path is not None and label is not None:
        _append_profile_ledger(profile_directory, profile_basetemp, label, command, time.monotonic() - started, result.returncode, profile_path)
    return result
