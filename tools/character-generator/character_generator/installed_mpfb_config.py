"""Resolve MPFB's installed user-config directory inside a Blender runtime.

The generator, the saved-output validator, and their test fixtures use this
module to reach the enabled MPFB extension's public location service. The
module imports no Blender or MPFB code at import time; resolution requires an
enabled MPFB add-on in the running Blender session.
"""

from __future__ import annotations

import sys
from importlib import import_module
from pathlib import Path


def _location_service():
    """Return the location service of the enabled MPFB extension."""

    module_names = sorted(
        name for name in sys.modules
        if name.startswith("bl_ext.") and name.endswith(".mpfb")
    )
    if not module_names:
        raise RuntimeError(
            "The MPFB add-on is not enabled, so its user config directory cannot "
            "be resolved. Install and enable MPFB, then rerun this tool."
        )
    try:
        return import_module(f"{module_names[0]}.services.locationservice").LocationService
    except ImportError as exc:
        raise RuntimeError(
            "The enabled MPFB add-on does not provide its locations service, so "
            "its user config directory cannot be resolved."
        ) from exc


def resolve_config_dir() -> Path:
    """Return the MPFB user config directory holding installed human presets."""

    config_dir = _location_service().get_user_config()
    if not isinstance(config_dir, str) or not config_dir:
        raise RuntimeError(
            "MPFB did not provide its user config directory. Ensure the MPFB "
            "add-on is installed and enabled, then rerun this tool."
        )
    path = Path(config_dir)
    if not path.is_dir():
        raise RuntimeError(
            f"The MPFB user config directory does not exist: {path}. Ensure MPFB "
            "is installed and has initialised its user data."
        )
    return path
