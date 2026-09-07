"""Run one Blender test helper under cProfile without changing its argv."""
from __future__ import annotations

import argparse
import cProfile
import os
from pathlib import Path
import runpy
import sys


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--target", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--basetemp", required=True)
    parser.add_argument("target_arguments", nargs=argparse.REMAINDER)
    options = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    target_arguments = options.target_arguments
    if target_arguments[:1] == ["--"]:
        target_arguments = target_arguments[1:]

    output = Path(options.output)
    basetemp = Path(options.basetemp).resolve(strict=True)
    output_parent = output.parent.resolve(strict=True)
    try:
        output_parent.relative_to(basetemp)
    except ValueError as error:
        raise RuntimeError("Kimodo profile output escapes pytest basetemp") from error
    if output_parent == basetemp or os.path.lexists(output):
        raise RuntimeError("Kimodo profile output is not a new artefact beneath pytest basetemp")
    profiler = cProfile.Profile()
    previous_argv = sys.argv
    try:
        # Blender scripts conventionally parse arguments following its `--`
        # delimiter, so retain that observable argv contract under the wrapper.
        sys.argv = [options.target, "--", *target_arguments]
        profiler.runcall(runpy.run_path, options.target, run_name="__main__")
    finally:
        sys.argv = previous_argv
        profiler.dump_stats(str(output))


if __name__ == "__main__":
    main()
