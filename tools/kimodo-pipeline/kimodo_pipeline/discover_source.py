"""Blender CLI entry point for read-only Kimodo source discovery."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from kimodo_pipeline.discovery import DiscoveryError, discover


def _arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Read-only Kimodo source discovery.")
    parser.add_argument("--blend", required=True, help="Path to the source .blend file.")
    parser.add_argument("--variant", required=True, help="Lower-case snake_case variant key.")
    arguments = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    return parser.parse_args(arguments)


def main() -> int:
    arguments = _arguments()
    try:
        report = discover(arguments.blend, arguments.variant)
    except (DiscoveryError, ValueError) as error:
        print(f"Kimodo discovery failed: {error}", file=sys.stderr)
        return 2
    print("KIMODO_DISCOVERY_JSON=" + json.dumps(report, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
