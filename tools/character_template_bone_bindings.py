#!/usr/bin/env python3
"""Bone-binding integrity helpers for regenerated character templates.

Regenerating a reference character re-exports its skeleton with deform-only helper
bones (for example the RIG-002 forearm-twist helpers), which shifts the bone order.
Godot re-saves IK modifier bone references by name, so the ``<prefix>_bone_name`` /
``<prefix>_bone`` property pairs serialised in a saved template act as the
authoritative bone-name-to-index mapping for the skeleton that scene targets.
``BoneAttachment3D`` nodes are not refreshed the same way: their stored
``bone_idx`` is load-authoritative (the engine only falls back to ``bone_name``
when the index is -1 or below), so a stale index silently re-binds the attachment
to whichever bone now owns that index.

These helpers parse template scene text, cross-check every ``BoneAttachment3D``
against the refreshed modifier references in the same skeleton scope, and refresh
stale attachment indices on demand. Use
``tools/check_character_template_bone_bindings.py`` after regeneration re-saves a
template; the companion integration test proves the same contract against the
loaded skeleton at runtime.
"""

from __future__ import annotations

import re
from dataclasses import dataclass


NODE_NAME_PATTERN = re.compile(r'\bname="(?P<name>[^"]+)"')
NODE_TYPE_PATTERN = re.compile(r'\btype="(?P<node_type>[^"]+)"')
NODE_PARENT_PATTERN = re.compile(r'\bparent="(?P<parent>[^"]+)"')
MODIFIER_BONE_NAME_PATTERN = re.compile(r'^(?P<prefix>[\w/]+)_bone_name = "(?P<bone>[^"]+)"$')
MODIFIER_BONE_INDEX_PATTERN = re.compile(r'^(?P<prefix>[\w/]+)_bone = (?P<index>-?\d+)$')
ATTACHMENT_BONE_NAME_PATTERN = re.compile(r'^bone_name = "(?P<bone>[^"]+)"$')
ATTACHMENT_BONE_INDEX_PATTERN = re.compile(r'^bone_idx = (?P<index>-?\d+)$')

BONE_ATTACHMENT_TYPE = "BoneAttachment3D"


@dataclass(frozen=True)
class ModifierBonePair:
    """A refreshed ``<prefix>_bone_name``/``<prefix>_bone`` reference from one modifier node."""

    node_name: str
    parent: str | None
    property_prefix: str
    bone_name: str
    bone_index: int


@dataclass(frozen=True)
class AttachmentBoneBinding:
    """The stored binding of one ``BoneAttachment3D`` node."""

    node_name: str
    parent: str | None
    bone_name: str | None
    bone_index: int | None
    bone_index_line: int | None


@dataclass(frozen=True)
class SceneBoneBindings:
    """Bone references parsed from one scene file's text."""

    modifier_pairs: tuple[ModifierBonePair, ...]
    attachments: tuple[AttachmentBoneBinding, ...]


def parse_scene_bone_bindings(scene_text: str) -> SceneBoneBindings:
    """Parse refreshed modifier bone references and attachment bindings from ``.tscn`` text."""

    modifier_pairs: list[ModifierBonePair] = []
    attachments: list[AttachmentBoneBinding] = []

    node_name: str | None = None
    node_type: str | None = None
    node_parent: str | None = None
    pending_bone_names: dict[str, str] = {}
    pending_bone_indices: dict[str, int] = {}
    attachment_bone_name: str | None = None
    attachment_bone_index: int | None = None
    attachment_bone_index_line: int | None = None

    def flush_node() -> None:
        nonlocal node_name, node_type, node_parent
        if node_name is None:
            return
        for prefix, bone_name in pending_bone_names.items():
            if prefix in pending_bone_indices:
                modifier_pairs.append(
                    ModifierBonePair(
                        node_name=node_name,
                        parent=node_parent,
                        property_prefix=prefix,
                        bone_name=bone_name,
                        bone_index=pending_bone_indices[prefix],
                    )
                )
        if node_type == BONE_ATTACHMENT_TYPE:
            attachments.append(
                AttachmentBoneBinding(
                    node_name=node_name,
                    parent=node_parent,
                    bone_name=attachment_bone_name,
                    bone_index=attachment_bone_index,
                    bone_index_line=attachment_bone_index_line,
                )
            )
        node_name = None
        node_type = None
        node_parent = None

    for line_number, raw_line in enumerate(scene_text.splitlines(), start=1):
        line = raw_line.strip()
        if line.startswith("["):
            flush_node()
            if not line.startswith("[node "):
                continue

            header = line[len("[node ") :]
            name_match = NODE_NAME_PATTERN.search(header)
            type_match = NODE_TYPE_PATTERN.search(header)
            parent_match = NODE_PARENT_PATTERN.search(header)
            node_name = name_match.group("name") if name_match else ""
            node_type = type_match.group("node_type") if type_match else None
            node_parent = parent_match.group("parent") if parent_match else None
            pending_bone_names = {}
            pending_bone_indices = {}
            attachment_bone_name = None
            attachment_bone_index = None
            attachment_bone_index_line = None
            continue

        if node_name is None:
            continue

        modifier_name_match = MODIFIER_BONE_NAME_PATTERN.match(line)
        if modifier_name_match is not None and node_type != BONE_ATTACHMENT_TYPE:
            pending_bone_names[modifier_name_match.group("prefix")] = modifier_name_match.group("bone")
            continue

        modifier_index_match = MODIFIER_BONE_INDEX_PATTERN.match(line)
        if modifier_index_match is not None and node_type != BONE_ATTACHMENT_TYPE:
            pending_bone_indices[modifier_index_match.group("prefix")] = int(modifier_index_match.group("index"))
            continue

        if node_type == BONE_ATTACHMENT_TYPE:
            attachment_name_match = ATTACHMENT_BONE_NAME_PATTERN.match(line)
            if attachment_name_match is not None:
                attachment_bone_name = attachment_name_match.group("bone")
                continue
            attachment_index_match = ATTACHMENT_BONE_INDEX_PATTERN.match(line)
            if attachment_index_match is not None:
                attachment_bone_index = int(attachment_index_match.group("index"))
                attachment_bone_index_line = line_number

    flush_node()

    return SceneBoneBindings(
        modifier_pairs=tuple(modifier_pairs),
        attachments=tuple(attachments),
    )


