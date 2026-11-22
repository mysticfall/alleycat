"""Validate complete MotionBricks JSON and atomically publish one destination at a time."""
import json
import math
import os
import tempfile
from pathlib import Path


def _require(condition, message):
    if not condition:
        raise ValueError(message)


def _numbers(values, count):
    _require(isinstance(values, list) and len(values) == count, "Invalid numeric row size")
    _require(all(type(v) in (int, float) and math.isfinite(v) for v in values), "Non-finite numeric row")


def _matrix(value, size):
    _require(isinstance(value, list) and len(value) == size, "Invalid matrix size")
    for row in value:
        _numbers(row, size)


def validate_motionbricks(data, kind):
    """Structural/integrity checks only; no generated motion quality or endpoint gates."""
    _require(isinstance(data, dict), "Expected export object")
    names = data.get("names")
    _require(isinstance(names, list) and names and all(isinstance(n, str) and n for n in names),
             "Missing joint names")
    _require(len(set(names)) == len(names) and names[0] == "Hips", "Invalid joint identity")
    count = len(names)

    def pose(rows):
        _require(isinstance(rows, list) and len(rows) == count, "Incomplete pose")
        for row in rows:
            _numbers(row, 7)
            _require(any(v != 0 for v in row[3:]), "Zero quaternion")

    if kind == "model-frames":
        offsets = data.get("offsets")
        _require(isinstance(offsets, list) and len(offsets) == count, "Incomplete model frames")
        for offset in offsets:
            _matrix(offset, 3)
        return
    _require(kind == "poses", "Unknown MotionBricks export kind")
    parents = data.get("parents")
    _require(isinstance(parents, list) and len(parents) == count and parents[0] == -1,
             "Invalid parent layout")
    for i, parent in enumerate(parents[1:], 1):
        _require(type(parent) is int and 0 <= parent < i, "Parent must precede child")
    pose(data.get("rest"))
    rests = data.get("blenderRest")
    _require(isinstance(rests, list) and len(rests) == count, "Incomplete Blender rests")
    for rest in rests:
        _matrix(rest, 4)
    _require(not ({"looks", "mapping", "targetRest"} & data.keys()), "Unexpected legacy calibration rows")
    sources = data.get("sources")
    _require(isinstance(sources, dict) and sources, "Missing source hashes")
    for path, digest in sources.items():
        _require(isinstance(path, str) and path and isinstance(digest, str) and len(digest) == 64
                 and all(c in "0123456789abcdef" for c in digest), "Invalid source hash")
    clips = data.get("clips")
    _require(isinstance(clips, dict) and "idle" in clips
             and all(isinstance(name, str) and name for name in clips), "Missing approved idle or invalid clip identity")
    for clip in clips.values():
        _require(isinstance(clip, dict) and clip.get("fps") == 30, "Invalid source sample rate")
        _require(isinstance(clip.get("action"), str) and clip["action"] and clip.get("source") in sources,
                 "Missing clip provenance")
        frames = clip.get("frames")
        _require(isinstance(frames, list) and len(frames) >= 4, "Incomplete action window")
        for frame in frames:
            pose(frame)


def publish_motionbricks(destination, data, kind):
    """Old bytes survive validation/write/replace failure; atomicity is per destination, not across exports."""
    validate_motionbricks(data, kind)
    text = json.dumps(data, separators=(",", ":"), allow_nan=False)
    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    descriptor, name = tempfile.mkstemp(prefix=f".{destination.name}.", suffix=".tmp", dir=destination.parent)
    staged = Path(name)
    try:
        os.close(descriptor)
        staged.write_text(text, encoding="utf-8")
        # Check staged bytes too: a short write must never become the durable export.
        loaded = json.loads(staged.read_text(encoding="utf-8"))
        validate_motionbricks(loaded, kind)
        _require(loaded == data, "Staged export differs from validated data")
        os.replace(staged, destination)
    finally:
        staged.unlink(missing_ok=True)
