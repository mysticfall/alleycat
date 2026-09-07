"""Blender CLI entry point for staged ANIM-002 materialisation."""
from __future__ import annotations
import argparse
import json
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from kimodo_pipeline.materialise import MaterialisationError, materialise


def main() -> int:
    parser = argparse.ArgumentParser(description="Materialise a Kimodo ANIM-002 locomotion set.")
    parser.add_argument("--blend", required=True); parser.add_argument("--variant", required=True)
    parser.add_argument("--temp-root"); parser.add_argument("--tuning", help="JSON file with optional span_cap and span_weight scoring values.")
    parser.add_argument("--roles", help="Comma-separated exact ANIM-002 registry role codes to update atomically.")
    parser.add_argument("--output", help="Distinct materialised copy; required with repair/report binding.")
    parser.add_argument("--source-repair-report", help="Report binding --blend to immutable repaired precursor bytes.")
    parser.add_argument("--report", help="Materialisation report linking precursor and final carrier bytes.")
    options = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
    try:
        tuning = json.loads(Path(options.tuning).read_text(encoding="utf-8")) if options.tuning else None
        if tuning is not None and not isinstance(tuning, dict): raise ValueError("tuning JSON must be an object")
        roles = None if options.roles is None else options.roles.split(",")
        report = materialise(
            options.blend, options.variant, options.temp_root, tuning, roles,
            output_path=options.output,
            source_repair_report_path=options.source_repair_report,
            materialisation_report_path=options.report,
        )
    except (MaterialisationError, ValueError) as error:
        print(f"Kimodo materialisation failed: {error}", file=sys.stderr); return 2
    print("KIMODO_MATERIALISATION_JSON=" + json.dumps(report, sort_keys=True)); return 0


if __name__ == "__main__": raise SystemExit(main())