def validate_bone_attachment_bindings(bindings: SceneBoneBindings) -> list[str]:
    """Return loud failure descriptions for unverifiable or stale attachment bindings.

    Every ``BoneAttachment3D`` must serialise an explicit ``bone_name`` and ``bone_idx``, and a
    refreshed modifier reference pair for the same bone name must exist under the same
    parent scope mapping the bone to the stored index. Scenes whose skeleton scopes carry
    no modifier references at all cannot be verified textually and fail loudly here; the
    runtime integration guard covers those against the loaded skeleton instead.
    """

    errors: list[str] = []
    scope_maps: dict[str, dict[str, int]] = {}
    for scope, pairs in _modifier_pairs_by_scope(bindings).items():
        by_name: dict[str, int] = {}
        for pair in pairs:
            existing = by_name.get(pair.bone_name)
            if existing is not None and existing != pair.bone_index:
                errors.append(
                    f"refreshed bone references under parent '{scope}' disagree on bone "
                    f"'{pair.bone_name}': nodes map it to both {existing} and {pair.bone_index}."
                )
            by_name[pair.bone_name] = pair.bone_index
        scope_maps[scope] = by_name

    for attachment in bindings.attachments:
        scope = _scope_key(attachment.parent)
        label = f"BoneAttachment3D '{attachment.node_name}'"
        if attachment.bone_name is None:
            errors.append(f"{label} does not serialise bone_name.")
            continue
        if attachment.bone_index is None:
            errors.append(
                f"{label} does not serialise bone_idx; the engine must not rely on implicit bindings."
            )
            continue

        scope_map = scope_maps.get(scope)
        if scope_map is None or attachment.bone_name not in scope_map:
            errors.append(
                f"{label} bone '{attachment.bone_name}' has no refreshed "
                f"<prefix>_bone_name/<prefix>_bone reference pair under parent '{scope}'; "
                f"the stored bone_idx {attachment.bone_index} cannot be verified textually."
            )
            continue

        expected = scope_map[attachment.bone_name]
        if attachment.bone_index == expected:
            continue

        resolves_to = _bone_at_index(scope_map, attachment.bone_index)
        owner = f"'{resolves_to}'" if resolves_to is not None else "a bone named nowhere in this scope"
        errors.append(
            f"{label} stores bone_idx {attachment.bone_index} for bone '{attachment.bone_name}', but refreshed "
            f"references map '{attachment.bone_name}' to {expected}; the stored index currently belongs to "
            f"{owner}, so the load-time binding is stale."
        )

    return errors


def refresh_bone_attachment_binding_indices(scene_text: str) -> tuple[str, list[str]]:
    """Rewrite stale ``BoneAttachment3D`` ``bone_idx`` values to the refreshed reference indices.

    Only mismatched ``bone_idx`` property lines change; every other serialised line is returned
    byte-identical. Returns the rewritten text plus a description of each applied refresh.
    """

    bindings = parse_scene_bone_bindings(scene_text)
    scope_maps: dict[str, dict[str, int]] = {
        scope: {pair.bone_name: pair.bone_index for pair in pairs}
        for scope, pairs in _modifier_pairs_by_scope(bindings).items()
    }

    lines = scene_text.splitlines(keepends=True)
    changes: list[str] = []
    for attachment in bindings.attachments:
        scope = _scope_key(attachment.parent)
        if attachment.bone_index_line is None or attachment.bone_name is None:
            continue
        expected = scope_maps.get(scope, {}).get(attachment.bone_name)
        if expected is None or expected == attachment.bone_index:
            continue

        line_index = attachment.bone_index_line - 1
        line_ending = "\n" if lines[line_index].endswith("\n") else ""
        lines[line_index] = f"bone_idx = {expected}{line_ending}"
        changes.append(
            f"BoneAttachment3D '{attachment.node_name}' bone '{attachment.bone_name}': "
            f"bone_idx {attachment.bone_index} -> {expected}."
        )

    return "".join(lines), changes


def _modifier_pairs_by_scope(bindings: SceneBoneBindings) -> dict[str, list[ModifierBonePair]]:
    scopes: dict[str, list[ModifierBonePair]] = {}
    for pair in bindings.modifier_pairs:
        scopes.setdefault(_scope_key(pair.parent), []).append(pair)
    return scopes


def _scope_key(parent: str | None) -> str:
    return parent if parent else ""


def _bone_at_index(scope_map: dict[str, int], bone_index: int) -> str | None:
    for bone_name, index in scope_map.items():
        if index == bone_index:
            return bone_name
    return None
