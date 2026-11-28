"""Safe temporary-root resolution for the retained Kimodo pipeline."""
from __future__ import annotations

from pathlib import Path

from .bootstrap_paths import repository_root


def repository_temp_root() -> Path:
    """Return the repository-owned artefact boundary."""
    return repository_root() / "game" / "temp"


def resolve_temp_root(requested: str | Path | None = None) -> Path:
    """Resolve a temporary root, rejecting paths outside ``game/temp``."""
    allowed = repository_temp_root().resolve()
    candidate = allowed if requested is None else Path(requested).expanduser().resolve()
    try:
        candidate.relative_to(allowed)
    except ValueError as error:
        raise ValueError(
            f"--temp-root must resolve beneath the repository game/temp directory ({allowed}); "
            f"got {candidate}"
        ) from error
    return candidate
