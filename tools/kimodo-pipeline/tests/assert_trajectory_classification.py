"""Assert legacy Segment-4 transition codes cannot select lateral projection."""
from __future__ import annotations

from pathlib import Path
import sys


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from kimodo_pipeline.materialise import _trajectory


def main() -> None:
    for code in ("i2xr", "xr2i", "i2xl", "xl2i"):
        assert _trajectory(code) != ("lateral", "zero_yaw"), code
    for code in ("xr_l", "xr_r", "xl_l", "xl_r", "sl", "sr"):
        assert _trajectory(code) == ("lateral", "zero_yaw"), code


main()
