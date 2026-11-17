"""Structural check of the current published external-idle catalogue, not motion quality."""
import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]


def test_published_idle_catalogue_declares_only_idle_without_stitch_edges() -> None:
    catalogue = json.loads((ROOT / "game/data/motionbricks/catalogue.json").read_text(encoding="utf-8"))
    # This is the stage catalogue consumed by MotionCatalogue, not a packaged clip/stitch transport schema.
    assert set(catalogue) == {"variants"}
    assert catalogue["variants"]
    for variant in catalogue["variants"]:
        assert set(variant) == {"name", "stages"}
        assert variant["stages"]
        for stage in variant["stages"]:
            assert set(stage) == {"action", "speed", "entry", "exit"}
            assert stage["action"] == "idle"
            assert stage["speed"] == 0
