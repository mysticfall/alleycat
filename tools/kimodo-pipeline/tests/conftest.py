"""Opt-in profiling lifecycle for Blender-backed Kimodo tests."""
from __future__ import annotations

import os
from pathlib import Path
import shutil
import sys

def _pipeline_root() -> Path:
    for candidate in Path(__file__).resolve().parents:
        if (candidate / "kimodo_pipeline").is_dir() and (candidate / "config").is_dir():
            return candidate
    raise RuntimeError("Kimodo pipeline package root could not be located.")


sys.path.insert(0, str(_pipeline_root()))
from kimodo_pipeline.blender_cli import (
    PROFILE_BASETEMP_ENVIRONMENT_VARIABLE,
    PROFILE_DIRECTORY_ENVIRONMENT_VARIABLE,
    clear_pytest_blender_profiling,
    configure_pytest_blender_profiling,
)


def pytest_addoption(parser):
    parser.addoption(
        "--kimodo-profile",
        action="store_true",
        default=False,
        help="capture one cProfile artefact for each Blender test subprocess",
    )


def pytest_sessionstart(session):
    # An invoking shell must not be able to activate profiling accidentally.
    os.environ.pop(PROFILE_DIRECTORY_ENVIRONMENT_VARIABLE, None)
    os.environ.pop(PROFILE_BASETEMP_ENVIRONMENT_VARIABLE, None)
    clear_pytest_blender_profiling()
    if not session.config.getoption("--kimodo-profile"):
        return

    base_temporary_directory = session.config._tmp_path_factory.getbasetemp().resolve()
    profile_directory = base_temporary_directory / "kimodo-blender-profiles"
    if profile_directory.exists():
        if profile_directory.is_symlink() or profile_directory.parent.resolve() != base_temporary_directory:
            raise RuntimeError(f"Refusing to remove unsafe profile directory: {profile_directory}")
        shutil.rmtree(profile_directory)
    profile_directory.mkdir()
    configure_pytest_blender_profiling(profile_directory, base_temporary_directory)
    session.config._kimodo_profile_directory = profile_directory


def pytest_sessionfinish(session, exitstatus):
    clear_pytest_blender_profiling()
    os.environ.pop(PROFILE_DIRECTORY_ENVIRONMENT_VARIABLE, None)
    os.environ.pop(PROFILE_BASETEMP_ENVIRONMENT_VARIABLE, None)
