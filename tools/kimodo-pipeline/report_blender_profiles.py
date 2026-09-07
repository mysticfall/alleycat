"""Aggregate opt-in Kimodo Blender cProfile artefacts and wall-time ledger."""
from __future__ import annotations

import argparse
import io
import json
from pathlib import Path
import pstats


def _print_stats(profile_paths: list[Path], sort_key: pstats.SortKey, heading: str, limit: int) -> None:
    stream = io.StringIO()
    statistics = pstats.Stats(*(str(path) for path in profile_paths), stream=stream)
    statistics.strip_dirs().sort_stats(sort_key).print_stats(limit)
    print(f"\n{heading}\n{stream.getvalue().rstrip()}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile-dir", type=Path, required=True)
    parser.add_argument("--limit", type=int, default=20)
    options = parser.parse_args()
    profile_directory = options.profile_dir.resolve()
    profile_paths = sorted(profile_directory.glob("*.pstats"))
    ledger_path = profile_directory / "blender-subprocess-ledger.jsonl"
    entries = [json.loads(line) for line in ledger_path.read_text(encoding="utf-8").splitlines()] if ledger_path.is_file() else []
    total_wall_time = sum(entry["elapsed_monotonic_seconds"] for entry in entries)
    print(f"Profile directory: {profile_directory}")
    print(f"Blender launches: {len(entries)}")
    print(f"Recorded Blender wall time: {total_wall_time:.3f}s")
    print(f"Profile artefacts: {len(profile_paths)}")
    for entry in entries:
        print(
            f"  {entry['label']}: {entry['elapsed_monotonic_seconds']:.3f}s, "
            f"exit {entry['exit_status']}, {entry['profile_artifact']}"
        )
    if not profile_paths:
        return
    _print_stats(profile_paths, pstats.SortKey.CUMULATIVE, "Top Cumulative Costs", options.limit)
    _print_stats(profile_paths, pstats.SortKey.TIME, "Top Internal Costs", options.limit)


if __name__ == "__main__":
    main()
