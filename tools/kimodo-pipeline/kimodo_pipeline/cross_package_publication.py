"""Journalled, generic cross-root publication for prepared package stages."""
from __future__ import annotations

from collections.abc import Callable, Iterable
from dataclasses import dataclass
import json
import os
from pathlib import Path
import shutil
import uuid


class CrossPackagePublicationError(RuntimeError):
    """A cross-root publication transaction could not complete safely."""


Validation = Callable[[Path, Path], None]


@dataclass(frozen=True)
class StagedPackage:
    """One independently validated processed/runtime package pair."""

    name: str
    processed_stage: Path
    runtime_stage: Path
    processed_root: Path
    runtime_root: Path
    validate: Validation


@dataclass(frozen=True)
class _Target:
    label: str
    stage: Path | None
    root: Path


class CrossRootPublicationTransaction:
    """Replace any number of package roots and remove legacy roots as one rollback-safe unit."""

    def __init__(
        self,
        packages: Iterable[StagedPackage],
        *,
        legacy_roots: Iterable[Path] = (),
        journal_directory: Path | None = None,
        failure_point: str | None = None,
    ) -> None:
        self._packages = tuple(packages)
        self._failure_point = failure_point
        targets: list[_Target] = []
        for package in self._packages:
            targets.extend((
                _Target(f"{package.name}:processed", package.processed_stage.resolve(), package.processed_root.resolve()),
                _Target(f"{package.name}:runtime", package.runtime_stage.resolve(), package.runtime_root.resolve()),
            ))
        targets.extend(_Target(f"legacy:{index}", None, root.resolve()) for index, root in enumerate(legacy_roots))
        self._targets = tuple(targets)
        journal_parent = (journal_directory or Path.cwd()).resolve()
        self._token = uuid.uuid4().hex
        self._journal = journal_parent / f".kimodo-publication-{self._token}.journal.json"
        self._backups = tuple(target.root.with_name(f".{target.root.name}.backup-{self._token}") for target in self._targets)
        self._recovery = tuple(target.root.with_name(f".{target.root.name}.recovery-{self._token}") for target in self._targets)
        self._had_root = tuple(target.root.exists() for target in self._targets)
        self._published = [False] * len(self._targets)

    def publish(self) -> None:
        """Validate stages, replace all roots, then remove old roots or restore every root."""
        try:
            self._preflight()
            self._write_journal("prepared")
            for index in range(len(self._targets)):
                self._publish_target(index)
            self._validate_published()
            self._write_journal("post_publish_validated")
            self._remove_old_roots()
            self._remove_file(self._journal)
        except Exception as error:
            try:
                self._rollback()
            except Exception as rollback_error:
                raise CrossPackagePublicationError(
                    f"Cross-root publication failed ({error}); rollback also failed ({rollback_error}). "
                    f"Recovery journal retained at {self._journal}."
                ) from rollback_error
            raise CrossPackagePublicationError(
                f"Cross-root publication failed and all roots were restored: {error}"
            ) from error

    def _preflight(self) -> None:
        if not self._packages:
            raise CrossPackagePublicationError("At least one staged package is required.")
        if len({package.name for package in self._packages}) != len(self._packages) or any(not package.name for package in self._packages):
            raise CrossPackagePublicationError("Staged package names must be unique and non-empty.")
        stages = [target.stage for target in self._targets if target.stage is not None]
        roots = [target.root for target in self._targets]
        if len(set(stages + roots)) != len(stages) + len(roots):
            raise CrossPackagePublicationError("Staged and target roots must all be distinct directories.")
        for stage in stages:
            if not stage.is_dir():
                raise CrossPackagePublicationError(f"Prepared package stage is missing: {stage}")
        for root in roots:
            if root.exists() and not root.is_dir():
                raise CrossPackagePublicationError(f"Publication root is not a directory: {root}")
        self._validate_stages()
        self._inject("preflight")

    def _validate_stages(self) -> None:
        for package in self._packages:
            package.validate(package.processed_stage.resolve(), package.runtime_stage.resolve())

    def _validate_published(self) -> None:
        for package in self._packages:
            package.validate(package.processed_root.resolve(), package.runtime_root.resolve())

    def _publish_target(self, index: int) -> None:
        target = self._targets[index]
        backup = self._backups[index]
        if backup.exists():
            raise CrossPackagePublicationError(f"Transaction backup path already exists: {backup}")
        if target.root.exists():
            os.replace(target.root, backup)
            self._write_journal("old_root_relocated", index)
        if target.stage is not None:
            try:
                os.replace(target.stage, target.root)
            except Exception:
                if backup.exists() and not target.root.exists():
                    os.replace(backup, target.root)
                raise
            self._write_journal("replacement_published", index)
        else:
            self._write_journal("legacy_root_relocated", index)
        self._published[index] = True
        self._inject(f"after_target_{index}_publication")

    def _remove_old_roots(self) -> None:
        for index, backup in enumerate(self._backups):
            if not backup.exists():
                continue
            shutil.copytree(backup, self._recovery[index], copy_function=shutil.copy2)
            self._write_journal("old_root_recovery_staged", index)
            self._write_journal("old_root_deletion_started", index)
            self._inject("before_old_root_removal")
            self._remove_tree(backup)
            self._write_journal("old_root_deleted", index)
            self._inject("after_old_root_removal")
        for index, recovery in enumerate(self._recovery):
            if recovery.exists():
                self._write_journal("recovery_deletion_started", index)
                self._remove_tree(recovery)
                self._write_journal("recovery_deleted", index)
        self._write_journal("cleaned")

    def _rollback(self) -> None:
        self._write_journal("rolling_back")
        for index in range(len(self._targets) - 1, -1, -1):
            if not self._published[index]:
                continue
            target, backup, recovery = self._targets[index], self._backups[index], self._recovery[index]
            if target.stage is not None and target.root.exists():
                os.replace(target.root, target.stage)
            restore = backup if backup.exists() else recovery
            if self._had_root[index]:
                if not restore.exists():
                    raise CrossPackagePublicationError(f"Original root backup is unavailable: {target.root}")
                os.replace(restore, target.root)
            elif target.root.exists():
                self._remove_tree(target.root)
            if target.stage is not None:
                self._remove_tree(target.stage)
            self._remove_tree(backup)
            self._remove_tree(recovery)
        for target in self._targets:
            if target.stage is not None:
                self._remove_tree(target.stage)
        for path in self._backups + self._recovery:
            self._remove_tree(path)
        self._remove_file(self._journal)

    def _write_journal(self, state: str, target_index: int | None = None) -> None:
        self._journal.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "schema_version": 2,
            "state": state,
            "target_index": target_index,
            "targets": [
                {"label": target.label, "stage": str(target.stage) if target.stage is not None else None, "root": str(target.root)}
                for target in self._targets
            ],
            "backups": [str(path) for path in self._backups],
            "recovery": [str(path) for path in self._recovery],
            "had_root": list(self._had_root),
        }
        temporary = self._journal.with_suffix(".tmp")
        with temporary.open("w", encoding="utf-8") as file:
            json.dump(payload, file, indent=2, sort_keys=True)
            file.write("\n")
            file.flush()
            os.fsync(file.fileno())
        os.replace(temporary, self._journal)

    def _inject(self, point: str) -> None:
        if self._failure_point == point:
            raise CrossPackagePublicationError(f"Injected deterministic failure at {point}")

    @staticmethod
    def _remove_tree(path: Path) -> None:
        if path.exists():
            shutil.rmtree(path)

    @staticmethod
    def _remove_file(path: Path) -> None:
        if path.exists():
            path.unlink()
